# Queue metadata v1

`TelemetryBatchEnvelope` represents only the fields in `queue-envelope.schema.json`. Schema version
is `1`; the optional contract version, when present, is `v1`. JSON names are explicit, required metadata
must be present on deserialization, and unknown fields are rejected. Call `Validate()` after decoding
or construction, including after a record copy, before trusting metadata. It validates transport
constants, identifiers, length limits, the compressed size from one byte through 8 MiB, and the
lowercase SHA-256 format.

Queue adapters map this metadata to provider headers/attributes and carry the original gzip payload
separately as native provider message-body bytes. JSON serialization is a metadata representation,
not the queue body format. The payload must never be added as a string, byte array, or Base64 field.
Adapters must verify the recorded byte count and SHA-256 against the received body; this metadata type
does not decompress, hash, allocate, or copy the payload. Publisher confirmation, completion, retries,
and provider-specific attribute conversion follow in their respective implementation tasks.

`receivedAt` uses `DateTimeOffset`; producers supply UTC receipt time. `traceId` and `batchId` remain
available for correlation. The diagnostic string hides metadata values, including the payload digest;
structured logging filtering remains the responsibility of the observability policy.
