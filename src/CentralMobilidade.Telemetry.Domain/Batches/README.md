# Ciclo de vida de intake — T028

`IngestionBatch` é um agregado imutável e independente de infraestrutura. Recebe BatchId,
produtor e horário de recebimento; normaliza os horários para UTC e fixa a expiração
operacional em 30 dias. Identidade do produtor deve ser Unicode válido, sem controles,
não vazia e limitada a 128 caracteres e 256 bytes UTF-8.

Transições desta etapa:

```text
Receiving -> Publishing -> Accepted
    |             |
    v             v
Rejected      PublishFailed
```

`BeginPublishing` não define DurableAt. `ConfirmAcceptance` exige um
`DurableBatchConfirmation` com o mesmo BatchId; somente essa transição define DurableAt.
A aplicação deverá construir essa evidência depois do recibo real do provedor: o objeto
de domínio, por si só, não confirma publicação nem substitui publisher confirms.

Cada transição retorna uma nova instância com Version incrementada e preserva o estado
anterior. Repetir exatamente o mesmo evento (estado, horário e motivo/evidência) retorna
a instância atual sem incrementar a versão. Repetições conflitantes, transições fora
de fase e horários anteriores ao último evento são rejeitados sem mutação. Version
é uma versão de domínio; a comparação atômica na persistência pertence à T032.

Rejeições antes da publicação usam motivos tipados de validação, timeout, tamanho,
formato, backpressure ou cancelamento. Falha durante a publicação usa
DurabilityUnconfirmed ou RequestCancelled. PublishFailed não afirma que nenhuma
mensagem chegou ao broker; uma falha pode ser ambígua. Diagnósticos livres e exceções
de SDK não fazem parte do agregado. ToString não expõe o produtor.

Esta classe cobre apenas intake. Orquestração, mapeamento dos nomes de estado para
BatchLifecycle/entidades, códigos HTTP e restauração a partir do banco pertencem às
T032/T033; estados de processamento posteriores serão implementados nas tarefas de US2.

Validação: 23 testes em `IngestionBatchTests` passaram localmente e no Docker. A suíte
completa teve 258 aprovações e as mesmas 46 falhas TDD pendentes das T025–T027.
