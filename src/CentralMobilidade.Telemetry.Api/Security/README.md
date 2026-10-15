# Authentication and operator authorization

`AddTelemetrySecurity` registers separate `ProducerBearer` and `OperatorBearer` handlers and permission
policies. Production endpoints attach the appropriate named policy; the current root skeleton remains
public. No user or credential is supplied by default. `Authentication` configuration starts with empty
`Producers`, `Operators`, `ViewerServices`, and `Profiles` arrays, so protected routes cannot authenticate
until credentials are provisioned. These settings and hashes belong in private configuration or a
secret provider, never in tracked application files, command-line arguments, responses, or logs.

## Credential format and storage

Generate credentials with `OpaqueBearerTokens.Generate(kind)` and store `BearerTokenHash.Create(token)`.
The token contains 32 cryptographically random bytes, encoded in canonical Base64URL, with a type
prefix: `cm_p_` for producers, `cm_o_` for human operators, and `cm_v_` for viewer presentation services.
The prefixes do not carry identity or permissions. Deliver the plaintext token privately once; do not
persist it. Authorization accepts a single `Authorization: Bearer <token>` header and rejects malformed,
duplicate, oversized, noncanonical, missing, disabled, expired, or wrong-type credentials generically.

Hashes use PBKDF2-HMAC-SHA256, a random 16-byte salt, a 32-byte verifier, and 600,000 iterations.
Verification uses a fixed-time byte comparison and clears the derived verifier buffer. A separate
SHA-256 digest indexes the high-entropy opaque token; it is not a password verifier. Human operators
use opaque bearer credentials rather than user-chosen passwords. Hash configuration requires 600,000
through 1,000,000 iterations. See the [OWASP password storage guidance](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html).

`ProducerCredential`, `HumanOperatorCredential`, and `ViewerPresentationServiceCredential` are distinct
records. Each has `CredentialId`, `Hash`, `Enabled`, and optional `ExpiresAt`. Producers additionally
have `ProducerId`; operators reference a stable `ActorId`. Viewer credentials additionally name their
`GrafanaOrganizationId`. They are API presentation credentials, not Grafana bootstrap passwords or
Grafana organization service-account tokens. Credential/hash diagnostic strings hide their values.

The application-level `IAuthenticationCredentialStore` can be replaced by infrastructure without
referencing API types. The current `ConfiguredSecurityStore` reads `IOptionsMonitor<SecurityOptions>`
and supports configuration reload. Startup validation rejects unknown settings, malformed hashes,
duplicate credential IDs/digests, duplicate actor profiles, unknown permissions, missing profile
references, mismatched credential/profile kinds, and elevated viewer-service permissions.

Rotate by adding a new credential ID/hash for the same producer or actor, optionally overlapping
validity, then disabling or expiring the old credential. Permissions live in the actor profile rather
than token claims; changing or disabling the profile takes effect on subsequent requests. Environment
configuration requires a process restart to reload. File/secret-provider reload uses the underlying
configuration provider's change notifications. Protect the private configuration source against writes
by application clients; only trusted administration provisions profiles and rotates credentials.

## Shared internal producer

One technical producer uses one rotatable ingestion credential for all company IDs. `ProducerCredential`
has no company allowlist; `empresa_id` is validated by the Line Protocol parser, not a registry.
Hash fields are configured under `Authentication:Producers:0:Hash`; set the matching `CredentialId`
and `ProducerId` through protected configuration. Rotation may temporarily overlap two credentials
for the same producer; it does not create credentials per company. Producer authentication grants
submission and ownership checks for its own batches, not operator/viewer presentation permissions.
Optional driver enrichment remains separately scoped and audited.

## Policies and tenant scope

| Policy | Behavior |
| --- | --- |
| `Producer` | Producer authentication only, for submission |
| `ProducerBatchRead` | Producer authentication plus an existing, unexpired batch owned by that producer |
| `batch.read` | Operator permission and every company in the persisted batch snapshot |
| `telemetry.read` | Operator permission and the requested company |
| `driver.read` | Operator permission and the requested company |
| `quarantine.raw.read` | Administrative operator permission and every company in the owning batch |
| `cross_company.read` | Explicit human operator permission; never implicit in authentication |

The operator principal contains only server-issued actor/credential identifiers, credential kind, and
viewer organization. Permissions and allowed companies are reloaded server-side through the store.
Viewer profiles can contain only `telemetry.read` and `driver.read`, and their organization must match
the typed credential. They cannot obtain raw quarantine, batch administration, or cross-company access.
The authorizer repeats these restrictions even if its store is replaced.

Company targets come from the route/query `empresaId` or the persisted batch's complete company scope;
callers never supply their own allowed-company list. Case-sensitive identifiers are preserved. A batch
with unknown company scope requires explicit human cross-company permission. Missing or mismatched
company scope yields the same generic 404 as an unavailable resource; a missing permission yields a
generic 403, and failed authentication yields a generic 401 with `WWW-Authenticate: Bearer`. Handlers
must still apply authorized company scope, query bounds, and resource availability. Company existence
is authoritative from valid producer-supplied IDs; no cadastral visibility gate applies. Batch status endpoints
supporting both producer and operator access must select the appropriate authorization branch, not
combine their policies as an AND requirement.

## Denial audit and fail-closed behavior

`AuditedAuthorizationResultHandler` awaits `IAuditWriter.AppendAsync` before returning any denied
operator-policy response, including authentication challenges. It records a stable action/category,
actor type, viewer organization when known, outcome, UTC time, trace correlation, and an opaque UUID
resource when available. Unauthenticated requests use the `Unidentified` actor kind; no individual
human attribution is invented. Untrusted company/driver identifiers, header values, full URLs,
coordinates, plate, name, CPF, raw content, and secrets are not copied into denial audit records.
Their company scope is empty when no trusted scope has been established.

The returned receipt must match the event ID and use UTC. A missing writer, failed append, or invalid
receipt yields generic `503 AUDIT_UNAVAILABLE`, without backend diagnostics. Cancellation propagates.
The default configuration does not install a fake successful audit writer: durable PostgreSQL audit
persistence is implemented in T018. Until a real writer is registered, denied operator-policy requests
fail closed with 503. Successful sensitive disclosure and missing/expired content audits belong to the
resource handlers in T060/T068/T069; policy success alone is not permission to skip those audits.

## Validation

On 2026-10-13, the 11-project solution compiled on Windows without warnings/errors. Windows Application
Control blocked loading the generated Application assembly in the test process (`0x800711C7`), so tests
ran in the project's Linux .NET 10 target using Docker. All 123 implemented tests passed: 72 unit,
40 contract, and 11 security integration tests. Security checks cover credential isolation, scoped
human/viewer access, producer ownership, multi-company/incomplete batch scopes, explicit cross-company
access, credential/profile revocation, rotation, generic failures, duplicate/malformed headers, salted
hashing, startup rejection of privileged viewers, token exclusion from captured logs, combined-policy
audit categorization, and missing/failed audit writers.
The tests use a real HTTP authentication/authorization pipeline with isolated credential, batch, and
audit fixtures; they do not claim PostgreSQL durability or future end-to-end ingestion validation.

Run the security tests on Linux without writing build output into the repository:

```powershell
docker run --rm --mount "type=bind,source=$($PWD.Path),target=/source,readonly" --workdir /source mcr.microsoft.com/dotnet/sdk:10.0.401-noble dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests/CentralMobilidade.Telemetry.IntegrationTests.csproj --artifacts-path /tmp/test-artifacts --filter FullyQualifiedName~Security --verbosity minimal
```

## Combined status authentication (T059)

Only the batch-status route uses `BatchStatusRead`/`BatchStatusBearer`, selecting
one producer or operator validator and emitting one challenge. Other routes retain
their existing credential-specific policies. The endpoint derives its application
identity solely from the authenticated server principal; caller-supplied company,
producer or actor parameters cannot grant visibility. Viewer service tokens do not
grant batch-status access. Status authorization failures are non-disclosing 404s;
this metadata read does not perform a sensitive raw-content lookup.
