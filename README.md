# Central Mobilidade

Plataforma de ingestão, processamento, consulta e observabilidade de telemetria veicular para múltiplas empresas.

O sistema deverá receber lotes compactados de eventos enviados por uma central interna, confirmar o recebimento somente depois que os dados estiverem duravelmente enfileirados, processar cada evento de forma assíncrona e disponibilizar a telemetria autorizada para consulta e visualização no Grafana.

> **Estado atual:** projeto em construção. A solução e os seis projetos de produção em .NET 10 já foram criados (T001 e T002). As APIs, workers, persistência, contêineres, testes e dashboards descritos neste documento ainda fazem parte do plano de implementação.

## O que o projeto deverá entregar

Ao final, a Central Mobilidade deverá fornecer:

- uma API autenticada e versionada para receber lotes `gzip` em InfluxDB Line Protocol;
- confirmação `202 Accepted` somente depois da persistência durável do lote na fila;
- processamento assíncrono, incremental e limitado por memória, bytes, linhas e tempo;
- aceitação parcial: linhas válidas são preservadas mesmo quando outras linhas do lote são inválidas;
- idempotência por empresa, veículo e instante do evento, sem alteração silenciosa do evento original;
- isolamento e retenção controlada de rejeições em quarentena protegida;
- acompanhamento do estado de cada lote, com contagens e códigos de erro estáveis;
- separação de telemetria de empresas conhecidas e ainda desconhecidas;
- promoção segura da área pendente para a área consultável quando a empresa for reconhecida;
- consultas limitadas para posição atual, trajeto de um veículo e saúde da ingestão;
- resolução cadastral separada e auditada para apresentação mínima da identidade do motorista;
- dashboards técnicos e de negócio no Grafana, com isolamento entre organizações;
- métricas, logs, traces, health checks, alertas e runbooks operacionais;
- ambiente local reproduzível com Docker Compose e volumes persistentes;
- abstrações e testes de contrato que permitam migrar de RabbitMQ para Google Pub/Sub;
- uma suíte automatizada de testes unitários, de contrato, integração, arquitetura, recuperação, carga e privacidade;
- evidência final de ingestão, armazenamento e visualização de exatamente 100.000 eventos válidos.

## Fluxo previsto

```text
Central interna de telemetria
          |
          | POST gzip + Line Protocol v1
          v
Telemetry.Api
  - autenticação do produtor
  - limites e backpressure
  - spool e hash em streaming
  - criação do lote
  - publicação com confirmação
          |
          +----------------------> PostgreSQL
          |                         lotes, resultados, identidade,
          |                         configuração e auditoria
          v
Fila durável
  - RabbitMQ inicialmente
  - Google Pub/Sub por adaptador
          |
          v
Telemetry.Worker
  - descompactação limitada
  - parser incremental
  - validação e idempotência
  - quarentena de rejeições
  - processamento parcial
          |
          +----------------------> InfluxDB pendente
          |                         empresas desconhecidas
          |
          +----------------------> InfluxDB consultável
                                    empresas reconhecidas
                                             |
                  +--------------------------+-------------------+
                  |                                              |
       Grafana interno direto                         API de apresentação
       acesso privilegiado                            acesso limitado por empresa
                                                               |
                                                        Grafana de viewers
```

O `Telemetry.Api` e o `Telemetry.Worker` são processos separados para permitir escalabilidade e falhas independentes, mas pertencem ao mesmo contexto de negócio e à mesma versão de implantação.

## Capacidades funcionais

### 1. Recebimento durável

O produtor técnico enviará os lotes para `POST /api/v1/telemetry/batches`. A entrada deverá exigir autenticação, `Content-Encoding: gzip`, UTF-8 e o contrato Line Protocol v1.

O retorno `202 Accepted` será permitido apenas depois da confirmação do broker. A resposta incluirá um `batchId` globalmente único e o endereço para consulta de status. Falhas anteriores à confirmação deverão retornar respostas não bem-sucedidas e classificáveis, sem marcar o lote como aceito.

Um lote confirmado deverá sobreviver a reinícios da aplicação e dos contêineres. Mensagens não processadas terão retenção de até sete dias.

### 2. Processamento parcial e limitado

O worker deverá descompactar e ler o conteúdo incrementalmente, sem materializar todo o corpo descompactado em uma única `string` ou estrutura sem limite.

Cada linha será validada independentemente:

- linhas válidas seguem para armazenamento;
- linhas inválidas recebem posição, código de erro estável e digest seguro;
- o conteúdo bruto rejeitado fica somente na quarentena protegida;
- uma linha inválida não impede que as demais sejam persistidas;
- a fila somente é reconhecida depois que todos os resultados duráveis forem registrados.

### 3. Idempotência e conflitos

A identidade lógica de um evento será formada por:

```text
empresa_id + veiculo_id + DataHoraEvento (UTC)
```

Durante toda a retenção da telemetria, um guardião de identidade imutável deverá impedir uma segunda gravação lógica para essa combinação.

- um replay idêntico não cria outro evento;
- dentro de 30 dias, o replay poderá ser classificado precisamente pelo fingerprint;
- um evento diferente para uma identidade já confirmada não substitui nem mescla o original;
- dentro de 30 dias, o conflito gera evidência inspecionável;
- reservas incompletas poderão ser retomadas pelo proprietário ou assumidas após expiração de lease, com fencing contra concorrência;
- uma falha ambígua entre PostgreSQL e InfluxDB poderá repetir a escrita física idêntica, mas nunca criar um segundo evento lógico mutável.

A estratégia definitiva do guardião de identidade depende de benchmark obrigatório para 10.000 e 100.000 veículos e será documentada em ADR antes da implementação correspondente.

### 4. Empresas conhecidas e pendentes

Eventos de empresas reconhecidas serão gravados no banco InfluxDB consultável. Eventos de empresas desconhecidas, ou cuja consulta cadastral seja indisponível ou inconclusiva, irão para um banco pendente separado e invisível aos dashboards.

Um worker de promoção deverá mover esses eventos de maneira idempotente quando a empresa for reconhecida. A origem pendente só poderá ser apagada depois da confirmação e reconciliação do destino consultável.

### 5. Consulta de lotes e rejeições

O produtor poderá consultar somente os lotes enviados por sua própria credencial. Operadores precisarão da permissão `batch.read` e de escopo para todas as empresas do lote, salvo quando possuírem `cross_company.read`.

A API deverá expor:

- estado e horários do lote;
- totais de linhas aceitas, rejeitadas e reconhecidas como replay;
- resumos por código de erro;
- paginação de rejeições sem exposição de conteúdo bruto;
- consulta administrativa do conteúdo bruto por identificador opaco, com permissão e auditoria específicas.

Identificadores desconhecidos, expirados ou sem autorização deverão produzir a mesma resposta não reveladora.

### 6. Apresentação e dashboards

O produto deverá fornecer três visualizações iniciais:

- posição mais recente dos veículos;
- trajeto de um único veículo, ordenado pelo instante real do evento;
- saúde da ingestão por empresa.

Os endpoints de apresentação não aceitarão SQL arbitrário. Os limites padrão serão:

| Consulta | Limite padrão |
|---|---:|
| Posições mais recentes | 500 veículos por página |
| Trajeto | 1 veículo, até 24 horas e 10.000 pontos |
| Saúde da ingestão | até 7 dias, granularidade mínima de 1 minuto e 10.000 pontos |

Usuários internos privilegiados poderão usar uma organização Grafana com acesso direto ao InfluxDB consultável. Viewers restritos usarão organizações sem datasource InfluxDB e acessarão apenas a API limitada por meio do plugin Infinity.

### 7. Identidade do motorista

A telemetria armazenará somente o identificador pseudônimo `motorista_id`. Nome, CPF, telefone ou outros identificadores diretos não poderão ser armazenados na telemetria, em logs, métricas ou traces.

Quando autorizado, o adaptador cadastral poderá retornar apenas:

- identificador pseudônimo;
- nome de apresentação;
- opcionalmente, os quatro últimos dígitos do CPF.

O acesso deverá ser auditado antes da divulgação e falhar fechado se a auditoria durável não puder ser registrada.

## Contrato de telemetria

O contrato de entrada será fechado e versionado. Cada evento deverá conter:

- `empresa_id`;
- `veiculo_id`;
- latitude entre -90 e 90;
- longitude entre -180 e 180;
- velocidade não negativa;
- RPM não negativo;
- ignição booleana;
- `DataHoraEvento`, usada como timestamp UTC.

Campos opcionais incluem `motorista_id` pseudônimo e placa. Campos adicionais fora do contrato deverão ser rejeitados. Eventos atrasados ou fora de ordem conservarão o horário original.

As definições normativas estão em:

- [OpenAPI](specs/001-telemetry-ingestion/contracts/openapi.yaml);
- [Line Protocol v1](specs/001-telemetry-ingestion/contracts/line-protocol-v1.md);
- [envelope da fila](specs/001-telemetry-ingestion/contracts/queue-envelope.schema.json).

O corpo original `gzip` viajará na fila como bytes nativos. Metadados como versão, `batchId`, trace, produtor, tamanho e SHA-256 serão transportados em headers ou attributes, nunca como um envelope JSON/Base64 envolvendo o payload.

## Limites iniciais

Os valores são hipóteses operacionais que deverão ser validadas por testes, não garantias antecipadas de capacidade.

| Limite | Valor inicial |
|---|---:|
| Corpo HTTP compactado | 8 MiB |
| Corpo descompactado | 64 MiB |
| Razão máxima de expansão | 100:1 |
| Tamanho máximo por linha | 16 KiB |
| Linhas por lote de ingestão | 50.000 |
| Duração máxima de upload | 30 segundos |
| Pontos por escrita no InfluxDB | 5.000 |
| Bytes por escrita no InfluxDB | 2 MiB |
| Intervalo máximo para flush | 1 segundo |

Cada limite será configurável, tipado, validado durante a inicialização e testado nos valores de fronteira.

## Segurança, privacidade e auditoria

O modelo previsto separa credenciais e responsabilidades:

- **Producer credential:** somente ingestão e consulta dos próprios lotes;
- **Operator credential:** permissões explícitas e escopo de empresas;
- **GrafanaBootstrapAdminCredential:** somente bootstrap de organizações e membros no Grafana, via Basic authentication;
- **GrafanaOrganizationServiceAccount:** reconciliação de recursos dentro de uma organização Grafana;
- **ViewerPresentationServiceCredential:** acesso da organização viewer à API, limitado às empresas autorizadas.

Regras obrigatórias:

- segredos serão armazenados como hashes ou fornecidos por fontes externas e nunca registrados;
- consultas sensíveis, tentativas negadas e leituras cross-company serão auditadas;
- auditoria não conterá payload, coordenadas, placa, nome, CPF, token ou conteúdo de quarentena;
- conteúdo bruto rejeitado não aparecerá em logs, métricas ou traces;
- logs técnicos permitirão correlação sem expor dados pessoais;
- ambientes não locais deverão permitir TLS administrado pela operação;
- a análise LGPD será técnica e indicará decisões que dependem de DPO/jurídico, sem alegar conformidade legal automática.

No MVP, a auditoria de viewers será atribuída à organização Grafana e à credencial de serviço, não a um usuário humano individual.

## Retenção planejada

| Informação | Retenção |
|---|---:|
| Mensagens aceitas e não processadas | até 7 dias |
| Status de lote | 30 dias |
| Conteúdo bruto em quarentena | no máximo 30 dias |
| Evidência detalhada de fingerprint/conflito | 30 dias |
| Logs locais no Loki | 30 dias |
| Telemetria no ambiente local | 60 dias |
| Telemetria futura em produção | 5 anos, condicionada a capacidade e storage externo validados |

O guardião imutável da identidade permanecerá pelo tempo de retenção da telemetria correspondente.

## Stack tecnológica

- C# 14 e .NET 10 LTS;
- ASP.NET Core Minimal APIs;
- `System.IO.Pipelines` para streaming;
- RabbitMQ como fila durável inicial;
- Google Cloud Pub/Sub e emulator para portabilidade e testes de contrato;
- InfluxDB 3 Core para telemetria;
- PostgreSQL, Npgsql e EF Core 10 para plano de controle, auditoria e idempotência;
- filesystem protegido para quarentena inicial;
- OpenTelemetry, Prometheus, Loki e Serilog para observabilidade;
- Grafana OSS com plugin Infinity assinado e versões fixadas;
- FluentValidation;
- xUnit, Testcontainers e ferramentas de carga como NBomber ou k6;
- Docker e Docker Compose para o ambiente local.

## Estrutura da solução

```text
CentralMobilidade.slnx
src/
├── CentralMobilidade.Telemetry.Api
├── CentralMobilidade.Telemetry.Worker
├── CentralMobilidade.Telemetry.Domain
├── CentralMobilidade.Telemetry.Application
├── CentralMobilidade.Telemetry.Infrastructure
└── CentralMobilidade.Telemetry.Contracts
tests/
├── CentralMobilidade.Telemetry.UnitTests
├── CentralMobilidade.Telemetry.ContractTests
├── CentralMobilidade.Telemetry.IntegrationTests
├── CentralMobilidade.Telemetry.ArchitectureTests
└── CentralMobilidade.Telemetry.LoadTests
deploy/
├── containers
├── grafana
├── prometheus
└── loki
```

As dependências deverão apontar para dentro:

- Domain não depende de infraestrutura;
- Contracts permanece independente e versionável;
- Application usa Domain e Contracts e define as portas;
- Infrastructure implementa as portas e integra provedores;
- API e Worker são os hosts de composição.

Testes de arquitetura deverão impedir violações dessa direção.

## Ambiente local esperado

O Docker Compose final deverá iniciar, com volumes nomeados persistentes:

- API e Worker;
- PostgreSQL;
- RabbitMQ;
- InfluxDB consultável;
- InfluxDB pendente;
- Grafana;
- Prometheus;
- Loki;
- armazenamento de quarentena;
- opcionalmente, o emulator do Google Pub/Sub.

Os serviços deverão ter health checks, restart policies, limites de recursos, usuários não-root e filesystems somente leitura onde possível. Imagens e plugins não usarão a tag `latest`.

O ambiente inicial continuará sujeito à perda física do host único. Alta disponibilidade ou proteção contra perda do host somente poderão ser declaradas após réplica ou backup independente e validação de recuperação.

## Observabilidade e operação

O sistema deverá publicar indicadores para:

- taxa de entrada e volumes compactado/descompactado;
- tamanho e idade do backlog;
- latência por estágio;
- pontos aceitos e rejeitados;
- replays e conflitos;
- tentativas e falhas de dependências;
- pressão de capacidade, CPU, memória e disco;
- atividade e freshness do worker.

Alertas iniciais cobrirão backlog, risco de exceder cinco minutos até consulta, aproximação da retenção da fila, falhas de storage, pressão de disco e silêncio do worker.

Runbooks deverão documentar backup e restore, limitações do host único, migração RabbitMQ → Pub/Sub e futura adoção de Google Cloud Storage.

## Metas mensuráveis

O produto só deverá declarar capacidade depois de produzir evidências reproduzíveis para os seguintes critérios:

- sustentar 5.000 eventos por segundo;
- tornar consultáveis em até cinco minutos 99% dos eventos reconhecidos sob carga normal;
- não perder eventos reconhecidos durante reinícios de aplicação ou contêiner;
- no perfil de referência de 8 vCPUs e 16 GiB, manter CPU sustentada em até 85% e memória da aplicação em até 12 GiB;
- suportar o cenário-alvo de 100.000 veículos enviando um evento a cada 20 segundos;
- absorver uma reconexão de 10.000 veículos com uma hora acumulada, enviada em dez minutos enquanto a carga normal continua, drenando o backlog em até 20 minutos;
- persistir 100% das linhas válidas de lotes mistos e classificar 100% das inválidas ou conflitantes;
- manter replays sem eventos lógicos adicionais e conflitos sem modificar o original;
- responder a 95% das leituras de status em até dois segundos;
- impedir que empresas desconhecidas ou não autorizadas apareçam nos dashboards;
- encontrar zero ocorrências de payload, placa, coordenadas, nome, CPF, token ou segredo nos canais proibidos de observabilidade;
- provar, no aceite final, exatamente 100.000 eventos válidos conciliados entre lotes, InfluxDB e Grafana.

## Estratégia de testes

Todo comportamento de produção deverá ter verificação automatizada no projeto apropriado:

- **UnitTests:** regras de domínio, parsing, limites, canonicalização e opções;
- **ContractTests:** OpenAPI, Line Protocol, fila e adaptadores cadastrais;
- **IntegrationTests:** PostgreSQL, RabbitMQ, InfluxDB, Pub/Sub emulator, Grafana, Compose, recuperação e privacidade;
- **ArchitectureTests:** direção das dependências e isolamento de camadas;
- **LoadTests:** carga constante, reconexão, soak, indisponibilidade, recuperação e prova final de 100.000 eventos.

O desenvolvimento seguirá TDD nas histórias: os testes do comportamento deverão falhar pela razão esperada antes da implementação.

## Roadmap de entrega

1. **Setup:** solução, projetos, dependências centrais, padrões de build e contêineres.
2. **Fundação:** contratos, configuração, segurança, persistência, health checks e observabilidade básica.
3. **MVP de ingestão durável:** autenticação, limites, RabbitMQ e `202` confirmado pelo broker.
4. **Processamento:** parser, validação, quarentena, InfluxDB, idempotência e recuperação.
5. **Suporte operacional:** status de lotes, rejeições e retenção.
6. **Apresentação:** consultas limitadas, promoção de empresas, identidade auditada e dashboards.
7. **Portabilidade:** contrato comum de filas, Pub/Sub, alertas e runbooks.
8. **Gates de produção:** performance, segurança, LGPD técnica, recuperação, prova de 100.000 eventos e matriz de rastreabilidade.

O primeiro MVP comprovará apenas o recebimento durável. A primeira versão útil acrescentará persistência de telemetria e inspeção de lotes. A entrega completa dependerá dos dashboards, portabilidade e de todos os gates de produção.

## Definição de pronto

O projeto estará concluído quando:

- todas as tarefas de [tasks.md](specs/001-telemetry-ingestion/tasks.md) estiverem implementadas e marcadas;
- os contratos implementados forem compatíveis com os documentos normativos;
- todos os testes automatizados e cenários do quickstart passarem;
- os benchmarks de idempotência, carga, burst, soak e storage estiverem documentados;
- a recuperação após falhas de dependência estiver comprovada;
- os controles técnicos de privacidade e segurança estiverem revisados;
- os dashboards internos e restritos renderizarem dados autorizados corretamente;
- empresas desconhecidas e viewers fora de escopo permanecerem isolados;
- a execução final de 100.000 eventos reconciliar exatamente o total entre ingestão, armazenamento e visualização;
- cada requisito e cada projeto de produção possuir cobertura automatizada rastreável;
- riscos residuais, especialmente o host local único e a retenção futura de cinco anos, estiverem explicitamente registrados.

## Documentação do projeto

- [Especificação funcional](specs/001-telemetry-ingestion/spec.md)
- [Plano técnico e arquitetura](specs/001-telemetry-ingestion/plan.md)
- [Modelo de dados](specs/001-telemetry-ingestion/data-model.md)
- [Pesquisa e decisões técnicas](specs/001-telemetry-ingestion/research.md)
- [Guia de validação](specs/001-telemetry-ingestion/quickstart.md)
- [Plano completo de tarefas](specs/001-telemetry-ingestion/tasks.md)
- [Constituição de engenharia](.specify/memory/constitution.md)

## Princípios que não podem ser negociados

1. Nenhum `202` antes da confirmação de durabilidade.
2. Nenhum processamento de entrada sem limites explícitos.
3. Nenhuma exposição de tenant, identidade ou rejeição sem autorização e auditoria adequadas.
4. Nenhum conflito pode modificar o evento original.
5. Nenhuma alegação de capacidade, durabilidade em cloud, retenção de cinco anos ou conformidade legal sem evidência verificável.

