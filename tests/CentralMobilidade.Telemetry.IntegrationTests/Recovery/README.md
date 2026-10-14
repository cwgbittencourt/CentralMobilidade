# Sobrevivência do lote após encerramento da API — T027

Testes de recuperação do worker e fronteiras de acknowledgement da T042 são descritos
em [WorkerAcknowledgementBoundaryTests.md](WorkerAcknowledgementBoundaryTests.md).
São cenários TDD distintos desta prova de encerramento real do processo da API.

```powershell
dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests --filter FullyQualifiedName~AcceptedBatchSurvivalTests
```

Execute no checkout da solução com a API compilada na mesma configuração dos testes
(Debug ou Release) e com `dotnet` disponível no PATH. As fixtures iniciam PostgreSQL
17.11 e RabbitMQ 4.3.6 via Testcontainers, ou usam os brokers exclusivos de validação
indicados por `TELEMETRY_TEST_POSTGRES` e `TELEMETRY_TEST_RABBITMQ`.

O teste principal inicia a DLL de produção da API em outro processo, com Kestrel em
uma porta local temporária. Configura uma credencial de produtor gerada para o teste,
um banco isolado migrado e nomes RabbitMQ `t027.<UUID>`. Credenciais são transmitidas
por variáveis de ambiente do filho, sem argumentos de linha de comando. A saída do
processo é drenada sem ser armazenada ou publicada.

Depois da liveness, o teste envia o gzip pelo POST real e exige 202, BatchAccepted e
Location coerentes. Somente depois de receber a resposta completa ele chama
`Process.Kill(entireProcessTree: true)`, aguarda a saída e exige código diferente de
zero. Esse encerramento não é um StopAsync nem uma parada graciosa.

Uma nova conexão com RabbitMQ deve encontrar exatamente o lote aceito na fila,
com mensagem persistente, bytes gzip originais, BatchId do recibo e metadados íntegros
(versão, produtor, trace, recebimento, tamanho e SHA-256). O teste libera a entrega
sem reconhecer, conecta outro consumidor, verifica redelivery e só então reconhece
a mensagem. Os recursos RabbitMQ exclusivos do teste são removidos no descarte;
volumes e bancos externos de validação são preservados.

O segundo teste comprova o mecanismo de crash: o próprio teste publica uma mensagem
confirmada no broker real, encerra a API real e recupera os bytes em outra conexão.
Essa prova do mecanismo não substitui a aceitação durável feita pelo endpoint.

## Estado TDD

Após a T035, os dois cenários passam contra PostgreSQL e RabbitMQ reais: a API só retorna
`202` após o publisher confirm, e o lote continua disponível depois do encerramento
abrupto do processo. Não há skips nem expectativas que aceitem falhas. Relatórios TRX
locais ficam em `artifacts/validation/T027/`, ignorado pelo Git.

O crash ocorre depois do recibo HTTP, que deve depender do publisher confirm. A janela
entre confirm e envio do recibo, reinício/perda do broker ou host, retomada do worker
e persistência no InfluxDB não são comprovados por este cenário.
