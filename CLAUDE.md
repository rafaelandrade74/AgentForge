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
0 Fundação do repositório (feita) · 1 Autenticação (feita) · 2 Domínio e persistência (feita; ADR 0003) · 3 CliAgentRunner (feita; ADR 0004) · 4 Workspaces Git (feita; ADR 0005) · 5 Orquestrador (feita; ADR 0006) · 6 Tempo real (feita; ADR 0007) · 7 Interface web (feita; ADR 0008) · 8 Deploy homelab (feita; ADR 0009, README) · 9 Validação ponta a ponta (feita; ADR 0010 e ADR 0011 — com lacunas a corrigir: tarefas dependentes devem nascer das branches das dependências). · 10 Ambientes de execução (feita; ADR 0017) · 11 Git e login por ambiente (feita; ADR 0018) — próximas: 12 Perfis de stack e container por tarefa · 13 Ambiente por papel e fila · 14 Validação (plano em `docs/plano-ambientes.md`).

## Importar do GitHub (ADR 0014)
Projetos → "Importar do GitHub": lista repositórios pelo token (usado só na chamada, nunca guardado/logado), cria o projeto, autoriza a chave SSH como deploy key somente leitura (privados) e já clona; clone falho desfaz o cadastro. Código em `backend/AgentForge.Api/GitHub`.

## Segredos (ADR 0015)
Processos filhos (`claude`, `git`) nascem sem os segredos da aplicação (`Secrets.ScrubEnvironment`); URLs com token saem mascaradas (`Secrets.MaskUrls`); login limitado (`Auth:LoginAttemptsPerMinute`). Nunca logar nem devolver token/senha.

## MCP (ADR 0012)
O AgentForge é também servidor MCP em `POST /mcp` (18 ferramentas; tokens de acesso `afk_...` criados na tela Integração, válidos só no `/mcp`). Código em `backend/AgentForge.Api/Mcp`, sem dependência nova.

## Credenciais Git (ADR 0013)
Tela **Git**: gera uma chave SSH ed25519 no servidor (modo 2; a privada nunca sai dele), mostra a pública para autorizar no GitHub e testa o acesso. O `git` usa a chave via `GIT_SSH_COMMAND` (só se ela existir). Testes que geram chave usam a imagem `backend/Dockerfile.tests` (`docker build -f backend/Dockerfile.tests -t agentforge-tests backend`).

## Modos de execução (ADR 0010)
1 Docker + CLI no computador (host agent: `scripts/host-agent`, `docker-compose.host-agent.yml`) · 2 Docker + CLI no container (padrão; login pela tela Agentes) · 3 tudo no computador (`scripts/run-native`). Todo processo de CLI (agentes, git) passa pelo `IProcessLauncher` (`AgentForge.Execution`); `Agents:Launcher` = `local` | `host`.

## Regras
Siga as fases em ordem; não pule critérios de aceite; código simples e testável; registre decisões em `docs/adr/`; pergunte antes de adicionar dependências fora da stack.

## Comandos
- Testes backend (precisam do Docker para o Postgres via Testcontainers; o TestServer 8 quebra no runtime 10, então rode no SDK 8): `docker run --rm -v "${PWD}/backend:/src" -v //var/run/docker.sock:/var/run/docker.sock -e TESTCONTAINERS_RYUK_DISABLED=true -w /src agentforge-tests sh -c 'export TESTCONTAINERS_HOST_OVERRIDE=$(getent ahostsv4 host.docker.internal | head -1 | cut -d" " -f1); dotnet test -o /tmp/out'` (forçar IPv4 evita falhas intermitentes de conexão ao Postgres do Testcontainers)
- Teste real do Claude (gasta créditos): `AGENTFORGE_CLAUDE_INTEGRATION=1` + `AGENTFORGE_TEST_DB=<postgres>` e `dotnet test --filter Real_claude` (com `DOTNET_ROLL_FORWARD=Major` no SDK 10).
- Nova migration: `dotnet ef migrations add <Nome> --project backend/AgentForge.Infrastructure --output-dir Persistence/Migrations` (com `DOTNET_ROLL_FORWARD=Major` se só houver o SDK 10). Swagger em `/swagger`.
- Se mudar `POSTGRES_PASSWORD` depois do primeiro start, recrie o volume (`docker compose down -v`): o Postgres só lê a senha ao inicializar.
- Testes frontend: `cd frontend; ng test --watch=false --browsers=ChromeHeadless` (defina `CHROME_BIN` se não houver Chrome)
- Auth (Fase 1): usuário admin e JWT vêm de `ADMIN_USERNAME`, `ADMIN_PASSWORD`, `JWT_SECRET` no `.env`; refresh tokens ficam no banco (hash)
- Subir tudo: `cp .env.example .env && docker compose up --build` (front em http://localhost:8080)
- Banco opcional: o `db` do compose está no profile `local-db` (`COMPOSE_PROFILES=local-db` no `.env`, padrão). Para usar um Postgres existente: `COMPOSE_PROFILES=` vazio + `DATABASE_URL=<string Npgsql>`. `.env` antigo precisa da linha `COMPOSE_PROFILES=local-db`.
- Configuração: `Agents:Launcher` (`local`|`host`) define o modo de execução; referência completa das chaves no README ("appsettings.json"). Migrations rodam sozinhas no start (`MigrateAsync` no `Program.cs`).

## Dependências dos agentes (ADR 0016)
No modo 2 o usuário `app` do container da API tem sudo sem senha (`AGENT_SUDO=true`, `API_NO_NEW_PRIVILEGES=false`): agentes instalam apt/Node/Python no container (efêmero; o permanente vai no `backend/Dockerfile`). Endurecer: `AGENT_SUDO=false` + `API_NO_NEW_PRIVILEGES=true`. Modos 1 e 3 usam as permissões do seu usuário no computador.

## Ambientes de execução (ADR 0017)
Tela **Ambientes**: máquinas com host agent cadastradas (URL, token, pasta de clones). `Project.EnvironmentId` (nulo = padrão do servidor, `Agents:Launcher`). `IEnvironmentLaunchers` entrega launcher + raiz de clones por projeto (usado por `WorkspaceManager`, `CliAgentRunner`, `WorkItemProcessor`). O token fica no banco protegido pelo Data Protection (chaves no volume `dp-keys`, `DataProtection:KeysDir`) e nunca sai pela API. `/api/environments` é só JWT. Código em `backend/AgentForge.Orchestrator/Environments` e `AgentForge.Api/Endpoints/EnvironmentEndpoints.cs`.

## Git e login por ambiente (ADR 0018)
Telas **Git** e **Agentes** têm seletor de ambiente (`?environmentId=` em `/api/git/ssh` e `/api/agents`; inexistente = 409, nunca cai no servidor). O host agent gera e guarda a chave SSH da própria máquina (`HostAgent:SshDir`, rotas `/v1/ssh*`; `SshKeyStore` em `AgentForge.Execution`) e o `git` dele a usa se existir. `GitCredentialService` e `AgentCliService` resolvem o ambiente via `IEnvironmentLaunchers`; caches e sessões de login são por ambiente. O aviso global da barra reflete só o servidor.
