# Cutover RabbitMQ → Pub/Sub — T088

Este runbook cobre preparação, dual-publish proposto, drain, verificação, rollback
e a alternativa com manutenção. Atende à troca de adaptador/configuração sem
alterar o contrato externo (FR-030), preservando a confirmação antes de `202`.
Não executa uma migração nem provisiona recursos Google.

## Capacidade atual e escolha do caminho

| Caminho | Situação e condição de uso |
| --- | --- |
| Manutenção com produtor pausado e RabbitMQ drenado | Procedimento compatível com a seleção atual, sujeito aos gates abaixo e a uma janela aceita pelo responsável da instalação |
| Dual-publish com continuidade de intake | Desenho operacional condicionado a implementação adicional, testes de falhas e prova gerenciada; **bloqueado na versão atual** |
| Bridge de backlog entre provedores | Não existe ferramenta implementada/validada neste repositório; não usar ACK + cópia manual como substituto |

[`QueueProviderRegistration`](../../src/CentralMobilidade.Telemetry.Infrastructure/DependencyInjection/README.md)
aceita apenas `RabbitMq` ou `PubSub`, registra um único port e congela as opções
na inicialização. A troca exige reiniciar/recriar os hosts afetados. Não há
hot reload, `Queue:Provider=Dual`, fallback, outbox ou coordenador de duas
confirmações. Registrar o port duas vezes é rejeitado. Não declare zero downtime
com duas APIs balanceadas ou com dois POSTs do mesmo gzip: cada POST gera outro
`batchId`, e isso não é uma segunda confirmação do mesmo lote.

O procedimento seguro disponível hoje é a seção de manutenção. Se o responsável
não aceitar a janela necessária e o desenho de dual-publish ainda não estiver
implementado/aprovado, **não iniciar o cutover**. A T088 documenta esses gates;
não implementa os componentes ausentes nem aprova uma operação real.

## 1. Preparação comum e gates de go/no-go

Registre em controle privado: responsável, ambiente/contexto, release, imagens,
projeto GCP, região/endpoint, recursos de origem/destino, rota de intake, janela,
limites de latência/erro/backlog, prazo de observação e tempo reservado para
rollback. Defina os critérios numéricos a partir da carga validada; não derive
capacidade de um smoke test. Reúna [backup restaurável coordenado](backup-restore.md)
e configurações/chaves recuperáveis antes da operação. Rollback de fila não deve
restaurar PostgreSQL/Influx para um snapshot anterior às novas escritas.

O mesmo PostgreSQL/guards, Influx `telemetry`, quarentena, autenticação, chaves,
schema e versão de aplicação permanecem em uso. Não aproveite o cutover para
migrar object store, schema, retenção ou infraestrutura de identidade. Trocar de
fila não elimina a limitação física do host dos stores. Veja
[hardening e secrets](../../deploy/hardening.md).

### Destino gerenciado

Provisionar antecipadamente, em procedimento de infraestrutura revisado, dois
topics e duas subscriptions pull: principal e inspeção dead. Os nomes são completos
e pertencem ao projeto configurado. Cada subscription deve apontar ao topic certo,
estar attached e ter retenção de mensagens de pelo menos sete dias; trate também
expiração por inatividade, filtros que possam excluir mensagens, quotas, tamanho
de mensagens, região, conectividade e custos. Não reutilize uma subscription com
consumidores desconhecidos. A criação deve preceder qualquer cópia/publicação;
um topic sem destinatário apropriado não é uma cópia verificável do backlog.

Use `UseEmulator=false`, endpoint Google TLS/443 e ADC da identidade real do
workload. Credencial do operador `gcloud` e ADC dos hosts não são a mesma prova.
A composição faz GetTopic/GetSubscription e verifica binding/retention; não cria
recursos, concede IAM ou prova permissão efetiva de publicação/ACK. O worker faz
pull, renova lease e ACK; retry/dead-letter também publicam. Por isso o papel do
worker precisa das permissões de publicação pertinentes, além de consumo,
settlement e leitura de metadados. Revise os privilégios mínimos por identidade
e recurso, sem conceder administração ampla como atalho.

O dead topic do adaptador é explícito: ele confirma a publicação de uma cópia e
só então ACKa a original. Não equivale a uma política gerenciada de dead-letter.
Uma política gerenciada adicional pode mudar o fluxo e exige prova separada;
não assuma que `MaxAttempts` equivale ao contador nativo do serviço.

Exemplo **sem secrets**, para compor a configuração privada de API e worker:

```json
{
  "Queue": {
    "Provider": "PubSub",
    "StartupTimeout": "00:00:10",
    "PubSub": {
      "ProjectId": "cutover-project",
      "UseEmulator": false,
      "Endpoint": "pubsub.googleapis.com:443",
      "Topic": "projects/cutover-project/topics/telemetry-batches",
      "Subscription": "projects/cutover-project/subscriptions/telemetry-worker",
      "DeadLetterTopic": "projects/cutover-project/topics/telemetry-dead",
      "DeadLetterSubscription": "projects/cutover-project/subscriptions/telemetry-dead-inspection",
      "ConcurrentDeliveries": 4,
      "PublishTimeout": "00:00:05",
      "DrainTimeout": "00:00:30"
    }
  }
}
```

Substitua o projeto/recursos pelo inventário aprovado. Não basta pôr essas chaves
no `.env` da infraestrutura: forneça configuração privada aos dois hosts, com
`__` para variáveis de ambiente ou override/arquivo de runtime revisado. O worker
atual aceita `.env.worker`; a API também precisa receber sua configuração por
canal protegido. Nenhum token/chave ADC entra nesse JSON ou nos argumentos.
`Worker:Concurrency` deve ser no máximo `ConcurrentDeliveries`; o prazo do host
e `stop_grace_period` precisam comportar drain + publish + cleanup. Consulte
[limites e configuração](../../src/CentralMobilidade.Telemetry.Infrastructure/Queue/PubSub/README.md).

Se usar CLI para inspecionar recursos, escolha explicitamente projeto e destino
privado. Estes comandos são somente leitura e **não** funcionam como comandos de
provisionamento do emulador. Não imprima as respostas em logs compartilhados.

```powershell
$ErrorActionPreference = 'Stop'
$GcpProject = 'cutover-project' # substituir pelo projeto revisado
$Evidence = (Resolve-Path data/cutover/conjunto-privado).Path # diretório com ACL restrita
foreach ($topic in @('telemetry-batches','telemetry-dead')) {
    $result = & gcloud pubsub topics describe $topic --project=$GcpProject --format=json
    if ($LASTEXITCODE -ne 0) { throw 'Inspeção de topic falhou.' }
    [IO.File]::WriteAllText((Join-Path $Evidence ($topic + '-topic.json')), ($result -join "`n"))
}
foreach ($subscription in @('telemetry-worker','telemetry-dead-inspection')) {
    $result = & gcloud pubsub subscriptions describe $subscription --project=$GcpProject --format=json
    if ($LASTEXITCODE -ne 0) { throw 'Inspeção de subscription falhou.' }
    [IO.File]::WriteAllText((Join-Path $Evidence ($subscription + '-subscription.json')), ($result -join "`n"))
}
```

Complete a inspeção com canário autorizado no **serviço gerenciado**, usando as
identidades/configuração efetivas: publicação confirmada, consumo, escrita, status
durável, ACK, redelivery e consulta autenticada. Use dados sintéticos identificados
e escopo autorizado; não publique lotes falsos como health check. Teste também
recusa IAM, indisponibilidade, timeout ambíguo, quotas/backpressure, lease e
shutdown. Se a API continuar sem `IDurablePublicationProbe`, `/health/ready` fica
fail-closed: não remova o gate nem substitua prova de publicação por ping/metadata.
Resolva a evidência/hook de publicação antes de usar readiness no roteamento.

## 2. Invariantes de qualquer transferência

Um lote conserva o envelope v1: `schemaVersion`, `batchId`, `traceId`, `producerId`,
`receivedAt`, content type/encoding, `compressedBytes`, `payloadSha256` e a presença
original de `contractVersion`. Preserve os bytes gzip **idênticos**, sem recompressão
ou JSON/Base64 no corpo nativo. O tamanho máximo é 8 MiB comprimidos; cópia precisa
ser limitada e recuperável. O ID nativo Pub/Sub não substitui `batchId`.
Veja o [contrato](../../specs/001-telemetry-ingestion/contracts/queue-envelope.schema.json).

Mantenha o instante de recebimento, idade, provenance e prazos; uma cópia nova não
renova os sete dias. Hoje o adaptador Pub/Sub rejeita mensagens a partir de sete
dias desde `receivedAt`, mesmo que a retenção gerenciada seja maior. Inventarie
idade antes de planejar transferência. Preserve tentativas/códigos reconhecidos
sem converter headers AMQP privados (`x-death`, delivery tags) em envelope de
negócio; essa tradução exigiria bridge implementada/testada. Unknown attributes
podem ser descartados nos retries; não use atributo improvisado como ledger.

Publique/registre confirmação no destino **antes** de remover a cópia de origem.
Timeout/cancelamento ou falha de ACK pode produzir duplicatas; não prova ausência
da mensagem. Após confirmação ambígua, conserve o registro e a origem e reconcilie.
Não use um pull destrutivo, purge, seek para o futuro ou ACK em massa para “drenar”.
Todas as tentativas mantêm os IDs e o conteúdo originais, não repetem POST HTTP.
Guards, leases e resultados de linha são preservados; não desative idempotência.

## 3. Dual-publish / interrupção mínima — desenho bloqueado até implementação

O desenho abaixo permite planejar continuidade do intake, mas não é executável
com uma chave de configuração da versão atual. Exige estes componentes e provas:

| Gate adicional | Evidência necessária antes de habilitar |
| --- | --- |
| Coordenador de duas publicações | Um `batchId` e corpo original, confirmação individual RabbitMQ e Pub/Sub; `202` somente após ambas as confirmações e estado de aceitação durável |
| Registro recuperável de intenções/recibos | Pendência por provedor e payload recuperável protegido, com limites/retenção revisados; queda entre cada etapa não esquece cópia parcial |
| Repetição segura e reconciliação | Mesma identidade em timeout/crash; nenhum sucesso inferido de ausência de erro, gravação de intenção ou confirmação só de um provedor |
| Handoff de consumidores | Teste de reentrega entre provedores com o mesmo batch/guards/counters, incluindo partial write e lease/fence; nenhuma suposição de exatamente uma entrega |
| Recursos/credenciais/observação | Prova gerenciada, capacidade e privacidade; contagem privada por coorte/ID e métricas agregadas sem payload/PII |

1. Prepare o destino e mantenha RabbitMQ como autoridade de processamento. Mantenha
   a subscription Pub/Sub vinculada e retendo as cópias, sem consumidor que as ACKe
   como se fossem apenas “shadow”. Faça canários isolados antes da coorte real.
2. Ative o coordenador já testado numa fronteira registrada. Lotes novos da coorte
   vão aos dois provedores com um estado de aceitação único. Com confirmação parcial,
   não devolva `202`; preserve a intenção e trate o resultado como ambíguo. O contrato
   segue externo e síncrono: intenção/outbox sozinha não substitui confirmação de fila.
3. Drene os lotes anteriores à fronteira no RabbitMQ e reconcilie cada pendência
   parcial. O ledger privado deve distinguir anterior à coorte, confirmado nos dois,
   pendente e ambíguo. Backlog antigo sem cópia só pode ser drenado ou migrado por
   bridge aprovada, nunca presumido presente no Pub/Sub.
4. Com todos os gates satisfeitos, faça handoff controlado: pare a recepção do worker
   RabbitMQ, aguarde drain/liberação das entregas, depois habilite o worker Pub/Sub.
   O intake dual pode continuar, mas a pausa de processamento e o catch-up precisam
   caber no orçamento de queryability medido. Duas autoridades simultâneas exigem
   outra prova de concorrência; não são pressuposto deste desenho.
5. No Pub/Sub, confronte coortes, outcomes e pontos. Cópias já processadas precisam
   ser tratadas de forma idempotente pelo fluxo de produção, sem alterar resultados.
   Injetar duplicates em dois provedores ainda exige teste específico: contratos
   individuais não certificam esse handoff. Só conclua a mudança para Pub/Sub-only
   depois do prazo de observação e da reconciliação dos recibos parciais.

Continuar intake sem interrupção não significa processamento sem latência extra.
Não prometa zero downtime nem o SLO antes de validar a implementação e a carga.
Até lá, use a janela de manutenção abaixo ou adie a migração.

## 4. Manutenção aceita — procedimento suportado pela seleção atual

### Fechar intake e estabelecer a fronteira

1. Obtenha aceite da janela, comportamento de manutenção e retenção/retry duráveis
   do produtor. Defina como observar o último request em curso e o último `202`.
   O produtor precisa guardar trabalho ainda não confirmado; o spool temporário da
   API não é backlog durável de manutenção. Não devolva `202` de uma página/proxy.
2. Pause o produtor e bloqueie novos requests no ingresso. Aguarde os requests
   antigos concluírem ou registre cada resultado ambíguo antes de parar as APIs
   RabbitMQ. Freeze de ingress não encerra requests em curso sozinho.
3. Registre a fronteira e inventário de batches/recibos de forma privada. Revise
   `Publishing`/`PublishFailed` e respostas perdidas: falha HTTP não prova que o
   RabbitMQ não recebeu a mensagem. Não force `Accepted` por SQL nem crie novo
   batch artificial para esconder a ambiguidade. Sem reconciliação segura: no-go.

### Drenar a origem com seu worker

Mantenha o worker RabbitMQ, PostgreSQL/Influx/quarentena originais e a topologia em
uso enquanto o produtor está bloqueado. Ele processa e ACKa pelo fluxo normal.
Observe principal, retry, unacked, DLQ, mensagens em prefetch/lease e produtores
externos. Nomes padrão, quando não sobrescritos: `telemetry.batches.queue`,
`telemetry.batches.retry.queue`, `telemetry.batches.dead.queue`, vhost `telemetry`.

Exemplo de inspeção da origem (ajuste os arquivos realmente utilizados):

```powershell
$Project = 'centralmobilidade' # confirmar o contexto/projeto antes
$EnvFile = (Resolve-Path .env).Path
$Evidence = (Resolve-Path data/cutover/conjunto-privado).Path
$inventory = & docker compose --project-name $Project --env-file $EnvFile `
    -f deploy/compose.yaml exec -T rabbitmq rabbitmqctl -q list_queues -p telemetry `
    name messages_ready messages_unacknowledged consumers
if ($LASTEXITCODE -ne 0) { throw 'Inspeção RabbitMQ falhou.' }
[IO.File]::WriteAllText((Join-Path $Evidence 'rabbitmq-queues.txt'), ($inventory -join "`n"))
```

Declare a origem drenada somente com principal/retry ready=0, unacked=0,
ausência de publicações em curso e todos os batches confirmados até a fronteira
com outcome durável reconciliado. Observe novamente depois da maior janela de
retry/lease/recuperação configurada, investigando qualquer novo tráfego; uma foto
vazia ou métrica atrasada não basta. Nenhum timeout fixo universal prova drain.
Estado terminal `Failed`/`Expired` ou DLQ não significa sucesso de ingestão: cada
caso precisa de disposição explícita, aprovada e verificável.

Se houver backlog que não drena, erro de escrita, ambiguidade, idade perto da
expiração ou DLQ sem destino aprovado, interrompa. Volte ao serviço RabbitMQ ou
renegocie uma manutenção maior; a versão atual não transfere esse backlog.
Não expurgue retry/DLQ nem altere TTL para produzir zero. Se uma custódia de DLQ
na origem for aprovada, conserve acesso, prazo e responsabilidade e registre que
a desativação do broker permanece pendente.

### Parar, trocar e verificar

1. Pare graciosamente o worker RabbitMQ após o drain. Verifique liberações e ausência
   de consumers antes de iniciar o substituto. Preserve arquivos/volumes/topologia
   RabbitMQ, segredos e configuração de retorno; não execute `down --volumes`.
2. Aplique a configuração Pub/Sub revisada ao worker e à API; os dois precisam
   reiniciar/recriar, pois as opções são snapshots. Mantenha APIs antigas fora da
   rota para evitar publicação acidental na origem. Não há fallback para RabbitMQ.
3. Inicie primeiro o worker Pub/Sub e confira binding, consumer heartbeat,
   `/health/worker`, `/health/dependencies` e `/health/ready`. Depois inicie a API
   fora do tráfego normal e confira sua readiness de publicação real. Não relaxe
   TLS, IAM, probe ou validações para obter health verde.
4. Enquanto o produtor normal está pausado, use um canário autorizado: gzip v1,
   `202` confirmado pelo Pub/Sub, `batchId`/status estável, contadores de linhas,
   estado terminal esperado, ponto(s) autenticados com identidade/conteúdo/timestamp
   originais e ACK sem perda. Verifique também retry, reentrega e DLQ protegida
   com casos controlados; nenhum canário em dados reais sem escopo autorizado.
5. Reabra gradualmente o produtor após go explícito. Registre a primeira aceitação
   Pub/Sub-only e monitore ambos os lados pelo período aprovado. Nova mensagem na
   origem após a fronteira exige localizar API/publicador antigo; não a ignore.

O tempo total de manutenção inclui drain, troca, validação e reserva para rollback.
Se esse tempo exceder a janela ou o risco de retenção, aborte antes de reabrir.

## 5. Evidência de verificação e condições de abortar

| Sinal | Evidência exigida |
| --- | --- |
| Aceitação | Recibos/batches por coorte, último `202` da origem, primeiro do destino, intenções ambíguas reconciliadas; ID nativo não é a identidade de negócio |
| Transporte | Bytes/hash e campos de envelope preservados, destino correto e confirmações; nunca inferir sucesso de publish pelo health GetTopic |
| Processamento | Status e resultados por linha, accepted/rejected/replayed explicados, leases/fences preservados e ausência de conflito inesperado |
| Dados | Comparação de identidades/valores/timestamps e contagens com outcomes; equalizar números globais não prova conteúdo |
| Drain | Principal/retry/unacked, DLQ e pendências de aplicação; no destino, backlog/idade/ACK/lease observados em conjunto |
| Operação | Erros, quotas, CPU/memória, write/query latency e canário end-to-end dentro dos limites previamente aprovados |

Os [painéis e alertas locais](../../deploy/grafana/provisioning/alerting/README.md)
usam principalmente sinais RabbitMQ/aplicação/host. Idade de entregas observadas
não é idade da mensagem mais antiga parada, nem métrica de backlog gerenciado.
Prepare monitoramento do Pub/Sub antes de depender dele; NoData não é zero
saudável. Contadores podem resetar e não substituem reconciliação por identidade.

Abortar por divergência de hash/ID, publicação parcial sem resolução, falha de
ACK/lease recorrente, ponto perdido/conflitante, backlog/idade crescentes, readiness
sem evidência, expiração iminente ou limite operacional excedido. Registre motivos
fixos e evidência privada; não coloque payload, coordenadas, placa, CPF, credencial,
nomes de empresa/veículo ou conteúdo dead em labels/logs comuns.

## 6. Rollback por fase

| Momento | Ação segura |
| --- | --- |
| Antes de qualquer aceitação Pub/Sub | Mantenha ingresso fechado; pare hosts novos, restaure a configuração RabbitMQ, confirme worker e API da origem, teste canário e reabra após verificação |
| Durante dual-publish futuro, com cópia Rabbit confirmada para todos | Freeze e reconcilie o ledger, pare consumidores do destino, retome uma única autoridade RabbitMQ após drain/liberação de leases; conserve o Pub/Sub para diagnóstico |
| Depois de aceitar lotes Pub/Sub-only | Trocar `Queue:Provider` de volta não move as mensagens. Bloqueie intake e drene Pub/Sub pelo worker atual antes da volta, ou use uma bridge reversa previamente implementada/testada |
| Destino indisponível com lotes exclusivos ainda pendentes | Preserve subscription/topics e configuração; mantenha a manutenção e recupere o destino. Não prometa rollback imediato/sem perda sem cópia durável acessível desses lotes |

Nunca execute os dois consumidores como solução improvisada de rollback. Preserve
os stores atuais com as novas escritas; restaurar um backup antigo pode desfazer
guards e permitir conflito. Não delete subscriptions, detach, faça purge/seek ou
regenere IDs para voltar. Replay por snapshot/timestamp Pub/Sub depende de retenção
previamente configurada, pode reentregar e não recria mensagens já descartadas;
não é um “undo” de aplicação nem substituto de cópia RabbitMQ. Qualquer uso exige
procedimento separado, revisão do estado de ACK e prova de idempotência.

Depois de aceitar escritas na nova fase, rollback é outro corte com reconciliação.
Mantenha recursos de ambos os provedores pelo prazo aprovado, observando suas
expirações. Retire o RabbitMQ somente depois de todas as coortes/pendências/DLQ,
reserva de rollback, evidência e custódia aprovadas; não há remoção automática nesta
tarefa. A retenção do adaptador não é estendida pela duração da migração.

## 7. Validação reproduzível e limites

```powershell
dotnet test tests/CentralMobilidade.Telemetry.UnitTests --filter FullyQualifiedName~QueueProviderRegistrationTests
dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests --filter 'FullyQualifiedName~QueueProviderHostTests|FullyQualifiedName~PubSubEmulatorTests|FullyQualifiedName~RabbitMqReusableQueueContractTests'
```

Esses testes verificam seleção/configuração isolada, snapshots de opções, contratos
compartilhados nos dois provedores, preservação de bytes/metadados, confirmações,
settlement/reentrega e hosts Pub/Sub com PostgreSQL/Influx reais. Os cenários de
ciclo de vida incluem drain, cancelamento e perda/recriação de subscription.
São pré-condições de compatibilidade; **não executam uma migração**, coordenador
dual, bridge, rollback Pub/Sub-only ou canário no serviço Google gerenciado.

O emulador tem estado de sessão e não prova IAM, retenção de sete dias, SLA,
durabilidade física, capacidade ou operação gerenciada. Nenhum projeto Google,
credencial real, produtor ou deployment existente é alterado por esses testes.
Guarde a evidência da operação real separadamente, com responsáveis e resultados
de cada gate; sem ela não declare cutover concluído ou zero/minimal downtime.

Fontes e contratos: [seleção/health](../../src/CentralMobilidade.Telemetry.Infrastructure/DependencyInjection/README.md),
[Pub/Sub adapter](../../src/CentralMobilidade.Telemetry.Infrastructure/Queue/PubSub/README.md),
[contratos reutilizáveis](../../tests/CentralMobilidade.Telemetry.ContractTests/Queue/README.md),
[propriedades de subscription](https://docs.cloud.google.com/pubsub/docs/subscription-properties),
[replay e seek](https://docs.cloud.google.com/pubsub/docs/replay-overview),
[limites do emulador](https://docs.cloud.google.com/pubsub/docs/emulator).
