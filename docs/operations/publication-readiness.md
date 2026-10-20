# Readiness de publicação RabbitMQ — T113

`AddTelemetryQueue` registra `IDurablePublicationProbe` para RabbitMQ. A readiness
da API exige PostgreSQL durável e uma observação positiva desse probe; não depende
de Influx disponível nem de um worker ativo. O Pub/Sub permanece sem probe de
publicação e sua readiness continua falhando de forma fechada.

## O que é verificado

O probe usa a mesma conexão, credenciais e configuração de topologia do provider
de ingestão, cria um canal com confirms e tracking habilitados, declara/verifica a
topologia canônica durável e publica na **mesma exchange/routing key de ingestão**.
A publicação é persistente e mandatory; o SDK só conclui após a confirmação
correspondente e reprova NACK ou return. Falha de conexão, topologia incompatível,
permissão negada, bloqueio ou timeout não produz Ready=true. Não é ping TCP,
consulta management, resultado de startup ou confirmação numa rota auxiliar.

Assim como o publisher existente, a criação do canal declara resources/bindings
canônicos idempotentemente; pode recriar recursos próprios ausentes. Portanto,
reparo de um binding ausente não é registrado como uma falha permanente. Um
conflito de durabilidade/exclusividade/argumentos ou permissão insuficiente falha.

Cada observação usa `Health:ProbeTimeout` (padrão três segundos), incluindo espera
por acesso ao probe. Chamadas concorrentes compartilham observações de até cinco
segundos, incluindo falhas. Isso limita controles a um a cada cinco segundos por
processo; mudanças no broker podem demorar até esse intervalo para aparecer.
O resultado não autoriza retornar `202` sem a confirmação real do lote enviado.
O SDK pode demorar mais para fechar um canal bloqueado. O chamador continua
limitado pelo deadline; apenas uma operação/limpeza pendente é mantida por
instância e nenhuma substituta é iniciada até ela terminar. Durante essa limpeza
a readiness permanece não aprovada; a recuperação pode demorar além do cache.

## Contrato operacional v1

O envelope de negócio e seu schema v1 não mudam. O controle é um tipo operacional
separado, transportado no mesmo broker, com estes valores exatos:

| Campo AMQP | Valor |
| --- | --- |
| `Type` | `centralmobilidade.publication-probe.v1` |
| `ContentType` | `application/vnd.centralmobilidade.publication-probe.v1` |
| `Persistent` | true |
| `MessageId` | `health-` seguido de UUID não vazio, formato N |
| Body UTF-8/ASCII | `CM-PUBLICATION-PROBE-V1` |
| `ContentEncoding` | ausente/vazio |
| Headers | somente o header técnico `Constants.PublishSequenceNumberHeader` acrescentado pelo SDK, ou nenhum |

Não contém empresa, veículo, timestamp de evento, PII, bearer token ou batchId.
O consumidor reconhece somente a combinação exata antes do decoder de negócio,
faz ACK no canal protegido pelo mesmo lock de I/O e não entrega `IQueueDelivery`
ao worker. Não cria batch state, evento, guard, auditoria/quarentena ou métricas
de negócio. Controle malformado ou com headers de envelope não é silenciosamente
descartado: segue o tratamento de envelope inválido existente.

Controles são persistentes e seguem o TTL existente da fila, sem prazo curto que
pudesse expirar antes da confirmação. Se o worker estiver parado, permanecem no
backlog e aparecem na contagem/idade do broker até consumo ou expiração. Dimensionar
essa carga operacional (máximo 17.280 controles/dia/processo sob polling contínuo);
não interpretá-la como telemetria aceita. O TTL/dead-letter existente também se aplica.

## Implantação e compatibilidade

Implantar primeiro o worker que reconhece o controle, depois a API com o probe.
Um worker antigo classificaria o controle como envelope inválido e o enviaria ao
dead-letter; não usá-lo como validação deste contrato. Para rollback, parar primeiro
as novas sondagens/retornar a API anterior e drenar controles com o worker compatível
antes de regressar o consumidor. Não limpar DLQ ou mensagens de negócio para ocultar
uma implantação em ordem incorreta.

Rebuildar API **e worker**: imagens anteriores não contêm a alteração. O preparador
aceita `--api-image`/`--worker-image` para preservar imagens históricas:

```powershell
py -3 scripts/Run-CapacityGates.py --final-proof-preflight --api-image centralmobilidade-api:t113 --worker-image centralmobilidade-worker:t113
```

Esse comando verifica readiness em uma fixture nova e preserva seus volumes. Não
executa a T102 inteira, não provisiona Grafana nem envia o corpus final. T098/T102,
carga, scanners e prontidão de implantação mantêm seus próprios critérios.

## Verificação

```powershell
dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests --filter FullyQualifiedName~DurablePublicationProbeTests
```

Os controles usam RabbitMQ descartável real: registro DI/publicação persistente na
fila correta, coalescing concorrente, negação de escrita mantendo permissão de
topologia, topologia incompatível/recuperação, broker inacessível/cancelamento,
alarme de memória impedindo confirmação e exclusão do controle do processamento.
Permissões e alarme são alterados apenas dentro do container de teste próprio.

## Resultados T113 — 2026-10-20

| Verificação | Resultado |
| --- | --- |
| Suíte unitária | 380/380 PASS, zero skips |
| Contratos | 154/154 PASS, zero skips |
| Integração completa inicial | 852 PASS / 1 FAIL: alarme revelou 22 s de espera no fechamento do canal |
| Após corrigir separação do deadline/cleanup | 112/112 PASS, zero skips: oito testes novos + health, RabbitMQ, provider host e recovery |
| Build final da solução | Zero warnings/erros |
| API/worker reais em Docker, imagens finais | Live 200 / ready 200; dois warmups Completed; PostgreSQL on/on |

As três primeiras falhas da preparação inicial foram diagnosticadas: tipo/header
do SDK no reconhecimento do controle, fixture de fila temporária não permitida
pela versão do broker e consequente envio do controle ao dead-letter. Foram
corrigidas antes da regressão. A falha de cleanup encontrada na suíte completa
também foi corrigida e revalidada nas suítes afetadas; a suíte inteira não foi
repetida depois desse ajuste. Não somar execuções sobrepostas como casos distintos.

Imagens finais construídas pelos Dockerfiles do projeto, sem atualizar deployments:

- API `centralmobilidade-api:t113`:
  `sha256:8a53b74ccbdb33db8b4ea587d77e2b42d9def8103f619920d89ad66beedd5636`.
- Worker `centralmobilidade-worker:t113`:
  `sha256:3a370caa275a2ba4a85dfa80d1fa452052dd398029b71da6eedfd168bc0006d6`.

Preflight final `cm-t102-e455a7c50a8f`, de 15:21:36 a 15:23:19 UTC−03:00;
evidência privada em `artifacts/validation/T102/e455a7c50a8f42ce806a9f5e8cb6aac5/`.
Engine 29.8.2 Linux x86_64, Compose 5.6.0, SDK 10.0.401; API 2 CPUs/4 GiB,
worker 6 CPUs/12 GiB, stores/coleta fora desse orçamento. Não é teste de capacidade.
TRX/logs/builds e hashes de source em `artifacts/validation/T113/`. Um preflight
anterior `007cb7d8a0f94c8284f5c88a336a5e48` também ficou pronto, antes do ajuste de cleanup.

Nenhum corpus da T102 ou Grafana foi executado aqui. Esse gate foi corrigido para
RabbitMQ, mas não concede aprovação de release nem certifica Pub/Sub, durabilidade
de host físico, retenção ou capacidade. Não foram alterados deployment/volumes de usuário.
