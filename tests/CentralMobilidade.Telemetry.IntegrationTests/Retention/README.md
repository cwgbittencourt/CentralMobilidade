# Retenção operacional (T056)

OperationalRetentionTests acrescenta 16 cenários de retenção com PostgreSQL, filesystem,
InfluxDB 3 Core 3.11.6 e exportador Prometheus reais. Um servidor PostgreSQL é compartilhado
pela classe, com banco isolado por cenário; um único servidor InfluxDB contém somente
o banco telemetry, criado com retention_period=60d. Nenhum volume do projeto é alterado.

Cinco casos exercitam componentes já existentes: remoção do conteúdo bruto um microssegundo
antes, no instante e um microssegundo após o prazo inclusivo de 30 dias; cancelamento sem
remoção; e consultas de telemetria de duas empresas com 29, 59 e 61 dias. As consultas têm
janelas de duas horas para não confundir limite de consulta histórica com retenção.

A [retenção do InfluxDB Core](https://docs.influxdata.com/influxdb3/core/reference/internals/data-retention/)
é aplicada nas consultas: pontos expirados deixam de ser consultáveis, mas podem ainda
existir no armazenamento até a remoção física. O teste confirma escrita e filtragem real,
sem afirmar apagamento físico imediato, execução de 60 dias ou resistência à perda do host.

Os outros 11 cenários exigem a T061 e permanecem ativos, com PendingImplementation=T061:

- Remoção conjunta de lote, resultados de linha, metadados e conteúdo de quarentena no limite de 30 dias.
- Execução repetida e concorrente em dois providers independentes, preservando dados não expirados.
- Expiração da evidência de fingerprint em 30 dias, conservando o guard até o fim da telemetria.
- Preservação e remoção do guard imediatamente antes/no instante/após seu prazo de 60 dias.
- Falha de exclusão do conteúdo sem esquecer seus metadados necessários à recuperação.
- Limpeza dos dados operacionais de 31 dias sem remover a telemetria do mesmo banco.
- Continuidade dos contadores agregados exportados após excluir os detalhes operacionais.

A chamada de limpeza localiza o tipo de produção
CentralMobilidade.Telemetry.Worker.Retention.OperationalRetentionWorker e sua inscrição como
IHostedService na composição de Program.ConfigureServices. O componente deverá expor uma
passagem awaitable RunOnceAsync(CancellationToken), retornando Task ou ValueTask, e usar
o IClock/TimeProvider injetado para o instante de corte. O teste não substitui essa rotina
com SQL, fake de exclusão ou skip. A única falha injetada envolve DeleteExpiredAsync do
quarantine store real, preservando os demais métodos.

O relógio controlado governa os limites de PostgreSQL/quarentena; a expiração nativa do
InfluxDB usa o relógio real do servidor. As métricas verificadas são contadores agregados
do processo/exportador existente, com rótulo fixo de escopo OpenTelemetry permitido e sem
rótulos por lote/empresa/veículo. Isso não comprova histórico durável do Prometheus após
reinício, os novos instrumentos da T062 ou a exclusividade de um lease distribuído.

```powershell
dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests --filter FullyQualifiedName~OperationalRetentionTests
```

Os relatórios ficam ignorados em artifacts/validation/T056/. A implementação da T061 é
necessária antes de afirmar que a limpeza operacional está funcionando automaticamente.

Validação final: cinco novos casos aprovados e 11 falhas TDD pela ausência do componente
T061. A suíte funcional Linux completa teve 343 aprovações e 12 falhas em 355 casos:
essas 11 mais a regressão anterior da T055, prevista para T058. Contratos: 80/80;
arquitetura: 47/47; zero skips. Build da solução sem avisos ou erros. A tentativa Windows
foi bloqueada pelo Controle de Aplicativo na carga de DLL (0x800711C7); nenhuma política
de segurança foi alterada. O benchmark de capacidade ADR-001 continua separado.
