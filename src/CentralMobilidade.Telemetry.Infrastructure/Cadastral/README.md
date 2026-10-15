# Optional driver enrichment — T067

`ConfiguredCadastralAdapter` now implements only `IDriverResolver`. It does not
restore the company-eligibility adapter removed in T105. Valid producer company
IDs remain authoritative; `Cadastral:BaseUrl` and `CompanyRegistry:Url` are ignored.
Ingestion, the telemetry writer and the worker have no dependency on this resolver.

The API registers `AddOptionalDriverEnrichment()` independently of storage. The
resolver is a singleton with a shared bounded cache; a previously registered
`IDriverResolver` is preserved so another provider can replace it. Registration
does not connect to the source or require enrichment configuration at startup.
Actual presentation handlers/routes and authorization/audit-before-disclosure
remain T068–T070. Obtaining an optional identity never grants permission to return it.

```json
{
  "DriverEnrichment": {
    "Enabled": false,
    "BaseUrl": "https://driver-facade.example/",
    "Timeout": "00:00:02",
    "CacheTtl": "00:01:00"
  }
}
```

Absent or false Enabled means no request. When enabled, BaseUrl must be an absolute
HTTP(S) URL without userinfo, query or fragment. A base path is preserved. Timeout
must be positive and no more than 30 seconds; CacheTtl must be positive and at most
five minutes. Defaults are two seconds and one minute. Invalid optional settings
return no enrichment rather than blocking startup or company visibility. Use HTTPS
for the production facade and supply its deployment-specific authentication through
the named `DriverEnrichment` HTTP client; no cadastral credential is inferred from
producer/operator tokens.

The facade protocol is `GET <base>/drivers/{escapedDriverId}?empresaId={escapedCompanyId}`.
Only HTTP 200 with a JSON object containing exactly the requested driverId and a
nonblank displayName (at most 256 UTF-16 units, no control characters) yields an
identity. An optional `cpf` is minimized to its last four digits when it contains
11 ASCII digits or the `000.000.000-00` format. Malformed CPF is omitted. This is
format/minimization handling, not CPF ownership or check-digit verification.
Unknown provider fields are not projected or cached; duplicate top-level fields,
malformed Unicode/JSON, excessive nesting and oversized responses are rejected.

Both declared and streamed responses are bounded to 16 KiB and JSON depth four.
404, non-200, transport/body failures and the adapter timeout return no identity;
negative outcomes are not cached. Caller cancellation propagates, including before
a cache hit, and is passed through HTTP and body reads. Provider diagnostics, raw
responses, name, CPF and URL are never logged by the adapter. The production named
HTTP client disables redirects, cookies, automatic decompression and its default
HTTP loggers; full CPF stays out of the returned projection and cache.

Positive cache keys include company and driver. The cache holds at most 4,096
minimal identities, removes expired entries on access/insertion, and evicts the
oldest entry at capacity. The injected clock controls expiry. Configuration reload
invalidates cached identities and prevents an in-flight response from the revoked
generation being returned or cached. Changed settings are also detected on access.
Source replacement and disable/re-enable cannot reuse a previous positive result.
Environment-provider changes require the normal configuration reload/restart;
this adapter does not monitor external cadastral records or environment variables.

The constructor takes HttpClient, IConfiguration and IClock. It owns its client
and reload subscription, clearing the cache when disposed. Other providers can
replace the port without changing ingestion/storage contracts.

Validation: all 22 original T063 cases now pass, along with 21 new safety cases for
malformed/duplicate/invalid-Unicode identities, bounded declared/chunked responses,
invalid optional settings, in-flight cancellation/source replacement, revocation
and cache capacity. Three real loopback HTTP registration tests prove singleton
cache, secret/HTTP-log suppression, redirect/cookie refusal and escaped identifiers.
Full suite evidence is recorded in tasks.md and ignored `artifacts/validation/T067/`.
