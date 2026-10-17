using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CentralMobilidade.Telemetry.LoadTests.Scenarios;

public sealed record ReconnectionBurstEvidence(int Vehicles, int SamplesPerVehicle, int BurstSeconds,
    int RecoverySeconds, long ScheduledEvents, long AcknowledgedEvents, long DrainedEvents,
    double? ObservedDrainSecondsAfterBurst);

internal sealed record BurstProfile(int Vehicles, int SamplesPerVehicle, int BurstSeconds, int RecoverySeconds, bool Reference)
{
    internal static readonly BurstProfile Target = new(10000, 180, 600, 1200, true);
    internal long Events => (long)Vehicles * SamplesPerVehicle;
    internal int EventsInBatch(int batch)
        => batch >= BurstSeconds * 10 ? 0 : checked((int)(Events * (batch + 1) / (BurstSeconds * 10) - Events * batch / (BurstSeconds * 10)));
    internal long FirstOrdinal(int batch) => Events * batch / (BurstSeconds * 10);
}

/// <summary>1.8M delayed events over 600s, on top of 5,000 normal events/s through the 1,200s recovery window.</summary>
public static class ReconnectionBurstScenario
{
    public static Task<SteadyStateReport> RunAsync(SteadyStateOptions options, HttpClient http, string producerToken, CancellationToken token = default)
    {
        if (options.DurationSeconds != 600)
            throw new ArgumentException("The reference burst lasts exactly 600 seconds.");
        return RunAsync(options, http, producerToken, BurstProfile.Target, token);
    }

    internal static Task<SteadyStateReport> RunAsync(SteadyStateOptions options, HttpClient http, string producerToken, BurstProfile profile, CancellationToken token = default)
        => SteadyStateScenario.RunCoreAsync(options with
        {
            DurationSeconds = profile.BurstSeconds + profile.RecoverySeconds
        }, http, producerToken, profile, token);

    internal static (byte[] Gzip, long RawBytes) CreateBatch(string run, int batch, DateTimeOffset start, BurstProfile profile)
    {
        // Normal and offline vehicles are disjoint; neither namespace overlaps another run.
        var normal = SteadyStateScenario.CreateBatch(run, batch, start);
        using var raw = new MemoryStream();
        using (var gzip = new GZipStream(new MemoryStream(normal.Gzip), CompressionMode.Decompress))
            gzip.CopyTo(raw);
        using (var writer = new StreamWriter(raw, new UTF8Encoding(false), leaveOpen: true))
        {
            for (var point = 0; point < profile.EventsInBatch(batch); point++)
            {
                var ordinal = profile.FirstOrdinal(batch) + point;
                var vehicle = ordinal % profile.Vehicles;
                var sample = ordinal / profile.Vehicles;
                // Target covers [-3600s,-20s], oldest first, exactly 180 samples per vehicle.
                var timestamp = start.ToUnixTimeMilliseconds() - profile.SamplesPerVehicle * 20000L + sample * 20000;
                writer.Write(FormattableString.Invariant($"telemetria,empresa_id=load-{run}-{vehicle % 100},veiculo_id=offline-v{vehicle} latitude=1,longitude=2,velocidade=3,rpm=4i,ignicao=true {timestamp}\n"));
            }
        }
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true))
            gzip.Write(raw.GetBuffer().AsSpan(0, checked((int)raw.Length)));
        return (output.ToArray(), raw.Length);
    }
}
