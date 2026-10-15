using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Security;
using CentralMobilidade.Telemetry.Contracts.Api.V1;
using CentralMobilidade.Telemetry.IntegrationTests.Persistence;
using CentralMobilidade.Telemetry.IntegrationTests.Queue;
using RabbitMQ.Client;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Recovery;

[Trait("Category", "Integration")]
[Trait("Feature", "US1Recovery")]
public sealed class AcceptedBatchSurvivalTests(RabbitMqContractBroker broker) : IClassFixture<RabbitMqContractBroker>
{
    private static readonly byte[] Payload = MakeGzip();

    [Fact]
    public async Task Accepted_envelope_survives_abrupt_API_termination_after_durable_acknowledgement()
    {
        await using var postgres = await MigratedPostgresFixture.StartAsync();
        await using var api = await ApiProcess.Start(broker.Endpoint, postgres.Connection);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/telemetry/batches");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", api.Token);
        request.Headers.Add("X-Contract-Version", "v1");
        request.Content = new ByteArrayContent(Payload);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain; charset=utf-8");
        request.Content.Headers.ContentEncoding.Add("gzip");
        using var response = await api.Client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, "T033/T034/T035 must provide intake and wait for the publisher confirm before 202");
        var accepted = JsonSerializer.Deserialize<BatchAccepted>(await response.Content.ReadAsStringAsync());
        accepted.ShouldNotBeNull();
        accepted.BatchId.ShouldNotBe(Guid.Empty);
        accepted.State.ShouldBe("accepted");
        accepted.StatusUrl.ShouldBe("/api/v1/telemetry/batches/" + accepted.BatchId.ToString("D"));
        response.Headers.Location!.OriginalString.ShouldBe(accepted.StatusUrl);
        // Response fully received; kill without graceful shutdown or consuming/acking the batch.
        await api.Kill();
        await using var recovery = await broker.Connect();
        await using var channel = await recovery.CreateChannelAsync();
        (await channel.QueueDeclarePassiveAsync(api.Queue)).MessageCount.ShouldBe(1u);
        var delivery = await channel.BasicGetAsync(api.Queue, autoAck: false);
        delivery.ShouldNotBeNull("The acknowledged envelope must remain available after API death");
        delivery.Body.ToArray().ShouldBe(Payload);
        delivery.BasicProperties.Persistent.ShouldBeTrue();
        delivery.BasicProperties.ContentType.ShouldBe("text/plain; charset=utf-8");
        delivery.BasicProperties.ContentEncoding.ShouldBe("gzip");
        var headers = delivery.BasicProperties.Headers!;
        Header(headers["batchId"]).ShouldBe(accepted.BatchId.ToString("D"));
        Header(headers["schemaVersion"]).ShouldBe("1");
        Header(headers["contractVersion"]).ShouldBe("v1");
        Header(headers["producerId"]).ShouldBe("recovery-producer");
        Header(headers["traceId"]).ShouldNotBeNullOrWhiteSpace();
        DateTimeOffset.Parse(Header(headers["receivedAt"]), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(accepted.ReceivedAt);
        Header(headers["payloadSha256"]).ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Payload)));
        Convert.ToInt64(headers["compressedBytes"], System.Globalization.CultureInfo.InvariantCulture).ShouldBe(Payload.Length);
        await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true);
        await using var secondRecovery = await broker.Connect();
        await using var secondChannel = await secondRecovery.CreateChannelAsync();
        using var redeliveryDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        BasicGetResult? redelivery;
        do
        {
            redelivery = await secondChannel.BasicGetAsync(api.Queue, autoAck: false, redeliveryDeadline.Token);
            if (redelivery is null)
                await Task.Delay(TimeSpan.FromMilliseconds(25), redeliveryDeadline.Token);
        } while (redelivery is null);
        redelivery.ShouldNotBeNull();
        redelivery.Redelivered.ShouldBeTrue();
        redelivery.Body.ToArray().ShouldBe(Payload);
        await secondChannel.BasicAckAsync(redelivery.DeliveryTag, multiple: false);
    }

    [Fact]
    public async Task Crash_harness_kills_real_API_process_without_stopping_the_broker()
    {
        await using var postgres = await MigratedPostgresFixture.StartAsync();
        await using var api = await ApiProcess.Start(broker.Endpoint, postgres.Connection);
        await using var publisher = await broker.Connect();
        await using var channel = await publisher.CreateChannelAsync(new CreateChannelOptions(true, true));
        // Harness probe: this message is published by the test, not the intake adapter.
        await channel.QueueDeclareAsync(api.Queue, durable: true, exclusive: false, autoDelete: false);
        await channel.BasicPublishAsync("", api.Queue, true, new BasicProperties { Persistent = true }, Payload);
        await api.Kill();
        await using var recovery = await broker.Connect();
        await using var fresh = await recovery.CreateChannelAsync();
        var message = await fresh.BasicGetAsync(api.Queue, autoAck: true);
        message.ShouldNotBeNull();
        message.Body.ToArray().ShouldBe(Payload);
    }

    private static string Header(object? value) => value is byte[] bytes ? Encoding.UTF8.GetString(bytes) : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!;
    private static byte[] MakeGzip()
    {
        using var bytes = new MemoryStream();
        using (var gzip = new GZipStream(bytes, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes("telemetria,empresa_id=company,veiculo_id=vehicle latitude=1.0,longitude=2.0,velocidade=0.0,rpm=0i,ignicao=false 1791849600000\n"));
        return bytes.ToArray();
    }

    private sealed class ApiProcess : IAsyncDisposable
    {
        private readonly Process process;
        private readonly Uri brokerEndpoint;
        private readonly Task outputDrain;
        private readonly Task errorDrain;
        private readonly string name = "t027." + Guid.NewGuid().ToString("N");
        internal string Token { get; } = OpaqueBearerTokens.Generate(BearerCredentialKind.Producer);
        internal string Queue => name + ".queue";
        internal HttpClient Client
        {
            get;
        }

        private ApiProcess(Uri brokerEndpoint, string postgres)
        {
            this.brokerEndpoint = brokerEndpoint;
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "CentralMobilidade.slnx")))
                root = root.Parent;
            var project = Path.Combine(root?.FullName ?? throw new InvalidOperationException("Recovery tests require the solution checkout."), "src", "CentralMobilidade.Telemetry.Api");
#if DEBUG
            const string configuration = "Debug";
#else
            const string configuration = "Release";
#endif
            var binary = Path.Combine(project, "bin", configuration, "net10.0", "CentralMobilidade.Telemetry.Api.dll");
            File.Exists(binary).ShouldBeTrue("Build the API before running recovery tests");
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var address = "http://127.0.0.1:" + port;
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = project, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(binary);
            start.ArgumentList.Add("--urls");
            start.ArgumentList.Add(address);
            var hash = BearerTokenHash.Create(Token);
            var rabbit = new ConnectionFactory { Uri = brokerEndpoint };
            foreach (var (key, value) in new Dictionary<string, string>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Testing",
                ["AllowedHosts"] = "127.0.0.1;localhost",
                ["ConnectionStrings__TelemetryControl"] = postgres,
                ["Authentication__Producers__0__CredentialId"] = "recovery-test",
                ["Authentication__Producers__0__ProducerId"] = "recovery-producer",
                ["Authentication__Producers__0__Enabled"] = "true",
                ["Authentication__Producers__0__Hash__LookupDigest"] = hash.LookupDigest,
                ["Authentication__Producers__0__Hash__Salt"] = hash.Salt,
                ["Authentication__Producers__0__Hash__Verifier"] = hash.Verifier,
                ["Authentication__Producers__0__Hash__Iterations"] = hash.Iterations.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Queue__Provider"] = "RabbitMq",
                ["Queue__RabbitMq__Host"] = rabbit.HostName,
                ["Queue__RabbitMq__Port"] = rabbit.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Queue__RabbitMq__VirtualHost"] = rabbit.VirtualHost,
                ["Queue__RabbitMq__Username"] = rabbit.UserName,
                ["Queue__RabbitMq__Password"] = rabbit.Password,
                ["Queue__RabbitMq__Exchange"] = name + ".exchange",
                ["Queue__RabbitMq__Queue"] = Queue,
                ["Queue__RabbitMq__RoutingKey"] = name + ".route",
                ["Queue__RabbitMq__RetryExchange"] = name + ".retry.exchange",
                ["Queue__RabbitMq__RetryQueue"] = name + ".retry.queue",
                ["Queue__RabbitMq__DeadLetterExchange"] = name + ".dead.exchange",
                ["Queue__RabbitMq__DeadLetterQueue"] = name + ".dead.queue"
            })
                start.Environment[key] = value;
            process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch the API.");
            outputDrain = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            errorDrain = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            // This is a crash/durability proof, not a latency benchmark. Allow the configured
            // 30-second upload budget plus publication/state work before the harness aborts.
            Client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(45) };
        }

        internal static async Task<ApiProcess> Start(Uri brokerEndpoint, string postgres)
        {
            var api = new ApiProcess(brokerEndpoint, postgres);
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (true)
                {
                    if (api.process.HasExited)
                        throw new InvalidOperationException("API exited before liveness; exit code " + api.process.ExitCode);
                    try
                    {
                        using var response = await api.Client.GetAsync("/health/live", deadline.Token);
                        if (response.StatusCode == HttpStatusCode.OK)
                            return api;
                    }
                    catch (HttpRequestException) { }
                    await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token);
                }
            }
            catch { await api.DisposeAsync(); throw; }
        }

        internal async Task Kill()
        {
            process.HasExited.ShouldBeFalse("The actual API must be alive at the crash boundary");
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            process.HasExited.ShouldBeTrue();
            process.ExitCode.ShouldNotBe(0, "The crash must not be a graceful shutdown");
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await Task.WhenAll(outputDrain, errorDrain).WaitAsync(TimeSpan.FromSeconds(5));
                await using var connection = await new ConnectionFactory { Uri = brokerEndpoint }.CreateConnectionAsync();
                foreach (var suffix in new[] { ".queue", ".retry.queue", ".dead.queue", ".exchange", ".retry.exchange", ".dead.exchange" })
                {
                    await using var channel = await connection.CreateChannelAsync();
                    try
                    {
                        if (suffix.EndsWith(".queue", StringComparison.Ordinal))
                            await channel.QueueDeleteAsync(name + suffix);
                        else
                            await channel.ExchangeDeleteAsync(name + suffix);
                    }
                    catch (RabbitMQ.Client.Exceptions.OperationInterruptedException error) when (error.ShutdownReason?.ReplyCode == 404) { }
                }
            }
            finally { Client.Dispose(); process.Dispose(); }
        }
    }
}
