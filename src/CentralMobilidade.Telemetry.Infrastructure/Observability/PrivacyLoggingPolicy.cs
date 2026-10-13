using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Json;
using Serilog.Parsing;

namespace CentralMobilidade.Telemetry.Infrastructure.Observability;

public enum OperationalLogCode
{
    ApplicationEvent, ServiceStarted, ServiceStopped, ServiceFailure, BatchReceived, BatchPublished,
    BatchProcessing, BatchCompleted, BatchRejected, BatchRetryScheduled, DependencyFailure,
    AuthorizationDenied, AuditUnavailable, QuarantineWritten, PromotionProgress
}

public static class PrivacyLoggingPolicy
{
    public static Logger CreateLogger(TelemetryServiceRole role, TextWriter? output = null)
    {
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role));
        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .Destructure.ToMaximumDepth(4).Destructure.ToMaximumCollectionCount(16).Destructure.ToMaximumStringLength(256)
            .Destructure.With(new PrivateObjectPolicy())
            .Enrich.FromLogContext().Enrich.With(new TraceBatchEnricher())
            .WriteTo.Sink(new PrivacyJsonSink(output ?? Console.Out, role))
            .CreateLogger();
    }

    public static IServiceCollection AddPrivacyLogging(this IServiceCollection services, TelemetryServiceRole role, TextWriter? output = null)
    {
        // No unfiltered fan-out to default console/debug/event-source providers.
        services.AddLogging(logging => logging.ClearProviders());
        return services.AddSerilog(CreateLogger(role, output), dispose: true);
    }

    private sealed class PrivateObjectPolicy : IDestructuringPolicy
    {
        public bool TryDestructure(object value, ILogEventPropertyValueFactory factory, out LogEventPropertyValue result)
        {
            // Never reflect unknown objects, read payload getters or call their ToString methods.
            result = value is BatchId batch ? new ScalarValue(batch.Value) : new ScalarValue("[REDACTED]");
            return true;
        }
    }

    private sealed class TraceBatchEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory factory)
        {
            var activity = Activity.Current;
            if (activity is null)
                return;
            logEvent.AddOrUpdateProperty(factory.CreateProperty("TraceId", activity.TraceId.ToHexString()));
            logEvent.AddOrUpdateProperty(factory.CreateProperty("SpanId", activity.SpanId.ToHexString()));
            if (activity.GetTagItem("batch.id") is string value && Guid.TryParseExact(value, "D", out var batch) && batch != Guid.Empty)
                logEvent.AddOrUpdateProperty(factory.CreateProperty("BatchId", batch));
        }
    }

    private sealed class PrivacyJsonSink(TextWriter output, TelemetryServiceRole role) : ILogEventSink
    {
        private readonly object sync = new();
        private readonly JsonFormatter formatter = new(renderMessage: false);
        private static readonly MessageTemplate Template = new MessageTemplateParser().Parse("Operational event");

        public void Emit(LogEvent original)
        {
            var properties = new List<LogEventProperty>();
            foreach (var property in original.Properties)
                if (SafeProperty(property.Key, property.Value) is { } safe)
                {
                    properties.RemoveAll(p => p.Name == safe.Name);
                    properties.Add(safe);
                }
            properties.RemoveAll(p => p.Name is "Service" or "Source" or "ExceptionCategory");
            properties.Add(new("Service", new ScalarValue(role == TelemetryServiceRole.Api ? "api" : "worker")));
            properties.Add(new("Source", new ScalarValue(SourceCategory(original))));
            if (properties.All(p => p.Name != "EventCode"))
                properties.Add(new("EventCode", new ScalarValue(OperationalLogCode.ApplicationEvent.ToString())));
            if (original.Exception is not null)
                properties.Add(new("ExceptionCategory", new ScalarValue(ExceptionCategory(original.Exception))));
            // Message literals/rendered strings and exception text bypass destructuring; replace them at the final boundary.
            var sanitized = new LogEvent(original.Timestamp, original.Level, null, Template, properties,
                original.TraceId ?? default, original.SpanId ?? default);
            lock (sync)
            {
                formatter.Format(sanitized, output);
                output.Flush();
            }
        }

        private static LogEventProperty? SafeProperty(string name, LogEventPropertyValue value)
        {
            if (value is not ScalarValue scalar)
                return null;
            var key = name.Replace("_", "", StringComparison.Ordinal).Replace(".", "", StringComparison.Ordinal).ToLowerInvariant();
            string? canonical = null;
            object? safe = null;
            switch (key)
            {
                case "batchid":
                case "quarantineid":
                    var id = scalar.Value is Guid guid ? guid : scalar.Value is string text && Guid.TryParseExact(text, "D", out var parsed) ? parsed : Guid.Empty;
                    if (id != Guid.Empty)
                    {
                        canonical = key == "batchid" ? "BatchId" : "QuarantineId";
                        safe = id;
                    }
                    break;
                case "traceid":
                case "spanid":
                    if (scalar.Value is string hex && hex.Length == (key == "traceid" ? 32 : 16) && hex.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') && hex.Any(c => c != '0'))
                    {
                        canonical = key == "traceid" ? "TraceId" : "SpanId";
                        safe = hex;
                    }
                    break;
                case "errorcode":
                    if (scalar.Value is string code && (TelemetryErrorCodes.RejectionCodes.Contains(code) || TelemetryErrorCodes.Problems.ContainsKey(code)))
                    {
                        canonical = "ErrorCode";
                        safe = code;
                    }
                    break;
                case "eventcode":
                    if (KnownEnum(scalar.Value, out OperationalLogCode eventCode))
                    {
                        canonical = "EventCode";
                        safe = eventCode.ToString();
                    }
                    break;
                case "state":
                    if (KnownEnum(scalar.Value, out BatchLifecycle state))
                    {
                        canonical = "State";
                        safe = state.ToString();
                    }
                    break;
                case "accepted":
                case "rejected":
                case "replayed":
                case "linenumber":
                case "retrycount":
                case "compressedbytes":
                case "decompressedbytes":
                case "pointcount":
                    var count = scalar.Value switch
                    {
                        long number => number,
                        int number => number,
                        _ => -1L
                    };
                    if (count >= 0)
                    {
                        canonical = key;
                        safe = count;
                    }
                    break;
                case "statuscode":
                    if (scalar.Value is int status && status is >= 100 and <= 599)
                    {
                        canonical = "StatusCode";
                        safe = status;
                    }
                    break;
                case "elapsedmilliseconds":
                    if (scalar.Value is double elapsed && double.IsFinite(elapsed) && elapsed >= 0)
                    {
                        canonical = "ElapsedMilliseconds";
                        safe = elapsed;
                    }
                    break;
            }
            return canonical is null ? null : new LogEventProperty(canonical, new ScalarValue(safe));
        }

        private static bool KnownEnum<T>(object? value, out T result) where T : struct, Enum
        {
            result = default;
            if (value is T typed)
                result = typed;
            else if (value is not string text || !Enum.TryParse(text, out result))
                return false;
            return Enum.IsDefined(result);
        }

        private static string SourceCategory(LogEvent logEvent)
        {
            var source = logEvent.Properties.TryGetValue("SourceContext", out var property) && property is ScalarValue { Value: string context } ? context : "";
            if (source.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) || source.StartsWith("Npgsql", StringComparison.Ordinal))
                return "database";
            if (source.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) || source.StartsWith("System.Net.Http", StringComparison.Ordinal))
                return "http";
            if (source.StartsWith("Microsoft.Hosting", StringComparison.Ordinal) || source.StartsWith("Microsoft.Extensions.Hosting", StringComparison.Ordinal))
                return "host";
            return "application";
        }

        private static string ExceptionCategory(Exception exception) => exception switch
        {
            OperationCanceledException => "canceled",
            TimeoutException => "timeout",
            IOException => "io",
            System.Data.Common.DbException => "database",
            UnauthorizedAccessException => "access_denied",
            ArgumentException => "invalid_argument",
            InvalidOperationException => "invalid_operation",
            _ => "unexpected"
        };
    }
}
