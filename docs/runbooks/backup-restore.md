# Backup e restauração coordenados — T087

Este procedimento cobre a topologia local de um nó RabbitMQ, PostgreSQL 17 e
InfluxDB 3 Core com object store `file`. É uma manutenção **offline**, com janela
aprovada pelo responsável da instalação. Execute primeiro um ensaio isolado.
Identifique o contexto Docker e o projeto explicitamente; os exemplos não são
autorização para parar uma instalação em uso. Não use `down --volumes`, `prune`,
limpeza de diretórios ou restauração sobre volumes existentes.

## O que precisa pertencer ao mesmo conjunto

| Chave Compose | Conteúdo e identidade a preservar |
| --- | --- |
| `rabbitmq_data` | Todo `/var/lib/rabbitmq`, incluindo cookie Erlang, metadados e mensagens; hostname `rabbitmq`, nome do nó registrado na origem, imagem 4.3.6-management-alpine |
| `postgres_data` | Todo `/var/lib/postgresql/data`: banco, roles, WAL, guards, fences, estados, auditoria e evidências; imagem 17.11-bookworm, mesma plataforma |
| `influxdb_queryable_data` | Todo `/var/lib/influxdb3`, incluindo `data` e estado do nó `queryable`; imagem 3.11.6-core, banco `telemetry` |
| `quarantine_data` | Arquivos protegidos em `/quarantine/rejections`, com UID/GID 1654 e modo 0700 no diretório |
| `grafana_data`, `prometheus_data`, `loki_data` | Estado operacional persistente; copiar também neste procedimento integral |

Inclua os YAMLs/overrides realmente usados, commit e alterações locais revisadas,
configurações RabbitMQ/Loki/Prometheus/Grafana e IDs/digests das imagens. Mantenha
em custódia criptografada separada os arquivos `*_FILE` da T086, JSON do token
administrativo Influx existente, `.env`/`.env.worker`, credenciais de apresentação,
chaves de diagnóstico/quarentena, cursor, autenticação e demais segredos externos.
Um arquivo de configuração com caminhos de secrets não contém os secrets.
Consulte [hardening](../../deploy/hardening.md) e o
[contrato de configuração](../../deploy/README.md). Não regenere tokens/chaves no
restore; perder uma chave pode tornar a quarentena ilegível.

Preserve os identificadores de negócio, timestamps, leases, fences e deadlines.
Não combine o PostgreSQL de um horário com fila/Influx de outro: um guard antigo
pode deixar de proteger um ponto que já existe. Não marque reservas como concluídas
nem elimine mensagens para fazer contagens coincidirem. A aplicação tem recuperação
at-least-once; o conjunto frio não constitui uma transação distribuída.

## 1. Preparar e registrar o corte

Use PowerShell 7, Docker CLI/Compose e as imagens já verificadas. O operador precisa
ler os volumes e proteger o destino. Arquivos abaixo contêm dados sensíveis e
hashes de credenciais: destino privado, fora de Git, ACL restrita, criptografia e
cópia independente do host. Em Linux, o helper root pode criar arquivos 0600 de
propriedade root; use uma conta/ACL autorizada para calcular hashes e transportá-los.

Do repositório, ajuste os caminhos e acrescente **todos** os arquivos Compose
realmente usados. Não imprima o modelo resolvido, pois ele pode conter segredos.
Use um diretório novo sem vírgula no caminho (`--mount` usa vírgulas como separador).

```powershell
$ErrorActionPreference = 'Stop'
$Docker = (Get-Command docker -ErrorAction Stop).Source
$Project = 'centralmobilidade' # confirmar no contexto/instalação escolhidos
$EnvFile = (Resolve-Path .env).Path
$Compose = @('compose', '--project-name', $Project, '--env-file', $EnvFile,
    '-f', (Resolve-Path deploy/compose.yaml).Path)
# Acrescentar -f para overrides/monitoring quando usados na instalação.
$BackupDir = Join-Path (Get-Location) ('data/backups/' + [guid]::NewGuid().ToString('N'))
if ($BackupDir.Contains(',')) { throw 'Caminho incompatível com --mount.' }
New-Item -ItemType Directory -Path $BackupDir -ErrorAction Stop | Out-Null
if ($IsWindows) {
    $owner = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    & icacls $BackupDir /inheritance:r /grant:r "${owner}:(OI)(CI)F" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao proteger o diretório.' }
} else {
    & chmod 700 -- $BackupDir
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao proteger o diretório.' }
}
function D {
    & $Docker @args
    if ($LASTEXITCODE -ne 0) { throw 'Operação Docker falhou; interrompa o procedimento.' }
}
function DC {
    $commandArguments = @($Compose) + @($args)
    D @commandArguments
}
$Model = ((DC config --format json --no-env-resolution) -join "`n") | ConvertFrom-Json
$Keys = @('postgres_data','rabbitmq_data','influxdb_queryable_data',
    'quarantine_data','grafana_data','prometheus_data','loki_data')
$VolumeMap = [ordered]@{}
foreach ($key in $Keys) {
    $VolumeMap[$key] = $Model.volumes.PSObject.Properties[$key].Value.name
    if (-not $VolumeMap[$key]) { throw 'Volume ausente do modelo.' }
    $v = ((D volume inspect $VolumeMap[$key]) -join "`n") | ConvertFrom-Json
    if ($v[0].Labels.'com.docker.compose.project' -ne $Project) {
        throw 'Propriedade divergente; revisar mapeamento externo antes de continuar.'
    }
}
$RabbitNode = ((DC exec -T rabbitmq rabbitmqctl -q eval 'node().') -join '').Trim().Trim([char]39)
$ImageIds = [ordered]@{}
foreach ($service in @('postgres','rabbitmq','influxdb','grafana','prometheus','loki')) {
    $id = (DC ps -q $service) -join ''
    if (-not $id) { throw 'Serviço da origem ausente.' }
    $ImageIds[$service] = (D inspect --format '{{.Image}}' $id) -join ''
}
$CutStartedUtc = [DateTimeOffset]::UtcNow.ToString('o')
```

Volumes externos/customizados exigem mapeamento/ownership revisado, sem remover o
guard apenas para prosseguir. Registre também plataforma, versões, configurações,
último batch confirmado e contagens/estados de batches, guards e fila (ready,
unacked, retries, DLQ). Guarde amostras identificadas e contagens **privadamente**
para comparação posterior; não publique IDs, payload, coordenadas ou tokens.

1. Bloqueie novos envios no produtor/ingresso e aguarde requests/confirmações em
   curso; registre o último `202`. Pare a API e qualquer outro publicador externo.
2. Pare o worker graciosamente, dentro do prazo de drain configurado. Confira
   ausência de consumidores/publicadores externos e de entregas unacked; aguarde
   fechamento/requeue. Jobs de retention, migração e escrita direta também param.
   Se houver timeout, OOM, término forçado ou estado ambíguo, investigue antes do corte.
3. Com o broker ainda ativo e sem mutações, opcionalmente exporte definições:

```powershell
# Apenas se os serviços app estiverem presentes nos arquivos selecionados:
# DC --profile app stop --timeout 60 api
# DC --profile app stop --timeout 60 worker
$Definitions = (DC exec -T rabbitmq rabbitmqctl -q export_definitions -) -join "`n"
try { $null = $Definitions | ConvertFrom-Json } catch { throw 'Exportação de definições inválida.' }
[IO.File]::WriteAllText((Join-Path $BackupDir 'definitions.json'), $Definitions)
DC --profile '*' stop --timeout 60
DC --profile '*' ps -a
```

`definitions.json` contém topologia e dados de autenticação, **não mensagens**.
O backup integral do nó é a fonte de recuperação das mensagens; não importe
definições novamente sobre um nó restaurado integralmente. Confirme que os seis
servidores estão parados e tiveram desligamento limpo, sem código 137/OOM. Não
reinicie o daemon/host no meio da cópia. Pause schedulers externos que possam
reativar serviços; `unless-stopped` reinicia processos que saem espontaneamente.

## 2. Copiar volumes parados

O guard abaixo verifica existência e ausência de contêineres ativos em cada volume.
Ele não detecta processos externos ao Docker: a exclusividade do corte também é
responsabilidade do operador. O helper não tem rede; monta origem somente leitura.

```powershell
function Assert-OfflineVolume {
    param([string]$Volume)
    $null = D volume inspect $Volume
    $ids = @(D ps -aq --filter "volume=$Volume")
    foreach ($id in $ids) {
        $running = (D inspect --format '{{.State.Running}}' $id) -join ''
        if ($running -ne 'false') { throw 'Volume ainda associado a contêiner ativo.' }
    }
}
```

Este é o corpo shell do helper de backup, também executado pelo ensaio automatizado:

<!-- t087-backup -->
```sh
set -eu
umask 077
test ! -e "/backup/$1.tar"
tar -cpf "/backup/$1.tar" -C /data .
```

```powershell
$BackupBody = @'
set -eu
umask 077
test ! -e "/backup/$1.tar"
tar -cpf "/backup/$1.tar" -C /data .
'@
$Hashes = [ordered]@{}
if (Test-Path -LiteralPath (Join-Path $BackupDir 'definitions.json')) {
    $Hashes['definitions'] = (Get-FileHash -LiteralPath (Join-Path $BackupDir 'definitions.json') -Algorithm SHA256).Hash
}
foreach ($key in $Keys) {
    Assert-OfflineVolume $VolumeMap[$key]
    D run --rm --network none --read-only --user 0:0 --cap-drop ALL --cap-add DAC_OVERRIDE `
        --security-opt no-new-privileges:true `
        --mount "type=volume,source=$($VolumeMap[$key]),target=/data,readonly" `
        --mount "type=bind,source=$BackupDir,target=/backup" `
        alpine:3.23.4 sh -ec $BackupBody t087 $key
    $Hashes[$key] = (Get-FileHash -LiteralPath (Join-Path $BackupDir "$key.tar") -Algorithm SHA256).Hash
}
$Manifest = [ordered]@{
    schema = 1; sourceProject = $Project; startedUtc = $CutStartedUtc
    completedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    rabbitNode = $RabbitNode; influxNode = 'queryable'; postgresMajor = 17
    volumes = $VolumeMap; imageIds = $ImageIds; sha256 = $Hashes
}
$Manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $BackupDir 'manifest.json') -Encoding utf8NoBOM
```

Copie o diretório completo enquanto os serviços permanecem parados. Não copie
somente Parquet, tabelas PostgreSQL ou subpastas de mensagens. Neste caminho frio,
toda a árvore do Influx é capturada sem mutações, incluindo catálogo, WAL e
snapshots; não se aplica o procedimento de cópia online/ordenada de object stores
remotos. Não use `influx backup`/`influx restore` do InfluxDB 2 para Core 3.

`tar` preserva os metadados Unix usados pelas imagens fixadas; mounts customizados,
tablespaces, WAL externo, ACLs/xattrs ou outro object store exigem procedimento
específico. Não percorra um symlink externo como se seu destino estivesse incluído.
Proteja também `definitions.json`, manifest e configuração. Uma cópia incompleta
não recebe o status de backup válido. SHA-256 detecta alteração, não autentica a
origem: confira os hashes e um manifest autenticado/assinatura/custódia confiável
após transportar o conjunto criptografado para armazenamento independente.

Depois da cópia, a origem pode ser retomada conforme a seção de abertura abaixo,
mantendo o produtor bloqueado até readiness e reconciliação. A restauração de teste
deve usar rede e portas isoladas e não conectar produtores reais.

## 3. Restaurar exclusivamente em volumes novos

Recupere a configuração, imagens compatíveis e secrets da mesma instalação antes
de iniciar qualquer servidor. Para o primeiro restore, use a mesma imagem,
plataforma e formato; upgrade é outra operação. RabbitMQ deve iniciar com o mesmo
nome de nó registrado (`rabbit@rabbitmq` na composição padrão); preserve hostname,
cookie e metadados. Não renomeie o nó para contornar uma falha. Influx mantém
`--node-id queryable`, diretório e token existentes. Os defaults de senhas não
substituem credenciais já persistidas no PostgreSQL/RabbitMQ/Grafana.
Se a recuperação precisar funcionar sem registry/rede, mantenha também cópias
verificadas das imagens (`docker image save`/`docker image load`), inclusive as
imagens customizadas com plugins. Confirme os IDs/digests e arquitetura registrados,
sem substituir uma tag indisponível por `latest`.

Em outro projeto/contexto, carregue novamente as funções D/DC acima, selecione
arquivos e um `.env` privado de recuperação, ports loopback livres (ou `0`) e
construa o modelo do **destino** sem iniciar serviços. Não reutilize os nomes de
volumes da origem. Não carregue `.env.worker` real no ensaio; use um override
revisado com `env_file: !reset []` e credenciais de teste quando houver apps.

```powershell
$Docker = (Get-Command docker -ErrorAction Stop).Source
$ErrorActionPreference = 'Stop'
$EnvFile = (Resolve-Path .env.restore).Path # somente configuração privada do destino
$BackupDir = (Resolve-Path data/backups/conjunto-verificado).Path
$Keys = @('postgres_data','rabbitmq_data','influxdb_queryable_data',
    'quarantine_data','grafana_data','prometheus_data','loki_data')
$RestoreProject = 'restore-' + [guid]::NewGuid().ToString('N')
$Compose = @('compose','--project-name',$RestoreProject,'--env-file',$EnvFile,
    '-f',(Resolve-Path deploy/compose.yaml).Path)
# Acrescentar apenas overrides revisados do destino. Defina GRAFANA_PORT livre/0.
$TargetModel = ((DC config --format json --no-env-resolution) -join "`n") | ConvertFrom-Json
$Saved = Get-Content -LiteralPath (Join-Path $BackupDir 'manifest.json') -Raw | ConvertFrom-Json
if ($Saved.schema -ne 1 -or $Saved.influxNode -ne 'queryable' -or $Saved.postgresMajor -ne 17) {
    throw 'Manifest incompatível.'
}
if ($Saved.rabbitNode -ne 'rabbit@rabbitmq' -or $TargetModel.services.rabbitmq.hostname -ne 'rabbitmq' -or
    ($TargetModel.services.rabbitmq.environment.RABBITMQ_NODENAME -and
     $TargetModel.services.rabbitmq.environment.RABBITMQ_NODENAME -ne 'rabbit@rabbitmq')) {
    throw 'Identidade RabbitMQ customizada/divergente exige procedimento revisado.'
}
if (($TargetModel.services.influxdb.command -join ' ') -notmatch '--node-id queryable(?: |$)') {
    throw 'Node-id Influx divergente.'
}
foreach ($service in @('postgres','rabbitmq','influxdb','grafana','prometheus','loki')) {
    $image = $TargetModel.services.PSObject.Properties[$service].Value.image
    $imageId = (D image inspect --format '{{.Id}}' $image) -join ''
    if ($imageId -ne $Saved.imageIds.PSObject.Properties[$service].Value) { throw 'Imagem divergente.' }
}
# Validar TODO o conjunto e sua custódia antes de criar qualquer volume.
foreach ($key in $Keys) {
    $actual = (Get-FileHash -LiteralPath (Join-Path $BackupDir "$key.tar") -Algorithm SHA256).Hash
    if ($actual -ne $Saved.sha256.PSObject.Properties[$key].Value) { throw 'Checksum divergente.' }
}
if ($Saved.sha256.PSObject.Properties['definitions']) {
    $actual = (Get-FileHash -LiteralPath (Join-Path $BackupDir 'definitions.json') -Algorithm SHA256).Hash
    if ($actual -ne $Saved.sha256.definitions) { throw 'Checksum das definições divergente.' }
}
$TargetVolumes = [ordered]@{}
foreach ($key in $Keys) {
    $name = $TargetModel.volumes.PSObject.Properties[$key].Value.name
    if ($name -ne "${RestoreProject}_$key" -or $name -eq $Saved.volumes.PSObject.Properties[$key].Value) { throw 'Destino não isolado.' }
    & $Docker volume inspect $name *> $null
    if ($LASTEXITCODE -eq 0) { throw 'Volume de destino já existe; não sobrescrever.' }
    D volume create --label "com.docker.compose.project=$RestoreProject" `
        --label "com.docker.compose.volume=$key" $name | Out-Null
    $TargetVolumes[$key] = $name
}
```

Uma falha de `volume inspect` também pode significar Engine inacessível: confirme
o contexto/daemon antes da criação. Se houver qualquer falha parcial, preserve o
destino para diagnóstico; recomece com outro projeto/volumes novos. A ausência de
um volume de origem é uma falha, não um motivo para Docker criá-lo vazio.

<!-- t087-restore -->
```sh
set -eu
test -z "$(ls -A /data)"
tar -xpf "/backup/$1.tar" -C /data
```

```powershell
$RestoreBody = @'
set -eu
test -z "$(ls -A /data)"
tar -xpf "/backup/$1.tar" -C /data
'@
foreach ($key in $Keys) {
    Assert-OfflineVolume $TargetVolumes[$key]
    D run --rm --network none --read-only --user 0:0 --cap-drop ALL `
        --cap-add CHOWN --cap-add FOWNER --cap-add DAC_OVERRIDE --security-opt no-new-privileges:true `
        --mount "type=volume,source=$($TargetVolumes[$key]),target=/data" `
        --mount "type=bind,source=$BackupDir,target=/backup,readonly" `
        alpine:3.23.4 sh -ec $RestoreBody t087 $key
}
DC up -d --no-build --wait --wait-timeout 240 postgres rabbitmq influxdb
```

Restaure todos os volumes antes de `create`/`up`, evitando população automática
com arquivos da imagem. Revise UID/GID: PostgreSQL 999:999, RabbitMQ 100:101,
Influx 1500:1500, Grafana 472:0, Loki 10001:10001, Prometheus 65534:65534 e
quarentena 1654:1654/0700. Não corrija permissões com `chmod -R 777` nem execute
`chown -R` indiscriminadamente sobre dados existentes.

## 4. Verificar antes de reabrir

Mantenha produtor, API, worker, retention e consumidores externos parados até a
verificação dos stores. Não rode migrações destrutivas nem o inicializador como
substituto da inspeção: um banco recriado vazio pode ficar saudável.

| Camada | Critério verificável de recuperação |
| --- | --- |
| PostgreSQL | Conectar com a credencial recuperada; conferir versão/schema/migrações, roles e contagens/estados capturados de batches, guards e evidências; comparar identidades amostradas, não só `SELECT 1` |
| RabbitMQ | Comparar nó, vhost `telemetry`, usuários/permissões, exchanges/bindings/policies, filas principais/retry/DLQ, ready/unacked e amostras de MessageId/propriedades/payload; não consumir/ackar mensagens de produção para fazer um teste |
| InfluxDB | Consulta autenticada ao banco `telemetry`; comparar schema, identidades e valores/timestamps amostrados; dados podem existir em WAL ainda não convertidos em Parquet |
| Quarentena | Conferir bytes/hash, ownership/deadlines e descriptografia autorizada com a chave recuperada, sem imprimir conteúdo bruto |
| Operação | Conferir dados/configuração Grafana, Prometheus e Loki, credenciais e silenciamento dos alertas; health sozinho não prova recuperação dos dados |

TTL de mensagens e retenção continuam sujeitos ao tempo transcorrido. Não prometa
recuperar mensagens/diagnósticos já expirados nem estenda deadlines para esconder
perdas. Divergência, autenticação inválida, corrupção ou UID incompatível interrompe
a abertura; preserve evidência privada e a origem. Contagens isoladas não provam
conteúdo nem ausência de conflito.

Após os stores conferidos, execute os inicializadores idempotentes e serviços
operacionais com `DC up -d --no-build --wait --wait-timeout 240`. Quando os arquivos
app estiverem configurados, suba o worker, verifique heartbeat, consumer e readiness,
e só então a API. Confira `/health/ready` e `/health/dependencies` em ambos e
`/health/worker` no worker; liveness pode estar verde com dependência indisponível.
Reconcile batches em progresso com leases/fences preservados e acompanhe
reentregas, guards, rejeições, backlog e idade. Confira a limpeza pelos deadlines
originais antes de abrir acesso: rejeições/quarentena
vencidas não podem voltar a ficar disponíveis pela restauração. O conjunto e sua
política de descarte precisam respeitar os limites dos dados nele contidos.
Reabra o produtor gradualmente
depois de um batch canário autorizado atravessar gzip → confirmação → estado
terminal → consulta autenticada com os valores esperados, sem duplicatas lógicas.

Para rollback antes da reabertura, mantenha o destino bloqueado e retome a origem
intacta com sua configuração/secrets. Não execute simultaneamente consumidores
nos dois ambientes. Após aceitar novas escritas no destino, voltar ao snapshot
antigo exige outro corte e reconciliação dos novos batches; não é rollback sem perda.

## 5. Host físico, RPO/RTO e comprovação

Volumes nomeados, publisher confirms e três contêineres no mesmo host não protegem
contra perda do disco/VM/host. Uma cópia em `data/backups` ou no mesmo Docker Desktop
também compartilha esse risco. Proteção exige cópia criptografada independente,
custódia das chaves, retenção/acesso aprovados e restore testado em outro ambiente.
O emulador Pub/Sub não é backup durável nem réplica de recuperação.

O responsável deve registrar e aprovar frequência, janela de manutenção, retenção
dos backups, destino independente, responsáveis e RPO/RTO **medidos**. RPO depende
do último corte recuperável, não da data da última tentativa de cópia; dados aceitos
depois do corte podem se perder numa falha física. RTO inclui transferência,
restauração, validação e reconciliação. Esta tarefa não define nem certifica metas.
Backups também retêm dados sensíveis: inclua expiração, descarte seguro e restrição
de acesso na política, sem declarar conformidade legal.

```powershell
dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests --filter FullyQualifiedName~BackupRestoreTests
```

O ensaio automatizado usa imagens/Compose reais e somente projetos UUID: grava
marcadores, linha SQL, pontos autenticados e mensagem durável confirmada; para a
origem; arquiva os sete volumes com os corpos shell acima; confere checksums;
restaura volumes novos e compara dados, MessageId/propriedades e permissões.
Também rejeita corrupção e destino não vazio. O estado da origem é preservado
durante a comparação. Cleanup só remove recursos do ensaio com ownership conferido.
Os segredos sintéticos e arquivos tar permanecem em evidência privada ignorada.
Isso não comprova host-loss real, RPO/RTO, backup remoto criptografado, decrypt de
quarentena, todas as filas/casos de ack ou processamento de um canário completo.
Não confunda os testes anteriores de restart da T078/T086 com um restore de backup.

Referências técnicas: [RabbitMQ 4.3 — backup e identidade do nó](https://www.rabbitmq.com/docs/backup),
[PostgreSQL 17 — backup físico completo com servidor parado](https://www.postgresql.org/docs/17/backup-file.html),
[InfluxDB 3 Core — estado no object store e recuperação](https://docs.influxdata.com/influxdb3/core/admin/backup-restore/).
