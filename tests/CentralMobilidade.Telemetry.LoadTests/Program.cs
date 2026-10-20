using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.LoadTests.Scenarios;

namespace CentralMobilidade.Telemetry.LoadTests;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 4 || args[0] != "--scenario" || args[1] is not ("steady-5000-eps" or "reconnect-burst" or "influx-outage" or "final-100k-grafana") || args[2] != "--config")
        {
            Console.Error.WriteLine("Usage: --scenario <steady-5000-eps|reconnect-burst|influx-outage|final-100k-grafana> --config <private-json-path>");
            return 2;
        }
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        try
        {
            var config = await File.ReadAllTextAsync(args[3], stop.Token);
            using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, MaxConnectionsPerServer = 32 };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            var token = Environment.GetEnvironmentVariable("TELEMETRY_LOAD_PRODUCER_TOKEN") ?? "";
            if (args[1] == "final-100k-grafana")
            {
                var finalOptions = JsonSerializer.Deserialize<FinalProofOptions>(config) ?? throw new ArgumentException();
                var secrets = new FinalProofSecrets(token, Environment.GetEnvironmentVariable("TELEMETRY_PROOF_INFLUX_TOKEN") ?? "",
                    Environment.GetEnvironmentVariable("TELEMETRY_PROOF_INTERNAL_GRAFANA_TOKEN") ?? "",
                    Environment.GetEnvironmentVariable("TELEMETRY_PROOF_VIEWER_GRAFANA_TOKEN") ?? "");
                var result = await Final100kGrafanaProofScenario.RunAsync(finalOptions, http, secrets, stop.Token);
                Console.WriteLine(result.Passed ? "PASS: final acceptance scenario; inspect private evidence. Not a capacity approval." : "FAIL: final acceptance incomplete; inspect private evidence.");
                return result.Passed ? 0 : 1;
            }
            if (args[1] == "influx-outage")
            {
                var outage = JsonSerializer.Deserialize<InfluxOutageOptions>(config) ?? throw new ArgumentException();
                var result = await InfluxOutageScenario.RunAsync(outage, http, token, stop.Token,
                    Environment.GetEnvironmentVariable("TELEMETRY_LOAD_FAULT_TOKEN"));
                Console.WriteLine(result.Passed ? "PASS: outage scenario gates passed; inspect the private report." : "FAIL: inspect the private report and proxy restoration status.");
                return result.Passed ? 0 : 1;
            }
            var options = JsonSerializer.Deserialize<SteadyStateOptions>(config) ?? throw new ArgumentException();
            var report = args[1] == "reconnect-burst"
                ? await ReconnectionBurstScenario.RunAsync(options, http, token, stop.Token)
                : await SteadyStateScenario.RunAsync(options, http, token, stop.Token);
            Console.WriteLine(report.Passed ? "PASS: scenario gates passed; inspect the private report." : "FAIL: inspect the private report.");
            return report.Passed ? 0 : 1;
        }
        catch
        {
            // Never print provider responses, configuration or credentials.
            Console.Error.WriteLine("Scenario could not complete. Check private configuration, target and report; no capacity result is certified.");
            return 2;
        }
    }
}
