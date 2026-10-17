using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using RabbitMQ.Client;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Deployment;

[Trait("Category", "Integration")]
public sealed class ComposeRecoveryTests(ComposeRecoveryFixture fixture) : IClassFixture<ComposeRecoveryFixture>
{
    [Fact]
    public async Task Every_profile_has_explicit_runtime_limits_and_restricted_filesystems()
    {
        await using var project = fixture.Project();
        var model = await project.ModelAsync(development: true, monitoring: true);
        foreach (var (name, service) in model["services"]!.AsObject())
        {
            var job = name is "influxdb-init" or "quarantine-init";
            service!["restart"].ShouldNotBeNull(name).GetValue<string>().ShouldBe(job ? "no" : "unless-stopped", name);
            service["read_only"]!.GetValue<bool>().ShouldBeTrue(name);
            long.Parse(service["mem_limit"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture).ShouldBeGreaterThan(0, name);
            service["cpus"]!.GetValue<double>().ShouldBeGreaterThan(0, name);
            service["pids_limit"]!.GetValue<int>().ShouldBeGreaterThan(0, name);
            if (name == "quarantine-init")
                service["user"]!.GetValue<string>().ShouldBe("0:0"); // Bounded ownership initialization only.
            else
                service["user"]!.GetValue<string>().Split(':')[0].ShouldNotBeOneOf("0", "root", "");
            service["security_opt"]!.AsArray().ShouldContain(v => v!.GetValue<string>() == "no-new-privileges:true");
            service["cap_drop"]!.AsArray().ShouldContain(v => v!.GetValue<string>() == "ALL");
            if (!job)
                service["healthcheck"]!["test"]!.AsArray().ShouldNotBeEmpty(name);
        }
    }
    [Fact]
    public async Task Production_models_require_seven_named_volumes_health_probes_and_initializer_dependencies()
    {
        await using var project = fixture.Project();
        var model = await project.ModelAsync();
        var volumes = model["volumes"]!.AsObject();
        volumes.Select(v => v.Key).ShouldBe(ComposeRecoveryFixture.Volumes, ignoreOrder: true);
        foreach (var volume in volumes)
            volume.Value!["name"]!.GetValue<string>().ShouldBe(project.Name + "_" + volume.Key);
        foreach (var (service, volume, target) in ComposeRecoveryFixture.Mounts)
        {
            var mounts = model["services"]![service]!["volumes"]!.AsArray();
            var mount = mounts.Single(v => v!["target"]!.GetValue<string>() == target)!;
            mount["type"]!.GetValue<string>().ShouldBe("volume");
            mount["source"]!.GetValue<string>().ShouldBe(volume);
            (mount["read_only"]?.GetValue<bool>() ?? false).ShouldBeFalse();
        }
        foreach (var server in ComposeRecoveryFixture.Servers)
            model["services"]![server]!["healthcheck"]!["test"]!.AsArray().ShouldNotBeEmpty();
        model["services"]!["influxdb-init"]!["depends_on"]!["influxdb"]!["condition"]!.GetValue<string>().ShouldBe("service_healthy");
        model["services"]!["grafana"]!["depends_on"]!["quarantine-init"]!["condition"]!.GetValue<string>().ShouldBe("service_completed_successfully");
        model["services"]!["grafana"]!["depends_on"]!["influxdb-init"]!["condition"]!.GetValue<string>().ShouldBe("service_completed_successfully");
        var development = await project.ModelAsync(development: true);
        foreach (var service in new[] { "api", "worker" })
        {
            development["services"]![service]!["volumes"]!.AsArray().ShouldContain(v => v!["type"]!.GetValue<string>() == "volume" && v["source"]!.GetValue<string>() == "quarantine_data" && v["target"]!.GetValue<string>() == "/quarantine");
            development["services"]![service]!["depends_on"]!["postgres"]!["condition"]!.GetValue<string>().ShouldBe("service_healthy");
            development["services"]![service]!["depends_on"]!["rabbitmq"]!["condition"]!.GetValue<string>().ShouldBe("service_healthy");
        }
    }

    [Fact]
    public async Task Healthy_stack_preserves_database_message_points_and_all_volume_markers_after_restart_and_recreation()
    {
        await using var project = fixture.Project();
        await project.UpAsync();
        await project.AssertHealthyAsync();
        await project.AssertHardenedAsync(ComposeRecoveryFixture.Servers);
        var marker = Guid.NewGuid().ToString("N");
        await project.ComposeOkAsync("exec", "-T", "postgres", "psql", "-U", "telemetry", "-d", "telemetry_control", "-v", "ON_ERROR_STOP=1", "-c", $"CREATE TABLE t078_proof(value text PRIMARY KEY); INSERT INTO t078_proof VALUES ('{marker}');");
        await project.VolumeMarkersAsync(marker, write: true);
        var points = await project.ComposeOkAsync("exec", "-T", "influxdb", "sh", "/validation/influx-proof.sh", "seed");
        points.Output.ShouldContain("t078-a");
        points.Output.ShouldContain("t078-b");
        await project.PublishProofAsync(marker);
        await VerifyAsync();
        // In-container shutdown is not a Docker manual stop; Engine must restart the broker.
        await project.ComposeOkAsync("exec", "-T", "rabbitmq", "rabbitmqctl", "stop");
        await project.AwaitEngineRestartAsync("rabbitmq");
        await VerifyAsync();
        await project.ComposeOkAsync(new[] { "restart", "--timeout", "10" }.Concat(ComposeRecoveryFixture.Servers).ToArray());
        await project.UpAsync();
        await project.AssertHealthyAsync();
        await VerifyAsync();
        // Force replacement, including the idempotent one-shot initializers, without deleting volumes.
        await project.ComposeOkAsync("up", "-d", "--force-recreate", "--no-build", "--wait", "--wait-timeout", "240");
        await project.AssertHealthyAsync();
        await VerifyAsync();

        async Task VerifyAsync()
        {
            (await project.ComposeOkAsync("exec", "-T", "postgres", "psql", "-U", "telemetry", "-d", "telemetry_control", "-Atc", "SELECT value FROM t078_proof")).Output.Trim().ShouldBe(marker);
            (await project.ComposeOkAsync("exec", "-T", "influxdb", "sh", "/validation/influx-proof.sh", "verify")).Output.Trim().ShouldBe(points.Output.Trim());
            await project.VerifyProofMessageAsync(marker);
            await project.VolumeMarkersAsync(marker, write: false);
        }
    }

    [Fact]
    public async Task App_emulator_and_exporter_profiles_start_with_their_actual_restrictions()
    {
        await using var project = fixture.Project();
        await project.EnableProfilesAsync();
        await project.ComposeOkAsync("build", "api", "worker");
        await project.ComposeOkAsync("up", "-d", "--no-build", "--wait", "--wait-timeout", "240", "api", "worker", "pubsub-emulator", "postgres-exporter", "node-exporter");
        await project.AssertHardenedAsync(["api", "worker", "pubsub-emulator", "postgres-exporter", "node-exporter"]);
        foreach (var (service, port) in new[] { ("api", "8080"), ("worker", "9464") })
        {
            await project.ComposeOkAsync("exec", "-T", service, "/bin/busybox", "wget", "-q", "-O", "/dev/null", "http://127.0.0.1:" + port + "/health/live");
            await project.ComposeOkAsync("exec", "-T", service, "sh", "-ec", "test -w /quarantine/rejections; test \"$(stat -c %a /quarantine/rejections)\" = 700");
        }
    }

    [Fact]
    public async Task Explicitly_required_external_volume_missing_from_engine_fails_before_service_start()
    {
        await using var project = fixture.Project();
        var missing = project.Name + "_missing";
        (await fixture.DockerAsync("volume", "inspect", missing)).ExitCode.ShouldNotBe(0);
        await project.OverrideAsync(new JsonObject { ["volumes"] = new JsonObject { ["postgres_data"] = new JsonObject { ["external"] = true, ["name"] = missing } } });
        (await project.ComposeAsync("up", "-d", "--no-build", "--no-deps", "postgres")).ExitCode.ShouldNotBe(0);
        (await project.RowsAsync()).ShouldNotContain(r => r.GetProperty("Service").GetString() == "postgres" && r.GetProperty("State").GetString() == "running");
    }

    [Theory]
    [InlineData("postgres", "postgres_data", "/var/lib/postgresql/data")]
    [InlineData("influxdb", "influxdb_queryable_data", "/var/lib/influxdb3")]
    [InlineData("quarantine-init", "quarantine_data", "/quarantine")]
    public async Task Readonly_persistent_storage_cannot_produce_a_successful_start(string service, string volume, string target)
    {
        await using var project = fixture.Project();
        await project.OverrideAsync(Readonly(service, volume, target));
        (await project.ComposeAsync("up", "-d", "--no-build", "--no-deps", "--wait", "--wait-timeout", "30", service)).ExitCode.ShouldNotBe(0);
        (await project.RowsAsync()).ShouldNotContain(r => r.GetProperty("Service").GetString() == service && r.GetProperty("State").GetString() == "running" && r.GetProperty("Health").GetString() == "healthy");
    }

    [Fact]
    public async Task Failed_quarantine_initializer_blocks_its_Grafana_dependent()
    {
        await using var project = fixture.Project();
        await project.OverrideAsync(Readonly("quarantine-init", "quarantine_data", "/quarantine"));
        (await project.ComposeAsync("up", "-d", "--no-build", "--wait", "--wait-timeout", "60", "grafana")).ExitCode.ShouldNotBe(0);
        var rows = await project.RowsAsync();
        rows.ShouldContain(r => r.GetProperty("Service").GetString() == "quarantine-init" && r.GetProperty("State").GetString() == "exited" && r.GetProperty("ExitCode").GetInt32() != 0);
        rows.ShouldNotContain(r => r.GetProperty("Service").GetString() == "grafana" && r.GetProperty("State").GetString() == "running");
    }

    private static JsonObject Readonly(string service, string volume, string target) => new()
    {
        ["services"] = new JsonObject { [service] = new JsonObject { ["volumes"] = new JsonArray(new JsonObject { ["type"] = "volume", ["source"] = volume, ["target"] = target, ["read_only"] = true }) } }
    };
}

public sealed class ComposeRecoveryFixture : IAsyncLifetime
{
    internal static readonly string[] Volumes = ["postgres_data", "rabbitmq_data", "influxdb_queryable_data", "prometheus_data", "loki_data", "grafana_data", "quarantine_data"];
    internal static readonly string[] Servers = ["postgres", "rabbitmq", "influxdb", "prometheus", "loki", "grafana"];
    internal static readonly (string Service, string Volume, string Target)[] Mounts = [("postgres", "postgres_data", "/var/lib/postgresql/data"), ("rabbitmq", "rabbitmq_data", "/var/lib/rabbitmq"), ("influxdb", "influxdb_queryable_data", "/var/lib/influxdb3"), ("prometheus", "prometheus_data", "/prometheus"), ("loki", "loki_data", "/loki"), ("grafana", "grafana_data", "/var/lib/grafana"), ("quarantine-init", "quarantine_data", "/quarantine")];
    internal string Root { get; } = FindRoot();
    private readonly string owner = "t078-" + Guid.NewGuid().ToString("N");
    internal string DirectoryPath { get; private set; } = "";
    private string executable = "";
    private readonly Dictionary<string, string> environment = new();
    private readonly List<string> secrets = [];
    internal string RabbitPassword => environment["RABBITMQ_PASSWORD"];
    internal string ControlConnection => "Host=postgres;Database=telemetry_control;Username=telemetry;Password=" + environment["POSTGRES_PASSWORD"];
    internal string AdminToken
    {
        get
        {
            using var document = JsonDocument.Parse(File.ReadAllText(environment["INFLUXDB_QUERYABLE_ADMIN_TOKEN_FILE"]));
            return document.RootElement.GetProperty("token").GetString()!;
        }
    }
    public async Task InitializeAsync()
    {
        executable = FindDocker();
        DirectoryPath = Path.Combine(Root, "artifacts/validation/T078", owner);
        Directory.CreateDirectory(DirectoryPath);
        foreach (var key in new[] { "POSTGRES_PASSWORD", "RABBITMQ_PASSWORD", "GRAFANA_ADMIN_PASSWORD", "INFLUXDB_GRAFANA_READ_TOKEN", "POSTGRES_METRICS_PASSWORD" })
        {
            environment[key] = "t078-" + Guid.NewGuid().ToString("N");
            secrets.Add(environment[key]);
            if (key != "RABBITMQ_PASSWORD")
            {
                var secretFile = Path.Combine(DirectoryPath, key + ".txt");
                await File.WriteAllTextAsync(secretFile, environment[key]);
                environment[key + "_FILE"] = secretFile;
            }
        }
        environment["GRAFANA_PORT"] = "0";
        foreach (var key in new[] { "POSTGRES_PORT", "RABBITMQ_PORT", "RABBITMQ_MANAGEMENT_PORT", "INFLUXDB_QUERYABLE_PORT", "PROMETHEUS_PORT", "LOKI_PORT", "API_PORT", "PUBSUB_EMULATOR_PORT" })
            environment[key] = "0";
        environment["INFLUXDB_QUERYABLE_ADMIN_TOKEN_FILE"] = Path.Combine(DirectoryPath, "queryable.json");
        (await DockerAsync("run", "--rm", "--network", "none", "--mount", $"type=bind,source={DirectoryPath},target=/tokens", "influxdb:3.11.6-core", "influxdb3", "create", "token", "--admin", "--offline", "--name", "t078-bootstrap", "--output-file", "/tokens/queryable.json")).ExitCode.ShouldBe(0, "Offline bootstrap failed; no token output is printed.");
        using (var token = JsonDocument.Parse(await File.ReadAllTextAsync(environment["INFLUXDB_QUERYABLE_ADMIN_TOKEN_FILE"])))
            secrets.Add(token.RootElement.GetProperty("token").GetString()!);
        await File.WriteAllTextAsync(Path.Combine(DirectoryPath, "influx-proof.sh"), """
            #!/bin/sh
            set -eu
            INFLUXDB3_AUTH_TOKEN=$(sed -n 's/.*"token": *"\([^"]*\)".*/\1/p' /run/secrets/influxdb_admin_token)
            export INFLUXDB3_AUTH_TOKEN
            export INFLUXDB3_HOST_URL=http://localhost:8181
            if [ "$1" = seed ]; then
              printf '%s\n' '__POINT_A__' '__POINT_B__' > /tmp/t078.lp
              influxdb3 write --quiet --database telemetry --precision ms --gzip --file /tmp/t078.lp
            fi
            influxdb3 query --database telemetry --format csv 'SELECT empresa_id,veiculo_id,latitude,longitude,time FROM telemetria ORDER BY empresa_id'
            """.Replace("__POINT_A__", Point("t078-a", 1)).Replace("__POINT_B__", Point("t078-b", 3)).Replace("\r\n", "\n") + "\n");
        await using var project = Project();
        await project.ComposeOkAsync("build", "grafana", "loki");
    }
    private static string Point(string company, int latitude) => $"telemetria,empresa_id={company},veiculo_id=shared latitude={latitude}.0,longitude=2.0,velocidade=0.0,rpm=0i,ignicao=false {DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds()}";
    internal ComposeProject Project() => new(this, "t078-" + Guid.NewGuid().ToString("N"));
    internal string Image(string service) => owner + "-" + service;
    internal async Task<CommandResult> DockerAsync(params string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { WorkingDirectory = Root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        foreach (var entry in environment)
            info.Environment[entry.Key] = entry.Value;
        using var process = new Process { StartInfo = info };
        var operation = arguments[0] == "compose" ? "compose " + arguments.FirstOrDefault(v => v is "config" or "build" or "up" or "down" or "restart" or "exec" or "port" or "ps") : arguments[0];
        await File.AppendAllTextAsync(Path.Combine(DirectoryPath, "commands.log"), $"START {operation}\n");
        process.Start().ShouldBeTrue();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(12));
        }
        catch { process.Kill(entireProcessTree: true); throw; }
        var stdout = await output;
        var stderr = await error;
        // Resolved Compose models contain secrets: never log stdout or full command lines.
        await File.AppendAllTextAsync(Path.Combine(DirectoryPath, "commands.log"), $"END {operation} exit={process.ExitCode}\n");
        if (process.ExitCode != 0 && !(arguments.Length > 1 && arguments[1] == "inspect"))
        {
            foreach (var secret in secrets)
                stderr = stderr.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            await File.AppendAllTextAsync(Path.Combine(DirectoryPath, "failures.log"), stderr + "\n");
        }
        return new(process.ExitCode, arguments[0] == "logs" ? stdout + stderr : stdout);
    }
    internal async Task CaptureAsync(string id)
    {
        var output = (await DockerAsync("logs", "--tail", "80", id)).Output;
        foreach (var secret in secrets)
            output = output.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(DirectoryPath, id + "-logs.txt"), output);
    }
    public async Task DisposeAsync()
    {
        if (!string.IsNullOrEmpty(executable) && Directory.Exists(DirectoryPath))
            foreach (var service in new[] { "grafana", "loki", "api", "worker" })
                if ((await DockerAsync("image", "inspect", Image(service))).ExitCode == 0)
                    (await DockerAsync("image", "rm", Image(service))).ExitCode.ShouldBe(0, "Could not remove a test-owned image tag.");
    }
    private static string FindDocker()
    {
        var configured = Environment.GetEnvironmentVariable("TELEMETRY_TEST_DOCKER_CLI");
        if (!string.IsNullOrEmpty(configured))
            return configured;
        if (OperatingSystem.IsWindows())
            foreach (var path in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs/DockerDesktop/resources/bin/docker.exe"), "C:/Program Files/Docker/Docker/resources/bin/docker.exe" })
                if (File.Exists(path))
                    return path;
        return "docker";
    }
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CentralMobilidade.slnx")))
            directory = directory.Parent;
        return directory.ShouldNotBeNull().FullName;
    }
    internal sealed record CommandResult(int ExitCode, string Output);
}

internal sealed class ComposeProject : IAsyncDisposable
{
    private readonly ComposeRecoveryFixture fixture;
    internal string Name
    {
        get;
    }
    private readonly string overrides;
    private readonly List<string> files;
    internal ComposeProject(ComposeRecoveryFixture fixture, string name)
    {
        this.fixture = fixture;
        Name = name;
        overrides = Path.Combine(fixture.DirectoryPath, name + ".json");
        files = [Path.Combine(fixture.Root, "deploy/compose.yaml"), overrides];
        var model = new JsonObject
        {
            ["services"] = new JsonObject
            {
                ["grafana"] = new JsonObject { ["image"] = fixture.Image("grafana") },
                ["loki"] = new JsonObject { ["image"] = fixture.Image("loki") },
                ["rabbitmq"] = new JsonObject { ["ports"] = new JsonArray(new JsonObject { ["target"] = 5672, ["published"] = "0", ["host_ip"] = "127.0.0.1" }) },
                ["influxdb"] = new JsonObject { ["volumes"] = new JsonArray(new JsonObject { ["type"] = "bind", ["source"] = fixture.DirectoryPath, ["target"] = "/validation", ["read_only"] = true }) }
            }
        };
        File.WriteAllText(overrides, model.ToJsonString());
    }
    internal async Task OverrideAsync(JsonObject model)
    {
        var path = Path.Combine(fixture.DirectoryPath, Name + "-fault.json");
        await File.WriteAllTextAsync(path, model.ToJsonString());
        files.Add(path);
    }
    internal Task<ComposeRecoveryFixture.CommandResult> ComposeAsync(params string[] arguments) => fixture.DockerAsync(Arguments(arguments));
    internal async Task EnableProfilesAsync()
    {
        files.Add(Path.Combine(fixture.Root, "deploy/compose.override.yaml"));
        files.Add(Path.Combine(fixture.Root, "deploy/compose.monitoring.yaml"));
        var reset = Path.Combine(fixture.DirectoryPath, Name + "-env-reset.yaml");
        await File.WriteAllTextAsync(reset, "services:\n  worker:\n    env_file: !reset []\n");
        files.Add(reset); // Never load the user's optional .env.worker.
        var environment = new JsonObject
        {
            ["ConnectionStrings__TelemetryControl"] = fixture.ControlConnection,
            ["Queue__RabbitMq__Password"] = fixture.RabbitPassword,
            ["InfluxDb__Queryable__Token"] = fixture.AdminToken,
            ["Processing__DiagnosticKey"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            ["BatchInspection__CursorKey"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
        };
        await OverrideAsync(new JsonObject
        {
            ["services"] = new JsonObject
            {
                ["api"] = new JsonObject { ["image"] = fixture.Image("api"), ["environment"] = environment.DeepClone() },
                ["worker"] = new JsonObject { ["image"] = fixture.Image("worker"), ["environment"] = environment.DeepClone(), ["env_file"] = new JsonArray() }
            }
        });
    }
    internal async Task AssertHardenedAsync(string[] services)
    {
        foreach (var service in services)
        {
            var id = (await ComposeOkAsync("ps", "-q", service)).Output.Trim();
            var inspected = await fixture.DockerAsync("inspect", id);
            inspected.ExitCode.ShouldBe(0);
            using var document = JsonDocument.Parse(inspected.Output);
            var host = document.RootElement[0].GetProperty("HostConfig");
            host.GetProperty("ReadonlyRootfs").GetBoolean().ShouldBeTrue(service);
            host.GetProperty("Memory").GetInt64().ShouldBeGreaterThan(0, service);
            host.GetProperty("NanoCpus").GetInt64().ShouldBeGreaterThan(0, service);
            host.GetProperty("PidsLimit").GetInt64().ShouldBeGreaterThan(0, service);
            host.GetProperty("CapDrop").EnumerateArray().ShouldContain(value => value.GetString() == "ALL");
            host.GetProperty("SecurityOpt").EnumerateArray().ShouldContain(value => value.GetString() == "no-new-privileges:true");
            host.GetProperty("RestartPolicy").GetProperty("Name").GetString().ShouldBe("unless-stopped");
            var command = service == "loki" ? new[] { "exec", "-T", service, "/bin/busybox", "sh", "-ec", "test \"$(/bin/busybox id -u)\" -ne 0; /bin/busybox touch /tmp/t086-proof; /bin/busybox rm /tmp/t086-proof" }
                : new[] { "exec", "-T", service, "sh", "-ec", "test \"$(id -u)\" -ne 0; touch /tmp/t086-proof; rm /tmp/t086-proof" };
            await ComposeOkAsync(command);
        }
    }
    internal async Task AwaitEngineRestartAsync(string service)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(90))
        {
            var id = (await ComposeOkAsync("ps", "-aq", service)).Output.Trim();
            using var json = JsonDocument.Parse((await fixture.DockerAsync("inspect", id)).Output);
            var container = json.RootElement[0];
            if (container.GetProperty("RestartCount").GetInt32() > 0 &&
                container.GetProperty("State").GetProperty("Health").GetProperty("Status").GetString() == "healthy")
                return;
            await Task.Delay(1000);
        }
        Assert.Fail("Engine did not restart the test broker into a healthy state.");
    }
    internal async Task<ComposeRecoveryFixture.CommandResult> ComposeOkAsync(params string[] arguments)
    {
        var result = await ComposeAsync(arguments);
        if (result.ExitCode != 0 && arguments.Contains("up", StringComparer.Ordinal))
            foreach (var row in await RowsAsync())
                await fixture.CaptureAsync(row.GetProperty("ID").GetString()!);
        result.ExitCode.ShouldBe(0, "Compose operation failed; inspect the private T078 failure log.");
        return result;
    }
    private string[] Arguments(string[] arguments)
    {
        Name.ShouldMatch("^t078-[a-f0-9]{32}$");
        var list = new List<string> { "compose", "--project-name", Name, "--env-file", Path.Combine(fixture.Root, ".env.example") };
        foreach (var file in files)
            list.AddRange(["-f", file]);
        list.AddRange(arguments);
        return list.ToArray();
    }
    internal async Task<JsonNode> ModelAsync(bool development = false, bool monitoring = false)
    {
        var args = new List<string>();
        if (development)
            args.AddRange(["-f", Path.Combine(fixture.Root, "deploy/compose.override.yaml"), "--profile", "app", "--profile", "pubsub-emulator"]);
        if (monitoring)
            args.AddRange(["-f", Path.Combine(fixture.Root, "deploy/compose.monitoring.yaml"), "--profile", "host-metrics"]);
        args.AddRange(["config", "--format", "json"]);
        if (development)
            args.Add("--no-env-resolution");
        return JsonNode.Parse((await ComposeOkAsync(args.ToArray())).Output)!;
    }
    internal Task UpAsync() => ComposeOkAsync("up", "-d", "--no-build", "--wait", "--wait-timeout", "240");
    internal async Task<JsonElement[]> RowsAsync()
    {
        var text = (await ComposeOkAsync("ps", "-a", "--format", "json")).Output.Trim();
        if (text.Length == 0)
            return [];
        if (text[0] == '[')
        {
            using var json = JsonDocument.Parse(text);
            return json.RootElement.EnumerateArray().Select(r => r.Clone()).ToArray();
        }
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => { using var json = JsonDocument.Parse(line); return json.RootElement.Clone(); }).ToArray();
    }
    internal async Task AssertHealthyAsync()
    {
        var rows = await RowsAsync();
        rows.Length.ShouldBe(8);
        foreach (var service in ComposeRecoveryFixture.Servers)
        {
            var row = rows.Single(r => r.GetProperty("Service").GetString() == service);
            row.GetProperty("State").GetString().ShouldBe("running", service);
            row.GetProperty("Health").GetString().ShouldBe("healthy", service);
        }
        foreach (var service in new[] { "influxdb-init", "quarantine-init" })
        {
            var row = rows.Single(r => r.GetProperty("Service").GetString() == service);
            row.GetProperty("State").GetString().ShouldBe("exited");
            row.GetProperty("ExitCode").GetInt32().ShouldBe(0);
        }
    }
    internal async Task VolumeMarkersAsync(string marker, bool write)
    {
        var args = new List<string> { "run", "--rm", "--network", "none" };
        foreach (var volume in ComposeRecoveryFixture.Volumes)
            args.AddRange(["--mount", $"type=volume,source={Name}_{volume},target=/proof/{volume}"]);
        var script = write ? $"for d in /proof/*; do printf '%s' '{marker}' > \"$d/t078-proof\"; done" : $"for d in /proof/*; do test \"$(cat \"$d/t078-proof\")\" = '{marker}'; done";
        args.AddRange(["alpine:3.23.4", "sh", "-ec", script]);
        (await fixture.DockerAsync(args.ToArray())).ExitCode.ShouldBe(0);
    }
    private async Task<IConnection> RabbitAsync()
    {
        var address = (await ComposeOkAsync("port", "rabbitmq", "5672")).Output.Trim();
        var port = int.Parse(address[(address.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        return await new ConnectionFactory { HostName = "127.0.0.1", Port = port, UserName = "telemetry", Password = fixture.RabbitPassword, VirtualHost = "telemetry" }.CreateConnectionAsync();
    }
    internal async Task PublishProofAsync(string marker)
    {
        await using var connection = await RabbitAsync();
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        await channel.QueueDeclareAsync("t078-proof", durable: true, exclusive: false, autoDelete: false);
        await channel.BasicPublishAsync("", "t078-proof", true, new BasicProperties { Persistent = true, MessageId = marker }, Encoding.UTF8.GetBytes(marker));
    }
    internal async Task VerifyProofMessageAsync(string marker)
    {
        await using var connection = await RabbitAsync();
        await using var channel = await connection.CreateChannelAsync();
        var message = (await channel.BasicGetAsync("t078-proof", autoAck: false)).ShouldNotBeNull();
        Encoding.UTF8.GetString(message.Body.Span).ShouldBe(marker);
        message.BasicProperties.MessageId.ShouldBe(marker);
        message.BasicProperties.Persistent.ShouldBeTrue();
        await channel.BasicNackAsync(message.DeliveryTag, false, true);
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            await ComposeOkAsync("--profile", "*", "down", "--timeout", "10", "--remove-orphans");
        }
        finally
        {
            // Remove only exact UUID-scoped volumes with matching Compose ownership labels.
            foreach (var volume in ComposeRecoveryFixture.Volumes)
            {
                var name = Name + "_" + volume;
                var inspected = await fixture.DockerAsync("volume", "inspect", name);
                if (inspected.ExitCode != 0)
                    continue;
                using var json = JsonDocument.Parse(inspected.Output);
                json.RootElement[0].GetProperty("Labels").GetProperty("com.docker.compose.project").GetString().ShouldBe(Name);
                (await fixture.DockerAsync("volume", "rm", name)).ExitCode.ShouldBe(0);
            }
        }
    }
}
