# Writer InfluxDB v3 — T038

## Atualização T049

O writer e o adapter cadastral estão implementados. Os 12 cenários originais do
writer, o controle direto e uma nova prova de escrita parcial contra InfluxDB real
passaram. Mais 23 cenários cobrem visibilidade explícita por empresa, consultas
inconclusivas/indisponíveis, timeout/cancelamento, limites de resposta sem tamanho
declarado, JSON/índices duplicados, transporte privado, status não confirmatório,
422, destinos separados e redirects bloqueados pelo registro HTTP de produção.
O teste de timestamp passou a interpretar os horários de consulta sem fuso como UTC.

Integração completa: 191 aprovações e 22 falhas previstas (três T050, 19 T051).
Unitários 241/241 e arquitetura 47/47. Relatórios locais ignorados em
`artifacts/validation/T049/`: `writer-final.trx`, `safety.trx`, `integration.trx`,
`unit.trx` e `architecture.trx`.

Os três testes do batcher continuam aguardando T050; 19 cenários de processamento
aguardam T051. As seções seguintes preservam o registro TDD da T038. Formato e
configuração estão nos READMEs de `Infrastructure/Influx` e `Infrastructure/Cadastral`.

```powershell
dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests --filter "FullyQualifiedName~InfluxV3WriterTests|FullyQualifiedName~InfluxRealServerTests"
```

São 16 cenários: um controle contra o servidor real, 12 do writer de produção T049
e três do montador de lotes T050. Nenhum é ignorado ou aceita falha como sucesso.

## HTTP e servidor real

Os testes do writer usam `ITelemetryWriter`, com reflexão somente para construir
`InfluxV3TelemetryWriter(HttpClient, IConfiguration)` antes da T049. Um servidor Kestrel
em loopback/porta temporária captura a requisição HTTP real e controla as respostas.
Ele verifica POST `/api/v3/write_lp`, bancos queryable/pending separados, bearer,
gzip nativo sem recompressão, `precision=millisecond`, `accept_partial=true`,
`no_sync=false`, resultado 204 e propriedade do stream com o chamador.

Retornos parciais 400 usam o formato Influx v3 (`error`, `data`, `line_number`,
`original_line`, `error_message`). O índice do provider é baseado em um; deve ser
mapeado ao batch/linha original, que não coincidem com a posição no lote de escrita.
Type conflict é registrado como `STORAGE_SCHEMA_CONFLICT`. Respostas 401/403/500/503,
JSON inválido, lista vazia ou índices fora dos limites não podem virar sucesso.
Exceções devem permanecer tipadas e privadas, sem texto bruto do provider.

`InfluxContractServer` inicia a imagem fixa `influxdb:3.11.6-core` com Testcontainers,
porta aleatória, armazenamento em memória e autenticação desabilitada somente nessa
instância isolada. Não altera o Compose nem seus volumes. O controle envia gzip com
uma linha válida e outra inválida, verifica índice 2 no erro e consulta um ponto gravado.
Outro teste exige que o writer de produção grave e preserve o timestamp em milissegundos
ao consultar o servidor. O controle direto não substitui esse teste do adaptador.
O container é descartado ao fim da classe. Não comprova sobrevivência a reinício,
disco/WAL ou perda física do host; isso pertence aos testes de recuperação.

## Limites do montador

A ponte propõe `InfluxWriteBatcher(ITelemetryWriter, TelemetryOptions, TimeProvider)`
e `AddAsync(TelemetryWritePoint, ReadOnlyMemory<byte>, TelemetryDestination, CancellationToken)`
retornando `ValueTask`. Um writer de captura verifica o gzip emitido e seus owners.
5.000 pontos, 2 MiB e um segundo são exercitados como gatilhos independentes, exigindo
que nenhum envio ocorra antes de atingir o limite. Nos casos de pontos/bytes, o timer
é elevado para isolar a fronteira e evitar corrida com a duração do teste. O caso de
tempo usa o padrão de um segundo e watchdog de três segundos. A T050 pode substituir
a reflexão por chamadas tipadas mantendo o comportamento e as asserções.

Fontes do contrato: [API de escrita InfluxDB 3 Core](https://docs.influxdata.com/influxdb3/core/api/write-data/)
e [configuração do servidor](https://docs.influxdata.com/influxdb3/core/reference/config-options/),
conferidas em 2026-10-14; decisões do projeto em `specs/001-telemetry-ingestion/research.md`.

## Evidência TDD

Integração completa: 130 aprovações (129 anteriores + controle real Influx) e 15 falhas
esperadas por tipos de produção ausentes: 12 aguardam T049 e três aguardam T050.
Compilação sem avisos. Os 74 casos TDD unitários de T036/T037 não foram reexecutados
nessa suíte. Relatório local: `artifacts/validation/T038/T038.trx` (Git ignored).
