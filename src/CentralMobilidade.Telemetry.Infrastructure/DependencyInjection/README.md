# Queue provider composition (T081)

API and worker call `AddTelemetryQueue` with configuration and their actual host
environment. Only `RabbitMq` or `PubSub` is accepted (case insensitive). Missing,
unknown or invalid selected-provider configuration fails with a fixed, value-free
error. The inactive provider's settings are ignored. The selected settings are
snapshotted: change provider/endpoints by restarting both hosts, not by hot reload.
There is no fallback, dual publication or automatic cutover; T088 owns that procedure.
Application/domain ingestion and processing handlers remain provider neutral.

## RabbitMQ

Keep `Queue:Provider=RabbitMq` and provide `Queue:RabbitMq:Host`, `Port`,
`VirtualHost`, `Username` and a private `Password`. The shipped blank password is
intentional and must be supplied before startup. Topology, retries, prefetch and
timeouts are validated before consumption. Publishing creates its connection and
confirm-enabled channel asynchronously on first use; no synchronous DI network wait
is introduced. Runtime consumer recovery still handles transient broker outages.

The registered `IDurableBatchQueue` wraps the production publisher and consumer;
the worker obtains `IMonitoredBatchConsumer`. Connection/channel ownership remains
with the container and existing adapters. Existing topology names/volumes are not
changed, migrated or recreated by selecting this provider.

## Pub/Sub

Use the same settings in API and worker, with full resource names:

```json
{
  "Queue": {
    "Provider": "PubSub",
    "StartupTimeout": "00:00:10",
    "PubSub": {
      "ProjectId": "your-project",
      "UseEmulator": false,
      "Endpoint": "pubsub.googleapis.com:443",
      "Topic": "projects/your-project/topics/telemetry-batches",
      "Subscription": "projects/your-project/subscriptions/telemetry-worker",
      "DeadLetterTopic": "projects/your-project/topics/telemetry-dead",
      "DeadLetterSubscription": "projects/your-project/subscriptions/telemetry-dead-inspection",
      "ConcurrentDeliveries": 4
    }
  }
}
```

Provision both topics and both subscriptions before starting. Each subscription
must belong to its corresponding topic, be attached and, in managed mode, report
at least seven days of message retention. The dead subscription is mandatory so
confirmed dead-topic publications have a recipient. Startup validates existence
and binding through bounded, single-attempt GetTopic/GetSubscription RPCs; it does
not create or modify cloud resources. Grant metadata-read permissions as well as
the publish/consume/ACK permissions needed by the adapters. Startup metadata checks
do not prove publish permission, seven-day survival, IAM correctness or cloud SLA.

Managed mode permits the standard or regional `*-pubsub.googleapis.com` endpoint
on port 443. An owned HTTP/2 channel uses TLS certificate verification and scoped
[Application Default Credentials](https://docs.cloud.google.com/dotnet/docs/reference/help/client-configuration),
cached once per host with token refresh handled by the credential. No credential
JSON is accepted through queue options. Configure the workload's ADC privately.
The adapter owns no credentials; the composition root cancels/disposes its channel.
gRPC send/receive limits are explicitly 10 MiB to carry the bounded 8 MiB gzip
envelope and attributes, including messages beyond gRPC's usual 4 MiB receive limit.

For the local emulator, set `UseEmulator=true`, `Endpoint=localhost:<mapped-port>`
(or its Docker service host/port), and run with `Development` or `Testing`.
Managed Google endpoints cannot be used in this insecure mode. Emulator mode is
rejected in other environments. Its channel uses HTTP without ADC. Explicit
CallInvoker construction ignores ambient emulator routing variables: neither mode
falls back to the other. See Google's [emulator configuration guidance](https://docs.cloud.google.com/dotnet/docs/reference/help/emulators).
The emulator skips the unsupported managed retention check; successful startup
and tests remain session-only compatibility evidence, not production durability.

`ConcurrentDeliveries` defaults to four and permits 1–64. Each worker lane uses the
T080 unary-pull adapter with one outstanding bounded body; a bounded merge feeds
the existing worker processing slots. `Worker:Concurrency` cannot exceed this
capacity (or RabbitMQ Prefetch). Drain and publish budgets are taken from the
selected provider rather than RabbitMQ-specific keys. See the [adapter settings](../Queue/PubSub/README.md).
`Queue:StartupTimeout` defaults to ten seconds and permits 100 ms–one minute.

Subscription monitoring belongs to the active consumer session. Successful
GetSubscription RPCs on that same client and configured subscription refresh its
heartbeat only while binding remains correct and attached. All lanes must be
available. A lost subscription marks the worker unavailable, drains/cancels bounded
work and enters the existing recovery loop. Startup checks are separate: a missing
subscription at initial startup fails the host. Graceful shutdown permits durable
completion; forced shutdown releases originals without implicitly acknowledging.

## Validation and health boundaries

Nine real-emulator host cases cover the production HTTP intake through PostgreSQL,
Pub/Sub and worker to actual InfluxDB, mixed-batch results and worker readiness;
missing/misbound resources; two-slot concurrency under backpressure; graceful and
forced shutdown; subscription loss/recreation; and a valid gzip transport body over
4 MiB (a transport probe, not a valid Line Protocol workload). Thirty-three unit
cases cover configuration boundaries, isolation, lazy resolution, managed endpoint
policy, duplicate registration and snapshot behavior. The existing RabbitMQ host
and reusable queue tests remain part of regression.

Worker readiness continues to require real storage and consumer freshness.
Selecting a provider or passing metadata validation does not itself make API
publication readiness healthy: its existing `IDurablePublicationProbe` hook fails
closed when absent. This composition does not substitute a connectivity/metadata
probe for proof of actual durable publication and does not publish fake business
batches as health checks. Successful intake still requires the real provider's
confirmation before `202`. Managed-service permission/durability/capacity and the
cutover gates require separate evidence. Reports are ignored under
`artifacts/validation/T081/`; no existing deployment or cloud project is changed.
