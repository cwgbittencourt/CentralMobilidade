using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.LoadTests.Generators;

public sealed class DedupCorpusGeneratorTests
{
    [Theory]
    [InlineData(10000)]
    [InlineData(100000)]
    public async Task Export_profiles_have_expected_cardinality_and_recoverable_oracle(int vehicles)
    {
        var directory = Path.Combine(FindRoot(), "artifacts", "validation", "T040", vehicles + "-" + Guid.NewGuid().ToString("N"));
        var manifest = await DedupCorpusGenerator.ExportAsync(directory, new(vehicles));
        manifest.Originals.ShouldBe(vehicles * 3L);
        manifest.IdenticalReplays.ShouldBe(vehicles * 3L / 10);
        manifest.Conflicts.ShouldBe(vehicles * 3L / 100);
        var identities = new HashSet<(string, string, long)>();
        var fleet = new HashSet<string>();
        var companies = new HashSet<string>();
        using var compressed = File.OpenRead(Path.Combine(directory, "corpus.lp.gz"));
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var lines = new StreamReader(gzip, Encoding.UTF8);
        using var oracle = File.OpenText(Path.Combine(directory, "oracle.jsonl"));
        long total = 0;
        while (await oracle.ReadLineAsync() is { } json)
        {
            var row = JsonSerializer.Deserialize<DedupCorpusRow>(json).ShouldNotBeNull();
            row.Ordinal.ShouldBe(++total);
            (await lines.ReadLineAsync()).ShouldBe(row.Line.TrimEnd('\r', '\n'));
            var identity = (row.CompanyId, row.VehicleId, row.UnixMilliseconds);
            if (row.ExpectedOutcome == DedupOutcome.Original)
            {
                identities.Add(identity).ShouldBeTrue();
                row.Line.ShouldBe(row.OriginalLine);
            }
            else
            {
                identities.Contains(identity).ShouldBeTrue("The original precedes each retry/conflict");
                row.Line.ShouldNotBe(row.OriginalLine);
            }
            fleet.Add(row.VehicleId);
            companies.Add(row.CompanyId);
        }
        total.ShouldBe(manifest.TotalLines);
        identities.Count.ShouldBe(vehicles * 3);
        fleet.Count.ShouldBe(vehicles);
        companies.Count.ShouldBe(10);
        (await lines.ReadLineAsync()).ShouldBeNull();
    }

    [Fact]
    public void Seed_and_culture_reproduce_identical_line_bytes()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
            var first = Digest(new(1000));
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Digest(new(1000)).ShouldBe(first);
            Digest(new(1000, Seed: 40002)).ShouldNotBe(first);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Replay_preserves_fields_and_conflict_changes_only_speed()
    {
        var rows = DedupCorpusGenerator.Generate(new(100, 1)).Take(3).ToArray();
        rows.Select(row => row.ExpectedOutcome).ShouldBe(new[] { DedupOutcome.Original, DedupOutcome.IdenticalReplay, DedupOutcome.Conflict });
        var original = Fields(rows[0].Line);
        Fields(rows[1].Line).ShouldBe(original, ignoreOrder: true);
        var conflict = Fields(rows[2].Line);
        conflict["velocidade"].ShouldBe(original["velocidade"] + 1);
        foreach (var key in original.Keys.Where(key => key != "velocidade"))
            conflict[key].ShouldBe(original[key]);
    }

    private static Dictionary<string, double> Fields(string line) => line.Split(' ')[1].Split(',')
        .Where(field => !field.StartsWith("placa=", StringComparison.Ordinal))
        .Select(field => field.Split('='))
        .ToDictionary(pair => pair[0], pair => pair[1] is "true" ? 1d : pair[1] is "false" ? 0d : double.Parse(pair[1].TrimEnd('i'), CultureInfo.InvariantCulture));

    private static string Digest(DedupCorpusOptions options)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var row in DedupCorpusGenerator.Generate(options))
            hash.AppendData(Encoding.UTF8.GetBytes(row.Line));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CentralMobilidade.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Corpus export tests require the solution checkout.");
    }
}
