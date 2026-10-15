# Concorrência do ciclo de vida (T055)

BatchLifecycleTests usa o PostgresBatchStateStore real e as migrações existentes.
A classe compartilha um PostgreSQL descartável; cada cenário cria BatchIds próprios.
Cada concorrente usa uma instância do adaptador e um DbContext independente, sem lock
em memória no teste. Uma barreira libera as operações concorrentes, com prazo de 30 s.
O fixture mantém fsync e synchronous_commit habilitados. O override TELEMETRY_TEST_POSTGRES
continua disponível e usa um banco isolado por UUID; não apaga bancos ou volumes existentes.

Os 18 casos verificam:

- Páginas sobrepostas contam cada linha, rejeição e metadado de quarentena uma única vez.
- Páginas contraditórias têm um vencedor e rollback integral da perdedora, sem metadados órfãos.
- Repetições idênticas da conclusão preservam contadores, timestamp e versão.
- Comandos concorrentes de retry, falha e conclusão têm um único vencedor otimista.
- Os seis estados terminais não permitem transições posteriores; repetir o comando original é seguro.
- Resultados existentes podem ser reentregues após o término, mas linhas novas são recusadas.
- Dois contextos EF não podem sobrescrever a mesma versão silenciosamente.
- Cancelamento não grava resultados, e um snapshot anterior a novas linhas não pode finalizar o lote.
- Retomar Processing após Retrying deve preservar o primeiro início e aceitar o replay do comando.

Validação inicial no Windows: 17 aprovações e uma falha TDD ativa, sem skips. A falha é
Concurrent_resume_replays_keep_the_original_start_time_and_one_retry_increment, marcada
com PendingImplementation=T058. IsExactReplay compara o horário de retomada com
ProcessingStartedAt, que corretamente conserva o início original. Por isso, o primeiro
comando de retomada retorna true e seus sete replays retornam false. A T058 deve corrigir
essa classificação sem reiniciar o timestamp nem incrementar RetryCount novamente.

A regressão Linux completa confirmou 338 aprovações e somente essa falha em 339 testes
de integração funcionais, sem skips (benchmark ADR-001 separado). Contratos: 80/80;
arquitetura: 47/47. A solução compilou com zero avisos e erros. Nenhum comportamento
de produção foi alterado pela T055.

```powershell
dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests --filter FullyQualifiedName~BatchLifecycleTests
```

Esses testes validam persistência e concorrência, não a projeção HTTP, autorização,
limpeza de retenção, throughput ou durabilidade de conteúdo bruto. A quarentena é
representada aqui por seus metadados transacionais. Relatórios locais ficam ignorados
em artifacts/validation/T055/. A falha TDD permanece visível na suíte padrão até a T058.

## T057 — inspeção autorizada de lotes

`BatchInspectionTests` exercita PostgreSQL migrado, o autorizador real com perfis
revogáveis, o armazenamento de quarentena real e a auditoria PostgreSQL real.
São 43 casos para propriedade do produtor, permissões e escopo completo/incompleto,
paginação limitada com cursor autenticado vinculado ao lote e com validade,
revogação entre páginas, fronteira superior estável e ausência de conteúdo protegido.
A recuperação bruta cobre permissão humana, vínculo lote/quarentena, arquivo ausente
ou corrompido, expiração, exclusão, recibo de auditoria inválido e falha de commit.
Barreiras de auditoria comprovam que não há abertura antes do commit e que mudanças
de prazo, permissão ou exclusão durante a espera impedem a entrega.

A chave de cursor deve ser provisionada no ambiente da API; os testes usam apenas
uma chave sintética. Ausência e configuração inválida falham sem impedir a consulta
de visibilidade. Os endpoints HTTP serão conectados nas T059/T060. Os relatórios
locais ficam ignorados em `artifacts/validation/T057/`.
