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
| `empresa_id` | yes | Tenant identifier; presentation requires cadastral validity |
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

## Event identity and replay

Logical identity is `(empresa_id, veiculo_id, timestamp)`. Whitespace and tag/field ordering are
canonicalized before the event fingerprint is calculated.

- Same identity and same canonical fingerprint: identical replay; no new logical event.
- Same identity and different fingerprint: `DUPLICATE_CONFLICT`; preserve the original.

## Evolution

Adding or changing a tag, field, unit, type, measurement, or identity rule requires a reviewed contract
revision. Breaking changes require a new API/schema version and coexistence window.
