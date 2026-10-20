using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace CentralMobilidade.Telemetry.LoadTests.Scenarios;

internal static class FinalProofGrafana
{
    private const string Infinity = "yesoreyeram-infinity-datasource";
    internal static async Task PreflightAsync(ProofHttp wire, FinalProofOptions o, FinalProofSecrets s, CancellationToken ct)
    {
        using (var response = await wire.Send(o.Grafana, "api/health", s.InternalGrafana, ct))
        {
            var health = await ProofHttp.Json(response, ct);
            ProofHttp.Require(health.GetProperty("database").GetString() == "ok", "grafana_unhealthy");
            await wire.Save("grafana-health.json", health, ct);
        }
        foreach (var viewer in new[] { false, true })
        {
            var secret = viewer ? s.ViewerGrafana : s.InternalGrafana;
            var org = viewer ? o.ViewerOrganizationId : o.InternalOrganizationId;
            using var orgResponse = await wire.Send(o.Grafana, "api/org", secret, ct, org: org);
            var organization = await ProofHttp.Json(orgResponse, ct);
            ProofHttp.Require(organization.GetProperty("id").GetInt32() == org, "grafana_org_mismatch");
            using var response = await wire.Send(o.Grafana, "api/datasources", secret, ct, org: org);
            var sources = await ProofHttp.Json(response, ct);
            var types = sources.EnumerateArray().Select(x => x.GetProperty("type").GetString()).ToArray();
            ProofHttp.Require(viewer ? types.Length > 0 && types.All(t => t == Infinity) : types.Contains("influxdb"), "grafana_datasource_isolation");
            await wire.Save(viewer ? "viewer-source-types.json" : "internal-source-types.json", types, ct);
        }
    }

    internal static async Task VerifyAsync(ProofHttp wire, FinalProofOptions o, FinalProofSecrets s, DateTimeOffset started, CancellationToken ct)
    {
        long internalTotal = 0;
        foreach (var viewer in new[] { false, true })
            foreach (var kind in new[] { "latest-vehicle-position", "vehicle-trip", "company-ingestion-health" })
            {
                var audience = viewer ? "viewer" : "internal";
                var uid = audience + "-" + (kind == "latest-vehicle-position" ? "latest-position" : kind == "vehicle-trip" ? "vehicle-trip" : "ingestion-health");
                var credential = viewer ? s.ViewerGrafana : s.InternalGrafana;
                var org = viewer ? o.ViewerOrganizationId : o.InternalOrganizationId;
                using var response = await wire.Send(o.Grafana, "api/dashboards/uid/" + uid, credential, ct, org: org);
                var loaded = await ProofHttp.Json(response, ct);
                var model = loaded.GetProperty("dashboard");
                ProofHttp.Require(model.GetProperty("uid").GetString() == uid, "dashboard_uid_mismatch");
                var panels = model.GetProperty("panels").EnumerateArray().Where(p => p.GetProperty("type").GetString() != "row").ToArray();
                // These production dashboards currently have one table panel, not a replacement proof dashboard.
                ProofHttp.Require(panels.Length == 1, "dashboard_panel_mismatch");
                var panel = panels[0];
                using var template = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Root(), "deploy/grafana/dashboards", audience, kind + ".json"), ct));
                var expected = template.RootElement.GetProperty("panels")[0].GetProperty("targets");
                var actual = panel.GetProperty("targets");
                ProofHttp.Require(actual.GetArrayLength() == expected.GetArrayLength() && actual.GetArrayLength() == 1, "dashboard_targets_mismatch");
                var queryProperty = viewer ? "url" : "rawSql";
                ProofHttp.Require(actual[0].GetProperty(queryProperty).GetString() == expected[0].GetProperty(queryProperty).GetString() &&
                    actual[0].GetProperty("datasource").GetProperty("type").GetString() == (viewer ? Infinity : "influxdb"), "production_query_mismatch");
                for (var company = 0; company < (viewer || kind == "vehicle-trip" ? 1 : 10); company++)
                {
                    var variables = Variables(o, company, DateTimeOffset.UtcNow.AddSeconds(1));
                    if (kind == "latest-vehicle-position")
                        variables["limit"] = "500";
                    var query = await Query(variables);
                    var rows = Rows(query, false);
                    if (kind == "company-ingestion-health")
                    {
                        long sum = 0;
                        foreach (var row in rows)
                        {
                            ProofHttp.Require(row["empresaId"].GetString() == Final100kGrafanaProofScenario.Company(o.Namespace, company), "health_scope_mismatch");
                            sum += row[viewer ? "accepted" : "storedPoints"].GetInt64();
                            if (viewer)
                                ProofHttp.Require(row["rejected"].GetInt64() == 0, "health_rejected_points");
                        }
                        ProofHttp.Require(sum == 10000, "health_count_mismatch");
                        if (!viewer)
                            internalTotal += sum;
                    }
                    else
                        VerifyPositions(rows, o, company, kind == "vehicle-trip");
                    await wire.Save($"{audience}-{kind}-{company:D2}.json", query, ct);
                    if (viewer && kind == "latest-vehicle-position")
                    {
                        var forbidden = Variables(o, 1, DateTimeOffset.UtcNow);
                        forbidden["limit"] = "500";
                        var denied = await Query(forbidden);
                        Rows(denied, true);
                        await wire.Save("viewer-unauthorized.json", denied, ct);
                    }
                    if (company != 0)
                        continue;
                    var args = string.Join("&", variables.Select(v => Uri.EscapeDataString(v.Key is "from" or "to" ? v.Key : "var-" + v.Key) + "=" + Uri.EscapeDataString(v.Value)));
                    using var image = await wire.Send(o.Grafana, $"render/d-solo/{uid}/_?panelId={panel.GetProperty("id").GetInt32()}&width=1000&height=600&{args}", credential, ct, org: org);
                    var png = await ProofHttp.Bytes(image, ct);
                    ProofHttp.Require(image.Content.Headers.ContentType?.MediaType == "image/png" && png.Length > 1000 &&
                        png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "render_not_png");
                    await File.WriteAllBytesAsync(Path.Combine(o.ReportDirectory, audience + "-" + kind + ".png"), png, ct);
                }

                async Task<JsonElement> Query(Dictionary<string, string> variables)
                {
                    using var body = new StringContent(JsonSerializer.Serialize(new
                    {
                        queries = Resolve(actual, variables),
                        from = variables["from"],
                        to = variables["to"]
                    }), Encoding.UTF8, "application/json");
                    using var queryResponse = await wire.Send(o.Grafana, "api/ds/query", credential, ct, body, org);
                    return await ProofHttp.Json(queryResponse, ct);
                }
            }
        ProofHttp.Require(internalTotal == 100000, "grafana_total_mismatch");
        await wire.Save("grafana-totals.json", new
        {
            InternalStoredPoints = internalTotal,
            ViewerCompanyAccepted = 10000,
            started,
            Completed = DateTimeOffset.UtcNow,
            Note = "Internal event-time versus viewer processing-time; synthetic query checks accompany PNGs, not pixel interpretation."
        }, ct);
    }

    internal static Dictionary<string, string> Variables(FinalProofOptions o, int company, DateTimeOffset to) => new()
    {
        ["empresaId"] = Final100kGrafanaProofScenario.Company(o.Namespace, company),
        ["vehicleId"] = Final100kGrafanaProofScenario.TripVehicle,
        ["from"] = o.EventStart.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
        ["to"] = to.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
        ["limit"] = "10000",
        ["granularitySeconds"] = "60"
    };

    internal static JsonElement Resolve(JsonElement model, Dictionary<string, string> variables)
    {
        var node = JsonNode.Parse(model.GetRawText())!;
        Visit(node);
        return JsonSerializer.SerializeToElement(node);
        void Visit(JsonNode current)
        {
            if (current is JsonObject obj)
                foreach (var key in obj.Select(p => p.Key).ToArray())
                {
                    if (obj[key] is JsonValue v && v.TryGetValue<string>(out var text))
                        obj[key] = Replace(text);
                    else if (obj[key] is JsonNode child)
                        Visit(child);
                }
            else if (current is JsonArray array)
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue v && v.TryGetValue<string>(out var text))
                        array[i] = Replace(text);
                    else if (array[i] is JsonNode child)
                        Visit(child);
                }
        }
        string Replace(string text)
        {
            foreach (var bound in new[] { "from", "to" })
                text = text.Replace("${__" + bound + ":date:iso}", DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(variables[bound], CultureInfo.InvariantCulture)).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    .Replace("${__" + bound + "}", variables[bound], StringComparison.Ordinal);
            foreach (var (key, value) in variables)
                text = text.Replace("${" + key + ":percentencode}", Uri.EscapeDataString(value), StringComparison.Ordinal)
                    .Replace("${" + key + ":sqlstring}", "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'", StringComparison.Ordinal)
                    .Replace("${" + key + "}", value, StringComparison.Ordinal);
            ProofHttp.Require(!text.Contains("${", StringComparison.Ordinal), "unresolved_dashboard_variable");
            return text;
        }
    }

    internal static List<Dictionary<string, JsonElement>> Rows(JsonElement query, bool denied)
    {
        var results = query.GetProperty("results");
        ProofHttp.Require(results.EnumerateObject().Count() == 1, "unexpected_query_results");
        var result = results.GetProperty("A");
        if (result.TryGetProperty("error", out var error))
            ProofHttp.Require(denied || error.ValueKind == JsonValueKind.Null, "grafana_query_error");
        var rows = new List<Dictionary<string, JsonElement>>();
        var observed404 = false;
        foreach (var frame in result.GetProperty("frames").EnumerateArray())
        {
            if (frame.GetProperty("schema").TryGetProperty("meta", out var meta) && meta.TryGetProperty("custom", out var custom) && custom.TryGetProperty("responseCodeFromServer", out var status))
            {
                if (denied)
                    observed404 |= status.GetInt32() == 404;
                else
                    ProofHttp.Require(status.GetInt32() == 200, "grafana_upstream_error");
            }
            var fields = frame.GetProperty("schema").GetProperty("fields").EnumerateArray().Select(x => x.GetProperty("name").GetString()!).ToArray();
            var columns = frame.GetProperty("data").GetProperty("values");
            ProofHttp.Require(fields.Length == columns.GetArrayLength() && fields.Distinct(StringComparer.Ordinal).Count() == fields.Length, "invalid_frame_schema");
            var count = fields.Length == 0 ? 0 : columns[0].GetArrayLength();
            ProofHttp.Require(columns.EnumerateArray().All(c => c.GetArrayLength() == count), "invalid_frame_columns");
            for (var i = 0; i < count; i++)
                rows.Add(fields.Select((name, column) => (name, value: columns[column][i])).ToDictionary(x => x.name, x => x.value));
        }
        ProofHttp.Require(denied ? observed404 && rows.Count == 0 : rows.Count > 0, denied ? "viewer_scope_leak" : "empty_query_frames");
        return rows;
    }

    private static void VerifyPositions(List<Dictionary<string, JsonElement>> rows, FinalProofOptions o, int company, bool trip)
    {
        ProofHttp.Require(rows.Count == 100, "position_count_mismatch");
        var seen = new HashSet<string>();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var vehicleName = row["vehicleId"].GetString()!;
            var vehicle = vehicleName == Final100kGrafanaProofScenario.TripVehicle ? 0 : int.Parse(vehicleName.AsSpan(1), CultureInfo.InvariantCulture);
            var expected = Final100kGrafanaProofScenario.Event(o.Namespace, o.EventStart, vehicle, trip ? index : 99);
            var time = row["eventTime"].ValueKind == JsonValueKind.Number ? DateTimeOffset.FromUnixTimeMilliseconds(row["eventTime"].GetInt64()) : row["eventTime"].GetDateTimeOffset();
            var ignition = row["ignition"].ValueKind is JsonValueKind.True or JsonValueKind.False ? row["ignition"].GetBoolean() : bool.Parse(row["ignition"].GetString()!);
            ProofHttp.Require(vehicle is >= 0 and < 1000 && vehicle % 10 == company && (trip ? vehicle == 0 : seen.Add(vehicleName)) &&
                vehicleName == expected.Vehicle && row["empresaId"].GetString() == expected.Company && time == expected.Time &&
                Math.Abs(row["latitude"].GetDouble() - expected.Latitude) < 1e-9 && Math.Abs(row["longitude"].GetDouble() - expected.Longitude) < 1e-9 &&
                row["speed"].GetDouble() == expected.Speed && ignition == expected.Ignition, "position_or_trip_mismatch");
        }
    }

    internal static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CentralMobilidade.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository templates required.");
    }
}
