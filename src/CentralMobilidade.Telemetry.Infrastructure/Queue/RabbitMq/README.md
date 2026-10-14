# Topologia e recuperação RabbitMQ — T030

`RabbitMqTopology` captura e valida a configuração de nomes e limites; `EnsureAsync`
declara idempotentemente três exchanges direct duráveis e três filas classic duráveis,
não exclusivas e sem auto-delete:

- A fila principal recebe RoutingKey do exchange principal e aponta ao exchange de
  dead-letter. TTL padrão: sete dias.
- A fila de retry recebe o mesmo RoutingKey de seu exchange e, após RetryDelay
  (padrão: um segundo), retorna ao exchange/routing key principal.
- A fila de dead-letter recebe o RoutingKey original e tem o mesmo TTL da fila principal.

Nomes padrão usam `telemetry.batches.*`. As chaves de nomes sob `Queue:RabbitMq` são
Exchange, Queue, RoutingKey, RetryExchange, RetryQueue, DeadLetterExchange e DeadLetterQueue.
RetryDelay deve ser pelo menos um milissegundo e menor que `Telemetry:QueueRetention`,
que deve ser pelo menos sete dias. Nomes de recursos são distintos, limitados a 255 bytes
UTF-8 e não podem ser vazios, conter controles ou começar com `amq.`.
TTL é aplicado por fila; não representa uma garantia de idade total após ciclos de retry.

`RabbitMqConnectionManager` valida Host, Port, Username e Password antes de conectar;
VirtualHost padrão é `/`. Ele mantém uma conexão com AutomaticRecoveryEnabled e
TopologyRecoveryEnabled, intervalo de recuperação de um segundo e timeout de conexão
de dez segundos. Cada CreatePublishChannelAsync devolve um canal próprio, com publisher
confirms e rastreamento habilitados, depois de declarar a topologia. Chamadas simultâneas
de criação são serializadas; canais devolvidos são propriedade do chamador.

Conexões que estão em recuperação são aguardadas com cancelamento e deadline, preservando
os canais existentes. Um canal fechado pela aplicação é substituído por nova chamada à
fábrica. DisposeAsync encerra a conexão e impede novas criações. Falhas do provedor
retornam DURABILITY_UNCONFIRMED sem detalhes do SDK/credenciais; erros AMQP 403/406 são
permanentes e outras falhas de conexão são transitórias. Cancelamento do chamador é preservado.

Recuperar transporte/canais não confirma nem republica automaticamente um lote em voo:
confirmação correlacionada e falha ambígua de publicação pertencem à T031. Esta tarefa
não registra o publicador nem declara readiness da API.

Sete testes com RabbitMQ real passaram: declaração repetida, argumentos independentes
de durabilidade/TTL, bindings retry/dead-letter, recuperação após interrupção TCP,
substituição de canal fechado, descarte e configuração inválida. O teste de transporte
usa um proxy exclusivo e verifica uma nova conexão, a mensagem preservada e nova
publicação no canal recuperado, sem reiniciar o broker compartilhado. A expectativa
de dead-letter da T026 foi completada com TTL, evitando retenção ilimitada nessa fila.

A suíte completa teve 278 aprovações e as mesmas 46 falhas TDD das T025–T027. Isso não
comprova reinício/perda do host nem redundância de uma fila classic em broker único.

## Publicação confirmada — T031

`RabbitMqBatchPublisher` implementa a publicação de IDurableBatchQueue. Recebe um canal
com confirms habilitados e IConfiguration; o canal injetado permanece propriedade do
chamador. `CreateAsync` usa RabbitMqConnectionManager e cria um publicador que possui
e descarta seu próprio canal. As publicações são serializadas por instância.

O envelope é validado antes de publicar. O corpo é lido em um buffer alugado limitado
ao tamanho declarado (máximo v1: 8 MiB), sem JSON, Base64, conversão em string ou nova
compressão. O adaptador verifica comprimento exato, cabeçalho gzip básico e SHA-256.
RabbitMQ.Client exige o corpo nativo como ReadOnlyMemory<byte>; esse buffer limitado
é adicional ao spool em disco da T029 e é zerado no retorno ao pool. A stream de entrada
nunca é descartada pelo adaptador; o chamador deve reposicioná-la para uma nova tentativa.

BasicProperties usa Persistent, MessageId=BatchId, CorrelationId=TraceId e os atributos
do envelope. BasicPublishAsync usa mandatory=true. O recibo só é construído depois do
ACK da sequência dessa publicação (ou ACK múltiplo que a inclua), além da conclusão do
await do SDK. Envio concluído e ACK de outra sequência não estabelecem aceitação.
Retorno mandatory associado ao MessageId e nack nunca retornam recibo.

`Queue:RabbitMq:PublishTimeout` tem padrão de cinco segundos e cobre preparação/leitura,
declaração inicial e espera da confirmação. Timeout e perda de conexão são conservadoramente
ambíguos: não se infere ausência de mensagem nem se republica automaticamente. Nack é
transitório; retorno sem rota e erros AMQP 403/406 são permanentes. Falhas usam
DURABILITY_UNCONFIRMED com mensagem fixa e sem exceção interna. Envelope/corpo inválido
usa REQUEST_INVALID. Cancelamento explícito do chamador permanece OperationCanceledException.

Consumir entregas ainda pertence às tarefas do worker; ConsumeAsync não está implementado.
T031 não conecta o endpoint, não fornece readiness da API e não afirma aceitação HTTP.
Validação atual: 12 testes de publicação passaram, incluindo confirmação correlacionada,
mandatory sem rota, nack/timeout, integridade e preservação dos bytes. A suíte completa
teve 289 aprovações e 36 falhas pendentes das T025/T027; as dez falhas da T026 foram resolvidas.
