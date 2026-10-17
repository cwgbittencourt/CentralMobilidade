# Upgrade para um único InfluxDB — T106

Este procedimento é uma manutenção offline. Não rode o novo worker sobre o schema antigo.
A migração `20261014230000_SingleTelemetryStore` remove somente o schema PostgreSQL obsoleto.
Ela não consulta InfluxDB, não copia telemetria e não transforma um guard Pending em Queryable.
Qualquer guard não reconciliado ou linha remanescente de promoção bloqueia a migração inteira.
Não use `down --volumes`, não regenere tokens e não reescreva migrações aplicadas.
O [runbook de backup/restore](backup-restore.md) descreve o corte coordenado da
topologia atual. Para o legado com dois Influx, inclua também o volume pending e
seu token no mesmo corte; o conjunto de sete volumes atual não cobre esse legado.

## Inventário e preparação

1. Identifique explicitamente o projeto Docker, banco PostgreSQL, ambos os catálogos Influx e
   volumes reais. Ausência no contexto Docker local não prova que bancos externos estão vazios.
2. Pare API, consumidores, produtores de escrita e qualquer promoção/retention; preserve a fila.
   Registre o último batch aceito. Faça backup restaurável e coordenado de PostgreSQL, ambos os
   volumes Influx, fila e credenciais. Teste restauração isolada antes da manutenção com dados.
3. No PostgreSQL legado, exporte para armazenamento protegido os resultados completos de:

```sql
SELECT "Destination", "StorageLocation", "State", count(*)
FROM event_identity_guards GROUP BY 1,2,3;
SELECT "State", count(*), sum("ExpectedPointCount"), sum("ConfirmedPointCount")
FROM pending_telemetry_promotions GROUP BY 1;
SELECT count(*) FROM ingestion_batches;
SELECT count(*) FROM event_identity_evidence;
```

   Exporte também todas as promoções, guards e evidências, com chave, dono original, fence e
   deadlines; calcule SHA-256 dos arquivos e guarde-os com os backups. Não publique identificadores,
   coordenadas, tokens ou arquivos de telemetria em logs, Git ou evidências públicas.
4. Via `/api/v3/query_sql` autenticado em cada Influx, liste as tabelas, schema e contagens. Para
   `telemetria`, percorra páginas de no máximo 500 pontos e respostas de no máximo 2 MiB, com ordem
   estável `(time, empresa_id, veiculo_id)` e LIMIT/OFFSET enquanto as escritas estão paradas. Não
   use um COUNT global para inferir que cada identidade existe. Tabela/schema inesperados, erro
   de consulta/autenticação ou resposta truncada interrompem o procedimento.

## Copiar e verificar antes de alterar guards

Para cada página da origem pending:

1. Reconstrua Line Protocol v1 fechado, preservando todas as tags, campos, valores e o timestamp
   original em milissegundos UTC. Omitir um campo opcional presente altera o evento e é proibido.
   Calcule o fingerprint com `TelemetryLineProtocolV1Parser.Parse(...).Event.Fingerprint`, o mesmo
   algoritmo da aplicação; não use SHA-256 de JSON, da linha textual ou de sua ordem de campos.
2. Leia o guard/evidência pelo triplo `(empresa_id, veiculo_id, timestamp)`. Exija guard Committed,
   evidência não expirada e fingerprint correspondente. Reserved/Writing, falta de guard, evidência
   ausente/expirada ou conteúdo conflitante exigem investigação; não invente evidência nem declare
   esses eventos aceitos. Para Promoting, confirme que o processo anterior parou e aguarde a lease
   expirar. Retome o procedimento com os mesmos pontos, sem reaproveitar dono/fence antigo.
3. Exija exatamente um ponto por identidade lógica em cada store; duplicatas ou schemas
   incompatíveis interrompem o procedimento. Leia a mesma identidade em queryable. Conteúdo diferente interrompe o upgrade, preservando
   original, origem e guards. Se já for idêntico, não reescreva. Se ausente, envie a página gzip
   para `/api/v3/write_lp?db=telemetry&precision=millisecond&accept_partial=true&no_sync=false`.
   Use o nome real do banco queryable inventariado, caso não seja `telemetry`. Rejeição parcial,
   timeout ou resposta ambígua não confirmam cópia: releia cada identidade antes de prosseguir.
4. Releia cada ponto no destino, recalcule o fingerprint pelo parser real e compare origem,
   evidência PostgreSQL e destino. Exija contagem e conjunto de identidades iguais para a página.
   Só então produza um CSV privado com as colunas abaixo. O CSV é uma declaração do operador
   baseada em leitura real dos dois Influx; o script SQL não valida essas leituras remotamente.

Na mesma sessão `psql`, prepare e importe **uma página verificada**:

```sql
\set ON_ERROR_STOP on
CREATE TEMP TABLE single_influx_verified_page (
 company_id text NOT NULL, vehicle_id text NOT NULL, event_time timestamptz NOT NULL,
 source_fingerprint text NOT NULL, destination_fingerprint text NOT NULL,
 PRIMARY KEY(company_id, vehicle_id, event_time)
);
-- Comando psql; substitua o caminho pelo CSV privado produzido após a verificação:
\copy single_influx_verified_page FROM '/private/verified-page.csv' WITH (FORMAT csv, HEADER true)
\i scripts/sql/reconcile-single-influx-page.sql
```

O script usa transação, lock limitado, até 500 identidades e as transições/fences do schema
legado. Ele falha sem manifesto, com evidência ausente/expirada, conflito, dono incompleto ou
lease ativa. Não altera fingerprint, dono original, retenção, batch, ponto Influx ou quarentena.
A repetição de uma página já reconciliada não incrementa o fence novamente. Para a próxima
página, use `TRUNCATE pg_temp.single_influx_verified_page` e importe o próximo CSV verificado.
Em erro, execute `ROLLBACK`, investigue e repita; nunca ignore a transação abortada.

Mantenha os pontos e o volume pending intactos durante todo o procedimento. Uma falha entre
cópia e reconciliação é recuperável relendo o destino; uma falha SQL reverte a página inteira.

## Reconciliação global e retirada do schema

1. Releia todas as páginas e prove igualdade do conjunto de identidades e fingerprints da origem
   com o subconjunto correspondente do destino. Reconcilie `destino_depois = destino_antes +
   identidades_antes_ausentes`; nenhum outro escritor pode operar nesse intervalo. Confira cada
   faixa/batch dos registros de promoção, ExpectedPointCount, ConfirmedPointCount e pontos reais.
   Não marque como Completed uma promoção incompleta apenas para liberar a migração.
2. Exporte as linhas completas de promoção e seu relatório de verificação para arquivo protegido,
   com hash, contagens e referência dos backups. Somente após provar todas as faixas e preservar
   esse histórico, exclua explicitamente as linhas legadas de `pending_telemetry_promotions` numa
   transação administrativa. A migração recusa até linhas Completed não arquivadas; não as apaga
   silenciosamente. Não exclua guards nem evidências para contornar a trava.
3. Confirme zero linhas nas consultas abaixo. Se qualquer resultado for diferente de zero, pare:

```sql
SELECT count(*) FROM event_identity_guards
WHERE "Destination" <> 'Queryable' OR "StorageLocation" <> 'QueryableCommitted'
   OR "PromotionOwner" IS NOT NULL OR "PromotionLeaseUntil" IS NOT NULL;
SELECT count(*) FROM pending_telemetry_promotions;
```

4. Gere/revise o script EF e aplique a nova migração na conexão explicitamente selecionada:

```powershell
dotnet ef migrations script 20261014190000_PartitionIdentityGuard 20261014230000_SingleTelemetryStore --project src/CentralMobilidade.Telemetry.Infrastructure
# Use conexão administrativa via configuração protegida da design-time factory:
dotnet ef database update 20261014230000_SingleTelemetryStore --project src/CentralMobilidade.Telemetry.Infrastructure
```

   A factory usa `ConnectionStrings__TelemetryControl`; confira o destino antes de executar. A migração
   mantém PK, partições, índices, guards, evidências, donos, leases de processamento, fencing,
   retenção e proteção de truncate, e remove a tabela de promoção e quatro colunas obsoletas.
5. Verifique migrações aplicadas, contagens, replay/conflito, takeover, auditoria e impossibilidade
   de truncar guards/partições. Registre resultado real. Só retome a aplicação quando os gates
   de configuração/health/Compose T107–T112 estiverem completos. Retire serviços na T109; preserve
   o volume pending original para recuperação até encerramento formal da manutenção.

## Falhas e rollback

Conflito, evidência ausente, contagem divergente ou estado intermediário não comprovado bloqueiam
upgrade; não existe fallback que descarte telemetria aceita. A migração PostgreSQL é transacional.
O Down só aceita guard e evidência vazios, restaura o schema anterior para instalações vazias e
não reconstitui histórico arquivado nem desfaz escritas Influx. Para bases com dados, restaure
backups coordenados e a versão anterior após suspender novas escritas; não faça downgrade isolado.

Validação automatizada usa bancos temporários e evidências de pontos sintéticos. Ela prova os
checks de controle/migração; não substitui a leitura e reconciliação dos dois Influx reais. Nenhuma
reconciliação de ambiente produtivo é presumida pela ausência de containers locais.

## Aplicar a composição T109 após a reconciliação

Somente após os checks acima, pare os servidores legados antes de iniciar o serviço `influxdb`.
Preserve o mesmo nome de projeto Compose, o volume `influxdb_queryable_data` (nome físico padrão
`centralmobilidade_influxdb_queryable_data`), o mount `/var/lib/influxdb3`, o node-id `queryable`
e o arquivo de token queryable original. O novo nome do serviço não deve criar outro catálogo.
`influxdb-init` aponta para `http://influxdb:8181` e inicializa somente `telemetry`; exit 0 é esperado.
Atualize overrides externos e clientes que ainda usam o DNS antigo. O Compose novo não declara
nem monta o volume pending; ele permanece no Docker e deve ser preservado com seus backups/token.
Não execute remoção automática de volumes ou órfãos. A alteração dos YAMLs não reconcilia dados e
não autoriza reaplicar esta composição sobre uma instalação legada ainda não verificada.
