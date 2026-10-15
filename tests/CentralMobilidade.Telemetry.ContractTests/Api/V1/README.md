# Contrato HTTP de ingestão (T024)

`IngestBatchContractTests` lê o OpenAPI 3.1 de
`specs/001-telemetry-ingestion/contracts/openapi.yaml` com o leitor Microsoft OpenAPI YAML
2.12.0. O build copia esse arquivo para o diretório dos testes, evitando dependência do
diretório de trabalho e preservando a resolução das referências locais do contrato.

```powershell
dotnet test tests/CentralMobilidade.Telemetry.ContractTests --filter FullyQualifiedName~IngestBatchContractTests
```

Os 18 casos verificam o POST versionado, autenticação exclusiva de produtor, os headers
obrigatórios de versão e gzip, traceparent opcional, corpo binário text/plain com charset
UTF-8 documentado, e respostas 202, 400, 401, 408, 413, 415, 429 e 503. Também verificam
Location, Retry-After, desafio Bearer, formato fechado de Problem Details, códigos do catálogo
HTTP e compatibilidade de BatchAccepted com o schema publicado.

Esses testes verificam o documento de contrato e os DTOs; não iniciam a API nem publicam
mensagens. A execução HTTP, os limites de upload, a publicação durável e a impossibilidade
de responder 202 antes do publisher confirm serão comprovadas nas tarefas seguintes de US1.
A rota de ingestão ainda será implementada na T034 e conectada na T035.

Na validação inicial, 12 casos falharam por lacunas no documento; após completar os headers
e os schemas de erro, os 18 passaram no Docker Linux. A execução local da DLL de testes
foi bloqueada pelo Controle de Aplicativo do Windows; a compilação local passou.

## Consulta de lotes e quarentena (T054)

`BatchStatusContractTests` acrescenta 22 casos sobre os três GETs de US3. Verifica
status com produtor proprietário ou operador, permissão batch.read e escopo de todas
as empresas ou cross_company.read explícito, rejeições exclusivas de operador,
paginação opcional com padrão 50 e limite 200, estados públicos, contadores,
timestamps e metadados permitidos. Falhas de visibilidade usam 404 com o mesmo schema
Problem de recurso ausente; credencial ausente, inválida, desabilitada ou de tipo
incorreto usa 401 com desafio Bearer.

A consulta de conteúdo bruto exige UUID de quarentena associado ao lote, credencial
de operador e quarantine.raw.read. O contrato exige auditoria durável antes de liberar
conteúdo, incluindo tentativas bem-sucedidas, negadas, ausentes e expiradas. Documenta
200 text/plain, 401/403/404 Problem Details e 503 AUDIT_UNAVAILABLE sem conteúdo bruto
quando a auditoria falha. Os GETs de metadados não expõem a existência do lote via 403.

```powershell
dotnet test tests/CentralMobilidade.Telemetry.ContractTests --filter FullyQualifiedName~BatchStatusContractTests
```

Essas verificações leem o OpenAPI e serializam DTOs; não simulam uma política própria
nem afirmam autorização, paginação ou auditoria em execução HTTP. Essas provas e os
endpoints continuam nas T057–T060. A suíte completa de contratos tem 80 casos aprovados
na validação da T054. Relatórios locais ficam ignorados em artifacts/validation/T054/.
