import math
import unittest
from storage_projection_model import build, scaled_bytes


def example():
    tables = {"event_identity_guards": 20, "event_identity_evidence": 20, "ingestion_batches": 2,
              "line_processing_results": 21, "quarantined_rejections": 1, "audit_events": 2}
    return {"status": "component measurements complete; conditional projections only", "influxCount": 20,
            "validEvents": 20, "outcomes": {"accepted": 20, "rejected": 1, "replayed": 0}, "auditedDeniedReads": 2,
            "parquetFiles": [{"row_count": 22, "size_bytes": 440}],
            "before": {"pg": {t: {"rows": 0, "bytes": 100} for t in tables},
                       "disk": {s: {"allocated": 100} for s in ("quarantine", "rabbitmq", "loki")}},
            "after": {"pg": {t: {"rows": n, "bytes": 100 + n * 200} for t, n in tables.items()},
                      "disk": {s: {"allocated": 500} for s in ("quarantine", "loki")}},
            "queued": {"queue": {"messages": 2}, "disk": {"rabbitmq": {"allocated": 500}}},
            "compression": {"regular": {"gzip": 20}, "varied": {"gzip": 40}},
            "logs": {"lokiProbeLines": 10, "lines": 2, "bytes": 100}}


class ProjectionArithmeticTests(unittest.TestCase):
    def test_units_and_time_horizons(self):
        self.assertEqual(scaled_bytes(1, 5000, 60), 25920000000)
        self.assertEqual(scaled_bytes(1, 5000, 1825), 788400000000)
        self.assertEqual(scaled_bytes(100, 5000, 30), 1296000000000)
        self.assertEqual(scaled_bytes(50, 5000, 7), 151200000000)

    def test_rejects_missing_infinite_and_overflow(self):
        for value in (None, 0, -1, math.inf, math.nan):
            with self.subTest(value=value), self.assertRaises(ValueError):
                scaled_bytes(value, 5000, 60)
        with self.assertRaises(ValueError):
            scaled_bytes(1e308, 5000, 1825)

    def test_short_retention_does_not_become_five_year_retention(self):
        rows = build(example(), 5000)["components"]
        for name in ("event_identity_evidence", "ingestion_batches", "line_processing_results", "quarantined_rejections", "quarantineFiles", "lokiAllocatedColdProbe", "queueAllocatedColdSample"):
            with self.subTest(name=name):
                self.assertEqual(rows[name]["localBytes"], rows[name]["futureBytes"])
        self.assertAlmostEqual(rows["event_identity_guards"]["futureBytes"] / rows["event_identity_guards"]["localBytes"], 1825 / 60)

    def test_missing_measurements_remain_missing(self):
        data = example()
        data["queued"]["disk"]["rabbitmq"]["allocated"] = 100
        result = build(data, 5000)
        self.assertFalse(result["coverageComplete"])
        self.assertIn("queueAllocatedColdSample", result["unmeasured"])
        self.assertNotIn("queueAllocatedColdSample", result["components"])
        self.assertFalse(result["productionCapacityApproved"])

    def test_reconciliation_and_completion_are_required(self):
        for change in ("count", "rows", "status"):
            data = example()
            if change == "count":
                data["influxCount"] -= 1
            elif change == "rows":
                data["after"]["pg"]["event_identity_guards"]["rows"] -= 1
            else:
                data.pop("status")
            with self.subTest(change=change), self.assertRaises(ValueError):
                build(data, 5000)

    def test_queue_payload_is_not_added_twice(self):
        result = build(example(), 5000)
        expected = sum(row["localBytes"] for key, row in result["components"].items() if key != "queueValidPayloadOnly")
        self.assertEqual(result["illustrativeSumBytes"]["local"], expected)


if __name__ == "__main__":
    unittest.main()
