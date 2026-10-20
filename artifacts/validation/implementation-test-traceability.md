# Rastreabilidade implementação → testes — T103

Revisado em **2026-10-20**, base **`c37deaa`**, sem mudança em código de produção.
Escopo: seis projetos `src`, cinco projetos de testes, **FR-001–FR-043** e
**SC-001–SC-012** da [especificação][spec]. A versão atual da constituição é 2.0.0:
empresa válida é reconhecida pelo produtor; pending storage/promotion foram removidos.
Notas antigas de tarefas são histórico, não componentes que precisam ser recriados.

Cada linha liga comportamento implementado, produção e verificação automatizada.
Isso é rastreabilidade de comportamento, **não percentual de cobertura de linhas**,
nem prova de que todo branch foi executado. Arquitetura prova dependências, não
substitui testes funcionais. As metas operacionais têm seus limites explícitos abaixo.

## Projetos de produção e limites de responsabilidade

| Projeto | Implementação e fronteira coberta | Projetos/verificação automatizada |
| --- | --- | --- |
| [Domain][domain-project] | Identificadores/UTC, TelemetryEvent, lifecycle, resultados/rejeições e auditoria | [UnitTests][unit-project]: [ValueObjectTests][values-tests], [TelemetryEventTests][events-tests], [IngestionBatchTests][batch-domain-tests], [ProcessingEntityTests][entities-tests]; [ArchitectureTests][arch-project] verifica referências declaradas, restauradas e assemblies |
| [Contracts][contracts-project] | DTOs v1, erros, envelope nativo e correspondência com contratos autorais | [ContractTests][contract-project]: [ApiDtoSerializationTests][dto-tests], [AuthoredContractConformanceTests][authored-tests], [TelemetryBatchEnvelopeTests][envelope-tests], [TelemetryErrorCodesTests][errors-tests]; arquitetura |
| [Application][app-project] | Opções, autenticação/hash, portas, ingestão, parser, processamento, batching, visibilidade e apresentação | [UnitTests][unit-project]: [AcceptTelemetryBatchHandlerTests][accept-tests], [LineProtocolV1GoldenTests][parser-tests], [ProcessingPolicyTests][policy-tests], [PresentationHandlerTests][presentation-unit-tests], [TelemetryOptionsTests][options-tests]; [IntegrationTests][integration-project]: [ProcessingRealStorageTests][real-processing-tests], [BatchInspectionTests][inspection-tests]; arquitetura |
| [Infrastructure][infra-project] | Spool/gzip; RabbitMQ/PubSub; PostgreSQL/migrations/guard; Influx; quarantine; cadastral; auditoria; observabilidade/health/DI | [IntegrationTests][integration-project]: famílias detalhadas abaixo; [UnitTests][unit-project]: provider/error/config safety; [ContractTests][contract-project]: portas/envelope/auditoria; arquitetura |
| [Api][api-project] | Composition root, autenticação/policies, ingest/status/rejeições, apresentação, health e transporte | [IntegrationTests][integration-project]: [IntakeBoundaryTests][intake-tests], [BearerSecurityTests][security-tests], [BatchStatusEndpointTests][status-tests], [BatchRejectionEndpointTests][rejection-tests], [PresentationAuthorizationTests][auth-tests], [HealthEndpointTests][health-endpoint-tests]; [ContractTests][contract-project]: ingest/status; [LoadTests][load-project]: [prova real T102][proof]; arquitetura |
| [Worker][worker-project] | Host, processamento/ACK/retry/shutdown e cleanup leased | [IntegrationTests][integration-project]: [WorkerHostTests][worker-tests], [WorkerAcknowledgementBoundaryTests][ack-tests], [OperationalRetentionTests][retention-tests], [OperationalRetentionLeaseTests][lease-tests], [WorkerHeartbeatMetricsTests][heartbeat-tests]; [LoadTests][load-project]: outage/steady/burst e T102; arquitetura |

### Componentes Infrastructure e artefatos operacionais

| Produção | Testes que exercitam a fronteira |
| --- | --- |
| [Streaming][spool] e gzip | [BoundedRequestSpoolTests][spool-tests], [GzipBoundaryPropertyTests][gzip-tests], [IntakeBoundaryTests][intake-tests] |
| [RabbitMQ][rabbit], confirms, recovery e controles de readiness | [RabbitMqTopologyTests][topology-tests], [RabbitMqPublisherContractTests][publisher-tests], [RabbitMqConsumerContractTests][consumer-tests], [DurablePublicationProbeTests][probe-tests], [AcceptedBatchSurvivalTests][survival-tests] |
| [Pub/Sub][pubsub] e [seleção por configuração][queue-di] | [PubSubEmulatorTests][pubsub-tests], [PubSubAdapterSafetyTests][pubsub-safety-tests], [QueueProviderHostTests][provider-tests]; emulator não comprova durabilidade GCP, readiness Pub/Sub segue fail-closed |
| [PostgreSQL][db], mappings e migrations | [ControlMigrationIntegrationTests][migration-tests], [SingleTelemetryStoreMigrationTests][single-store-tests], [BatchTransitionReplayMigrationTests][replay-migration-tests], [PostgresBatchStateStoreTests][state-tests], [ControlPersistenceTests][persistence-tests] |
| [Guard][guard] e clocks de retenção | [SelectedEventIdentityGuardTests][guard-tests], [ProcessingRealStorageTests][real-processing-tests], [OperationalRetentionTests][retention-tests] e [benchmark][benchmark-tests]; janela longa/capacidade não inferida de teste pequeno |
| [Influx writer][writer] e [leitores][reader] | [InfluxV3WriterTests][writer-tests], [InfluxWriteBatcherTests][batcher-tests], [StorageAdapterSafetyTests][storage-tests], [PresentationReaderTests][reader-tests], [VehicleTripQueryTests][trip-tests] |
| [Quarantine][quarantine], [auditoria][audit], [enrichment][cadastral] e DI | [QuarantineStoreTests][quarantine-tests], [PostgresAuditWriterTests][audit-tests], [PresentationAuditPolicyTests][audit-policy-tests], [DriverEnrichmentRegistrationTests][enrichment-tests], [CadastralAdapterContractTests][cadastral-tests] |
| [Observabilidade][observability], [privacy logging][logging], [health][health] e métricas | [TelemetryObservabilityTests][observability-tests], [ObservabilityLeakTests][leak-tests], [OtlpTraceExportTests][otlp-tests], [TelemetryHealthTests][health-tests], [BatchMetricsPersistenceTests][metrics-tests], [LokiIngestionTests][loki-tests], [PrometheusScrapingTests][prometheus-tests] |
| [Compose][compose], imagens, volumes, backup e recuperação | [ComposeRecoveryTests][compose-tests], [BackupRestoreTests][backup-tests], [single-store recovery script][single-recovery]; nenhum desses prova recuperação de perda física sem backup independente |
| [Grafana/templates][grafana], [reconciliador][reconciler] e alertas | [GrafanaProvisioningTests][grafana-tests], [GrafanaReconciliationRuntimeTests][reconciliation-tests], [PlatformOperationsTests][platform-tests], [TelemetryAlertsTests][alert-tests], T102 |
| [Scans][scans] e [modelo de sizing][sizing] | [Test-SecurityChecks.ps1][scan-tests], [test_storage_projection_model.py][sizing-tests], [test_storage_capacity_measurement.py][storage-measure-tests]; implementação de scan não equivale a findings resolvidos |

## Requisitos funcionais

“Coberto” significa que há assertions automatizadas para o comportamento indicado.
Quando o requisito depende de operação/cloud/capacidade, a coluna final limita a
conclusão. Não há dispensa aprovada para comportamento de produção sem teste.

| Requisito | Produção | Verificação automatizada e alcance |
| --- | --- | --- |
| FR-001 | [Intake][ingest], [security][security] | [IntakeBoundaryTests][intake-tests], [BearerSecurityTests][security-tests], [IngestBatchContractTests][ingest-contract-tests]: gzip/UTF8/v1, credenciais e produtor único; T102 envia dez empresas sem allowlist |
| FR-002 | [Opções][options], [spool][spool], [parser][parser] | [TelemetryOptionsTests][options-tests], [IntakeBoundaryTests][intake-tests], [GzipBoundaryPropertyTests][gzip-tests]: limites independentes/charset/duração/cancelamento |
| FR-003 | [Accept handler][accept], [publisher][rabbit] | [RabbitMqPublisherContractTests][publisher-tests], [AcceptTelemetryBatchHandlerTests][accept-tests], [AcceptedBatchSurvivalTests][survival-tests]: confirmação antes de 202, ID/Location e crash |
| FR-004 | [Accept handler][accept], [publisher][rabbit] | [IntakeAdmissionGateTests][admission-tests], [RabbitMqPublisherContractTests][publisher-tests]: nack/timeout/indisponibilidade nunca viram aceitação |
| FR-005 | [Spool][spool], [gzip][gzip], [batcher][batcher] | [BoundedRequestSpoolTests][spool-tests], [GzipBoundaryPropertyTests][gzip-tests], [InfluxWriteBatcherTests][batcher-tests]: buffers/arquivos/linhas limitados, flush e cleanup |
| FR-006 | [Parser v1][parser], [domain][events] | [LineProtocolV1GoldenTests][parser-tests], [TelemetryEventTests][events-tests], [AuthoredContractConformanceTests][authored-tests]: schema fechado/campos/tipos/IDs |
| FR-007 | [Parser][parser] | [LineProtocolV1GoldenTests][parser-tests], [DriverEnrichmentSafetyTests][driver-safety-tests]: campos opcionais pseudônimos e rejeição de identidade direta |
| FR-008 | [UTC domain][timestamp], [parser][parser] | [ValueObjectTests][values-tests], [VehicleTripQueryTests][trip-tests], [FinalProofProtocolTests][final-protocol-tests] e T102: milissegundos/UTC/atrasados; leitura UTC sem offset não usa fuso local |
| FR-009 | [Process handler][process] | [ProcessingRealStorageTests][real-processing-tests], [WorkerAcknowledgementBoundaryTests][ack-tests]: válidos persistem e inválidos têm resultado durável separado |
| FR-010 | [Process handler][process], [erros][errors] | [ProcessingRealStorageTests][real-processing-tests], [TelemetryErrorCodesTests][errors-tests], [BatchRejectionEndpointTests][rejection-tests]: códigos/posição/digest/contexto seguro |
| FR-011 | [Quarantine][quarantine], [cleanup][cleanup] | [QuarantineStoreTests][quarantine-tests], [OperationalRetentionTests][retention-tests]: isolamento e expiração 30 dias por relógio controlado; não é espera física de 30 dias |
| FR-012 | [Guard][guard], [TelemetryEvent][events] | [SelectedEventIdentityGuardTests][guard-tests], [TelemetryEventTests][events-tests]: empresa/veículo/tempo independentes do fingerprint |
| FR-013 | [Guard][guard], [process][process] | [SelectedEventIdentityGuardTests][guard-tests], [ProcessingRealStorageTests][real-processing-tests]: concorrência/lease/fencing/recovery/commit e evidência 30 dias; viabilidade cinco anos permanece condicional |
| FR-014 | [Guard][guard], [writer][writer] | [SelectedEventIdentityGuardTests][guard-tests], [ProcessingRealStorageTests][real-processing-tests]: replay/conflito impedem segunda escrita/merge; clocks guard/evidence distintos |
| FR-015 | [Worker][worker], [consumer][consumer] | [WorkerHostTests][worker-tests], [WorkerAcknowledgementBoundaryTests][ack-tests], [AcceptedBatchSurvivalTests][survival-tests], [ComposeRecoveryTests][compose-tests]: redelivery/retry/ACK/shutdown/restarts |
| FR-016 | [Topology][topology], [opções][options] | [RabbitMqTopologyTests][topology-tests], [TelemetryOptionsTests][options-tests], [InfluxOutageScenarioTests][outage-tests]: TTL/retention e modelo de backlog; não prova sete dias reais nem durabilidade cloud |
| FR-017 | [Status endpoint][status], [status handler][status-handler] | [BatchStatusEndpointTests][status-tests], [BatchStatusContractTests][status-contract-tests], [BatchLifecycleTests][lifecycle-tests]: autenticação, projeção, estados/contadores/categorias |
| FR-018 | [Cleanup][cleanup], [retention worker][retention-worker] | [OperationalRetentionTests][retention-tests], [OperationalRetentionLeaseTests][lease-tests]: prazo 30 dias, idempotência, concorrência e leases |
| FR-019 | [Process][process], [single-store migration][single-migration] | [SingleTelemetryStoreMigrationTests][single-store-tests], [ProcessingRealStorageTests][real-processing-tests], [DriverEnrichmentRegistrationTests][enrichment-tests] e T102: um store, IDs válidos sem lookup/promotion |
| FR-020 | [Enrichment/DI][cadastral] | [CadastralAdapterContractTests][cadastral-tests], [DriverEnrichmentRegistrationTests][enrichment-tests], [PresentationAuthorizationTests][auth-tests]: ausência/falha cadastral não bloqueia telemetria autorizada |
| FR-021 | [Presentation handlers][presentation], [enrichment][cadastral] | [DriverEnrichmentSafetyTests][driver-safety-tests], [PresentationAuthorizationTests][auth-tests]: identidade fora do Influx, nome/últimos quatro CPF, masking e campos mínimos |
| FR-022 | [Policies][security], [audit][audit] | [PresentationAuthorizationTests][auth-tests], [BatchRejectionEndpointTests][rejection-tests], [PostgresAuditWriterTests][audit-tests]: permissão, escopo e auditoria de acesso sensível/cross-company |
| FR-023 | [Presentation][presentation], [Grafana][grafana] | [PresentationReaderTests][reader-tests], [GrafanaProvisioningTests][grafana-tests] e T102: latest/trip/health pelos dois caminhos reais |
| FR-024 | [Influx reader][reader] | [VehicleTripQueryTests][trip-tests] e T102: trajetória ordenada por event-time, apesar da chegada atrasada |
| FR-025 | [Observabilidade][observability], [health][health], [probe][probe] | [TelemetryObservabilityTests][observability-tests], [BatchMetricsPersistenceTests][metrics-tests], [WorkerHeartbeatMetricsTests][heartbeat-tests], [DurablePublicationProbeTests][probe-tests], [TelemetryHealthTests][health-tests]: indicadores, freshness e publicação real fail-closed |
| FR-026 | [Logging][logging] | [PrivacyLoggingTests][privacy-log-tests], [TelemetryObservabilityTests][observability-tests], [OtlpTraceExportTests][otlp-tests]: correlação permitida e remoção de dados proibidos |
| FR-027 | [Logging][logging], [spans][spans] | [ObservabilityLeakTests][leak-tests], [PrivacyLoggingTests][privacy-log-tests], [OtlpTraceExportTests][otlp-tests]: payload/quarantine não escapam para logs/métricas/traces |
| FR-028 | [Compose][compose], [cleanup][cleanup] | [ComposeRecoveryTests][compose-tests], [OperationalRetentionTests][retention-tests], [BackupRestoreTests][backup-tests]: volumes/restarts e retenção configurada 60 dias; não garante perda física de host |
| FR-029 | [Opções retenção][retention-options], [runbook GCS][gcs] | [OperationalRetentionOptionsTests][retention-options-tests], [modelo de sizing][sizing-tests]: configuração/projeções; migração, GCS/IAM, retenção real cinco anos e capacidade NÃO comprovados |
| FR-030 | [Queue DI][queue-di], [RabbitMQ][rabbit], [Pub/Sub][pubsub] | [QueueProviderHostTests][provider-tests], [PubSubEmulatorTests][pubsub-tests], [DurableBatchQueueContractTests][queue-contract-tests]: contrato/seleção por configuração; cutover cloud e readiness Pub/Sub ainda não aprovados |
| FR-031 | [Intake/transporte][ingest], [Compose][compose] | [IntakeBoundaryTests][intake-tests], [BearerSecurityTests][security-tests], [ComposeRecoveryTests][compose-tests]: HTTPS/forwarded headers/trusted proxies e HTTP development; certificados reais dependem do operador |
| FR-032 | [Batcher][batcher], [opções][options] | [InfluxWriteBatcherTests][batcher-tests], [TelemetryOptionsTests][options-tests]: primeiro threshold count/bytes/time, padrão <=5.000 e limites configurados |
| FR-033 | [Runner final][final-scenario], [preparador][final-fixture] | [Final100kGrafanaProofScenarioTests][final-tests], [FinalProofProtocolTests][final-protocol-tests]; [T102 real PASS][proof]: 200 lotes, 100.000 identidades/campos, queries e seis PNGs |
| FR-034 | [Projetos][app-project] e [regra de dependência][layers] | [LayerDependencyTests][layers], esta matriz e [validador][validator]: inventário/links/IDs completos; não medem cobertura de branches nem autorizam comportamento sem teste |
| FR-035 | [Raw endpoint][raw-endpoint], [raw handler][raw-handler] | [BatchRejectionEndpointTests][rejection-tests], [BatchInspectionTests][inspection-tests], [OperationalRetentionTests][retention-tests]: ID opaco, permissão, auditoria, expiração, resultado não divulgador |
| FR-036 | [Security][security], [visibilidade][visibility] | [BearerSecurityTests][security-tests], [PresentationAuthorizationTests][auth-tests], [BatchRejectionEndpointTests][rejection-tests], [BatchStatusEndpointTests][status-tests]: produtor separado de humano/service e cross-company explícito |
| FR-037 | [Audit][audit], [raw handler][raw-handler], [presentation][presentation] | [PresentationAuthorizationTests][auth-tests], [BatchRejectionEndpointTests][rejection-tests], [PresentationAuditPolicyTests][audit-policy-tests]: aguarda commit, denied/missing/expired, falha fecha acesso |
| FR-038 | [Visibility][visibility], [status handler][status-handler] | [BatchStatusEndpointTests][status-tests], [BatchInspectionTests][inspection-tests], [BatchRejectionEndpointTests][rejection-tests]: dono ou batch.read com todas empresas; unknown e forbidden têm mesmo 404 |
| FR-039 | [Grafana][grafana], [reconciliador][reconciler] | [GrafanaProvisioningTests][grafana-tests], [GrafanaReconciliationRuntimeTests][reconciliation-tests], [PresentationAuthorizationTests][auth-tests] e T102: orgs/credenciais distintas, viewer sem Influx, interno direto |
| FR-040 | [Endpoints apresentação][presentation-endpoints], [query safety][presentation] | [PresentationQuerySafetyTests][query-safety-tests], [PresentationAuthorizationTests][auth-tests], [PresentationReaderTests][reader-tests]: todos limites numéricos/SQL parametrizado/escopo/não divulgação; T102 verifica dados reais |
| FR-041 | [Presentation][presentation], [audit][audit] | [PresentationAuthorizationTests][auth-tests], [PresentationAuditPolicyTests][audit-policy-tests]: atribuição organização/service confiável, não header spoofado; sem atribuição humana individual no MVP |
| FR-042 | [Imagem Grafana][grafana-image], [Compose][compose] | [GrafanaProvisioningTests][grafana-tests], [GrafanaReconciliationRuntimeTests][reconciliation-tests], [scan tests][scan-tests]: versão/signature/health e falha segura se plugin errado; T102 usa versão real fixada |
| FR-043 | [Reconciliador][reconciler] | [GrafanaProvisioningTests][grafana-tests], [GrafanaReconciliationRuntimeTests][reconciliation-tests]: Admin Organizations só Basic, recursos só SA local, rotação/convergência, bootstrap não vai para datasource/API |

## Critérios mensuráveis e pendências de aceitação

| Critério | Automação/evidência | Conclusão limitada |
| --- | --- | --- |
| SC-001 | [SteadyStateScenarioTests][steady-tests], [AcceptedBatchSurvivalTests][survival-tests], [relatório capacidade][capacity] | Recovery funcional coberto; 5.000/s com p99 <=5 min não aprovado, T094 pendente |
| SC-002 | [SteadyStateScenarioTests][steady-tests], [relatório capacidade][capacity] | Cenário/limiares CPU/RAM implementados; perfil 100k veículos/load/soak não aprovado |
| SC-003 | [ReconnectionBurstScenarioTests][burst-tests], [relatório capacidade][capacity] | Schedule/contabilidade/backpressure/drain verificados por testes; burst real completo não aprovado |
| SC-004 | [ProcessingRealStorageTests][real-processing-tests], [InfluxV3WriterTests][writer-tests] | Mistos reais e parciais do provider cobertos; conferir categorias estáveis |
| SC-005 | [SelectedEventIdentityGuardTests][guard-tests], [ProcessingRealStorageTests][real-processing-tests], [benchmark][benchmark-tests] | Imutabilidade/replay/conflito/clocks cobertos; escala/cinco anos não inferidos |
| SC-006 | [BatchStatusEndpointTests][status-tests], [SteadyStateScenarioTests][steady-tests] | Semântica/latência no runner; p95 <2 s sob carga normal permanece gate T094 |
| SC-007 | [VehicleTripQueryTests][trip-tests], [GrafanaProvisioningTests][grafana-tests], [T102][proof] | Queries/renders reais funcionam; T102 não mede p95 <5 s com amostragem de carga |
| SC-008 | [ProcessingRealStorageTests][real-processing-tests], [single-store tests][single-store-tests], [T102][proof] | Dez IDs novos, credencial única, lookup ausente, único Influx/queryable e escopo preservado |
| SC-009 | [ObservabilityLeakTests][leak-tests], [OtlpTraceExportTests][otlp-tests], [PrivacyLoggingTests][privacy-log-tests] | Canários de PII/payload/token cobertos; [findings segurança][security-doc] e aprovação LGPD não encerrados |
| SC-010 | [ComposeRecoveryTests][compose-tests], [OperationalRetentionTests][retention-tests], [WorkerHostTests][worker-tests], [BackupRestoreTests][backup-tests] | Restart e relógios/TTL cobertos; sete dias cronológicos/perda física não certificados; quickstart T098 pendente |
| SC-011 | [FinalProofProtocolTests][final-protocol-tests], [T102 real][proof] | PASS real: 100.000 sem perda/duplicata, lotes terminais, três painéis por caminho |
| SC-012 | [GrafanaProvisioningTests][grafana-tests], [PresentationAuthorizationTests][auth-tests], [T102][proof] | Isolamento real org/datasource, viewer empresa00, empresa01 upstream404/zero linhas, interno direto |

## Evidências de execução e reprodução

Resultados T103 nesta base, **zero falhas e zero skips**:

| Suíte | Resultado atual | Reprodução |
| --- | --- | --- |
| UnitTests | 380/380 | `dotnet test tests/CentralMobilidade.Telemetry.UnitTests --no-build` |
| ContractTests | 154/154 | `dotnet test tests/CentralMobilidade.Telemetry.ContractTests --no-build` |
| ArchitectureTests | 47/47 | `dotnet test tests/CentralMobilidade.Telemetry.ArchitectureTests --no-build` |

TRX privados em `artifacts/validation/T103/tests/`. As suítes foram executadas
contra os assemblies já compilados da base, sem alteração de produção desde T113.
Para reproduzir do zero, executar `dotnet build CentralMobilidade.slnx` antes.

Execução **16:06:47–16:06:55 UTC−03:00**, 581/581 testes. Hashes SHA-256 dos TRX:

| TRX | SHA-256 |
| --- | --- |
| ArchitectureTests.trx | `4710e011a5284104ac5539850e687b36c6eb1bc62315ff70a7168619551f2bd4` |
| ContractTests.trx | `8aa5a6396fd3bb8e6f59b26b7a3b9f09186f74b08752cd4e17bc5ad4e303a5eb` |
| UnitTests.trx | `f7420a2ed3604cc8be6da472babc823c1dd124e80fa3a690876ea38047df387c` |

Integração: a nota [T113][tasks] registra 112/112 testes relevantes após correção
de timeout, incluindo oito novos casos do probe real, health, RabbitMQ, provider e
recovery. A execução completa anterior teve 852 PASS/1 FAIL, corrigida depois;
**não se apresenta a integração completa como rerodada/passa nesta T103**.
A evidência de [T098][quickstart-results] 845/845 é histórica, anterior à T113.
Em T102, 40/40 testes de corpus/protocolo e a execução real final passaram.
Os testes de protocolo com HTTP simulado não substituem a prova Docker real.

Exemplos de assertions verificáveis nos arquivos ligados:

- `Mixed_delivery_uses_real_guard_writer_quarantine_results_and_terminal_redelivery`
  em ProcessingRealStorageTests: integração dos providers reais e redelivery terminal.
- `Runtime_registration_publishes_a_persistent_control_to_the_actual_durable_queue`
  e `Resource_alarm_withholding_confirmation_fails_within_health_deadline`
  em DurablePublicationProbeTests: publicação e falha real com deadline.
- `Driver_identity_never_escapes_if_audit_cannot_prove_commit` e
  `Every_numeric_query_boundary_is_enforced_by_production_before_backend_IO`
  em PresentationAuthorizationTests: fail-closed e limites antes de I/O.
- `Full_protocol_reconciles_every_identity_fields_grafana_frames_and_six_PNGs`
  em FinalProofProtocolTests: controle do protocolo; execução real está em T102.

Validação estática desta matriz:

```powershell
py -3 scripts/Test-ImplementationTraceability.py
```

O validador exige uma linha para cada FR/SC atual, todos os projetos `src` e os
cinco projetos de teste, links locais existentes e teste `.cs` em cada FR
(FR-029 também liga o teste Python de projeção junto ao teste de opções).
Não decide se uma assertion demonstra semanticamente o requisito: essa revisão
está nas descrições de alcance. Novos requisitos/projetos exigem nova revisão.
Resultado nesta revisão: **PASS, 6 projetos source, 5 projetos de testes,
43 FR, 12 SC e 153 referências locais válidas**, todos os FR/SC com teste concreto.

T103 entrega rastreabilidade. T094/capacidade e T098/quickstart continuam pendentes;
T096 tem findings e T097 decisões de DPO/legal. GCS/cutover real, Pub/Sub readiness,
cinco anos, SLOs sob carga e perda física não recebem aprovação por esta matriz.
Nenhuma exclusão de teste ou autorização de release foi concedida.

## Referências locais

[spec]: ../../specs/001-telemetry-ingestion/spec.md
[tasks]: ../../specs/001-telemetry-ingestion/tasks.md
[proof]: 100k-final-proof/README.md
[capacity]: ../performance/telemetry-capacity-report.md
[quickstart-results]: quickstart-results.md
[security-doc]: ../../docs/security/lgpd-technical-controls.md
[validator]: ../../scripts/Test-ImplementationTraceability.py
[domain-project]: ../../src/CentralMobilidade.Telemetry.Domain/CentralMobilidade.Telemetry.Domain.csproj
[contracts-project]: ../../src/CentralMobilidade.Telemetry.Contracts/CentralMobilidade.Telemetry.Contracts.csproj
[app-project]: ../../src/CentralMobilidade.Telemetry.Application/CentralMobilidade.Telemetry.Application.csproj
[infra-project]: ../../src/CentralMobilidade.Telemetry.Infrastructure/CentralMobilidade.Telemetry.Infrastructure.csproj
[api-project]: ../../src/CentralMobilidade.Telemetry.Api/CentralMobilidade.Telemetry.Api.csproj
[worker-project]: ../../src/CentralMobilidade.Telemetry.Worker/CentralMobilidade.Telemetry.Worker.csproj
[unit-project]: ../../tests/CentralMobilidade.Telemetry.UnitTests/CentralMobilidade.Telemetry.UnitTests.csproj
[contract-project]: ../../tests/CentralMobilidade.Telemetry.ContractTests/CentralMobilidade.Telemetry.ContractTests.csproj
[integration-project]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/CentralMobilidade.Telemetry.IntegrationTests.csproj
[arch-project]: ../../tests/CentralMobilidade.Telemetry.ArchitectureTests/CentralMobilidade.Telemetry.ArchitectureTests.csproj
[load-project]: ../../tests/CentralMobilidade.Telemetry.LoadTests/CentralMobilidade.Telemetry.LoadTests.csproj
[values-tests]: ../../tests/CentralMobilidade.Telemetry.UnitTests/Domain/ValueObjectTests.cs
[events-tests]: ../../tests/CentralMobilidade.Telemetry.UnitTests/Domain/TelemetryEventTests.cs
[batch-domain-tests]: ../../tests/CentralMobilidade.Telemetry.UnitTests/Batches/IngestionBatchTests.cs
[entities-tests]: ../../tests/CentralMobilidade.Telemetry.UnitTests/Domain/ProcessingEntityTests.cs
[dto-tests]: ../../tests/CentralMobilidade.Telemetry.ContractTests/Api/V1/ApiDtoSerializationTests.cs
[authored-tests]: ../../tests/CentralMobilidade.Telemetry.ContractTests/Api/V1/AuthoredContractConformanceTests.cs
[envelope-tests]: ../../tests/CentralMobilidade.Telemetry.ContractTests/Messaging/V1/TelemetryBatchEnvelopeTests.cs
[errors-tests]: ../../tests/CentralMobilidade.Telemetry.ContractTests/Errors/TelemetryErrorCodesTests.cs
[accept-tests]: ../../tests/CentralMobilidade.Telemetry.UnitTests/Ingestion/AcceptTelemetryBatchHandlerTests.cs
[parser-tests]: ../../tests/CentralMobilidade.Telemetry.UnitTests/LineProtocol/LineProtocolV1GoldenTests.cs
[policy-tests]: ../../tests/CentralMobilidade.Telemetry.UnitTests/Processing/ProcessingPolicyTests.cs
[presentation-unit-tests]: ../../tests/CentralMobilidade.Telemetry.UnitTests/Presentation/PresentationHandlerTests.cs
[options-tests]: ../../tests/CentralMobilidade.Telemetry.UnitTests/Configuration/TelemetryOptionsTests.cs
[real-processing-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Recovery/ProcessingRealStorageTests.cs
[inspection-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Batches/BatchInspectionTests.cs
[intake-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Ingestion/IntakeBoundaryTests.cs
[security-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Security/BearerSecurityTests.cs
[status-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Batches/BatchStatusEndpointTests.cs
[rejection-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Batches/BatchRejectionEndpointTests.cs
[auth-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Privacy/PresentationAuthorizationTests.cs
[health-endpoint-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Health/HealthEndpointTests.cs
[worker-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Recovery/WorkerHostTests.cs
[ack-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Recovery/WorkerAcknowledgementBoundaryTests.cs
[retention-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Retention/OperationalRetentionTests.cs
[lease-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Retention/OperationalRetentionLeaseTests.cs
[heartbeat-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Observability/WorkerHeartbeatMetricsTests.cs
[spool-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Streaming/BoundedRequestSpoolTests.cs
[gzip-tests]: ../../tests/CentralMobilidade.Telemetry.UnitTests/Streaming/GzipBoundaryPropertyTests.cs
[topology-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Queue/RabbitMqTopologyTests.cs
[publisher-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Queue/RabbitMqPublisherContractTests.cs
[consumer-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Queue/RabbitMqConsumerContractTests.cs
[probe-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Queue/DurablePublicationProbeTests.cs
[survival-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Recovery/AcceptedBatchSurvivalTests.cs
[pubsub-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Queue/PubSubEmulatorTests.cs
[pubsub-safety-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Queue/PubSubAdapterSafetyTests.cs
[provider-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Queue/QueueProviderHostTests.cs
[migration-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Persistence/ControlMigrationIntegrationTests.cs
[single-store-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Persistence/SingleTelemetryStoreMigrationTests.cs
[replay-migration-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Persistence/BatchTransitionReplayMigrationTests.cs
[state-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Persistence/PostgresBatchStateStoreTests.cs
[persistence-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Persistence/ControlPersistenceTests.cs
[guard-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Recovery/SelectedEventIdentityGuardTests.cs
[benchmark-tests]: ../../tests/CentralMobilidade.Telemetry.LoadTests/Scenarios/IdentityGuardBenchmarkTests.cs
[writer-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Influx/InfluxV3WriterTests.cs
[batcher-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Influx/InfluxWriteBatcherTests.cs
[storage-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Influx/StorageAdapterSafetyTests.cs
[reader-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Queries/PresentationReaderTests.cs
[trip-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Queries/VehicleTripQueryTests.cs
[quarantine-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Quarantine/QuarantineStoreTests.cs
[audit-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Persistence/PostgresAuditWriterTests.cs
[audit-policy-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Persistence/PresentationAuditPolicyTests.cs
[enrichment-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Cadastral/DriverEnrichmentRegistrationTests.cs
[cadastral-tests]: ../../tests/CentralMobilidade.Telemetry.ContractTests/Cadastral/CadastralAdapterContractTests.cs
[observability-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Observability/TelemetryObservabilityTests.cs
[leak-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Privacy/ObservabilityLeakTests.cs
[otlp-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Observability/OtlpTraceExportTests.cs
[health-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Health/TelemetryHealthTests.cs
[metrics-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Observability/BatchMetricsPersistenceTests.cs
[loki-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Observability/LokiIngestionTests.cs
[prometheus-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Observability/PrometheusScrapingTests.cs
[compose-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Deployment/ComposeRecoveryTests.cs
[backup-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Deployment/BackupRestoreTests.cs
[single-recovery]: ../../scripts/Test-SingleInfluxRecovery.ps1
[grafana-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Grafana/GrafanaProvisioningTests.cs
[reconciliation-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Grafana/GrafanaReconciliationRuntimeTests.cs
[platform-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Grafana/PlatformOperationsTests.cs
[alert-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Grafana/TelemetryAlertsTests.cs
[scan-tests]: ../../scripts/Test-SecurityChecks.ps1
[sizing-tests]: ../../scripts/test_storage_projection_model.py
[storage-measure-tests]: ../../scripts/test_storage_capacity_measurement.py
[ingest-contract-tests]: ../../tests/CentralMobilidade.Telemetry.ContractTests/Api/V1/IngestBatchContractTests.cs
[admission-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Ingestion/IntakeAdmissionGateTests.cs
[driver-safety-tests]: ../../tests/CentralMobilidade.Telemetry.ContractTests/Cadastral/DriverEnrichmentSafetyTests.cs
[status-contract-tests]: ../../tests/CentralMobilidade.Telemetry.ContractTests/Api/V1/BatchStatusContractTests.cs
[lifecycle-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Batches/BatchLifecycleTests.cs
[privacy-log-tests]: ../../tests/CentralMobilidade.Telemetry.IntegrationTests/Observability/PrivacyLoggingTests.cs
[retention-options-tests]: ../../tests/CentralMobilidade.Telemetry.UnitTests/Configuration/OperationalRetentionOptionsTests.cs
[queue-contract-tests]: ../../tests/CentralMobilidade.Telemetry.ContractTests/Queue/DurableBatchQueueContractTests.cs
[final-tests]: ../../tests/CentralMobilidade.Telemetry.LoadTests/Scenarios/Final100kGrafanaProofScenarioTests.cs
[final-protocol-tests]: ../../tests/CentralMobilidade.Telemetry.LoadTests/Scenarios/FinalProofProtocolTests.cs
[query-safety-tests]: ../../tests/CentralMobilidade.Telemetry.UnitTests/Presentation/PresentationQuerySafetyTests.cs
[steady-tests]: ../../tests/CentralMobilidade.Telemetry.LoadTests/Scenarios/SteadyStateScenarioTests.cs
[burst-tests]: ../../tests/CentralMobilidade.Telemetry.LoadTests/Scenarios/ReconnectionBurstScenarioTests.cs
[outage-tests]: ../../tests/CentralMobilidade.Telemetry.LoadTests/Scenarios/InfluxOutageScenarioTests.cs
[layers]: ../../tests/CentralMobilidade.Telemetry.ArchitectureTests/LayerDependencyTests.cs
[spool]: ../../src/CentralMobilidade.Telemetry.Infrastructure/Streaming/BoundedRequestSpool.cs
[rabbit]: ../../src/CentralMobilidade.Telemetry.Infrastructure/Queue/RabbitMq/RabbitMqBatchPublisher.cs
[pubsub]: ../../src/CentralMobilidade.Telemetry.Infrastructure/Queue/PubSub/PubSubBatchQueue.cs
[queue-di]: ../../src/CentralMobilidade.Telemetry.Infrastructure/DependencyInjection/QueueProviderRegistration.cs
[db]: ../../src/CentralMobilidade.Telemetry.Infrastructure/Persistence/TelemetryControlDbContext.cs
[guard]: ../../src/CentralMobilidade.Telemetry.Infrastructure/Idempotency/SelectedEventIdentityGuard.cs
[writer]: ../../src/CentralMobilidade.Telemetry.Infrastructure/Influx/InfluxV3TelemetryWriter.cs
[reader]: ../../src/CentralMobilidade.Telemetry.Infrastructure/Presentation/InfluxPresentationReader.cs
[quarantine]: ../../src/CentralMobilidade.Telemetry.Infrastructure/Quarantine/FileSystemQuarantineStore.cs
[audit]: ../../src/CentralMobilidade.Telemetry.Infrastructure/Auditing/PostgresAuditWriter.cs
[cadastral]: ../../src/CentralMobilidade.Telemetry.Infrastructure/Cadastral/ConfiguredCadastralAdapter.cs
[observability]: ../../src/CentralMobilidade.Telemetry.Infrastructure/Observability/TelemetryObservability.cs
[logging]: ../../src/CentralMobilidade.Telemetry.Infrastructure/Observability/PrivacyLoggingPolicy.cs
[health]: ../../src/CentralMobilidade.Telemetry.Infrastructure/Health/TelemetryHealthChecks.cs
[compose]: ../../deploy/compose.yaml
[grafana]: ../../deploy/grafana/provisioning/
[reconciler]: ../../scripts/Provision-GrafanaOrganizations.ps1
[scans]: ../../scripts/Invoke-SecurityChecks.ps1
[sizing]: ../../scripts/storage_projection_model.py
[ingest]: ../../src/CentralMobilidade.Telemetry.Api/Endpoints/V1/IngestTelemetryBatchEndpoint.cs
[security]: ../../src/CentralMobilidade.Telemetry.Api/Security/OperatorAuthorizationPolicies.cs
[options]: ../../src/CentralMobilidade.Telemetry.Application/Configuration/TelemetryOptions.cs
[parser]: ../../src/CentralMobilidade.Telemetry.Application/LineProtocol/TelemetryLineProtocolV1Parser.cs
[accept]: ../../src/CentralMobilidade.Telemetry.Application/Ingestion/AcceptTelemetryBatchHandler.cs
[gzip]: ../../src/CentralMobilidade.Telemetry.Application/Streaming/BoundedGzipLineReader.cs
[batcher]: ../../src/CentralMobilidade.Telemetry.Application/Processing/InfluxWriteBatcher.cs
[events]: ../../src/CentralMobilidade.Telemetry.Domain/Telemetry/TelemetryEvent.cs
[timestamp]: ../../src/CentralMobilidade.Telemetry.Domain/ValueObjects/EventTimestamp.cs
[process]: ../../src/CentralMobilidade.Telemetry.Application/Processing/ProcessTelemetryBatchHandler.cs
[errors]: ../../src/CentralMobilidade.Telemetry.Contracts/Errors/TelemetryErrorCodes.cs
[cleanup]: ../../src/CentralMobilidade.Telemetry.Infrastructure/Persistence/PostgresOperationalRetentionCleanup.cs
[worker]: ../../src/CentralMobilidade.Telemetry.Worker/TelemetryProcessingWorker.cs
[consumer]: ../../src/CentralMobilidade.Telemetry.Infrastructure/Queue/RabbitMq/RabbitMqBatchConsumer.cs
[topology]: ../../src/CentralMobilidade.Telemetry.Infrastructure/Queue/RabbitMq/RabbitMqTopology.cs
[status]: ../../src/CentralMobilidade.Telemetry.Api/Endpoints/V1/GetBatchStatusEndpoint.cs
[status-handler]: ../../src/CentralMobilidade.Telemetry.Application/Batches/GetBatchStatusHandler.cs
[retention-worker]: ../../src/CentralMobilidade.Telemetry.Worker/Retention/OperationalRetentionWorker.cs
[single-migration]: ../../src/CentralMobilidade.Telemetry.Infrastructure/Persistence/Migrations/20261014230000_SingleTelemetryStore.cs
[presentation]: ../../src/CentralMobilidade.Telemetry.Application/Presentation/PresentationHandlers.cs
[probe]: ../../src/CentralMobilidade.Telemetry.Infrastructure/Queue/RabbitMq/RabbitMqDurablePublicationProbe.cs
[spans]: ../../src/CentralMobilidade.Telemetry.Infrastructure/Observability/PrivacySpanProcessor.cs
[retention-options]: ../../src/CentralMobilidade.Telemetry.Application/Configuration/OperationalRetentionOptions.cs
[gcs]: ../../docs/runbooks/gcs-migration.md
[final-scenario]: ../../tests/CentralMobilidade.Telemetry.LoadTests/Scenarios/Final100kGrafanaProofScenario.cs
[final-fixture]: ../../scripts/final_proof_fixture.py
[raw-endpoint]: ../../src/CentralMobilidade.Telemetry.Api/Endpoints/V1/GetQuarantinedRejectionContentEndpoint.cs
[raw-handler]: ../../src/CentralMobilidade.Telemetry.Application/Batches/GetQuarantinedRejectionContentHandler.cs
[visibility]: ../../src/CentralMobilidade.Telemetry.Application/Batches/BatchVisibility.cs
[presentation-endpoints]: ../../src/CentralMobilidade.Telemetry.Api/Endpoints/V1/Presentation/PresentationEndpoints.cs
[grafana-image]: ../../deploy/grafana/Dockerfile
