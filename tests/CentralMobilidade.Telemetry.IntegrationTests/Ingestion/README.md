# Limites da ingestão — T025

`IntakeBoundaryTests` contém 35 cenários executáveis contra o entry point de produção,
iniciado por `WebApplicationFactory`. A rota de produção implementada na T034 é exercitada diretamente.
Somente a fila e o armazenamento de estado são substituídos por doubles em memória;
a autenticação e o pipeline HTTP continuam sendo os da API.

```powershell
dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests --filter FullyQualifiedName~IntakeBoundaryTests
```

## Cobertura

- Corpo comprimido abaixo, no limite e acima do máximo, com e sem Content-Length;
  o limite usa o tamanho real da fixture gzip, evitando arquivos de 8 MiB por cenário.
- Content-Type ausente ou incorreto, charsets incompatíveis/duplicados e variantes
  UTF-8 válidas, incluindo charset entre aspas e nomes sem distinção de caixa.
- Content-Encoding ausente/incorreto/composto e versão de contrato ausente/incorreta.
- Corpo vazio, bytes sem gzip, cabeçalho incompleto, assinatura/método inválidos e flags reservadas.
- Upload bloqueado que deve expirar com 408 e cancelar a leitura; desconexão durante
  a leitura que não pode publicar nem registrar estado Accepted.
- Proxy conhecido por endereço ou rede, origem não confiável, rede externa,
  cadeia limitada a um salto, simetria de headers e X-Forwarded-Host forjado.

Os corpos são streams assíncronos não posicionáveis. O teste não permite obter Length
nem efetuar leitura síncrona. As rejeições devem usar Problem Details com código estável,
sem publicar na fila. Os casos aceitos preservam os bytes gzip; o teste de tamanho também
confere tamanho e SHA-256 do envelope. A credencial de produtor é gerada para o processo
de teste e configurada somente como hash.

O teste de proxies fornece `ForwardedHeadersOptions` com KnownProxies `10.10.0.2`,
KnownIPNetworks `10.20.0.0/24`, ForwardLimit 1, RequireHeaderSymmetry true e apenas
XForwardedFor/XForwardedProto. O pipeline de produção deve aplicar essas opções.
O observador apenas captura o contexto; ele não interpreta os headers.

## Estado TDD

A T025 adicionou os testes antes da implementação das T029/T033/T034/T035. Após a T035,
os 35 cenários passam, incluindo limites, headers, gzip, timeout, cancelamento,
respostas duráveis com `Location`/`Retry-After` e processamento de forwarded headers
somente para proxies e redes explicitamente confiáveis. Nenhum cenário foi desabilitado
ou ignorado.

O TestServer simula
streams e cancelamento do HTTP; esta etapa não comprova limites de transporte do Kestrel,
enquadramento de chunked HTTP, durabilidade real de RabbitMQ/PostgreSQL ou throughput.
CRC/truncamento do conteúdo comprimido e limites de descompressão pertencem à T037/T045.

Relatórios TRX locais: `artifacts/validation/T025/` (ignorado pelo Git).
