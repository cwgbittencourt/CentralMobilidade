# Ingestão em lotes e análise geográfica paralela no mesmo Worker

Data: 17/10/2026. Situação: proposta discutida como ponto de partida, ainda sem
aprovação de implementação ou alteração dos requisitos vigentes.

Este registro reúne o contexto completo da evolução discutida: recebimento em
lotes, persistência durável, processamento geográfico e consulta dos resultados.
O cache Redis é uma possibilidade adicional, não uma condição para esse fluxo.
Nenhuma task é criada por este documento. Não implementar a proposta nem alterar
o Docker Compose antes de uma nova decisão explícita.

## Contexto e objetivo

A frota de referência informada tem 11.462 veículos ativos cadastrados: JCA 7.905,
ADTSA 438, COMPORTE 838, SING 1.044 e ÁGUIA BRANCA 1.237. Cadastro ativo não significa
transmissão simultânea. Frequência, picos, reconexão e crescimento ainda dependem
do levantamento com o mantenedor.

A intenção é receber e armazenar telemetria em lotes e, em paralelo, verificar
quebras de cerca e outros eventos geográficos, mantendo a implantação inicialmente
no mesmo executável e container Worker. Não é necessário introduzir uma segunda
fila RabbitMQ ou outro container de processamento para essa opção.

O transporte gzip por lote, a ingestão e o agrupamento de escritas no InfluxDB
pertencem à base existente. A integração com Tryvia.GeoAdapter, os jobs geográficos,
o banco espacial e as consultas de resultados descritos a seguir são evolução
proposta, não recursos já implementados.

## Fluxo proposto

1. **API recebe o lote.** O produtor interno envia eventos em gzip com a credencial
   compartilhada de ingestão. A API valida o envelope e aplica limites de bytes,
   duração e admissão, preservando o processamento detalhado das linhas no Worker.
2. **RabbitMQ confirma a publicação.** A API publica o gzip como corpo nativo de
   uma mensagem persistente, aguarda a confirmação durável e conclui o estado de
   aceitação antes de responder HTTP `202` com o identificador do lote. Não responder
   sucesso e só depois tentar persistir a mensagem.
3. **Serviço de ingestão processa.** Consome a mensagem, descompacta com limites,
   valida cada evento, aplica idempotência e proteção de conflitos e grava os pontos
   válidos no único InfluxDB. Resultados e rejeições seguem os controles existentes.
4. **Registra a análise pendente.** Após conciliar a persistência dos eventos,
   registra os jobs geográficos elegíveis na mesma transação PostgreSQL que conclui
   os estados correspondentes. Chaves únicas e recuperação de reentrega impedem
   perda de agendamento ou criação indevida de trabalhos duplicados.
5. **Confirma a mensagem original.** O ACK exige os resultados operacionais e os
   jobs duráveis. Não aguarda a análise espacial. Falha ao registrar o job exige
   recuperação; não pode ser tratada como agendamento concluído.
6. **Serviço geo executa em paralelo.** Outro BackgroundService, dentro do mesmo
   Worker, obtém jobs elegíveis no PostgreSQL com posse controlada e concorrência
   própria. Recebe os pontos necessários do job ou busca contexto no InfluxDB.
   Chama o Tryvia.GeoAdapter para avaliar cercas e outros eventos usando PostGIS.
7. **Persiste resultados geográficos.** O serviço grava ocorrências, progresso e
   versão de análise de forma recuperável e depois conclui o job. O repositório
   desses resultados é responsabilidade da aplicação, não uma gravação automática
   feita pelo GeoAdapter.
8. **API e Grafana apresentam.** Consultam telemetria e resultados autorizados,
   distinguindo ingestão concluída, análise pendente, análise falha e análise
   concluída. A disponibilidade de um ponto no mapa não significa que sua análise
   geográfica já terminou.

## Lotes e concorrência

O valor de **5.000 eventos por lote** foi sugerido como hipótese de teste, não
como tamanho definitivo ou garantia de 5.000 eventos por segundo. Uma mensagem
RabbitMQ pode conter esse lote gzip; não equivale a 5.000 mensagens individuais.
Prefetch limita mensagens não confirmadas, não linhas dentro do gzip.

Separar três parâmetros: eventos por mensagem de entrada, quantidade de mensagens
em processamento e pontos por chamada de escrita no InfluxDB. Escritas também
precisam de limites de bytes e tempo; não esperar indefinidamente completar 5.000
pontos. Um lote não deve disparar 5.000 tarefas simultâneas.

Paralelizar veículos diferentes com limites explícitos. Coordenar por empresa e
veículo quando o processamento depender da sequência temporal ou do estado da
cerca. Ordenar pelo horário do evento, tratando contexto entre lotes, duplicidades,
eventos atrasados e reprocessamento. Apenas ordenar as linhas de um lote não resolve
concorrência entre lotes do mesmo veículo.

## Dois serviços internos com responsabilidades próprias

O mesmo host registra os dois BackgroundServices com scopes de dependências,
concorrência, cancelamento e tratamento de falhas próprios. Não utilizar uma Task
solta ou apenas uma fila em memória para guardar trabalhos necessários à correção.

Jobs precisam de estado, tentativas, próxima execução, lease, proprietário e fencing.
Adquirir a posse em transação curta e não manter bloqueios SQL durante toda a análise.
Mais de uma instância do Worker exige controle de posse entre processos, além da
coordenação por veículo. Falhas transitórias devem agendar nova tentativa; trabalhos
que esgotarem tentativas precisam permanecer inspecionáveis e reprocessáveis.

CPU, memória, conexões e reinício do processo são compartilhados. Concorrências
separadas não reservam recursos fisicamente. Medir o consumo combinado e limitar
ou pausar análise sob pressão para proteger a ingestão. Uma falha não tratada de
BackgroundService pode encerrar o host inteiro e precisa de política explícita.

## Persistência e recuperação

O agendamento proposto é uma tabela de jobs no PostgreSQL de controle existente.
Não há relay de publicação ou fila RabbitMQ geográfica nessa opção. O banco espacial
proposto guarda cercas e resultados/progresso; definir schema, índices, migrations,
permissões e backup antes de adotá-lo.

Não existe transação distribuída entre InfluxDB, PostgreSQL de controle e banco
espacial. Preservar a conciliação do guard após uma escrita ambígua no InfluxDB.
Na etapa geo, queda após gravar resultados e antes de concluir o job pode repetir
a análise: resultados exigem chaves idempotentes, controle de versão e proteção
contra gravação por proprietário com fencing expirado.

Se o job referenciar pontos do InfluxDB, definir prazo de processamento e tratamento
de referências expiradas. Se carregar pontos, definir tamanho, proteção e retenção
desse conteúdo. Não presumir que todo o histórico precisa ser duplicado no PostgreSQL.

## Papel do Tryvia.GeoAdapter

A biblioteca é chamada pelo serviço geo no mesmo processo .NET. Não recebe
mensagens RabbitMQ diretamente e não consulta InfluxDB. O serviço fornece posições
e contexto, e a biblioteca executa as operações espaciais no provider configurado.

Na opção discutida, PostGIS seria preparado para cercas e análises espaciais.
Adicionar um serviço de banco espacial é distinto de adicionar outro Worker.
O aproveitamento de infraestrutura existente precisa de avaliação específica.

Antes de processar trajetórias multiempresa, resolver o isolamento por empresa no
contrato/executor espacial examinado e o mapeamento entre `empresa_id` textual e
proprietário inteiro da biblioteca. A autorização da API não substitui isolamento
durante o processamento. Esses controles não introduzem cadastro obrigatório de
empresa para a ingestão aceitar um identificador válido.

## Evolução opcional com cache

Avaliar Redis para o último contexto efetivamente analisado por empresa e veículo,
com TTL renovável inicial de 24 horas e recuperação de histórico/progresso durável
na ausência ou incompatibilidade do cache. O cache não será a fonte oficial das
ocorrências ou do progresso.

Regras temporais, permanência, atualização atômica, fallback e perguntas em aberto
estão no [registro específico do cache](geo-analysis-context-cache.md). A sugestão
de última posição ou até dez posições anteriores é uma hipótese a validar, não
uma regra suficiente para todos os eventos geográficos.

## Por que esta opção foi considerada

Combina implantação inicial simples, recuperação de trabalhos após reinício e
controle próprio da concorrência geo. A indisponibilidade espacial não exige que
cada mensagem de ingestão espere a análise, desde que o job já esteja durável.
Ainda assim, backlog de jobs pode pressionar os bancos e o host compartilhados.

Se medições justificarem, o serviço geo poderá ser separado em outro processo no
futuro, preservando o contrato durável de jobs. Outra fila só deve ser introduzida
por necessidade demonstrada, não como requisito desta proposta inicial.

## Validação e decisões pendentes

- Confirmar o perfil operacional com o mantenedor e definir prazo próprio para
  disponibilidade dos resultados geográficos.
- Medir tamanhos de lote, concorrências, transações PostgreSQL, custo espacial e
  carga combinada, incluindo cenários com e sem cache.
- Definir quais eventos e cercas serão avaliados, versionamento, ruído de GPS,
  ordenação, janela de atraso, permanência e reprocessamento.
- Testar quedas nas fronteiras de persistência e agendamento, expiração de lease,
  fencing, duplicidades, falta de PostGIS, shutdown e retomada de jobs.
- Definir observabilidade, privacidade, retenção, health e consulta de pendências.

A **T094 permanece reprovada e aberta**. Este fluxo não corrige automaticamente o
déficit medido nem aprova capacidade. Esta documentação registra a direção discutida
para avaliação posterior, sem adicionar tasks, mudar o plano de execução ou alterar
implementação e Compose.
