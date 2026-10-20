# Quickstart — execução T098

Data: **2026-10-20**, America/Sao_Paulo (UTC−03:00). Resultado: **incompleto / bloqueado**.
T098 permanece `[ ]`: uma falha real de readiness impede a sequência do smoke,
a T094 continua reprovada e a prova final depende de T101/T102 ainda pendentes.
Este registro não aprova implantação, capacidade, segurança ou conformidade LGPD.

## Origem e ambiente

- Guia executado/revisado: `specs/001-telemetry-ingestion/quickstart.md`, SHA-256
  inicial `898baa0e871ef4abb3cf66f64dd1c69f6efdd98e39bc331dc094a4b015d197b4`.
- Revisão inicial: `a1e53799481c7bae4225c4b79ef86ff7564140ab`. A tentativa acrescenta
  somente tooling/evidência/documentação; nenhuma alteração em `src/`.
- Checklist `requirements.md`: 16/16; nenhum item pendente. Hooks de extensão ausentes.
- Windows, PowerShell **7.6.5**, .NET SDK **10.0.401**, Python **3.14.6**.
- Docker Engine **29.8.2**, Linux x86_64; Compose **5.6.0**; VM com 16 CPUs lógicas
  e 33.608.843.264 bytes de RAM. Disco livre Windows inicialmente
  1.476.467.404.800 bytes; isso não mede espaço disponível dentro do VHDX Linux.
- Smoke: PostgreSQL `17.11-bookworm` (`fsync=on`, `synchronous_commit=on`),
  RabbitMQ `4.3.6-management-alpine`, InfluxDB `3.11.6-core` (file, 60 dias),
  Prometheus `v3.15.0-busybox`, Alpine `3.23.4`.
- Imagem API `centralmobilidade-api:t094`:
  `sha256:881a0c1ab82c02f1057711fd987b383be208c6eb7011a50c8e74fe9929dd5c06`.
- Imagem worker `centralmobilidade-worker:t094`:
  `sha256:5362e95f5efb544b53f23d7e809df0faabbcf7eb1eb61d79ab33581c599d9004`.
  Imagens Release do source `520f8ed`; não houve mudança de `src/` até a revisão inicial.
  Limites inspecionados: API 2 CPUs/4 GiB, worker 6 CPUs/12 GiB.

Credenciais novas e privadas, tokens distintos e somente hashes no catálogo da API.
Nenhuma `.env` existente, catálogo/token de deployment ou volume de usuário foi alterado.
O smoke usa credenciais de bootstrap dos stores; não valida permissões mínimas de produção.

## Comandos e horários observados

Todos os comandos partem da raiz do repositório. Horários abaixo em UTC−03:00.
`$R` significa o caminho absoluto de `artifacts/validation/T098/run-20261020`.
Saídas completas, TRX, configurações e tokens ficam **ignorados** pelo Git.

| Início → fim | Comando | Resultado |
| --- | --- | --- |
| 11:31:37 → 11:31:39 | `dotnet restore CentralMobilidade.slnx` | Exit 0 |
| 11:31:39 → 11:31:48 | `dotnet build CentralMobilidade.slnx --no-restore` | Exit 0; zero warnings/erros |
| 11:31:48 → 11:31:54 | `dotnet test tests/CentralMobilidade.Telemetry.ContractTests --no-build --logger trx --results-directory $R/contracts` | **154/154 PASS**, zero skips |
| 11:32:33 → 11:40:11 | `dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests --no-build --logger trx --results-directory $R/integration` | **845/845 PASS**, zero skips |
| 11:40:27 → 11:46:10 | `py -3 scripts/Run-CapacityGates.py --quickstart-smoke` | Exit 1; preparação inicial inválida; sem resultado funcional |
| 11:45:28 → 11:48:41 | Mesmo comando após correção da configuração da fixture | Exit 1; **readiness não atingida** antes do intake do cenário |
| 11:49:22 → 11:49:40 | Diagnóstico restrito descrito abaixo | Liveness 200, readiness **503**; PostgreSQL `on/on`; dois batches `Completed` |

Antes da integração, `TELEMETRY_TEST_POSTGRES` e `TELEMETRY_TEST_RABBITMQ` foram
limpos apenas no processo de teste para usar fixtures descartáveis, sem conexões externas herdadas.
Os filtros `Category=Contract/Integration` do guia original não selecionavam todos os
testes dos projetos. Foram substituídos pelos comandos completos acima.

As tentativas de smoke se sobrepuseram durante a espera/limpeza da preparação inicial,
em projetos/volumes distintos. Não são ensaio de desempenho nem de isolamento de recursos.

## Falhas e diagnóstico

1. Fixture inicial `6a3002ac95e24fdd89043a951bea9916`: a configuração do preparador
   omitia a chave persistente de cursor e usava `CompanyIds: []` no record do administrador.
   O binding não consegue fornecer esse parâmetro obrigatório com a lista JSON vazia.
   A API encerrou na inicialização. O preparador agora gera uma chave de 32 bytes e
   fornece uma empresa explícita; `cross_company.read` continua sendo permissão separada.
   Essa tentativa não comprova falha de ingestão nem aprovação de qualquer cenário.
2. Fixture corrigida `d70b6a31e32f428a9a7e676f1b60ce22`: API/worker iniciaram e os dois
   lotes gzip de aquecimento foram persistidos. O smoke parou em `stage: valid intake`,
   na espera de readiness, **antes** dos seus lotes válido/parcial e do restart.
3. Diagnóstico reutilizou somente os volumes dessa segunda fixture. Comandos:

   ```powershell
   # $F: caminho absoluto do compose.json privado da segunda fixture
   docker compose --project-name cm-t098-d70b6a31e32f -f $F up -d postgres rabbitmq api
   docker compose --project-name cm-t098-d70b6a31e32f -f $F port api 8080
   # $P: porta loopback retornada; após liveness e mais 15 segundos
   Invoke-WebRequest "http://127.0.0.1:$P/health/live" -SkipHttpErrorCheck
   Invoke-WebRequest "http://127.0.0.1:$P/health/ready" -SkipHttpErrorCheck
   docker compose --project-name cm-t098-d70b6a31e32f -f $F exec -T postgres psql -U telemetry -d telemetry_control -At -c 'SELECT current_setting(''fsync''),current_setting(''synchronous_commit''),(SELECT count(*) FROM ingestion_batches WHERE "State"=''Completed'')'
   docker compose --project-name cm-t098-d70b6a31e32f -f $F down --timeout 60
   ```

   Resultado: live **200**, ready **503** com `{"status":"unhealthy"}`, SQL **`on|on|2`**.
   A inspeção de `src/` encontra a interface `IDurablePublicationProbe` e o health check,
   mas nenhuma implementação/registro em runtime. Sem esse serviço,
   `DurablePublicationHealthCheck` retorna Unhealthy. A limitação já é descrita no
   [contrato de health](../../src/CentralMobilidade.Telemetry.Infrastructure/Health/README.md).
   POSTs confirmados não substituem esse probe. A suíte funcional usa controles próprios
   e seu PASS não elimina a falha observada nos hosts reais desta fixture.

O smoke permanece fail-closed: não troca readiness por liveness e não injeta probe falso.
Seus passos posteriores estão preparados, mas **não foram validados nesta execução**.
Não houve correção de produto nesta tarefa de validação.

## Cobertura de todos os cenários do guia

“Fixture PASS” abaixo significa teste automatizado isolado aprovado nesta execução;
não significa execução manual integrada de todos os passos no mesmo deployment.

| Seção | Evidência desta execução e limite |
| --- | --- |
| Pré-requisitos / §1 segredos | Ambiente observado; novas credenciais privadas e hashes no smoke. Hardening/permissões mínimas de um deployment existente não executados. |
| §2 dependências/Grafana | Fixture PASS: `ComposeRecoveryTests` 9; `GrafanaRuntimeTests` 5; `GrafanaReconciliationRuntimeTests` 2; `GrafanaProvisioningTests` 64. Reconciliador real e organizações/templates isolados; o health backend do reconciliador é um probe de teste. Não constitui bootstrap completo de um único deployment com todos os hosts reais. |
| §3 restore/migrações/aplicação | Restore/build/contratos PASS; migrations reais no smoke e testes de migrations/persistência. **FAIL no host API readiness**, embora liveness e warmup funcionem. `up --wait` do app real não foi aprovado. |
| §4 gzip/Influx/dashboard/scopes/limites | Dois warmups reais completados. Fixtures de intake, Influx e apresentação/Grafana PASS. A cadeia do mesmo ponto do novo smoke até ambos os dashboards não foi executada; não inferir esse resultado da soma de fixtures. |
| §5 parcial/quarentena | Fixtures `BatchInspectionTests` 117, `QuarantineStoreTests` 16 e processamento/worker PASS. Lote HTTP de quatro linhas do novo smoke **não executado**, bloqueado em readiness. |
| §6 Influx indisponível + reinício API/worker | Fixtures `AcceptedBatchSurvivalTests` 2, `WorkerHostTests` 19 e `BackupRestoreTests` 1 PASS. Cenário HTTP combinado do novo smoke **não executado**. Backup usa volumes novos; sem prova de perda de host/RPO/RTO/off-host. |
| §7 privacidade | Fixture PASS: `ObservabilityLeakTests` 3, `PrivacyCanaryScannerTests` 29, `PresentationAuthorizationTests` 123 e auditoria. Exercita logs/Loki/Prometheus/OTLP com canários, saídas não vazias e controles de vazamento; transporte Loki explícito. Sem varredura de um deployment existente ou aprovação LGPD. |
| §8 paridade de filas | Fixture PASS: RabbitMQ reusable 16, publisher 12, consumer 19, topology 7; Pub/Sub emulator 22, SDK controls 4, adapter safety 14, host provider 9. Recursos reais/emulador; não é prova de durabilidade cloud ou autorização de cutover. |
| §9 integração/capacidade/dedup | Integração completa PASS. Steady, burst, outage de carga e benchmark SQL **não executados nesta tentativa**, após falha sequencial de readiness. T094 já reprovada; evidência histórica separada abaixo. |
| §10 exatamente 100.000 eventos + Grafana | **Não executado**: `Final100kGrafanaProofScenario.cs` ausente, T101/T102 pendentes. Nenhum pequeno smoke, corpus T040 ou teste de dashboard substitui essa prova. |
| §11 shutdown preservando dados | Preparador e diagnóstico executaram `down --timeout 60` somente nos projetos UUID próprios, sem `--volumes`. Volumes de ambas as tentativas preservados; nenhum stack de usuário foi parado. |
| T082 métricas | Fixture `PrometheusScrapingTests` 2 PASS, mais observability/heartbeat. Não valida host físico Windows, todos os exporters de um deployment ou idade exata da cabeça da fila. |
| T083 Loki | `LokiIngestionTests` 2 PASS; retenção/configuração/labels isolados. Sem espera real de 30 dias/eliminação de backup. |
| T084 operações | `PlatformOperationsTests` 1 PASS; dados/controlos próprios da fixture. Não é prova de SLO sob carga. |
| T104–T112 foundation | `SingleTelemetryStoreMigrationTests` 26 e processamento/queries reais PASS. Evidência anterior detalhada no [gate foundation](foundation-single-influx/README.md); não repetimos todos os comandos manuais legados. |
| T085 alertas | `TelemetryAlertsTests` 1 PASS; regras locais e notificações mutadas. Controles temporais/dados de teste não certificam retenção de provedor nem breach real de SLO. |

As contagens por classe são subconjuntos dos **845** testes, não totais adicionais.
Fixtures têm dados/ciclos de vida distintos; não montar uma alegação end-to-end inexistente.

## Gates ainda necessários

- Corrigir/implementar o probe de publicação durável na seleção real da fila, sem simular
  confirmação de capacidade nem publicar telemetria falsa; validar readiness positiva e
  falhas de conexão/rota/permissões/confirm. Reexecutar o pequeno smoke desde o início.
- Reexecutar a sequência integrada do guia, incluindo as verificações de Grafana do mesmo
  ponto e os passos interrompidos. O novo preparador não inclui Grafana.
- Resolver/reexecutar [T094](../performance/telemetry-capacity-report.md): histórico de
  **2026-10-17**, baseline 311,44/500 eventos/s confirmados e target 4.199,17/5.000,
  reconciliação incompleta, soaks bloqueados. **Não são resultados novos da T098**.
- Executar steady/burst/outage e dedup conforme gates; `--scenario dedup-strategy`
  do guia original era inválido e foi corrigido para o teste `IdentityGuardBenchmarkTests`.
  O benchmark SQL é protótipo, não aprovação do adapter/pipeline.
- Implementar T101 e executar T102 para exatamente 100.000 eventos, contagem independente
  e Grafana interno/viewer. Só então reavaliar o `[X]` da T098.

Após a falha sequencial, a execução de novos cenários foi interrompida conforme
`speckit-implement`: **“Halt execution if any non-parallel task fails”**.
Somente diagnóstico, preservação de evidências e documentação continuaram.

## Evidências e preservação

- `artifacts/validation/T098/run-20261020/`: `environment.json`, `steps.json`,
  `integration-step.json`, TRX/logs e metadados das duas tentativas.
- `artifacts/validation/T098/6a3002ac95e24fdd89043a951bea9916/`: tentativa inicial,
  configuração privada e `summary.json` com erro/site.
- `artifacts/validation/T098/d70b6a31e32f428a9a7e676f1b60ce22/`: hashes de source,
  limites/imagens inspecionados, preflight/Prometheus, `quickstart-smoke.json`
  (`passed: false`), `summary.json`, `readiness-diagnostic.json` e logs de cleanup.

As pastas brutas podem conter credenciais e dados sintéticos protegidos; não publicar seus
JSON/logs/volumes. Apenas este relatório revisado recebe exceção exata no `.gitignore`.
Teardown preserva volumes para análise; não houve exclusão de dados nem envio externo.
Verificação final: **zero containers remanescentes e quatro volumes preservados por
projeto** (`cleanup.json`). Sintaxe Python validada com
`py -3 -m py_compile scripts/Run-CapacityGates.py scripts/quickstart_smoke.py`.
Os campos de último HTTP de readiness/horário final do helper foram acrescentados após
as tentativas; essa mudança de metadados recebeu somente a validação de sintaxe.
