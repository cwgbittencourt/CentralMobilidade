# Pub/Sub batch adapter (T080)

`PubSubBatchQueue` implements `IDurableBatchQueue` using injected
`PublisherServiceApiClient`, `SubscriberServiceApiClient` and `IConfiguration`.
It never creates topics/subscriptions, discovers credentials, chooses an endpoint,
or shuts down injected clients/channels. Provider registration, authenticated cloud
versus explicit local emulator construction and application wiring belong to T081;
the running application still uses its existing RabbitMQ wiring.

## Publish and metadata

The body is the exact original gzip in `PubsubMessage.Data`, without JSON/base64
wrapping or recompression. One bounded publication at a time reads at most the
declared 8 MiB, checks length, gzip header and SHA-256, and leaves the caller's
stream open. Full gzip validation remains the worker's job. All nine required
envelope fields and optional `contractVersion` map to string attributes with
invariant numbers and UTC round-trip timestamps. The envelope's batch/trace IDs
are independent of the service-generated message ID.

A receipt requires a completed unary Publish RPC with exactly one nonblank
message ID. Every RPC has cancellation/deadline bounds and an explicit single
SDK attempt. Application retries are responsible for ambiguous results; no receipt
or failure proves that a timed-out message was not stored. Temporary body arrays
are cleared on release. There is no log of request data, SDK diagnostics or tokens.

## Pull and settlement

Each enumeration pulls one message and waits for its settlement/disposal before
pulling another, bounding its outstanding body to 8 MiB. Consumers must settle or
dispose the yielded delivery before requesting the next. The adapter validates
incoming metadata, size/hash/header, future timestamps and attempt values. Invalid
bounded envelopes, messages older than seven days and exhausted attempts go to the
explicit dead topic; an oversized body or absent ACK ID fails closed without ACK.
Unknown attributes are discarded on retry/dead publication. Malformed known
metadata may remain in the dead message for protected diagnosis; the dead topic
needs restricted access, and its contents must never enter ordinary observability.

Only `CompleteAsync` acknowledges a processed delivery. Retry retains the original
while waiting with bounded exponential jitter, publishes a copy with incremented
`deliveryAttempt`, waits for its confirmation and then acknowledges the original.
Explicit dead-letter follows the same ordering. At the attempt ceiling, retry
routes to dead with `RETRY_EXHAUSTED`; malformed metadata uses `INVALID_ENVELOPE`.
Only catalogued error codes and fixed adapter codes are admitted; unknown text,
including uppercase plate-like values, becomes `POISON_BATCH`.

Publishing and acknowledging are separate operations: a crash or lost ACK can
produce duplicate copies. Downstream idempotency is still required. Native
`ReceivedMessage.DeliveryAttempt` is best effort; the explicit attribute preserves
attempts across adapter-created retry copies, not every crash/nack redelivery.
This is an explicit adapter dead topic, not managed Pub/Sub dead-letter policy.

Initial `ModifyAckDeadline` and periodic renewal keep long processing leased.
Renewal failure or its maximum duration expires the local delivery: settlement
fails safely and consumption surfaces a sanitized provider failure. Processing
must be cancelled by the caller; the adapter never commits its application work.
Enumeration shutdown allows a bounded drain, then disposes outstanding delivery.
Disposal never acknowledges: it attempts deadline zero (NACK); if unavailable,
provider lease expiry permits redelivery. Adapter disposal cancels its operations;
injected clients remain owned by their composition root. Pub/Sub remains at-least-once,
including during a lease, and renewal is not an exactly-once guarantee. See Google's
[unary pull lease guidance](https://docs.cloud.google.com/pubsub/docs/lease-management).

## Adapter settings

All keys are under `Queue:PubSub`. Resource names must be full
`projects/{project}/topics/{topic}` or `projects/{project}/subscriptions/{subscription}`.
When supplied, `ProjectId` must match each resource; main and dead topics must differ.
Invalid adapter settings produce a fixed error without echoing values.

| Setting | Default | Bounds |
|---|---|---|
| Topic, Subscription, DeadLetterTopic | Required | Full names, up to 512 characters |
| PublishTimeout | 5 seconds | 1 ms–1 minute; also bounds settlement RPCs |
| PullTimeout | 30 seconds | 1 ms–1 minute |
| DrainTimeout | 30 seconds | 0–5 minutes |
| MaxAttempts | 10 | 1–1,000 |
| RetryDelay | 1 second | Positive, no larger than MaxRetryDelay |
| MaxRetryDelay | 1 minute | Up to 10 minutes; requested delays are capped here |
| AckDeadlineSeconds | 60 | 10–600 |
| LeaseRenewalInterval | 20 seconds | At least 100 ms, at most half the ACK deadline |
| MaxLeaseDuration | 30 minutes | At least one ACK deadline, at most one hour |

`Endpoint`, `UseEmulator` and any dead subscription are client/resource provisioning
concerns, not connection switches in this injected adapter. The composition root
must configure suitable message-size limits and validate topic/subscription binding,
IAM, TLS and seven-day retention in the real service. The local age check neither
configures broker retention nor proves survival after seven days.

## Safe failure mapping and evidence

| gRPC status | Kind |
|---|---|
| PermissionDenied, Unauthenticated, InvalidArgument, NotFound, FailedPrecondition, OutOfRange, Unimplemented | Permanent |
| Unavailable, ResourceExhausted, Aborted | Transient |
| DeadlineExceeded, Cancelled without caller cancellation, Internal, Unknown, other failures | Ambiguous |

RPC failures expose only `DURABILITY_UNCONFIRMED`; invalid envelopes expose
Permanent/`REQUEST_INVALID`. Caller cancellation stays cancellation. An empty pull
deadline is a polling boundary, not a failed publication or proof of worker health.
No inner SDK exception is retained in the public exception.

The [integration binding and controls](../../../../tests/CentralMobilidade.Telemetry.IntegrationTests/Queue/README.md#t080--adaptador-implementado)
run the same 16 reusable contracts as RabbitMQ, six publish error mappings, four
SDK controls and 14 additional safety cases against an isolated emulator. Thirty
unit cases target bounded settings/bytes, confirmation shape, cancellation, pull/
ACK taxonomy, lease failure and settlement lifecycle using controlled SDK clients.
They are transport controls, not an in-memory implementation of the queue port.
Emulator state is session-only; these tests do not establish cloud IAM, production
durability, seven-day retention, throughput or host-loss protection. Evidence is
ignored under `artifacts/validation/T080/`.
