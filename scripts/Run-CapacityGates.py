"""Private, isolated Docker capacity fixture. Standard-library Python; no existing deployments touched.

Run from repo root: py -3 scripts/Run-CapacityGates.py --duration 600
Artifacts and ephemeral credentials remain under artifacts/validation/T094/<run>/.
"""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import subprocess
import threading
import time
import urllib.request
import urllib.parse
import traceback
import sys
import uuid

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--duration", type=int, default=600)
    parser.add_argument("--vehicles", type=int, choices=(10000, 100000))
    parser.add_argument("--soak-duration", type=int, default=1800)
    parser.add_argument("--storage-measurement", action="store_true", help="T095 isolated component/storage sample; no load or soak approval")
    parser.add_argument("--quickstart-smoke", action="store_true", help="T098 small real intake/partial/restart scenario, not final 100k proof")
    args = parser.parse_args()
    if args.storage_measurement and args.quickstart_smoke:
        raise ValueError("Select only one special measurement mode")
    if args.storage_measurement or args.quickstart_smoke:
        args.vehicles = 100000
    if not 60 <= args.duration <= 3600:
        raise ValueError("Duration must be between 60 and 3600 seconds")
    if not 600 <= args.soak_duration <= 3600:
        raise ValueError("Soak must be between 600 and 3600 seconds")
    if args.vehicles is None:
        failures = []
        for vehicles in (10000, 100000):
            result = subprocess.run([sys.executable, str(Path(__file__).resolve()), "--duration", str(args.duration), "--vehicles", str(vehicles)], cwd=ROOT)
            if result.returncode == 0:
                result = subprocess.run([sys.executable, str(Path(__file__).resolve()), "--duration", str(args.soak_duration), "--vehicles", str(vehicles)], cwd=ROOT)
            else:
                print(f"Soak {vehicles}: blocked by failed load/preflight gate; not a passing soak.", flush=True)
            failures.append(result.returncode)
        return 1 if any(failures) else 0
    run = uuid.uuid4().hex
    task = "T098" if args.quickstart_smoke else "T095" if args.storage_measurement else "T094"
    private = ROOT / "artifacts/validation" / task / run
    private.mkdir(parents=True)
    (private / "collector").mkdir()
    (private / "collector/metrics").write_text("", encoding="utf-8")
    docker = shutil.which("docker") or str(Path(os.environ.get("LOCALAPPDATA", "")) / "Programs/DockerDesktop/resources/bin/docker.exe")
    project = "cm-" + task.lower() + "-" + run[:12]
    summary = {"run": run, "project": project, "durationSeconds": args.duration, "results": [], "capacityApproved": False}
    summary["sourceFilesSha256"] = {str(path.relative_to(ROOT)): hashlib.sha256(path.read_bytes()).hexdigest()
                                     for path in [Path(__file__).resolve(), *sorted((ROOT / "tests/CentralMobilidade.Telemetry.LoadTests").rglob("*.cs"))]
                                     if "obj" not in path.parts and "bin" not in path.parts}
    if args.storage_measurement:
        module_path = ROOT / "scripts/storage_capacity_measurement.py"
        summary["sourceFilesSha256"][str(module_path.relative_to(ROOT))] = hashlib.sha256(module_path.read_bytes()).hexdigest()
    if args.quickstart_smoke:
        module_path = ROOT / "scripts/quickstart_smoke.py"
        summary["sourceFilesSha256"][str(module_path.relative_to(ROOT))] = hashlib.sha256(module_path.read_bytes()).hexdigest()

    def command(argv, name="commands", check=True):
        result = subprocess.run(argv, cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace")
        # Outputs are private; never print commands/configuration/credentials.
        with (private / (name + ".log")).open("a", encoding="utf-8") as output:
            output.write("Offline token generation output suppressed.\n" if name == "token" else result.stdout + result.stderr)
        if check and result.returncode:
            raise RuntimeError("Fixture command failed: " + name)
        return result

    compose = private / "compose.json"
    def dc(*parts, check=True):
        return command([docker, "compose", "--project-name", project, "-f", str(compose), *parts], "compose", check)
    def sql(query):
        return dc("exec", "-T", "postgres", "psql", "-U", "telemetry", "-d", "telemetry_control", "-At", "-c", query).stdout.strip()
    def port(service, target):
        return int(dc("port", service, str(target)).stdout.strip().split(":")[-1])
    def get(url, auth=None, data=None):
        headers = {}
        if auth:
            headers["Authorization"] = auth
        if data is not None:
            data = json.dumps(data).encode()
            headers["Content-Type"] = "application/json"
        with urllib.request.urlopen(urllib.request.Request(url, data=data, headers=headers), timeout=20) as response:
            return json.loads(response.read())

    postgres_password, rabbit_password = secrets.token_hex(24), secrets.token_hex(24)
    producer_token = "cm_p_" + base64.urlsafe_b64encode(secrets.token_bytes(32)).decode().rstrip("=")
    salt = secrets.token_bytes(16)
    credential_hash = {"LookupDigest": hashlib.sha256(producer_token.encode()).hexdigest(), "Salt": base64.b64encode(salt).decode(),
                       "Verifier": base64.b64encode(hashlib.pbkdf2_hmac("sha256", producer_token.encode(), salt, 600000, 32)).decode(), "Iterations": 600000}
    common = {"ConnectionStrings": {"TelemetryControl": f"Host=postgres;Database=telemetry_control;Username=telemetry;Password={postgres_password}"},
              "Queue": {"Provider": "RabbitMq", "RabbitMq": {"Host": "rabbitmq", "VirtualHost": "telemetry", "Username": "telemetry", "Password": rabbit_password}},
              "Processing": {"DiagnosticKey": base64.b64encode(secrets.token_bytes(32)).decode()}, "Quarantine": {"Path": "/quarantine/rejections"},
              "Logging": {"LogLevel": {"Default": "Warning"}}, "Observability": {"TraceSamplingRatio": 0.01}}
    (private / "migration.sql").write_text("", encoding="utf-8")
    collector = None
    stop = threading.Event()
    thread = None
    try:
        info = json.loads(command([docker, "info", "--format", "{{json .}}"], "engine").stdout)
        summary["engine"] = {key: info.get(key) for key in ("NCPU", "MemTotal", "ServerVersion", "OSType", "Architecture")}
        summary["revision"] = command(["git", "rev-parse", "HEAD"], "revision").stdout.strip()
        command([docker, "run", "--rm", "--network", "none", "--mount", f"type=bind,source={private},target=/tokens",
                 "influxdb:3.11.6-core", "influxdb3", "create", "token", "--admin", "--offline", "--name", "t094", "--output-file", "/tokens/influx-token.json"], "token")
        influx_token = json.loads((private / "influx-token.json").read_text())["token"]
        common["InfluxDb"] = {"Queryable": {"Url": "http://influxdb:8181", "Database": "telemetry", "Token": influx_token}}
        api_config = json.loads(json.dumps(common))
        api_config.update({"AllowedHosts": "*", "Authentication": {"Producers": [{"CredentialId": "capacity", "ProducerId": "capacity", "Hash": credential_hash}]}})
        if args.quickstart_smoke:
            from quickstart_smoke import configure
            smoke_identity = configure(api_config, run)
        worker_config = json.loads(json.dumps(common))
        worker_config["Worker"] = {"Concurrency": 4}
        for name, data in (("api", api_config), ("worker", worker_config)):
            (private / (name + ".json")).write_text(json.dumps(data), encoding="utf-8")
        services = {
            "postgres": {"image": "postgres:17.11-bookworm", "cpus": 2, "mem_limit": "2g", "command": ["postgres", "-c", "fsync=on", "-c", "synchronous_commit=on"],
                         "environment": {"POSTGRES_DB": "telemetry_control", "POSTGRES_USER": "telemetry", "POSTGRES_PASSWORD": postgres_password},
                         "volumes": ["postgres:/var/lib/postgresql/data", f"{private / 'migration.sql'}:/setup/migration.sql:ro"]},
            "rabbitmq": {"image": "rabbitmq:4.3.6-management-alpine", "cpus": 1, "mem_limit": "1g", "hostname": "rabbitmq",
                         "environment": {"RABBITMQ_DEFAULT_USER": "telemetry", "RABBITMQ_DEFAULT_PASS": rabbit_password, "RABBITMQ_DEFAULT_VHOST": "telemetry"},
                         "ports": ["127.0.0.1::15672"], "volumes": ["rabbitmq:/var/lib/rabbitmq"]},
            "influxdb": {"image": "influxdb:3.11.6-core", "cpus": 2, "mem_limit": "4g",
                         "command": ["influxdb3", "serve", "--node-id", "queryable", "--object-store", "file", "--data-dir", "/data", "--admin-token-file", "/tokens/token.json", "--disable-authz", "health,ping,metrics", "--disable-package-management"],
                         "ports": ["127.0.0.1::8181"], "volumes": ["influx:/data", f"{private / 'influx-token.json'}:/tokens/token.json:ro"],
                         "environment": {"INFLUXDB3_AUTH_TOKEN": influx_token, "INFLUXDB3_HOST_URL": "http://localhost:8181"}},
        }
        for name, cpus, memory, exposed in (("api", 2, "4g", 8080), ("worker", 6, "12g", 9464)):
            services[name] = {"image": "centralmobilidade-" + name + ":t094", "cpus": cpus, "mem_limit": memory,
                              "environment": {"DOTNET_ENVIRONMENT": "Capacity", "ASPNETCORE_ENVIRONMENT": "Capacity"},
                              "ports": [f"127.0.0.1::{exposed}"], "volumes": [f"{private / (name + '.json')}:/app/appsettings.Capacity.json:ro", "quarantine:/quarantine"]}
        services["observer"] = {"image": "alpine:3.23.4", "entrypoint": ["sleep", "infinity"], "volumes": ["postgres:/observe/postgres:ro", "rabbitmq:/observe/rabbitmq:ro", "influx:/observe/influx:ro"]}
        if args.storage_measurement:
            # Short-sample snapshot profile, never applied to deployments or the reference load gate.
            services["influxdb"]["command"] += ["--gen1-duration", "1m", "--wal-files-per-snapshot", "10", "--force-snapshot-max-age", "30s"]
            services["loki"] = {"image": "centralmobilidade-loki:3.7.0-busybox-1.37.0", "command": ["-config.file=/etc/loki/local-config.yaml"],
                                "ports": ["127.0.0.1::3100"], "volumes": ["loki:/loki", f"{ROOT / 'deploy/loki/loki-config.yaml'}:/etc/loki/local-config.yaml:ro"]}
            services["observer"]["volumes"] += ["quarantine:/observe/quarantine:ro", "loki:/observe/loki:ro"]
        services["collector"] = {"image": "centralmobilidade-api:t094", "entrypoint": ["/bin/busybox"], "command": ["httpd", "-f", "-p", "9101", "-h", "/collector"], "volumes": [f"{private / 'collector'}:/collector:ro"]}
        services["prometheus"] = {"image": "prom/prometheus:v3.15.0-busybox", "cpus": 0.5, "mem_limit": "512m", "ports": ["127.0.0.1::9090"], "volumes": [f"{private / 'prometheus.json'}:/etc/prometheus/prometheus.yml:ro"]}
        (private / "prometheus.json").write_text("{}", encoding="utf-8")
        volume_names = ["postgres", "rabbitmq", "influx", "quarantine"] + (["loki"] if args.storage_measurement else [])
        compose.write_text(json.dumps({"services": services, "volumes": {name: {} for name in volume_names}}), encoding="utf-8")
        command(["dotnet", "ef", "migrations", "script", "--project", "src/CentralMobilidade.Telemetry.Infrastructure", "--no-build", "--output", str(private / "migration.sql")], "migration")
        dc("run", "--rm", "--no-deps", "--user", "0", "--entrypoint", "/bin/sh", "influxdb", "-c", "chown 1500:1500 /data")
        dc("up", "-d", "postgres", "rabbitmq", "influxdb", "observer")
        for attempt in range(60):
            if dc("exec", "-T", "postgres", "pg_isready", "-U", "telemetry", check=False).returncode == 0:
                break
            time.sleep(1)
        dc("exec", "-T", "postgres", "psql", "-U", "telemetry", "-d", "telemetry_control", "-v", "ON_ERROR_STOP=1", "-f", "/setup/migration.sql")
        for attempt in range(60):
            result = dc("exec", "-T", "influxdb", "influxdb3", "create", "database", "--retention-period", "60d", "telemetry", check=False)
            if result.returncode == 0: break
            time.sleep(1)
        dc("run", "--rm", "--no-deps", "--user", "0", "--entrypoint", "/bin/busybox", "worker", "sh", "-c", "mkdir -p /quarantine/rejections && chown 1654:1654 /quarantine /quarantine/rejections && chmod 700 /quarantine/rejections")
        dc("up", "-d", "api", "worker")
        api_port, worker_port, rabbit_port, influx_port = port("api", 8080), port("worker", 9464), port("rabbitmq", 15672), port("influxdb", 8181)
        rabbit_auth = "Basic " + base64.b64encode(("telemetry:" + rabbit_password).encode()).decode()
        api_url = f"http://127.0.0.1:{api_port}"
        for attempt in range(90):
            try:
                with urllib.request.urlopen(api_url + "/health/live", timeout=2) as response:
                    if response.status == 200: break
            except Exception: time.sleep(1)
        identifiers = {service: dc("ps", "-q", service).stdout.strip() for service in ("api", "worker")}
        inspected = json.loads(command([docker, "inspect", *identifiers.values()], "inspect").stdout)
        summary["limits"] = [{"name": item["Name"], "image": item["Image"], "nanoCpus": item["HostConfig"]["NanoCpus"], "memoryBytes": item["HostConfig"]["Memory"]} for item in inspected]
        cpu_capacity = sum(item["HostConfig"]["NanoCpus"] for item in inspected) / 1e9
        memory_capacity = sum(item["HostConfig"]["Memory"] for item in inspected)
        state = {"values": {}, "at": 0}
        def unit_bytes(value):
            number, unit = re.fullmatch(r"([0-9.]+)\s*([A-Za-z]+)", value.strip()).groups()
            return float(number) * {"B": 1, "KiB": 1024, "MiB": 1024**2, "GiB": 1024**3}[unit]
        def observe():
            while not stop.is_set():
                try:
                    stats = [json.loads(line) for line in command([docker, "stats", "--no-stream", "--format", "{{json .}}", *identifiers.values()], "stats").stdout.splitlines()]
                    queue = get(f"http://127.0.0.1:{rabbit_port}/api/queues/telemetry/telemetry.batches.queue", rabbit_auth)
                    disk = int(dc("exec", "-T", "observer", "du", "-sk", "/observe").stdout.split()[0]) * 1024
                    age = float(sql('SELECT COALESCE(EXTRACT(EPOCH FROM clock_timestamp()-min("ReceivedAt")),0) FROM ingestion_batches WHERE "State" IN (\'Accepted\',\'Processing\',\'Retrying\')'))
                    state["values"] = {"load_reference_cpu_cores": cpu_capacity, "load_reference_memory_bytes": memory_capacity,
                        "load_application_cpu_percent": sum(float(row["CPUPerc"].rstrip("%")) for row in stats) / cpu_capacity,
                        "load_application_memory_bytes": sum(unit_bytes(row["MemUsage"].split("/")[0]) for row in stats),
                        "load_storage_used_bytes": disk, "load_queue_messages": queue.get("messages", 0), "load_pending_batch_age_seconds": max(0, age)}
                    state["at"] = time.time()
                    state["values"]["load_observation_timestamp_seconds"] = state["at"]
                    temporary = private / "collector/metrics.tmp"
                    temporary.write_bytes(("\n".join(f"{key} {value}" for key, value in state["values"].items()) + "\n").encode("utf-8"))
                    temporary.replace(private / "collector/metrics")
                except Exception:
                    pass  # Stale/missing measurements become HTTP 503, never synthetic zero.
                stop.wait(3)
        thread = threading.Thread(target=observe, daemon=True); thread.start()
        prom = {"global": {"scrape_interval": "5s"}, "scrape_configs": [{"job_name": "capacity", "fallback_scrape_protocol": "PrometheusText0.0.4", "static_configs": [{"targets": ["collector:9101"]}]},
                {"job_name": "worker", "static_configs": [{"targets": ["worker:9464"]}]}]}
        (private / "prometheus.json").write_text(json.dumps(prom), encoding="utf-8")
        dc("up", "-d", "collector", "prometheus")
        prom_url = f"http://127.0.0.1:{port('prometheus', 9090)}"
        names = {"referenceCpuCores": "load_reference_cpu_cores", "referenceMemoryBytes": "load_reference_memory_bytes", "cpuPercent": "load_application_cpu_percent", "memoryBytes": "load_application_memory_bytes", "diskUsedBytes": "load_storage_used_bytes", "queueMessages": "load_queue_messages", "queueOldestAgeSeconds": "load_pending_batch_age_seconds"}
        names = {key: value + " and on() ((time()-load_observation_timestamp_seconds)<20)" for key, value in names.items()}
        for key, quantile in (("writeP50Seconds", ".50"), ("writeP95Seconds", ".95"), ("writeP99Seconds", ".99")):
            names[key] = f'histogram_quantile({quantile},sum by(le)(rate(telemetry_storage_write_duration_seconds_bucket{{job="worker"}}[30m])))'
        # Seed a histogram, scrape it, then produce a new confirmed write for a real positive rate.
        import gzip
        warm = f"telemetria,empresa_id=warm-{run},veiculo_id=warm latitude=1,longitude=2,velocidade=3,rpm=4i,ignicao=true {int(time.time()*1000)}\n".encode()
        request = urllib.request.Request(api_url + "/api/v1/telemetry/batches", data=gzip.compress(warm), headers={"Authorization": "Bearer " + producer_token, "X-Contract-Version": "v1", "Content-Type": "text/plain; charset=utf-8", "Content-Encoding": "gzip"})
        with urllib.request.urlopen(request, timeout=30) as response: warm_receipt = json.loads(response.read())
        for attempt in range(120):
            status = get(api_url + "/api/v1/telemetry/batches/" + warm_receipt["batchId"], "Bearer " + producer_token)
            if status["state"] == "completed": break
            time.sleep(1)
        else: raise RuntimeError("Warmup did not persist")
        time.sleep(10)
        warm = warm.replace(b"veiculo_id=warm ", b"veiculo_id=warm-second ")
        request = urllib.request.Request(api_url + "/api/v1/telemetry/batches", data=gzip.compress(warm), headers={"Authorization": "Bearer " + producer_token, "X-Contract-Version": "v1", "Content-Type": "text/plain; charset=utf-8", "Content-Encoding": "gzip"})
        with urllib.request.urlopen(request, timeout=30) as response: json.loads(response.read())
        time.sleep(15)
        summary["warmup"] = status["state"]
        with urllib.request.urlopen(f"http://127.0.0.1:{worker_port}/metrics", timeout=10) as response:
            (private / "worker-metrics.txt").write_bytes(response.read())
        checks = {key: get(prom_url + "/api/v1/query?query=" + urllib.parse.quote(query, safe="")) for key, query in names.items()}
        (private / "preflight.json").write_text(json.dumps(checks, indent=2), encoding="utf-8")
        (private / "prometheus-targets.json").write_text(json.dumps(get(prom_url + "/api/v1/targets"), indent=2), encoding="utf-8")
        dc("logs", "--tail", "20", "collector")
        if args.storage_measurement:
            from storage_capacity_measurement import measure
            summary["storage"] = measure(private, dc, sql, get, api_url, producer_token, influx_port, influx_token, rabbit_port, rabbit_auth, port)
            summary["status"] = "storage sample measured; projections are conditional, not aged-retention or production capacity approval"
            return 0
        if args.quickstart_smoke:
            from quickstart_smoke import execute
            summary["quickstart"] = execute(private, dc, sql, get, api_url, producer_token, influx_port, influx_token, rabbit_port, rabbit_auth, worker_port, smoke_identity)
            summary["status"] = "small quickstart smoke complete; Grafana/capacity/final-proof gates remain separate"
            return 0
        environment = dict(os.environ, TELEMETRY_LOAD_PRODUCER_TOKEN=producer_token)
        for vehicles in (args.vehicles,):
            config = {"Api": api_url + "/", "Prometheus": prom_url + "/", "ReportDirectory": str(private / str(vehicles)), "Queries": names,
                      "Vehicles": vehicles, "DurationSeconds": args.duration, "MaxConcurrentPosts": 16, "DrainSeconds": 300}
            config_path = private / f"load-{vehicles}.json"; config_path.write_text(json.dumps(config), encoding="utf-8")
            print(f"Starting real {vehicles}-vehicle run ({args.duration}s); private evidence {private.name}", flush=True)
            with (private / f"load-{vehicles}.log").open("w", encoding="utf-8") as output:
                process = subprocess.run(["dotnet", "run", "--no-build", "--project", "tests/CentralMobilidade.Telemetry.LoadTests", "--", "--scenario", "steady-5000-eps", "--config", str(config_path)], cwd=ROOT, env=environment, stdout=output, stderr=output)
            reports = list((private / str(vehicles)).glob("steady-*.json"))
            entry = {"vehicles": vehicles, "exitCode": process.returncode, "reports": [str(path.relative_to(ROOT)) for path in reports]}
            if reports:
                report = json.loads(reports[0].read_text())
                prefix = "load-" + report["RunId"] + "-%"
                result = get(f"http://127.0.0.1:{influx_port}/api/v3/query_sql", "Bearer " + influx_token,
                             {"db": "telemetry", "q": f"SELECT count(*) AS n FROM telemetria WHERE empresa_id LIKE '{prefix}'", "format": "json"})
                entry["influxCount"] = result[0]["n"]
                entry["acknowledged"] = report["AcknowledgedEvents"]
                entry["reconciled"] = report["ReconciledEvents"]
                entry["passed"] = report["Passed"] and entry["influxCount"] == report["AcknowledgedEvents"]
            summary["results"].append(entry)
            (private / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
            print(f"Completed {vehicles}-vehicle run; exit={process.returncode}", flush=True)
            if process.returncode == 2: raise RuntimeError("Load preflight or execution incomplete")
        summary["status"] = "measured; review pending-age proxy, queryability and soak limitations before any approval"
    except Exception as error:
        summary["failure"] = type(error).__name__  # No secrets/provider diagnostics in shared output.
        summary["failureSite"] = [{"file": Path(frame.filename).name, "line": frame.lineno} for frame in traceback.extract_tb(error.__traceback__)]
        print("Fixture could not complete; inspect private evidence.", flush=True)
    finally:
        stop.set()
        if thread: thread.join(timeout=30)
        if collector: collector.shutdown()
        if compose.exists(): dc("down", "--timeout", "60", check=False)  # Preserve named volumes.
        (private / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
        print("Evidence: " + str(private / "summary.json"), flush=True)
    return 0 if len(summary["results"]) == 1 and all(item.get("passed") for item in summary["results"]) else 1


if __name__ == "__main__":
    sys.exit(main())
