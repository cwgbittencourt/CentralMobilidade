using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using CentralMobilidade.Telemetry.Infrastructure.Streaming;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Quarantine;

[Trait("Category", "Integration")]
public sealed class QuarantineStoreTests
{
    private static readonly byte[] Raw = Encoding.UTF8.GetBytes("SEEDED_RAW placa=BRA2E19 latitude=-23.55052 motorista=José cpf=12345678901 token=SECRET\n");

    [Fact]
    public async Task Protected_content_roundtrips_exact_bytes_with_sha256_and_opaque_metadata()
    {
        await using var fixture = Fixture.Create();
        var request = Request();
        using var input = new MemoryStream(Raw);
        var receipt = await fixture.Store.WriteAsync(request, input, CancellationToken.None);
        receipt.QuarantineId.ShouldBe(request.QuarantineId);
        receipt.ContentSha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Raw)));
        input.CanRead.ShouldBeTrue();
        var metadata = (await fixture.Store.GetMetadataAsync(request.QuarantineId, CancellationToken.None)).ShouldNotBeNull();
        metadata.BatchId.ShouldBe(request.BatchId);
        metadata.LineNumber.ShouldBe(request.LineNumber);
        metadata.ErrorCode.ShouldBe(request.ErrorCode);
        metadata.CreatedAt.ShouldBe(request.CreatedAt);
        metadata.DeleteAfter.ShouldBe(request.DeleteAfter);
        metadata.ContentSha256.ShouldBe(receipt.ContentSha256);
        await using var content = (await fixture.Store.OpenReadAsync(request.QuarantineId, CancellationToken.None)).ShouldNotBeNull();
        content.ContentSha256.ShouldBe(receipt.ContentSha256);
        using var recovered = new MemoryStream();
        await content.Content.CopyToAsync(recovered);
        recovered.ToArray().ShouldBe(Raw);
        typeof(QuarantineReceipt).GetProperties().Any(p => p.Name.Contains("Path", StringComparison.OrdinalIgnoreCase)).ShouldBeFalse();
        typeof(QuarantineMetadata).GetProperties().Any(p => p.Name.Contains("Path", StringComparison.OrdinalIgnoreCase)).ShouldBeFalse();
        typeof(IQuarantineContent).GetProperties().Any(p => p.Name.Contains("Path", StringComparison.OrdinalIgnoreCase)).ShouldBeFalse();
    }

    [Fact]
    public async Task Filesystem_permissions_exclude_other_accounts()
    {
        await using var fixture = Fixture.Create();
        using var input = new MemoryStream(Raw);
        await fixture.Store.WriteAsync(Request(), input, CancellationToken.None);
        var files = Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories);
        files.ShouldNotBeEmpty();
        if (OperatingSystem.IsWindows())
        {
            var allowed = new[] { WindowsIdentity.GetCurrent().User!.Value, "S-1-5-18", "S-1-5-32-544" };
            foreach (var file in files)
            {
                var rules = new FileInfo(file).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
                foreach (FileSystemAccessRule rule in rules)
                    if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & FileSystemRights.ReadData) != 0)
                        allowed.ShouldContain(rule.IdentityReference.Value, "Only owner, SYSTEM or local administrators may read quarantine");
            }
        }
        else
        {
            foreach (var file in files)
                (File.GetUnixFileMode(file) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)).ShouldBe((UnixFileMode)0);
            (File.GetUnixFileMode(fixture.Root) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)).ShouldBe((UnixFileMode)0);
        }
    }

    [Fact]
    public async Task Store_logs_metadata_without_raw_content_or_paths()
    {
        await using var fixture = Fixture.Create();
        using var input = new MemoryStream(Raw);
        await fixture.Store.WriteAsync(Request(), input, CancellationToken.None);
        fixture.Logs.Messages.ShouldNotBeEmpty("Exercise the store's actual logging before checking privacy");
        var logs = string.Join('\n', fixture.Logs.Messages);
        foreach (var secret in new[] { "SEEDED_RAW", "BRA2E19", "-23.55052", "José", "12345678901", "SECRET", fixture.Root })
            logs.ShouldNotContain(secret);
    }

    [Fact]
    public async Task Tampered_content_is_detected_before_successful_read()
    {
        await using var fixture = Fixture.Create();
        var request = Request();
        using var input = new MemoryStream(Raw);
        await fixture.Store.WriteAsync(request, input, CancellationToken.None);
        var files = Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories);
        var target = files.FirstOrDefault(file => File.ReadAllBytes(file).AsSpan().IndexOf(Raw) >= 0)
            ?? files.OrderByDescending(file => new FileInfo(file).Length).First();
        var bytes = await File.ReadAllBytesAsync(target);
        bytes[^1] ^= 0x80;
        await File.WriteAllBytesAsync(target, bytes);
        await Should.ThrowAsync<ProviderOperationException>(async () =>
        {
            await using var content = (await fixture.Store.OpenReadAsync(request.QuarantineId, CancellationToken.None)).ShouldNotBeNull();
            await content.Content.CopyToAsync(Stream.Null);
        });
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task Deletion_is_inclusive_at_30_day_deadline_and_replay_safe(int offsetSeconds, bool deleted)
    {
        await using var fixture = Fixture.Create();
        var request = Request();
        using var input = new MemoryStream(Raw);
        await fixture.Store.WriteAsync(request, input, CancellationToken.None);
        (await fixture.Store.DeleteExpiredAsync(request.QuarantineId, request.DeleteAfter.AddSeconds(offsetSeconds), CancellationToken.None)).ShouldBe(deleted);
        if (deleted)
        {
            (await fixture.Store.GetMetadataAsync(request.QuarantineId, CancellationToken.None)).ShouldBeNull();
            (await fixture.Store.OpenReadAsync(request.QuarantineId, CancellationToken.None)).ShouldBeNull();
            await fixture.Store.DeleteExpiredAsync(request.QuarantineId, request.DeleteAfter.AddSeconds(offsetSeconds), CancellationToken.None);
            Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).ShouldBeEmpty();
        }
        else
            (await fixture.Store.GetMetadataAsync(request.QuarantineId, CancellationToken.None)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Retention_longer_than_30_days_is_rejected_before_any_file_is_written()
    {
        await using var fixture = Fixture.Create();
        var request = Request();
        using var input = new MemoryStream(Raw);
        await Should.ThrowAsync<ArgumentException>(() => fixture.Store.WriteAsync(request with { DeleteAfter = request.DeleteAfter.AddSeconds(1) }, input, CancellationToken.None).AsTask());
        Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task Concurrent_identical_replays_produce_one_recoverable_object()
    {
        await using var fixture = Fixture.Create();
        var request = Request();
        var receipts = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var input = new MemoryStream(Raw);
            return await fixture.Store.WriteAsync(request, input, CancellationToken.None);
        }));
        receipts.Select(receipt => receipt.ContentSha256).Distinct().Count().ShouldBe(1);
        await using var content = (await fixture.Store.OpenReadAsync(request.QuarantineId, CancellationToken.None)).ShouldNotBeNull();
        using var recovered = new MemoryStream();
        await content.Content.CopyToAsync(recovered);
        recovered.ToArray().ShouldBe(Raw);
    }

    [Fact]
    public async Task Conflicting_replay_preserves_original_content()
    {
        await using var fixture = Fixture.Create();
        var request = Request();
        using var input = new MemoryStream(Raw);
        await fixture.Store.WriteAsync(request, input, CancellationToken.None);
        using var replacement = new MemoryStream(Encoding.UTF8.GetBytes("different raw line"));
        await Should.ThrowAsync<ProviderOperationException>(() => fixture.Store.WriteAsync(request, replacement, CancellationToken.None).AsTask());
        await using var content = (await fixture.Store.OpenReadAsync(request.QuarantineId, CancellationToken.None)).ShouldNotBeNull();
        using var recovered = new MemoryStream();
        await content.Content.CopyToAsync(recovered);
        recovered.ToArray().ShouldBe(Raw);
    }

    [Fact]
    public async Task Failed_source_read_exposes_no_partial_object_and_allows_retry()
    {
        await using var fixture = Fixture.Create();
        var request = Request();
        using var broken = new FailingStream();
        var error = await Should.ThrowAsync<ProviderOperationException>(() => fixture.Store.WriteAsync(request, broken, CancellationToken.None).AsTask());
        error.Message.ShouldNotContain("SEEDED_PRIVATE_SOURCE_FAILURE");
        error.InnerException.ShouldBeNull();
        (await fixture.Store.GetMetadataAsync(request.QuarantineId, CancellationToken.None)).ShouldBeNull();
        (await fixture.Store.OpenReadAsync(request.QuarantineId, CancellationToken.None)).ShouldBeNull();
        Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).ShouldBeEmpty();
        using var retry = new MemoryStream(Raw);
        await fixture.Store.WriteAsync(request, retry, CancellationToken.None);
        (await fixture.Store.GetMetadataAsync(request.QuarantineId, CancellationToken.None)).ShouldNotBeNull();
    }

    [Fact]
    public async Task New_store_instance_recovers_published_object_and_replays_without_memory_state()
    {
        await using var fixture = Fixture.Create();
        var request = Request();
        using var input = new MemoryStream(Raw);
        var receipt = await fixture.Store.WriteAsync(request, input, default);
        var reopened = fixture.OpenAnotherStore();
        (await reopened.GetMetadataAsync(request.QuarantineId, default))!.ContentSha256.ShouldBe(receipt.ContentSha256);
        using var replay = new MemoryStream(Raw);
        (await reopened.WriteAsync(request, replay, default)).ShouldBe(receipt);
        Directory.GetFiles(fixture.Root).Length.ShouldBe(1);
    }

    [Fact]
    public async Task Conflicting_metadata_cannot_replace_original_even_when_bytes_match()
    {
        await using var fixture = Fixture.Create();
        var request = Request();
        using var input = new MemoryStream(Raw);
        await fixture.Store.WriteAsync(request, input, default);
        using var replay = new MemoryStream(Raw);
        await Should.ThrowAsync<ProviderOperationException>(() => fixture.OpenAnotherStore()
            .WriteAsync(request with
            {
                LineNumber = request.LineNumber + 1
            }, replay, default).AsTask());
        (await fixture.Store.GetMetadataAsync(request.QuarantineId, default))!.LineNumber.ShouldBe(request.LineNumber);
        Directory.GetFiles(fixture.Root).Length.ShouldBe(1);
    }

    [Fact]
    public async Task Corrupt_raw_bytes_fail_closed_for_metadata_read_and_deletion()
    {
        await using var fixture = Fixture.Create();
        var request = Request();
        using var input = new MemoryStream(Raw);
        await fixture.Store.WriteAsync(request, input, default);
        var file = Directory.GetFiles(fixture.Root).ShouldHaveSingleItem();
        var bytes = await File.ReadAllBytesAsync(file);
        bytes[0] ^= 1;
        await File.WriteAllBytesAsync(file, bytes);
        await Should.ThrowAsync<ProviderOperationException>(() => fixture.Store.OpenReadAsync(request.QuarantineId, default).AsTask());
        await Should.ThrowAsync<ProviderOperationException>(() => fixture.Store.GetMetadataAsync(request.QuarantineId, default).AsTask());
        await Should.ThrowAsync<ProviderOperationException>(() => fixture.Store.DeleteExpiredAsync(request.QuarantineId, request.DeleteAfter, default).AsTask());
    }

    [Fact]
    public async Task Cancelled_write_publishes_nothing_and_preserves_caller_stream()
    {
        await using var fixture = Fixture.Create();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var input = new MemoryStream(Raw);
        await Should.ThrowAsync<OperationCanceledException>(() => fixture.Store.WriteAsync(Request(), input, cancelled.Token).AsTask());
        input.CanRead.ShouldBeTrue();
        Directory.GetFiles(fixture.Root).ShouldBeEmpty();
    }

    [Fact]
    public async Task Earlier_deletion_deadline_is_honored_without_extending_retention()
    {
        await using var fixture = Fixture.Create();
        var request = Request();
        request = request with
        {
            DeleteAfter = request.CreatedAt.AddDays(1)
        };
        using var input = new MemoryStream(Raw);
        await fixture.Store.WriteAsync(request, input, default);
        (await fixture.Store.DeleteExpiredAsync(request.QuarantineId, request.DeleteAfter, default)).ShouldBeTrue();
    }

    private static QuarantineWriteRequest Request()
    {
        var created = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        return new(Guid.NewGuid(), new BatchId(Guid.NewGuid()), 17, TelemetryErrorCodes.LpInvalidSyntax, created, created.AddDays(30));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider services;
        internal string Root
        {
            get;
        }
        internal CaptureLogs Logs { get; } = new();
        internal IQuarantineStore Store
        {
            get;
        }
        private Fixture(Type type)
        {
            Root = Path.Combine(Path.GetTempPath(), "centralmobilidade-t039-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new[] { new System.Collections.Generic.KeyValuePair<string, string?>("Quarantine:Path", Root) }).Build();
            var collection = new ServiceCollection();
            collection.AddSingleton<IConfiguration>(config);
            collection.AddLogging(logging => logging.ClearProviders().AddProvider(Logs));
            services = collection.BuildServiceProvider();
            Store = ActivatorUtilities.CreateInstance(services, type).ShouldBeAssignableTo<IQuarantineStore>()!;
        }
        internal static Fixture Create()
        {
            var type = typeof(BoundedRequestSpool).Assembly.GetType("CentralMobilidade.Telemetry.Infrastructure.Quarantine.FileSystemQuarantineStore");
            type.ShouldNotBeNull("T048 must implement the production protected filesystem quarantine store");
            return new(type);
        }
        internal IQuarantineStore OpenAnotherStore() => (IQuarantineStore)ActivatorUtilities.CreateInstance(services, Store.GetType());
        public async ValueTask DisposeAsync()
        {
            if (Store is IAsyncDisposable disposable)
                await disposable.DisposeAsync();
            await services.DisposeAsync();
            // Exact UUID-owned fixture directory, never a configured shared quarantine path.
            var resolved = Path.GetFullPath(Root);
            Path.GetDirectoryName(resolved).ShouldBe(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())));
            Path.GetFileName(resolved).ShouldStartWith("centralmobilidade-t039-");
            if (Directory.Exists(Root))
                Directory.Delete(resolved, true);
        }
    }

    private sealed class CaptureLogs : ILoggerProvider
    {
        internal ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(Messages);
        public void Dispose()
        {
        }
        private sealed class CaptureLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                messages.Enqueue(formatter(state, exception));
                if (state is IEnumerable<KeyValuePair<string, object?>> properties)
                    foreach (var property in properties)
                        messages.Enqueue(property.Key + "=" + property.Value);
                if (exception is not null)
                    messages.Enqueue(exception.ToString());
            }
        }
    }

    private sealed class FailingStream : Stream
    {
        private bool read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException(); set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (read)
                throw new IOException("SEEDED_PRIVATE_SOURCE_FAILURE");
            read = true;
            Raw.AsMemory(0, Math.Min(Raw.Length, buffer.Length)).CopyTo(buffer);
            return ValueTask.FromResult(Math.Min(Raw.Length, buffer.Length));
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
