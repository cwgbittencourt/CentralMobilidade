# Projeções de armazenamento — T095

Data: 2026-10-20, America/Sao_Paulo. **Projeções condicionais calculadas e verificadas
com amostra real; capacidade de produção e retenção envelhecida não aprovadas.**
A conclusão da T095 entrega medidas, modelo reproduzível e suas limitações. Não
converte o resultado reprovado da [T094](telemetry-capacity-report.md) em PASS.

## Experimento e rastreabilidade

Fixture nova `cm-t095-8b559d65c2e8`, de 11:07:54 a 11:10:18 (-03:00),
143,794 s incluindo preparação/coleta do módulo. Não é uma carga de dez minutos
nem um soak. Comandos e testes estão no [guia](../../scripts/STORAGE-CAPACITY.md).

Código-base `ca6fd0b49aa7f18af61e9600d7b881e2738cc8b9`; API/worker usam imagens
Release da T094, sem mudanças em `src/` desde a construção delas:

- API `sha256:881a0c1ab82c02f1057711fd987b383be208c6eb7011a50c8e74fe9929dd5c06`.
- Worker `sha256:5362e95f5efb544b53f23d7e809df0faabbcf7eb1eb61d79ab33581c599d9004`.

Docker 29.8.2 Linux x86_64, 16 CPUs lógicas e 33.608.843.264 bytes de RAM na VM;
API limitada a 2 CPUs/4 GiB e worker a 6 CPUs/12 GiB. PostgreSQL 17.11-bookworm,
RabbitMQ 4.3.6-management-alpine, InfluxDB 3.11.6-core, Loki 3.7.0 com BusyBox
1.37.0 e observador Alpine 3.23.4. Python 3.14.6; esquema PostgreSQL atual aplicado
por migrations reais. `fsync/synchronous_commit = on/on` consultados no servidor.
Stores/coleta ficam fora do orçamento da aplicação. Esse perfil não prova taxa alvo.

O Influx usa database `telemetry`, object store file e retenção 60d. **Só nesta
fixture**, gen1=1m, WAL files/snapshot=10 e force-snapshot-max-age=30s aceleram a
observação de Parquet; o deployment permanece com sua configuração anterior.
Loki usa o arquivo de configuração do repositório. Credenciais efêmeras de bootstrap
e inicializadores privilegiados da fixture não são prova de hardening de produção.

Evidência privada: `artifacts/validation/T095/8b559d65c2e84c9fb68ac9d4e3258dbb/`.
`storage.json` SHA-256:
`405bc512e86f8f5abc4003f7939d756db08aaa0c558720766d356b12733f5ad0`.
`summary.json` guarda hashes do preparador/módulo e identidade das imagens;
`projections.json` guarda hash do input/modelo e reprodução do corpus. Configurações,
tokens e logs brutos continuam ignorados, sem publicação neste relatório.

## Amostra e medidas

São 20.000 identidades distintas, 100 empresas sintéticas, 20.000 veículos e
motoristas, com uma observação por veículo. IDs curtos, namespace UUID, valores
sintéticos sem PII real; não representam toda a cardinalidade/entropia futura.
Metade possui campos numéricos regulares; metade valores variados, placa opcional
e sete casas nas coordenadas. Seed 95, evento em Unix ms `1792504703000`.

| Grupo | Eventos | Line Protocol bytes | Gzip bytes | Raw/evento | Gzip/evento | Razão raw/gzip |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Regular | 10.000 | 1.716.780 | 100.753 | 171,678 | 10,0753 | 17,0395 |
| Variado | 10.000 | 2.206.767 | 515.453 | 220,6767 | 51,5453 | 4,2812 |
| Válidos combinados | 20.000 | 3.923.547 | 616.206 | 196,17735 | 30,8103 | 6,3673 |

Gzip usa nível 1 e mtime=0. A regeneração reproduziu exatamente os bytes agregados
dos dois grupos. A diferença de mais de cinco vezes entre os custos gzip mostra
por que a compressão da T094, de campos simples, não basta para dimensionamento.

Quarenta lotes de 500 válidos e um lote de 100 linhas inválidas foram confirmados
pela API com o worker parado. A fila observada tinha **41 mensagens ready, zero
unacked e 616.739 bytes de payload**, incluindo 533 bytes gzip do lote inválido.
Depois da retomada, os status reconciliaram **20.000 aceitos, 100 rejeitados e zero
replays**; consulta independente contou os mesmos 20.000 pontos no Influx.
Outros 100 GETs brutos sem autenticação receberam 401 e produziram 100 registros
de auditoria, sem divulgação de conteúdo.

O [catálogo de Parquet](https://docs.influxdata.com/influxdb3/core/admin/query-system-data/)
retornou **3 arquivos, 20.002 linhas e 1.133.868 bytes**: inclui os dois pontos de
aquecimento. Custo físico de Parquet observado: **56,687731 bytes/linha**; não
confundir com gzip de transporte. Configuração de snapshot/densidade de arquivos
afeta essa medida; não foi medido um histórico de 60 dias/cinco anos.

### PostgreSQL e filesystem

São deltas antes/depois com contagens exatas. `pg_total_relation_size` inclui
heap, índices e TOAST físicos, somando descendentes de partições e evitando contar
a tabela lógica e seus filhos duas vezes. Sem VACUUM de normalização: atualizações,
páginas parcialmente ocupadas e alocações iniciais afetam bytes/unidade.

| Relação | Novas linhas | Crescimento bytes | Bytes/linha |
| --- | ---: | ---: | ---: |
| event_identity_guards | 20.000 | 8.478.720 | 423,936 |
| event_identity_evidence | 20.000 | 6.447.104 | 322,3552 |
| ingestion_batches | 41 | 262.144 | 6.393,756098 |
| line_processing_results | 20.100 | 5.636.096 | 280,402786 |
| quarantined_rejections | 100 | 122.880 | 1.228,8 |
| audit_events | 100 | 65.536 | 655,36 |

O protótipo SQL da ADR-001 não substitui estes custos do schema/fluxo atuais.
O guard medido aqui difere dos 740,88 bytes/linha do protótipo; não é prova de
redução estável em qualquer tamanho/idade de base.

| Store | Alocado antes | Alocado com fila parada | Alocado final |
| --- | ---: | ---: | ---: |
| PostgreSQL | 49.377.280 | 49.410.048 | 138.473.472 |
| RabbitMQ | 311.296 | 966.656 | 978.944 |
| InfluxDB | 106.496 | 106.496 | 4.640.768 |
| Quarentena | 8.192 | 8.192 | 425.984 |
| Loki | 126.976 | 126.976 | 954.368 |

`du -sk` mede blocos alocados no filesystem Linux; `du -sb` mede comprimento
aparente. Isso não é tamanho real do VHDX no Windows nem capacidade de disco físico.
Exemplo: a quarentena cresceu **417.792 bytes alocados** para 100 objetos (4.177,92
bytes/objeto), mas só 57.674 bytes aparentes incluindo metadados/diretórios. São
custos separados dos 122.880 bytes de metadados SQL de rejeição.

RabbitMQ cresceu 655.360 bytes durante a retenção de 41 mensagens (15.984,390244
bytes/mensagem). É amostra fria de segmento/WAL, não custo marginal estável nem
prova de seis milhões de mensagens/semanas de backlog. Depois de ack, o volume
continuou maior: exclusão lógica não implica liberação imediata de segmentos.

WAL PostgreSQL gerado: **70.427.152 bytes**, ou 3.521,3576 bytes por válido no
contexto completo da amostra, incluindo rejeições, auditoria e atividade auxiliar.
Retenção de WAL/archive, checkpoints e backups precisam de política própria;
esse custo não foi somado como se fosse uma tabela permanente por evento.
O diretório Influx também contém catálogo/WAL, além de Parquet. Esses componentes
não crescem necessariamente com o mesmo coeficiente de retenção do Parquet.

### Logs

O stdout real de API/worker cresceu **258.646 bytes / 691 linhas**, ou 12,9323
bytes por válido no contexto medido. Inclui retomada do worker e tentativas
administrativas negadas; não representa uma taxa estável por evento em produção.

Foram enviados 2.000 replays limitados dessas linhas ao Loki em um stream de
teste. O store cresceu 827.392 bytes (413,696 bytes/linha), incluindo WAL/chunk/index
de uma alocação fria. Uma recriação isolada do container seguida de consulta
independente retornou **2.000 linhas** (`loki-reconciliation.json`). Não é teste de
perda física do host ou expiração/compaction de 30 dias. Um único stream e mensagens
repetidas não reproduzem a cardinalidade/variabilidade de todos os serviços.
Logs Docker encapsulados, host, PostgreSQL, RabbitMQ, Influx e outros backends
não foram medidos como corpus próprio; o custo global de logging permanece aberto.

## Modelo e projeções condicionais

Unidades: **TB = 10¹² bytes**, não TiB. Aritmética:
`bytes/unidade medidos × unidades/s assumidas × 86.400 × dias`.
Ano definido como 365 dias; cinco anos = **1.825 dias**, sem ajustar anos bissextos.
O cenário baseline assume 500 válidos/s e o target 5.000 válidos/s contínuos.

No target: 25,92 bilhões de válidos/60d e 788,4 bilhões/1.825d. A amostra contém
0,5% de rejeições **por válido**, portanto a hipótese adiciona 25 rejeições/s aos
5.000 válidos/s; não presume 5.000 linhas totais/s. Lotes reproduzem o mix medido:
500 válidos por lote, 100 inválidos por lote, total de 10,25 lotes/s. São suposições
de planejamento, não uma taxa de rejeição/ingestão observada sob carga alvo.

| Componente — target | Janela aplicada | Local (TB) | Futuro (TB) |
| --- | --- | ---: | ---: |
| Parquet | 60 / 1.825 dias | 1,469346 | 44,692607 |
| Guards PostgreSQL | 60 / 1.825 dias | 10,988421 | 334,231142 |
| Evidência de fingerprint | 30 dias em ambos | 4,177723 | 4,177723 |
| Estado de batch | 30 dias em ambos | 0,169869 | 0,169869 |
| Resultados de linha | 30 dias em ambos | 3,652190 | 3,652190 |
| Metadados de quarentena | 30 dias em ambos | 0,079626 | 0,079626 |
| Objetos de quarentena | 30 dias em ambos | 0,270729 | 0,270729 |
| Fila — alocação fria observada | 7 dias em ambos | 0,099090 | 0,099090 |
| Loki — probe frio | 30 dias em ambos | 0,185240 | 0,185240 |
| Uma cópia do payload stdout | Hipótese de 30 dias, não política configurada | 0,167603 | 0,167603 |
| Audit append-only | Hipótese de 0,1 acesso/s, horizonte 60 / 1.825 dias | 0,000340 | 0,010334 |
| **Soma ilustrativa** | Com as hipóteses acima | **21,260178** | **387,736155** |

| Perfil | Válidos/s assumidos | Soma local (TB) | Soma futura (TB) |
| --- | ---: | ---: | ---: |
| Baseline 10.000 veículos/20s | 500 | 2,126324 | 38,782916 |
| Target 100.000 veículos/20s | 5.000 | 21,260178 | 387,736155 |

Audit permanece em 0,1 acesso/s nos dois perfis, portanto a soma não escala
exatamente por dez. A retenção de audit não foi aprovada nem implementada pelo
cleanup de 30 dias; horizonte de cálculo não significa expiração. A cópia stdout
de 30 dias é hipótese de espaço, não comprovação de rotação configurada.

Payload gzip válido para sete dias: **0,093170 TB** no mix medido, sem overhead
de mensagem/replicação. Esse valor não é somado novamente à alocação do broker,
evitando dupla contagem. Usar só o corpus regular daria 0,030468 TB; só o variado,
0,155873 TB. A janela de sete dias permanece igual no futuro, não vira cinco anos.

Sensibilidade da quarentena (objetos + SQL, 30d, mesmos custos por rejeição):

| Rejeições por válido assumidas | Rejeições/s no target | Espaço ilustrativo |
| --- | ---: | ---: |
| 0,1% | 5 | 0,070071 TB |
| 0,5% (amostra) | 25 | 0,350355 TB |
| 1% | 50 | 0,700711 TB |
| 5% | 250 | 3,503555 TB |

Não extrapolar globalmente tudo para cinco anos: fila, status, evidência,
quarentena e observabilidade têm relógios próprios. O guard acompanha a telemetria
e domina o cenário futuro; removê-lo aos 30 dias para diminuir o orçamento quebraria
a proteção de identidade exigida. Os dois horizontes são hipóteses matemáticas;
o [perfil de cinco anos](../../docs/runbooks/gcs-migration.md) ainda exige mudança
revisada de configuração/código e capacidade no destino.

## Limites e decisão

A soma **não é orçamento de disco aprovado, limite superior garantido ou validação
de operação em 60 dias/cinco anos**. Faltam medição envelhecida de índices/retention,
VACUUM/fragmentação, densidade estável de Parquet, segmentos da fila, cardinalidade
completa, corpus real autorizado, carga concorrente, compaction/delete e espaço
livre de manutenção. Allocations frias podem superestimar custos marginais;
IDs maiores, versões de linhas, WAL/archive, réplicas, backups, scratch e crescimento
administrativo podem subestimar demanda real. Nenhum fator arbitrário foi usado
para declarar que essas ausências estão cobertas.

Cloud adiciona catálogo/WAL, cache, backups independentes, versões/soft delete,
replicação e operações de objetos; não foi criado bucket, executada migração GCS
ou validado custo/IO/consulta histórica no serviço. Host físico/VHDX e cópias fora
do host precisam de planejamento independente. A [T094](telemetry-capacity-report.md)
continua reprovada; [segurança](../../docs/security/security-check-results.md)
e [decisões LGPD](../../docs/security/lgpd-technical-controls.md) continuam pendentes.

Validação entregue: contagens independentes dos stores, custos por componente,
reprodução gzip, **10 testes de modelo aprovados**, checks de sintaxe/whitespace
e documentação. Os testes rejeitam inputs ausentes/zero/não finitos/overflow,
SQL não reconciliado, amostra incompleta e dupla contagem do payload da fila.
Não houve mudança de produção/migrations nem repetição das suítes .NET ou do
soak T094. A primeira preparação falhou antes da carga por PATH mínimo do Loki;
corrigida com chamadas BusyBox explícitas, seguida da amostra final completa.
Containers/redes foram encerrados; os volumes exclusivos das tentativas foram
preservados. Não se reutilizaram deployment/dados ou volumes T094.

T095 conclui a análise reprodutível das projeções **condicionais**. Antes do release,
validar as hipóteses com carga/retention representativas, definir políticas de
audit/logs/backups, resolver capacidade do guard e comprovar espaço/IO/recuperação;
não habilitar cinco anos nem reduzir controles com base nesta amostra curta.
