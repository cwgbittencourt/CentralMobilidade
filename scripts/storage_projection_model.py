"""Conditional T095 arithmetic. Inputs must be measured; this never approves a deployment."""
import argparse
import hashlib
import json
import math
from pathlib import Path

DAY = 86400


def scaled_bytes(bytes_per_unit, units_per_second, days):
    if any(not isinstance(v, (int, float)) or not math.isfinite(v) or v <= 0 for v in (bytes_per_unit, units_per_second, days)):
        raise ValueError("Positive finite measurement, rate and retention required")
    value = bytes_per_unit * units_per_second * days * DAY
    if not math.isfinite(value):
        raise ValueError("Projection overflow")
    return value


def build(measurement, valid_events_per_second, audit_reads_per_second=0.1):
    if measurement.get("status") != "component measurements complete; conditional projections only":
        raise ValueError("Completed measurement required")
    n = measurement["influxCount"]
    if n != measurement["validEvents"] or n <= 0 or measurement["outcomes"]["replayed"] != 0:
        raise ValueError("Reconciled independent event count required")
    bad = measurement["outcomes"]["rejected"]
    if bad <= 0 or measurement["auditedDeniedReads"] <= 0:
        raise ValueError("Positive rejection and audit samples required")
    before, after = measurement["before"], measurement["after"]
    components, missing = {}, []

    def add(name, byte_delta, units, rate, retention):
        if byte_delta <= 0 or units <= 0:
            missing.append(name)
            return
        ratio = byte_delta / units
        components[name] = {"measuredBytes": byte_delta, "measuredUnits": units, "bytesPerUnit": ratio,
                            "unitsPerSecondAssumed": rate, "localBytes": scaled_bytes(ratio, rate, retention or 60),
                            "futureBytes": scaled_bytes(ratio, rate, retention or 1825), "retentionDays": retention,
                            "note": "fixed retention" if retention else "60/1825-day horizon, not approved audit policy"}

    parquet = measurement["parquetFiles"]
    parquet_bytes = sum(row["size_bytes"] for row in parquet)
    parquet_rows = sum(row["row_count"] for row in parquet)
    if parquet_rows < n + 2:
        raise ValueError("Complete sample Parquet persistence required")
    add("telemetryParquet", parquet_bytes, n + 2, valid_events_per_second, 0)
    # Current retention window is 60d. Only the future hypothesis changes it to 1825d.
    components["telemetryParquet"]["note"] = "60-day local / 1825-day future telemetry hypothesis"
    pg_windows = {"event_identity_guards": 0, "event_identity_evidence": 30,
                  "ingestion_batches": 30, "line_processing_results": 30, "quarantined_rejections": 30, "audit_events": 0}
    for table, days in pg_windows.items():
        rows = after["pg"][table]["rows"] - before["pg"][table]["rows"]
        delta = after["pg"][table]["bytes"] - before["pg"][table]["bytes"]
        expected = {"event_identity_guards": n, "event_identity_evidence": n,
                    "ingestion_batches": measurement["queued"]["queue"]["messages"],
                    "line_processing_results": n + bad, "quarantined_rejections": bad,
                    "audit_events": measurement["auditedDeniedReads"]}[table]
        if rows != expected:
            raise ValueError("SQL sample row counts do not reconcile")
        rate = audit_reads_per_second if table == "audit_events" else valid_events_per_second * rows / n
        add(table, delta, rows, rate, days)
    if "event_identity_guards" in components:
        components["event_identity_guards"]["note"] = "guard lifetime follows 60/1825-day telemetry hypothesis"
    for name, store, units, rate, days in (
        ("quarantineFiles", "quarantine", bad, valid_events_per_second * bad / n, 30),
        ("queueAllocatedColdSample", "rabbitmq", measurement["queued"]["queue"]["messages"], valid_events_per_second * measurement["queued"]["queue"]["messages"] / n, 7),
        ("lokiAllocatedColdProbe", "loki", measurement["logs"]["lokiProbeLines"], valid_events_per_second * measurement["logs"]["lines"] / n, 30)):
        end = measurement["queued"]["disk"] if store == "rabbitmq" else after["disk"]
        add(name, end[store]["allocated"] - before["disk"][store]["allocated"], units, rate, days)
    valid_gzip = sum(row["gzip"] for row in measurement["compression"].values())
    add("queueValidPayloadOnly", valid_gzip, n, valid_events_per_second, 7)
    add("stdoutPayloadPlanningCopy", measurement["logs"]["bytes"], n, valid_events_per_second, 30)
    # Do not add payload again on top of the observed broker footprint.
    total_local = sum(row["localBytes"] for key, row in components.items() if key != "queueValidPayloadOnly")
    total_future = sum(row["futureBytes"] for key, row in components.items() if key != "queueValidPayloadOnly")
    return {"validEventsPerSecondAssumed": valid_events_per_second, "rejectionsPerValidEventAssumed": bad / n,
            "auditReadsPerSecondAssumed": audit_reads_per_second, "fiveYearsDefinitionDays": 1825,
            "components": components, "unmeasured": missing, "coverageComplete": not missing,
            "illustrativeSumBytes": {"local": total_local, "future": total_future},
            "productionCapacityApproved": False,
            "limitations": ["Cold allocations are not validated marginal costs", "No aged retention/compaction/cleanup soak",
                            "Audit horizon and 30-day stdout copy are planning assumptions, not configured policy",
                            "Excludes backups, replicas, WAL archive, temp space, free-space margin, cloud versions/soft delete and host logs"]}


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("measurement", type=Path)
    args = parser.parse_args()
    evidence = json.loads(args.measurement.read_text(encoding="utf-8"))
    # Reconstruct the synthetic body with the recorded Parquet event epoch. No raw payload is printed.
    from unittest.mock import patch
    from storage_capacity_measurement import corpus
    epoch_ms = min(row["min_time"] for row in evidence["parquetFiles"]) // 1000000
    reconstructed = {group: {"raw": 0, "gzip": 0, "events": 0} for group in ("regular", "varied")}
    with patch("storage_capacity_measurement.time.time", return_value=epoch_ms / 1000 + 600):
        for raw, payload, group in corpus(args.measurement.parent.name):
            reconstructed[group]["raw"] += len(raw)
            reconstructed[group]["gzip"] += len(payload)
            reconstructed[group]["events"] += 500
    if reconstructed != evidence["compression"]:
        raise ValueError("Corpus compression evidence does not reproduce")
    output = args.measurement.with_name("projections.json")
    output.write_text(json.dumps({"measurementSha256": hashlib.sha256(args.measurement.read_bytes()).hexdigest(),
                                  "modelSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                                  "corpusEpochMilliseconds": epoch_ms, "corpusReproduced": True,
                                  "baseline": build(evidence, 500), "target": build(evidence, 5000)}, indent=2), encoding="utf-8")
    saved = json.loads(output.read_text(encoding="utf-8"))
    if not saved["baseline"]["coverageComplete"] or not saved["target"]["coverageComplete"]:
        print("Incomplete component coverage; inspect unmeasured entries. No capacity result.")
        raise SystemExit(1)
    print("Conditional projections saved; no production capacity approval.")
