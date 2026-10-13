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
