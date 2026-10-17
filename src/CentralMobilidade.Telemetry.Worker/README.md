# Worker T053

Program.ConfigureServices compõe o consumidor selecionado, PostgreSQL, guard de identidade,
writer do InfluxDB único, parser e quarentena. O handler recebe um scope por entrega,
seguindo o [ciclo de vida dos hosted services](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0).
Não há consulta cadastral para decidir a empresa nem migração automática do banco.

Antes de iniciar, aplique as migrações com a identidade apropriada e forneça:

- `ConnectionStrings:TelemetryControl`: conexão de runtime com as permissões documentadas.
- `Queue:Provider=RabbitMq` e endpoint, virtual host, usuário e senha do RabbitMQ,
  ou `PubSub` com os recursos e o transporte descritos na [composição T081](../CentralMobilidade.Telemetry.Infrastructure/DependencyInjection/README.md).
- `InfluxDb:Queryable:Url`, `Database` e token com permissão de escrita no banco único.
- `Quarantine:Path`: diretório privado gravável, em volume persistente.
- `Processing:DiagnosticKey`: chave Base64 persistente com pelo menos 32 bytes aleatórios.
  Preserve a mesma chave entre reinícios e tentativas; não use uma chave dos testes.

Configuração obrigatória inválida falha antes de assinar a fila. Indisponibilidade transitória
do broker mantém o host vivo, com consumidor indisponível, e inicia reconexão com backoff/jitter
limitado a 30 segundos. A T081 também permite PubSub; seus recursos devem existir e
passar pela validação de inicialização antes do início do consumo.

| Configuração | Padrão | Regra |
|---|---|---|
| Worker:Concurrency | 4 | 1–64, até Prefetch do RabbitMQ ou ConcurrentDeliveries do Pub/Sub |
| Queue:RabbitMq:Prefetch | 8 | 1–64 por assinatura |
| Worker:HeartbeatInterval | 00:00:05 | 100 ms–10 s, menor que Health:WorkerMaxAge |
| Worker:RecoveryDelay | 00:00:01 | 100 ms–30 s, base do backoff de reconexão |
| Queue:RabbitMq:DrainTimeout | 00:00:30 | 0–5 minutos |
| Queue:RabbitMq:PublishTimeout | 00:00:05 | positivo, até 1 minuto |

DrainTimeout e PublishTimeout usam o bloco do provedor selecionado; os valores da
tabela continuam sendo os padrões. Pub/Sub usa até ConcurrentDeliveries canais de
pull limitados, com monitoramento RPC da assinatura e ACK/retry da T080. Não há
seleção de provedor no handler de domínio nem fallback entre filas.

O número de tarefas em processamento nunca excede Concurrency. As entregas restantes ficam
no buffer limitado pelo prefetch do consumidor. Cada entrega possui corpo e resultado próprios;
somente o handler confirma após a persistência da T051. Falhas não tratadas pelo handler pedem
retry; REQUEST_INVALID permanente vai à DLQ protegida. Falhas permanentes de armazenamento
também seguem a política limitada de retry, sem descartar o lote na primeira falha.

O heartbeat é emitido após RPC bem-sucedido no canal da assinatura ativa. O monitor pertence
à sessão e continua verificando a assinatura durante fila vazia e backpressure. Ele é cancelado
ao encerrar a assinatura; não basta um timer, uma requisição de health ou o processo estar vivo.
Readiness exige também PostgreSQL e conectividade com InfluxDB; não certifica capacidade sustentada
nem permissão de escrita por meio de uma escrita sintética. /health/worker indica apenas frescor
do consumidor. A porta interna de métricas/health continua 9464 e não é publicada pelo Compose.

No desligamento, o worker marca o consumidor indisponível, interrompe a recepção e preserva
o token do processamento até DrainTimeout. Ao expirar, cancela o trabalho e fecha o canal,
permitindo a reentrega das mensagens sem ACK. Um adaptador que não coopere com cancelamento
faz o serviço falhar após o orçamento adicional de limpeza, em vez de iniciar outra sessão
com tarefas antigas. HostOptions.ShutdownTimeout usa DrainTimeout + PublishTimeout + 10 s.
O Compose reserva 60 s por padrão; aumente stop_grace_period ao aumentar esses prazos.

O perfil app carrega opcionalmente o arquivo privado `.env.worker` da raiz. Esse arquivo
é ignorado pelo Git. Use os nomes de ambiente com `__` (por exemplo Processing__DiagnosticKey),
sem imprimir seus valores em comandos ou logs. O arquivo `.env` da infraestrutura continua
separado; ele não provisiona automaticamente as credenciais de runtime. Para execução no host,
exporte a configuração no processo e substitua os endpoints internos por loopback.

Os testes do host usam a mesma composição de Program.ConfigureServices, com broker e
armazenamentos reais no caso de lote misto. Testes controlados verificam concorrência,
heartbeat, retry, poison, recuperação da assinatura e os dois caminhos de shutdown.
Isso não substitui os gates de carga ADR-001, reinício do processo e perda física do host.

## Retenção operacional (T061)

OperationalRetentionWorker é registrado como IHostedService na composição de produção.
Executa uma passagem ao iniciar e depois a cada Retention:Interval (5 minutos por padrão).
RunOnceAsync(CancellationToken) usa o IClock injetado e delega à porta de limpeza;
falhas transitórias não encerram o consumidor e são tentadas novamente no próximo ciclo.
Os logs são fixos, sem conteúdo bruto, caminhos ou diagnósticos privados do provider.

A implementação PostgreSQL adquire pg_try_advisory_xact_lock com uma chave fixa por
banco. Essa posse pertence à transação/conexão e é liberada no commit, rollback ou
queda da conexão; outro worker não espera nem executa uma passagem concorrente.
O prazo de cancelamento da passagem é Retention:PassTimeout (1 minuto). Há também
idle_in_transaction_session_timeout de 120 segundos para uma transação sem progresso.
Não há tabela ou lease de identidade reutilizado para a limpeza.

Cada fase processa no máximo PageSize × MaxPages registros (200 × 20 por padrão),
com cursores por UUID para quarentena/lotes e deletes limitados para evidências/guards.
Limites de configuração: intervalo 1 s–1 h, timeout 1 s–5 min, página 1–1000,
até 100 páginas. Ajuste o orçamento conforme backlog e custo dos cascades no PostgreSQL.
A limpeza usa os prazos persistidos, inclusive no instante exato da expiração.

Primeiro remove bytes expirados da quarentena. Só esquece os metadados após confirmar
remoção ou ausência; falha/no-op mantém o objeto e o lote para recuperação, sem impedir
outros objetos elegíveis. Se os bytes desaparecerem e o commit falhar, a passagem
seguinte confirma a ausência e conclui a exclusão. Depois bloqueia cada lote expirado,
reverifica a ausência de quarentena remanescente e o remove com seus resultados de linha.
Evidências expiram independentemente, em 30 dias. Guards só são removidos após ambos
GuardExpiresAt e TelemetryExpiresAt; um lease de processamento ativo também os preserva.
O relógio do banco limita a exclusão de guards, além do relógio injetado e do trigger
imutável existente. Auditoria e contadores agregados não são apagados.

A rotina não envia comandos de exclusão de telemetria ao InfluxDB. O inicializador
local já cria o único banco telemetry com retention-period 60d. Essa retenção nativa
filtra consultas; não equivale a apagar imediatamente todos os arquivos físicos.
Os novos instrumentos de métricas da T062 e os gates de capacidade permanecem separados.
Não é necessária migração ou alteração de privilégios: o papel de runtime existente
já possui DELETE nas tabelas operacionais. API e worker devem compartilhar o mesmo
armazenamento privado de quarentena e PostgreSQL.
