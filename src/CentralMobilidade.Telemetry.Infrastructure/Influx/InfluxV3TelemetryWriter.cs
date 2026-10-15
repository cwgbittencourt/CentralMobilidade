using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Errors;
using Microsoft.Extensions.Configuration;

namespace CentralMobilidade.Telemetry.Infrastructure.Influx;

public sealed class InfluxV3TelemetryWriter : ITelemetryWriter
{
    private readonly HttpClient client;
    private readonly Destination queryable;
    private readonly TimeSpan timeout;
    private readonly int maxPoints;
    private readonly long maxBytes;
    internal const int MaxResponseBytes = 256 * 1024;

    public InfluxV3TelemetryWriter(HttpClient client, IConfiguration configuration)
    {
        this.client = client;
        queryable = ReadDestination(configuration);
        timeout = configuration.GetValue("InfluxDb:WriteTimeout", TimeSpan.FromSeconds(30));
        maxPoints = configuration.GetValue("Telemetry:WriteBatchMaxPoints", 5000);
        maxBytes = configuration.GetValue("Telemetry:WriteBatchMaxBytes", 2L * 1024 * 1024);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5) || maxPoints <= 0 || maxBytes <= 0)
            throw new ArgumentException("Invalid Influx write limits.");
    }

    public async ValueTask<TelemetryWriteResult> WriteAsync(TelemetryWriteRequest request, Stream gzipLineProtocol, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(gzipLineProtocol);
        if (request.Points is null || request.Points.Count == 0 ||
            request.Points.Count > maxPoints || request.UncompressedBytes <= 0 || request.UncompressedBytes > maxBytes || !gzipLineProtocol.CanRead)
            throw new ArgumentException("Invalid bounded write request.", nameof(request));
        var owners = new HashSet<(Guid, int)>();
        foreach (var point in request.Points)
            if (point is null || point.BatchId is null || point.Identity is null || point.LineNumber <= 0 || !owners.Add((point.BatchId.Value, point.LineNumber)))
                throw new ArgumentException("Invalid or repeated write owner.", nameof(request));
        if (request.TraceId is null || request.TraceId.Length != 32 || !IsHexTrace(request.TraceId))
            throw new ArgumentException("A valid trace identifier is required.", nameof(request));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, queryable.Uri);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", queryable.Token);
            message.Headers.Accept.Add(new("application/json"));
            message.Headers.TryAddWithoutValidation("traceparent", $"00-{request.TraceId}-{ActivitySpanId.CreateRandom()}-01");
            message.Content = new BorrowedGzipContent(gzipLineProtocol);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.NoContent)
                return new(request.Points.Count, []);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
            {
                var body = await BoundedProviderJson.ReadAsync(response.Content, MaxResponseBytes, deadline.Token);
                return ReconcilePartial(body, request);
            }
            throw Failure(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.RequestEntityTooLarge
                ? ProviderFailureKind.Permanent : ProviderFailureKind.Ambiguous);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or JsonException)
        {
            throw Failure(ProviderFailureKind.Ambiguous);
        }
    }

    private static TelemetryWriteResult ReconcilePartial(byte[] body, TelemetryWriteRequest request)
    {
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !BoundedProviderJson.UniqueProperties(root) ||
            !root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.String ||
            error.GetString() != "partial write of line protocol occurred" || !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0 || data.GetArrayLength() > request.Points.Count)
            throw Failure(ProviderFailureKind.Ambiguous);
        var rejected = new HashSet<int>();
        var rejections = new List<TelemetryWriteRejection>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !BoundedProviderJson.UniqueProperties(item) ||
                !item.TryGetProperty("line_number", out var line) || line.ValueKind != JsonValueKind.Number ||
                !line.TryGetInt32(out var number) || number <= 0 || number > request.Points.Count || !rejected.Add(number) ||
                !item.TryGetProperty("error_message", out var detail) || detail.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(detail.GetString()))
                throw Failure(ProviderFailureKind.Ambiguous);
            var point = request.Points[number - 1];
            var text = detail.GetString()!;
            var code = text.Contains("type", StringComparison.OrdinalIgnoreCase) &&
                (text.Contains("column", StringComparison.OrdinalIgnoreCase) || text.Contains("field", StringComparison.OrdinalIgnoreCase))
                ? TelemetryErrorCodes.StorageSchemaConflict : TelemetryErrorCodes.StorageRejected;
            rejections.Add(new(point.BatchId, point.LineNumber, code));
        }
        return new(request.Points.Count - rejected.Count, rejections);
    }

    private static Destination ReadDestination(IConfiguration configuration)
    {
        var url = configuration["InfluxDb:Queryable:Url"];
        var database = configuration["InfluxDb:Queryable:Database"];
        var token = configuration["InfluxDb:Queryable:Token"];
        if (!Uri.TryCreate(url, UriKind.Absolute, out var origin) || origin.Scheme is not ("http" or "https") ||
            origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.AbsolutePath != "/" ||
            string.IsNullOrWhiteSpace(database) || string.IsNullOrWhiteSpace(token) || token.Contains('\r') || token.Contains('\n'))
            throw new ArgumentException("Invalid Influx destination configuration.");
        return new(new Uri(origin, "/api/v3/write_lp?db=" + Uri.EscapeDataString(database) +
            "&precision=millisecond&accept_partial=true&no_sync=false"), token);
    }
    private static bool IsHexTrace(string trace)
    {
        var nonZero = false;
        foreach (var c in trace)
        {
            if (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
            nonZero |= c != '0';
        }
        return nonZero;
    }
    private static ProviderOperationException Failure(ProviderFailureKind kind) => new(TelemetryErrorCodes.StorageRejected, kind);
    private sealed record Destination(Uri Uri, string Token);

    private sealed class BorrowedGzipContent : HttpContent
    {
        private readonly Stream input;
        public BorrowedGzipContent(Stream input)
        {
            this.input = input;
            Headers.ContentType = new("text/plain");
            Headers.ContentEncoding.Add("gzip");
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => input.CopyToAsync(stream, 65536);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) => input.CopyToAsync(stream, 65536, token);
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}

internal static class BoundedProviderJson
{
    internal static async ValueTask<byte[]> ReadAsync(HttpContent content, int limit, CancellationToken token)
    {
        if (content.Headers.ContentLength > limit)
            throw new IOException("Provider response limit exceeded.");
        await using var input = await content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit + 1 - (int)output.Length)), token);
            if (read == 0)
                return output.ToArray();
            output.Write(buffer, 0, read);
            if (output.Length > limit)
                throw new IOException("Provider response limit exceeded.");
        }
    }
    internal static bool UniqueProperties(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Add(property.Name))
                return false;
        return true;
    }
}
