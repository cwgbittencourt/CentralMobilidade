using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Configuration;
using CentralMobilidade.Telemetry.Contracts.Errors;

namespace CentralMobilidade.Telemetry.Application.Streaming;

/// <summary>Returns borrowed UTF-8 line bytes; consume/copy before advancing the iterator.</summary>
public sealed class BoundedGzipLineReader
{
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync(Stream input, long compressedBytes,
        TelemetryOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        if (!input.CanRead)
            throw new ArgumentException("Input must be readable.", nameof(input));
        if (compressedBytes <= 0 || compressedBytes > options.MaxCompressedBytes)
            throw new ArgumentOutOfRangeException(nameof(compressedBytes));
        if (options.MaxDecompressedBytes <= 0 || options.MaxDecompressedBytes == long.MaxValue ||
            options.MaxLineBytes <= 0 || options.MaxLineBytes > options.MaxDecompressedBytes || options.MaxLineBytes == int.MaxValue || options.MaxLinesPerBatch <= 0 ||
            !double.IsFinite(options.MaxExpansionRatio) || options.MaxExpansionRatio <= 0 ||
            options.UploadTimeout <= TimeSpan.Zero || options.UploadTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentException("Invalid bounded gzip limits.", nameof(options));
        var maxBytes = options.MaxDecompressedBytes;
        var maxLine = options.MaxLineBytes;
        var maxLines = options.MaxLinesPerBatch;
        var ratio = options.MaxExpansionRatio;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.UploadTimeout);
        var token = deadline.Token;
        using var counted = new CountedInput(input, compressedBytes);
        using var gzip = new GZipStream(counted, CompressionMode.Decompress, leaveOpen: true);
        var readBuffer = new byte[64 * 1024];
        var lineBuffer = new byte[maxLine + 1]; // One extra byte permits a CR before LF at the exact cap.
        long total = 0;
        long physicalLines = 0;
        var length = 0;
        var pendingEmpty = 0;
        uint crc = uint.MaxValue;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var size = (int)Math.Min(readBuffer.Length, maxBytes - total + 1);
                var count = await gzip.ReadAsync(readBuffer.AsMemory(0, size), token).AsTask().WaitAsync(token);
                if (count == 0)
                    break;
                total += count;
                if (total > maxBytes)
                    throw Reject(TelemetryErrorCodes.PayloadDecompressedLimit);
                if (total / (double)compressedBytes > ratio)
                    throw Reject(TelemetryErrorCodes.PayloadExpansionLimit);
                for (var i = 0; i < count; i++)
                {
                    var value = readBuffer[i];
                    crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
                    if (value != (byte)'\n')
                    {
                        if (length == maxLine + 1 || (length == maxLine && value != (byte)'\r'))
                            throw Reject(TelemetryErrorCodes.LpLineTooLarge);
                        lineBuffer[length++] = value;
                        continue;
                    }
                    if (++physicalLines > maxLines)
                        throw Reject(TelemetryErrorCodes.RequestInvalid);
                    var effectiveLength = length > 0 && lineBuffer[length - 1] == (byte)'\r' ? length - 1 : length;
                    if (effectiveLength == 0)
                        pendingEmpty++;
                    else
                    {
                        while (pendingEmpty > 0)
                        {
                            token.ThrowIfCancellationRequested();
                            pendingEmpty--;
                            yield return ReadOnlyMemory<byte>.Empty;
                        }
                        token.ThrowIfCancellationRequested();
                        yield return lineBuffer.AsMemory(0, effectiveLength);
                    }
                    length = 0;
                }
            }
            // GZipStream can tolerate an incomplete trailer. Require explicit CRC/ISIZE evidence.
            counted.ValidateTrailer(~crc, total);
            if (length > maxLine)
                throw Reject(TelemetryErrorCodes.LpLineTooLarge);
            if (length > 0)
            {
                if (++physicalLines > maxLines)
                    throw Reject(TelemetryErrorCodes.RequestInvalid);
                while (pendingEmpty > 0)
                {
                    token.ThrowIfCancellationRequested();
                    pendingEmpty--;
                    yield return ReadOnlyMemory<byte>.Empty;
                }
                token.ThrowIfCancellationRequested();
                yield return lineBuffer.AsMemory(0, length);
            }
            token.ThrowIfCancellationRequested();
        }
        finally
        {
            Array.Clear(lineBuffer);
            Array.Clear(readBuffer);
        }
    }

    private static ProviderOperationException Reject(string code) => new(code, ProviderFailureKind.Permanent);
    private static readonly uint[] CrcTable = CreateCrcTable();
    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) == 0 ? value >> 1 : (value >> 1) ^ 0xedb88320;
            table[i] = value;
        }
        return table;
    }

    private sealed class CountedInput(Stream input, long expected) : Stream
    {
        private readonly byte[] tail = new byte[8];
        private long total;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException(); set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Asynchronous reads required.");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var count = await input.ReadAsync(buffer, token);
            if (count > expected - total)
                throw new InvalidDataException("Compressed length does not match the envelope.");
            for (var i = 0; i < count; i++)
                tail[(int)((total + i) & 7)] = buffer.Span[i];
            total += count;
            return count;
        }
        internal void ValidateTrailer(uint crc, long decompressed)
        {
            if (total != expected || total < 18)
                throw new InvalidDataException("Incomplete gzip member.");
            Span<byte> trailer = stackalloc byte[8];
            for (var i = 0; i < 8; i++)
                trailer[i] = tail[(int)((total - 8 + i) & 7)];
            if (BinaryPrimitives.ReadUInt32LittleEndian(trailer) != crc ||
                BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..]) != (uint)decompressed)
                throw new InvalidDataException("Gzip integrity verification failed.");
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
