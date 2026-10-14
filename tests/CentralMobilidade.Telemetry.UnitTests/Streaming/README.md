# Limites gzip — T037

`GzipBoundaryPropertyTests` contém 29 cenários para o leitor de produção da T045.

```powershell
dotnet test tests/CentralMobilidade.Telemetry.UnitTests --filter FullyQualifiedName~GzipBoundaryPropertyTests
```

- Geração determinística (seed 37001): 20 payloads por cenário de fragmentação,
  com cinco tamanhos de leitura, Unicode multibyte, LF/CRLF e comparação exata das linhas.
- Truncamento de cinco tamanhos, incluindo trailer e conteúdo, e corrupção de cada
  byte do CRC. A enumeração deve falhar com `InvalidDataException`, inclusive depois
  de produzir linhas; concluir a enumeração com sucesso significa ter validado o gzip.
- 16 KiB por linha: abaixo, no limite e acima, contando bytes UTF-8 e excluindo LF/CRLF.
- 50.000 linhas: abaixo, no limite e acima, independentemente dos demais limites.
- 64 MiB descomprimidos: abaixo, no limite e acima. O payload é gerado em blocos de
  8 KiB, sem materializar os 64 MiB; o consumidor apenas conta linhas.
- Expansão acima de 100:1 e inclusividade de um limite configurado, com os limites
  de bytes e linhas isolados. Nos outros testes a razão é elevada deliberadamente
  para que um limite não mascare outro.
- Cancelamento do chamador e deadline configurado interrompem leitura bloqueada;
  um watchdog de três segundos impede que uma implementação sem cancelamento trave o teste.

Streams são assíncronos, não posicionáveis e não expõem `Length`. O leitor mantém
a propriedade do stream com o chamador. A memória retornada para uma linha pode ser
reutilizada no próximo avanço; o teste a consome imediatamente.

## Ponte temporária TDD

O projeto unitário referencia Infrastructure para testar o leitor de streaming.
Reflexão mantém a compilação antes da T045; não há leitor substituto, skips ou bypasses.
Superfície proposta: `BoundedGzipLineReader.ReadAsync(Stream, long compressedBytes,
TelemetryOptions, CancellationToken)` retorna `IAsyncEnumerable<ReadOnlyMemory<byte>>`.
O tamanho comprimido é o total conhecido do corpo original (spool da ingestão).
O leitor retorna linhas sem delimitadores e inclui a última linha sem newline.
Limites usam `ProviderOperationException` e os códigos existentes; o catálogo ainda
não define um código específico para contagem de linhas, então esse teste exige
rejeição tipada sem inventar um código. Deadline/cancelamento usam `OperationCanceledException`.
A T045 pode substituir a ponte por chamadas tipadas preservando o comportamento.

Este conjunto usa fuzz determinístico de fragmentação e propriedades de preservação
e limites; não usa biblioteca externa de property testing nem afirma cobrir todos
os formatos de membros gzip. A geração de casos de 64 MiB só ocorre após resolver
o leitor, evitando custo desnecessário durante a etapa vermelha.

## Resultado

29 falhas esperadas pela ausência da T045, 45 falhas esperadas da T036 pela ausência
da T046 e 101 testes unitários anteriores aprovados. Compilação sem avisos.
Relatório: `artifacts/validation/T037/T037.trx` (ignorado pelo Git).
