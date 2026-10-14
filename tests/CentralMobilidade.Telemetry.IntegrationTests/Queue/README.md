# Contrato de publicação RabbitMQ — T026

```powershell
dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests --filter FullyQualifiedName~RabbitMqPublisherContractTests
```

A fixture inicia RabbitMQ `4.3.6-management-alpine` via Testcontainers. Opcionalmente,
`TELEMETRY_TEST_RABBITMQ` pode apontar para um broker exclusivo de validação. O valor é
uma URI AMQP com credenciais de teste; não use um broker de produção. Cada sessão de
publicação usa nomes `t026.<UUID>` e remove somente suas próprias filas e exchanges.
O broker externo e seus volumes não são removidos pela fixture.

Os 12 cenários cobrem:

- Conectividade real: bytes nativos, propriedade persistent e publisher confirms rastreados.
- Publicação pelo adaptador, recibo com o BatchId correto, corpo gzip idêntico e todos
  os atributos do envelope, incluindo tamanho, SHA-256, versão, produtor e correlação.
- Topologia existente e durável, filas classic não exclusivas, retenção de sete dias,
  retry de um segundo, dead-letter e sobrevivência ao fechamento da conexão do publicador.
- Bindings reais de retry/dead-letter preservando o corpo original.
- Retorno mandatory para rota sem binding, que nunca pode produzir um recibo de sucesso.
- Envelope com versão, tamanho ou digest inválidos, rejeitado antes de BasicPublishAsync.
- Nack, ausência de confirmação e recibo bloqueado até a confirmação correlacionada.
- ACK de outra sequência que não pode liberar um recibo de aceitação.

O interceptor encaminha os métodos do IChannel real. Nos casos de falha controlada,
ele entrega o PublishException do SDK para nack, mantém a confirmação pendente até o
token de timeout do adaptador ser cancelado, ou libera explicitamente uma confirmação.
O deadline externo de dois segundos no teste de timeout não é aceito como uma falha
do provedor: o adaptador deve aplicar seu timeout configurado de 500 ms e retornar
ProviderOperationException. Nacks/timeouts controlados não são evidência de uma falha
física do broker. As demais operações usam RabbitMQ real.

## Composição para T030/T031

O teste procura a classe de produção
`CentralMobilidade.Telemetry.Infrastructure.Queue.RabbitMq.RabbitMqBatchPublisher`,
que deve implementar IDurableBatchQueue. A ausência da classe é uma falha explícita;
não há um publicador falso substituindo a implementação esperada.

O bootstrap fornece IConfiguration, IConnection, IChannel com confirms habilitados e
logging ao ActivatorUtilities. A conexão e o canal são dependências substituíveis para
permitir os testes determinísticos de confirmação. Se a implementação usar opções
tipadas ou uma fábrica de canais, adapte esse bootstrap mantendo as mesmas asserções.

As chaves de configuração sob `Queue:RabbitMq` são Exchange, Queue, RoutingKey,
RetryExchange, RetryQueue, DeadLetterExchange, DeadLetterQueue, PublishTimeout e
RetryDelay. `Telemetry:QueueRetention` é sete dias. Os nomes são exclusivos por teste;
retry retorna ao exchange/routing key principal e a fila principal aponta ao exchange
de dead-letter. A topologia deve existir antes das verificações passivas e equivalentes:
essas verificações não podem criar a topologia que deveriam testar.

## Estado após T031

A implementação T031 resolveu os dez cenários que falhavam por ausência do publicador.
Agora os 12 testes passam, incluindo a nova guarda contra ACK de outra sequência.
Nesse caso controlado, o interceptor simula envio concluído sem confirmação e entrega
ACKs explicitamente; ele não publica uma mensagem real. Isso verifica a correlação
independentemente do rastreamento interno do SDK. Os demais testes mantêm o escopo
real/controlado descrito acima. A compilação local e no Docker passou sem avisos.
A suíte completa teve 289 aprovações e somente 36 falhas de intake ainda pendente
(35 T025, uma T027). Nenhum cenário foi ignorado nem configurado para aceitar falhas.

Esta etapa não comprova reinício/perda do host, implementação do consumidor ou durabilidade
de Pub/Sub. Relatórios TRX locais ficam em `artifacts/validation/T026/`, ignorado pelo Git.
