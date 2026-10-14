# Corpus determinístico de deduplicação — T040

O gerador `Generators/DedupCorpusGenerator.cs` fornece `Generate(DedupCorpusOptions)`
como sequência lazy de uma linha por vez e `ExportAsync(directory, options, token)`
para materializar dados sem reter o corpus completo na memória.

## Perfis v1

| Veículos | Empresas | Amostras por veículo | Originais | Replays idênticos | Conflitos | Total de linhas |
|---:|---:|---:|---:|---:|---:|---:|
| 10.000 | 10 | 3 | 30.000 | 3.000 | 300 | 33.300 |
| 100.000 | 10 | 3 | 300.000 | 30.000 | 3.000 | 333.000 |

Parâmetros padrão: seed 40001, início Unix-ms 1791979200000, intervalo de 20 segundos,
ordenação por rodada seguida do veículo, empresa distribuída por módulo entre dez tenants.
Identidade é `(company, vehicle, UnixMilliseconds)`. Cada décimo original recebe replay;
cada centésimo recebe conflito depois dele. As taxas são relativas aos originais,
respectivamente 10% e 1%, não ao total enviado. O primeiro evento deve permanecer imutável.

Os dados são inteiramente sintéticos: coordenadas válidas, velocidade 0–129,9 km/h,
RPM 0–4.999, ignição booleana, motorista pseudônimo em 25% dos veículos e placa
sintética em 20%. SplitMix64 explícito, formatação invariant e ordinais sequenciais
garantem reprodução sem depender da implementação de `System.Random`.
Replays alteram ordem de tags/campos, representação numérica e LF para CRLF, mantendo
o conteúdo semântico. Conflitos mantêm identidade e alteram somente velocidade em +1.

O oracle identifica o resultado esperado e preserva a linha original, permitindo
reconciliação futura sem substituir o parser/fingerprint de produção. Não fornece um
hash canônico alternativo nem considera Influx last-write-wins como deduplicação segura.

## Uso e exportação

```csharp
var options = new DedupCorpusOptions(Vehicles: 100_000);
foreach (var row in DedupCorpusGenerator.Generate(options))
{
    // row.Line: LP UTF-8; row.ExpectedOutcome: Original/IdenticalReplay/Conflict.
    // Consumir imediatamente ou alimentar o benchmark da T041.
}
await DedupCorpusGenerator.ExportAsync(outputDirectory, options, cancellationToken);
```

O diretório de saída recebe `corpus.lp.gz`, `oracle.jsonl` e `manifest.json` v1 com
parâmetros, totais e bytes descomprimidos. Os arquivos usam `CreateNew`: não substituem
evidência existente. O manifest só é escrito após terminar os dois streams; cancelamento
ou falha pode deixar arquivos incompletos sem manifest, que não devem ser usados.
A reprodução prometida é dos bytes LP/oracle e totais, não do gzip entre versões de codec.

Para modelar uma hora por veículo, configure `SamplesPerVehicle: 180`. O perfil padrão
tem 60 segundos nominais de amostras por frota, não materializa 30 dias ou cinco anos.
A T041 pode repetir/ampliar o iterator e ordenar concorrência/hot keys; entregas atrasadas,
leases, fencing, recuperação e expiração de evidência serão provadas nos testes próprios.
Os perfis não medem throughput: 5.000 eventos/s é o alvo futuro para a frota de 100.000
veículos, e não uma taxa demonstrada por gerar o arquivo.

## Validação

```powershell
dotnet test tests/CentralMobilidade.Telemetry.LoadTests
```

Quatro testes passam: exportação/recuperação dos perfis de 10.000 e 100.000 veículos,
cardinalidade de identidades/empresas/veículos, oracle por linha, determinismo entre
culturas e mudança de seed, equivalência dos campos de replay e alteração isolada de
velocidade no conflito. Os perfis são efetivamente gerados e recuperados na validação;
não são apenas projeções de contagem. Os testes de exportação retêm a evidência em
`artifacts/validation/T040/<vehicles>-<UUID>/` (ignorado pelo Git), sem dados grandes
versionados neste diretório. Use o manifest para identificar cada execução completa.
