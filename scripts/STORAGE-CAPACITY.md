# T095 — isolated storage measurement

Requires Python 3 (standard library only), PowerShell/.NET 10, restored local EF
tool and Docker/Compose. Run from the repository root:

```powershell
dotnet tool restore
dotnet build CentralMobilidade.slnx
docker build -f deploy/containers/api.Dockerfile -t centralmobilidade-api:t094 .
docker build -f deploy/containers/worker.Dockerfile -t centralmobilidade-worker:t094 .
docker build -f deploy/loki/Dockerfile -t centralmobilidade-loki:3.7.0-busybox-1.37.0 .
py -3 -m unittest discover -s scripts -p 'test_storage*.py' -v
py -3 scripts/Run-CapacityGates.py --storage-measurement
py -3 scripts/storage_projection_model.py artifacts/validation/T095/RUN_UUID/storage.json
```

Replace RUN_UUID with the directory printed by the measurement. `:t094` are the
shared preparer's local app-image aliases; rebuild from reviewed source, do not
assume a cached tag represents the checkout. The default command without the
storage switch keeps the T094 load/soak behavior. The storage switch runs a separate
UUID project, not a reference-rate load test, and never sets capacity approval.

The fixture uses current production API/worker, migrated PostgreSQL, RabbitMQ,
InfluxDB, filesystem quarantine, production log output and a separate Loki.
It confirms 40 500-event gzip batches plus one 100-line invalid batch with the
worker stopped, measures that queue, resumes processing and reconciles outcomes
and an independent Influx count. A separate 100 unauthenticated raw-read attempts
measures append-only audit rows; every response must be 401. No raw data are
disclosed. No real user data are used.

Parquet is measured only after system metadata covers all 20,000 sample points
and two warmup points. For a bounded experiment only, Influx uses 1-minute gen1
windows, 10 WAL files/snapshot and forced snapshot age 30s. Production defaults
are not changed. These parameters and small file density affect compression/cost.

Read-only observer mounts measure apparent and allocated file bytes separately.
SQL counts exact rows and sums physical heap/index/TOAST relations including
partition descendants; WAL generation is reported separately. Log payload growth
comes from actual API/worker stdout; 2,000 replayed production-log lines amortize
a cold Loki probe. The sample does not measure all host/backend log sources,
steady-state log rotation, retention deletion, aged indexes or compacted storage.

The projection model requires reconciled events/SQL rows, positive measured costs
and full Parquet coverage. Missing/nonpositive allocations remain unmeasured,
never become zero demand. It regenerates gzip using the recorded Parquet epoch,
run namespace and seed 95, requiring exact byte-count reproduction. `projections.json`
records source/model hashes and both 500/5,000-valid-event/s scenarios. Thirty-day
stores and the seven-day queue keep those windows in the future scenario; only
telemetry/guards grow to the explicitly defined 1,825-day horizon. Audit rate and
stdout retention are illustrative assumptions and require policy decisions.

The initial sum excludes double-counting queue payload on top of measured broker
allocation. It is **not a disk-sizing approval**: cold page/segment/chunk costs,
sample key widths, rejection rate, audit access and log verbosity can dominate.
Backups/replication, WAL archive, scratch space, filesystem free space, cloud
versions/soft delete and production age/cardinality need separate evidence.

Every configuration, raw log, token and measurement stays under ignored
`artifacts/validation/T095/<uuid>/`. Do not upload that directory; it contains
ephemeral credentials and possible sensitive diagnostic output. Only the
[reviewed report](../artifacts/performance/storage-capacity-report.md) is versioned.
Containers/networks created by the fixture are removed on success/failure without
deleting its evidence volumes. Existing deployments and T094 volumes are not reused.
