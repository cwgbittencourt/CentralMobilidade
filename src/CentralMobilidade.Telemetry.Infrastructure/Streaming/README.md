# Spool comprimido limitado — T029

`BoundedRequestSpool` recebe o limite de bytes comprimidos, timeout e, opcionalmente,
um diretório temporário existente. `ReadAsync` lê um Stream assíncrono, sem precisar de
Length, Position ou leitura síncrona, e aceita Content-Length opcional.

O corpo original é gravado em arquivo temporário com nome aleatório e CreateNew.
A memória não cresce com o payload: são usados buffers fixos de 64 KiB e dez bytes
para o cabeçalho. SHA-256 é calculado incrementalmente sobre os mesmos bytes gravados;
o corpo não é convertido em string, JSON ou Base64. O buffer alugado é zerado ao retornar
ao pool. Em Unix, o arquivo é criado com permissões 0600; no Windows herda as permissões
do diretório temporário. O diretório deve ser restrito e adequado à capacidade do intake.

O loop lê no máximo um byte além do limite para detectar excesso, sem gravá-lo. Um
Content-Length acima do máximo é rejeitado antes de ler; tamanho declarado divergente,
corpo vazio e cabeçalho gzip básico inválido retornam REQUEST_INVALID. Verificações de
magic, método e flags reservadas também aceitam cabeçalhos recebidos em fragmentos.
Descompressão, CRC, trailer e cabeçalhos opcionais completos pertencem à T045.

O resultado `SpooledRequest` expõe CompressedBytes, PayloadSha256 e Body apenas para
leitura, reposicionado no início e com suporte a seek. O chamador conserva a propriedade
do stream de entrada e deve descartar o resultado depois da publicação. Dispose/DisposeAsync
fecha e remove o arquivo via DeleteOnClose; falhas durante a leitura também o fecham.
Não se afirma limpeza automática de arquivos após SIGKILL ou perda do host.

O deadline abrange leitura, escrita e flush. Timeout próprio retorna UPLOAD_TIMEOUT;
cancelamento do chamador continua sendo OperationCanceledException. Excesso retorna
PAYLOAD_COMPRESSED_LIMIT. Falhas de I/O retornam INTERNAL_ERROR como falha transitória,
sem publicar caminhos, conteúdo ou exceções internas. A vinculação à configuração
TelemetryOptions e ao endpoint será feita nas tarefas de orquestração/intake.

Treze testes em `IntegrationTests/Streaming/BoundedRequestSpoolTests.cs` passaram no
Docker Linux: limites, bytes/hash, proteção contra escrita, fragmentação não posicionável,
cabeçalho inválido, tamanho declarado, descarte/limpeza, timeout e cancelamento.
A suíte completa teve 271 aprovações e as mesmas 46 falhas TDD pendentes das T025–T027.
A execução da versão final da DLL local foi bloqueada pelo Controle de Aplicativo do
Windows; a compilação e a validação no Docker passaram.
