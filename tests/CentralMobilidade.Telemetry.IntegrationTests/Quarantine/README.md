# Quarentena protegida — T039

```powershell
dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests --filter FullyQualifiedName~QuarantineStoreTests
```

Os 11 cenários exercitam o `IQuarantineStore` de produção planejado para T048.
Não há store substituto, skips ou falhas aceitas como sucesso. Reflexão serve apenas
para localizar `CentralMobilidade.Telemetry.Infrastructure.Quarantine.FileSystemQuarantineStore`
antes de sua implementação; `ActivatorUtilities` fornece `IConfiguration` e logging.
`Quarantine:Path` aponta a um diretório temporário exclusivo com UUID. A fixture
descarta apenas esse diretório após verificar seu pai/nome, nunca o volume do Compose.

## Cobertura

- Bytes originais e SHA-256 devem coincidir após recuperação; metadata deve preservar
  batch, linha original, código, timestamps e digest, sem campos de caminho público.
- Arquivos devem ter acesso restrito: no Windows, leitura só para o usuário atual,
  SYSTEM e administradores locais; em Unix, sem leitura/escrita por grupo/outros
  e sem acesso de grupo/outros ao diretório raiz. O teste exige armazenamento
  protegido por permissões; não afirma criptografia em repouso nem testa gestão de chaves.
- Logging real do adapter é capturado sem o filtro global de privacidade, incluindo
  propriedades estruturadas e exceções. É exigido ao menos um evento de escrita,
  sem raw, placa, coordenadas, nome, CPF, token ou caminho da fixture. Assim o teste
  não passa simplesmente porque nenhum log foi emitido.
- Alteração de um arquivo do objeto persistido deve impedir uma leitura bem-sucedida
  com `ProviderOperationException`; digest não pode apenas ser retornado sem verificação.
- Cutoffs antes, no instante e depois do prazo de 30 dias verificam exclusão inclusiva,
  metadata/conteúdo ausentes e repetição segura. Retenção maior que 30 dias é rejeitada
  antes de escrever. O scheduler de limpeza permanece na T061.
- Oito replays idênticos concorrentes devem recuperar os mesmos bytes; replay conflitante
  não pode substituir o original. Falha de leitura após um primeiro fragmento não
  pode expor objeto parcial e deve permitir retry, com erro privado e tipado.

Esses testes usam filesystem real do host de execução; o ramo de permissões depende
do sistema operacional, sem skips. A execução atual em Windows valida a compilação
do ramo ACL, mas as asserções só serão alcançadas após implementar T048. A presença
de proteção Unix não é comprovada pela execução Windows. Recuperação após crash,
criptografia/chaves, ataques por symlink e auditoria/autorização administrativa não
são garantias desse conjunto; acesso administrativo será coberto nas T054/T057/T060.

## Estado TDD

11 falhas esperadas pela ausência explícita da T048. Integração completa: 130 passam
e 26 falhas TDD esperadas (15 da T038 + 11 da T039), sem skips. Relatório local:
`artifacts/validation/T039/T039.trx` (ignorado pelo Git). A metadata e os ports são
os existentes da T016; somente testes/documentação são adicionados nesta tarefa.
