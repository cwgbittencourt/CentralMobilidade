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
        if (args.Length != 4 || args[0] != "--scenario" || args[1] is not ("steady-5000-eps" or "reconnect-burst") || args[2] != "--config")
        {
            Console.Error.WriteLine("Usage: --scenario <steady-5000-eps|reconnect-burst> --config <private-json-path>");
            return 2;
        }
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        try
        {
            var options = JsonSerializer.Deserialize<SteadyStateOptions>(await File.ReadAllTextAsync(args[3], stop.Token))
                ?? throw new ArgumentException();
            using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, MaxConnectionsPerServer = 32 };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            var token = Environment.GetEnvironmentVariable("TELEMETRY_LOAD_PRODUCER_TOKEN") ?? "";
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
