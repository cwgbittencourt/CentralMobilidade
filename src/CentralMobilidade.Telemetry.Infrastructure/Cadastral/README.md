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
