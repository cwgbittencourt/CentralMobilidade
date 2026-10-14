using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Errors;

namespace CentralMobilidade.Telemetry.Infrastructure.Streaming;

/// <summary>Spools original compressed bytes with fixed memory, bounded disk and streaming SHA-256.</summary>
public sealed class BoundedRequestSpool
{
    private const int BufferSize = 64 * 1024;
    private readonly long maxBytes;
    private readonly TimeSpan timeout;
    private readonly string directory;

    public BoundedRequestSpool(long maxCompressedBytes, TimeSpan uploadTimeout, string? temporaryDirectory = null)
    {
        if (maxCompressedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxCompressedBytes));
        if (uploadTimeout <= TimeSpan.Zero || uploadTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(uploadTimeout));
        maxBytes = maxCompressedBytes;
        timeout = uploadTimeout;
        directory = Path.GetFullPath(temporaryDirectory ?? Path.GetTempPath());
    }

    public async Task<SpooledRequest> ReadAsync(Stream input, long? contentLength = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        if (!input.CanRead)
            throw new ArgumentException("Input must be readable.", nameof(input));
        if (contentLength > maxBytes)
            throw Reject(TelemetryErrorCodes.PayloadCompressedLimit);
        if (contentLength is < 0 or 0)
            throw Reject(TelemetryErrorCodes.RequestInvalid);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        FileStream? file = null;
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                BufferSize = BufferSize,
                Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            file = new FileStream(Path.Combine(directory, "telemetry-" + Guid.NewGuid().ToString("N") + ".spool"), options);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            var header = new byte[10];
            var headerBytes = 0;
            while (true)
            {
                // At most one byte beyond the cap is read; it is never written to disk.
                var remaining = maxBytes - total;
                var count = remaining >= BufferSize ? BufferSize : (int)remaining + 1;
                var read = await input.ReadAsync(buffer.AsMemory(0, count), deadline.Token);
                if (read == 0)
                    break;
                if (read > remaining)
                    throw Reject(TelemetryErrorCodes.PayloadCompressedLimit);
                var prefix = Math.Min(read, header.Length - headerBytes);
                buffer.AsSpan(0, prefix).CopyTo(header.AsSpan(headerBytes));
                headerBytes += prefix;
                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
                total += read;
            }
            if (headerBytes != 10 || header[0] != 0x1f || header[1] != 0x8b || header[2] != 8 || (header[3] & 0xe0) != 0
                || (contentLength.HasValue && total != contentLength.Value))
                throw Reject(TelemetryErrorCodes.RequestInvalid);
            await file.FlushAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            file.Position = 0;
            var result = new SpooledRequest(file, total, Convert.ToHexStringLower(hash.GetHashAndReset()));
            file = null; // Ownership moves to the result; input always remains caller-owned.
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw Reject(TelemetryErrorCodes.UploadTimeout);
        }
        catch (IOException)
        {
            throw new ProviderOperationException(TelemetryErrorCodes.InternalError, ProviderFailureKind.Transient);
        }
        finally
        {
            try
            {
                if (file is not null)
                    await file.DisposeAsync();
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
        }
    }

    private static ProviderOperationException Reject(string code) => new(code, ProviderFailureKind.Permanent);
}

public sealed class SpooledRequest : IAsyncDisposable, IDisposable
{
    internal SpooledRequest(FileStream file, long bytes, string digest)
    {
        Body = new ReadOnlySpool(file);
        CompressedBytes = bytes;
        PayloadSha256 = digest;
    }
    public Stream Body
    {
        get;
    }
    public long CompressedBytes
    {
        get;
    }
    public string PayloadSha256
    {
        get;
    }
    public void Dispose() => Body.Dispose();
    public ValueTask DisposeAsync() => Body.DisposeAsync();
    public override string ToString() => "[SpooledRequest]";

    private sealed class ReadOnlySpool(FileStream file) : Stream
    {
        public override bool CanRead => file.CanRead;
        public override bool CanSeek => file.CanSeek;
        public override bool CanWrite => false;
        public override long Length => file.Length;
        public override long Position
        {
            get => file.Position; set => file.Position = value;
        }
        public override int Read(byte[] buffer, int offset, int count) => file.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => file.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => file.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => file.Seek(offset, origin);
        public override void Flush()
        {
        }
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
