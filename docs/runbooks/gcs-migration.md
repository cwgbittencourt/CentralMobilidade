# GCS e retenção de cinco anos — T089

Este é o plano futuro do object store do **InfluxDB 3 Core**, associado à FR-029.
Não altera o deployment, cria bucket ou certifica capacidade. O ambiente atual
usa `influxdb:3.11.6-core`, node ID `queryable`, object store `file`, volume
`influxdb_queryable_data` e database única `telemetry`, criada com retenção `60d`.
GCS não substitui PostgreSQL, fila ou quarentena, nem adiciona HA ao Core single-node.

## Configuração e identidade futuras

A [documentação oficial de GCS no Core](https://docs.influxdata.com/influxdb3/core/object-storage/gcs/)
define `--object-store google`, `--bucket` e autenticação Google. Exemplo de
argumentos **para uma implantação isolada revisada**, não um comando de migração:

```text
influxdb3 serve
  --node-id queryable
  --object-store google
  --bucket BUCKET_DEDICADO_REVISADO
  --google-service-account /run/secrets/gcs_service_account.json
  --admin-token-file /run/secrets/influxdb_admin_token
  --disable-authz health,ping,metrics
  --disable-package-management
```

O exemplo mantém a identidade da origem para um restore compatível. Não execute
dois processos sobre o mesmo catálogo/bucket. Remova as opções locais de object
store do override futuro; montar o volume antigo não transfere seus dados ao GCS.
Fixe imagem/digest e valide os argumentos em `serve --help` da versão exata:
documentação online pode descrever versão posterior. Não combine upgrade e migração.

O arquivo JSON é uma alternativa explícita de credencial; o argumento contém
somente o caminho. Monte-o read-only com acesso apenas ao UID do Influx (1500 no
Compose atual); não versione, imprima ou inclua seu conteúdo em argumentos/logs.
Prefira identidade de workload sem chave longa onde o runtime e a biblioteca da
imagem suportarem; demonstre obtenção/renovação do token e acesso no processo real
antes de retirar a opção de arquivo. ADC da aplicação Pub/Sub ou login `gcloud`
do operador não comprova identidade do Influx. Token HTTP Influx e identidade IAM
do bucket são credenciais distintas. Rotação deve incluir revogação e teste de falha.

Reserve bucket privado por ambiente, proprietário, projeto, localização aprovada,
classe inicial de armazenamento e orçamento. Revise acesso uniforme, prevenção de
acesso público, criptografia e custódia de chaves; se usar CMEK, inclua permissões,
rotação e recuperação da chave na prova de restore. Restrinja IAM ao bucket e
identidade do Influx. A orientação do fornecedor usa `roles/storage.objectAdmin`;
revise [as permissões do papel](https://docs.cloud.google.com/storage/docs/access-control/iam-roles)
e valide leitura, listagem, criação, atualização e exclusão necessárias ao engine.
Não conceda administração do projeto como atalho. Separe identidade de backup e
operador da identidade de serviço; API, worker e Grafana continuam usando HTTP/token
Influx, sem acesso direto ao bucket. Exija TLS, DNS/egress e políticas de rede reais.

## Prérequisitos e bloqueios de retenção

| Item | Gate antes da operação |
| --- | --- |
| Cópia consistente | Backup coordenado restaurado em isolamento, inventário/hash e versão/identidade compatíveis; seguir [T087](backup-restore.md) |
| Formato do engine | Provar transferência completa do catálogo, checkpoints, WAL e objetos referenciados, preservando caminhos relativos; copiar apenas Parquet é insuficiente |
| Retenção do engine | Política aprovada para cinco anos, aplicada e consultada na database existente; initializer atual cria `60d` somente se a database não existe |
| Opções da aplicação | `TelemetryOptions` exige `LocalTelemetryRetention == 60 dias`; não existe perfil de produção de cinco anos pronto para habilitar |
| Imutabilidade | Guards/fences em PostgreSQL precisam cobrir toda a retenção de telemetria, inclusive replay tardio e conflito; 30 dias de evidência não substituem esse guard |
| Cleanup e restauração | Ajustar e testar prazos, limpeza e consultas de identidade conjuntamente, sem apagar ou recriar guards existentes |
| Prova gerenciada | IAM efetivo, operações condicionais, escrita sincronizada, reinício/recuperação, consulta, exclusão controlada e falhas de rede/quota |

Alterar bucket ou retenção Influx sozinho não satisfaz esses gates. O código e o
initializer precisam de uma mudança futura revisada para suportar cinco anos;
não desabilite o validador nem alongue globalmente todos os prazos. Fila continua
com sete dias; status, evidência limitada e quarentena mantêm seus prazos próprios
de até 30 dias. Quarentena continua no adapter de filesystem protegido; este plano
não implementa `IQuarantineStore` em GCS nem autoriza guardar rejeições por cinco anos.

Não use lifecycle `Delete` genérico no bucket ativo: idade de objeto não é idade
do evento e catálogo/WAL podem continuar necessários. A [política de lifecycle](https://docs.cloud.google.com/storage/docs/lifecycle)
atua sobre objetos, não linhas. Retenção bloqueada/holds também podem impedir a
limpeza do engine; versionamento e soft delete afetam espaço, custos e eliminação
real. Revise essas políticas em conjunto, com testes de retenção e restauração.
Backups ficam em destino independente, com acesso/prazo próprios; bucket ativo
durável não é backup contra exclusão lógica ou credencial comprometida.

## Migração com janela aceita

1. Registre responsáveis, origem/destino, imagem, node ID, database, inventário,
   limites de divergência, janela, RPO/RTO medidos e reserva de rollback. Preserve
   configuração, secrets, guards e backup restaurável. Não migre fila junto:
   a [T088](queue-provider-cutover.md) tem gates separados.
2. Ensaie em ambiente privado isolado com dados sintéticos/representativos. Defina
   uma ferramenta de cópia verificável e bounded, com manifesto, checksums,
   retomada e relatório de faltas/extras. **Não há migrador GCS implementado neste
   repositório**; sem ensaio de restauração compatível, não iniciar a operação.
3. Pause produtor/ingresso e finalize requests; preserve o último `202` e resultados
   ambíguos. Pare graciosamente worker e qualquer escritor/cleanup do Influx e
   PostgreSQL antes do corte coordenado. A fila conserva o trabalho pendente;
   orçamento de manutenção deve caber na retenção/idade real do backlog.
4. Pare o Influx de forma limpa e mantenha a origem intacta. Transfira o conjunto
   completo consistente, não uma cópia viva de arquivos em mutação. O procedimento
   do fornecedor para [backup/restore](https://docs.influxdata.com/influxdb3/core/admin/backup-restore/)
   orienta o conjunto do engine; compatibilidade file→GCS deve ser provada na
   versão fixada. Não presuma que trocar a flag descobre o catálogo local.
5. Confira manifesto/hash antes de iniciar o destino isolado, sem initializer
   criando catálogo vazio ou processo antigo acessando o mesmo conjunto. Verifique
   database, schema, retenção real, timestamps em milissegundos, identidades,
   valores e contagens por faixa/empresa/veículo; totais globais não bastam.
6. Teste escrita `no_sync=false`, reinício, recuperação WAL, consulta autenticada,
   falhas de IAM/rede e canário completo pelo worker: ponto, status e ACK. Teste
   replay idêntico sem nova escrita e conflito preservando a primeira versão.
   Preserve PostgreSQL e suas leases/fences; não recrie os guards para passar testes.
7. Só após aceite, configure endpoint/token HTTP dos clientes se necessário,
   inicie um único writer ativo, reabra gradualmente e monitore erro, latência,
   backlog, idade, crescimento e custo pelo período aprovado. GCS não exige nova
   interface de negócio no writer. A existência de objetos não prova queryability.

Antes de qualquer escrita exclusiva no destino, o retorno pode usar o conjunto
original preservado, com ingresso fechado e canário verificado. Depois de novas
escritas, voltar à cópia antiga perde dados: feche ingresso, preserve destino,
reconcilie e faça um novo corte/restauração completa ensaiada. Não restaure
PostgreSQL antigo contra Influx novo, não execute dupla escrita improvisada e
não reenvie POSTs gerando novos batch IDs como migração. Se GCS estiver indisponível,
recupere o destino ou mantenha manutenção até haver caminho sem perda comprovado.
Retire a origem somente após aceite, backup recuperável e fim da reserva de rollback.

## Gate de capacidade para cinco anos

O alvo de 100.000 veículos a cada 20 segundos representa 5.000 eventos/s,
432 milhões/dia e aproximadamente 788,4 bilhões em 1.825 dias. É uma estimativa
de planejamento com ano de 365 dias; defina cinco anos/calendário e atrasos na
política efetiva. Não representa capacidade alcançada ou tamanho em disco.

Meça bytes/evento persistido, compressão Parquet, cardinalidade, arquivos,
catálogo/WAL, crescimento de guards/índices PostgreSQL, backups, versões e margem
temporária de cópia/compaction. Projete espaço por componente usando taxa medida
vezes horizonte, mais overhead/margem explícitos. Inclua disco/cache local,
CPU/RAM, throughput, requests GCS, leituras históricas, egress, recuperação e
custos das classes/retention policies; não use bytes gzip HTTP como bytes armazenados.

| Prova | Critério de liberação |
| --- | --- |
| Carga normal e bursts offline | Throughput sustentado, backpressure limitado, sem perda; 99% dos eventos aceitos consultáveis em até cinco minutos na carga acordada |
| Histórico representativo | Faixas antigas de viagem por veículo e saúde dentro dos limites existentes, com quantidade realista de arquivos/cardinalidade, caches frio e quente e latências aprovadas |
| Idempotência duradoura | Replay/conflito próximo do limite de retenção e depois de cleanup da evidência curta; guard original continua efetivo |
| Soak e capacidade | Crescimento medido e projeção documentada de cinco anos com orçamento/margem e alarmes antes de saturação |
| Falhas e recuperação | IAM negado, quota/rede, reinício/perda de host e restore completo em host independente, dentro de RPO/RTO aprovados |
| Retenção e privacidade | Expiração demonstrada por camada, backups incluídos, sem raw/PII/secrets em logs, labels ou relatórios públicos |

Core continua single-node; storage externo não elimina gargalos de consulta,
limites de arquivos ou necessidade de histórico representativo. Se a prova falhar,
reduza o escopo ou revise engine/arquitetura em decisão separada; não declare
capacidade de cinco anos a partir de smoke tests ou da durabilidade do bucket.

Guarde evidência privada com versão/configuração sanitizada, método/corpus, carga,
percentis, crescimento, custos, falhas e resultados de restore. Sem essas provas
e as mudanças de retenção/guards, FR-029 permanece um gate futuro. A T089 entrega
o runbook; nenhum ensaio GCS, migração ou benchmark de cinco anos é alegado aqui.
