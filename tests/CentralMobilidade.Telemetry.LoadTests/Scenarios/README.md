# Steady state — T091

O runner `steady-5000-eps` envia 500 eventos por gzip a cada 100 ms, durante 600 s
por padrão. São 100 empresas sintéticas e 100.000 veículos, um evento por veículo
a cada 20 s; cada execução usa namespace novo para não contar replay como carga.
Não consulta cadastro de empresas. O gerador mantém apenas payloads em voo e
metadados limitados pelo máximo de uma hora de execução (36.000 lotes).

O relógio é aberto: não aguarda processamento para agendar o próximo POST.
Concorrência de POSTs é limitada (16 por padrão). Slot atrasado em 100 ms ou falta
de vaga reprova o cenário, sem rajada de catch-up. HTTP 429/503, timeout e resposta
ambígua também reprovam; não há retry automático de POST com novo batch ID.

## Preparar o alvo de referência

Use uma instalação de teste descartável, com autenticação e armazenamento reais,
sem tráfego de outros testes. **Não apontar para produção.** Reserve o gerador
fora do orçamento do alvo e registre hardware, versões/digests, disco, configuração
de worker/filas e limites de CPU/memória em manifesto privado da execução.

O perfil de referência é 8 vCPUs e 16 GiB disponíveis à aplicação. O Compose
`compose.override.yaml` de desenvolvimento usa API 1 CPU/1 GiB e worker 2 CPU/2 GiB;
não representa esse perfil. Prepare um override de carga revisado, por exemplo
API 2 CPUs/4 GiB e worker 6 CPUs/12 GiB, além de orçamento separado para dependências
e gerador. Confirme os limites efetivos no runtime antes do teste. Nenhum override
ou deployment é aplicado automaticamente por este runner.

O Prometheus precisa enxergar recursos de API + worker por container/cgroup.
As métricas atuais de host não isolam consumo da aplicação; não as use como se
fossem CPU/RAM de API+worker. Provisione coleta/recording rules apropriadas ou
forneça consultas equivalentes verificadas. Também é necessário medir disco dos
stores/volumes e idade real do backlog. O histograma de idade de **entregas
observadas** não é a idade da mensagem mais antiga parada. Sem fonte confiável,
o gate permanece sem evidência, e não deve ser preenchido com zero.

Prepare scrape e aquecimento fora da janela medida para que histogramas de escrita
tenham amostras. Cada consulta abaixo precisa produzir **um único vetor agregado**
finito, não negativo e com timestamp de avaliação recente. A API usada é a
[HTTP API oficial do Prometheus](https://prometheus.io/docs/prometheus/latest/querying/api/).
O timestamp da avaliação não prova frescor dos scrapes de origem: as consultas
devem filtrar targets DOWN/séries antigas e preservar ausência como ausência.
Não use constantes, `or vector(0)` ou agregações de alvos parciais para ocultar falha.
Valide escopo e frescor das recording rules com os scrapes do ambiente real.

| Chave | Semântica exigida |
| --- | --- |
| referenceCpuCores | Limite efetivo agregado de API+worker; deve ser 8 |
| referenceMemoryBytes | Limite efetivo agregado de API+worker; deve ser 16 × 1024³ |
| cpuPercent | CPU agregada de API+worker / 8 cores × 100; cada amostra deve ser <=85 |
| memoryBytes | Memória agregada de API+worker, mesma definição em toda a execução; cada amostra <=12 GiB |
| diskUsedBytes | Ocupação dos volumes relevantes, sem contar mounts repetidos; registrar baseline, série e delta |
| queueMessages | Backlog da fila principal, incluindo ready e unacked, escopo isolado |
| queueOldestAgeSeconds | Idade real do backlog; zero somente quando a fonte comprova fila vazia |
| writeP50Seconds / writeP95Seconds / writeP99Seconds | Quantis do histograma de escrita do worker, agregando buckets antes do quantil, com janela documentada |

## Configuração e execução

Crie JSON privado fora do Git, com caminhos absolutos. Os nomes `load_*` abaixo
são **contrato de recording rules a provisionar**, não métricas já instaladas.
Substitua por consultas reais revisadas. A execução falha antes de enviar carga
quando consultas estão vazias, inválidas, ausentes, multissérie, negativas ou NaN,
ou quando o perfil/limites iniciais não correspondem ao gate.

```json
{
  "Api": "http://localhost:8080/",
  "Prometheus": "http://localhost:9090/",
  "ReportDirectory": "C:/Users/lucas/source/repos/CentralMobilidade/artifacts/validation/T091/real-run",
  "DurationSeconds": 600,
  "MaxConcurrentPosts": 16,
  "DrainSeconds": 300,
  "Queries": {
    "referenceCpuCores": "load_reference_cpu_cores",
    "referenceMemoryBytes": "load_reference_memory_bytes",
    "cpuPercent": "load_application_cpu_percent",
    "memoryBytes": "load_application_memory_bytes",
    "diskUsedBytes": "load_storage_used_bytes",
    "queueMessages": "load_queue_messages",
    "queueOldestAgeSeconds": "load_queue_oldest_age_seconds",
    "writeP50Seconds": "load_write_p50_seconds",
    "writeP95Seconds": "load_write_p95_seconds",
    "writeP99Seconds": "load_write_p99_seconds"
  }
}
```

Forneça `TELEMETRY_LOAD_PRODUCER_TOKEN` por ambiente protegido do processo, sem
colocar o segredo no JSON, argumentos ou histórico. O token autoriza POST e status
dos próprios lotes; não é enviado ao Prometheus. Endpoints devem ser origens HTTP(S)
sem credentials, path, query ou fragmento. TLS/canal privado e ACL do diretório de
relatório ficam sob responsabilidade da instalação. O runner não relaxa certificados.

```powershell
dotnet run --project tests/CentralMobilidade.Telemetry.LoadTests -- --scenario steady-5000-eps --config C:/caminho-privado/steady.json
```

Exit code: 0 para gates do cenário aprovados, 1 para execução medida reprovada,
2 para configuração/preflight/execução que não produziu resultado completo. Ctrl+C
cancela, aguarda POSTs em voo e tenta salvar relatório sem imprimir conteúdo privado.
Falha anterior ao início da carga pode não produzir JSON; nunca equivale a PASS.
Não remova volumes ou lotes automaticamente ao terminar.

## Relatório e limites da evidência

O JSON `steady-<run>.json` registra horário, duração/configuração (SHA-256), taxa
alvo e realizada, IDs dos lotes confirmados, eventos agendados/tentados/confirmados/reconciliados, bytes gzip
e descomprimidos tentados por segundo (não goodput confirmado), p50/p95/p99 de intake, amostras de CPU/RAM/disco/backlog
e quantis de escrita. Coleta ocorre antes, durante carga/drain e no final.
Falta de monitoramento ou excesso dos limites reprova. Em execução de referência,
a taxa confirmada incluindo conclusão dos POSTs precisa ser >=4.950 eventos/s
(tolerância explícita de 1% de borda, oferta continua 5.000/s, nenhum slot pode
faltar); o relatório sempre mostra a taxa real. Faltam limites universais
de disco/backlog/write latency, por isso suas séries são evidência para análise.

Após envio, o runner consulta cada batch confirmado com concorrência limitada até
o prazo de drain. Exige `completed`, 500 accepted, total 500, zero rejected/replayed.
Isso reconcilia estado durável da aplicação, não consulta independente de pontos
no Influx. Lote ausente/não terminal ou divergente reprova. Os percentis de
`TerminalObservationMilliseconds` são limites superiores observados após o envio,
incluem a espera por polling e **não provam queryability nem o SLO de cinco minutos**.
Essa prova e a contagem independente de Influx continuam gates T094/T101–T102.

Execução curta serve só como smoke: `ReferenceCapacityRun=false` quando duração
é menor que 600 s. Mesmo `true` identifica o perfil/duração pretendidos, não uma
certificação de capacidade sem revisão das fontes, manifesto e evidências reais.
Os testes de `SteadyStateScenarioTests` usam transporte simulado para testar
protocolo, geração, percentis e falhas. Não executam a carga contra Docker e não
substituem o ensaio T094. Rode-os separadamente do benchmark PostgreSQL existente:

```powershell
dotnet test tests/CentralMobilidade.Telemetry.LoadTests --filter FullyQualifiedName~SteadyStateScenarioTests
```

Os cenários `reconnect-burst` e `influx-outage` estão descritos abaixo. Nenhum resultado de 5.000
eventos/s no hardware de referência é alegado pela entrega da T091.

## Reconnection burst — T092

```powershell
dotnet run --project tests/CentralMobilidade.Telemetry.LoadTests -- --scenario reconnect-burst --config C:/caminho-privado/steady.json
```

Usa o mesmo JSON privado, token, monitoramento e perfil de referência da T091.
`DurationSeconds` precisa ser **600** nesse modo: representa a janela de burst,
não a duração total. O runner acrescenta 1.200 segundos de recuperação com carga
normal ativa; depois há até `DrainSeconds` (máximo 300) para reconciliar o restante
da carga normal. Essa cauda não estende o deadline dos lotes do burst.

Nos primeiros dez minutos, cada slot de 100 ms contém 500 eventos normais e 300
atrasados, no mesmo gzip de 800 linhas. São 8.000 eventos/s oferecidos nesse trecho:
5.000 normais/s mais 3.000 atrasados/s. O componente offline
tem 10.000 veículos distintos, 180 amostras por veículo, a cada 20 segundos, cobrindo
de uma hora antes do início até 20 segundos antes dele, em ordem cronológica.
Não há sobreposição com os IDs dos 100.000 veículos normais ou com outra execução.
São **1.800.000 eventos atrasados**, sem depender de um cadastro de empresas.

Depois do minuto dez, os slots continuam com 500 eventos normais a cada 100 ms
por mais vinte minutos. Isso soma 9.000.000 eventos normais e 1.800.000 atrasados,
**10.800.000 no total**, em 18.000 lotes (6.000 mistos e 12.000 normais).
O gerador e a concorrência continuam limitados; não armazena o corpus inteiro.

Um observador concorrente passa a consultar os lotes mistos ao terminar o burst,
enquanto os POSTs normais continuam. A coorte a drenar inclui **todas** as linhas
desses lotes, inclusive as normais. Exige estado `completed`, contagens exatas,
zero rejeições/replays e todos os recibos distintos reconciliados até o instante
monotônico de término do burst +1.200 s. As consultas usam cancelamento nesse
deadline. Uma conclusão observada apenas depois dele reprova, mesmo que o banco
possa ter concluído antes; não se infere drain de uma fila aparentemente vazia.

`Reconnection` no relatório `reconnect-<run>.json` separa eventos offline agendados,
confirmados e drenados no prazo, veículos/amostras, janelas e tempo observado após
o burst. Tempo de drain é limite superior observado por polling, não instante
exato de persistência. Todos os lotes normais confirmados também precisam ser
reconciliados na cauda final. Série de backlog/idade e métricas da T091 permanecem
no relatório; não se declara que toda a fila está vazia enquanto tráfego continua.

Este perfil escolhe uma aceitação **mais estrita** que a alternativa de backpressure
da SC-003: qualquer 429/503 é registrado e reprova a execução, sem retry automático.
Isso evita aprovar um ensaio que ofereceu o corpus mas não o aceitou integralmente;
não classifica uma resposta explícita de backpressure como perda de lote confirmado.
Reprova também slots perdidos, recibo repetido, ambiguidade, falha de monitoramento,
divergência/perda de resultado ou prazo excedido. Nenhum dado existente é apagado.

Os testes usam um perfil **interno** reduzido, indisponível no CLI, com burst de um
segundo e recuperação de um segundo para exercitar o deadline real do runner.
O relatório desse perfil sempre tem `ReferenceCapacityRun=false`. A distribuição
completa dos 1,8 milhão de eventos é verificada separadamente, incluindo 180 amostras
por veículo e os limites dos timestamps. Execute a regressão dos dois cenários:

```powershell
dotnet test tests/CentralMobilidade.Telemetry.LoadTests --filter 'FullyQualifiedName~ReconnectionBurstScenarioTests|FullyQualifiedName~SteadyStateScenarioTests'
```

A T092 implementa o cenário e as asserções de reconciliação/prazo; não executa
o ensaio de capacidade T094. Estados de batch não substituem uma contagem e consulta
independentes de pontos no Influx, nem provam sobrevivência a perda física de host.
Essas evidências continuam obrigatórias nos gates de capacidade/aceitação final.

## Influx outage e projeção de sete dias — T093

O modo `influx-outage` usa a geração de 5.000 eventos/s da T091. Injeta falha de
conectividade **somente** por um proxy Toxiproxy já existente e exclusivo da
instalação de teste; não para containers, altera volumes ou executa comandos shell.
Prepare a instalação descartável com todos os acessos de escrita do worker ao
Influx passando pelo proxy. Verifique essa rota com um canário antes do ensaio.
Não use produção ou uma instalação compartilhada; `IsolatedTarget=true` registra
essa escolha explícita e não detecta isolamento físico automaticamente.

Provisione e fixe a versão do Toxiproxy no manifesto privado. O runner usa a
[API oficial de proxy](https://github.com/Shopify/toxiproxy#http-api): consulta o
nome/listen/upstream exatos e altera apenas `enabled`, verificando o estado depois.
Não cria/deleta proxy, muda upstream, manipula toxics ou chama reset global.
O estado inicial precisa ser enabled e a fila de teste precisa estar vazia.
Mantenha controle exclusivo da fixture durante o teste. O endereço de controle
deve ser uma origem loopback, com encaminhamento local protegido quando necessário.
Se a fixture exigir autenticação, forneça `TELEMETRY_LOAD_FAULT_TOKEN` por ambiente
protegido; ele é distinto do token do produtor e só vai ao controle do proxy.

Além das dez consultas da T091, configure quatro consultas agregadas e verificadas:

| Chave | Medida necessária |
| --- | --- |
| queuePayloadBytes | Bytes de corpo gzip retidos na fila do ensaio, incluindo ready/unacked, sem somar o mesmo corpo duas vezes |
| queueDiskUsedBytes | Ocupação em disco do broker/volume dedicado, com baseline; memória residente não equivale a bytes persistidos |
| storageWriteFailures | Contador de tentativas de escrita falhas do worker, mesmo escopo antes/durante/depois |
| storageWriteConfirmations | Contador de confirmações de escrita do worker, nunca um contador de envios HTTP ou de mensagens ACKadas |

Consultas devem preservar ausência, identidade de instância e resets; scrape
precisa ter tempo de registrar cada fase. Use outage maior que scrape + timeout
de escrita + backoff. Prepare aquecimento antes e use janela de quantis de escrita
que mantenha amostras durante a ausência esperada de novas escritas; NaN não vira
zero. Os nomes de recording rules deste exemplo são contratos a provisionar, como
na T091. O runner não instala essas fontes. Um endpoint direto ao Influx que
ignore o proxy invalida o experimento, mesmo que a API de controle responda bem.

Exemplo de JSON privado, com endpoints e nomes revisados para a fixture:

```json
{
  "Load": {
    "Api": "http://localhost:8080/",
    "Prometheus": "http://localhost:9090/",
    "ReportDirectory": "C:/Users/lucas/source/repos/CentralMobilidade/artifacts/validation/T093/real-run",
    "DurationSeconds": 600,
    "MaxConcurrentPosts": 16,
    "DrainSeconds": 300,
    "Queries": {
      "referenceCpuCores": "load_reference_cpu_cores",
      "referenceMemoryBytes": "load_reference_memory_bytes",
      "cpuPercent": "load_application_cpu_percent",
      "memoryBytes": "load_application_memory_bytes",
      "diskUsedBytes": "load_storage_used_bytes",
      "queueMessages": "load_queue_messages",
      "queueOldestAgeSeconds": "load_queue_oldest_age_seconds",
      "writeP50Seconds": "load_write_p50_seconds",
      "writeP95Seconds": "load_write_p95_seconds",
      "writeP99Seconds": "load_write_p99_seconds",
      "queuePayloadBytes": "load_queue_payload_bytes",
      "queueDiskUsedBytes": "load_queue_disk_used_bytes",
      "storageWriteFailures": "load_storage_write_failures_total",
      "storageWriteConfirmations": "load_storage_write_confirmations_total"
    }
  },
  "FaultControl": "http://127.0.0.1:8474/",
  "ProxyName": "centralmobilidade_load_influx",
  "ExpectedListen": "[::]:8182",
  "ExpectedUpstream": "influxdb:8181",
  "IsolatedTarget": true,
  "OutageSeconds": 120,
  "RecoverySeconds": 600
}
```

```powershell
dotnet run --project tests/CentralMobilidade.Telemetry.LoadTests -- --scenario influx-outage --config C:/caminho-privado/outage.json
```

Os 120 segundos de indisponibilidade são seguidos por 600 segundos de recuperação
com carga normal ativa. A duração efetiva de geração substitui `Load.DurationSeconds`
pela soma dessas duas fases (720 por padrão). O tempo real de indisponibilidade
inclui chamadas/preflight e é registrado separadamente. No final, o runner usa até
`Load.DrainSeconds` para reconciliar todos os lotes. Outage aceita 1–600 s e
recuperação 1–1.800 s; ensaios menores que 120/600 são smoke, sem perfil de referência.
Revise a política de retries/MaxAttempts para a janela escolhida: mandar dados
para DLQ durante a falha não conta como recuperação bem-sucedida.

Para PASS, durante a falha backlog em mensagens **e** bytes deve aumentar, o contador
de falhas de escrita deve crescer e o de confirmações permanecer no baseline.
Depois da restauração, confirmações precisam voltar, a fila retornar ao baseline
em mensagens/bytes e todos os `202` reconciliar em lotes completos com as contagens
exatas. Métricas ausentes, resets/divergência, falha de controle/restauração,
rejeição/perda/não conclusão de lote ou qualquer falha da T091 reprovam. Como os
estados são observados no fim, a latência é limite superior; não comprova por si
só presença independente de cada ponto no Influx.

O `finally` tenta reabilitar o proxy mesmo após erro ambíguo de disable ou Ctrl+C,
com deadline próprio de 30 s; nunca herda o token cancelado da carga. Se não conseguir
confirmar a restauração, `ProxyRestored=false` e `proxy_restore_unconfirmed` ficam
no relatório `outage-<run>.json`. Intervenção do operador continua necessária nesse
caso; a aplicação não garante recuperação de um controlador inacessível. Falhas de
preflight anteriores à mutação podem não gerar relatório. O relatório `steady-*`
associado mantém recibos, percentis e séries de recursos, sem tokens ou payloads.

### Extrapolação, sem alegação de retenção comprovada

A 5.000 eventos/s, sete dias equivalem a **3.024.000.000 eventos** ou **6.048.000
mensagens de 500 eventos**. O relatório projeta bytes de payload pelo gzip/evento
medido; projeta disco apenas quando existem crescimento positivo e mensagens
adicionais observadas. Sem isso, disco fica `null`, não zero. Segmentos, metadados,
réplicas, outros stores, índices/guards PostgreSQL, logs, backups e margem precisam
de avaliação separada. O corpus sintético simples não representa automaticamente
a compressão/entropia da frota real; T094/T095 precisam de medições representativas.

Uma coorte totalmente reconciliada após falha comprovada fornece um limite inferior
de throughput de recuperação: eventos concluídos / tempo observado após restaurar.
Com entrada normal mantida, o modelo de catch-up usa `backlog / (throughput - 5000)`.
Se throughput não superar 5.000, ou o ensaio falhar, não se afirma capacidade líquida
de recuperação: tempo fica `null`, com `catch_up_not_demonstrated`. Mesmo um valor
calculável é `estimate_only`, sujeito a I/O, CPU, espaço, retries, aging e carga real.

Backlog de sete dias e retenção de sete dias não deixam mais sete dias para drenar:
a mensagem mais antiga já está no limite de expiração. Compare idade real, prazo
restante e plano de recuperação **antes** dessa fronteira; não renove timestamps/TTL
para esconder perda. Este cenário curto não testa espera de sete dias, sobrevivência
a reinício/perda de host, saturação real do disco ou restauro do broker.

Os 18 testes novos usam transporte simulado e janelas de um segundo, incluindo
cancelamento, upstream divergente, efeito da falha ausente, perda de lote, falha de
restauração e resposta ambígua ao disable. Execute a regressão dos três cenários:

```powershell
dotnet test tests/CentralMobilidade.Telemetry.LoadTests --filter 'FullyQualifiedName~InfluxOutageScenarioTests|FullyQualifiedName~ReconnectionBurstScenarioTests|FullyQualifiedName~SteadyStateScenarioTests'
```

A execução real contra fixture privada e os gates de capacidade permanecem na T094;
a T093 não altera nem interrompe um deployment existente durante sua validação.
