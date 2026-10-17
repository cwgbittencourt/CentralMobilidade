# Cobertura unitária — T090

Execute `dotnet test tests/CentralMobilidade.Telemetry.UnitTests` a partir da raiz.
Não exige Docker. A T090 acrescenta 43 casos à suíte existente (380 no total em
2026-10-17), exercitando código de produção por interfaces públicas.

| Área | Evidência |
| --- | --- |
| Opções | `Configuration/TelemetryOptionsTests.cs` valida binding, startup, limites e retenções; `OperationalRetentionOptionsTests.cs` adiciona extremos válidos e rejeição de trabalho de cleanup sem limites |
| Permissões/escopos | `Presentation/PresentationHandlerTests.cs` cobre permissões independentes, cross-company, viewer, revogação, correspondência exata de actor/tipo/empresa/permissão e authorizer negando/falhando sem consulta de dados |
| Auditoria fail-closed | Mesmo arquivo: indisponibilidade, recibo divergente, bloqueio aguardando commit e commit sem data UTC válida impedem divulgação; resolução interna pode preceder auditoria, mas o resultado não retorna sem confirmação |
| Empresa válida sem cadastro | `Processing/ProcessingPolicyTests.cs` processa gzip pelo handler real e preserva IDs novos, espaços/Unicode e escapes no único port de escrita, sem resolver cadastral |
| Canonicalização | `LineProtocol/LineProtocolV1GoldenTests.cs` cobre ordem, representação numérica, CRLF, conteúdo alterado, zero assinado, placa normalizada sob cultura turca e distinção de IDs |
| Erros | `Errors/ProviderFailureTests.cs` cobre status públicos, classes de falha e catálogo fechado sem diagnóstico privado; testes de processamento verificam replay/conflito/existência sem classificação e falha de escrita sem commit/ACK |
| Clocks separados | Handler real recebe eventos antigos/atuais/futuros com receipt anterior: guard cobre 60 dias após o maior entre evento/processamento; evidência usa 30 dias após processamento, sem usar o receipt antigo como início |

Os doubles do processamento apenas capturam pedidos, retornam decisões do port e
registram efeitos. Não implementam classificação de fingerprints, exclusão,
CAS/fencing ou um banco substituto. A execução de SQL de cleanup, concorrência
do guard, persistência e recuperação continuam cobertas por integração, especialmente
`Recovery/SelectedEventIdentityGuardTests.cs`. Os testes unitários não certificam
durabilidade de banco/fila, cinco anos de retenção, capacidade ou operação cloud.

A T090 não muda comportamento de produção. Relatórios TRX/logs locais ficam
ignorados em `artifacts/validation/T090/` na raiz.
