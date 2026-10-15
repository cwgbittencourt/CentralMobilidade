# Optional cadastral enrichment — T107

The internal producer is authoritative for company existence: every valid `empresa_id`
is recognized without a registry request. One ingestion credential covers all companies.

`ICompanyVisibilityResolver` and `ConfiguredCadastralAdapter` were removed in T105.
`AddTelemetryStorageAdapters()` registers only the single-store telemetry writer.
Legacy company-lookup configuration is not read and cannot gate persistence or authorized
telemetry visibility. The old T049 lookup protocol and pending-routing behavior are
superseded by constitution 2.0.0 and FR-019; they must not be reintroduced.

`IDriverResolver` remains the separate application port for optional, authorized,
company-scoped driver presentation. No concrete driver resolver is currently supplied
by this folder. Its future implementation must preserve minimal identity disclosure,
audit and authorization, and must not reintroduce company-existence gating.

## T063 contract coverage

The optional enrichment contract suite now lives under ContractTests/Cadastral.
Six regression/contract cases pass for the existing minimal identity surface,
CPF DTO validation, registry-independent write acknowledgement and real server-side
visibility authorization. Sixteen active PendingImplementation=T067 cases require
the production driver adapter; they do not reintroduce company recognition or pending
storage. The harness documents a replaceable driver HTTP facade and timeout/cache
configuration under DriverEnrichment, separate from ignored legacy company-lookup
keys. T067 can evolve the dependency registration while preserving the behavioral
assertions. Actual presentation queries, audit-before-disclosure and HTTP endpoint
authorization remain T064/T068–T070; write protocol tests do not certify disk durability.
