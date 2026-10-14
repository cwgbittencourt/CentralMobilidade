using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Infrastructure.Streaming;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Streaming;

public sealed class BoundedRequestSpoolTests
{
    private static byte[] Payload()
    {
        using var bytes = new MemoryStream();
        using (var gzip = new GZipStream(bytes, CompressionLevel.Fastest, true))
            gzip.Write(new byte[1000]);
        return bytes.ToArray();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Limit_is_enforced_and_bytes_hash_and_readonly_rewind_are_preserved(int offset)
    {
        var bytes = Payload();
        using var input = new MemoryStream(bytes);
        var spooler = new BoundedRequestSpool(bytes.Length + offset, TimeSpan.FromSeconds(2));
        if (offset < 0)
        {
            var failure = await Should.ThrowAsync<ProviderOperationException>(() => spooler.ReadAsync(input));
            failure.ErrorCode.ShouldBe(TelemetryErrorCodes.PayloadCompressedLimit);
            return;
        }
        await using var result = await spooler.ReadAsync(input);
        result.CompressedBytes.ShouldBe(bytes.Length);
        result.PayloadSha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(bytes)));
        result.Body.CanWrite.ShouldBeFalse();
        Should.Throw<NotSupportedException>(() => result.Body.WriteByte(0));
        using var copy = new MemoryStream();
        await result.Body.CopyToAsync(copy);
        copy.ToArray().ShouldBe(bytes);
        result.Body.Position = 0;
        result.Body.ReadByte().ShouldBe(0x1f);
        input.CanRead.ShouldBeTrue();
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("short")]
    [InlineData("magic")]
    [InlineData("method")]
    [InlineData("flags")]
    public async Task Invalid_gzip_header_is_rejected(string variant)
    {
        var bytes = Payload();
        if (variant == "empty")
            bytes = [];
        if (variant == "short")
            bytes = bytes[..9];
        if (variant == "magic")
            bytes[0] = 0;
        if (variant == "method")
            bytes[2] = 0;
        if (variant == "flags")
            bytes[3] |= 0xe0;
        using var input = new MemoryStream(bytes);
        var failure = await Should.ThrowAsync<ProviderOperationException>(() => new BoundedRequestSpool(1024, TimeSpan.FromSeconds(1)).ReadAsync(input));
        failure.ErrorCode.ShouldBe(TelemetryErrorCodes.RequestInvalid);
    }

    [Fact]
    public async Task Declared_limit_rejects_before_read_and_mismatched_length_is_invalid()
    {
        using var input = new MemoryStream(Payload());
        var spooler = new BoundedRequestSpool(1024, TimeSpan.FromSeconds(1));
        var oversized = await Should.ThrowAsync<ProviderOperationException>(() => spooler.ReadAsync(input, 1025));
        oversized.ErrorCode.ShouldBe(TelemetryErrorCodes.PayloadCompressedLimit);
        input.Position.ShouldBe(0);
        var mismatch = await Should.ThrowAsync<ProviderOperationException>(() => spooler.ReadAsync(input, input.Length + 1));
        mismatch.ErrorCode.ShouldBe(TelemetryErrorCodes.RequestInvalid);
    }

    [Fact]
    public async Task Files_are_removed_on_success_disposal_and_rejection()
    {
        var directory = Path.Combine(Path.GetTempPath(), "t029-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var spooler = new BoundedRequestSpool(1024, TimeSpan.FromSeconds(1), directory);
            using var input = new MemoryStream(Payload());
            await using (var result = await spooler.ReadAsync(input))
                Directory.GetFiles(directory).Length.ShouldBe(1);
            Directory.GetFiles(directory).ShouldBeEmpty();
            using var oversized = new MemoryStream(Payload());
            await Should.ThrowAsync<ProviderOperationException>(() => new BoundedRequestSpool(10, TimeSpan.FromSeconds(1), directory).ReadAsync(oversized));
            Directory.GetFiles(directory).ShouldBeEmpty();
            using var invalid = new MemoryStream(new byte[2]);
            await Should.ThrowAsync<ProviderOperationException>(() => spooler.ReadAsync(invalid));
            Directory.GetFiles(directory).ShouldBeEmpty();
        }
        finally { Directory.Delete(directory); }
    }

    [Fact]
    public async Task Timeout_and_caller_cancellation_are_distinct_and_cleanup_files()
    {
        var directory = Path.Combine(Path.GetTempPath(), "t029-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var blocked = new BlockingInput();
            var failure = await Should.ThrowAsync<ProviderOperationException>(() => new BoundedRequestSpool(1024, TimeSpan.FromMilliseconds(30), directory).ReadAsync(blocked));
            failure.ErrorCode.ShouldBe(TelemetryErrorCodes.UploadTimeout);
            Directory.GetFiles(directory).ShouldBeEmpty();
            using var abort = new CancellationTokenSource();
            abort.CancelAfter(TimeSpan.FromMilliseconds(30));
            await Should.ThrowAsync<OperationCanceledException>(() => new BoundedRequestSpool(1024, TimeSpan.FromSeconds(1), directory).ReadAsync(blocked, cancellationToken: abort.Token));
            Directory.GetFiles(directory).ShouldBeEmpty();
        }
        finally { Directory.Delete(directory); }
    }

    [Fact]
    public async Task Fragmented_nonseekable_body_does_not_require_length_or_sync_reads()
    {
        var bytes = Payload();
        using var input = new FragmentedInput(bytes);
        await using var spool = await new BoundedRequestSpool(1024, TimeSpan.FromSeconds(1)).ReadAsync(input);
        spool.CompressedBytes.ShouldBe(bytes.Length);
        spool.PayloadSha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    [Fact]
    public void Invalid_configuration_is_rejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new BoundedRequestSpool(0, TimeSpan.FromSeconds(1)));
        Should.Throw<ArgumentOutOfRangeException>(() => new BoundedRequestSpool(1, TimeSpan.Zero));
    }

    private sealed class FragmentedInput(byte[] bytes) : Stream
    {
        private int offset;
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException(); set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (offset == bytes.Length)
                return ValueTask.FromResult(0);
            buffer.Span[0] = bytes[offset++];
            return ValueTask.FromResult(1);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingInput : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException(); set => throw new NotSupportedException();
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
