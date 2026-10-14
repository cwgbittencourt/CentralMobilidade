using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CentralMobilidade.Telemetry.LoadTests.Generators;

public enum DedupOutcome { Original, IdenticalReplay, Conflict }
public sealed record DedupCorpusOptions(int Vehicles, int SamplesPerVehicle = 3, int Companies = 10, ulong Seed = 40001);
public sealed record DedupCorpusRow(long Ordinal, string CompanyId, string VehicleId, long UnixMilliseconds,
    DedupOutcome ExpectedOutcome, string Line, string OriginalLine);
public sealed record DedupCorpusManifest(int Version, DedupCorpusOptions Options, long Originals,
    long IdenticalReplays, long Conflicts, long TotalLines, long UncompressedBytes);

/// <summary>Synthetic, stable across process/runtime/culture; emits one row at a time.</summary>
public static class DedupCorpusGenerator
{
    public const long StartUnixMilliseconds = 1791979200000;

    public static IEnumerable<DedupCorpusRow> Generate(DedupCorpusOptions options)
    {
        Validate(options);
        long ordinal = 0;
        // Round-major ordering represents fleet traffic with one sample per vehicle every 20 seconds.
        for (var sample = 0; sample < options.SamplesPerVehicle; sample++)
            for (var vehicle = 0; vehicle < options.Vehicles; vehicle++)
            {
                var key = (long)sample * options.Vehicles + vehicle;
                var company = "company-" + (vehicle % options.Companies).ToString("D3", CultureInfo.InvariantCulture);
                var id = "vehicle-" + vehicle.ToString("D6", CultureInfo.InvariantCulture);
                var timestamp = checked(StartUnixMilliseconds + sample * 20000L);
                var bits = Mix(unchecked(options.Seed + (ulong)key));
                var latitude = -23.0 - (bits % 100000) / 100000.0;
                var longitude = -46.0 - ((bits >> 17) % 100000) / 100000.0;
                var speed = ((bits >> 32) % 1300) / 10.0;
                var rpm = (bits >> 43) % 5000;
                var ignition = (bits & 1) == 0 ? "true" : "false";
                var driver = vehicle % 4 == 0 ? ",motorista_id=driver-" + vehicle.ToString("D6", CultureInfo.InvariantCulture) : "";
                var plate = vehicle % 5 == 0 ? ",placa=\"SYN" + vehicle.ToString("D6", CultureInfo.InvariantCulture) + "\"" : "";
                var tags = "telemetria,empresa_id=" + company + ",veiculo_id=" + id + driver;
                var original = FormattableString.Invariant($"{tags} latitude={latitude:R},longitude={longitude:R},velocidade={speed:R},rpm={rpm}i,ignicao={ignition}{plate} {timestamp}\n");
                yield return new(++ordinal, company, id, timestamp, DedupOutcome.Original, original, original);
                if (key % 10 == 0)
                {
                    // Changed ordering/spelling/CRLF, same semantic event: canonical replay.
                    var replay = FormattableString.Invariant($"telemetria,veiculo_id={id},empresa_id={company}{driver} ignicao={ignition},rpm={rpm}i,velocidade={speed:F1},longitude={longitude:R},latitude={latitude:R}{plate} {timestamp}\r\n");
                    yield return new(++ordinal, company, id, timestamp, DedupOutcome.IdenticalReplay, replay, original);
                }
                if (key % 100 == 0)
                {
                    var conflict = FormattableString.Invariant($"{tags} latitude={latitude:R},longitude={longitude:R},velocidade={speed + 1:R},rpm={rpm}i,ignicao={ignition}{plate} {timestamp}\n");
                    yield return new(++ordinal, company, id, timestamp, DedupOutcome.Conflict, conflict, original);
                }
            }
    }

    /// <summary>Writes gzip LP plus a bounded-memory JSONL oracle and final manifest. Caller owns directory.</summary>
    public static async Task<DedupCorpusManifest> ExportAsync(string directory, DedupCorpusOptions options, CancellationToken cancellationToken = default)
    {
        Validate(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        long originals = 0, replays = 0, conflicts = 0, bytes = 0;
        await using (var file = new FileStream(Path.Combine(directory, "corpus.lp.gz"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        await using (var oracle = new StreamWriter(new FileStream(Path.Combine(directory, "oracle.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.None), new UTF8Encoding(false)))
        {
            foreach (var row in Generate(options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var encoded = Encoding.UTF8.GetBytes(row.Line);
                await gzip.WriteAsync(encoded, cancellationToken);
                bytes += encoded.Length;
                if (row.ExpectedOutcome == DedupOutcome.Original) originals++;
                else if (row.ExpectedOutcome == DedupOutcome.IdenticalReplay) replays++;
                else conflicts++;
                await oracle.WriteLineAsync(JsonSerializer.Serialize(row).AsMemory(), cancellationToken);
            }
        }
        var manifest = new DedupCorpusManifest(1, options, originals, replays, conflicts, originals + replays + conflicts, bytes);
        await using var manifestFile = new FileStream(Path.Combine(directory, "manifest.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(manifestFile, manifest, cancellationToken: cancellationToken);
        return manifest;
    }

    private static void Validate(DedupCorpusOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Vehicles <= 0 || options.SamplesPerVehicle <= 0 || options.Companies <= 0 || options.Companies > options.Vehicles)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    private static ulong Mix(ulong value)
    {
        // SplitMix64, explicitly unchecked; independent of System.Random implementation changes.
        unchecked
        {
            value += 0x9e3779b97f4a7c15UL;
            value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
            value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
            return value ^ (value >> 31);
        }
    }
}
