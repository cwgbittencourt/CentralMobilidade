using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.LoadTests.Scenarios;

public sealed class Final100kGrafanaProofScenarioTests
{
    internal const string Run = "0123456789abcdef0123456789abcdef";
    internal static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeMilliseconds(1791686400000);

    [Fact]
    public void Corpus_has_exact_unique_cardinality_and_is_reproducible()
    {
        var events = Final100kGrafanaProofScenario.Events(Run, Start).ToArray();
        events.Length.ShouldBe(100000);
        events.Select(e => e.Company).Distinct().Count().ShouldBe(10);
        events.Select(e => e.Vehicle).Distinct().Count().ShouldBe(1000);
        events.Select(e => (e.Company, e.Vehicle, e.Time)).Distinct().Count().ShouldBe(100000);
        events.GroupBy(e => e.Company).ShouldAllBe(g => g.Count() == 10000);
        events.GroupBy(e => e.Vehicle).ShouldAllBe(g => g.Count() == 100);
        Final100kGrafanaProofScenario.Events(Run, Start).ShouldBe(events);
        Final100kGrafanaProofScenario.Events("1123456789abcdef0123456789abcdef", Start).First().Company.ShouldNotBe(events[0].Company);
    }

    [Fact]
    public void Named_trip_has_ordered_and_delayed_arrivals_with_original_event_time()
    {
        var trip = Final100kGrafanaProofScenario.Events(Run, Start).Where(e => e.Vehicle == "proof-trip").ToArray();
        trip.Length.ShouldBe(100);
        trip[0].Time.ShouldBe(Start.AddSeconds(1000));
        trip[49].Time.ShouldBe(Start.AddSeconds(1980));
        trip[50].Time.ShouldBe(Start);
        trip[99].Time.ShouldBe(Start.AddSeconds(980));
        trip.Take(50).Select(e => e.Time).ShouldBe(trip.Take(50).Select(e => e.Time).Order());
        trip.Skip(50).Select(e => e.Time).ShouldBe(trip.Skip(50).Select(e => e.Time).Order());
    }

    [Fact]
    public void Gzip_batches_are_bounded_deterministic_and_culture_independent()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
            long lines = 0;
            var identities = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            for (var batch = 0; batch < 200; batch++)
            {
                var gzip = Final100kGrafanaProofScenario.CreateBatch(Run, Start, batch);
                gzip.Length.ShouldBeLessThan(8 * 1024 * 1024);
                using var stream = new GZipStream(new MemoryStream(gzip), CompressionMode.Decompress);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var body = reader.ReadToEnd();
                var rows = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                rows.Length.ShouldBe(500);
                rows.ShouldAllBe(row => row.StartsWith("telemetria,empresa_id=fp-", StringComparison.Ordinal) && row.Contains("latitude=1", StringComparison.Ordinal));
                foreach (var row in rows)
                {
                    var parts = row.Split(' ');
                    parts.Length.ShouldBe(3);
                    var tags = parts[0].Split(',');
                    tags.Length.ShouldBe(3);
                    var fields = parts[1].Split(',');
                    fields.Length.ShouldBe(5);
                    var latitude = double.Parse(fields[0].Split('=')[1], CultureInfo.InvariantCulture);
                    latitude.ShouldBeInRange(1d, 1.099d);
                    long.Parse(parts[2], CultureInfo.InvariantCulture).ShouldBeInRange(Start.ToUnixTimeMilliseconds(), Start.AddSeconds(1980).ToUnixTimeMilliseconds());
                    identities.Add(parts[0] + " " + parts[2]).ShouldBeTrue();
                }
                lines += rows.Length;
                gzip.ShouldBe(Final100kGrafanaProofScenario.CreateBatch(Run, Start, batch));
            }
            lines.ShouldBe(100000);
            identities.Count.ShouldBe(100000);
        }
        finally { CultureInfo.CurrentCulture = previous; }
        Should.Throw<ArgumentOutOfRangeException>(() => Final100kGrafanaProofScenario.CreateBatch(Run, Start, 200));
    }
}
