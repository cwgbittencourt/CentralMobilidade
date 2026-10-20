# Reconciliação de contratos — T099

Data: 2026-10-20, America/Sao_Paulo. Base de produção: commit `51ee5bd`.
**Reconciliação concluída no escopo abaixo: quatro lacunas de documentação
corrigidas, 925 testes aprovados, zero falhas/skips nas execuções finais.**
Isso não é validação formal de todos os inputs de OpenAPI/JSON Schema, prova de
capacidade, execução do quickstart completo ou aprovação de release.

## Método e artefatos

Comparação manual das oito operações de negócio com os endpoints/handlers,
serialização DTO, autenticação, projeções e códigos estáveis. O YAML é o contrato
autoral; a API atual não publica um documento OpenAPI gerado para comparação
automática. A presença da dependência OpenAPI no projeto não demonstra esse endpoint.
Health/metrics não fazem parte das oito operações descritas neste contrato.

O [OpenAPI](../../specs/001-telemetry-ingestion/contracts/openapi.yaml) é carregado
pelo reader existente com referências locais e diagnóstico sem erros. O
[schema de envelope](../../specs/001-telemetry-ingestion/contracts/queue-envelope.schema.json)
é confrontado com metadados CLR/serialização, constantes e limites; os adapters
reais são exercitados com RabbitMQ e emulador Pub/Sub. O
[Line Protocol](../../specs/001-telemetry-ingestion/contracts/line-protocol-v1.md)
é confrontado com parser/leitor e suíte unitária, inclusive seu exemplo publicado.
Não foi adicionado outro parser de produção nem biblioteca de validação genérica.

Hashes SHA-256 dos contratos finais:

| Artefato | SHA-256 |
| --- | --- |
| openapi.yaml | `4f6e93c67533b6658ec3f25770963b05b0572630829bdeac2ce2cd3d7109695a` |
| queue-envelope.schema.json | `b743b28c7909cc451c9563b7d8aa31a10216e1142fa47672de28cdfc63bb3ef3` |
| line-protocol-v1.md | `87d9a77b82f31896d107885be68d425c2b2290685cf78b67da6ed7fdeee3887a` |

## Diferenças reconciliadas

| ID | Antes | Resultado |
| --- | --- | --- |
| D-01 | Listagem administrativa de rejeições tratava erro de provider como `500 INTERNAL_ERROR`, ausente no YAML | Adicionada resposta `500` com schema `Problem`; teste da lista exata de outcomes atualizado |
| D-02 | Leitura bruta de quarentena tratava erro de provider antes do início da resposta como `500`, ausente no YAML | Adicionada resposta `500`; descrição distingue falha anterior à resposta de interrupção após iniciar o stream |
| D-03 | `403` de motorista tinha só descrição, embora runtime retorne `application/problem+json` | Adicionado schema `Problem` ao response body |
| D-04 | Schema de envelope aceitava o UUID zero, que `TelemetryBatchEnvelope.Validate()` rejeita | Acrescentado pattern de exclusão do UUID zero; `format: uuid` continua necessário para a sintaxe |

Oito novos casos em
[AuthoredContractConformanceTests](../../tests/CentralMobilidade.Telemetry.ContractTests/Api/V1/AuthoredContractConformanceTests.cs)
conectam documentos e implementação: dois outcomes administrativos, um body de
permissão, propriedades/required/constantes e serialização do envelope, dois
limites de tamanho, UUID vazio e exemplo Line Protocol. A primeira execução
produziu **4 falhas esperadas / 4 aprovações**; após os ajustes, os oito passaram.

Também foram explicitados: encoding de timestamp dos providers, limites de IDs
na ingestão versus apresentação, tipos/normalizações do parser e separação entre
aceitação gzip e validação assíncrona. Não foram alterados código de produção,
nomes de campos, tipos de payload, regras de acesso, esquemas de banco ou valores
já publicados. A versão autoral `1.0.0-draft` e as versões de protocolo `v1`/`1`
permanecem; são correções de documentação de comportamento existente, sem migração.

## Matriz HTTP

| Método/operação | Comparação e evidência executada | Resultado/limite |
| --- | --- | --- |
| POST `/api/v1/telemetry/batches` | [Endpoint](../../src/CentralMobilidade.Telemetry.Api/Endpoints/V1/IngestTelemetryBatchEndpoint.cs), IngestBatchContractTests, IntakeBoundaryTests e BoundedRequestSpoolTests | Headers versão/gzip/UTF-8, recibo `202`, Location/Retry-After, limite/timeout/backpressure e falha de confirmação cobertos; `202` não significa processamento concluído |
| GET `/api/v1/telemetry/batches/{batchId}` | [Endpoint](../../src/CentralMobilidade.Telemetry.Api/Endpoints/V1/GetBatchStatusEndpoint.cs), BatchStatusContractTests, ApiDtoSerializationTests e BatchStatusEndpointTests | DTO/estados/counters, produtor dono versus operador humano, escopo e não divulgação por `404` |
| GET `/api/v1/telemetry/batches/{batchId}/rejections` | [Endpoint](../../src/CentralMobilidade.Telemetry.Api/Endpoints/V1/ListBatchRejectionsEndpoint.cs), BatchInspectionTests e contrato autoral | Paginação 50/200, cursor protegido e contexto autenticado, permissão/escopo e metadados sem conteúdo bruto; `500` reconciliado por inspeção do catch e teste do documento |
| GET `/api/v1/telemetry/batches/{batchId}/rejections/{quarantineId}/content` | [Endpoint](../../src/CentralMobilidade.Telemetry.Api/Endpoints/V1/GetQuarantinedRejectionContentEndpoint.cs), BatchInspectionTests e contrato autoral | Permissão própria, auditoria antes de abrir o conteúdo, falha fechada `503`, missing/expired/denied, descarte do stream; `500` reconciliado por inspeção do catch e teste do documento |
| GET `/api/v1/presentation/drivers/{driverId}` | [Endpoints](../../src/CentralMobilidade.Telemetry.Api/Endpoints/V1/Presentation/PresentationEndpoints.cs), PresentationAuthorizationTests, apresentação unitária e DTO | Escopo, `driver.read`, minimização, auditoria, revogação, problemas seguros; body `403` reconciliado |
| GET `/api/v1/presentation/telemetry/latest` | Mesmos endpoints/testes de apresentação | Escopo server-side, array DTO e cursor em `X-Next-Cursor`; teto padrão 500 |
| GET `/api/v1/presentation/telemetry/trips/{vehicleId}` | Mesmos endpoints/testes de apresentação | Um veículo, UTC, ordenação e limites padrão 24 horas/10.000 pontos |
| GET `/api/v1/presentation/telemetry/ingestion-health` | Mesmos endpoints/testes de apresentação | Empresa autorizada, janela até sete dias, bucket mínimo 60 segundos e teto padrão 10.000 pontos |

Os novos testes de `500` verificam o contrato corrigido; não são injeções novas
dessas falhas no HTTP. A resposta/branch foi revista no código e as suítes existentes
executadas. Não se afirma cobertura de toda exceção possível ou de todas as
combinações de estado/credencial/entrada. O Problem customizado da aplicação não
implica que um proxy, servidor ou stream já iniciado sempre consiga devolver JSON.

## Envelope e transporte de fila

| Aspecto | Resultado verificado |
| --- | --- |
| Metadados | Nove membros JSON obrigatórios e `contractVersion` opcional; dez propriedades totais, sem corpo/Base64 no DTO; esquema fechado na representação JSON |
| Valores | `schemaVersion=1`, `contentType=text/plain; charset=utf-8`, `contentEncoding=gzip`, `contractVersion=v1` quando presente; tamanho 1–8.388.608, digest SHA-256 lowercase; UUID não vazio |
| Timestamp | Schema JSON declara date-time; publishers emitem UTC round-trip `.NET O`, sete casas fracionárias, e consumers exigem esse encoding. Uma string RFC 3339 arbitrária não é automaticamente válida no header do provider |
| RabbitMQ | Corpo nativo gzip, propriedades/header de identidade/correlação, persistência/confirm, hash/tamanho, consumo manual, retry, redelivery, settlement e dead-letter exercitados pelas suítes selecionadas |
| Pub/Sub | Mesmo contrato reutilizável sobre adapter real e emulador; `Data` contém gzip nativo, attributes são strings, inclusive `compressedBytes`; não confundir o tipo JSON lógico com o tipo físico do attribute |
| Extensões de transporte | `deliveryAttempt`/informação de provider não são novas propriedades do modelo JSON. Consumers projetam os metadados conhecidos e controlam tentativa separadamente; o JSON Schema não é um validador da tabela completa de headers AMQP/attributes |

A [suíte reutilizável](../../tests/CentralMobilidade.Telemetry.ContractTests/Queue/DurableBatchQueueContractTests.cs)
compara inspeção nativa e adapter, evitando validar apenas os bytes esperados
reconstruídos pelo teste. Formatos de GUID aceitos pelo codec RabbitMQ podem ser
mais amplos que Pub/Sub; os publishers usam UUID em formato `D` comum aos dois.
`Validate()` rejeita strings obrigatórias em branco além dos limites estruturais
do schema. Serializar/deserializar isoladamente não substitui validar envelope,
gzip, tamanho/hash e coerência das propriedades do provider.

O emulador não demonstra IAM, retenção gerenciada, HA ou sobrevivência de sete dias
no serviço cloud. Este relatório confirma paridade nos cenários executados,
sem transformar quiet windows de testes em garantia de entrega exatamente uma vez.

## Line Protocol e limites de compatibilidade

Parser de produção: [TelemetryLineProtocolV1Parser](../../src/CentralMobilidade.Telemetry.Application/LineProtocol/TelemetryLineProtocolV1Parser.cs).
Golden cases, streaming/propriedades e testes de identidade na suíte unitária
verificam escapes, UTF-8, colunas/tipos fechados, campos obrigatórios, fronteiras,
ordem/canonicalização, replay e fingerprint. O exemplo literal do Markdown é
carregado pelo novo teste e aceito pelo parser real.

IDs válidos de empresas sem cadastro permanecem aceitos; não há renomeação de
`empresa_id`, lookup de existência ou store de pendentes de empresa. Placa é
normalizada em uppercase no evento antes do fingerprint. Ignition é lowercase
`true`/`false`; RPM é Int64 não negativo com `i`; floats são finitos. Timestamp
é Unix ms e deve caber na conversão Int64 de nanosegundos do storage.

Limitações preservadas e explicitadas, sem aprovação de mudança funcional:

- Ingestão aceita IDs até 256 bytes UTF-8, enquanto a apresentação limita também
  a 128 code units UTF-16. Portanto alguns IDs válidos na ingestão não são
  selecionáveis nas consultas atuais. Isso é uma limitação funcional existente,
  não evidência de consulta universal de todos os IDs aceitos.
- `maxLength` JSON Schema conta caracteres Unicode; o runtime usa limites UTF-16
  e UTF-8 adicionais. Descrições indicam a restrição, mas não há prova de
  equivalência mecânica para todo Unicode ou todos os parâmetros.
- O schema de metadados não expressa todas as verificações dinâmicas dos adapters
  (hash/corpo, relógio futuro, gzip e propriedades nativas), nem o OpenAPI expressa
  sozinho todas as regras de autorização, cursor e limites configuráveis.
- Não foi executado um validador completo JSON Schema Draft 2020-12 sobre todo
  request/response nem um diff OpenAPI gerado. Os testes novos verificam somente
  as partes enumeradas, somadas à revisão e aos cenários de runtime existentes.

Mudanças futuras nesses limites ou semânticas precisam de revisão de contrato,
testes e decisão de compatibilidade/versionamento; não aumentar limites ou relaxar
validação só para tornar as especificações superficialmente iguais.

## Execução e reprodução

Ambiente: Windows, SDK .NET 10.0.401, dependências fixadas no repositório;
integrações usam Testcontainers, portas/recursos isolados e imagens declaradas nas
fixtures. Sem acesso a deployment/dados de produção. Relatórios brutos ignorados
em `artifacts/validation/T099/raw/`; somente este Markdown é versionado.

| Execução | Passou | Falhou | Ignorados |
| --- | ---: | ---: | ---: |
| Contratos finais (`contracts-final.trx`) | 154 | 0 | 0 |
| Unitários completos (`unit.trx`) | 380 | 0 | 0 |
| Integrações selecionadas (`integration.trx`) | 391 | 0 | 0 |

Integração real: 2026-10-20 10:42:25–10:43:15 (-03:00). São 117 BatchInspection,
19 BatchStatusEndpoint, 36 IntakeBoundary, 123 PresentationAuthorization,
14 PubSubAdapterSafety, 22 PubSubEmulator, 19 RabbitMqConsumerContract,
12 RabbitMqPublisherContract, 16 RabbitMqReusableQueueContract e 13
BoundedRequestSpool. Build final: zero avisos/erros; formatação C#, referências
locais e whitespace conferidos. As suítes unitária/integrada foram executadas
antes das correções exclusivamente documentais; a suíte de contratos foi repetida
após elas. Nenhum código de produção foi alterado.

```powershell
dotnet build CentralMobilidade.slnx --no-restore
dotnet test tests/CentralMobilidade.Telemetry.ContractTests --no-build
dotnet test tests/CentralMobilidade.Telemetry.UnitTests --no-build
dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests --no-build --filter 'FullyQualifiedName~IntakeBoundaryTests|FullyQualifiedName~BatchStatusEndpointTests|FullyQualifiedName~BatchInspectionTests|FullyQualifiedName~PresentationAuthorizationTests|FullyQualifiedName~RabbitMqReusableQueueContractTests|FullyQualifiedName~PubSubEmulatorTests|FullyQualifiedName~RabbitMqPublisherContractTests|FullyQualifiedName~RabbitMqConsumerContractTests|FullyQualifiedName~PubSubAdapterSafetyTests|FullyQualifiedName~BoundedRequestSpoolTests'
```

Use `--logger 'trx;LogFileName=...' --results-directory artifacts/validation/T099/raw`
para salvar evidência. Não foram repetidas a suíte funcional inteira, carga/soak,
Grafana final 100k, scanners ou quickstart completo. T094/T095/T098/T100–T103 e
pendências de segurança/LGPD mantêm seus próprios gates. T099 registra compatibilidade
no escopo exercitado e limitações residuais; não autoriza produção.
