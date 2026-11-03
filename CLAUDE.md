# AgentForge

Aplicação web auto-hospedada (homelab) que orquestra o ciclo de desenvolvimento com agentes de IA. Cada papel (Scrum Master, Dev, Tech Lead, Security, QA) roda como uma instância de CLI de agente em modo headless. No MVP, só o Claude Code.

## Princípios
- Backend C# (.NET 8), frontend Angular 18+ (standalone, signals). Tudo sobe com `docker compose up`.
- Todo agente roda pelo próprio binário CLI, nunca por API. Um único `CliAgentRunner` cuida de processo, streaming, timeout e persistência; cada agente implementa um `ICliAgentAdapter`. Só existe o `ClaudeCodeAdapter` no MVP. O backend não lê nem guarda credenciais de agentes.
- Cada tarefa roda em um git worktree isolado, branch `agentforge/task-<id>`; ninguém faz push para `main`.
- Toda execução é persistida (prompt, saída, custo, duração, status) e transmitida ao vivo.
- Humano no controle: aprovações configuráveis.
- A saída de todo agente termina com um bloco JSON validado; inválida conta como falha e é reexecutada uma vez.

## Estrutura
- `backend/` — solução .NET: Api, Domain, Infrastructure, Orchestrator, Tests
- `frontend/` — Angular
- `prompts/<papel>.md` — prompts de sistema versionados
- `docs/adr/` — decisões de arquitetura

## Fluxo do WorkItem
Transições válidas ficam em tabela no código (`WorkflowDefinition`). Revisão reprovada devolve ao Dev com os apontamentos; no terceiro retorno vai para Blocked. Dependências só liberam a tarefa quando estão em Done.

## Fases (executar uma por vez; parar para validação; commit `fase-N: <resumo>`)
0 Fundação do repositório (feita) · 1 Autenticação (feita) · 2 Domínio (feita) e persistência (EF Core, CRUD, Swagger) · 3 CliAgentRunner e ClaudeCodeAdapter · 4 Workspaces Git · 5 Orquestrador · 6 Tempo real (SignalR `/hubs/runs`) · 7 Interface web · 8 Deploy homelab · 9 Validação ponta a ponta.

## Regras
Siga as fases em ordem; não pule critérios de aceite; código simples e testável; registre decisões em `docs/adr/`; pergunte antes de adicionar dependências fora da stack.

## Comandos
- Testes backend (precisam do Docker para o Postgres via Testcontainers; o TestServer 8 quebra no runtime 10, então rode no SDK 8): `docker run --rm -v "${PWD}/backend:/src" -v //var/run/docker.sock:/var/run/docker.sock -e TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal -e TESTCONTAINERS_RYUK_DISABLED=true -w /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet test -o /tmp/out`
- Nova migration: `dotnet ef migrations add <Nome> --project backend/AgentForge.Infrastructure --output-dir Persistence/Migrations` (com `DOTNET_ROLL_FORWARD=Major` se só houver o SDK 10). Swagger em `/swagger`.
- Se mudar `POSTGRES_PASSWORD` depois do primeiro start, recrie o volume (`docker compose down -v`): o Postgres só lê a senha ao inicializar.
- Testes frontend: `cd frontend; ng test --watch=false --browsers=ChromeHeadless` (defina `CHROME_BIN` se não houver Chrome)
- Auth (Fase 1): usuário admin e JWT vêm de `ADMIN_USERNAME`, `ADMIN_PASSWORD`, `JWT_SECRET` no `.env`; refresh tokens ficam no banco (hash)
- Subir tudo: `cp .env.example .env && docker compose up --build` (front em http://localhost:8080)
