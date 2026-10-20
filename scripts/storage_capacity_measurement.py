"""T095 bounded, isolated sample invoked only by Run-CapacityGates.py. No existing data reused."""
import gzip
import json
import math
import random
import time
import urllib.request
import urllib.error
import uuid

DAY = 86400


def project(bytes_per_unit, units_per_second, days):
    """Decimal bytes; refuse missing/zero/invalid measurements instead of inventing capacity."""
    if any(not isinstance(v, (int, float)) or not math.isfinite(v) or v <= 0 for v in (bytes_per_unit, units_per_second, days)):
        raise ValueError("Positive finite measurement/rate/retention required")
    return bytes_per_unit * units_per_second * days * DAY


def corpus(run, count=20000, batch_size=500):
    if count <= 0 or batch_size <= 0 or count % batch_size:
        raise ValueError("Whole positive batches required")
    rng = random.Random(95)
    epoch = (int(time.time()) - 600) * 1000
    for start in range(0, count, batch_size):
        lines = []
        for n in range(start, start + batch_size):
            # Half a regular low-entropy corpus; half varied numeric/optional fields.
            fields = "latitude=1,longitude=2,velocidade=3,rpm=4i,ignicao=true"
            if n >= count // 2:
                fields = f'latitude={rng.uniform(-80,80):.7f},longitude={rng.uniform(-170,170):.7f},velocidade={rng.uniform(0,130):.3f},rpm={rng.randrange(600,5000)}i,ignicao={"true" if n % 2 else "false"},placa="SYN{n:07}"'
            lines.append(f"telemetria,empresa_id=storage-{run}-{n % 100},veiculo_id=v{n},motorista_id=m{n} {fields} {epoch}\n")
        raw = "".join(lines).encode()
        yield raw, gzip.compress(raw, compresslevel=1, mtime=0), "regular" if start < count // 2 else "varied"


def measure(private, dc, sql, get, api_url, producer_token, influx_port, influx_token, rabbit_port, rabbit_auth, port):
    started = time.time()
    result = {"schemaVersion": 1, "startedUtcEpoch": started, "validEvents": 20000, "rejectedEvents": 100,
              "batchSize": 500, "productionCapacityApproved": False, "projections": {}}

    def save():
        (private / "storage.json").write_text(json.dumps(result, indent=2), encoding="utf-8")

    def request(url, data=None, headers=None, method=None):
        with urllib.request.urlopen(urllib.request.Request(url, data=data, headers=headers or {}, method=method), timeout=30) as response:
            payload = response.read()
            try:
                value = json.loads(payload) if payload else None
            except json.JSONDecodeError:
                value = payload.decode("utf-8")
            return response.status, value

    def query(q):
        return get(f"http://127.0.0.1:{influx_port}/api/v3/query_sql", "Bearer " + influx_token, {"db": "telemetry", "q": q, "format": "json"})

    def disk():
        # Allocated bytes and apparent file length are distinct measures. Read-only observer mounts.
        return {name: {"allocated": int(dc("exec", "-T", "observer", "du", "-sk", "/observe/" + name).stdout.split()[0]) * 1024,
                       "apparent": int(dc("exec", "-T", "observer", "du", "-sb", "/observe/" + name).stdout.split()[0])}
                for name in ("postgres", "rabbitmq", "influx", "quarantine", "loki")}

    def pg():
        output = {}
        for table in ("event_identity_guards", "event_identity_evidence", "ingestion_batches", "line_processing_results", "quarantined_rejections", "audit_events"):
            size = sql(f"WITH RECURSIVE rels(oid) AS (SELECT '{table}'::regclass::oid UNION ALL SELECT i.inhrelid FROM pg_inherits i JOIN rels r ON i.inhparent=r.oid) SELECT coalesce(sum(pg_total_relation_size(r.oid)),0) FROM rels r JOIN pg_class c ON c.oid=r.oid WHERE c.relkind='r'")
            output[table] = {"rows": int(sql(f"SELECT count(*) FROM {table}")), "bytes": int(size)}
        output["walLsn"] = sql("SELECT pg_current_wal_lsn()")
        return output

    def logs():
        return {service: dc("logs", "--no-color", "--no-log-prefix", service).stdout for service in ("api", "worker")}

    try:
        dc("run", "--rm", "--no-deps", "--user", "0", "--entrypoint", "/bin/busybox", "loki", "sh", "-c", "/bin/busybox mkdir -p /loki && /bin/busybox chown 10001:10001 /loki")
        dc("up", "-d", "loki")
        loki_url = f"http://127.0.0.1:{port('loki', 3100)}"
        for _ in range(90):
            try:
                request(loki_url + "/ready")
                break
            except Exception:
                time.sleep(1)
        else:
            raise RuntimeError("Loki not ready")
        # Waiting for all warmup outcomes prevents them entering sample denominators.
        for _ in range(120):
            if int(sql('SELECT count(*) FROM ingestion_batches WHERE "State" NOT IN (\'Completed\',\'CompletedWithRejections\',\'Failed\')')) == 0:
                break
            time.sleep(1)
        else:
            raise RuntimeError("Warmup not terminal")
        result["durability"] = sql("SELECT current_setting('fsync') || '/' || current_setting('synchronous_commit')")
        if result["durability"] != "on/on":
            raise RuntimeError("Durable SQL settings required")
        result["before"] = {"pg": pg(), "disk": disk()}
        baseline_logs = logs()
        dc("stop", "--timeout", "60", "worker")
        receipts, compression = [], {key: {"raw": 0, "gzip": 0, "events": 0} for key in ("regular", "varied")}
        run = private.name
        batches = list(corpus(run))
        bad = "".join(f"telemetria,empresa_id=storage-{run}-0,veiculo_id=reject{n} forbidden=1 {int(time.time()) * 1000}\n" for n in range(100)).encode()
        for raw, payload, group in batches + [(bad, gzip.compress(bad, compresslevel=1, mtime=0), "rejected")]:
            status, receipt = request(api_url + "/api/v1/telemetry/batches", payload,
                {"Authorization": "Bearer " + producer_token, "X-Contract-Version": "v1", "Content-Type": "text/plain; charset=utf-8", "Content-Encoding": "gzip"})
            if status != 202 or receipt.get("state") != "accepted":
                raise RuntimeError("Valid durable receipt required")
            receipts.append(receipt)
            if group in compression:
                compression[group]["raw"] += len(raw)
                compression[group]["gzip"] += len(payload)
                compression[group]["events"] += 500
        result["compression"] = compression
        result["queued"] = {"disk": disk(), "queue": get(f"http://127.0.0.1:{rabbit_port}/api/queues/telemetry/telemetry.batches.queue", rabbit_auth)}
        # Management statistics update asynchronously; exact count is required before deriving ratios.
        for _ in range(30):
            queue = get(f"http://127.0.0.1:{rabbit_port}/api/queues/telemetry/telemetry.batches.queue", rabbit_auth)
            if queue.get("messages") == len(receipts):
                result["queued"]["queue"] = {k: queue.get(k) for k in ("messages", "messages_ready", "messages_unacknowledged", "message_bytes", "message_bytes_ready")}
                break
            time.sleep(1)
        else:
            raise RuntimeError("Queue count mismatch")
        print("T095: confirmed paused-worker queue sample; draining production worker.", flush=True)
        save()
        dc("up", "-d", "worker")
        totals = {"accepted": 0, "rejected": 0, "replayed": 0}
        pending = list(receipts)
        deadline = time.monotonic() + 600
        while pending and time.monotonic() < deadline:
            remaining = []
            for receipt in pending:
                state = get(api_url + "/api/v1/telemetry/batches/" + receipt["batchId"], "Bearer " + producer_token)
                if state["state"] in ("completed", "completed_with_rejections"):
                    for key in totals:
                        totals[key] += state[key + "Lines"]
                elif state["state"] in ("failed", "expired"):
                    raise RuntimeError("Sample failed")
                else:
                    remaining.append(receipt)
            pending = remaining
            if pending:
                time.sleep(2)
        if pending or totals != {"accepted": 20000, "rejected": 100, "replayed": 0}:
            raise RuntimeError("Sample did not reconcile")
        result["outcomes"] = totals
        # A separate, explicitly denied administrative sample measures append-only audit rows.
        # No identity/raw data are disclosed; audit failure must not be mistaken for a successful denial.
        for _ in range(100):
            try:
                request(api_url + "/api/v1/telemetry/batches/" + receipts[0]["batchId"] + "/rejections/" + str(uuid.UUID(run)) + "/content")
                raise RuntimeError("Unauthenticated raw read unexpectedly allowed")
            except urllib.error.HTTPError as error:
                if error.code != 401:
                    raise RuntimeError("Denied audit sample failed") from None
        result["auditedDeniedReads"] = 100
        count = query(f"SELECT count(*) AS n FROM telemetria WHERE empresa_id LIKE 'storage-{run}-%'")[0]["n"]
        if count != 20000:
            raise RuntimeError("Independent Influx count mismatch")
        result["influxCount"] = count
        print("T095: 20,000 valid points and 100 rejections reconciled; waiting for Parquet.", flush=True)
        # Full fixture has only this sample plus two warmup points. Require every point in Parquet,
        # not merely queryable from memory/WAL, before deriving a bytes/row ratio.
        for _ in range(120):
            parquet = query("SELECT * FROM system.parquet_files")
            if sum(row["row_count"] for row in parquet) >= 20002:
                result["parquetFiles"] = parquet
                break
            time.sleep(2)
        else:
            raise RuntimeError("Parquet persistence not observed")
        final_logs = logs()
        new_lines = {service: final_logs[service][len(baseline_logs[service]):].splitlines() for service in final_logs}
        result["logs"] = {"note": "application stdout only, not all host/backend logs", "bytes": sum(len((line + '\n').encode()) for lines in new_lines.values() for line in lines),
                          "lines": sum(len(lines) for lines in new_lines.values())}
        for service, text in final_logs.items():
            (private / (service + "-logs.txt")).write_text(text, encoding="utf-8")
        # Replay a bounded production-log corpus to amortize cold Loki chunk/index allocation.
        source_lines = [line for lines in new_lines.values() for line in lines]
        if not source_lines:
            raise RuntimeError("No log measurement")
        values = [[str(time.time_ns() + n), source_lines[n % len(source_lines)]] for n in range(2000)]
        request(loki_url + "/loki/api/v1/push", json.dumps({"streams": [{"stream": {"service_name": "telemetry-storage-probe"}, "values": values}]}).encode(), {"Content-Type": "application/json"})
        request(loki_url + "/flush", b"", method="POST")
        time.sleep(10)
        result["logs"]["lokiProbeLines"] = len(values)
        result["after"] = {"pg": pg(), "disk": disk()}
        result["walGeneratedBytes"] = int(sql(f"SELECT pg_wal_lsn_diff(pg_current_wal_lsn(), '{result['before']['pg']['walLsn']}')"))
        result["elapsedSeconds"] = time.time() - started
        result["status"] = "component measurements complete; conditional projections only"
        save()
        return result
    finally:
        save()  # Keep partial evidence if any required measurement fails.
