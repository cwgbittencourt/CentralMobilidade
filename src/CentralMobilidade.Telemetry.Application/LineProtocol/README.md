# Parser e fingerprint Line Protocol v1 — T046

`TelemetryLineProtocolV1Parser.Parse(ReadOnlyMemory<byte>)` recebe uma única linha
limitada, opcionalmente terminada em LF/CRLF. Limite padrão 16 KiB; o construtor com
`IOptions<TelemetryOptions>` usa o limite configurado. Verifica bytes antes de decodificar,
usa UTF-8 estrito e não materializa payloads inteiros. O leitor T045 fornece as linhas;
este parser escaneia cada linha e mantém dicionários do schema fixo (três tags/seis fields).

Aceita exclusivamente `telemetria`, tags empresa/veículo obrigatórias e motorista
opcional; campos latitude/longitude/velocidade float finitos, RPM inteiro signed com
sufixo `i`, ignição `true`/`false` e placa string opcional. Rejeita colunas desconhecidas
(inclusive fingerprint fornecido pelo produtor), duplicadas, tipos incorretos, sintaxe
malformada, coordenadas fora dos limites, velocidade/RPM negativos e IDs inválidos.
Não resolve driver nem empresa cadastral. O timestamp é Unix-ms UTC e precisa caber
no range de nanosegundos signed de Influx, sem inferência de precisão ou consulta ao relógio.

Escapes de tags e strings são decodificados antes de construir `TelemetryEvent`.
Aspas em valores de tags são literais; backslash antes de caractere não especial é
preservado. Campos string aceitam espaços/vírgulas dentro das aspas e aspas/backslashes
escapados. Referência usada: [Line Protocol InfluxData](https://docs.influxdata.com/influxdb3/enterprise/reference/line-protocol/).
O contrato local v1 continua mais restrito que o protocolo genérico (measurement,
colunas e booleanos fechados). Comentários não são eventos desse schema.

Resultado é `LineProtocolParseResult`: Event validado com fingerprint ou ErrorCode
do catálogo T013. `ParsedTelemetryEvent.Value` contém o evento T043, com projeções
de atributos/Identity para processamento. ToString dos resultados/eventos é privado;
erro nunca devolve raw, placa, coordenadas, IDs ou exceções do decoder. Limites de
tipo/número são distintos dos códigos de sintaxe, timestamp e coordenadas.

## Formato canônico v1

Fingerprint é SHA-256 lowercase de um frame binário com esta ordem:

1. String de versão `centralmobilidade:telemetry:event:v1`.
2. CompanyId e VehicleId preservados em case/Unicode; UnixMilliseconds (Int64).
3. Booleano indicando presença de DriverId e, se presente, sua string.
4. Latitude, longitude e velocidade (IEEE754 Double), RPM (Int64), ignição (booleano).
5. Booleano indicando presença de placa e, se presente, placa uppercase invariant T043.

Frame usa BinaryWriter: strings UTF-8 com comprimento de bytes em inteiro 7-bit,
numéricos little-endian e booleanos em um byte. Zero negativo é escrito como zero
positivo; números equivalentes em diferentes grafias/exponentes tornam-se os mesmos
valores Double. Ordering/escaping/terminação/whitespace entre seções não entram no
hash. Presença distingue optional ausente de string vazia; ID/driver não sofre trim,
case-folding ou normalização Unicode. Todo conteúdo do evento e sua identidade participam.

Essa representação v1 precisa permanecer estável durante a coexistência de writers
e evidência de replay. Mudanças de campos/normalização/frame exigem revisão e nova
versão, não alteração silenciosa do digest. Não é o HMAC diagnóstico de linhas rejeitadas
da T044 nem depende de chave ou cultura. Não gera LP de escrita ou batches nessa etapa.

## Validação

55 testes de parser passam (45 T036 + dez cenários adicionais): schema, escaping,
UTF-8, tipos, unidades, timestamp, valores opcionais, colunas duplicadas, limites,
canonicalização/cultura/zero negativo e diagnósticos privados. Suíte unitária completa:
241 pass; arquitetura: 47 pass, sem skips. Build Linux sem avisos/erros.

Windows compilou, mas bloqueou a nova DLL unitária por Application Control
`0x800711C7`, inclusive fora do sandbox. Foi usada a imagem Linux de validação Docker,
SDK 10.0.401/runtime 10.0.12, sem modificar políticas do Windows. Evidência local em
`artifacts/validation/T046/unit-linux.txt` e `architecture-linux.txt`; Dockerfile local
ignorado em `artifacts/validation/T046/tests.Dockerfile`. O `unit.trx` Windows registra
o bloqueio e não é evidência de testes verdes.
