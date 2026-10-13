# API v1 transport models

These types match `specs/001-telemetry-ingestion/contracts/openapi.yaml`. JSON property names are
explicit, independent of serializer naming policy. Required response fields carry `JsonRequired`;
schemas with `additionalProperties: false` reject unknown properties on deserialization.

`BatchAccepted` is an output-only acknowledgement whose state is always `accepted`; construct it only
after durable confirmation. `BatchStatus` uses the seven externally visible states, with string-only
enum conversion and the exact `completed_with_rejections` spelling. Internal receiving/publishing
states are not exposed. Optional nonnullable arrays/objects (`errorSummary`, `links`) are omitted when
absent; explicitly nullable fields can be serialized as null.

Latest-position and trip endpoints return arrays of `TelemetryPosition`, and ingestion-health returns
an array of `IngestionHealthPoint`; no additional response envelope is introduced. Rejection metadata
does not contain raw lines, plate, coordinates, quarantine locations, or driver identity. Driver
presentation exposes only `driverId`, authorized `displayName`, and optional four-digit CPF suffix.
Driver and position diagnostic strings omit sensitive values; structured logging filtering still
belongs to T021.

The request records describe route, query, and header parameters, not JSON bodies. Intake accepts a
gzip stream separately, and endpoints must bind `X-Contract-Version`, `Content-Type`,
`Content-Encoding`, and `traceparent` explicitly to `IngestBatchHeaders`. The request annotations
describe individual OpenAPI bounds, including 128-character presentation identifiers, 512-character
latest-position cursors, 200 rejection items, 500 vehicles, 10,000 points, and 60-second minimum health
granularity. They do not run validation automatically. Handlers/endpoints must validate these models,
UTC ordering and configurable time windows, company scope, permissions, and audit requirements.
Response construction likewise must supply valid counters, identities, and UTC event timestamps.

These are transport records, not persistence entities or domain identifiers. Provider, authorization,
and processing logic remain outside the Contracts assembly.
