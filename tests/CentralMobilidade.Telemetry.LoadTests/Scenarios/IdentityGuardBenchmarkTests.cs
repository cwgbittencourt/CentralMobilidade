using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.LoadTests.Generators;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace CentralMobilidade.Telemetry.LoadTests.Scenarios;

/// <summary>Real durable PostgreSQL comparison; batch latency, not invented per-event latency.</summary>
public sealed class IdentityGuardBenchmarkTests
{
    private const int BatchSize = 1000;
    [Fact]
    public async Task Compare_durable_identity_indexes_and_separate_evidence_at_target_rate()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17.11-bookworm")
            .WithCommand("-c", "fsync=on", "-c", "synchronous_commit=on").Build();
        await postgres.StartAsync();
        await using var source = NpgsqlDataSource.Create(postgres.GetConnectionString());
        await using (var settings = source.CreateCommand("SELECT current_setting('fsync') || '/' || current_setting('synchronous_commit')"))
            (await settings.ExecuteScalarAsync()).ShouldBe("on/on");
        var results = new List<object>();
        foreach (var candidate in new[] { "plain", "hash16", "range16" })
        {
            await Setup(source, candidate);
            await Correctness(source, candidate);
            var guard = await Measure(source, candidate, false);
            var evidence = await Measure(source, candidate, true);
            results.Add(new
            {
                candidate,
                guard,
                evidence
            });
        }
        var directory = Path.Combine(Root(), "artifacts", "validation", "T041", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "benchmark.json"), JsonSerializer.Serialize(new
        {
            version = 1,
            utc = DateTimeOffset.UtcNow,
            postgres = "17.11-bookworm",
            durable = true,
            processors = Environment.ProcessorCount,
            os = Environment.OSVersion.ToString(),
            batchSize = BatchSize,
            targetEventsPerSecond = 5000,
            measuredEvents = 50000,
            results
        }, new JsonSerializerOptions { WriteIndented = true }));
        results.Any(result => JsonSerializer.SerializeToElement(result).GetProperty("guard").GetProperty("targetMet").GetBoolean())
            .ShouldBeTrue("At least one guard candidate must meet the measured throughput gate");
    }

    private static async Task Setup(NpgsqlDataSource source, string name)
    {
        var partition = name == "hash16" ? " PARTITION BY HASH(vehicle)" : name == "range16" ? " PARTITION BY RANGE(event_ms)" : "";
        await Execute(source, $"""
            CREATE TABLE {name} (company text COLLATE "C" NOT NULL,vehicle text COLLATE "C" NOT NULL,event_ms bigint NOT NULL,
                owner uuid NOT NULL,fence bigint NOT NULL DEFAULT 1,state smallint NOT NULL DEFAULT 0,lease_ms bigint,
                fingerprint bytea NOT NULL,expires_ms bigint NOT NULL,PRIMARY KEY(company,vehicle,event_ms)){partition};
            CREATE INDEX ON {name}(expires_ms);
            CREATE INDEX ON {name}(lease_ms);
            CREATE TABLE {name}_evidence(company text COLLATE "C" NOT NULL,vehicle text COLLATE "C" NOT NULL,event_ms bigint NOT NULL,
                fingerprint bytea NOT NULL,expires_ms bigint NOT NULL,PRIMARY KEY(company,vehicle,event_ms)){partition};
            CREATE INDEX ON {name}_evidence(expires_ms);
            """);
        if (name == "plain")
            return;
        for (var i = 0; i < 16; i++)
        {
            var rule = name == "hash16" ? $"FOR VALUES WITH (MODULUS 16, REMAINDER {i})" :
                $"FOR VALUES FROM ({DedupCorpusGenerator.StartUnixMilliseconds + i * 20000L}) TO ({DedupCorpusGenerator.StartUnixMilliseconds + (i + 1) * 20000L})";
            await Execute(source, $"CREATE TABLE {name}_{i} PARTITION OF {name} {rule}; CREATE TABLE {name}_evidence_{i} PARTITION OF {name}_evidence {rule};");
        }
    }

    private static async Task Correctness(NpgsqlDataSource source, string name)
    {
        var original = SHA256.HashData(Encoding.UTF8.GetBytes("original"));
        var conflict = SHA256.HashData(Encoding.UTF8.GetBytes("conflict"));
        var owner = Guid.NewGuid();
        var winners = await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ =>
        {
            await using var command = source.CreateCommand($"INSERT INTO {name} VALUES('c','race',@time,@owner,1,0,100,@fp,9999999999999) ON CONFLICT DO NOTHING");
            command.Parameters.AddWithValue("time", DedupCorpusGenerator.StartUnixMilliseconds);
            command.Parameters.AddWithValue("owner", owner);
            command.Parameters.AddWithValue("fp", original);
            return await command.ExecuteNonQueryAsync();
        }));
        winners.Sum().ShouldBe(1);
        async Task<int> Transition(Guid actor, long fence, long now, byte[] fingerprint, bool takeover)
        {
            await using var command = source.CreateCommand($"""
                UPDATE {name} SET owner=@owner,fence=fence+@increment,lease_ms=@now+100
                WHERE vehicle='race' AND state<>2 AND fingerprint=@fp AND fence=@fence
                AND ((owner=@owner) OR (lease_ms<=@now AND @takeover))
                """);
            command.Parameters.AddWithValue("owner", actor);
            command.Parameters.AddWithValue("increment", takeover ? 1 : 0);
            command.Parameters.AddWithValue("now", now);
            command.Parameters.AddWithValue("fp", fingerprint);
            command.Parameters.AddWithValue("fence", fence);
            command.Parameters.AddWithValue("takeover", takeover);
            return await command.ExecuteNonQueryAsync();
        }
        (await Transition(owner, 1, 20, original, false)).ShouldBe(1); // Resume unchanged fence.
        var successor = Guid.NewGuid();
        (await Transition(successor, 1, 30, original, true)).ShouldBe(0); // Lease still valid.
        (await Transition(successor, 1, 121, conflict, true)).ShouldBe(0); // Never replace original.
        var takeovers = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Transition(successor, 1, 121, original, true)));
        takeovers.Sum().ShouldBe(1);
        (await Transition(owner, 1, 122, original, false)).ShouldBe(0); // Stale owner/fence.
        await using var commit = source.CreateCommand($"UPDATE {name} SET state=2,lease_ms=NULL WHERE vehicle='race' AND owner=@owner AND fence=2 AND lease_ms>122");
        commit.Parameters.AddWithValue("owner", successor);
        (await commit.ExecuteNonQueryAsync()).ShouldBe(1);
        (await Transition(successor, 2, 123, original, false)).ShouldBe(0); // Committed is immutable.
        var conflicts = await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ =>
        {
            await using var command = source.CreateCommand($"INSERT INTO {name} VALUES('c','race',@time,@owner,1,0,100,@fp,9999999999999) ON CONFLICT DO NOTHING");
            command.Parameters.AddWithValue("time", DedupCorpusGenerator.StartUnixMilliseconds);
            command.Parameters.AddWithValue("owner", Guid.NewGuid());
            command.Parameters.AddWithValue("fp", conflict);
            return await command.ExecuteNonQueryAsync();
        }));
        conflicts.Sum().ShouldBe(0);
        await using var read = source.CreateCommand($"SELECT fingerprint FROM {name} WHERE vehicle='race'");
        ((byte[])(await read.ExecuteScalarAsync())!).ShouldBe(original);
        await Execute(source, $"DELETE FROM {name} WHERE vehicle='race'");
    }

    private static async Task<object> Measure(NpgsqlDataSource source, string name, bool evidence)
    {
        var table = evidence ? name + "_evidence" : name;
        var rows = DedupCorpusGenerator.Generate(new(100000, 1)).Where(row => row.ExpectedOutcome == DedupOutcome.Original).Take(50000).ToArray();
        var latency = new List<double>();
        var total = Stopwatch.StartNew();
        for (var offset = 0; offset < rows.Length; offset += BatchSize)
        {
            var batch = rows.Skip(offset).Take(BatchSize).ToArray();
            var delay = TimeSpan.FromSeconds(offset / 5000d) - total.Elapsed;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay);
            var watch = Stopwatch.StartNew();
            await using var connection = await source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var select = evidence ? "company,vehicle,event_ms,fp,event_ms+2592000000" : "company,vehicle,event_ms,@owner,1,0,event_ms+30000,fp,event_ms+5184000000";
            await using var insert = new NpgsqlCommand($"""
                INSERT INTO {table} SELECT {select} FROM unnest(@companies::text[],@vehicles::text[],@times::bigint[],@fingerprints::bytea[]) AS x(company,vehicle,event_ms,fp)
                ON CONFLICT DO NOTHING
                """, connection, transaction);
            insert.Parameters.AddWithValue("companies", batch.Select(row => row.CompanyId).ToArray());
            insert.Parameters.AddWithValue("vehicles", batch.Select(row => row.VehicleId).ToArray());
            insert.Parameters.AddWithValue("times", batch.Select(row => row.UnixMilliseconds).ToArray());
            insert.Parameters.AddWithValue("fingerprints", batch.Select(row => SHA256.HashData(Encoding.UTF8.GetBytes(row.OriginalLine))).ToArray());
            if (!evidence)
                insert.Parameters.AddWithValue("owner", Guid.NewGuid());
            (await insert.ExecuteNonQueryAsync()).ShouldBe(BatchSize);
            if (!evidence)
            {
                await using var transition = new NpgsqlCommand($"""
                    UPDATE {name} AS g SET state=1 FROM unnest(@companies::text[],@vehicles::text[],@times::bigint[]) AS x(company,vehicle,event_ms)
                    WHERE (g.company,g.vehicle,g.event_ms)=(x.company,x.vehicle,x.event_ms) AND g.state=0;
                    UPDATE {name} AS g SET state=2,lease_ms=NULL FROM unnest(@companies::text[],@vehicles::text[],@times::bigint[]) AS x(company,vehicle,event_ms)
                    WHERE (g.company,g.vehicle,g.event_ms)=(x.company,x.vehicle,x.event_ms) AND g.state=1
                    """, connection, transaction);
                transition.Parameters.AddWithValue("companies", batch.Select(row => row.CompanyId).ToArray());
                transition.Parameters.AddWithValue("vehicles", batch.Select(row => row.VehicleId).ToArray());
                transition.Parameters.AddWithValue("times", batch.Select(row => row.UnixMilliseconds).ToArray());
                (await transition.ExecuteNonQueryAsync()).ShouldBe(BatchSize * 2);
            }
            await transaction.CommitAsync();
            latency.Add(watch.Elapsed.TotalMilliseconds);
        }
        total.Stop();
        await using var size = source.CreateCommand(name == "plain" ? "SELECT pg_total_relation_size(@table::regclass)" :
            "SELECT coalesce(sum(pg_total_relation_size(relid)),0)::bigint FROM pg_partition_tree(@table::regclass) WHERE isleaf");
        size.Parameters.AddWithValue("table", table);
        var allocated = (long)(await size.ExecuteScalarAsync())!;
        var perRow = allocated / (double)rows.Length;
        latency.Sort();
        var rate = rows.Length / total.Elapsed.TotalSeconds;
        return new
        {
            events = rows.Length,
            seconds = total.Elapsed.TotalSeconds,
            achievedEventsPerSecond = rate,
            targetMet = rate >= 5000,
            batchP95Ms = latency[(int)(latency.Count * .95)],
            batchP99Ms = latency[(int)(latency.Count * .99)],
            allocatedBytes = allocated,
            bytesPerRow = perRow,
            projected30DayBytes = perRow * 5000 * 86400 * 30,
            projected60DayBytes = perRow * 5000 * 86400 * 60,
            projectedFiveYearBytes = perRow * 5000 * 86400 * 365 * 5
        };
    }

    private static async Task Execute(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CentralMobilidade.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Benchmark requires solution checkout.");
    }
}
