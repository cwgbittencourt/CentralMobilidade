using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Configuration;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Infrastructure.Streaming;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.UnitTests.Streaming;

public sealed class GzipBoundaryPropertyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(31)]
    [InlineData(257)]
    public async Task Seeded_chunk_boundaries_preserve_utf8_and_normalize_newlines(int chunk)
    {
        var read = Reader();
        var random = new Random(37001);
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var expected = new List<string>();
            var payload = new StringBuilder();
            for (var line = 0; line < random.Next(2, 20); line++)
            {
                var value = "São Paulo,José,🚗," + new string('x', random.Next(1, 200));
                expected.Add(value);
                payload.Append(value).Append(line % 2 == 0 ? "\r\n" : "\n");
            }
            var bytes = Gzip(Encoding.UTF8.GetBytes(payload.ToString()));
            using var stream = new ChunkStream(bytes, chunk);
            var actual = new List<string>();
            await foreach (var line in read(stream, bytes.Length, Options(), CancellationToken.None))
                actual.Add(new UTF8Encoding(false, true).GetString(line.Span));
            actual.ShouldBe(expected);
            stream.Disposed.ShouldBeFalse("The caller owns the compressed stream");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(9)]
    public async Task Truncated_member_must_not_complete_successfully(int removed)
    {
        var read = Reader();
        var bytes = Gzip(Encoding.UTF8.GetBytes("valid line\nsecond line\n"));
        using var stream = new ChunkStream(bytes[..^removed], 1);
        await Should.ThrowAsync<InvalidDataException>(() => Drain(read(stream, bytes.Length - removed, Options(), CancellationToken.None)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Each_crc_byte_is_checked_before_successful_completion(int crcByte)
    {
        var read = Reader();
        var bytes = Gzip(Encoding.UTF8.GetBytes("valid line\n"));
        bytes[bytes.Length - 8 + crcByte] ^= 0x80;
        using var stream = new ChunkStream(bytes, 7);
        await Should.ThrowAsync<InvalidDataException>(() => Drain(read(stream, bytes.Length, Options(), CancellationToken.None)));
    }

    [Theory]
    [InlineData(16383, true)]
    [InlineData(16384, true)]
    [InlineData(16385, false)]
    public async Task Line_limit_counts_utf8_bytes_not_characters(int length, bool accepted)
    {
        var read = Reader();
        var line = new string('é', length / 2) + (length % 2 == 1 ? "x" : "");
        var bytes = Gzip(Encoding.UTF8.GetBytes(line + "\r\n"));
        using var stream = new ChunkStream(bytes, 257);
        var operation = Drain(read(stream, bytes.Length, Options(), CancellationToken.None));
        if (accepted)
            (await operation).ShouldBe(1);
        else
            (await Should.ThrowAsync<ProviderOperationException>(() => operation)).ErrorCode.ShouldBe(TelemetryErrorCodes.LpLineTooLarge);
    }

    [Theory]
    [InlineData(49999, true)]
    [InlineData(50000, true)]
    [InlineData(50001, false)]
    public async Task Batch_line_count_has_an_independent_ceiling(int count, bool accepted)
    {
        var read = Reader();
        var bytes = RepeatedGzip(Encoding.UTF8.GetBytes("x\n"), count);
        using var stream = new ChunkStream(bytes, 257);
        var operation = Drain(read(stream, bytes.Length, Options(), CancellationToken.None));
        if (accepted)
            (await operation).ShouldBe(count);
        else
            await Should.ThrowAsync<ProviderOperationException>(() => operation);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task Decompressed_limit_is_exactly_64_MiB(long delta, bool accepted)
    {
        var read = Reader();
        const long ceiling = 64L * 1024 * 1024;
        var block = new byte[8192];
        Array.Fill(block, (byte)'x');
        block[^1] = (byte)'\n';
        var total = ceiling + delta;
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, true))
        {
            for (long remaining = total; remaining > 0; remaining -= Math.Min(remaining, block.Length))
                gzip.Write(block.AsSpan(0, (int)Math.Min(remaining, block.Length)));
        }
        using var stream = new ChunkStream(compressed.ToArray(), 8192);
        var operation = Drain(read(stream, compressed.Length, Options(), CancellationToken.None));
        if (accepted)
            (await operation).ShouldBe((int)((total + block.Length - 1) / block.Length));
        else
            (await Should.ThrowAsync<ProviderOperationException>(() => operation)).ErrorCode.ShouldBe(TelemetryErrorCodes.PayloadDecompressedLimit);
    }

    [Fact]
    public async Task Expansion_above_100_to_1_is_rejected_independently_of_byte_ceiling()
    {
        var read = Reader();
        var bytes = RepeatedGzip(Encoding.UTF8.GetBytes(new string('x', 1000) + "\n"), 1000);
        (1001000L > bytes.Length * 100L).ShouldBeTrue();
        var options = Options();
        options.MaxExpansionRatio = 100;
        using var stream = new ChunkStream(bytes, 257);
        var error = await Should.ThrowAsync<ProviderOperationException>(() => Drain(read(stream, bytes.Length, options, CancellationToken.None)));
        error.ErrorCode.ShouldBe(TelemetryErrorCodes.PayloadExpansionLimit);
    }

    [Fact]
    public async Task Cancellation_interrupts_a_blocked_read()
    {
        var read = Reader();
        using var stream = new BlockingStream();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Should.ThrowAsync<OperationCanceledException>(() => Drain(read(stream, 100, Options(), cancellation.Token)).WaitAsync(TimeSpan.FromSeconds(3)));
    }

    private delegate IAsyncEnumerable<ReadOnlyMemory<byte>> ReadLines(Stream stream, long compressedBytes, TelemetryOptions options, CancellationToken cancellationToken);

    [Fact]
    public async Task Configured_deadline_interrupts_read_without_caller_cancellation()
    {
        var read = Reader();
        using var stream = new BlockingStream();
        var options = Options();
        options.UploadTimeout = TimeSpan.FromMilliseconds(100);
        await Should.ThrowAsync<OperationCanceledException>(() =>
            Drain(read(stream, 100, options, CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task Configured_expansion_ceiling_is_inclusive(int slack, bool accepted)
    {
        var read = Reader();
        var data = Encoding.UTF8.GetBytes(new string('x', 1000) + "\n");
        var bytes = Gzip(data);
        var options = Options();
        options.MaxExpansionRatio = (data.Length + slack) / (double)bytes.Length;
        using var stream = new ChunkStream(bytes, 7);
        var operation = Drain(read(stream, bytes.Length, options, CancellationToken.None));
        if (accepted)
            (await operation).ShouldBe(1);
        else
            (await Should.ThrowAsync<ProviderOperationException>(() => operation)).ErrorCode.ShouldBe(TelemetryErrorCodes.PayloadExpansionLimit);
    }

    private static ReadLines Reader()
    {
        var type = typeof(BoundedRequestSpool).Assembly.GetType("CentralMobilidade.Telemetry.Infrastructure.Streaming.BoundedGzipLineReader");
        type.ShouldNotBeNull("T045 must implement the bounded production gzip reader");
        var method = type.GetMethod("ReadAsync", [typeof(Stream), typeof(long), typeof(TelemetryOptions), typeof(CancellationToken)]);
        method.ShouldNotBeNull("ReadAsync must expose a cancellable bounded line sequence");
        var instance = method.IsStatic ? null : Activator.CreateInstance(type);
        return (stream, compressedBytes, options, token) =>
            method.Invoke(instance, [stream, compressedBytes, options, token]).ShouldBeAssignableTo<IAsyncEnumerable<ReadOnlyMemory<byte>>>()!;
    }

    private static TelemetryOptions Options() => new() { MaxExpansionRatio = 1000000 };

    private static async Task<int> Drain(IAsyncEnumerable<ReadOnlyMemory<byte>> lines)
    {
        var count = 0;
        await foreach (var _ in lines)
            count++;
        return count;
    }

    private static byte[] Gzip(byte[] data) => RepeatedGzip(data, 1);

    private static byte[] RepeatedGzip(byte[] block, int count)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, true))
            for (var i = 0; i < count; i++)
                gzip.Write(block);
        return output.ToArray();
    }

    private class ChunkStream(byte[] bytes, int chunk) : Stream
    {
        private int offset;
        public bool Disposed
        {
            get; private set;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException(); set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Asynchronous reads required");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(Math.Min(chunk, buffer.Length), bytes.Length - offset);
            bytes.AsMemory(offset, count).CopyTo(buffer);
            offset += count;
            return ValueTask.FromResult(count);
        }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingStream() : ChunkStream([], 1)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
