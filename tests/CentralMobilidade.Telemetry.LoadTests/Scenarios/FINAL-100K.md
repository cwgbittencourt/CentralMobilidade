# Prova final de 100.000 eventos — T101

O modo `final-100k-grafana` implementa a FR-033/SC-011 e o cenário final do
[quickstart](../../../specs/001-telemetry-ingestion/quickstart.md). A T101 entrega
o runner e controles automatizados de protocolo. **A execução real e a avaliação
das imagens/evidências são a T102**. Testes com HTTP simulado não são essa prova.
Este cenário funcional não mede 5.000 eventos/s, soak, cinco anos ou perda de host.

## Preparar uma instalação de teste exclusiva

Prepare API/worker, PostgreSQL, fila e um único Influx real com migrations,
credenciais e permissões reais, catálogo preservado e tabela `telemetria` já
existente (um warmup fora do namespace basta). O runner não cria serviços,
cadastros, datasources, dashboards nem tokens; não modifica/deleta stores ou volumes.
Não aponte para produção ou instalação compartilhada.

Para verificar primeiro a readiness em uma fixture nova com os stores reais:

```powershell
py -3 scripts/Run-CapacityGates.py --final-proof-preflight
```

O comando usa as imagens locais `centralmobilidade-api:t094` e
`centralmobilidade-worker:t094`; confirme que correspondem à fonte candidata.
Ele aplica migrations, envia dois warmups fora do namespace final, registra health,
durabilidade PostgreSQL e count SQL, depois remove só seus containers/rede sem
apagar volumes. Saída privada em `artifacts/validation/T102/<uuid>/summary.json`.
Exit 1 indica readiness reprovada/incompletude; exit 0 aprova **somente esse gate**.
O modo não provisiona Grafana, não invoca a CLI final nem envia o corpus de 100.000
eventos. O [registro T102](../../../artifacts/validation/100k-final-proof/README.md)
separa o resultado desse gate das etapas de aceitação ainda não executadas.

Desative/deixe indisponível o lookup cadastral de empresas na fixture. Todos os
IDs `fp-<namespace>-00` a `-09` devem ser novos e ausentes de cadastros. O pipeline
deve aceitá-los com a mesma credencial de produtor sem cadastrar/allowlistar empresas.
`IsolatedTarget` e `CompanyLookupUnavailable` são declarações obrigatórias do
preparador, **não descoberta automática nem prova de indisponibilidade cadastral**.
Registre a configuração efetiva sem segredos na evidência operacional da T102.

Provisionar os dashboards de produção T071–T074 em Grafana real com renderer:

- Organização interna 1, datasource Influx direto e os três dashboards internos.
- Organização viewer >1, somente Infinity, credencial de apresentação limitada
  **exatamente à empresa `fp-<namespace>-00`**, sem Influx nem privilégios humanos.
- Service accounts Grafana distintos e limitados às respectivas organizações,
  com leitura de dashboards/datasources e permissão de consulta/render.
  Não fornecer bootstrap/server-admin ao runner. A credencial Infinity/API
  permanece provisionada no datasource e é diferente dos tokens Grafana.
- A viewer API deve aplicar seus limites reais; latest usa limit=500, trip e
  health até 10.000. Não substituir queries/dados por um backend de teste na T102.

Gere o namespace **antes** de provisionar o escopo do viewer e use os mesmos
valores na configuração final. Não é permitido reutilizar namespace com eventos:
o count independente anterior precisa ser zero. Uma tentativa parcial exige
namespace/diretório novos e nova configuração de escopo; preserve os dados anteriores.

## Configuração privada e execução

Use origens HTTP(S) sem usuário/senha, paths, query ou fragmento. HTTP somente
no desenvolvimento confiável. `EventStart` é UTC, precisão de milissegundos,
há menos de 12 horas e com pelo menos 2.000 segundos até o presente; uma hora
atrás atende. O diretório de evidências precisa ser absoluto, novo ou vazio.
Deadline de 1 a 3.600 segundos cobre preflight, envio, reconciliação, Grafana e renders.

Exemplo de criação de **configuração sem segredos**, para adaptar às portas da fixture:

```powershell
$namespace = [guid]::NewGuid().ToString('N')
$eventStart = [DateTimeOffset]::FromUnixTimeMilliseconds([DateTimeOffset]::UtcNow.AddHours(-1).ToUnixTimeMilliseconds())
$configuration = @{
    Api = 'http://127.0.0.1:8080/'
    Influx = 'http://127.0.0.1:8181/'
    Grafana = 'http://127.0.0.1:3000/'
    Namespace = $namespace
    EventStart = $eventStart.ToString('o')
    IsolatedTarget = $true
    CompanyLookupUnavailable = $true
    InternalOrganizationId = 1
    ViewerOrganizationId = 2
    DeadlineSeconds = 1800
    PollMilliseconds = 500
    ReportDirectory = [IO.Path]::GetFullPath("artifacts/validation/100k-final-proof/$namespace")
}
# Provisionar primeiro o viewer para "fp-$namespace-00" na fixture.
New-Item -ItemType Directory -Path data -Force | Out-Null
$configuration | ConvertTo-Json | Set-Content data/final-100k.json -Encoding utf8NoBOM
```

Injete no processo, a partir de fontes privadas, quatro credenciais **distintas**:

| Variável | Uso |
| --- | --- |
| `TELEMETRY_LOAD_PRODUCER_TOKEN` | POST gzip e status dos próprios lotes |
| `TELEMETRY_PROOF_INFLUX_TOKEN` | Leitura SQL independente da única database `telemetry` |
| `TELEMETRY_PROOF_INTERNAL_GRAFANA_TOKEN` | Service account da organização interna |
| `TELEMETRY_PROOF_VIEWER_GRAFANA_TOKEN` | Service account da organização viewer |

Não colocar valores em argumentos, transcript ou JSON versionado. A CLI desabilita
redirects/cookies para evitar repassar credenciais a outro destino. Chamadas diretas
a `RunAsync` devem fornecer `HttpClient` com essas mesmas restrições.

```powershell
dotnet run --project tests/CentralMobilidade.Telemetry.LoadTests -- --scenario final-100k-grafana --config data/final-100k.json
```

Exit 0 = todos os checks do runner passaram; 1 = tentativa falhou/incompleta com
`report.json`; 2 = configuração/startup inválidos ou impossível gravar evidência.
O runner não imprime respostas de providers, parâmetros privados nem exceções brutas.
Limpe as variáveis de segredos do processo ao terminar. Preserve o projeto/volumes
de teste e siga o teardown explícito do preparador, sem `--volumes`.

## Corpus e critérios de aprovação

O corpus é determinístico por namespace + EventStart, sem consulta cadastral nem
PII real: **10 empresas ×100 veículos ×100 pontos =100.000 identidades**. Cada
veículo emite a cada 20 segundos, durante 1.980 segundos de intervalo. O veículo
`proof-trip`, da empresa 00, é a viagem nomeada. A chegada envia amostras 50–99
em ordem e depois 0–49 em ordem: metade atrasada, sem alterar event-time.

O runner envia exatamente 200 lotes gzip de 500 linhas, um por vez, sem repostar
POST ambíguo. Mantém apenas um corpo comprimido por vez, 200 recibos e um conjunto
limitado a 100.000 identidades durante a reconciliação. Respostas têm teto de 2 MiB.
O uso desses metadados de teste não é um padrão de buffering para a API de produção.

1. Exigir API ready 200, Grafana com database saudável, organizações e tipos de
   datasource corretos, e SQL count zero para todo o namespace (sem filtro de tempo).
   A readiness 503 observada na T098 continua bloqueando uma execução real: não
   substituir por liveness nem registrar probe fictício.
2. Exigir `202`, UUIDs distintos, estado accepted, `receivedAt` UTC e statusUrl/Location
   relativos exatos; não seguir URL arbitrária de recibo. Cada lote precisa terminar
   completed com total/accepted=500, rejected/replayed=0. Failed, expired, rejeição,
   identidade divergente e timeout/cancelamento reprovam.
3. Consultar Influx SQL independentemente dos status: exatamente 100.000 pontos,
   paginação em blocos de 1.000, unicidade de cada identidade e comparação de todos
   os campos sintéticos, coordenadas, RPM/ignição e timestamp. Count repetido após
   a leitura detecta divergência adicional; total sozinho não mascara duplicata/missing.
4. Carregar os dashboards reais pelo UID. Conferir query SQL/URL contra os templates
   do repositório e interpolar suas variáveis, sem trocar a query por uma prova ad hoc.
   Executar `/api/ds/query` com tokens/orgs separados. Latest verifica 100 posições
   mais recentes por empresa (10 internas + empresa 00 viewer); trip verifica os
   100 pontos em ordem, coordenadas/campos e timestamps pelos dois caminhos.
5. Health interno soma os pontos por event-time de cada empresa até **100.000**.
   Health viewer soma accepted=10.000/rejected=0 da empresa 00 por processing-time;
   não é a mesma semântica temporal. A janela inclui evento e processamento.
   Alterar empresa no latest viewer para 01 deve produzir upstream 404 e zero dados.
   Erro, frames vazios, valores divergentes ou ausência de isolamento reprovam.
6. Exigir seis respostas PNG válidas (latest/trip/health, interno/viewer) junto dos
   resultados de query conferidos. A assinatura PNG não interpreta pixels: T102
   deve revisar visualmente renders para confirmar layout/conteúdo e ausência de
   mensagens de erro; não concluir que qualquer PNG é uma dashboard válida.

## Evidências e limites

`manifest.json` registra versão do gerador, configuração não secreta e corpus;
`report.json` registra timestamps, stage, códigos seguros de falha, totais, recibos,
hash SHA-256 do Line Protocol concatenado na ordem de chegada e hash da configuração.
`CapacityApproved` permanece false. Também são gravados status terminais, páginas
SQL/counts, saúde/tipos de fonte Grafana, frames, teste de escopo, totais e seis PNGs.
Somente a conclusão integral pode produzir Passed=true; falha/cancelamento conserva
evidência parcial, sem limpar o ambiente nem transformar pendências em perda provada.

O diretório é privado/ignorado. Frames e PNGs contêm coordenadas sintéticas e
configurações podem identificar origens; revisar/sanitizar antes de publicar o
README da T102. Anexar nessa execução versões, imagens/digests, fonte/configuração,
prepared registry-unavailable state, comandos, horários e review visual.

Controles da T101:

```powershell
dotnet test tests/CentralMobilidade.Telemetry.LoadTests --filter 'FullyQualifiedName~Final100kGrafanaProofScenarioTests|FullyQualifiedName~FinalProofProtocolTests'
```

Esses testes verificam corpus, gzip/cultura e protocolo positivo/negativo com HTTP
simulado (incluindo PNG sintético). Não iniciam Docker/Grafana nem aprovam T102,
T094, T098, segurança ou implantação.
