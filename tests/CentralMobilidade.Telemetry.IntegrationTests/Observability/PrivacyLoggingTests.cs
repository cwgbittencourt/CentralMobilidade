using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CentralMobilidade.Telemetry.Infrastructure.Observability;
using Serilog;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Observability;

[Collection("Observability")]
public sealed class PrivacyLoggingTests
{
    [Fact]
    public async Task Api_host_still_starts_through_standard_integration_host_tooling()
    {
        await using var factory = new WebApplicationFactory<CentralMobilidade.Telemetry.Api.Security.ProducerBearerHandler>();
        using var client = factory.CreateClient();
        (await client.GetStringAsync("/")).ShouldBe("Central Mobilidade Telemetry API");
    }
    [Fact]
    public void Structured_output_removes_sensitive_properties_message_literals_and_exceptions()
    {
        using var output = new StringWriter();
        using var logger = PrivacyLoggingPolicy.CreateLogger(TelemetryServiceRole.Api, output);
        var batch = Guid.NewGuid();
        logger.ForContext("BatchId", batch).ForContext("ErrorCode", "LP_INVALID_SYNTAX")
            .ForContext("Payload", new
            {
                Plate = "ABC1D23",
                Cpf = "12345678900",
                Latitude = "-23.123456",
                Token = "SEEDED_TOKEN"
            }, true)
            .Error(new InvalidOperationException("SEEDED_EXCEPTION"), "SEEDED_DRIVER_NAME {Bearer} {@Driver}",
                "SEEDED_BEARER", new
                {
                    Name = "SEEDED_NAME",
                    Cpf = "SEEDED_CPF"
                });
        var text = output.ToString();
        foreach (var seed in new[] { "ABC1D23", "12345678900", "-23.123456", "SEEDED_TOKEN", "SEEDED_EXCEPTION", "SEEDED_DRIVER_NAME", "SEEDED_BEARER", "SEEDED_NAME", "SEEDED_CPF" })
            text.ShouldNotContain(seed);
        using var document = JsonDocument.Parse(text);
        document.RootElement.GetProperty("Properties").GetProperty("BatchId").GetString().ShouldBe(batch.ToString());
        document.RootElement.GetProperty("Properties").GetProperty("ErrorCode").GetString().ShouldBe("LP_INVALID_SYNTAX");
        document.RootElement.TryGetProperty("Exception", out _).ShouldBeFalse();
    }

    [Fact]
    public void Destructuring_does_not_read_unknown_object_properties_or_stringify_it()
    {
        using var output = new StringWriter();
        using var logger = PrivacyLoggingPolicy.CreateLogger(TelemetryServiceRole.Worker, output);
        var payload = new PrivatePayload();
        logger.Information("Hidden {@Payload}", payload);
        payload.Accessed.ShouldBeFalse();
        output.ToString().ShouldNotContain("SEEDED_PRIVATE");
    }

    [Fact]
    public void Correlation_comes_from_activity_without_exporting_baggage_or_tags()
    {
        using var output = new StringWriter();
        using var logger = PrivacyLoggingPolicy.CreateLogger(TelemetryServiceRole.Worker, output);
        using var activity = new Activity("SEEDED_OPERATION").SetIdFormat(ActivityIdFormat.W3C).Start();
        var batch = BatchId.New();
        activity.SetTag("batch.id", batch.ToString());
        activity.AddBaggage("cpf", "SEEDED_CPF");
        activity.SetTag("driver", "SEEDED_DRIVER");
        logger.ForContext("TraceId", "SEEDED_TRACE").Information("SEEDED_LITERAL");
        using var json = JsonDocument.Parse(output.ToString());
        var props = json.RootElement.GetProperty("Properties");
        props.GetProperty("TraceId").GetString().ShouldBe(activity.TraceId.ToHexString());
        props.GetProperty("BatchId").GetString().ShouldBe(batch.ToString());
        output.ToString().ShouldNotContain("SEEDED");
    }

    [Fact]
    public void Unknown_keys_and_invalid_safe_values_are_removed_and_aliases_do_not_drop_events()
    {
        using var output = new StringWriter();
        using var logger = PrivacyLoggingPolicy.CreateLogger(TelemetryServiceRole.Api, output);
        logger.ForContext("ErrorCode", "SEEDED_UNKNOWN_CODE").ForContext("TraceId", "SEEDED_BAD_TRACE")
            .ForContext("EventCode", "SEEDED_EVENT").ForContext("SourceContext", "SEEDED_CATEGORY")
            .ForContext("State", "SEEDED_STATE").ForContext("Unknown", "SEEDED_VALUE")
            .ForContext("LATITUDE", -23.123456).ForContext("driver_id", "SEEDED_DRIVER")
            .ForContext("BatchId", Guid.NewGuid()).ForContext("batch_id", Guid.NewGuid())
            .Warning("SEEDED_TEMPLATE");
        output.ToString().ShouldNotContain("SEEDED");
        output.ToString().ShouldNotContain("-23.123456");
        using var json = JsonDocument.Parse(output.ToString());
        json.RootElement.GetProperty("Properties").GetProperty("BatchId").GetString().ShouldNotBeNull();
        json.RootElement.GetProperty("Level").GetString().ShouldBe("Warning");
    }

    [Fact]
    public async Task Microsoft_logger_scopes_request_data_and_framework_errors_use_only_safe_json()
    {
        using var output = new StringWriter();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddPrivacyLogging(TelemetryServiceRole.Api, output);
        builder.Services.AddTelemetryObservability(builder.Configuration, TelemetryServiceRole.Api);
        await using var app = builder.Build();
        var batch = Guid.NewGuid();
        app.MapGet("/{driver}", (Microsoft.Extensions.Logging.ILoggerFactory factory, string driver) =>
        {
            var logger = factory.CreateLogger("SEEDED_CATEGORY");
            using var scope = logger.BeginScope(new Dictionary<string, object?> { ["BatchId"] = batch, ["Authorization"] = "SEEDED_TOKEN" });
            logger.LogError(new IOException("SEEDED_EXCEPTION"), "Driver {Driver} {@Payload} {ErrorCode}", driver,
                new
                {
                    Placa = "ABC1D23",
                    Motorista = "SEEDED_NAME",
                    Cpf = "12345678900"
                }, TelemetryErrorCodes.StorageRejected);
            return "ok";
        });
        await app.StartAsync();
        using var client = app.GetTestClient();
        await client.GetStringAsync("/SEEDED_DRIVER?token=SEEDED_QUERY");
        await app.StopAsync();
        var text = output.ToString();
        foreach (var seed in new[] { "SEEDED", "ABC1D23", "12345678900" })
            text.ShouldNotContain(seed);
        var events = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var entry = events.Single(e => e.RootElement.GetProperty("Level").GetString() == "Error");
            var props = entry.RootElement.GetProperty("Properties");
            props.GetProperty("BatchId").GetString().ShouldBe(batch.ToString());
            props.GetProperty("ErrorCode").GetString().ShouldBe(TelemetryErrorCodes.StorageRejected);
            props.GetProperty("TraceId").GetString()!.Length.ShouldBe(32);
        }
        finally { foreach (var entry in events) entry.Dispose(); }
    }

    [Fact]
    public void Parallel_writes_produce_complete_json_lines_and_keep_safe_counters()
    {
        using var output = new StringWriter();
        using var logger = PrivacyLoggingPolicy.CreateLogger(TelemetryServiceRole.Worker, output);
        Parallel.For(0, 32, index => logger.ForContext("EventCode", OperationalLogCode.BatchCompleted)
            .ForContext("Accepted", index).Information("SEEDED_STRING"));
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(32);
        foreach (var line in lines)
        {
            using var json = JsonDocument.Parse(line);
            json.RootElement.GetProperty("Properties").GetProperty("accepted").GetInt64().ShouldBeGreaterThanOrEqualTo(0);
            json.RootElement.GetProperty("Properties").GetProperty("EventCode").GetString().ShouldBe("BatchCompleted");
        }
        output.ToString().ShouldNotContain("SEEDED_STRING");
    }

    private sealed class PrivatePayload
    {
        public bool Accessed
        {
            get; private set;
        }
        public string Body
        {
            get
            {
                Accessed = true;
                throw new InvalidOperationException("SEEDED_PRIVATE");
            }
        }
        public override string ToString()
        {
            Accessed = true;
            throw new InvalidOperationException("SEEDED_PRIVATE");
        }
    }

    [Theory]
    [InlineData("Api")]
    [InlineData("Worker")]
    public async Task Startup_failures_emit_safe_json_and_nonzero_exit_without_raw_stderr(string project)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CentralMobilidade.slnx")))
            root = root.Parent;
        root.ShouldNotBeNull();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var directory = Path.Combine(root.FullName, "src", "CentralMobilidade.Telemetry." + project);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }
        };
        process.StartInfo.ArgumentList.Add(Path.Combine(directory, "bin", configuration, "net10.0", "CentralMobilidade.Telemetry." + project + ".dll"));
        process.StartInfo.Environment["Observability__TraceSamplingRatio"] = "SEEDED_STARTUP_CPF_TOKEN";
        process.StartInfo.Environment["DOTNET_ENVIRONMENT"] = "Development";
        process.StartInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        process.ExitCode.ShouldBe(1);
        (await stderr).ShouldBeEmpty();
        var text = await stdout;
        text.ShouldNotContain("SEEDED_STARTUP_CPF_TOKEN");
        using var json = JsonDocument.Parse(text);
        json.RootElement.GetProperty("Properties").GetProperty("EventCode").GetString().ShouldBe("ServiceFailure");
        json.RootElement.TryGetProperty("Exception", out _).ShouldBeFalse();
    }
}
