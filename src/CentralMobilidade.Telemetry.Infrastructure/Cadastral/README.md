# Configured company visibility — T049

`ConfiguredCadastralAdapter(HttpClient, IConfiguration)` implements the replaceable
`ICompanyVisibilityResolver`. Ingestion does not register companies or infer their
visibility. With no `Cadastral:CompanyVisibilityUrl`, every lookup is `Unavailable`,
so processing must keep the point in pending storage.

The configured HTTP integration contract is:

```text
GET <CompanyVisibilityUrl>?companyId=<URI-escaped exact company ID>
Authorization: Bearer <Cadastral:Token>   (if configured)

200 {"companyId":"exact-company-id","visible":true}
```

This defines the adapter's expected wire contract; no existing external cadastral
service or live integration is assumed. Supply a service implementing it, or replace
the port. The endpoint URL may contain a path, but no query, fragment or embedded
credentials. Company IDs preserve their exact case/Unicode representation.

Only HTTP 200, unique JSON properties, a matching company ID and boolean `visible:true`
return `Visible`. Explicit false and 404 return `Unknown`. Missing/mismatched fields,
duplicate keys and invalid JSON return `Inconclusive`. Other HTTP statuses, unavailable
transport, oversized bodies or timeout return `Unavailable`. All three non-visible
results route to pending; no stale positive fallback is cached. Caller cancellation
propagates.

`Cadastral:LookupTimeout` defaults to two seconds (positive, at most 30 seconds).
Response reads are bounded to 16 KiB with JSON depth 4. No raw provider details are
logged or returned. `AddTelemetryStorageAdapters()` supplies controlled HTTP clients
without redirects/cookies and with logged headers redacted; T053 wires the worker.
Driver identity resolution remains separate and is not implemented by this adapter.
