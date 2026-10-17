using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Deployment;

public sealed class BackupRestoreTests(ComposeRecoveryFixture fixture) : IClassFixture<ComposeRecoveryFixture>
{
    [Fact]
    public async Task Documented_cold_archives_restore_new_volumes_and_reject_corruption_or_nonempty_targets()
    {
        var guide = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "docs/runbooks/backup-restore.md"));
        var backupBody = Body("backup");
        var restoreBody = Body("restore");
        var directory = Path.Combine(fixture.DirectoryPath, "t087-backup");
        Directory.CreateDirectory(directory);
        await using var source = fixture.Project();
        await using var target = fixture.Project();
        await source.UpAsync();
        await source.AssertHealthyAsync();
        var marker = Guid.NewGuid().ToString("N");
        await source.ComposeOkAsync("exec", "-T", "postgres", "psql", "-U", "telemetry", "-d", "telemetry_control", "-v", "ON_ERROR_STOP=1", "-c",
            $"CREATE TABLE t087_proof(value text PRIMARY KEY); INSERT INTO t087_proof VALUES ('{marker}');");
        var points = (await source.ComposeOkAsync("exec", "-T", "influxdb", "sh", "/validation/influx-proof.sh", "seed")).Output.Trim();
        points.ShouldContain("t078-a");
        points.ShouldContain("t078-b");
        await source.PublishProofAsync(marker);
        await source.VolumeMarkersAsync(marker, write: true);
        var node = (await source.ComposeOkAsync("exec", "-T", "rabbitmq", "rabbitmqctl", "eval", "node().")).Output.Trim();
        node.ShouldContain("rabbit@rabbitmq");
        var definitions = (await source.ComposeOkAsync("exec", "-T", "rabbitmq", "rabbitmqctl", "-q", "export_definitions", "-")).Output;
        using (var json = JsonDocument.Parse(definitions))
            json.RootElement.GetProperty("queues").GetArrayLength().ShouldBeGreaterThan(0);
        await File.WriteAllTextAsync(Path.Combine(directory, "definitions.json"), definitions);
        await source.ComposeOkAsync("--profile", "*", "stop", "--timeout", "60");
        foreach (var row in await source.RowsAsync())
        {
            row.GetProperty("State").GetString().ShouldBe("exited");
            row.GetProperty("ExitCode").GetInt32().ShouldBe(0, row.GetProperty("Service").GetString());
        }
        var hashes = new Dictionary<string, string>();
        foreach (var key in ComposeRecoveryFixture.Volumes)
        {
            await AssertOfflineVolume(source.Name + "_" + key);
            (await Helper(source.Name + "_" + key, key, backupBody, restore: false)).ExitCode.ShouldBe(0);
            hashes[key] = await Hash(Path.Combine(directory, key + ".tar"));
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new
        {
            schema = 1,
            sourceProject = source.Name,
            rabbitNode = node,
            influxNode = "queryable",
            postgresMajor = 17,
            sha256 = hashes
        }));

        // Verify the trusted manifest before allocating destination volumes.
        var archive = Path.Combine(directory, "postgres_data.tar");
        var corrupt = Path.Combine(directory, "corrupt.tar");
        File.Copy(archive, corrupt);
        await using (var stream = new FileStream(corrupt, FileMode.Append, FileAccess.Write))
            await stream.WriteAsync(new byte[] { 255 });
        await Should.ThrowAsync<InvalidDataException>(() => RequireHash(corrupt, hashes["postgres_data"]));
        (await fixture.DockerAsync("volume", "inspect", target.Name + "_postgres_data")).ExitCode.ShouldNotBe(0);
        foreach (var key in ComposeRecoveryFixture.Volumes)
            await RequireHash(Path.Combine(directory, key + ".tar"), hashes[key]);
        foreach (var key in ComposeRecoveryFixture.Volumes)
        {
            var name = target.Name + "_" + key;
            (await fixture.DockerAsync("volume", "inspect", name)).ExitCode.ShouldNotBe(0);
            (await fixture.DockerAsync("volume", "create", "--label", "com.docker.compose.project=" + target.Name,
                "--label", "com.docker.compose.volume=" + key, name)).ExitCode.ShouldBe(0);
            (await Helper(name, key, restoreBody, restore: true)).ExitCode.ShouldBe(0);
        }
        // A retry must not overlay data, even if its tar/hash is valid.
        (await Helper(target.Name + "_postgres_data", "postgres_data", restoreBody, restore: true)).ExitCode.ShouldNotBe(0);
        await RequireHash(archive, hashes["postgres_data"]);
        (await fixture.DockerAsync("run", "--rm", "--network", "none", "--mount",
            $"type=volume,source={target.Name}_quarantine_data,target=/data,readonly", "alpine:3.23.4", "sh", "-ec",
            "test \"$(stat -c %u:%g:%a /data/rejections)\" = 1654:1654:700")).ExitCode.ShouldBe(0);
        await target.UpAsync();
        await target.AssertHealthyAsync();
        (await target.ComposeOkAsync("exec", "-T", "postgres", "psql", "-U", "telemetry", "-d", "telemetry_control", "-Atc", "SELECT value FROM t087_proof")).Output.Trim().ShouldBe(marker);
        (await target.ComposeOkAsync("exec", "-T", "influxdb", "sh", "/validation/influx-proof.sh", "verify")).Output.Trim().ShouldBe(points);
        (await target.ComposeOkAsync("exec", "-T", "rabbitmq", "rabbitmqctl", "eval", "node().")).Output.Trim().ShouldBe(node);
        await target.VerifyProofMessageAsync(marker);
        await target.VolumeMarkersAsync(marker, write: false);
        await source.VolumeMarkersAsync(marker, write: false);
        await source.UpAsync();
        await source.AssertHealthyAsync();
        await source.VerifyProofMessageAsync(marker); // Restoring never consumed the source's message.

        string Body(string operation)
        {
            var match = Regex.Match(guide, @"<!-- t087-" + operation + @" -->\s*```sh\r?\n([\s\S]*?)```");
            match.Success.ShouldBeTrue("Run the exact shell body from the operator runbook.");
            return match.Groups[1].Value.Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        async Task AssertOfflineVolume(string volume)
        {
            (await fixture.DockerAsync("volume", "inspect", volume)).ExitCode.ShouldBe(0);
            var ids = (await fixture.DockerAsync("ps", "-aq", "--filter", "volume=" + volume)).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var id in ids)
                (await fixture.DockerAsync("inspect", "--format", "{{.State.Running}}", id)).Output.Trim().ShouldBe("false");
        }
        Task<ComposeRecoveryFixture.CommandResult> Helper(string volume, string key, string script, bool restore)
        {
            var arguments = new List<string> { "run", "--rm", "--network", "none", "--read-only", "--user", "0:0", "--cap-drop", "ALL", "--cap-add", "DAC_OVERRIDE", "--security-opt", "no-new-privileges:true" };
            if (restore)
                arguments.AddRange(["--cap-add", "CHOWN", "--cap-add", "FOWNER"]);
            arguments.AddRange(["--mount", $"type=volume,source={volume},target=/data" + (restore ? "" : ",readonly"),
                "--mount", $"type=bind,source={directory},target=/backup" + (restore ? ",readonly" : ""),
                "alpine:3.23.4", "sh", "-ec", script, "t087", key]);
            return fixture.DockerAsync(arguments.ToArray());
        }
    }

    private static async Task<string> Hash(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }
    private static async Task RequireHash(string path, string expected)
    {
        if (!string.Equals(await Hash(path), expected, StringComparison.Ordinal))
            throw new InvalidDataException("Backup checksum differs from the trusted manifest.");
    }
}
