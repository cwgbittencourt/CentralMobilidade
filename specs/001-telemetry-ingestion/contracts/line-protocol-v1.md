# Telemetry Line Protocol Contract v1

## Transport

- Media type: `text/plain; charset=utf-8`
- Content encoding: `gzip` (required)
- Line delimiter: LF; CRLF is normalized during streaming parse
- Timestamp precision: Unix milliseconds in UTC
- Measurement/table: `telemetria`
- Empty lines: ignored only at the end of a payload; otherwise rejected and counted

## Shape

```text
telemetria,empresa_id=<id>,veiculo_id=<id>[,motorista_id=<id>] latitude=<float>,longitude=<float>,velocidade=<float>,rpm=<integer>,ignicao=<boolean>[,placa="<string>"] <unix_ms>
```

Canonical example:

```text
telemetria,empresa_id=empresa-42,veiculo_id=veiculo-900,motorista_id=motorista-77 latitude=-23.55052,longitude=-46.633308,velocidade=82.5,rpm=2150i,ignicao=true,placa="BRA2E19" 1791686400123
```

`DataHoraEvento` is represented by the final Line Protocol timestamp, not duplicated as a field.

## Closed schema

### Tags

| Name | Required | Notes |
|---|---:|---|
| `empresa_id` | yes | Company identifier; valid producer-supplied IDs are recognized without cadastral lookup |
| `veiculo_id` | yes | Stable vehicle identifier |
| `motorista_id` | no | Pseudonymous identifier only |

### Fields

| Name | Type | Required | Validation |
|---|---|---:|---|
| `latitude` | float | yes | -90 to 90 |
| `longitude` | float | yes | -180 to 180 |
| `velocidade` | float | yes | >= 0; km/h in v1 |
| `rpm` | signed integer (`i`) | yes | >= 0 |
| `ignicao` | boolean | yes | `true` or `false` |
| `placa` | quoted string | no | Presentation data; forbidden in general logs |

Unknown tags or fields are rejected in v1. Driver name, CPF, phone number, e-mail, or other direct
driver identity is forbidden.

### Runtime boundaries and normalization

Decoded company, vehicle and optional driver identifiers are nonblank, at most 256 UTF-8 bytes,
contain valid Unicode and no control characters. Case and Unicode representation are preserved;
identifiers are not trimmed or case-folded. Presentation HTTP endpoints apply a separate, narrower
128 UTF-16 code-unit limit as well as the domain's UTF-8 bound; ingestion acceptance is not a
guarantee that every long identifier can be selected by those endpoints.

Floating fields must be finite; decimal/exponent forms are allowed, with coordinate and nonnegative
speed bounds unchanged. RPM requires a nonnegative signed Int64 literal ending in `i` (not `u`).
Ignition accepts exactly lowercase `true`/`false`. Plate strings are normalized to uppercase before
fingerprinting; an absent plate and an explicitly empty string are distinct canonical values.
Duplicate tags/fields, invalid escapes and malformed UTF-8 are rejected. Unix-millisecond timestamps
must also fit the storage boundary; the parser rejects values outside its supported range.

The parser's default line ceiling is 16 KiB, with bounded streaming applied before parsing. The
default intake ceiling is 8 MiB compressed; worker defaults additionally bound decompressed bytes,
expansion ratio, line count and processing time. These configurable safety ceilings are not extra
Line Protocol fields or throughput guarantees. Gzip acceptance at the API does not prove that its
contents parse: decompression/schema failures are asynchronous batch outcomes. Interior blank lines
are rejected while trailing blanks are ignored by the batch reader, not the single-line parser.

## Event identity and replay

Logical identity is `(empresa_id, veiculo_id, timestamp)`. Whitespace and tag/field ordering are
canonicalized before the event fingerprint is calculated.

- Same identity and same canonical fingerprint: identical replay; no new logical event.
- Same identity and different fingerprint: `DUPLICATE_CONFLICT`; preserve the original.

## Evolution

Adding or changing a tag, field, unit, type, measurement, or identity rule requires a reviewed contract
revision. Breaking changes require a new API/schema version and coexistence window.

## Company recognition (2026-10-14)

`empresa_id` is the wire name for business `idEmpresa`; no tag rename or schema expansion is implied.
One shared internal producer credential accepts all valid company IDs. No company registration,
lookup, pending store, or promotion is required. Missing/invalid IDs follow existing validation;
all other schema, replay, privacy, and presentation authorization rules remain in force.
