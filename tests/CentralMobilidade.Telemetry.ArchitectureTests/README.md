# Testes de arquitetura

Execute a partir do checkout da solução:

```powershell
dotnet test tests/CentralMobilidade.Telemetry.ArchitectureTests
```

As regras verificam as referências declaradas nos projetos, as referências avaliadas no
`project.assets.json` gerado pelo restore e as referências presentes nos metadados das DLLs.
O teste lê os metadados com `PEReader`, sem carregar ou executar as classes dos projetos inspecionados.
O checkout, os assets de restore e as DLLs compiladas são necessários; ao usar `--no-build`,
execute previamente restore e build para atualizar esses artefatos.

Domain e Contracts são independentes. Application pode depender de Domain e Contracts;
Infrastructure pode depender dessas três camadas. Api e Worker podem depender das camadas
internas, mas não um do outro. Essa direção também impede ciclos entre as camadas.

Domain, Application e Contracts não podem depender de frameworks de host nem das famílias
de pacotes e assemblies de infraestrutura enumeradas em `InfrastructureFamilies`, incluindo
EF Core, Npgsql, RabbitMQ, Google Cloud, InfluxDB, ASP.NET Core, Hosting, OpenTelemetry e Serilog.
Os pacotes transitivos também são inspecionados. Ao introduzir um novo provedor, atualize a
lista e seus casos de rejeição. Bibliotecas de validação, opções e abstrações de injeção de
dependência continuam permitidas em Application.

A validação da T023 comprovou que uma referência temporária e não utilizada de Domain para
Infrastructure faz o teste falhar. O projeto original foi restaurado após essa verificação.
