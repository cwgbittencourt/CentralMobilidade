# Provider ports

The application owns these provider-neutral interfaces and transport-independent operation records.
They reference the Domain value objects and versioned Contracts, never provider SDKs, persistence
entities, HTTP contexts, claims principals, or filesystem paths. All external operations require an
explicit cancellation token. Cancellation propagates as `OperationCanceledException`; other adapter
failures use `ProviderOperationException` with a catalogued code and transient/permanent/ambiguous
classification. Ambiguous means responsibility cannot be established and must never imply acceptance
or safe destructive cleanup. Raw SDK diagnostics remain inside the adapter's privacy-safe handling.

| Port | Required adapter guarantees |
| --- | --- |
| `IDurableBatchQueue` | Confirmation before a publish receipt; original gzip bytes in the native body; bounded consumption; explicit completion, retry, and dead-letter actions |
| `IBatchStateStore` | Replay-safe creation and line results; atomic version-checked legal transitions; privacy-safe paginated rejections; complete company-scope evidence |
| `IQuarantineStore` | Protected atomic content writes, bounded streaming, integrity digest, opaque references, deadline-checked idempotent deletion |
| `IEventIdentityGuard` | Atomic reservation/resumption, lease renewal, expired-owner takeover with fencing, durable commit protection, fenced pending-to-queryable promotion |
| `ITelemetryWriter` | Bounded gzip writes, millisecond precision, durable sync, source-line mapping for partial rejections, explicit destination |
| `ICompanyVisibilityResolver` | Tenant-scoped cadastral decisions; unknown/unavailable/inconclusive companies remain pending |
| `IDriverResolver` | Tenant-scoped minimal identity projection; no full CPF |
| `IOperatorAuthorizer` | Trusted operator identity, enabled state, permissions, server-resolved company scope and explicit cross-company authorization |
| `IAuditWriter` | Append-only, replay-safe, durable receipt; no sensitive disclosure if audit fails |
| `IClock` | UTC offset-zero clock that can be replaced in tests |

Publishing/writing callers retain stream ownership. A queue delivery owns its readable body until
disposed; disposal never acknowledges it. Quarantine read handles own their streams and expose no
storage path. Publish metadata and gzip body remain separate arguments. An adapter returns a durable
receipt only after confirmation; delivery completion occurs only after all telemetry/quarantine/batch
outcomes are persisted. Delay and retry policies belong to the orchestration/adapter tasks.

Identity results distinguish a granted lease, a committed existing identity, and contention. Only a
granted reservation in `Reserved` or `Writing` can authorize a write. A committed guard never grants
another write; existing fingerprint evidence produces exact replay/conflict results within 30 days,
and its expiry produces `AlreadyExistsUnclassified`. Requests carry separate guard/evidence deadlines;
the guard must outlive its telemetry. Every mutation checks identity, owner, fencing version, state,
and lease atomically. Resume may repeat only the same physical write after ambiguous persistence.
Promotion reserves a separate fenced owner, reconciles the identical destination event, advances the
guard to queryable, and permits pending-source deletion only afterward. These interfaces do not select
the physical identity store: T041/ADR-001 remains mandatory before T047 implementation.

Batch snapshots distinguish an incomplete company scope from an empty known scope; callers must not
interpret unknown companies as permission to disclose a batch. Authorization targets describe the
companies involved in the operation, never the caller's allowed-company list. Human and viewer service
actors remain distinct; viewer attribution identifies its Grafana organization, not an individual.
Only trusted authentication builds actors, and adapters recheck server-side profiles. Producers cannot
use operator ports. Permission validation, non-disclosing errors, and audit-before-disclosure remain
mandatory application orchestration. Ports do not silently perform these security steps on behalf of
handlers, and their existence alone does not prove durability, atomicity, privacy, or provider parity.

`TelemetryWriteRequest.Points` and line-result collections are limited to configured batch size, not
whole uploads. Raw data is carried only through bounded streams. Audit records use closed action,
resource, and outcome enums and opaque resource IDs rather than arbitrary payload/metadata objects.
No administrative secret, full driver identity, plate, coordinates, raw rejection, or token belongs
in an audit record. Concrete adapters and recovery/authorization/privacy tests follow in later tasks.
