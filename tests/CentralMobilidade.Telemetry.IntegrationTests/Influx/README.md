# Writer InfluxDB v3 — T038

## Estado atual após T110

Constituição 2.0.0 e T105–T110 substituem o modelo de empresas desconhecidas: o writer usa apenas
`InfluxDb:Queryable`, sem consulta de existência. As seções T038/T049/T104/T105 abaixo são registros
históricos, não instruções para configurar bancos ou resolvers removidos. T107 validou 41 casos,
incluindo três contra Influx real; T108 resolveu a prontidão que exigia promoções. T109 usa os serviços
`influxdb` e `influxdb-init` e mantém volume/node-id queryable. T110 validou migração e guard; veja
[o relatório de recovery](../Recovery/WorkerAcknowledgementBoundaryTests.md) para resultados e limites.

Os três testes T050 e 21 cenários T051 continuam vermelhos por implementações ausentes. O [gate T112](../../../artifacts/validation/foundation-single-influx/README.md) validou a fundação; evidências da topologia dupla não comprovam persistência/reinício do Compose
atual. Configuração vigente em [deploy](../../../deploy/README.md) e
[writer](../../../src/CentralMobilidade.Telemetry.Infrastructure/Influx/README.md).

## Histórico T049 — topologia dupla superada

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
e `AddAsync(TelemetryWritePoint, ReadOnlyMemory<byte>, CancellationToken)`
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

## T104 — reconhecimento de empresas pela origem interna

Esta etapa adiciona testes antes da refatoração, conforme constituição 2.0.0 e FR-019.
Não altera o writer, o cadastro, o processamento, a autenticação ou o Compose.
As evidências T038 acima descrevem a implementação histórica e não validam esta alteração.

Filtro dos novos cenários:

```powershell
dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests/CentralMobilidade.Telemetry.IntegrationTests.csproj --filter "Feature=FoundationSingleInflux" --logger trx --results-directory artifacts/validation/T104
```

São 15 cenários: seis de parser (dois IDs válidos sem cadastro, ausência/formato inválido e
preservação do nome `empresa_id`), dois de storage, quatro de negação de privilégios ao
produtor, um de ingestão com a mesma credencial para dois IDs e dois de processamento
(sem resolver cadastral ou com resolver que falha se chamado).

Em 2026-10-14, .NET SDK 10.0.401 / runtime 10.0.13, a compilação passou e o filtro teve
**11 aprovações e quatro falhas esperadas**, sem testes ignorados:

- O registro ainda inclui `ICompanyVisibilityResolver` (adequação T105/T107).
- O writer ainda exige configuração `InfluxDb:Pending` (adequação T107).
- Dois cenários ainda aguardam `ProcessTelemetryBatchHandler` (T051); continuam vermelhos.

A ingestão exercita o entry point real com queue/state doubles: prova autenticação comum,
publicação confirmada e estado `Accepted`, ainda sem conclusão. Não comprova persistência final
no InfluxDB nem durabilidade física do broker. O parser real mantém os erros estáveis para IDs
inválidos. Os cenários de processamento usam guard/batch PostgreSQL reais e writer/quarentena
controlados quando T051 existir; o writer registra destinos e qualquer consulta de existência
falha explicitamente. Não implementamos T050/T051 para fazer esses testes passarem.

A suíte existente de autenticação passou antes da alteração: 11/11. Os relatórios TRX ficam
em `artifacts/validation/T104/` (ignorados pelo Git). Resultados vermelhos de T104 são evidência
TDD para a adequação; não representam aprovação do novo desenho em produção.

Regressão das cinco classes afetadas/referenciadas (storage, writer/batcher, recovery,
autenticação e intake): **123 cenários, 96 aprovações e 27 falhas**. A classificação do
TRX é: três T050, 21 T051 (19 anteriores + dois novos), duas adequações de storage e
uma falha temporal do teste existente `Concurrent_reservation_has_one_winner_and_conflicting_contender_cannot_replace_it`.
Esse teste fixa `Now` na inicialização da classe e cria leases de um minuto; a primeira
execução incluiu obtenção da imagem PostgreSQL e durou 3m50s. Com a imagem disponível,
a repetição isolada passou **1/1** em 13s. A sensibilidade temporal do teste existente
não foi corrigida nesta tarefa; não apresentamos a primeira execução como totalmente aprovada.
Relatórios locais: `t104-regression.trx` e `t104-lease-recheck.trx`, na pasta T104.

Comando da regressão (requer Docker acessível para os fixtures PostgreSQL temporários):

```powershell
dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests/CentralMobilidade.Telemetry.IntegrationTests.csproj --no-build --no-restore --filter "FullyQualifiedName~StorageAdapterSafetyTests|FullyQualifiedName~WorkerAcknowledgementBoundaryTests|FullyQualifiedName~BearerSecurityTests|FullyQualifiedName~IntakeBoundaryTests|FullyQualifiedName~InfluxV3WriterTests" --logger "trx;LogFileName=t104-regression.trx" --results-directory artifacts/validation/T104
```

Somente a T104 foi executada. T105–T112 e T050/T051 continuam pendentes.

## T105 — contratos com destino único (2026-10-14)

Os ports não expõem mais `ICompanyVisibilityResolver`, `CompanyVisibility`,
`TelemetryDestination`, `IdentityStorageLocation`, `PromotionLease` nem métodos de promoção.
`IDriverResolver`, o schema `empresa_id` e Reserved/Writing/Committed permanecem.
Writer, guard, testes e doubles usam os contratos sem argumento de destino;
a ponte reflexiva da futura T050 também foi ajustada, sem implementar o montador.

A implementação HTTP de existência e seu registro foram removidos junto com o port.
Os testes da regra antiga foram substituídos por ausência do resolver e ausência de
consulta cadastral mesmo com configuração legada. A proteção contra redirects agora
exercita o cliente HTTP de storage. Gzip, limites, cancelamento, sync e reconciliação
parcial continuam cobertos. Os controles reais de Influx foram adaptados ao banco
queryable, mas não reexecutados nesta etapa; a prova nova usa HTTP controlado.

Persistência mantém seus enums legados exclusivamente na Infrastructure, com os mesmos
valores gravados em string, sem alterar schema, snapshot ou migrações aplicadas.
Os novos cenários PostgreSQL comprovam que guards PendingCommitted/Promoting não são
alterados nem retomados como queryable. A migração e a reconciliação são T106.
O writer ainda valida a configuração Pending antiga; seu teste continua vermelho para T107.
Compose e banco de execução do projeto não foram alterados.

Validação: solução compilada com zero erros/avisos; unitários 242/242, contratos 58/58,
arquitetura 47/47. Integração direcionada: 106 cenários, 78 aprovações, 25 falhas TDD
esperadas (três T050, 21 T051, uma T107) e três falhas temporais dos fixtures de lease.
A âncora desses leases foi corrigida para depois da preparação do banco; os intervalos,
o takeover real no PostgreSQL e as asserções de fencing continuam os mesmos.
Os TRX locais ficam em `artifacts/validation/T105/` (ignorados pelo Git).

Comandos de validação:

```powershell
dotnet build CentralMobilidade.slnx --no-restore
dotnet test tests/CentralMobilidade.Telemetry.UnitTests --no-build --no-restore
dotnet test tests/CentralMobilidade.Telemetry.ContractTests --no-build --no-restore
dotnet test tests/CentralMobilidade.Telemetry.ArchitectureTests --no-build --no-restore
dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests --no-build --no-restore --filter "FullyQualifiedName~StorageAdapterSafetyTests|FullyQualifiedName~WorkerAcknowledgementBoundaryTests|FullyQualifiedName~SelectedEventIdentityGuardTests|FullyQualifiedName~InfluxV3WriterTests|FullyQualifiedName~ControlMigration|FullyQualifiedName~PostgresAuditWriterTests|FullyQualifiedName~IntakeBoundaryTests"
```

A suíte executou também o probe curto do guard existente; isso não aprova a capacidade
de 5.000 eventos/s nem substitui o gate de performance pendente da ADR-001.
T106–T112 e T050/T051 continuam pendentes.

Após a correção temporal, os seis cenários afetados passaram **6/6**, sem skips,
em `guard-clock-recheck.trx` (1m43s). O build do projeto de integração foi refeito
com zero erros/avisos. A suíte de 106 cenários não foi repetida integralmente:
a rechecagem cobre os seis testes cujo fixture mudou e resolve as três falhas
inesperadas da execução inicial. As outras 25 falhas seguem deliberadamente vermelhas.

## T107 — writer sem configuração de pendentes (2026-10-14)

O writer lê somente `InfluxDb:Queryable:{Url,Database,Token}`. Os fixtures comuns
agora fornecem apenas esse destino. A regressão T104 que falhava por exigir Pending
passou: duas empresas sem cadastro são enviadas ao mesmo banco, sem consulta cadastral.

Os novos casos verificam que valores Pending iguais ao destino atual, indisponíveis
ou malformados não bloqueiam nem redirecionam a escrita; configuração Queryable
inválida continua falhando sem fallback e sem expor os valores privados. O registro
HTTP real é exercitado em sucesso e redirect, sem chamar o endpoint cadastral legado.
O port `IDriverResolver` segue preservado, sem implementar enriquecimento nesta etapa.

A primeira seleção ampla (`FullyQualifiedName~Influx`) executou 43 casos: 42 passaram
e um teste de health falhou. O nome do método de health também contém "influx";
a falha é a adaptação T108 já prevista, pois o probe PostgreSQL ainda exige oito
tabelas, incluindo a tabela de promoção retirada na T106. Dos 42 aprovados, 41
pertencem ao namespace de escrita e um é outro cenário de health. O filtro correto
abaixo restringe o namespace e separa essa pendência, sem alterar nem ignorar o teste.

```powershell
dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests/CentralMobilidade.Telemetry.IntegrationTests.csproj --no-build --no-restore --filter "FullyQualifiedName~CentralMobilidade.Telemetry.IntegrationTests.Influx.&FullyQualifiedName!~Production_batcher_flushes_at_first_default_threshold" --logger "trx;LogFileName=writer-final.trx" --results-directory artifacts/validation/T107
```

Os três cenários reais usam InfluxDB `3.11.6-core`, armazenamento em memória e um
container temporário: timestamp em milissegundos, erro parcial real e reconciliação
parcial pelo writer de produção. A autenticação do servidor é desativada no fixture;
o header bearer é validado nos testes HTTP controlados. Não há prova nova de WAL,
reinício, perda do host ou autorização por banco. Nenhum volume do projeto é usado.

Build da solução: zero erros/avisos. Unitários 242/242, contratos 58/58 e arquitetura
47/47 passaram. T050/T051 não foram implementadas; os três testes do montador T050
continuam presentes e fora desta seleção. O teste de saúde pertence à próxima T108.
Relatórios locais, inclusive a regressão vermelha anterior à mudança e a seleção
ampla com falha de saúde, ficam em `artifacts/validation/T107/` (Git ignored).

A execução final do filtro restrito passou **41/41**, sem skips, em 34s
(`writer-final.trx`), incluindo os três cenários contra o servidor real. A falha de
health na seleção ampla permanece registrada para T108; não é uma aprovação da
suíte completa. Somente T107 foi marcada como concluída.
