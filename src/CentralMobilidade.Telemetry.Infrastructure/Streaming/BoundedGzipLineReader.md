# Leitor gzip limitado — T045

`ReadAsync(Stream, compressedBytes, TelemetryOptions, CancellationToken)` retorna uma
sequência assíncrona de linhas UTF-8 em bytes. Não lê Length/Position, não materializa
o payload como string e mantém a propriedade do stream com o chamador. Memória de
uma linha é emprestada até o próximo avanço: consumir/copiar antes de MoveNext.
Memória de trabalho é um buffer de 64 KiB, uma linha limitada (+1 byte para CRLF),
o estado do descompressor e oito bytes de trailer; buffers locais são limpos no finally.

Limites independentes: bytes descomprimidos (64 MiB padrão), razão calculada contra
o tamanho total comprimido conhecido do spool (100:1), bytes de linha sem delimitador
(16 KiB) e linhas físicas (50.000). Conta delimitadores nos bytes descomprimidos.
O limite de linhas físicas inclui linhas vazias para limitar recursos; vazias finais
não são emitidas, e vazias intermediárias são emitidas para rejeição pelo parser.
Última linha sem newline é preservada. Somente CR imediatamente antes de LF é removido.

Snapshot dos limites é feito no início da enumeração. Deadline ligado ao token usa
UploadTimeout durante toda a enumeração, incluindo tempo do consumidor entre avanços.
Excesso de byte/razão/linha usa o código estável correspondente em ProviderOperationException;
contagem excedida usa REQUEST_INVALID, pois não há código específico no catálogo.
Cancelamento/deadline usa OperationCanceledException. UTF-8/schema são validados na T046.

Além da validação do GZipStream, confere tamanho comprimido real, CRC32 incremental e
ISIZE do trailer para rejeitar truncamento que o descompressor pode tolerar. Sucesso
exige consumir a enumeração até o fim: linhas podem ser produzidas antes da validação
final, portanto o worker não pode reconhecer a entrega ao receber a primeira linha.
Gzip é validado como um único membro; membros concatenados não são suportados por
essa conferência de trailer. A verificação adicional não é um parser estrito de todos
os headers/extensões nem uma garantia de rejeição de qualquer garbage final forjado.
Erros de integridade são InvalidDataException com mensagens sem conteúdo bruto.

Validação T045: 35 cenários de streaming passam (29 T037 + seis testes de linhas
vazias/EOF e tamanho comprimido). Suíte unitária: 186 aprovações, 45 falhas previstas
da T036 até T046; arquitetura: 47 aprovações. Relatório em
`artifacts/validation/T045/unit.trx`, ignorado pelo Git.
