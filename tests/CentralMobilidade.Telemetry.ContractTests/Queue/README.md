# Reusable durable queue contracts — T076

`DurableBatchQueueContractTests` is an abstract xUnit suite with 16 inherited cases.
It depends only on the application queue/delivery ports and envelope/error contracts,
not RabbitMQ or Pub/Sub SDK types. Its source is linked into IntegrationTests through
an explicit Compile item, preserving one copy of the assertions without referencing
one test assembly from another. ContractTests still builds the source, but its
ordinary test run does not execute provider operations from an abstract class.
Actual provider evidence comes from the concrete integration binding.

`RabbitMqReusableQueueContractTests` derives from it and supplies a real isolated
RabbitMQ 4.3.6 broker plus the production publisher/consumer/topology. The separate
publisher and consumer ports reflect the existing production types: this suite
does not pretend that the publisher's unsupported ConsumeAsync is implemented.

The inherited cases verify:

- The native provider body equals the exact original gzip bytes, with size/hash,
  all required envelope attributes, optional contractVersion presence/absence,
  caller-owned stream lifetime and a matching UTC publish receipt.
- Two batches retain distinct batch/trace/producer metadata on consumption, without
  requiring provider delivery order or provider-generated message IDs to match.
- Sending alone cannot return a receipt before confirmation; negative confirmation
  is Transient and missing confirmation is Ambiguous, with DURABILITY_UNCONFIRMED.
- Invalid schema, encoding, version, length or digest is Permanent/REQUEST_INVALID
  before publication. Pre-cancelled caller operations remain cancellation.
- Disposing an unsettled session permits redelivery without implicit ack; explicit
  retry preserves bytes/metadata and increases attempt. Implicit redelivery only
  requires an attempt >=1, since provider attempt tracking differs.
- Manual completion is idempotent and produces no delivery within a bounded quiet
  observation window. Dead-letter settlement preserves original bytes/correlation
  and a safe error code. This does not assert exactly-once delivery forever.

## Bind another provider

Derive a public class from the suite in the provider integration project and
implement `CreateAsync` with a fresh isolated resource namespace per case. Supply
the production `IDurableBatchQueue` publisher, real `IQueueDelivery` consumption,
native inspection and disposal through `DurableQueueContractHarness`. Observe
actual provider body/attributes; never reconstruct them from expected metadata.
Native inspection intentionally removes its observed message, so those tests are
separate from adapter round trips. Keep backend-specific persistence/content and
SDK correlation checks in the binding; RabbitMQ also checks Persistent, MessageId,
CorrelationId and content properties against the native attributes.

Provide bounded fault controls for HoldConfirmation, NegativeConfirmation and
MissingConfirmation at the SDK/transport confirmation seam. `PublishEntered` must
mean that seam has actually been reached; `ConfirmAsync` releases the held matching
confirmation. The Rabbit binding reuses the existing controlled channel interceptor:
normal operations go to the broker, while the three fault cases control SDK send/
confirm behavior. In the held-ACK case no real message is sent. These cases verify
the adapter's confirmation boundary and taxonomy, not a physical broker fault or
storage persistence. Keep that distinction in any future provider binding.

Do not introduce an in-memory queue implementation, skip unsupported cases or add
provider-name branches to the shared assertions. Fail missing behavior normally.
An ambiguous failure must not be interpreted as proof that no message was stored.
Only invalid/pre-cancelled operations assert absence in the native queue.

Run the implemented binding from the repository root:

```powershell
dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests --filter FullyQualifiedName~RabbitMqReusableQueueContractTests
```

The fixture reuses the existing RabbitMqContractBroker helper, with one broker for
this class and unique topology per test, cleaned after disposal. An optional private
`TELEMETRY_TEST_RABBITMQ` endpoint uses the same isolated topology and cleanup; do
not point tests at ordinary production resources. Credentials are not command arguments.
Polling/consumption/confirmation waits are bounded; no sleeps for queue retention
or claims of host-loss protection are made. Existing provider-specific topology,
restart and fault tests remain in IntegrationTests.

T077 binds the suite to the Pub/Sub emulator and T080 implements that adapter;
neither is supplied by T076. Emulator session state is not production durability,
and passing only the RabbitMQ binding is not provider parity. The separate restart,
cutover and final acceptance gates remain necessary. Reports are ignored under
`artifacts/validation/T076/`.
