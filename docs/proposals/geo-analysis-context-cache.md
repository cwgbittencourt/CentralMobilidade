# Cache de contexto para análise geográfica

Data: 17/10/2026. Situação: ideia em estudo, sem decisão de implementação.

Registrar a possibilidade de usar Redis para reduzir consultas repetidas ao InfluxDB
durante a análise geográfica. Este registro não aprova a arquitetura, não cria tasks,
não altera requisitos vigentes e não autoriza implementação ou inclusão de serviços
no Docker Compose. A ideia precisa ser discutida e medida antes de ser adotada.

## Contexto da discussão

Este documento detalha somente a evolução de cache. O fluxo completo, incluindo
API, RabbitMQ, lotes, persistência, jobs e consultas, está na
[proposta de ingestão e análise paralela](parallel-ingestion-and-geo-analysis.md).

A proposta geográfica discutida prevê o mesmo executável e container Worker com
dois serviços internos concorrentes: ingestão e análise geográfica, com limites
próprios. Jobs duráveis no PostgreSQL permitiriam recuperar análises pendentes sem
introduzir outra fila RabbitMQ ou outro container de processamento.

Essa integração com Tryvia.GeoAdapter, seus jobs e o armazenamento espacial também
é uma proposta, não funcionalidade já implementada. O GeoAdapter receberia posições
fornecidas pelo serviço de análise; não acessaria o InfluxDB diretamente.

## Evolução sugerida

Utilizar Redis como cache reconstruível do contexto recente de cada veículo. O
serviço geográfico tentaria obter esse contexto no cache e, se ausente ou
incompatível, recuperaria os dados necessários do histórico e do progresso durável.

A hipótese inicial é uma expiração renovável de 24 horas, explicitamente aplicada
após atualização válida. Esse prazo significa validade desde a atualização, não
armazenamento de todas as posições das últimas 24 horas. Ainda não foi validado.

O contexto poderá incluir:

- chave por `empresa_id` e `veiculo_id`, sem usar placa como identidade principal;
- última posição efetivamente analisada, com coordenadas e horário do evento;
- versão ou identificador do progresso da análise;
- cercas relevantes, estado dentro/fora e início de permanência, conforme as regras;
- versão das cercas e das regras quando necessária para invalidar o contexto.

É essencial distinguir a última posição recebida ou armazenada da última posição
processada pelo serviço geográfico. A ingestão pode estar adiantada em relação à
análise; um ponto futuro não pode ser usado como posição anterior de um evento.

## Recuperação e correção a avaliar

Na falta do cache, buscar contexto anterior ao início do trecho analisado, por
empresa e veículo. A sugestão de obter a última posição ou até dez posições é uma
hipótese: quantidade, janela e critérios de suficiência dependem da regra e precisam
ser definidos. Não buscar simplesmente a posição mais recente de todo o histórico
ao analisar eventos atrasados.

Posições anteriores podem ajudar a detectar transições de cerca, mas não garantem
reconstruir o início de uma permanência. Resultados, progresso e estado necessário
à correção devem ser duráveis no banco geográfico proposto, permitindo reconstruir
o cache após expiração ou perda do Redis.

A hipótese de execução é persistir resultados e progresso duráveis antes de
atualizar o cache e concluir o job. Isso não constitui uma transação entre Redis e
os bancos: reentrega, falhas intermediárias e cache desatualizado exigem validação
de versão e resultados idempotentes. Cache não pode ser a única evidência de
conclusão de um job ou de emissão de uma ocorrência.

Eventos fora de ordem não devem sobrescrever contexto mais novo. Avaliar atualização
atômica por versão/horário, coordenação por empresa e veículo, leases e fencing,
incluindo workers concorrentes e retomada após falha. Uma atualização atômica no
Redis não resolve sozinha a consistência com o progresso persistido.

## Pontos abertos para discussão

- Benefício real frente à consulta direta ao InfluxDB e/ou ao progresso durável.
- Necessidade do Redis frente a um cache local limitado, considerando múltiplas
  instâncias, reinícios e coordenação.
- TTL adequado e o que pode renová-lo; distinguir atualização válida, leitura e
  reprocessamento de eventos antigos.
- Dados mínimos no cache e no job, limites de tamanho e retenção.
- Política para eventos atrasados, contexto insuficiente, empates de horário e
  mudança de cercas ou regras.
- Reconstrução de permanências, invalidação e recuperação de cache divergente.
- Comportamento com Redis indisponível e limites do fallback para evitar sobrecarga
  do InfluxDB ou do banco geográfico.
- Proteção de coordenadas, autenticação, rede, logs e política de persistência do
  Redis, caso seja adotado.
- Impacto operacional de um serviço adicional e orçamento de CPU, RAM e conexões
  compartilhado com ingestão e análise.

## Evidências desejáveis antes de decidir

Comparar o fluxo sem cache e com cache em carga representativa, medindo acertos,
latência, consultas evitadas, memória, atraso dos jobs e impacto na ingestão.
Verificar perda completa do cache, indisponibilidade, eventos fora de ordem,
concorrência, reentrega e atualização das cercas sem perda ou duplicação de
ocorrências.

Esta evolução não resolve nem substitui os ajustes e a revalidação da T094. Nenhuma
aprovação de capacidade decorre deste registro. Uma eventual adoção depende de
nova decisão explícita; até lá, não incluir tasks ou dependência obrigatória de Redis.
