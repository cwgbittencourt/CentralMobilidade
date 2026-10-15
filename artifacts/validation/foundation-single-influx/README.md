# T112 — gate da fundação com InfluxDB único

Estado: gate T112 concluído. T050 liberada para implementação, mas não iniciada.

## Ambiente e escopo

Execução local em 2026-10-14 (America/Sao_Paulo), timestamps Docker em 2026-10-15 UTC.
.NET SDK 10.0.401, Docker Engine 29.8.0, Compose 5.5.1. Imagens fixadas no Compose:
InfluxDB 3.11.6 Core, PostgreSQL 17.11, RabbitMQ 4.3.6, Grafana 12.2.5 + Infinity 4.1.1.
Checklist requirements.md: 16/16; nenhum extension hook configurado.

O gate valida a fundação antes de T050. T050/T051/T052/T053 não foram implementadas.
Não equivale a aceite do processamento completo, dashboards, capacidade ou recuperação de host.

## Comandos e resultados já obtidos

- `dotnet build CentralMobilidade.slnx --no-restore --verbosity minimal`: zero avisos/erros, inclusive após correção localizada de fixture.
- `dotnet test tests/CentralMobilidade.Telemetry.UnitTests/CentralMobilidade.Telemetry.UnitTests.csproj --no-build --no-restore`: 242/242.
- `dotnet test tests/CentralMobilidade.Telemetry.ContractTests/CentralMobilidade.Telemetry.ContractTests.csproj --no-build --no-restore`: 58/58.
- `dotnet test tests/CentralMobilidade.Telemetry.ArchitectureTests/CentralMobilidade.Telemetry.ArchitectureTests.csproj --no-build --no-restore`: 47/47.
- Integração completa com `--filter FullyQualifiedName!~Measure_actual_per_identity_adapter`: 257 casos, 226 aprovados, 31 falhas, nenhum skip. Destas, 3 são T050 ausente, 21 T051 ausente e 7 adicionais em RabbitMQ/API. Evidência: integration.trx.
- Reexecução focada inicial de RabbitMqPublisherContractTests + AcceptedBatchSurvivalTests: 7/14 aprovados, as mesmas 7 falhas. Evidência: queue-recovery-recheck.trx. Essas falhas não foram classificadas como esperadas.
- `./scripts/Test-SingleInfluxTopology.ps1`: configurações base e desenvolvimento/app/emulador aprovadas com interpolação sintética, sem expor segredos.

A integração aprovou todos os 26 casos SingleTelemetryStoreMigrationTests, seis upgrades populados
(duas versões anteriores x Reserved/Writing/Committed), 20 casos de saúde e 41 casos do writer
(incluindo três com Influx real). A mesma credencial aceita dois IDs ausentes do cadastro na fronteira
implementada de intake; parser/guard/writer e permissões possuem provas separadas. Isso não demonstra
uma execução ponta a ponta de T051, que continua ausente.

## Ajustes encontrados durante o gate

1. Modelo documental ainda continha storage_location retirado na T106: removido.
2. Descrições de 404 no OpenAPI sugeriam cadastro de empresa: alinhadas a indisponibilidade/escopo, sem alterar wire shape/status/schema.
3. Plan/tasks ainda apresentavam o planejamento original como estado atual: texto histórico esclarecido.
4. Fixture RabbitMQ aplicava 500 ms inclusive a declaração fria da topologia e publicações normais. Casos normais alinhados aos 5 s da produção; o cenário explícito de timeout mantém 500 ms. Regras de confirmação, retorno, nack e correlação não foram removidas.
5. Script novo de smoke inicialmente passou duas linhas como argumentos ao CLI Influx, que exige um argumento ou arquivo: corrigido para um arquivo com duas linhas. A primeira tentativa também encontrou saída do RabbitMQ na inicialização; a segunda atingiu seis serviços saudáveis antes da falha do comando de escrita. Esses ensaios não comprovam persistência.

## Reconciliação e limites

PostgreSQL real em fixtures isoladas verifica instalação nova, migração repetível, upgrade com dados,
proveniência/fingerprint/prazos, replay/conflito, retomada, fencing, retenção e proteção de truncate.
Os testes mantêm as recusas de guards/promoções legadas não reconciliadas, evidência ausente/conflitante,
lease ativo, rollback integral da página e restrições de privilégios. As provas de leitura de pontos
usadas pelo SQL de reconciliação são sintéticas; o script SQL não consulta catálogos Influx remotos.

Não foram encontrados containers/volumes do projeto padrão no inventário inicial. Catálogos externos
não foram inspecionados. Nenhum dado de implantação foi migrado. Instalações populadas continuam
obrigadas ao procedimento offline de docs/runbooks/single-influx-upgrade.md antes da troca.

ADR-001 continua registrando aproximadamente 393–595 eventos/s no adapter real contra meta de 5.000.
O benchmark foi preservado e excluído desta execução funcional; não há aprovação de capacidade.

Os projetos de smoke são exclusivos e recebem credenciais sintéticas. Seus volumes e arquivos de
token são preservados; containers são encerrados sem --volumes. private-docker.log pode conter
configuração sintética privada e não deve ser publicado nem adicionado ao Git. Este README contém
somente resultados e dados de teste públicos.

## Smoke concluído

`./scripts/Test-SingleInfluxRecovery.ps1 -SkipBuild` passou em 2026-10-15T02:11:39Z no projeto
`centralmobilidade-t112-f23d87ca04`. As imagens foram construídas nas tentativas anteriores; SkipBuild
reutiliza essas mesmas imagens. A primeira execução construiu Loki e Grafana/Infinity antes de
falhar na inicialização do RabbitMQ. A segunda chegou a seis servidores saudáveis e dois jobs com
exit zero, mas parou na chamada incorreta do CLI Influx. Esses resultados iniciais foram mantidos.

A execução corrigida verificou seis servidores saudáveis (PostgreSQL, RabbitMQ, InfluxDB, Prometheus,
Loki, Grafana), dois jobs com exit zero (Influx/quarentena) e exatamente um servidor + um init Influx.
Gravou uma linha PostgreSQL e dois eventos gzip, empresas t112-a/t112-b, mesmo veículo `shared`,
timestamp `2026-10-15T00:00:00.123`, valores distintos. Comparou o CSV integral após reinício e após
`down`/`up`, verificando identidades, valores e precisão. Os sete marcadores de volumes também
sobreviveram. A inicialização repetida preservou o banco. O mesmo token/node-id/volume foi reutilizado.

O override de teste adiciona somente o mount do script sintético e usa porta Grafana efêmera; não
habilita apps, não desabilita autenticação e não modifica os YAMLs de produção. Ao final os containers
e a rede exclusivos do teste foram removidos sem remover volumes. Prova detalhada local:
`centralmobilidade-t112-f23d87ca04/result.json`. Os volumes das três tentativas ficam preservados.
Marcadores provam persistência de arquivos, não semântica de RabbitMQ/Grafana/Loki; confirmação e
recuperação de envelopes RabbitMQ são avaliadas pelos testes específicos. Não há prova de perda
física do host, restauração de backup ou processamento T051.

Somente este README é permitido pelo .gitignore; TRX, logs e arquivos de token permanecem ignorados.

## Revalidação da fila e diagnóstico do teste de crash

Depois de alinhar a fixture ao prazo de produção e encerrar o smoke, a rodada de fila/API passou
13/14. Todos os 12 contratos RabbitMQ passaram: gzip/metadados, topologia durável, retries/DLQ,
confirmação correlacionada, ACK alheio, nack, timeout, retorno obrigatório e privacidade. O controle
de crash também passou. Somente o POST do cenário principal excedeu o HttpClient.Timeout=10s.
Evidência: queue-recovery-final.trx. A execução inicial completa não foi apagada nem declarada verde.

O cliente do harness foi ajustado para 45s: o limite anterior era menor que o orçamento de upload
real de 30s, antes mesmo da publicação/estado. Não foram alterados timeouts de produção, status,
confirmação, conteúdo nativo, persistência, instante do kill, contagem de mensagens ou redelivery.
Esse cenário prova sobrevivência do envelope após 202; o SLA de latência/capacidade continua sendo
um gate separado, sem aprovação por esta mudança. A reexecução somente do cenário ainda falho é registrada abaixo; o controle de crash já passou.

## Resultado final e liberação

`accepted-crash-final.trx`: 1/1 aprovado após alinhar o orçamento do cliente de teste. O cenário exigiu
202, envelope íntegro/persistente com o mesmo BatchId, kill real do processo, disponibilidade em nova
conexão, redelivery e ack manual. O controle de crash já havia passado na rodada 13/14. Portanto,
os 14 casos distintos de fila/API estão validados considerando as reexecuções, sem alterar o publisher.
A suíte completa não foi repetida após as duas correções localizadas de prazos dos harnesses.

| Verificação | Resultado |
| --- | --- |
| Unitários | 242/242 |
| Contratos | 58/58 |
| Arquitetura | 47/47 |
| Integração: casos distintos aprovados, considerando reexecuções | 233 |
| Integração: TDD ainda não implementado | 3 T050 + 21 T051 |
| Outras falhas sem revalidação aprovada | 0 |
| Build final | 0 avisos, 0 erros |
| Topologia base/desenvolvimento/perfis | aprovada |
| Reinício e recriação Compose com dados | aprovados |

Total: 580 casos distintos aprovados entre as quatro suítes; 24 falhas TDD preservadas. A execução
inicial permaneceu com 31 falhas; as reexecuções explicam as sete adicionais e não apagam o histórico.
Prazos funcionais de teste não constituem aprovação de latência do produto. ADR-001 e futuros gates
de carga/produção continuam bloqueando alegações de capacidade.

### Revisão cruzada da fundação

- Constituição 2.0.0 / FR-019 / research / plan: produtor autoritativo, uma credencial de ingestão e um destino. Sem allowlist de ingestão; autorização de apresentação permanece separada.
- Modelo / EF / migrações: sete entidades, sem campos de localização/promoção. Schema inicial aplicado preservado; somente migração forward com recusas/reconciliação explícitas.
- Contratos Line Protocol / OpenAPI / fila: empresa_id preservado, validação fechada e bytes gzip nativos. Descrições 404 alinhadas a escopo/disponibilidade; nenhum schema/status/versionamento foi alterado.
- Tasks / quickstart: T104–T112 antecedem T050; pendência do ciclo Accepted não se confunde com banco de empresas desconhecidas. Processador, consumidor e dashboards continuam futuros.
- Código / Compose / documentação: Queryable único, DNS influxdb:8181, um init, volume/node-id queryable preservados, credenciais de infraestrutura distintas do produtor.
- Privacidade / durabilidade: permissões, auditoria, lease/fencing, retenção, identidade imutável, confirmação da fila e reconciliação parcial continuam cobertos. Nenhum workflow de promoção/consulta de existência é necessário.

Nenhum achado bloqueante da adequação permanece aberto. A liberação refere-se ao trabalho T050;
não autoriza trocar uma implantação legada não reconciliada nem aprova produção, HA ou throughput.

Comandos finais adicionais:

```powershell
dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests/CentralMobilidade.Telemetry.IntegrationTests.csproj --no-build --no-restore --filter "FullyQualifiedName~RabbitMqPublisherContractTests|FullyQualifiedName~AcceptedBatchSurvivalTests" --logger "trx;LogFileName=queue-recovery-final.trx" --results-directory artifacts/validation/foundation-single-influx
dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests/CentralMobilidade.Telemetry.IntegrationTests.csproj --no-build --no-restore --filter "FullyQualifiedName~Accepted_envelope_survives_abrupt_API_termination_after_durable_acknowledgement" --logger "trx;LogFileName=accepted-crash-final.trx" --results-directory artifacts/validation/foundation-single-influx
```

Todos os artefatos TRX foram gravados com --results-directory artifacts/validation/foundation-single-influx
e nomes correspondentes às suítes. O benchmark excluído é somente Measure_actual_per_identity_adapter;
a suíte LoadTests/performance não foi executada nem aprovada. Nenhum teste foi marcado como skip.
