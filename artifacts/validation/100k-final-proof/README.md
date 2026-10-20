# Prova final de 100.000 eventos — T102

**Tentativa inicial de 2026-10-20 — BLOQUEADA no gate de readiness. T102 permanece `[ ]`.**
Foi executado um preflight real em uma nova fixture Docker. A CLI
`final-100k-grafana` **não foi invocada** e nenhum evento do corpus final foi
enviado. Não há reconciliação de 100.000 eventos, prova Grafana ou revisão de
renders aprovada nesta tentativa.

**Atualização T113 (2026-10-20):** o probe RabbitMQ foi implementado e um novo
preflight com imagens `:t113` retornou **live 200 / ready 200**, dois warmups
Completed e namespace final vazio. Run final privado `e455a7c50a8f42ce806a9f5e8cb6aac5`,
15:21:36–15:23:19 UTC−03:00, já com a correção do timeout de limpeza do canal.
Isso resolve o gate de readiness nessa fixture;
a T102 continua pendente de preparação Grafana e execução/revisão completa dos
100.000 eventos. O relato de falha abaixo é histórico e foi preservado.
Ver [implementação/limites de readiness](../../../docs/operations/publication-readiness.md).

## Origem, comando e ambiente

- Base: `26e18e943ab0f173cce4712a2e777e1a5759a6e0` (T101), com o novo modo de
  preflight no preparador. Nenhuma mudança em `src/` nesta tentativa.
- Projeto próprio: `cm-t102-456c4e106159`; run/namespace reservado:
  `456c4e106159428ba0ac4c0e96941d52`.
- Janela completa, incluindo preparação/cleanup: **14:50:35–14:52:23 UTC−03:00**
  (America/Sao_Paulo). O manifesto bruto registra timestamps ISO UTC.
- Comandos, da raiz do repositório:

  ```powershell
  py -3 -m py_compile scripts/Run-CapacityGates.py
  py -3 scripts/Run-CapacityGates.py --final-proof-preflight
  ```

- Sintaxe Python aprovada; preflight terminou com **exit 1**, por readiness 503,
  sem falha de preparação/exception registrada.
- Python **3.14.6**, .NET SDK **10.0.401**, Docker Engine **29.8.2** Linux x86_64,
  Compose **5.6.0**; VM Docker com 16 CPUs lógicas e 33.608.839.168 bytes de RAM.
- Stores: PostgreSQL `17.11-bookworm`, RabbitMQ `4.3.6-management-alpine`, InfluxDB
  `3.11.6-core`/file/database `telemetry`/60 dias; Prometheus `v3.15.0-busybox`,
  observador Alpine `3.23.4`. Grafana/renderer não foram iniciados.
- API `centralmobilidade-api:t094`, imagem
  `sha256:881a0c1ab82c02f1057711fd987b383be208c6eb7011a50c8e74fe9929dd5c06`;
  worker `centralmobilidade-worker:t094`, imagem
  `sha256:5362e95f5efb544b53f23d7e809df0faabbcf7eb1eb61d79ab33581c599d9004`.
  Imagens Release do source `520f8ed`; `git diff --stat 520f8ed HEAD -- src`
  confirmou ausência de alterações de produção até a base revisada.
- Quotas inspecionadas: API 2 CPUs/4 GiB, worker 6 CPUs/12 GiB. Dependências e
  coletor ficam fora desse orçamento. Isso não é medição de throughput/capacidade.
- Credenciais efêmeras, hashes de produtor e chave de cursor privados; migrations
  reais aplicadas em volumes novos. O uso de bootstrap dos stores não comprova
  hardening/privilégios mínimos de produção. Nenhum deployment existente foi usado.

## Resultados observados

| Verificação | Evidência direta | Resultado |
| --- | --- | --- |
| API `/health/live` | HTTP 200, `{"status":"healthy"}` | Processo ativo |
| API `/health/ready` | HTTP 503, `{"status":"unhealthy"}` | **FAIL**, bloqueia início do cenário T101 |
| PostgreSQL durável | `current_setting('fsync')` / `current_setting('synchronous_commit')` = `on/on` | Observado |
| Warmup real | Dois lotes gzip, fora do namespace final, chegaram a `Completed` | Aquecimento apenas; não é reconciliação do corpus |
| Namespace reservado no Influx | SQL independente abaixo retornou `n=0` | Vazio antes do cenário, não prova final |
| Envio dos 100.000 eventos / status terminais | `finalScenarioInvoked=false`, `finalEventsSubmitted=0` | **Não executado** |
| Reconciliação das 100.000 identidades/campos | Nenhum corpus submetido | **Não executada** |
| Grafana API health e isolamento interno/viewer | `grafanaProvisioned=false` | **Não executados** |
| Queries latest/trip/ingestion-total | Nenhuma query Grafana desta tentativa | **Não executadas** |
| Seis PNGs e revisão visual | Nenhum render desta tentativa | **Não executados** |
| Cleanup próprio | Zero containers remanescentes; quatro volumes preservados | Confirmado às 14:53:09 UTC−03:00 |

Consulta independente enviada pelo preparador à API SQL real do Influx:

```sql
SELECT count(*) AS n
FROM telemetria
WHERE empresa_id LIKE 'fp-456c4e106159428ba0ac4c0e96941d52-%'
```

Resultado: `[{"n":0}]`. Não interpretar esse zero como perda de eventos: os
100.000 eventos nunca foram enviados. Os dois pontos de aquecimento usam outro
prefixo e não foram contados como parte da prova.

## Causa e condição para retomar

A [T101](../../../tests/CentralMobilidade.Telemetry.LoadTests/Scenarios/Final100kGrafanaProofScenario.cs)
exige readiness 200 antes do count/preflight Grafana e de qualquer POST do corpus.
O problema já observado na [T098](../quickstart-results.md) persiste nesta fixture.
Inspeção atual de `src/` encontra a interface `IDurablePublicationProbe` e o health
check, mas não uma implementação/registro runtime. Sem o serviço,
`DurablePublicationHealthCheck` retorna Unhealthy. Veja os contratos de
[health](../../../src/CentralMobilidade.Telemetry.Infrastructure/Health/README.md) e
[seleção de provider](../../../src/CentralMobilidade.Telemetry.Infrastructure/DependencyInjection/README.md).

O aquecimento confirma fluxo básico real; não substitui o hook de capacidade da
rota de publicação durável. Não se trocou readiness por liveness, não se registrou
probe falso e não se modificou o runner T101 para contornar o gate.
Não houve correção de produto nesta tentativa.

Após a reprovação, novos cenários foram interrompidos conforme
[`speckit-implement`](../../../.agents/skills/speckit-implement/SKILL.md):
**“Halt execution if any non-parallel task fails”**. Somente documentação e
verificação de preservação/cleanup continuaram.

Para retomar:

1. Implementar/registrar o probe real na seleção da fila e verificar capacidade
   da rota, confirmação e falhas de conexão/permissão/topologia, sem transformar
   conectividade ou outra rota em prova de publicação. Testar a correção.
2. Revalidar readiness com imagens correspondentes ao novo source, sem usar as
   aliases antigas como se contivessem a correção.
3. Preparar toda a fixture T102 com Grafana/renderer reais, organização interna e
   viewer restrito à empresa 00; cadastro ausente/indisponível, namespaces novos e
   credenciais distintas. Seguir o [guia completo T101](../../../tests/CentralMobilidade.Telemetry.LoadTests/Scenarios/FINAL-100K.md).
4. Invocar `final-100k-grafana` e salvar manifest, recibos/status, contagem e páginas
   independentes, frames, isolamento, totais e seis renders. Revisar pixels/layout
   e registrar versões, fonte, configuração, horários e hashes dessa nova execução.
5. Só concluir a T102 com exatamente 100.000 eventos reconciliados e todos os
   caminhos Grafana aprovados. T094/capacidade, segurança, LGPD, T098 e readiness
   de implantação permanecem gates próprios, não são aprovados por uma prova funcional.

## Evidência privada e preservação

Raiz ignorada: `artifacts/validation/T102/456c4e106159428ba0ac4c0e96941d52/`.
`summary.json` contém timestamps, source hashes do preparador/cenários, engine,
imagens/quotas e os resultados acima. SHA-256 do arquivo bruto:
`2ac09bd6f771e6e2a3c47865f86927e6a7624fd16f57da8a2d6e255892d513da`.
Há também `cleanup.json`, scripts/migrations, métricas e logs de comandos privados.

Configurações e tokens podem estar nos arquivos brutos; não publicá-los.
`docker compose --project-name cm-t102-456c4e106159 -f <compose.json privado> down --timeout 60`
removeu somente containers/rede próprios, **sem `--volumes`**. Nenhum dado de usuário,
catálogo ou secret de uma instalação existente foi alterado. Apenas este README
revisado possui exceção no `.gitignore`; subdiretórios/evidências futuras continuam privados.

Não foram repetidos os testes de protocolo da T101 nem usados seus PNGs simulados
como evidência real. Esta entrega valida sintaxe, executa o gate Docker descrito,
confere referências e whitespace e registra a T102 ainda pendente.
