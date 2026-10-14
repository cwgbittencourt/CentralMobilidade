# ADR-001 — Atomic leased identity guard and separate fingerprint evidence

Date: 2026-10-14. Status: selected for development, with production capacity gates outstanding.

## Decision

Use PostgreSQL with a unique `(company, vehicle, event_ms)` identity and 16 hash
partitions by vehicle for the initial leased identity guard. Keep diagnostic fingerprint
evidence in an independently retained table for 30 days. Use bounded bulk transactions;
the measured target requires batches of 1,000 rather than individual commits or the
100-row pilot. The provider-neutral `IEventIdentityGuard` contract remains authoritative.

This selects the T047 implementation direction. It does not approve single-host
five-year capacity, the 8-vCPU reference load gate or a production release. T047 must
repeat the throughput/correctness checks against its actual schema and adapter;
T042 proves failure boundaries with real processing, and T091/T095 prove sustained
end-to-end performance and storage. The benchmark's simplified SQL is not a production
adapter and must not be copied without the full port's owner/line/location semantics.

## Atomicity and recovery rules

1. Reserve with unique-key insertion, never an unprotected read-before-write lookup.
   A racing writer cannot replace the existing owner/content.
2. The owner may resume an incomplete reservation. Another owner must wait for lease
   expiry and use compare-and-swap on the previous fencing version to take over.
3. Every renewal, transition and commit must compare the full identity, owner, fence
   and applicable lease/state. Reject old owners/fences after takeover.
4. Pin the original canonical fingerprint/content before permitting a write or resume.
   Takeover cannot authorize different telemetry. Evidence and reservation changes
   must be transactional; a conflicting second event preserves the original.
5. Commit the guard only after reconciled Influx persistence. PostgreSQL cannot commit
   atomically with Influx: a crash may repeat the same physical write, never authorize
   a changed logical event. SQL fencing alone does not fence the Influx network call.
6. Keep committed guard existence until the telemetry expires. Delete diagnostic
   fingerprint evidence after 30 days without deleting the guard. After evidence
   expiry, reject an existing identity without promising replay/conflict classification.
   Do not retain a second canonical digest inside a guard beyond that deadline.
7. Promotion is a separate fenced location change for a committed pending identity.
   It is not a new identity or permission to change the original fingerprint.

## Experiment

`tests/CentralMobilidade.Telemetry.LoadTests/Scenarios/IdentityGuardBenchmarkTests.cs`
starts isolated PostgreSQL 17.11 in Docker and verifies `fsync=on` and
`synchronous_commit=on`. It compares plain unique index, 16 hash partitions and 16
range partitions. Each candidate receives 50,000 new identities drawn from the
100,000-vehicle synthetic corpus, then a separate 50,000-row evidence measurement.
Input is paced at 5,000 events/s; measured wall time includes acquisition/transaction
work and pacing. Guard batches reserve, mark Writing and commit in one durable SQL
transaction. Latencies describe a database batch, not end-to-end telemetry latency
or an amortized per-event latency. No Influx, queue or visibility calls are included.

The correctness probe runs 16 concurrent reservations, 16 takeover attempts and
16 conflicting inserts. It verifies one winner, owner resume, rejection before expiry,
fingerprint conflict rejection, one fenced takeover, old-owner rejection and committed
immutability. These are prototype SQL guarantees; T042 remains necessary to prove
the deployed state machine across provider failures.

The range pilot routes this current-epoch corpus to a single active partition; other
partitions remain empty. It is not evidence of aged/multi-day retention performance.
Hash distributes keys. Measurements include heap/index allocation and transient update
bloat, without VACUUM normalization. Small-sample bytes/row can vary between runs.
The prototype includes a 32-byte digest on guard rows to test pinned-content behavior;
that extra persistent field is not approved for the final guard beyond the 30-day
diagnostic window. Actual port fields, metadata, promotions and evidence cleanup will
change cost and must be remeasured.

## Measured evidence

Runs on Windows with 16 visible processors through Docker Desktop. This is not the
8-vCPU/16-GiB application reference profile. Raw JSON is retained under ignored
`artifacts/validation/T041/<UTC timestamp>-<UUID>/benchmark.json`; a small evidence
snapshot beside this ADR preserves the comparison for review.

The first durable 100-row pilot achieved only about 2,069–2,111 guard events/s.
Evidence alone met 5,000/s. The subsequent 1,000-row pilot met 5,000/s for all three
guards. This demonstrates sensitivity to batching and does not establish headroom:
the generator is paced rather than a saturation benchmark. Final run values are
recorded in `001-conflict-detector-results.json` (three completed runs).

Final 1,000-row run, 50,000 events per table:

| Candidate | Guard events/s | Guard batch p95 / p99 ms | Guard bytes/row | Evidence events/s | Evidence batch p95 / p99 ms | Evidence bytes/row |
|---|---:|---:|---:|---:|---:|---:|
| Plain | 5,079 | 118.41 / 133.94 | 663.55 | 5,096 | 49.54 / 65.54 | 228.23 |
| Hash16 | 5,077 | 66.65 / 76.30 | 740.88 | 5,090 | 11.70 / 52.61 | 253.95 |
| Range16 | 5,081 | 62.70 / 64.99 | 672.89 | 5,094 | 25.02 / 52.57 | 236.09 |

Range and hash latency rankings changed between short runs. Hash is selected for
explicit distribution of vehicle keys, and lower guard latency than the plain table;
it is not claimed to dominate range or minimize allocation. The single-active-range
experiment cannot resolve aged partitions or time-retention performance. Five load-project
tests pass: four corpus checks and the final durable comparison/correctness gate.

## Storage projections and release limits

At 5,000 distinct events/s: 30 days = 12.96 billion identities; 60 days = 25.92 billion;
five 365-day years = 788.4 billion. Guard lifetime follows telemetry lifetime, while
diagnostic evidence has only the 30-day window. The report projects both tables
separately to expose their costs; five-year evidence retention is a hypothetical
comparison, not an approved policy.

Linear allocation extrapolation excludes replication, backups, WAL retention,
operational free space, telemetry data, different key widths and aged-index costs.
It is not a disk-sizing promise. Even the pilot implies multiple TB for evidence and
tens of TB for 60-day guards; five-year guards approach hundreds of TB. The development
Compose machine cannot be declared sufficient for that footprint. Release requires
measured retention cleanup, storage provisioning/sharding decisions and capacity tests.

For the selected hash prototype, guard projections are 19.20 TB for 60 days and
584.11 TB for five years; 30-day fingerprint evidence is 3.29 TB (decimal units).
These are independent table extrapolations, not measured aged datasets. Combined
guard plus evidence throughput was not measured; measuring each at 5,000/s does
not prove a combined transaction pipeline at that rate.

## Alternatives and consequences

- Plain table: simplest deployment, met the short batched target, but no partition
  distribution and higher measured guard batch latency than hash in the 1,000-row pilot.
- Hash partitions: selected for the initial indexed concurrency/latency experiment;
  expiration requires bounded indexed deletes rather than dropping time partitions.
  Its measured allocation is not consistently smaller. Partition count must remain
  configuration/migration controlled rather than dynamically changing key rules.
- Range partitions: attractive for dropping expired time windows, but aged partitions,
  late data and unequal telemetry expiry are untested. It is not selected solely on
  a one-active-partition pilot. A future hybrid requires its own benchmark/migration ADR.
- External atomic store: not benchmarked, so not selected. Adding a new durable service
  would require the same resume/lease/fence/immutability proofs and measured storage.
- Influx lookup or merge semantics, and Bloom filters alone: cannot enforce atomic
  first-writer preservation and are not authoritative write guards.

## Reproduction

### T047 actual adapter revalidation (2026-10-14)

The production per-identity adapter now uses the migrated hash16 table, atomic
reservation/evidence transaction, row locks, stable batch/line owner and fenced
leases. Writing and commit are separate compare-and-swap updates; the processing
handler must call commit only after confirmed Influx persistence (T051). Promotion
uses its own fenced lease and preserves the original evidence. The migration copies
existing rows and table grants and restores invariant/truncate protections, including
direct leaf truncation protection. It requires an exclusive lock and a full table
copy; schedule migration downtime and capacity before deployment.

Nine guard scenarios pass, including the six T042 PostgreSQL cases, promotion
takeover and upgrading populated storage. The actual adapter short probe uses
1,000 identities, concurrency 32, and reserve + Writing + Committed for each identity,
with separate evidence and no Influx. Observed runs reached approximately 393–595
events/s, p95 60–120 ms and p99 666–670 ms. This **misses 5,000 events/s** and does
not inherit the bulk SQL prototype's throughput. The per-identity contract currently
has no bulk operation. Bulk orchestration/adapter optimization and a repeated actual
adapter benchmark are required before capacity approval, in addition to T091/T095.
Passing correctness tests does not pass this performance gate.

Reproduce the actual probe with `dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests --filter FullyQualifiedName~SelectedEventIdentityGuardTests`.
The local ignored report is `artifacts/validation/T047/adapter-benchmark.json`.

```powershell
dotnet test tests/CentralMobilidade.Telemetry.LoadTests --filter FullyQualifiedName~IdentityGuardBenchmarkTests
```

The test retains JSON even if throughput fails and requires at least one candidate to
meet 5,000 measured events/s. The corpus and benchmark are synthetic and isolated;
containers are disposed and no Compose volumes are modified.

## Amendment 2026-10-14: Single telemetry destination

Constitution 2.0.0 and FR-019 supersede promotion/dual-location decisions above. Earlier measurements
and implementation reports are historical evidence, not instructions to retain promotion. T104-T112
remove promotion APIs/leases and pending destinations through a safe forward migration. Keep
Reserved/Writing/Committed, owner recovery, fencing, retention and immutable logical identity.
This simplification does not resolve the recorded throughput shortfall or approve target capacity.
