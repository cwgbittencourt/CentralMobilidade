# Fronteiras de recuperação do worker — T042

```powershell
dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests --filter FullyQualifiedName~WorkerAcknowledgementBoundaryTests
```

25 cenários executáveis, sem skips ou guard/processador substitutos. Reflexão mantém
a solução compilável antes das T047/T051. Os testes localizam o guard real da
Infrastructure e o handler real da Application antes de iniciar recursos de teste;
ausência de uma implementação é uma falha explícita, não sucesso.

## Guard PostgreSQL: seis cenários

`MigratedPostgresFixture` fornece banco migrado exclusivo e durável. O guard é criado
por `ActivatorUtilities` com a factory EF, clock, configuração e logging.

- 16 reservas concorrentes de uma identidade: exatamente uma recebe lease. Após
  commit, replays iguais são classificados como idênticos e diferentes como conflito.
- Retomada do dono em Reserved e Writing mantém fence; outro dono é bloqueado antes
  da expiração. 16 tentativas de takeover depois da expiração produzem um sucessor;
  renew/mark/commit do lease antigo são rejeitados.
- Evidência removida após 30 dias: tanto um replay previamente idêntico quanto um
  conflito passa a `AlreadyExistsUnclassified`; o guard committed permanece existente.
- O mesmo dono não pode alterar o fingerprint ao retomar uma reserva incompleta.

Não reaproveita o SQL simplificado do benchmark T041: exige `SelectedEventIdentityGuard`
implementar o contrato de produção `IEventIdentityGuard` sobre o schema atual.

## Processamento/ack: 19 cenários

A ponte propõe `ProcessTelemetryBatchHandler.HandleAsync(IQueueDelivery, CancellationToken)`
retornando Task ou ValueTask. Cada entrega/retry cria um novo handler e colaboradores
transientes, preservando apenas os estados externos: guard/batch reais no PostgreSQL
e ledgers controlados de telemetria/quarentena. O parser e o batcher reais são registrados
quando disponíveis. A T051 pode substituir a ponte por chamadas tipadas preservando
os mesmos comportamentos. A delegação efetiva de ack pelo consumer RabbitMQ ficará
na T052/T053; nenhum consumer alternativo é implementado aqui.

13 pontos de falha: antes/depois da reserva; antes/depois de Writing; antes/depois
da persistência Influx; antes/depois do commit do guard; antes/depois dos resultados
por linha; antes/depois do estado terminal; antes do ack. A primeira tentativa deve
atingir o ponto escolhido e não completar a entrega. Uma nova entrega do mesmo BatchId
deve terminar com um evento lógico, conteúdo original intacto, batch terminal e ack
depois dos resultados. Após escrita ambígua anterior ao guard commit, chamadas físicas
idênticas podem se repetir; após committed, exige uma só chamada física.

Os outros seis cenários verificam:

- Persistência bloqueada não autoriza ack nem guard commit antes da liberação.
- Falha antes/depois de gravar quarentena em lote misto exige recuperação sem duplicar
  a rejeição, preservando um evento válido e um item protegido (dois cenários).
- Dois workers processando conteúdos diferentes da mesma identidade não podem
  substituir o primeiro evento; eventual redelivery do concorrente gera rejeição.
- Após 31 dias e remoção da evidência, payload idêntico ou diferente é rejeitado
  sem nova escrita e sem prometer classificação idêntica (dois cenários).

O writer controlado preserva o primeiro conteúdo por identidade e conta chamadas
físicas; adulteração é detectada separadamente, portanto deduplicação do ledger não
mascara um handler que tente reescrever conteúdo diferente. Quarentena também é
replay-safe por ID, exigindo recuperação sem criar outro item. Desde T105 não existe
resolver de visibilidade no contrato; os cenários T104 exercitam configuração cadastral
legada ausente ou indisponível sem introduzir um resolver substituto.

## Limites da prova e resultado TDD

Atualização T047: os seis cenários exclusivos do guard passaram com PostgreSQL real.
Os 19 cenários do handler continuam aguardando T051 e suas dependências. Há mais
três testes T047 para promoção com fencing, migração com dados e medição do adapter.
A suíte completa passou de 130 para 139 aprovações e de 51 para 45 falhas previstas
(15 T038, 11 T039, 19 T042). O desempenho medido do adapter ainda não atende à meta;
consulte a revalidação T047 no ADR-001.

As falhas injetadas são exceções ambíguas nos ports antes/depois de ações persistidas,
não encerramento real de processo nem falhas do disco. Não provam WAL/restart do
Influx, entrega real de RabbitMQ ou filesystem real de quarentena; esses adapters
têm os testes específicos T038/T039 e recuperação operacional futura. A persistência
de guard e batch é real quando seus tipos de produção estiverem disponíveis.

Na T042, seis testes falham pela ausência da T047 e 19 pela ausência da T051. As
asserções de recuperação ainda não foram demonstradas verdes. Integração completa:
130 aprovações e 51 falhas previstas (15 T038 + 11 T039 + 25 T042), sem novas falhas
nos testes anteriores. Compilação e formatação passam. Relatórios locais ignorados:
`artifacts/validation/T042/T042.trx` e `artifacts/validation/T042/integration.trx`.
