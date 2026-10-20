"""T102 plumbing only: real production services/templates, no synthetic HTTP backends."""
import base64
from datetime import datetime, timedelta, timezone
import hashlib
import json
import os
import secrets
import shutil
import subprocess
import time
import urllib.request


def configure(config, run):
    token = "cm_v_" + secrets.token_urlsafe(32)
    salt = secrets.token_bytes(16)
    hashed = {"LookupDigest": hashlib.sha256(token.encode()).hexdigest(), "Salt": base64.b64encode(salt).decode(),
              "Verifier": base64.b64encode(hashlib.pbkdf2_hmac("sha256", token.encode(), salt, 600000, 32)).decode(), "Iterations": 600000}
    config["BatchInspection"] = {"CursorKey": base64.b64encode(secrets.token_bytes(32)).decode()}
    config["Authentication"].update({
        "ViewerServices": [{"CredentialId": "proof-viewer", "ActorId": "proof-viewer", "GrafanaOrganizationId": "2", "Hash": hashed}],
        "Profiles": [{"ActorId": "proof-viewer", "Kind": "ViewerPresentationService", "GrafanaOrganizationId": "2",
                      "CompanyIds": [f"fp-{run}-00"], "Permissions": ["telemetry.read"]}]})
    return {"viewer": token, "password": secrets.token_urlsafe(32), "renderer": secrets.token_urlsafe(32)}


def services(root, private, services, volumes, identity, influx):
    provisioning = private / "grafana-provisioning"
    shutil.copytree(root / "deploy/grafana/provisioning", provisioning)
    services["renderer"] = {"image": "grafana/grafana-image-renderer:v4.0.17", "shm_size": "512m",
                            "environment": {"AUTH_TOKEN": identity["renderer"], "HTTP_PORT": "8081"}}
    services["grafana"] = {"image": "centralmobilidade-grafana:12.2.5-infinity-4.1.1", "ports": ["127.0.0.1::3000"],
        "environment": {"GF_SECURITY_ADMIN_USER": "bootstrap", "GF_SECURITY_ADMIN_PASSWORD": identity["password"],
            "GF_AUTH_ANONYMOUS_ENABLED": "false", "GF_PLUGINS_PREINSTALL_DISABLED": "true",
            "GF_ANALYTICS_REPORTING_ENABLED": "false", "GF_ANALYTICS_CHECK_FOR_UPDATES": "false",
            "GF_RENDERING_SERVER_URL": "http://renderer:8081/render", "GF_RENDERING_CALLBACK_URL": "http://grafana:3000/",
            "GF_RENDERING_RENDERER_TOKEN": identity["renderer"], "INFLUXDB_GRAFANA_READ_TOKEN": influx},
        "volumes": ["grafana:/var/lib/grafana", f"{provisioning}:/etc/grafana/provisioning:ro",
            f"{root / 'deploy/grafana/dashboards/internal'}:/var/lib/grafana/dashboards/internal:ro",
            f"{root / 'deploy/grafana/dashboards/platform-operations.json'}:/var/lib/grafana/dashboards/platform/platform-operations.json:ro"]}
    volumes.append("grafana")


def execute(root, private, run, dc, port, api, influx_port, influx, producer, identity, sql):
    dc("up", "-d", "renderer", "grafana")
    grafana = f"http://127.0.0.1:{port('grafana', 3000)}"
    bootstrap = "Basic " + base64.b64encode(("bootstrap:" + identity["password"]).encode()).decode()

    def call(path, data=None, org=None):
        headers = {"Authorization": bootstrap}
        if org:
            headers["X-Grafana-Org-Id"] = str(org)
        if data is not None:
            headers["Content-Type"] = "application/json"
        request = urllib.request.Request(grafana + path, headers=headers, data=None if data is None else json.dumps(data).encode())
        with urllib.request.urlopen(request, timeout=30) as response:
            return json.loads(response.read())

    for attempt in range(90):
        try:
            if call("/api/health")["database"] == "ok":
                break
        except (OSError, ValueError):
            pass
        time.sleep(1)
    else:
        raise RuntimeError("Grafana did not become healthy")
    name = "T102 restricted viewer"
    if call("/api/orgs", {"name": name})["orgId"] != 2:
        raise RuntimeError("Unexpected organization identity")
    configuration = {"bootstrap": {"usernameEnv": "T102_BOOTSTRAP_USER", "passwordEnv": "T102_BOOTSTRAP_PASSWORD"},
        "readinessTimeoutSeconds": 60, "organizations": [{"name": name, "companyIds": [f"fp-{run}-00"], "members": [],
            "serviceAccount": {"name": "t102-reconciler", "role": "Admin"},
            "presentation": {"baseUrl": "http://api:8080", "credentialEnv": "T102_PRESENTATION", "permissions": ["telemetry.read"]},
            "datasourceUid": "viewer-api-proof", "dashboardDirectory": str(root / "deploy/grafana/dashboards/viewer")}]}
    config_path = private / "organizations.json"
    config_path.write_text(json.dumps(configuration), encoding="utf-8")
    env = dict(os.environ, T102_BOOTSTRAP_USER="bootstrap", T102_BOOTSTRAP_PASSWORD=identity["password"], T102_PRESENTATION=identity["viewer"])
    result = subprocess.run(["pwsh", "-NoProfile", "-NonInteractive", "-File", str(root / "scripts/Provision-GrafanaOrganizations.ps1"),
        "-GrafanaUrl", grafana, "-ConfigurationPath", str(config_path), "-StateDirectory", str(private / "grafana-state")],
        cwd=root, env=env, capture_output=True)
    (private / "reconcile.log").write_bytes(result.stdout + result.stderr)
    if result.returncode:
        raise RuntimeError("Grafana reconciliation failed; inspect private reconcile.log")

    def read_token(org):
        account = call("/api/serviceaccounts", {"name": "proof-reader", "role": "Viewer"}, org)
        return call(f"/api/serviceaccounts/{account['id']}/tokens", {"name": "proof", "secondsToLive": 3600}, org)["key"]

    evidence = root / "artifacts/validation/100k-final-proof" / run
    config = {"Api": api, "Influx": f"http://127.0.0.1:{influx_port}", "Grafana": grafana,
        "Namespace": run, "EventStart": (datetime.now(timezone.utc) - timedelta(hours=1)).isoformat(timespec="milliseconds"),
        "IsolatedTarget": True, "CompanyLookupUnavailable": True, "InternalOrganizationId": 1, "ViewerOrganizationId": 2,
        "DeadlineSeconds": 1800, "PollMilliseconds": 500, "ReportDirectory": str(evidence)}
    (private / "proof-config.json").write_text(json.dumps(config), encoding="utf-8")
    env = dict(os.environ, TELEMETRY_LOAD_PRODUCER_TOKEN=producer, TELEMETRY_PROOF_INFLUX_TOKEN=influx,
        TELEMETRY_PROOF_INTERNAL_GRAFANA_TOKEN=read_token(1), TELEMETRY_PROOF_VIEWER_GRAFANA_TOKEN=read_token(2))
    result = subprocess.run(["dotnet", "run", "--no-build", "--project", "tests/CentralMobilidade.Telemetry.LoadTests", "--",
        "--scenario", "final-100k-grafana", "--config", str(private / "proof-config.json")], cwd=root, env=env, capture_output=True)
    (private / "final-cli.log").write_bytes(result.stdout + result.stderr)
    dc("logs", "--tail", "100", "grafana", "renderer", "api", "worker")
    return {"passed": result.returncode == 0, "exitCode": result.returncode, "evidenceDirectory": str(evidence),
        "postgresDurability": sql("SELECT current_setting('fsync') || '/' || current_setting('synchronous_commit')"),
        "registryLookup": "No company registry configured or connected; producer has no company allowlist",
        "influxPrivilege": "InfluxDB Core admin-only token; fixture limitation, not least-privilege approval"}
