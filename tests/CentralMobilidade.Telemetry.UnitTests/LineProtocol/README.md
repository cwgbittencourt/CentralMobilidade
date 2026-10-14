# Golden tests de Line Protocol v1 — T036

Atualização T046: parser de produção implementado; os 45 cenários originais e dez
adicionais passam. Suíte unitária completa: 241 aprovações; arquitetura: 47 aprovações.
Validação final em Linux Docker após bloqueio da DLL pelo Application Control Windows.
Evidência: `artifacts/validation/T046/unit-linux.txt`. O resultado TDD abaixo é histórico.

`LineProtocolV1GoldenTests` exercita o parser de produção planejado na T046,
conforme `specs/001-telemetry-ingestion/contracts/line-protocol-v1.md` e `data-model.md`.

```powershell
dotnet test tests/CentralMobilidade.Telemetry.UnitTests --filter FullyQualifiedName~LineProtocolV1GoldenTests
```

Os 45 cenários verificam valores decodificados, escapes de espaço/vírgula/igual em tags,
aspas e barras em strings, identificadores Unicode, UTF-8 inválido, LF/CRLF, tipos
fechados, tags/campos obrigatórios, colunas desconhecidas, limites de coordenadas,
velocidade em km/h, RPM inteiro não negativo e timestamps Unix em milissegundos.
Fingerprints devem ser SHA-256 em hexadecimal, iguais para ordem de colunas,
representação numérica e terminação de linha equivalentes, e diferentes para mudanças
nos valores ou na identidade do evento. Os testes não impõem uma serialização canônica
interna específica.

## Ponte temporária para a implementação futura

Para manter a solução compilável antes da T043/T046, o helper usa reflexão somente
na chamada ao parser; não contém parser alternativo, skips ou bypasses de falhas.
O tipo esperado é `CentralMobilidade.Telemetry.Application.LineProtocol.TelemetryLineProtocolV1Parser`,
com `Parse(ReadOnlyMemory<byte>)` para uma única linha UTF-8 limitada, opcionalmente
terminada em LF/CRLF. O resultado expõe `Event` e `ErrorCode`; uma rejeição tem evento
nulo e código do catálogo. Um evento válido expõe `CompanyId`, `VehicleId`, `DriverId`,
`Timestamp`, `Latitude`, `Longitude`, `Speed`, `Rpm`, `Ignition`, `Plate` e `Fingerprint`.
Identificadores e timestamp usam os value objects existentes. Essa é a superfície
proposta pelos testes; a T046 pode substituir a ponte por chamadas tipadas mantendo
as mesmas asserções de comportamento.

Delimitação de múltiplas linhas, linhas vazias no meio/final do payload e limites de
descompressão pertencem ao leitor streaming das T037/T045. Este conjunto passa uma
linha por chamada e verifica linha vazia isolada como `LP_EMPTY_LINE`.

## Resultado TDD

Na T036, os 45 cenários falham pela ausência explícita do parser T046; os 101 testes
unitários anteriores passam. Compilação sem avisos. Relatório local em
`artifacts/validation/T036/T036.trx` (ignorado pelo Git).
As duas falhas de readiness PostgreSQL registradas na T035 são uma pendência separada
da T022 e não foram reexecutadas nesta tarefa de testes unitários.
