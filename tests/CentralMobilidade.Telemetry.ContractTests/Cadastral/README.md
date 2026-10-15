# Optional cadastral enrichment contracts (T063)

`CadastralAdapterContractTests` adds 22 cases. Six pass against existing production
contracts/adapters; 16 remain active TDD failures tagged `PendingImplementation=T067`
because the optional configured driver adapter has not been implemented. No production
resolver, skip or fake implementation is added to make those assertions pass.

The passing cases cover the company-scoped driver port, minimal/redacted presentation,
public CPF suffix validation, and absent/unavailable registry independence. The latter
uses the real InfluxV3TelemetryWriter with a controlled HTTP acknowledgement and the
real StoredOperatorAuthorizer with explicit scopes for two valid IDs absent from any
registry. No registry/driver enrichment is consulted; an unrelated company remains
denied. This proves the write-provider contract and visibility authorization decision,
not real Influx disk persistence or data returned by the future presentation API.
Those runtime proofs remain in integration tests and T064/T069/T070.

The active cases execute the future production ConfiguredCadastralAdapter through
IDriverResolver and controlled HTTP responses. They cover provider replacement,
missing driver, optional source failure, CPF minimization, malformed CPF, changed
cadastral data after cache expiry, tenant-separated cache keys, a mismatching driver
ID, bounded timeout/recovery, caller cancellation and absent/disabled configuration.
Source names, full CPF and private backend diagnostics must not enter diagnostic logs.
Identity disclosure still requires authorization and durable audit in T068–T070;
resolving optional enrichment is not permission to return it to a caller.

## T067 test seam

The fixture locates `CentralMobilidade.Telemetry.Infrastructure.Cadastral.ConfiguredCadastralAdapter`
from the production assembly and creates it with DI. The shared dependencies supplied
are IConfiguration, HttpClient/IHttpClientFactory, IClock/TimeProvider and ILogger.
It owns and disposes the adapter. If production registration introduces typed options,
the harness wiring can follow it without weakening the behavioral assertions.

The controlled facade contract is:

- `DriverEnrichment:Enabled` controls optional lookup; absent/false means no source request.
- `DriverEnrichment:BaseUrl` selects the replaceable facade (HTTPS test origins).
- `DriverEnrichment:Timeout` is 100 ms in timeout tests; timeout returns no identity,
  while caller cancellation propagates.
- `DriverEnrichment:CacheTtl` is one minute in cache tests. The injected UTC clock
  advances deterministically; expired entries refresh and provider reload invalidates
  the previous source's cached result. Cache keys include company and driver.
- `GET /drivers/{driverId}?empresaId={companyId}` returns JSON containing matching
  `driverId`, `displayName`, and optional source `cpf`. The facade may return a full
  synthetic CPF; the adapter must reduce 11 digits (including formatted input) to
  at most the last four digits. Invalid CPF cannot become an unmasked suffix.
- 404 means optional identity absent; HTTP 500/503 and transport failure also return
  no optional identity, without private diagnostics. Errors/timeouts are not cached
  as a successful identity; a subsequent successful source response is usable.
- Legacy `Cadastral:BaseUrl` / `CompanyRegistry:Url` values never gate valid-ID writes
  or scoped telemetry authorization and must not become company-eligibility lookups.

This is the test facade boundary, not a claim about an existing external cadastral
API or its database technology. Another provider can adapt its schema behind the
same driver port without changing ingestion/storage contracts. No actual external
HTTP request or Docker service is needed for these contract tests.

Reports: ignored `artifacts/validation/T063/`. The default contract suite intentionally
includes all active TDD failures until T067; no failed case is quarantined or skipped.
