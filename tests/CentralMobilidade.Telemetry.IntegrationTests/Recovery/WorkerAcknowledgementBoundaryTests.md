# Fronteiras de recuperação do worker — T042

## Atualização T051

O handler e a gravação transacional de resultados por linha estão implementados.
As 21 falhas TDD de processamento passaram, junto com os sete cenários anteriores
do guard. Foram acrescentadas provas de contadores exatos após retomada, rejeição
parcial sem commit indevido, cancelamento, hash/trailer gzip, renovação/perda de lease,
replay concorrente de resultados e limites dos buffers originais/canônicos.
`ProcessingRealStorageTests` combina PostgreSQL, InfluxDB e filesystem reais e verifica
o estado terminal antes da confirmação de uma entrega controlada e de sua redelivery.

Os dados de falha injetada abaixo continuam válidos; encerramento real de processo,
consumo RabbitMQ e política de poison não são demonstrados aqui. Configuração e
fluxo atual estão em [Processing](../../../src/CentralMobilidade.Telemetry.Application/Processing/README.md).
As seções que mencionam tipos ausentes registram a origem TDD anterior à T051.

Validação final em Linux Docker: integração funcional 282/282, unitários 242/242,
contratos 58/58 e arquitetura 47/47, sem skips. Depois do prazo próprio de processamento,
a seleção dirigida final passou 41/41, incluindo o novo timeout (283 casos de integração
distintos; a suíte inteira não foi repetida após essa mudança localizada). As 21 falhas
TDD originais estão resolvidas. O benchmark de capacidade ADR-001 foi excluído da seleção
funcional e permanece um gate separado.

No Windows, 56 falhas da execução ampla foram bloqueios `0x800711C7` da DLL da API;
um novo caso de gzip revelou `InvalidDataException` não tratada, corrigida como tentativa
não confirmada. A DLL da Application também foi bloqueada numa rechecagem; nenhuma
política de segurança foi alterada. As execuções finais Linux passaram com fontes atuais.
Relatórios privados ignorados: `artifacts/validation/T051/final-integration.trx`,
`final-processing.trx`, `final-unit.trx`, `final-contract.trx`, `final-architecture.trx`.

```powershell
dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests --filter FullyQualifiedName~WorkerAcknowledgementBoundaryTests
```

28 cenários executáveis após T104/T110, sem skips ou guard/processador substitutos. Reflexão mantém
a solução compilável antes das T047/T051. Os testes localizam o guard real da
Infrastructure e o handler real da Application antes de iniciar recursos de teste;
ausência de uma implementação é uma falha explícita, não sucesso.

## Guard PostgreSQL: sete cenários

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

## Processamento/ack: 21 cenários

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

Os outros cenários verificam:

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

## Limites da prova e histórico TDD

Atualização T047: os seis cenários exclusivos do guard passaram com PostgreSQL real.
Os 19 cenários do handler continuam aguardando T051 e suas dependências. A T110 substitui a antiga cobertura de promoção por upgrades e recuperação no schema único;
as verificações do schema legado ficam restritas aos testes da migração T106.
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

## Adequação T110 — destino único

A suíte mantém os 13 pontos de falha e todas as asserções de ack, resultados, quarentena e
imutabilidade. Os dois casos T104 de empresas ausentes do cadastro se somam aos 19 casos T042
aguardando o handler da T051. Não há skips, handler alternativo nem expectativa que aceite a ausência
da implementação. A T110 não implementa T050/T051 e não demonstra processamento ponta a ponta.

O novo sétimo cenário do guard usa o PostgreSQL real: duas empresas não cadastradas têm o mesmo
veículo e timestamp, recebem reservas independentes e fazem commit. Uma nova instância do adapter
com a mesma factory de persistência deve classificar replay/conflito sem reabrir nenhuma identidade.
Isso prova reinstanciação do adapter, não crash de processo ou reinício do servidor PostgreSQL.

`SelectedEventIdentityGuardTests` cobre seis upgrades populados: schema inicial e particionado,
cada um em Reserved, Writing e Committed. Preserva owner, fence, lease, prazos e fingerprint;
retoma o dono incompleto, confirma o guard, mantém replay/conflito e bloqueia truncate da partição.
Os campos legados aparecem apenas no seed SQL anterior à migração. A medição de capacidade T047
permanece no código e é excluída desta validação funcional; o déficit registrado no ADR-001 não é resolvido.

`Persistence/SingleTelemetryStoreMigrationTests` mantém a instalação nova, reconciliação idempotente,
recusas por pending/promoções não reconciliadas, conflitos/evidência ausente, rollback e privilégios.
São verificações do upgrade legado: não reintroduzem promoção na aplicação. As provas de pontos
Influx usadas pela reconciliação são sintéticas e não substituem o inventário dos catálogos reais.
`ControlPersistenceTests` exige as sete entidades atuais, suas chaves e privacidade, sem colunas
Destination/StorageLocation/PromotionOwner/PromotionLeaseUntil. Os testes unitários dos ports e
contratos de batch Accepted já atendem ao modelo único e são mantidos.

Comando de validação T110 (exclui apenas a medição de capacidade):

```powershell
dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests/CentralMobilidade.Telemetry.IntegrationTests.csproj --no-build --no-restore --filter "(FullyQualifiedName~.Persistence.|FullyQualifiedName~SelectedEventIdentityGuardTests|FullyQualifiedName~WorkerAcknowledgementBoundaryTests)&FullyQualifiedName!~Measure_actual_per_identity_adapter" --logger "trx;LogFileName=t110-recovery-migration.trx" --results-directory artifacts/validation/T110
```

Execução inicial: 71 casos, 49 aprovações, 21 falhas esperadas exclusivamente pela ausência de T051,
e uma expectativa obsoleta de oito entidades no modelo. Os seis upgrades novos e o cenário entre
empresas passaram. A correção da expectativa verifica explicitamente as sete entidades atuais,
preservando chaves, privacidade e invariantes; sua revalidação isolada é registrada separadamente.
Não se afirma que a execução inicial ficou verde. Unitários 242/242, contratos 58/58 e arquitetura
47/47 passaram. Os TRX estão em `artifacts/validation/T110/` (ignorados pelo Git).

Revalidação final: os três `ControlPersistenceTests` passaram 3/3 em `t110-model-final.trx`, incluindo
a expectativa corrigida. São 50 casos distintos de integração validados com sucesso considerando
as duas execuções; os 21 de T051 continuam vermelhos pelo motivo registrado. A seleção completa
não foi repetida após essa correção localizada. Build final da solução: zero avisos e zero erros.
Nenhum código de produção, migração aplicada, volume ou dado de implantação foi alterado.
