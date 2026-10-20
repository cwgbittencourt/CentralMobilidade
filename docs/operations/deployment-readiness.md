# Checklist de prontidão para implantação — T100

Revisão documental: **2026-10-20**, base `d44f299`. Decisão atual: **BLOQUEADO para
release de produção**. A T100 entrega este checklist; concluí-la não aprova os gates.
Nenhuma implantação, mudança de configuração ou nova execução de testes/scanners foi
realizada nesta revisão. Resultados anteriores abaixo conservam sua data e seu escopo.

Fontes normativas: [constituição 2.0.0](../../.specify/memory/constitution.md),
[especificação](../../specs/001-telemetry-ingestion/spec.md),
[plano](../../specs/001-telemetry-ingestion/plan.md) e
[tarefas](../../specs/001-telemetry-ingestion/tasks.md).
Os PASS de desenho no plano não são aprovação operacional do ambiente.

## Como usar e registrar a decisão

Copie este checklist para um registro privado da release e preencha:

| Campo obrigatório | Valor a preencher pelo responsável |
| --- | --- |
| Release e escopo | Commit, alterações locais revisadas, contratos/migrations, perfil e garantias pretendidas |
| Ambiente | Contexto Docker, projeto Compose, arquivos/overrides/profiles exatos, arquitetura, host e armazenamento |
| Artefatos | IDs/digests das imagens realmente implantadas, origem do build e versões de runtime/plugins |
| Responsáveis | Nomes de engenharia, operações, segurança, privacidade/jurídico e decisor da release |
| Evidências | Data/fuso, comandos, revisão/configuração sanitizada, relatório, resultados e localização privada dos dados brutos |
| Operação | Janela, fechamento/reabertura do intake, observação, critérios de abortar e caminho de recuperação |
| Decisão | BLOQUEADO/APROVADO, data, responsáveis, riscos residuais e referências a exceções válidas |

Marque `[x]` somente após verificar o critério no escopo declarado, com evidência
rastreável e responsável nominal. Ausência de evidência, teste ignorado, métrica
ausente e gate incompleto permanecem abertos. Evidência histórica ou fixture isolada
serve de referência; exige revisão de aplicabilidade à imagem/configuração candidata.
Qualquer mudança relevante invalida o aceite correspondente até nova verificação.
Não inclua tokens, configurações resolvidas, payloads ou dados pessoais no registro público.

Os responsáveis nas tabelas são **papéis propostos**, sem atribuição nominal nem aceite.
Todos os itens permanecem desmarcados para o próximo deployment. Um item condicional
(cloud/cutover) só pode ser registrado como não aplicável com justificativa de escopo,
responsável e evidência de que a capacidade não foi habilitada nem prometida.

## Bloqueios conhecidos na base revisada

| Gate | Evidência disponível | Pendência para fechamento | Responsável proposto |
| --- | --- | --- | --- |
| Readiness / quickstart | [T098](../../artifacts/validation/quickstart-results.md), 2026-10-20: contratos 154/154 e integração 845/845 PASS; host real live 200 / ready **503** | Implementar/registrar o probe real de publicação durável e reexecutar o guia inteiro; T098 `[ ]` | Engenharia + operações |
| Carga / soak | [T094](../../artifacts/performance/telemetry-capacity-report.md), 2026-10-17: baseline 311,44/500 e target 4.199,17/5.000 eventos/s confirmados; reconciliação incompleta | Corrigir e passar baseline/target, soaks, queryability temporal e demais gates de carga; T094 `[ ]` | Engenharia + performance |
| Storage / retenção | [T095](../../artifacts/performance/storage-capacity-report.md), 2026-10-20: amostra real e projeções condicionais | Dimensionamento, envelhecimento/cleanup, margem, WAL/backups e backend reais; cinco anos não habilitados/aprovados | Dados + operações |
| Segurança | [T096](../security/security-check-results.md), 2026-10-20: 5 PASS / 18 FAIL | Triar achados de 14 imagens, quatro revisões de licença e três bases indisponíveis; corrigir e repetir scans das imagens candidatas | Segurança + engenharia |
| Privacidade | [T097](../security/lgpd-technical-controls.md), 2026-10-20: revisão técnica entregue | Fechar AP-01–AP-11 com responsáveis competentes e evidências; sem aprovação legal implícita | Controlador + DPO/jurídico + segurança |
| Prova final / rastreabilidade | T101, T102 e T103 `[ ]` em [tasks.md](../../specs/001-telemetry-ingestion/tasks.md) | Implementar/executar exatamente 100.000 eventos com Grafana e completar matriz produção–testes | Engenharia + qualidade |
| Host único | Fixtures de restart/restore e runbooks existem | Sem prova de sobrevivência à perda física, cópia independente ou RPO/RTO operacional aprovado | Operações + decisor da release |

Os achados de segurança são candidatos para triagem, não prova automática de exploração
ou de credencial ativa vazada. Projeções de storage não são capacidade aprovada.
Não há exceção constitucional concedida por este documento.

## Gates de engenharia e aceitação

### I — Aceitação durável e recuperação

- [ ] **D01 — Confirm antes de `202`.** Demonstrar confirmação durável na rota real,
  bytes gzip nativos, falha/timeout/confirm ambíguo sem sucesso falso e status durável.
  No consumo, ACK somente após escrita e todos os outcomes/quarentena duráveis.
- [ ] **D02 — Recuperação e idempotência.** Passar redelivery e quedas em cada fronteira
  API/worker/broker/Influx/PostgreSQL; preservar lotes confirmados, leases/fences e
  reservas incompletas. Distinguir um evento lógico de uma única escrita de rede.
- [ ] **D03 — Retry e poison.** Verificar backoff exponencial limitado com jitter,
  cancelamento/shutdown, isolamento de poison e continuidade de tráfego saudável.
  Não remover mensagens/guards nem forçar estados para fazer contagens coincidirem.
- [ ] **D04 — Retenção da fila.** Demonstrar disponibilidade de não processados por
  até sete dias, TTL/expiração e limites reais de disco/idade, inclusive após restart;
  extrapolação curta não comprova sete dias (SC-010).

Referências: [contrato da fila](../../specs/001-telemetry-ingestion/contracts/queue-envelope.schema.json),
[configuração dos providers](../../src/CentralMobilidade.Telemetry.Infrastructure/DependencyInjection/README.md),
[backup/restore](../runbooks/backup-restore.md). Responsáveis: engenharia + operações.

### II — Streaming e recursos limitados

- [ ] **B01 — Limites independentes.** Testar UTF-8/gzip, bytes comprimidos e
  descomprimidos, expansão, tamanho de linha, contagem e tempo; rejeitar payload
  inválido/truncado sem corpo inteiro descomprimido em memória.
- [ ] **B02 — Batching e pressão.** Fechar escrita no primeiro limite de pontos,
  bytes ou tempo; padrão máximo de 5.000 pontos. Qualquer aumento precisa de medida
  e justificativa. Demonstrar backlog limitado, 429/503 explícitos e observabilidade
  de saturação, spool/tmpfs e esgotamento de recursos sem confirmação falsa.
- [ ] **B03 — Capacidade representativa.** Passar baseline de 10.000 veículos e
  target de 100.000 a cada 20 s: 5.000 eventos/s, 8 vCPUs/16 GiB de aplicação,
  CPU sustentada ≤85%, memória ≤12 GiB, sem OOM. Registrar limites efetivos e
  recursos dos stores separadamente, p50/p95/p99, bytes, disco, rejeições e backlog.
- [ ] **B04 — SLO, burst e soak.** Provar 99% queryable em até cinco minutos com
  medição independente; burst de 10.000 veículos/uma hora de backlog em dez minutos
  junto de 5.000 eventos/s, sem perda confirmada e drain ≤20 min; executar soaks.
  Verificar status/erros p95 <2 s e latest/trip p95 <5 s no envelope validado
  (SC-001–SC-003, SC-006–SC-007). A política mais estrita do runner reprova 429/503;
  documentar a diferença para a alternativa de backpressure explícita de SC-003.

Referências: [limites do contrato](../../specs/001-telemetry-ingestion/contracts/line-protocol-v1.md),
[guia de carga](../../tests/CentralMobilidade.Telemetry.LoadTests/Scenarios/README.md),
[T094](../../artifacts/performance/telemetry-capacity-report.md).
Responsáveis: engenharia + performance + operações.

### III — Isolamento e privacidade

- [ ] **P01 — Autoridade do produtor e escopo.** A mesma credencial interna aceita
  IDs válidos de múltiplas empresas sem cadastro/allowlist/promotion; ID inválido
  continua rejeitado. Enriquecimento indisponível não bloqueia armazenamento nem
  visibilidade autorizada. Operador/viewer não herdam privilégios do produtor (SC-008).
- [ ] **P02 — Leituras sensíveis.** Testar escopo server-side, revogação, mínimo
  privilégio e `cross_company.read` humano explícito; identidade e raw quarantine
  exigem auditoria durável antes de divulgação e falham fechadas se o audit falhar.
  CPF/nome permanecem fora do Influx; motorista usa somente ID pseudônimo na
  telemetria e cadastro retorna somente projeção mínima.
- [ ] **P03 — Grafana.** Reconciliar duas vezes sem duplicações; organização interna
  privilegiada mantém datasource Influx, viewers somente Infinity/API limitada.
  Provar não divulgação de empresa não autorizada e ausência de Influx no viewer;
  testar limites de latest/trip/health e atribuição ao ator técnico/organização,
  sem inventar identidade humana (SC-012). Três credenciais administrativas/API
  distintas; token de leitura Influx separado.
- [ ] **P04 — Privacidade observável.** Passar canários em logs, Loki, labels e OTLP,
  com saídas não vazias e controles de vazamento; sem payload, placa, coordenadas,
  nome, CPF ou segredos (SC-009). Revisar também proxy, browser, exports e backups.
- [ ] **P05 — Segredos, transporte e controles físicos.** Demonstrar hashes/secret
  sources protegidos, rotação/revogação, TLS fora de desenvolvimento e forwarded
  headers somente de proxies confiáveis; revisar ACLs, cifragem/custódia e restore
  de chaves. Quarentena atual **não cifra o conteúdo**: o nome de um campo/hash
  não comprova criptografia. Resolver o gap e a divergência documental conforme AP-08.
- [ ] **P06 — Decisões de privacidade.** Obter decisões registradas AP-01–AP-11 da
  [revisão técnica](../security/lgpd-technical-controls.md), incluindo finalidade,
  retenção/audit/backups, direitos, acessos, incidentes e destinos/fornecedores.
  Engenharia não concede aprovação do DPO/jurídico nem afirma conformidade legal.

Referências: [segurança da API](../../src/CentralMobilidade.Telemetry.Api/Security/README.md),
[acessos Grafana](grafana-access.md), [hardening](../../deploy/hardening.md).
Responsáveis: engenharia + segurança + operações + controlador/DPO/jurídico.

### IV — Contratos explícitos, versão e imutabilidade

- [ ] **C01 — Compatibilidade.** Revisar OpenAPI, Line Protocol fechado, envelope,
  códigos e retenções; executar testes de compatibilidade. `empresa_id` permanece
  o wire tag; `DataHoraEvento` determina UTC/event-time e ordenação de atrasados.
  Mudança incompatível exige versão e janela de migração documentadas.
- [ ] **C02 — Replay e conflito.** Preservar identidade empresa/veículo/instante
  e campos originais durante toda a retenção da telemetria; repetir lote não cria
  evento lógico extra. Evidência de fingerprint classifica replay/conflito por
  30 dias, separada do guard de 60 dias local/futuro horizonte (SC-005).
  ADR-001/benchmark SQL não aprova automaticamente o adapter em carga real.
- [ ] **C03 — Sucesso parcial e lifecycle.** Reconciliar 100% válidos e inválidos
  do lote misto, posições/códigos estáveis, contadores e status duráveis;
  raw só em quarentena protegida, sem vazamento nos metadados (SC-004).
  Demonstrar cleanup de status/quarentena em até 30 dias, com deadlines e
  impacto de backups/restore considerados (SC-010).

Referências: [contratos](../../specs/001-telemetry-ingestion/contracts/openapi.yaml),
[reconciliação T099](../../artifacts/validation/contract-conformance.md),
[research/decisões](../../specs/001-telemetry-ingestion/research.md).
Responsáveis: engenharia + dados + qualidade.

### V — Operação observável, testável e portátil

- [ ] **O01 — Readiness real.** Verificar API pronta somente com PostgreSQL durável
  e capacidade real de publicação; worker com store e consumidor fresco.
  Testar falhas de rota, permissões, conexão e confirmação. `/health/live`, probe
  falso e `up --wait` baseado em liveness não substituem readiness. Fechar a
  ausência de `IDurablePublicationProbe` encontrada pela T098.
- [ ] **O02 — Observabilidade utilizável.** Demonstrar correlação trace/batch e
  métricas de taxa, bytes, outcomes, duplicatas, retries, falhas de escrita,
  latência, profundidade e idade da fila. Conferir todos os targets habilitados,
  exporters/`pg_up` e heartbeat. Delivery age e idade de batch pendente não são
  prova exata da cabeça da fila; séries ausentes continuam ausentes.
- [ ] **O03 — Alertas e resposta.** Validar dashboards operacionais, NoData/Error,
  pressão de disco, falhas, backlog e consumidor parado. As notificações locais
  estão permanentemente mutadas: definir e testar o processo operacional necessário
  antes de depender de alerta externo, sem presumir que ele já existe.
- [ ] **O04 — Paridade e mudança de provider.** Executar contrato comum RabbitMQ e
  emulador Pub/Sub, preservando regras/bytes/confirm/ACK. Se Pub/Sub gerenciado
  entrar no escopo, validar recursos, IAM, retenção e recuperação no serviço real;
  emulador não comprova durabilidade cloud. Cutover segue o runbook com drain,
  verificação e rollback; dual-publish/bridge atuais não estão implementados.
  Confirmar também adapter cadastral substituível sem alterar regras de domínio.
- [ ] **O05 — Prova completa.** Passar unit, arquitetura, contratos, integração,
  retry/restart/recovery, privacidade, carga e soak aplicáveis. Reexecutar todo o
  [quickstart](../../specs/001-telemetry-ingestion/quickstart.md) com resultados.
  T101/T102 devem reconciliar **exatamente 100.000 eventos** pela API gzip, ≥10
  empresas/1.000 veículos, ordered/delayed/trip e Grafana interno/viewer
  latest/trip/ingestion-total (SC-011). Completar rastreabilidade T103 de todo
  projeto de produção e requisito implementado para testes.

Referências: [health](../../src/CentralMobilidade.Telemetry.Infrastructure/Health/README.md),
[Prometheus](../../deploy/prometheus/README.md), [Loki](../../deploy/loki/README.md),
[alertas](../../deploy/grafana/provisioning/alerting/README.md),
[cutover](../runbooks/queue-provider-cutover.md). Responsáveis: engenharia + operações + qualidade.

## Plataforma, dados e processo de release

- [ ] **R01 — Baseline e artefatos.** Confirmar .NET LTS suportado, imagens Docker
  reproduzíveis e compatíveis, versões/digests e assinatura do plugin. Rodar scans
  de dependências/imagens/segredos/licenças/pins sobre os artefatos candidatos;
  findings ou cobertura incompleta não recebem PASS. Triar e fechar os achados
  com decisões documentadas da [T096](../security/security-check-results.md).
- [ ] **R02 — Configuração efetiva e privilégios.** Revisar o modelo Compose com
  os mesmos arquivos/profiles do start, sem divulgar secrets. Verificar identidade
  não root, rootfs read-only, capabilities, quotas CPU/RAM/PID, tmpfs/spool,
  mounts e secrets legíveis só pelos UIDs/ACLs necessários. Isolar portas/redes;
  inicializadores terminam zero. Grant de runtime PostgreSQL separado de migrations.
- [ ] **R03 — Stores e migração.** PostgreSQL é controle/audit/guards, não store
  primário de telemetria. Um único Influx Core/queryable `telemetry`, node-id e
  catálogo preservados, 60 dias local. Antes de retirar store pending legado,
  inventariar/reconciliar pontos, guards e conflitos, sem descartar dados
  confirmados ou reescrever migrations aplicadas. Verificar índices, grants e
  contagens em esquema novo e upgrade populado.
- [ ] **R04 — Storage e horizonte.** Validar custo medido de Parquet, PostgreSQL
  heap/índices/TOAST/guards/evidência, WAL, fila, quarentena, logs e backups no
  hardware/backend real; incorporar crescimento, folga e cleanup. Cinco anos
  são plano futuro condicionado a capacidade e política aprovadas; não trocar
  apenas `60d` por cinco anos. GCS não adiciona HA ao Core single-node.
- [ ] **R05 — Recuperação coordenada.** Capturar corte consistente dos sete volumes
  do runbook, imagens/configuração e segredos/chaves em custódia protegida;
  verificar manifest/checksums e origem. Restaurar em volumes novos isolados,
  preservar identidades/nós/fences e reconciliar lotes/pontos antes de reabrir intake.
  Medir RPO/RTO e testar cópia independente; restart local não é esse ensaio.
- [ ] **R06 — Mudança e rollback.** Registrar janela, dono, critérios numéricos de
  abortar, parada/drain e observação conforme carga validada; testar rollback
  compatível com novos dados/schema. Manter o prazo de shutdown do worker dentro
  do grace period efetivo do container. Não misturar upgrades de imagem, store e
  provider sem plano próprio. Não regredir PostgreSQL/guards isoladamente nem
  regenerar token de catálogo existente. Nunca usar `down --volumes`/prune como
  procedimento normal de implantação, recovery ou correção de gate.
- [ ] **R07 — Governança e decisão.** Confirmar spec → plan → tasks → implementação
  → análise de consistência, ADRs, contratos revisados e testes por mudança.
  Registrar conformidade com cada grupo deste checklist, assinar a decisão e
  rever exceções antes da janela. Reavaliar após mudança de escopo/configuração.

Referências: [deploy](../../deploy/README.md), [hardening](../../deploy/hardening.md),
[migrações/grants](../../src/CentralMobilidade.Telemetry.Infrastructure/Persistence/Migrations/README.md),
[upgrade single Influx](../runbooks/single-influx-upgrade.md),
[storage](../../artifacts/performance/storage-capacity-report.md),
[GCS](../runbooks/gcs-migration.md), [backup/restore](../runbooks/backup-restore.md).
Responsáveis: engenharia + dados + operações + segurança + decisor da release.

## Riscos residuais do host único

Todos continuam **abertos** nesta base. Responsáveis nominais, prazos e aceite
devem ser preenchidos no registro; este documento não os concede.

| Risco | Impacto / limite atual | Mitigação e prova exigidas | Responsável proposto |
| --- | --- | --- | --- |
| H01 — Perda de host/disco/VM | Volumes nomeados e fila durável no mesmo host podem desaparecer juntos; sem garantia física de recuperação | Cópia protegida em domínio de falha independente, custódia das chaves e restore em outro host com reconciliação/RPO/RTO medidos | Operações |
| H02 — Indisponibilidade correlacionada | Core, PostgreSQL, broker e Grafana compartilham energia/rede/host; restart/volume não é HA | Plano de indisponibilidade, backpressure e recuperação; arquitetura independente validada antes de prometer disponibilidade que exija HA | Arquitetura + operações |
| H03 — Pressão de disco e I/O | Backlog, guard, WAL, compaction, logs e backup competem; fila de sete dias não cabe por definição | Dimensionamento/quotas/margem, monitoramento acionável e ensaio de saturação/recuperação sem perda confirmada | Dados + operações |
| H04 — Competição de CPU/RAM | Docker Desktop e gerador compartilham hardware; limites de teste não isolam disco/host nem provam produção | Ensaios representativos, recursos inspecionados e baseline de interferência; não extrapolar resultado de fixture curta | Performance + operações |
| H05 — Comprometimento privilegiado | Host/DBA pode ler fila, raw, tokens e backups ou alterar audit; append-only não protege contra administrador | Segregação, ACL/cifragem/chaves e cópia de evidência com custódia independente, revisão de acessos e resposta a incidente | Segurança + operações |
| H06 — Restore ou rollback divergente | Copiar stores de instantes diferentes ou perder guards/chaves pode reabrir identidade, perder audit ou sobrescrever lógica | Corte coordenado, versões/identidades preservadas, validação independente e rollback sem regressão isolada de metadados | Engenharia + dados + operações |
| H07 — Observabilidade no mesmo host | Falha derruba coleta/dashboards; alertas locais mutados não avisam externamente | Definir monitoramento/escalação fora do domínio de falha e testar o caminho antes de assumir cobertura | Operações |

## Fechamento e exceções

**Regra de decisão:** qualquer gate obrigatório aberto, FAIL ou evidência faltante
mantém a release BLOQUEADA. Não converter ausência de erro, completion de tarefa,
saúde de processo ou aceite informal em autorização de produção.

A constituição permite apenas exceção **escrita e limitada no tempo**, identificando
regra afetada, risco, responsável, mitigação e data de remoção. O registro deve ainda
conter escopo, justificativa, evidência, decisor competente, condição de revogação e
trabalho para remover a exceção. Encaminhar decisões legais ao papel de privacidade
responsável; exceção de engenharia não substitui aprovação jurídica. Exceção vencida
ou sem dono volta a bloquear. Nenhuma exceção foi criada ou aprovada aqui.
Um gate coberto por exceção conserva seu resultado original: registrar eventual
liberação limitada separadamente, sem trocar FAIL por PASS ou omitir o teste.

Para liberar, anexar evidências revisadas, decisão nominal/data e riscos residuais
explicitamente tratados, conforme governança do projeto. Nesta revisão, readiness,
carga, segurança, decisões de privacidade e prova final continuam pendentes.

Validação da T100: cobertura documental dos princípios I–V, restrições técnicas,
workflow/governança e SC-001–SC-012; referências locais e whitespace conferidos.
Nenhum novo resultado funcional, carga, scan ou restore é reivindicado.
