# Prova final de 100.000 eventos — T102

**PASS em 2026-10-20.** A CLI `final-100k-grafana` executou a prova real completa:
200 lotes gzip, 100.000 eventos, reconciliação integral no InfluxDB, queries dos
dashboards de produção e seis renders Grafana revisados visualmente.

## Execução e origem

- Run/namespace: `439c1fc655a14757b19cbd756d22efb2`.
- Projeto Docker exclusivo: `cm-t102-439c1fc655a1`, volumes novos e portas loopback
  efêmeras; nenhum deployment existente ou catálogo foi alterado.
- CLI: **18:54:11.987–19:00:27.944 UTC**, ou **15:54:11–16:00:27 UTC−03:00**.
  Os tempos incluem a prova funcional, sem interpretação como throughput de capacidade.
- Base de produção `01408d8` (T113), com preparador T102 e correções do runner
  neste commit; nenhuma alteração em `src/`. O manifesto privado guarda os hashes
  das fontes do runner e do preparador usadas em cada tentativa.
- Python 3.14.6, .NET SDK 10.0.401, Docker Engine 29.8.2 Linux x86_64,
  Compose 5.6.0, VM 16 CPUs/31,3 GiB; API 2 CPUs/4 GiB, worker 6 CPUs/12 GiB.
- PostgreSQL `17.11-bookworm`, RabbitMQ `4.3.6-management-alpine`,
  InfluxDB `3.11.6-core`, database `telemetry`, retenção 60 dias.
- Grafana `12.2.5`, Infinity assinado `4.1.1`, renderer `v4.0.17`.

| Imagem | ID SHA-256 |
| --- | --- |
| API `:t113` | `8a53b74ccbdb33db8b4ea587d77e2b42d9def8103f619920d89ad66beedd5636` |
| Worker `:t113` | `3a370caa275a2ba4a85dfa80d1fa452052dd398029b71da6eedfd168bc0006d6` |
| Grafana `:12.2.5-infinity-4.1.1` | `2acbb8ba4742e534838052648986c100cc9a19ea1eef923ff21d6bdeaa744115` |
| Renderer `:v4.0.17` | `f5211dfc79628c2269fcc67367b0d6c1db98140e036969d064b9123c335a9955` |

```powershell
dotnet build tests/CentralMobilidade.Telemetry.LoadTests --no-restore
py -3 scripts/Run-CapacityGates.py --final-proof --api-image centralmobilidade-api:t113 --worker-image centralmobilidade-worker:t113
```

O preparador aplica migrations, aquece fora do namespace final, provisiona os
templates reais internos e executa `Provision-GrafanaOrganizations.ps1` para o
viewer. A CLI recebe credenciais distintas de produtor, Influx e dois service
accounts Grafana Viewer, um por organização. Bootstrap só participa da preparação.
O viewer API tem apenas `telemetry.read`, organização 2 e empresa `fp-<run>-00`.
Não há lookup cadastral conectado nem allowlist de empresas do produtor.

## Evidência e resultados

| Verificação | Resultado real |
| --- | --- |
| API ready / Grafana health | HTTP 200 / `database=ok`, versão 12.2.5 |
| PostgreSQL | `fsync=on`, `synchronous_commit=on` |
| Corpus | 10 empresas novas, 1.000 veículos, 100 pontos/veículo, intervalos de 20 s |
| Chegada | Amostras 50–99, depois 0–49 atrasadas; viagem `proof-trip` preservada |
| Intake | 200 respostas 202 com UUIDs distintos, 500 linhas/lote |
| Terminais | 200 Completed; total/accepted=100.000; rejected/replayed=0 |
| Influx | Count inicial 0, final 100.000 e repetido após leitura; 100 páginas de 1.000 |
| Reconciliação integral | 100.000 identidades únicas, empresa/veículo/timestamp e todos os campos comparados |
| Interno latest | 100 veículos por empresa nas 10 empresas, posição da amostra 99 |
| Interno trip | 100 pontos ordenados de `proof-trip`, inclusive os recebidos atrasados |
| Interno health | 10.000 por empresa; soma das dez empresas = 100.000 |
| Viewer latest/trip/health | Empresa 00: 100 posições, 100 pontos da viagem, 10.000 aceitos e 0 rejeitados |
| Isolamento viewer | Somente datasource Infinity; empresa 01 retorna upstream 404 e zero linhas |
| Renders | Seis PNGs reais 1000×600, revisados; tabelas com dados e sem erro/empty panel |

O Grafana retorna HTTP 400 quando Infinity encapsula o 404 esperado da API na
consulta negada. Somente essa consulta permite HTTP 400, e ainda exige metadata
upstream 404 e nenhuma linha. Demais falhas HTTP continuam reprovando a prova.
Health interno agrega por tempo do evento; viewer agrega por tempo de processamento.

Consulta independente sem filtro temporal, executada antes e após o corpus:

```sql
SELECT count(*) AS n FROM telemetria
WHERE empresa_id LIKE 'fp-439c1fc655a14757b19cbd756d22efb2-%'
```

Resultados `[{"n":0}]` e `[{"n":100000}]`. A paginação usa ordenação por
`empresa_id,veiculo_id,time`, `LIMIT 1000 OFFSET ...`; a conferência de todos os
campos/identidades evita aprovar apenas pela igualdade do count.

- [200 status terminais completos, sintéticos](reviewed/batches.json).
- [Resultados de todos os dashboards: campos e colunas das frames](reviewed/dashboard-results.json).
  Metadados de provider/configuração não foram publicados.
- [Manifesto revisado, report, health, totais e hashes SHA-256 da evidência bruta](reviewed/evidence.json).
- Corpus SHA-256: `6bf7cba2d1f969beac1b22c1ff279e830799ad5024fa6eeb3bf63301da21863c`.

| Painel | Interno | Viewer |
| --- | --- | --- |
| Última posição | [PNG](reviewed/internal-latest-vehicle-position.png) | [PNG](reviewed/viewer-latest-vehicle-position.png) |
| Viagem | [PNG](reviewed/internal-vehicle-trip.png) | [PNG](reviewed/viewer-vehicle-trip.png) |
| Ingestão | [PNG](reviewed/internal-company-ingestion-health.png) | [PNG](reviewed/viewer-company-ingestion-health.png) |

Revisão visual: latest mostra velocidade 99 e ignição desligada; viagem começa na
amostra 0 e avança a cada 20 s; health viewer mostra 10.000/0. A viewport corta os
IDs longos de empresa e mostra só as primeiras linhas; valores completos e todas
as linhas foram conferidos nas frames, sem inferir 100.000 pontos apenas do PNG.
Os arquivos publicados contêm somente dados sintéticos e resultados revisados.

## Tentativas preservadas e correções

- Preflight histórico `456c4e106159428ba0ac4c0e96941d52`: readiness 503, corpus não
  enviado. T113 corrigiu o probe RabbitMQ; preflight `e455a7c50a8f42ce806a9f5e8cb6aac5`
  observou ready 200, sem executar a prova completa.
- Tentativa `6ccd446dc5a94e208772a66ed9537f6a`: 200 Completed e count 100.000;
  runner interpretou UTC sem offset do SQL como horário local do Windows e falhou
  em `stored_identity_mismatch`. Consulta diagnóstica confirmou os timestamps.
  Correção: assumir UTC explicitamente, com três testes de formatos/fuso.
- Tentativa `2c548205590c4a62abdbfa6597c16125`: reconciliação integral e três renders
  internos passaram; runner bloqueou o HTTP 400 da negação viewer. Correção de
  protocolo acima, mantendo verificação de upstream 404/zero linhas.
- Cada repetição usou namespace/volumes novos. Nenhuma tentativa parcial foi
  promovida a PASS nem reutilizou corpus para contornar o count inicial zero.
- Build sem erros/avisos; **40/40 testes** de corpus/protocolo passaram após as
  duas correções. Sintaxe Python e whitespace verificados.

## Preservação e limites

Configurações privadas, tokens, logs e respostas completas permanecem ignorados em
`artifacts/validation/T102/<run>/` e `100k-final-proof/<run>/`. Ao concluir, o
preparador remove apenas seus containers/rede e preserva os cinco volumes de cada
tentativa, sem prune/reset/remoção de dados existentes.

InfluxDB Core nesta versão oferece apenas token administrativo; a fixture não
aprova privilégios mínimos de produção. Prometheus serve ao observador auxiliar;
não foi iniciado Loki e não se aprova o dashboard de plataforma com esta prova.
`CapacityApproved=false`: esta execução funcional não aprova 5.000 eventos/s,
soak, cinco anos de armazenamento, perda física de host, segurança/LGPD ou release.
T094/T096/T097/T098/T103 continuam com seus próprios critérios e pendências.
