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
0 Fundação do repositório (feita) · 1 Autenticação (JWT + refresh, admin por env, BCrypt) · 2 Domínio e persistência (EF Core, CRUD, Swagger) · 3 CliAgentRunner e ClaudeCodeAdapter · 4 Workspaces Git · 5 Orquestrador · 6 Tempo real (SignalR `/hubs/runs`) · 7 Interface web · 8 Deploy homelab · 9 Validação ponta a ponta.

## Regras
Siga as fases em ordem; não pule critérios de aceite; código simples e testável; registre decisões em `docs/adr/`; pergunte antes de adicionar dependências fora da stack.

## Comandos
- Testes backend (máquina só com SDK 10): `$env:DOTNET_ROLL_FORWARD='Major'; dotnet test backend`
- Subir tudo: `cp .env.example .env && docker compose up --build` (front em http://localhost:8080)
