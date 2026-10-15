# Batch metrics integration (T062)

`BatchMetricsPersistenceTests` uses migrated PostgreSQL and a real Prometheus
TestServer endpoint. It verifies eight concurrent creation/transition replays,
rejection replay and rollback of an overlapping conflict page. Only newly committed
state entries/rejections are counted, and deleting batch metadata preserves the
observed cumulative totals. Seeded producer/company/vehicle/batch/quarantine IDs
never appear in the scrape. The collection disables parallel execution because
OpenTelemetry instruments are process-global and exact sample counts must be
isolated from other tests. No production filter is relaxed for this isolation.

Two unit cases cover all 11 states/16 rejection codes with empty tag sets, rejection
of unknown code text and isolation of a failing measurement listener. Reports are
ignored under artifacts/validation/T062/. These counters are not a durable ledger,
a current-state gauge, a backfill of existing rows or a capacity benchmark.
