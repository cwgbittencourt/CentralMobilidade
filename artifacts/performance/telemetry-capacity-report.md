# Telemetry capacity gate — T094

Data: 2026-10-17 (America/Sao_Paulo). Status: **gates de carga reprovados;
soaks bloqueados; T094 pendente e capacidade não aprovada**.
Este relatório separa medidas reais, tentativas que pararam no preflight e gates
que ainda não possuem evidência. Testes de transporte simulado não contam como carga.

## Ambiente e método

- Engine Docker 29.8.2, Linux x86_64, 16 CPUs lógicas, 33.608.843.264 bytes de RAM
  (~31,30 GiB) disponibilizados à VM Docker Desktop.
- Host: AMD Ryzen 7 5800X3D, oito cores físicos/16 threads, 68.626.685.952 bytes de
  RAM física. O dispositivo que hospeda o VHDX do Docker não foi identificado;
  os resultados não isolam latência de disco físico ou outros workloads do desktop.
- API: limite efetivo 2 CPUs/4 GiB; worker: 6 CPUs/12 GiB. Total da aplicação:
  **8 CPUs/16 GiB**, verificado por `docker inspect`, não inferido do host.
- PostgreSQL: 2 CPUs/2 GiB, comando explícito `fsync=on`, `synchronous_commit=on`; Influx: 2 CPUs/4 GiB;
  RabbitMQ: 1 CPU/1 GiB. Dependências e coleta ficam fora do orçamento da aplicação.
- Apps publicados em Release, source de produção no commit `520f8ed`; o runner
  acrescenta suporte explícito a `Vehicles=10000` ou `100000` na T094.
- Imagem API: `sha256:881a0c1ab82c02f1057711fd987b383be208c6eb7011a50c8e74fe9929dd5c06`.
- Imagem worker: `sha256:5362e95f5efb544b53f23d7e809df0faabbcf7eb1eb61d79ab33581c599d9004`.
- PostgreSQL `17.11-bookworm`, RabbitMQ `4.3.6-management-alpine`, InfluxDB
  `3.11.6-core` (object store file, database `telemetry`, retenção 60 dias),
  Prometheus `v3.15.0-busybox`. Worker concurrency 4, configuração de processamento
  padrão; credenciais efêmeras e migrations reais, sem atalhos de autenticação/durabilidade.
- Amostragem de traces configurada em 0,01, sem exportação OTLP externa. Grafana,
  consultas de usuários e dados históricos envelhecidos não participaram da carga.
- A fixture usa credenciais efêmeras de bootstrap para os stores e não replica
  todas as restrições de hardening do Compose de produção. Não é aprovação de
  permissões mínimas, root filesystem read-only ou configuração de produção.
- Gerador .NET no host Windows, fora dos limites dos containers; binário Debug.
  Docker/gerador compartilham o host físico, portanto o ensaio não representa
  hardware bare-metal dedicado nem proteção contra perda do host.
- Cada perfil recebe projeto Compose UUID, volumes e namespace de dados novos.
  Nenhum deployment existente é reutilizado. O teardown para/remove apenas os
  containers/rede dessa fixture e conserva seus volumes para investigação.

Os lotes gzip são agendados a cada 100 ms: 50 eventos para 10.000 veículos (500/s),
500 para 100.000 veículos (5.000/s). Cada veículo emite a cada 20 s. São 100 empresas
sintéticas válidas sem cadastro, sem PII. Campos numéricos simples favorecem compressão;
esse corpus não substitui variedade real, reconexões ou cardinalidade histórica.

O gate de carga dura 600 s por perfil. Se passar, o preparador executa um soak
adicional de 1.800 s em outra fixture do mesmo perfil. Falha de carga/preflight
bloqueia o soak; bloqueio não é resultado aprovado. Não há aprovação automática de
produção mesmo que esses ensaios passem.

## Coleta e limites

CPU/RAM vêm de `docker stats` de API+worker; CPU é a soma dos percentuais por core
dividida pelos oito cores alocados, e memória é a medida do Docker (working set,
com tratamento de cache do engine). Limites exigidos: CPU <=85% e RAM <=12 GiB.
Disco é `du` dos volumes de PostgreSQL, RabbitMQ e Influx montados read-only no
observador; não inclui logs do engine, backups, imagens ou outros mounts do host.

Backlog em mensagens vem da API management RabbitMQ. A idade exposta nesta fixture
é **idade conservadora do batch pendente mais antigo no PostgreSQL**, incluindo
Accepted/Processing/Retrying, e não timestamp lido da cabeça da fila. Essa diferença
impede tratar a série como prova exata de oldest-message RabbitMQ/TTL.

O coletor publica medidas reais por arquivo LF em servidor HTTP da rede privada;
Prometheus faz scrape a cada 5 s. Queries descartam medidas do coletor com mais
de 20 s. Quantis de escrita vêm do histograma real do worker, janela de 30 min,
após dois lotes de aquecimento separados por scrapes. A janela inclui aquecimento,
então os quantis não isolam perfeitamente só a fase medida.

O runner registra intake p50/p95/p99, taxas/bytes, amostras de recursos e outcomes
dos lotes. Uma consulta SQL independente no Influx conta pontos do namespace de
cada execução depois da reconciliação. Estado terminal e contagem final não provam,
sozinhos, 99% de queryability em cinco minutos nem sobrevivência a reinício/perda
de host. Essas lacunas não viram PASS por ausência de erro.

## Resultados

| Gate | Situação |
| --- | --- |
| Preflight da fixture funcional e métricas | Passou na fixture `5b63335f2d234abba50c7f8a178dda68`; valores reais de 8 cores/16 GiB e três quantis finitos |
| Baseline 10.000 veículos, 500 eventos/s | **FAIL**: 311,44 eventos/s confirmados; confirmação integral e reconciliação no prazo não atendidas |
| Target 100.000 veículos, 5.000 eventos/s | **FAIL**: 4.199,17 eventos/s confirmados; confirmação integral e reconciliação no prazo não atendidas |
| Soak de 30 minutos por perfil | **Não executado**: ambos bloqueados pelos gates de carga reprovados |
| Queryability em até cinco minutos | Ainda sem prova temporal independente |
| Idade real da cabeça da fila | Série aproximada de pending batch; gate exato sem prova |
| Capacidade de produção | **Não aprovada** |

### Baseline medido

Fixture `5b63335f2d234abba50c7f8a178dda68`, dataset/relatório
`2b9ec355ffa048d7a02b6aee5ac481e1`. Dez minutos de oferta, seguidos por até cinco
minutos de reconciliação. Amostras de 16:08:30 a 16:23:33 (-03:00).
Taxa inclui conclusão dos POSTs: janela de 600,606 s.

| Medida | Resultado |
| --- | ---: |
| Eventos programados | 300.000 |
| Eventos em POSTs tentados | 289.700 |
| Eventos com recibo `202` válido | 187.050 |
| Eventos reconciliados por status no prazo | 123.900 |
| Pontos contados independentemente no Influx ao fechar | 176.800 |
| Taxa confirmada | 311,44 eventos/s |
| Intake HTTP p50 / p95 / p99, todas as respostas | 231,42 / 1.709,19 / 3.522,89 ms |
| CPU máxima observada da aplicação | 54,94% dos oito cores |
| Memória máxima observada | 318.557.389 bytes (~303,80 MiB) |
| Backlog máximo observado | 1.638 mensagens |
| Idade pendente máxima observada | 387,61 s |
| Crescimento observado dos três volumes | 681.443.328 bytes (~649,88 MiB) |
| Bytes gzip / descomprimidos tentados | 3.238.134 / 43.393.460 |

Falhas registradas: `generator_concurrency_saturated`, `intake_not_202`,
`target_not_acknowledged`, `sustained_rate_not_met`, `nonterminal_batches` e
`acknowledged_not_reconciled`. Foram 206 slots sem POST e 2.053 respostas diferentes
de `202`; o runner não distinguiu os status HTTP individuais nesse relatório.

A contagem independente supera o contador reconciliado por polling porque a API
não confirmou todos os estados dentro do orçamento de consulta. Ao mesmo tempo,
10.250 eventos confirmados ainda não constavam da contagem Influx naquele instante.
Isso descreve pendência no corte, **não prova de perda**: os volumes/fila foram
conservados. Não há recuperação completa posterior certificada neste resultado.

Os percentis de observação terminal (p50 477,70 s; p95 783,76 s; p99 851,75 s)
incluem o início tardio e o custo do polling, portanto não são latência real de
persistência/queryability. A API esteve perto de sua quota individual de dois cores
em várias amostras; isso sugere investigar ingestão/autenticação/publicação e a
divisão 2+6, sem concluir que toda alocação de oito cores teria o mesmo resultado.

### Target medido

Fixture `7a19f265bbf24138b08782027533d346`, dataset/relatório
`13dbeab612514a5aa7c967b9043b1434`. Dez minutos de oferta, seguidos por até cinco
minutos de reconciliação. Amostras de 16:25:16 a 16:40:18 (-03:00).
Taxa inclui conclusão dos POSTs: janela de 600,594 s.

| Medida | Resultado |
| --- | ---: |
| Eventos programados | 3.000.000 |
| Eventos em POSTs tentados | 2.974.500 |
| Eventos com recibo `202` válido | 2.522.000 |
| Eventos reconciliados por status no prazo | 97.000 |
| Pontos contados independentemente no Influx ao fechar | 200.000 |
| Taxa confirmada | 4.199,17 eventos/s |
| Intake HTTP p50 / p95 / p99, todas as respostas | 190,66 / 1.201,06 / 2.033,72 ms |
| CPU máxima observada da aplicação | 63,09% dos oito cores |
| Memória máxima observada | 317.718.528 bytes (~303 MiB) |
| Backlog máximo observado | 4.780 mensagens |
| Idade pendente máxima observada | 842,78 s |
| Crescimento observado dos três volumes | 736.198.656 bytes (~702,09 MiB) |
| Bytes gzip / descomprimidos tentados | 17.244.520 / 448.520.250 |

O target registrou as mesmas seis categorias de falha do baseline. Foram 51 slots
sem POST e 905 respostas diferentes de `202`. A taxa de aceitação é cerca de 84%
da meta, e a persistência/reconciliação ficou muito atrás das confirmações. No
corte, 2.322.000 eventos confirmados ainda não constavam da contagem Influx.
Os volumes, inclusive mensagens pendentes, foram preservados; esse delta não
autoriza declarar perda definitiva nem recuperação completa.

Limites superiores de observação terminal: p50 766,37 s; p95 866,72 s; p99 891,70 s.
Eles incluem espera/polling e não são percentis de disponibilidade real dos pontos.

### Escrita e decisão do gate

| Máximo observado dos quantis de escrita em janela de 30 min | Baseline | Target |
| --- | ---: | ---: |
| p50 (s) | 0,7500 | 0,6133 |
| p95 (s) | 0,9750 | 0,9613 |
| p99 (s) | 0,9950 | 0,9923 |

Esses máximos são de quantis móveis do histograma por chamada, incluindo warmup;
não são percentis globais de tempo por evento nem taxa total de persistência.

CPU e memória passaram nos limites observados; taxa, confirmação integral e
reconciliação no prazo **falharam** nos dois perfis. A aprovação exige corrigir ou
reconfigurar os limitantes e repetir carga e soak, preservando publisher confirms,
WAL/sync, idempotência e guards. Investigar distribuição de CPU, custo de intake,
concorrência e transações de persistência por linha; este ensaio não identifica
causalidade suficiente para escolher uma correção de produção.

Permanecem sem prova: soak, backlog/idade estabilizados, 99% de queryability em
cinco minutos, recuperação completa de todos os recibos e capacidade com histórico
representativo. Burst/outage reais não foram executados nesta rodada de gates
reprovados. Por isso **T094 continua desmarcada** no tasks.md, mesmo com o relatório
e o preparador entregues. Containers dos dois perfis foram encerrados/removidos;
volumes de evidência permanecem. Nenhuma instalação existente foi modificada.

Os preflights anteriores não enviaram carga programada: identificaram ferramenta
EF não restaurada, falha de importação do preparador e problemas na exposição de
métricas (conectividade host/VM, servidor HTTP e terminação CRLF). Foram corrigidos
sem substituir medidas ausentes por constantes/zeros. Lotes de aquecimento foram
persistidos nas fixtures correspondentes; não contam como resultado de capacidade.

## Reprodução e evidência privada

```powershell
dotnet tool restore
dotnet build CentralMobilidade.slnx
docker build -f deploy/containers/api.Dockerfile -t centralmobilidade-api:t094 .
docker build -f deploy/containers/worker.Dockerfile -t centralmobilidade-worker:t094 .
py -3 scripts/Run-CapacityGates.py --duration 600 --soak-duration 1800
```

Requer Python 3, Docker/Compose, SDK .NET 10 e ferramentas locais do manifesto.
O preparador usa imagens/volumes novos; precisa de recursos para aplicação,
dependências e gerador. Porta/credenciais são geradas e mantidas somente na pasta
ignorada `artifacts/validation/T094/<run>/`. **Não publique essa pasta inteira**:
contém configuração privada, credenciais efêmeras, inspeções e logs brutos.

`summary.json` guarda perfil, limites, digests e hashes dos arquivos de código do
runner. Os `steady-*.json` guardam recibos/contagens/percentis/séries, e a consulta
independente de Influx é registrada no summary. Este Markdown é a evidência
sanitizada versionada; resultados completos permanecem locais.

Validação da automação: 45 testes de protocolo/geração/regressão aprovados antes
dos ensaios e novamente após a revisão final; build da solução sem avisos ou erros.
Eles verificam o runner e seus casos de falha, sem alterar os resultados
negativos das execuções reais descritas acima.
