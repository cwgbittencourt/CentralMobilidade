# Revisão técnica de controles LGPD — T097

Data: 2026-10-20, America/Sao_Paulo. Base revisada: commit `145aa84`.
**Revisão técnica concluída; decisões do DPO/jurídico pendentes. Este documento
não é parecer jurídico, certificação de conformidade LGPD ou autorização de produção.**

## Escopo e método

Inspeção de código, contratos, configuração, testes existentes e runbooks do gateway,
worker, PostgreSQL, RabbitMQ, InfluxDB, quarentena, enriquecimento e Grafana. Foram
considerados a [constituição](../../.specify/memory/constitution.md), o
[plano](../../specs/001-telemetry-ingestion/plan.md), os requisitos FR-007,
FR-020–FR-022, FR-026–FR-029, FR-031 e FR-035–FR-041 da
[especificação](../../specs/001-telemetry-ingestion/spec.md), e os contratos
[Line Protocol](../../specs/001-telemetry-ingestion/contracts/line-protocol-v1.md),
[fila](../../specs/001-telemetry-ingestion/contracts/queue-envelope.schema.json) e
[HTTP](../../specs/001-telemetry-ingestion/contracts/openapi.yaml).

“Implementado” abaixo significa encontrado no repositório, com testes identificados;
não significa comprovado no ambiente real. Evidências históricas em `tasks.md`
não foram reexecutadas nesta revisão documental. Não foram usados dados pessoais
reais, consultados cadastros de produção, alteradas políticas ou enviados pedidos
de aprovação. As pendências não recebem aceite implícito pelo fechamento da T097.

## Inventário e circulação dos dados

| Superfície | Dados e finalidade técnica | Destino/limite relevante |
| --- | --- | --- |
| Ingestão e fila | Gzip original com empresa, veículo, horário, coordenadas, medidas, motorista opcional e placa opcional; transporte e processamento | API → RabbitMQ → worker; o corpo original pode conter campos inválidos e identidade indevida antes da validação |
| Telemetria válida | Esquema fechado: IDs, posição, horário e medidas; placa/motorista opcionais | Única database `telemetry` no InfluxDB; consultas internas e apresentação autorizada |
| Controle operacional | Batch/trace/producer IDs, escopos de empresa, resultados e códigos de rejeição | PostgreSQL; guards também contêm identidade empresa/veículo/instante e evidência de fingerprint |
| Quarentena | Conteúdo bruto rejeitado e metadados; diagnóstico administrativo | Filesystem protegido e metadados PostgreSQL; pode conter PII arbitrária enviada fora do esquema |
| Enriquecimento | Nome de exibição e CPF recebido da fachada cadastral; projeção só de nome e últimos quatro dígitos opcionais | Adapter HTTP na API; cache limitado da projeção, resposta autorizada e Grafana |
| Auditoria | Ator técnico, organização Grafana quando aplicável, empresa, ação, recurso opaco, resultado e correlação | PostgreSQL append-only; a correlação pode permitir associação indireta |
| Observabilidade | Códigos operacionais, métricas agregadas e correlação permitida | Logs/Prometheus/Loki/spans; políticas excluem payload, placa, coordenadas, identidade e segredos |
| Backups/exportações | Cópias dos stores, fila, quarentena e configurações conforme escopo do backup | Runbook operacional; cópias podem manter dados que já expiraram na origem |

IDs de motorista/veículo, placa, trajetória e horários não são tratados nesta revisão
como dados anônimos. A separação cadastral reduz exposição, mas associação por IDs,
placa, rotina ou localização permanece possível. Nome mascarado/CPF parcial também
não demonstra anonimização. Classificação jurídica e necessidade de cada campo
ficam para a avaliação contextual do DPO/jurídico.

O produtor interno usa uma credencial compartilhada e é autoridade para IDs válidos
de empresa; não há validação de existência cadastral nem allowlist na ingestão.
Isso não autoriza visualização por qualquer empresa: os controles de leitura são
independentes. Erros de atribuição do produtor continuam sendo risco operacional
e de privacidade; precisam de processo de correção e responsabilização aprovado.

## Matriz de controles e limites observados

| ID | Controle encontrado e evidência | Limite ou pendência |
| --- | --- | --- |
| CT-01 | Esquema fechado proíbe nome, CPF, telefone e identidade direta no fluxo válido; [contrato](../../specs/001-telemetry-ingestion/contracts/line-protocol-v1.md) | Não impede que o produtor envie PII no corpo original, em campos inválidos ou em IDs sintaticamente válidos; fila/quarentena precisam da mesma proteção contextual |
| CT-02 | [Autenticação/autorização](../../src/CentralMobilidade.Telemetry.Api/Security/README.md): produtor separado de operador/viewer, credenciais verificadas por hash salted PBKDF2, escopo server-side e privilégio humano explícito para acesso entre empresas | Credencial de ingestão compartilhada amplia impacto de comprometimento; revisar distribuição, rotação/revogação e acesso administrativo no deployment |
| CT-03 | [Handlers de apresentação](../../src/CentralMobilidade.Telemetry.Application/Presentation/PresentationHandlers.cs) e [consultas](../../src/CentralMobilidade.Telemetry.Infrastructure/Presentation/README.md): consultas limitadas, escopo e revalidação de autorização; sem SQL arbitrário para viewer | Organização interna privilegiada mantém acesso direto ao Influx; acesso ao host, datasource e tokens pode contornar a API. Não há atribuição individual do viewer no MVP |
| CT-04 | [Adapter cadastral](../../src/CentralMobilidade.Telemetry.Infrastructure/Cadastral/ConfiguredCadastralAdapter.cs) minimiza CPF antes de projetar/cachear; cache por empresa/motorista, até 4.096 entradas, TTL padrão de um minuto/máximo cinco | CPF completo transita na resposta da fachada e memória durante parsing; nome e sufixo continuam identificáveis. HTTP é aceito pelo adapter; HTTPS/autenticação efetivos precisam de configuração revisada. Expiração lógica não prova limpeza imediata da memória |
| CT-05 | [Auditoria](../../src/CentralMobilidade.Telemetry.Infrastructure/Auditing/PostgresAuditWriter.cs), [política](../../src/CentralMobilidade.Telemetry.Infrastructure/Auditing/README.md) e handlers de identidade/quarentena: barreira durável antes da divulgação; falha de audit impede divulgação sensível | Append-only não é imutabilidade contra DBA/host. Não se afirma que toda leitura comum de telemetria produz auditoria de sucesso. Retenção/descarte do audit não estão resolvidos pela limpeza de 30 dias |
| CT-06 | [Quarentena](../../src/CentralMobilidade.Telemetry.Infrastructure/Quarantine/FileSystemQuarantineStore.cs): ACL/mode restritos, integridade, IDs opacos, acesso bruto autorizado/auditado e deadline | **Não há criptografia no adapter**. Hash não cifra nem protege contra escrita maliciosa pelo mesmo usuário/administrador. O campo `encrypted_content_location` do [modelo](../../specs/001-telemetry-ingestion/data-model.md) não comprova implementação de criptografia; divergência registrada para reconciliação |
| CT-07 | [Logging](../../src/CentralMobilidade.Telemetry.Infrastructure/Observability/PrivacyLoggingPolicy.cs), [spans](../../src/CentralMobilidade.Telemetry.Infrastructure/Observability/PrivacySpanProcessor.cs) e [teste de vazamento](../../tests/CentralMobilidade.Telemetry.IntegrationTests/Privacy/ObservabilityLeakTests.cs) restringem projeção e verificam valores semeados em logs, métricas, OTLP e Loki | Testes cobrem cenários/encodings definidos; não provam ausência universal de PII. Reverse proxy, host, browser, datasource, exports e fornecedores precisam de avaliação própria; não há garantia por simples presença do filtro |
| CT-08 | [Cleanup PostgreSQL](../../src/CentralMobilidade.Telemetry.Infrastructure/Persistence/PostgresOperationalRetentionCleanup.cs) e [worker](../../src/CentralMobilidade.Telemetry.Worker/Retention/OperationalRetentionWorker.cs): paginação, exclusão de conteúdo antes de esquecer metadados, retries posteriores e proteção dos guards | Falhas/corrupção podem atrasar exclusão; deadlines não significam apagamento físico imediato. Monitorar execução, backlog e objetos que falham; não alongar prazos para ocultar falhas |
| CT-09 | [Compose](../../deploy/compose.yaml), [Loki](../../deploy/loki/loki-config.yaml), retenção nativa e [testes](../../tests/CentralMobilidade.Telemetry.IntegrationTests/Retention/OperationalRetentionTests.cs) definem diferentes ciclos de dados | Retenção de consulta não equivale a sanitização do disco, réplica, cache, backup ou exportação. Cinco anos e capacidade ainda não aprovados |
| CT-10 | [API](../../src/CentralMobilidade.Telemetry.Api/Program.cs) suporta forwarded headers e proxies configurados; [hardening](../../deploy/README.md) define restrições de containers/secrets | HTTP local não prova TLS em produção, inclusive nos enlaces internos/cadastrais. Não foi demonstrada criptografia de volumes/backups nem custódia/rotação/restauração das chaves |
| CT-11 | [Backup/restore](../runbooks/backup-restore.md) exige cópia consistente, isolamento e reconciliação; [GCS futuro](../runbooks/gcs-migration.md) registra localização, IAM e retenção | Runbook não comprova backup externo criptografado ou RPO/RTO real. Exportações, soft delete, versionamento, holds e restores podem prolongar disponibilidade dos dados |
| CT-12 | [T096](security-check-results.md) entrega scanners de dependências, imagens, segredos, licenças, assinatura e versões | Gate real reprovado, com achados a triar e três imagens-base indisponíveis. Não converter T096 concluída em aceite de segurança |

Os testes [PresentationAuthorizationTests](../../tests/CentralMobilidade.Telemetry.IntegrationTests/Privacy/PresentationAuthorizationTests.cs),
[DriverEnrichmentSafetyTests](../../tests/CentralMobilidade.Telemetry.ContractTests/Cadastral/DriverEnrichmentSafetyTests.cs),
[AuditPolicyContractTests](../../tests/CentralMobilidade.Telemetry.ContractTests/Auditing/AuditPolicyContractTests.cs),
[PostgresAuditWriterTests](../../tests/CentralMobilidade.Telemetry.IntegrationTests/Persistence/PostgresAuditWriterTests.cs) e
[QuarantineStoreTests](../../tests/CentralMobilidade.Telemetry.IntegrationTests/Quarantine/QuarantineStoreTests.cs)
contêm verificações dos controles acima. Esta revisão leu suas definições e os
registros anteriores de validação; não lhes atribui um novo resultado de execução.

## Retenção e eliminação: política técnica atual

| Categoria | Configuração/evidência atual | Aprovação ainda necessária |
| --- | --- | --- |
| Fila não processada | Janela de sete dias; payload bruto pode conter dados rejeitáveis | Justificativa, risco de expiração, incidentes e recuperação; não é política de arquivo de longo prazo |
| Telemetria | Database local de 60 dias; cinco anos é desenho futuro condicionado | Necessidade/proporcionalidade por finalidade, marco inicial, obrigação/exceção e comprovação da eliminação |
| Batches/resultados/quarentena | Até 30 dias; sweeps periódicos e deadlines próprios | Adequação dos prazos, tratamento de falhas, staging/corrupção e cópias externas |
| Evidência de fingerprint | Janela operacional de 30 dias | Justificativa de manutenção e descarte; fingerprint não é anonimização certificada |
| Guards de identidade | Cobrem a retenção da telemetria; não expiram só porque a evidência de 30 dias foi removida | Conciliar eliminação/retificação aprovada e imutabilidade/idempotência, sem reaparecimento por replay |
| Cache cadastral | TTL padrão de um minuto, teto de cinco, capacidade 4.096; projeção reduzida | Expiração, correção/revogação e tratamento da memória; não substitui eliminação na fonte |
| Prometheus/Loki | 30 dias na configuração; Loki usa compactor e exclusão assíncrona | Acesso, finalidade, resíduos históricos, descarte e políticas dos demais exportadores/backends |
| Audit append-only | Não é excluído pelo cleanup operacional de 30 dias | Prazo explícito, necessidade de cada identificador, acesso e procedimento de descarte compatível com integridade |
| Backups/exports/browser/tracing externo | Sem ciclo completo de descarte comprovado nesta revisão | Inventariar destinos/cópias e definir prazo, bloqueio, eliminação, restauração e comprovação |

Não há fluxo completo de atendimento a titulares identificado no escopo revisado.
APIs operacionais de consulta/retention não são um serviço de confirmação, acesso,
correção, portabilidade, oposição ou eliminação do titular. A solução deve validar
identidade e autorização do solicitante fora da credencial do produtor, localizar
dados por empresa/veículo/motorista/tempo com validação humana, aplicar a decisão
aprovada e registrar evidência mínima do atendimento sem copiar PII para logs.

O desenho desse processo precisa tratar fila/reentrega, quarentena, guards,
cadastro/cache, telemetria, audit, exports e backups. Não executar exclusão ad hoc
nem modificar eventos imutáveis/guards para atender um pedido sem desenho revisado:
uma restauração ou replay pode reintroduzir dados, e uma obrigação de conservação
pode exigir outra decisão. Definir bloqueio, exceções, reconciliação e verificação
pós-restore com DPO/jurídico e engenharia.

## Registro de decisões pendentes

Todos os itens abaixo estão **PENDENTES**, sem responsável nominal, prazo ou aceite
recebido. Os papéis indicados são responsáveis propostos a confirmar pelo controlador;
não equivalem a atribuição de função jurídica a uma pessoa/empresa existente.
Registrar decisão, aprovador autorizado, data, versão/evidência e revisão prevista
antes de considerar um item resolvido. Não basta marcar a T097 como concluída.

| ID | Decisão/entrega necessária | Papéis propostos | Evidência de encerramento |
| --- | --- | --- | --- |
| AP-01 | Definir controlador(es), operador(es), encarregado/canal e responsabilidades do produtor, empresas, hospedagem e fornecedores | Direção + jurídico + DPO | Mapa de agentes/contratos e responsáveis nominais aprovados |
| AP-02 | Aprovar finalidade e hipótese/base aplicável por operação, incluindo monitoramento de trabalhadores, geolocalização e associação cadastral; não presumir consentimento | Controlador + jurídico/DPO | Registro de operações e justificativa por finalidade, necessidade de placa/motorista/CPF parcial e restrições de uso |
| AP-03 | Avaliar risco de associação/reidentificação, acesso privilegiado e uso secundário; decidir necessidade e conteúdo de RIPD | DPO + jurídico + segurança | Avaliação contextual/RIPD quando aplicável, medidas e decisão documentadas |
| AP-04 | Aprovar transparência e informação aos titulares, compartilhamentos, canal e processo de direitos | Controlador + DPO/jurídico | Avisos/processo aprovados, validação de identidade e responsáveis/prazos definidos pela política aplicável |
| AP-05 | Aprovar matriz de retenção, conservação/exceções, audit, backups e eliminação verificável; justificar 60 dias e eventual cinco anos separadamente | DPO/jurídico + dados/operações | Tabela aprovada por finalidade/store, inclusive descarte e evidência pós-restore |
| AP-06 | Projetar atendimento de correção/bloqueio/eliminação sem violar isolamento, integridade e idempotência | Engenharia + DPO/jurídico | Fluxo aprovado e ensaiado com dados sintéticos, incluindo replay, queue, guards, caches e backups |
| AP-07 | Definir destinos/localizações e subprocessadores; avaliar transferências internacionais e condições contratuais antes de GCS/Pub/Sub/OTLP externos | Jurídico/DPO + cloud/operações | Inventário de regiões/destinos e mecanismo/contratos aprovados; plano futuro não é transferência já executada |
| AP-08 | Aprovar requisitos de criptografia em trânsito/repouso, custódia de chaves e restrições a administradores/exports; reconciliar descrição da quarentena | Segurança + operações + DPO | Configuração demonstrada e restore/decrypt testado em isolamento; gap de criptografia tratado em mudança revisada |
| AP-09 | Definir revisão periódica de acesso, rotação/revogação, segregação da organização Grafana privilegiada e suficiência da atribuição por organização | Segurança + controlador/DPO | Matriz de acesso e evidência operacional; se necessário, desenho de identidade humana confiável |
| AP-10 | Aprovar resposta a incidente, critérios de avaliação/comunicação e preservação mínima de evidência | Segurança + DPO/jurídico | Runbook com responsáveis/canais e exercício; validar obrigações/prazos vigentes sem presumir comunicação automática |
| AP-11 | Triar achados T096, disponibilidade de imagens, capacidade T094/T095 e riscos de host único antes do release | Segurança + engenharia/operações + dono do release | Remediações verificadas e decisão formal sobre riscos residuais; exceções seguem a constituição |

## Conclusão técnica e validação desta entrega

Há controles implementados de minimização, escopo, auditoria antes de divulgação
sensível, observabilidade e retenção. Permanecem lacunas técnicas/de implantação e
decisões organizacionais/jurídicas identificadas nas matrizes. A revisão satisfaz
a entrega documental T097 e a exigência constitucional de distinguir evidência de
suposição e encaminhar decisões legais ao papel responsável. Não demonstra que
todos os requisitos constitucionais ou gates de release estão satisfeitos.

Validação desta mudança: conferência das referências locais, consistência de
requisitos/limitações com fontes e `git diff --check`. Não houve alteração de
código/configuração nem nova execução das suítes, carga, scanners ou ambientes.
Nenhuma aprovação foi solicitada/enviada em nome do usuário. T094/T095 e as
aprovações acima continuam independentes desta conclusão.

## Referências públicas consultadas

Referências consultadas em 2026-10-20 para orientar os assuntos que devem ser
avaliados pelos responsáveis, sem escolher base legal ou interpretar aplicabilidade
no lugar deles: [texto compilado da LGPD](https://www.planalto.gov.br/ccivil_03/_ato2015-2018/2018/lei/l13709compilado.htm),
[direitos dos titulares — ANPD](https://www.gov.br/anpd/pt-br/assuntos/titular-de-dados),
[glossário ANPD](https://www.gov.br/anpd/pt-br/documentos-e-publicacoes/glossario-anpd) e
[guias de agentes/encarregado e segurança — ANPD](https://www.gov.br/anpd/pt-br/centrais-de-conteudo/materiais-educativos-e-publicacoes).
