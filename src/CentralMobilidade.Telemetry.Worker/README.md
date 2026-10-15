# Worker T053

Program.ConfigureServices compõe o consumidor RabbitMQ, PostgreSQL, guard de identidade,
writer do InfluxDB único, parser e quarentena. O handler recebe um scope por entrega,
seguindo o [ciclo de vida dos hosted services](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0).
Não há consulta cadastral para decidir a empresa nem migração automática do banco.

Antes de iniciar, aplique as migrações com a identidade apropriada e forneça:

- `ConnectionStrings:TelemetryControl`: conexão de runtime com as permissões documentadas.
- `Queue:Provider=RabbitMq` e endpoint, virtual host, usuário e senha do RabbitMQ.
- `InfluxDb:Queryable:Url`, `Database` e token com permissão de escrita no banco único.
- `Quarantine:Path`: diretório privado gravável, em volume persistente.
- `Processing:DiagnosticKey`: chave Base64 persistente com pelo menos 32 bytes aleatórios.
  Preserve a mesma chave entre reinícios e tentativas; não use uma chave dos testes.

Configuração obrigatória inválida falha antes de assinar a fila. Indisponibilidade transitória
do broker mantém o host vivo, com consumidor indisponível, e inicia reconexão com backoff/jitter
limitado a 30 segundos. A modalidade PubSub ainda falha explicitamente; sua paridade é uma tarefa futura.

| Configuração | Padrão | Regra |
|---|---|---|
| Worker:Concurrency | 4 | 1–64, até Queue:RabbitMq:Prefetch |
| Queue:RabbitMq:Prefetch | 8 | 1–64 por assinatura |
| Worker:HeartbeatInterval | 00:00:05 | 100 ms–10 s, menor que Health:WorkerMaxAge |
| Worker:RecoveryDelay | 00:00:01 | 100 ms–30 s, base do backoff de reconexão |
| Queue:RabbitMq:DrainTimeout | 00:00:30 | 0–5 minutos |
| Queue:RabbitMq:PublishTimeout | 00:00:05 | positivo, até 1 minuto |

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
