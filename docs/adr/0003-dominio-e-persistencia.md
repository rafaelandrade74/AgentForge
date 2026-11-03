# ADR 0003 — Domínio e persistência

## Decisão
- EF Core 8 + Npgsql. `AppDbContext` fica em `AgentForge.Infrastructure`; as entidades, em `AgentForge.Domain` (POCOs sem dependências).
- Migrations ficam em `Infrastructure/Persistence/Migrations` e são aplicadas sozinhas no start da API (`MigrateAsync`). Para criar uma nova: `dotnet ef migrations add <Nome> --project backend/AgentForge.Infrastructure --output-dir Persistence/Migrations` (ferramenta local em `dotnet-tools.json`; usa `DesignTimeDbContextFactory`, então não precisa de banco).
- Enums são gravados como texto (legíveis no banco, seguros contra reordenação) e serializados como string na API.
- Dependências entre tarefas: tabela de junção `WorkItemDependencies`. Excluir uma tarefa remove os vínculos que apontam para ela.
- `AgentRoleConfig` tem um registro por papel (índice único). No primeiro start, cada papel ausente é criado com o prompt de `prompts/<papel>.md` (montado em `/app/prompts` no compose). Depois disso o banco é a fonte da verdade e a edição pela interface (Fase 7) não é sobrescrita.
- `AgentRun` só tem leitura na API: quem cria é o orquestrador (Fase 5).
- Os refresh tokens da Fase 1 foram para o banco (só o hash SHA-256), substituindo o armazenamento em memória do ADR 0002; reiniciar a API não desloga mais.
- Swagger em `/swagger` (sem autenticação para ler a documentação; os endpoints continuam protegidos).

## Dependências novas
`Npgsql.EntityFrameworkCore.PostgreSQL`, `Microsoft.EntityFrameworkCore.Design`, `Swashbuckle.AspNetCore`, `Testcontainers.PostgreSql` (testes).

## Testes
Testes de repositório e de API rodam contra um Postgres 16 real via Testcontainers. Com só o SDK 10 na máquina, rode dentro do SDK 8 com o socket do Docker (comando no `CLAUDE.md`).

## Estados do WorkItem
O enum atual (`Backlog, Todo, InDevelopment, TechLeadReview, SecurityReview, Qa, AwaitingApproval, Done, Blocked, Cancelled`) é o ponto de partida; a Fase 5 pode ajustá-lo junto com a `WorkflowDefinition`.
