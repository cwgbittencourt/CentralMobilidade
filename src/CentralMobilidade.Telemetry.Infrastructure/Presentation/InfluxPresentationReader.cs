using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Api.V1;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using Microsoft.Extensions.Configuration;

namespace CentralMobilidade.Telemetry.Infrastructure.Presentation;

public sealed class InfluxPresentationReader(HttpClient client, IConfiguration configuration) : ITelemetryPresentationReader
{
    public ValueTask<IReadOnlyList<TelemetryPosition>> LatestAsync(CompanyId company, VehicleId? vehicle, string? afterVehicle, int limit, CancellationToken token)
        => ReadAsync(company, vehicle, afterVehicle, null, null, limit, token);

    public ValueTask<IReadOnlyList<TelemetryPosition>> TripAsync(CompanyId company, VehicleId vehicle, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken token)
        => ReadAsync(company, vehicle, null, from, to, limit, token);

    private async ValueTask<IReadOnlyList<TelemetryPosition>> ReadAsync(CompanyId company, VehicleId? vehicle, string? after, DateTimeOffset? from, DateTimeOffset? to, int limit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (limit < 1 || limit > (from is null ? 501 : 10000))
            throw Failure();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var schema = await QueryAsync("SELECT column_name FROM information_schema.columns WHERE table_name = $table", new
            {
                table = "telemetria"
            }, deadline.Token);
            var columns = schema.RootElement.EnumerateArray().Select(row => row.GetProperty("column_name").GetString()).ToHashSet(StringComparer.Ordinal);
            if (columns.Count == 0)
                return [];
            if (new[] { "time", "empresa_id", "veiculo_id", "latitude", "longitude" }.Any(name => !columns.Contains(name)))
                throw Failure();
            var fields = "time, empresa_id, veiculo_id, latitude, longitude, " + string.Join(", ", new[] { "velocidade", "ignicao", "motorista_id" }.Select(name => columns.Contains(name) ? name : "NULL AS " + name));
            var where = "empresa_id = $company" + (vehicle is null ? "" : " AND veiculo_id = $vehicle");
            var parameters = new Dictionary<string, object> { ["company"] = company.Value };
            if (vehicle is not null)
                parameters["vehicle"] = vehicle.Value;
            string sql;
            if (from is not null)
            {
                parameters["from"] = from.Value.ToString("O", CultureInfo.InvariantCulture);
                parameters["to"] = to!.Value.ToString("O", CultureInfo.InvariantCulture);
                sql = $"SELECT {fields} FROM telemetria WHERE {where} AND time >= CAST($from AS TIMESTAMP) AND time < CAST($to AS TIMESTAMP) ORDER BY time ASC LIMIT {limit.ToString(CultureInfo.InvariantCulture)}";
            }
            else
            {
                if (after is not null)
                {
                    where += " AND veiculo_id > $after";
                    parameters["after"] = after;
                }
                sql = $"SELECT {fields} FROM (SELECT {fields}, ROW_NUMBER() OVER (PARTITION BY veiculo_id ORDER BY time DESC) AS position_rank FROM telemetria WHERE {where}) WHERE position_rank = 1 ORDER BY veiculo_id ASC LIMIT {limit.ToString(CultureInfo.InvariantCulture)}";
            }
            using var data = await QueryAsync(sql, parameters, deadline.Token);
            var rows = new List<TelemetryPosition>();
            foreach (var row in data.RootElement.EnumerateArray())
            {
                var time = DateTimeOffset.Parse(row.GetProperty("time").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                var item = new TelemetryPosition(row.GetProperty("empresa_id").GetString()!, row.GetProperty("veiculo_id").GetString()!, time,
                    row.GetProperty("latitude").GetDouble(), row.GetProperty("longitude").GetDouble(),
                    Present(row, "velocidade") ? row.GetProperty("velocidade").GetDouble() : null,
                    Present(row, "ignicao") ? row.GetProperty("ignicao").GetBoolean() : null,
                    Present(row, "motorista_id") ? row.GetProperty("motorista_id").GetString() : null);
                _ = new VehicleId(item.VehicleId);
                if (item.DriverId is not null)
                    _ = new DriverId(item.DriverId);
                if (item.EmpresaId != company.Value || vehicle is not null && item.VehicleId != vehicle.Value
                    || !double.IsFinite(item.Latitude) || Math.Abs(item.Latitude) > 90 || !double.IsFinite(item.Longitude) || Math.Abs(item.Longitude) > 180
                    || item.Speed is { } speed && (!double.IsFinite(speed) || speed < 0)
                    || from is not null && (time < from || time >= to || rows.Count > 0 && time < rows[^1].EventTime)
                    || from is null && rows.Any(previous => previous.VehicleId == item.VehicleId)
                    || rows.Count >= limit)
                    throw Failure();
                rows.Add(item);
            }
            return rows;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { throw Failure(); }
    }

    private async Task<JsonDocument> QueryAsync(string sql, object parameters, CancellationToken token)
    {
        var section = configuration.GetSection("InfluxDb:Queryable");
        var url = new Uri(section["Url"] ?? "", UriKind.Absolute);
        if (url.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
            throw Failure();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url, "/api/v3/query_sql"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", section["Token"]);
        request.Content = JsonContent.Create(new
        {
            db = section["Database"],
            q = sql,
            @params = parameters,
            format = "json"
        });
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        const int maximum = 8 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maximum)
            throw Failure();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var block = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(block, token)) != 0)
        {
            if (buffer.Length + read > maximum)
                throw Failure();
            buffer.Write(block, 0, read);
        }
        return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
    }

    private static bool Present(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null;
    private static ProviderOperationException Failure() => new(TelemetryErrorCodes.InternalError, ProviderFailureKind.Transient);
}
