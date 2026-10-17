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

## T076 — contrato reutilizável de provedores

`RabbitMqReusableQueueContractTests` executa os 16 casos herdados da
[suíte compartilhada](../../CentralMobilidade.Telemetry.ContractTests/Queue/README.md)
contra publicador/consumidor de produção e um broker isolado. O arquivo de contrato
é vinculado no projeto de integração, sem cópia de asserções nem referência entre
assemblies de testes. Inspeção nativa verifica os bytes gzip e atributos reais,
além das propriedades AMQP de persistência, conteúdo e correlação.

A confirmação retida/nack/timeout reutiliza o interceptor controlado já existente;
não representa falha física do RabbitMQ. Os demais fluxos usam o broker real.
Recursos têm nomes exclusivos e são removidos após cada caso. Não há fila em memória,
caso ignorado ou implementação antecipada de Pub/Sub. T077/T080 continuam necessários
para comprovar paridade. As notas T026/T031 acima são evidência histórica.

## T077 — emulador Pub/Sub e testes ativos do adaptador

As notas desta seção registram a criação dos testes na T077. A T080 implementou
o adaptador e resolveu suas 22 falhas, conforme o resultado abaixo.

`PubSubEmulatorTests.cs` conecta os mesmos 16 contratos T076 ao adaptador esperado
`CentralMobilidade.Telemetry.Infrastructure.Queue.PubSub.PubSubBatchQueue` e adiciona
seis casos de tradução de erros gRPC. A classe de produção ainda pertence à T080:
os 22 casos ficam ativos, com PendingImplementation=T080, e falham explicitamente
quando ela não existe. Nenhum publicador/fila em memória assume seu lugar. A trait
documenta a dependência; não ignora nem transforma falhas em sucesso.

Quatro controles independentes usam Google.Cloud.PubSub.V1 3.39.0 e o emulador real:
bytes gzip/atributos/ID confirmado, nack/reentrega até ack explícito, NotFound real
para tópico ausente e perda de tópico/assinatura após reinício. Antes do reinício,
o controle publica e recebe uma mensagem sem confirmar; depois exige NotFound,
não aceita Unavailable como prova de perda de estado. Os controles do SDK não são
evidência de conformidade do adaptador inexistente.

A coleção xUnit compartilha um único contêiner
`gcr.io/google.com/cloudsdktool/google-cloud-cli:588.0.0-emulators`, a mesma versão
do perfil Compose, com projeto UUID e porta aleatória. Cada caso que cria recursos
recebe tópicos/assinaturas únicos, removidos ao terminar. O teste de reinício atua
somente nesse contêiner descartável, sem volumes persistentes, nunca no Compose
ou em recursos de uma conta Google. Os testes da coleção são sequenciais entre si.
O canal HTTP/2 é explícito, local e de propriedade da fixture: não usa ADC,
variáveis de emulador globais, pools globais nem fallback para Pub/Sub de produção.
Após reiniciar, a fixture relê a porta mapeada e recria o canal/clientes; ignorar essa
etapa produzia conexão recusada e foi corrigido antes da validação final.

### Composição esperada na T080

O bootstrap usa reflexão apenas para localizar a classe de produção, verifica
IDurableBatchQueue e a instancia com ActivatorUtilities. Oferece IConfiguration,
PublisherServiceApiClient e SubscriberServiceApiClient por injeção. Isso não implementa
o registro de provedor T081. As chaves sob Queue:PubSub são ProjectId, Endpoint,
UseEmulator, Topic, Subscription, DeadLetterTopic, DeadLetterSubscription,
PublishTimeout, PullTimeout, MaxAttempts e DrainTimeout. Recursos são nomes completos
`projects/.../topics/...` e `projects/.../subscriptions/...` já criados pela fixture.
Se a implementação escolher outra composição pública, adapte o bootstrap mantendo
as asserções e os clientes locais; não crie um adaptador de teste substituto.

A inspeção nativa faz Pull/Acknowledge via SDK sobre a assinatura principal ou dead
e compara os bytes/atributos reais. O consumidor dos contratos é sempre o do
adaptador. O cliente injetado ControlledPublisher delega publicações normais ao
emulador e controla somente a fronteira de confirmação/erro: confirmação retida
aguarda uma resposta real antes de devolvê-la; Unavailable e erros de status são
injetados; confirmação ausente aguarda o cancelamento do adaptador. PublishTimeout
é 500 ms nesses casos e cinco segundos no caso de confirmação retida. Esperas
externas e RPCs têm limites. Falha ambígua não permite concluir que nada foi armazenado.

PermissionDenied, InvalidArgument e NotFound do serviço devem produzir falha
Permanent; Unavailable e ResourceExhausted, Transient; DeadlineExceeded, Ambiguous.
O código público é DURABILITY_UNCONFIRMED para esses erros após entrada no SDK;
metadados inválidos antes da publicação continuam REQUEST_INVALID nos contratos
compartilhados. Diagnósticos sintéticos do provedor não podem vazar na exceção.
Essas injeções verificam a tradução pretendida, não IAM real, esgotamento de quota
ou falha física da nuvem. O alvo dead é preparado para o caminho explícito do
adaptador; não comprova políticas gerenciadas de dead-letter do serviço.

### Limites da evidência

O emulador mantém recursos e mensagens somente durante sua sessão. Confirmação de
publicação, ack ou um teste bem-sucedido nessa sessão não estabelece persistência
após reinício, retenção de sete dias, backup, HA ou proteção contra perda do host.
Não acrescente um volume e interprete isso como durabilidade de produção.

A [documentação oficial do emulador](https://cloud.google.com/pubsub/docs/emulator)
informa implementação potencialmente diferente/incompleta, IAM não suportado,
retenção configurável não suportada (mensagens retidas indefinidamente dentro da
sessão) e ausência de expiração de assinaturas. O teste não aprova IAM, TLS,
credenciais de produção, quotas, desempenho, SLA, exactly-once ou garantias de
dead-letter/retry em produção. T080/T081, validação no serviço gerenciado e os
procedimentos de drain/cutover/recuperação continuam necessários. Não desative as
proteções de produção para reproduzir o acesso anônimo local do emulador.

```powershell
dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests --filter FullyQualifiedName~PubSubEmulator
```

Esse comando tem saída diferente de zero enquanto T080 estiver ausente: os quatro
controles do SDK devem passar e as 22 falhas devem apontar exclusivamente para a
ausência da classe esperada. Falha de imagem, conexão, reconexão, limpeza ou SDK é
inesperada e precisa ser corrigida; não é falha TDD aceitável. Relatórios/TRX ficam
em `artifacts/validation/T077/`, ignorados pelo Git. Testes não geram credenciais nem
requisitam projeto Google real.

## T080 — adaptador implementado

O bootstrap agora referencia diretamente o `PubSubBatchQueue` de produção, por
injeção dos mesmos clientes locais. A trait PendingImplementation foi removida;
as asserções dos 16 contratos compartilhados e seis casos gRPC foram preservadas.
Os quatro controles SDK continuam separados. Configuração de seleção do provedor,
criação dos clientes de produção e integração aos hosts continuam na T081.

`PubSubAdapterSafetyTests` acrescenta 14 casos com emulador real: nove envelopes
inválidos/expirados/esgotados, falha controlada na publicação de cópia de retry ou
dead-letter mantendo a original disponível, renovação além do prazo de 10 segundos
com NACK na disposição, limite de retry explícito e substituição de código de erro
desconhecido mesmo quando parece uma placa em maiúsculas. Bytes e correlação são
inspecionados via SDK; atributos desconhecidos não são copiados. Um observador da
resposta de ACK evita depender de espera arbitrária nos cenários automáticos.

São 40 casos de integração Pub/Sub: 16 contratos, seis traduções gRPC, quatro
controles SDK e 14 cenários adicionais. Trinta casos unitários verificam limites,
resposta de confirmação, cancelamento em andamento, falhas de pull/ACK/renovação,
idempotência/conflito e uso após disposição com controles no SDK. Não simulam uma
fila por trás do port de aplicação. O teste de renovação observa ausência de cópia
naquela janela, sem afirmar exclusão de duplicação em produção.

Veja [opções, ACK, retry e limites do adaptador](../../../src/CentralMobilidade.Telemetry.Infrastructure/Queue/PubSub/README.md).
O comando PubSub acima agora deve passar integralmente. Relatórios atuais estão
em `artifacts/validation/T080/`, ignorados. Permanecem os limites de sessão do
emulador, a necessidade de validação gerenciada e os gates de cutover/capacidade.

## T081 — composição dos hosts por configuração

`QueueProviderHostTests` executa nove casos com a composição de produção: API HTTP
com autenticação e confirmação Pub/Sub, worker com parser e PostgreSQL/Influx reais,
lote misto e readiness do worker; três falhas de recursos na inicialização;
concorrência limitada e heartbeat durante backpressure; drain/ACK ou cancelamento/
reentrega; perda e recuperação da assinatura; e gzip acima de 4 MiB no transporte.
Somente os cenários de ciclo de vida usam um processador controlado; o lote misto
usa o handler de produção e confere os resultados persistidos. Nenhuma fila em
memória substitui os adaptadores. Credenciais e recursos são sintéticos e isolados.

`ConfiguredApiFactory` fornece a configuração antecipada necessária ao Program.cs
da API mínima, por ConfigureHostConfiguration; a autenticação sintética é aplicada
na configuração normal de teste. Isso mantém a validação estrita de produção.
O teste de bytes grandes prova o transporte, sem afirmar validade Line Protocol.
Trinta e três testes unitários verificam seleção/isolamento, configurações inválidas,
política TLS/emulador e snapshot. Os hosts RabbitMQ existentes também são reexecutados.

Veja [configuração, permissões, limites e health](../../../src/CentralMobilidade.Telemetry.Infrastructure/DependencyInjection/README.md).
Os relatórios ficam ignorados em `artifacts/validation/T081/`. Validação gerenciada,
retenção física, capacidade e troca operacional de provedor continuam gates separados.
