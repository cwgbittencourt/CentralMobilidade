# Telemetry intake endpoint

`POST /api/v1/telemetry/batches` requires the producer authentication policy, exactly one
`X-Contract-Version: v1`, exactly one `Content-Encoding: gzip`, and `text/plain` with one UTF-8
charset parameter. Validation happens before reading or publishing the body.

The endpoint uses `BoundedRequestSpool` for the configured compressed-byte and upload-duration limits,
then constructs the provider-neutral envelope from the authenticated producer, current trace, UUIDv7,
original gzip byte count, and streaming SHA-256. The application handler owns durable publication
ordering. A `202` response is emitted only after that handler returns and includes matching `Location`,
JSON `statusUrl`, and a positive `Retry-After` for the first status check.

`IntakeAdmissionGate` bounds concurrent admitted requests and never queues excess HTTP work. A full
gate returns `429 INTAKE_BACKPRESSURE` with `Retry-After` before body intake. Safe provider errors map
through the stable problem catalog; durability failures also carry `Retry-After`. Caller cancellation
continues as cancellation and never becomes `202`. The endpoint owns and disposes the temporary spool,
while the incoming request stream remains owned by ASP.NET Core.

T035 completes host-level Kestrel limits, trusted proxy processing, global Problem Details, provider
composition, and final middleware ordering.

## Batch status (T059)

`GET /api/v1/telemetry/batches/{batchId}` is mapped by the API host and delegates to
`GetBatchStatusHandler`. The submitting producer can read its batch. Human operators
need `batch.read` plus every batch company, or explicit `cross_company.read` in
addition to `batch.read`; incomplete scope requires the cross-company permission.
The handler rechecks the current operator profile.

The status authentication policy forwards to exactly one existing bearer validator.
Credential format only chooses the validator; hashes, enabled state, expiry and
profiles are still validated server-side. Missing/invalid credentials, disabled or
expired credentials and viewer-service credentials return one `401` Bearer challenge
with a catalog Problem JSON body. Authenticated readers without visibility receive
the same `404 RESOURCE_NOT_FOUND` as unknown/expired batches. Noncanonical or empty
UUIDs and pre-acceptance states also use that safe 404.

Success returns the versioned JSON status DTO, including counts and nullable
processing/completion/total fields, with `Cache-Control: no-store`. Error bodies
include the stable code and traceId; no identity, company scope or internal version
is added to the public DTO. Administrative links remain omitted until the T060
rejection routes are available. Caller cancellation is propagated to the handler.
No additional migration or cursor key is required for status; the T058 migration
must already be applied for the current persistence model.

`BatchStatusEndpointTests` validates 19 HTTP scenarios through ASP.NET TestServer,
real salted bearer validation, current operator profiles and migrated PostgreSQL.
