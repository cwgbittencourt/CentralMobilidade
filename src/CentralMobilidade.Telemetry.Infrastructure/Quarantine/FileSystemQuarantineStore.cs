using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Errors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CentralMobilidade.Telemetry.Infrastructure.Quarantine;

/// <summary>Private, immutable objects published by same-directory atomic rename.</summary>
public sealed class FileSystemQuarantineStore : IQuarantineStore
{
    private const int TrailerSize = 44;
    private static ReadOnlySpan<byte> Magic => "CMQUAR01"u8;
    private readonly string root;
    private readonly ILogger<FileSystemQuarantineStore> logger;

    public FileSystemQuarantineStore(IConfiguration configuration, ILogger<FileSystemQuarantineStore> logger)
    {
        this.logger = logger;
        var configured = configuration["Quarantine:Path"];
        if (string.IsNullOrWhiteSpace(configured))
            throw new ArgumentException("Quarantine storage configuration is required.");
        try
        {
            root = Path.GetFullPath(configured);
            if (Directory.Exists(root))
                RejectLink(root);
            if (OperatingSystem.IsWindows())
            {
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(root).Create(security);
                RejectLink(root);
                new DirectoryInfo(root).SetAccessControl(security);
            }
            else
            {
                Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                RejectLink(root);
                File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch (Exception error) when (IsStorageError(error)) { throw Failure(); }
    }

    public async ValueTask<QuarantineReceipt> WriteAsync(QuarantineWriteRequest request, Stream rawContent, CancellationToken cancellationToken)
    {
        Validate(request);
        ArgumentNullException.ThrowIfNull(rawContent);
        var temporary = Path.Combine(root, Guid.NewGuid().ToString("N") + ".qstage");
        try
        {
            RejectLink(root);
            QuarantineMetadata metadata;
            await using (var file = CreatePrivate(temporary))
            using (var contentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            using (var envelopeHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[65536];
                int read;
                while ((read = await rawContent.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    contentHash.AppendData(buffer, 0, read);
                    envelopeHash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                metadata = new(request.QuarantineId, request.BatchId, request.LineNumber, request.ErrorCode,
                    request.CreatedAt, request.DeleteAfter, Convert.ToHexStringLower(contentHash.GetHashAndReset()));
                var json = JsonSerializer.SerializeToUtf8Bytes(metadata);
                if (json.Length > 4096)
                    throw Failure();
                envelopeHash.AppendData(json);
                await file.WriteAsync(json, cancellationToken);
                var trailer = new byte[TrailerSize];
                envelopeHash.GetHashAndReset().CopyTo(trailer, 0);
                BinaryPrimitives.WriteInt32LittleEndian(trailer.AsSpan(32), json.Length);
                Magic.CopyTo(trailer.AsSpan(36));
                await file.WriteAsync(trailer, cancellationToken);
                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporary, ObjectPath(request.QuarantineId), overwrite: false);
            }
            catch (IOException) when (File.Exists(ObjectPath(request.QuarantineId)))
            {
                await using var existing = await OpenVerified(request.QuarantineId, cancellationToken);
                if (existing is null || existing.Metadata != metadata)
                    throw Failure();
            }
            logger.LogInformation("Quarantine object stored {QuarantineId} {BatchId} {LineNumber} {ErrorCode}",
                request.QuarantineId, request.BatchId.Value, request.LineNumber, request.ErrorCode);
            return new(request.QuarantineId, metadata.ContentSha256);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (IsStorageError(error)) { throw Failure(); }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception error) when (IsStorageError(error)) { logger.LogWarning("Quarantine staging cleanup failed"); }
        }
    }

    public async ValueTask<QuarantineMetadata?> GetMetadataAsync(Guid quarantineId, CancellationToken cancellationToken)
    {
        await using var content = await OpenVerified(quarantineId, cancellationToken);
        return content?.Metadata;
    }

    public async ValueTask<IQuarantineContent?> OpenReadAsync(Guid quarantineId, CancellationToken cancellationToken) =>
        await OpenVerified(quarantineId, cancellationToken);

    public async ValueTask<bool> DeleteExpiredAsync(Guid quarantineId, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        if (cutoff.Offset != TimeSpan.Zero)
            throw new ArgumentException("Deletion cutoff must be UTC.", nameof(cutoff));
        var metadata = await GetMetadataAsync(quarantineId, cancellationToken);
        if (metadata is null || metadata.DeleteAfter > cutoff)
            return false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectLink(root);
            File.Delete(ObjectPath(quarantineId));
            logger.LogInformation("Quarantine object deleted {QuarantineId}", quarantineId);
            return true;
        }
        catch (Exception error) when (IsStorageError(error)) { throw Failure(); }
    }

    private async ValueTask<ContentHandle?> OpenVerified(Guid id, CancellationToken token)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("A quarantine identifier is required.", nameof(id));
        FileStream? file = null;
        try
        {
            RejectLink(root);
            var path = ObjectPath(id);
            try
            {
                RejectLink(path);
                file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            }
            catch (FileNotFoundException) { return null; }
            if (file.Length < TrailerSize)
                throw Failure();
            var trailer = new byte[TrailerSize];
            file.Position = file.Length - TrailerSize;
            await file.ReadExactlyAsync(trailer, token);
            var jsonLength = BinaryPrimitives.ReadInt32LittleEndian(trailer.AsSpan(32));
            var rawLength = file.Length - TrailerSize - jsonLength;
            if (!trailer.AsSpan(36).SequenceEqual(Magic) || jsonLength is <= 0 or > 4096 || rawLength < 0)
                throw Failure();
            file.Position = rawLength;
            var json = new byte[jsonLength];
            await file.ReadExactlyAsync(json, token);
            var metadata = JsonSerializer.Deserialize<QuarantineMetadata>(json) ?? throw Failure();
            Validate(new(metadata.QuarantineId, metadata.BatchId, metadata.LineNumber, metadata.ErrorCode, metadata.CreatedAt, metadata.DeleteAfter));
            if (metadata.QuarantineId != id)
                throw Failure();
            file.Position = 0;
            using var rawHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var envelopeHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            var remaining = rawLength;
            while (remaining > 0)
            {
                var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
                if (read == 0)
                    throw Failure();
                rawHash.AppendData(buffer, 0, read);
                envelopeHash.AppendData(buffer, 0, read);
                remaining -= read;
            }
            envelopeHash.AppendData(json);
            if (!CryptographicOperations.FixedTimeEquals(envelopeHash.GetHashAndReset(), trailer.AsSpan(0, 32)) ||
                Convert.ToHexStringLower(rawHash.GetHashAndReset()) != metadata.ContentSha256)
                throw Failure();
            file.Position = 0;
            var result = new ContentHandle(metadata, new RawReadStream(file, rawLength));
            file = null;
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (IsStorageError(error) || error is JsonException or ArgumentException) { throw Failure(); }
        finally { if (file is not null) await file.DisposeAsync(); }
    }

    private FileStream CreatePrivate(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options); // Windows inherits the protected root ACL at creation, before any bytes are written.
    }
    private string ObjectPath(Guid id) => Path.Combine(root, id.ToString("N") + ".quarantine");
    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw Failure();
    }
    private static void Validate(QuarantineWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.QuarantineId == Guid.Empty || request.BatchId is null || request.LineNumber <= 0 ||
            !TelemetryErrorCodes.IsRejectionCode(request.ErrorCode) || request.CreatedAt.Offset != TimeSpan.Zero ||
            request.DeleteAfter.Offset != TimeSpan.Zero || request.DeleteAfter < request.CreatedAt ||
            request.DeleteAfter - request.CreatedAt > TimeSpan.FromDays(30))
            throw new ArgumentException("Invalid quarantine metadata.", nameof(request));
    }
    private static bool IsStorageError(Exception error) => error is IOException or UnauthorizedAccessException or System.Security.SecurityException;
    private static ProviderOperationException Failure() => new(TelemetryErrorCodes.StorageRejected, ProviderFailureKind.Ambiguous);

    private sealed class ContentHandle(QuarantineMetadata metadata, Stream stream) : IQuarantineContent
    {
        public QuarantineMetadata Metadata { get; } = metadata;
        public Stream Content { get; } = stream;
        public string ContentSha256 => Metadata.ContentSha256;
        public ValueTask DisposeAsync() => Content.DisposeAsync();
    }

    private sealed class RawReadStream(FileStream file, long length) : Stream
    {
        private readonly long contentLength = length;
        private long remaining = length;
        public override bool CanRead => file.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => contentLength;
        public override long Position
        {
            get => contentLength - remaining; set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            try
            {
                var read = file.Read(buffer, offset, (int)Math.Min(count, remaining));
                remaining -= read;
                return read;
            }
            catch (Exception error) when (IsStorageError(error)) { throw Failure(); }
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                var read = await file.ReadAsync(buffer[..(int)Math.Min(buffer.Length, remaining)], cancellationToken);
                remaining -= read;
                return read;
            }
            catch (Exception error) when (IsStorageError(error)) { throw Failure(); }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                file.Dispose();
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await file.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }
}
