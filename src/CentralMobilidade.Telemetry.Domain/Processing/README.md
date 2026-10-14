# Resultados por linha e metadados de quarentena — T044

`LineProcessingResult` e `QuarantinedRejection` são records imutáveis, distintos dos
POCOs EF existentes em Infrastructure. Não dependem de Contracts/Application/EF;
a integração dos resultados e mapeamentos pelo processamento pertence à T051.

## Resultado por linha

Identidade é BatchId + posição de linha one-based. Accepted/IdenticalReplay exigem
empresa/veículo já validados e não permitem código de erro ou quarentena. Rejected
exige código conhecido e permite IDs parciais somente quando seguramente decodificados,
com referência opcional de quarentena não vazia. Timestamps são normalizados para UTC.

`ProcessingOutcome` e `LineRejectionCode` são enums do domínio. Os nomes de código
reproduzem exatamente os 16 códigos de rejeição v1; um teste exige igualdade com o
catálogo público T013 para detectar drift sem dependência Domain → Contracts.
Application deve mapear explicitamente para seus ports/DTOs, usando os nomes estáveis.

`SafeLineDigest.Compute(rawLine, key)` usa HMAC-SHA256 com chave de pelo menos 32 bytes
e formato `hmac-sha256:v1:<64 hex>`. Não retém raw nem chave. `FromStoredValue` reidrata
valores de persistência confiável e valida formato, sem prometer provar a origem
criptográfica de um token arbitrário. O produtor deve usar Compute, não prefixar SHA
simples. O operador deve fornecer chave aleatória secreta, gerir sua rotação e não
persisti-la/logá-la no resultado; o modelo não cria ou distribui chaves. Esse digest
diagnóstico não é o fingerprint canônico de evento usado para deduplicação na T046.

## Metadados da quarentena

QuarantineId opaco não vazio, batch, linha, código, referência de store protegida e
SHA-256 de conteúdo são obrigatórios. `ProtectedContentReference` valida texto interno
de até 2.048 caracteres, sem controles/Unicode malformado; não é um locator de API.
O digest de integridade é normalizado para hex minúsculo e difere do digest diagnóstico
keyed: ele verifica bytes cujo acesso exige proteção/autorização.

DeleteAfter deve ser >= CreatedAt e <= CreatedAt + 30 dias (720 horas), inclusivamente.
`MarkExpiredContentDeleted` retorna novo estado após confirmação externa de exclusão,
rejeita timestamp anterior ao deadline e preserva a primeira confirmação em replay.
Não realiza IO nem prova sozinho que um objeto foi excluído. O adapter T048 e o
worker T061 precisam confirmar e persistir esse evento; a função não substitui audit.

ToString retorna somente marcadores privados, incluindo os value objects de referência
e digest. Os getters internos continuam acessíveis a componentes confiáveis; não
serializar entidades diretamente em respostas públicas nem contornar filtros de logs.
O nome `EncryptedContentLocation` segue o modelo de dados e não demonstra criptografia:
o armazenamento precisa aplicar a proteção definida na T048 e sua política operacional.

## Evidência

23 testes novos passam: resultado/código/quarentena consistentes, identidade/linha,
catálogo completo, digest keyed e recuperação, deadline/deleção idempotente, SHA-256,
UTC, referências e diagnósticos privados. Unit suite: 151 pass, mesmas 74 falhas
esperadas T036/T037. Arquitetura: 47 pass. Relatório local ignorado pelo Git em
`artifacts/validation/T044/unit.trx`.
