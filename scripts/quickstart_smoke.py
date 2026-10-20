"""T098 real HTTP smoke on the preparer's new UUID fixture. Never accesses a user deployment."""
import base64
import gzip
import hashlib
import json
import secrets
import time
import urllib.error
import urllib.request


def configure(config, run):
    identity = {"a": "qs-" + run + "-a", "b": "qs-" + run + "-b"}
    config["BatchInspection"] = {"CursorKey": base64.b64encode(secrets.token_bytes(32)).decode()}
    authentication = config["Authentication"]
    authentication.update({"Operators": [], "ViewerServices": [], "Profiles": []})
    for actor in ("scoped", "admin", "viewer"):
        prefix = "cm_v_" if actor == "viewer" else "cm_o_"
        token = prefix + base64.urlsafe_b64encode(secrets.token_bytes(32)).decode().rstrip("=")
        identity[actor] = token
        salt = secrets.token_bytes(16)
        record = {"CredentialId": actor, "ActorId": actor, "Enabled": True,
                  "Hash": {"LookupDigest": hashlib.sha256(token.encode()).hexdigest(), "Salt": base64.b64encode(salt).decode(),
                           "Verifier": base64.b64encode(hashlib.pbkdf2_hmac("sha256", token.encode(), salt, 600000, 32)).decode(), "Iterations": 600000}}
        permissions = ["telemetry.read"] if actor == "viewer" else ["telemetry.read", "batch.read", "quarantine.raw.read"]
        if actor == "admin":
            permissions.append("cross_company.read")
        profile = {"ActorId": actor, "Kind": "ViewerPresentationService" if actor == "viewer" else "Human", "Enabled": True,
                   "Permissions": permissions, "CompanyIds": [identity["a"]]}
        if actor == "viewer":
            profile["GrafanaOrganizationId"] = record["GrafanaOrganizationId"] = "2"
        authentication["ViewerServices" if actor == "viewer" else "Operators"].append(record)
        authentication["Profiles"].append(profile)
    return identity


def execute(private, dc, sql, get, api_url, producer, influx_port, influx_token, rabbit_port, rabbit_auth, worker_port, identity):
    result = {"startedUtcEpoch": time.time(), "passed": False, "scope": "small real HTTP/persistence/restart; no Grafana/100k/capacity proof"}

    def save():
        (private / "quickstart-smoke.json").write_text(json.dumps(result, indent=2), encoding="utf-8")

    def call(url, token=None, data=None, headers=None):
        h = dict(headers or {})
        if token:
            h["Authorization"] = "Bearer " + token
        try:
            response = urllib.request.urlopen(urllib.request.Request(url, data=data, headers=h), timeout=20)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            body = response.read()
            try:
                value = json.loads(body)
            except (json.JSONDecodeError, UnicodeDecodeError):
                value = body.decode("utf-8", errors="replace")
            return response.status, value, dict(response.headers)

    def assert_status(status, expected):
        if status != expected:
            raise RuntimeError("Unexpected HTTP outcome")

    def post(lines):
        code, receipt, headers = call(api_url + "/api/v1/telemetry/batches", producer, gzip.compress(lines.encode(), mtime=0),
            {"Content-Type": "text/plain; charset=utf-8", "Content-Encoding": "gzip", "X-Contract-Version": "v1"})
        assert_status(code, 202)
        if receipt["state"] != "accepted" or headers.get("Location") != receipt["statusUrl"]:
            raise RuntimeError("Invalid intake receipt")
        return receipt

    def await_state(receipt, expected, timeout=120):
        until = time.monotonic() + timeout
        while time.monotonic() < until:
            code, state, _ = call(api_url + receipt["statusUrl"], producer)
            assert_status(code, 200)
            if state["state"] == expected:
                return state
            if state["state"] in ("failed", "expired"):
                raise RuntimeError("Batch failed")
            time.sleep(1)
        raise RuntimeError("State deadline exceeded")

    def ready():
        for _ in range(90):
            try:
                status = call(api_url + "/health/ready")[0]
                result["lastReadinessHttpStatus"] = status
                if status == 200:
                    return
            except Exception:
                pass
            time.sleep(1)
        raise RuntimeError("API readiness deadline exceeded")

    stamp = (int(time.time()) - 60) * 1000
    def line(company, vehicle, speed=3, latitude=1, extra=""):
        return f"telemetria,empresa_id={company},veiculo_id={vehicle} latitude={latitude},longitude=2,velocidade={speed},rpm=4i,ignicao=true{extra} {stamp}\n"

    try:
        result["stage"] = "valid intake"
        ready()
        valid = post(line(identity["a"], "first") + line(identity["b"], "second"))
        initial = await_state(valid, "completed")
        if initial["acceptedLines"] != 2 or initial["rejectedLines"] != 0:
            raise RuntimeError("Valid batch counters mismatch")
        result["validBatch"] = {"batchId": valid["batchId"], "accepted": 2, "state": initial["state"]}
        result["stage"] = "partial acceptance and quarantine"
        partial = post(line(identity["a"], "third") + line(identity["a"], "unknown", extra=",unknown=1")
                       + line(identity["a"], "coordinate", latitude=91) + line(identity["a"], "first", speed=99))
        state = await_state(partial, "completed_with_rejections")
        if state["acceptedLines"] != 1 or state["rejectedLines"] != 3 or state["replayedLines"] != 0:
            raise RuntimeError("Partial batch counters mismatch")
        code, page, _ = call(api_url + partial["statusUrl"] + "/rejections", identity["scoped"])
        assert_status(code, 200)
        codes = sorted(item["code"] for item in page["items"])
        if codes != sorted(["LP_UNKNOWN_COLUMN", "LP_COORDINATE_OUT_OF_RANGE", "DUPLICATE_CONFLICT"]):
            raise RuntimeError("Rejection catalog mismatch")
        quarantine = sql(f'SELECT "QuarantineId" FROM quarantined_rejections WHERE "BatchId"=\'{partial["batchId"]}\' LIMIT 1')
        code, raw, _ = call(api_url + partial["statusUrl"] + "/rejections/" + quarantine + "/content", identity["scoped"])
        assert_status(code, 200)
        if not isinstance(raw, str) or not raw:
            raise RuntimeError("Raw content not returned")
        audited = sql('SELECT count(*) FROM audit_events WHERE "ActorId"=\'scoped\' '
                      'AND "Action"=\'ReadQuarantineContent\' AND "Outcome"=\'Allowed\'')
        if int(audited) < 1:
            raise RuntimeError("Quarantine access audit missing")
        result["partialBatch"] = {"batchId": partial["batchId"], "accepted": 1, "rejected": 3, "codes": codes, "quarantineReadAudited": True}
        result["stage"] = "direct presentation scopes"
        for actor, company, expected in (("viewer", "a", 200), ("viewer", "b", 404), ("scoped", "b", 404), ("admin", "b", 200)):
            code, _, _ = call(api_url + "/api/v1/presentation/telemetry/latest?empresaId=" + identity[company], identity[actor])
            assert_status(code, expected)
        result["directPresentationScope"] = "viewer A=200/B=404; scoped B=404; explicit cross-company admin B=200"
        result["stage"] = "restart recovery"
        dc("stop", "--timeout", "60", "influxdb")
        try:
            recovery = post(line(identity["a"], "recovered"))
            await_state(recovery, "retrying")
            assert_status(call(api_url + "/health/ready")[0], 200)
            for _ in range(30):
                queue = get(f"http://127.0.0.1:{rabbit_port}/api/queues/telemetry", rabbit_auth)
                if sum(q.get("messages", 0) for q in queue) >= 1:
                    break
                time.sleep(1)
            else:
                raise RuntimeError("Acknowledged batch absent from owned broker")
            code, metrics, _ = call(f"http://127.0.0.1:{worker_port}/metrics")
            assert_status(code, 200)
            (private / "retry-metrics.txt").write_text(metrics, encoding="utf-8")
            if "telemetry_queue_delivery_age_seconds" not in metrics or "retry" not in metrics.lower():
                raise RuntimeError("Retry/backlog metric families missing")
            dc("restart", "api", "worker")
            ready()
            code, saved, _ = call(api_url + recovery["statusUrl"], producer)
            assert_status(code, 200)
            if saved["state"] not in ("accepted", "processing", "retrying"):
                raise RuntimeError("Recovery status lost")
        finally:
            dc("up", "-d", "influxdb")
        final = await_state(recovery, "completed")
        if final["acceptedLines"] != 1:
            raise RuntimeError("Recovered count mismatch")
        points = get(f"http://127.0.0.1:{influx_port}/api/v3/query_sql", "Bearer " + influx_token,
                     {"db": "telemetry", "q": f"SELECT count(*) AS n FROM telemetria WHERE empresa_id LIKE 'qs-{private.name}-%'", "format": "json"})[0]["n"]
        if points != 4:
            raise RuntimeError("Independent final count mismatch")
        result["recovery"] = {"batchId": recovery["batchId"], "observedRetrying": True, "apiAndWorkerRestartedWhileInfluxStopped": True,
                              "finalState": final["state"], "independentLogicalPoints": points}
        result["passed"] = True
        result["stage"] = "complete"
        result["finishedUtcEpoch"] = time.time()
        return result
    finally:
        result["finishedUtcEpoch"] = time.time()
        save()
