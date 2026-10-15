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

Na T031, o publicador fornece apenas publicação; seu ConsumeAsync não é o ponto de entrada do worker.
T031 não conecta o endpoint, não fornece readiness da API e não afirma aceitação HTTP.
Validação atual: 12 testes de publicação passaram, incluindo confirmação correlacionada,
mandatory sem rota, nack/timeout, integridade e preservação dos bytes. A suíte completa
teve 289 aprovações e 36 falhas pendentes das T025/T027; as dez falhas da T026 foram resolvidas.

## Consumidor T052

`RabbitMqBatchConsumer(connections, configuration).ConsumeAsync(token)` fornece entregas
`IQueueDelivery`. Cada enumeração possui canal exclusivo, prefetch limitado e ACK manual.
O chamador deve descartar cada entrega e chamar CompleteAsync somente após os efeitos duráveis
da T051. DisposeAsync nunca confirma implicitamente. O host T053 usa essa enumeração com
monitoramento da assinatura; veja [configuração e shutdown do worker](../../../CentralMobilidade.Telemetry.Worker/README.md).

O callback copia os bytes antes de retornar, conforme a [API .NET v7 do RabbitMQ](https://www.rabbitmq.com/client-libraries/dotnet-api-guide).
O limite v1 é 8 MiB por corpo; Prefetch aceita 1–64 (padrão 8), limitando os snapshots de
corpos não confirmados a aproximadamente Prefetch × 8 MiB por enumeração, além dos buffers
do SDK. Configure também o limite de mensagem do broker. Corpo acima do contrato encerra
a sessão sem ACK; requer correção operacional, pois não é copiado para a memória da aplicação.

RetryAsync mantém o original não confirmado durante backoff exponencial com jitter de ±20%,
limitado por MaxRetryDelay; depois republica os mesmos bytes com mandatory e publisher confirms
rastreados pelo SDK, e somente então confirma a entrega original. Não utiliza o salto DLX
da fila classic de retry para estabelecer durabilidade. Uma falha entre confirmação da cópia
e ACK pode duplicar a entrega; a identidade original e os resultados duráveis da T051 protegem
o reprocessamento. O contador deliveryAttempt acompanha as republicações; reentregas por queda
de conexão conservam esse contador e não provam uma nova tentativa concluída.

Envelope inválido, excesso de tentativas e expiração do prazo original seguem para a DLQ
por publicação persistente confirmada antes do ACK. DeadLetterAsync faz o mesmo para poison.
A DLQ contém o gzip original e exige o mesmo controle de acesso/retenção da fila principal;
não é uma saída de diagnóstico pública. Headers arbitrários não são copiados, códigos de erro
são limitados e sanitizados, e o consumidor não registra o corpo nem exceções do provider.
Retorno sem rota, nack, timeout ou perda de conexão deixam o original sem ACK e invalidam
a sessão. Uma nova enumeração cria outro canal; entregas antigas não podem confirmar após recuperação.

| Queue:RabbitMq | Padrão | Limite |
|---|---|---|
| Prefetch | 8 | 1–64 |
| MaxAttempts | 10 | 1–1000 |
| RetryDelay | 00:00:01 | ≥ 1 ms, até MaxRetryDelay |
| MaxRetryDelay | 00:01:00 | até 10 minutos |
| DrainTimeout | 00:00:30 | 0–5 minutos |
| PublishTimeout | 00:00:05 | positivo, até 1 minuto no consumidor |

O cancelamento encerra a leitura e cancela a assinatura, permitindo às entregas já cedidas
concluir dentro de DrainTimeout. O processamento em andamento deve usar token próprio para
poder terminar durante esse prazo. Ao expirar, o canal fecha, operações pendentes são canceladas
e mensagens não confirmadas voltam ao broker. Entregas ainda no buffer não são processadas
durante o drain. CompleteAsync é idempotente na mesma entrega; ações terminais conflitantes falham.
