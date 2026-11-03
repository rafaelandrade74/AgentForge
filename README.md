# AgentForge

Aplicação web auto-hospedada que orquestra o ciclo de desenvolvimento de um produto com agentes de IA. Você cadastra um
projeto (repositório Git), escreve uma demanda em linguagem natural e acompanha, pela interface, o Scrum Master quebrando a
demanda em tarefas, o Dev implementando, o Tech Lead e o Security revisando e o QA testando, até a tarefa chegar a **Done**
com um branch pronto para merge. Cada papel é uma execução do **Claude Code** em modo headless.

Nenhum agente faz push: o trabalho fica no branch `agentforge/task-<id>` do clone local, e o merge é seu.

## Três modos de execução

A aplicação e o CLI do agente podem ficar em lugares diferentes. Escolha o que combina com a sua máquina:

| # | Aplicação | CLI do agente (`claude`, `git`) | Login do Claude | Como subir |
|---|---|---|---|---|
| **1** | Docker | **no seu computador** (host agent) | o que já existe no computador | `scripts/host-agent` + compose com `docker-compose.host-agent.yml` |
| **2** | Docker | **no container** (padrão) | pela tela **Agentes** (link) — ou reaproveitando o do computador | `docker compose up -d` |
| **3** | **tudo no computador**, sem Docker para a aplicação | no computador | o que já existe no computador | `scripts/run-native` |

> **"Já tenho o Claude instalado e logado no computador. Dá para usar?"** Sim: o modo **1** usa o binário e o login do
> computador; o modo **3** roda tudo nele. No modo 2, dá para reaproveitar só o *login* (veja
> [Reaproveitar o login do computador](#reaproveitar-o-login-do-computador-modo-2)).

Em qualquer modo, a tela **Agentes** mostra se o CLI está instalado e conectado, **testa de verdade** com uma chamada
mínima e, se não estiver conectado, conduz o login por link (sem chave de API). Ela também diz *onde* os CLIs estão rodando.

### Modo 2 — Docker com o CLI no container (padrão)

```bash
git clone <este-repositório> agentforge && cd agentforge
cp .env.example .env          # ou ./scripts/init-env.sh para gerar segredos aleatórios
docker compose up -d
```

Abra **http://localhost:8080**, entre com `ADMIN_USERNAME` / `ADMIN_PASSWORD` do `.env` e vá em **Agentes**:

1. Se o Claude Code aparecer **Conectado**, o teste roda sozinho. Pronto.
2. Se aparecer **Não conectado**, clique em **Conectar**, abra o link, entre na sua conta e cole o código na tela.
   O login fica no volume `claude-config` e sobrevive a reinícios. Não precisa de chave de API nem de terminal.

A primeira subida compila as imagens (alguns minutos). `docker compose ps` deve mostrar `db`, `api` e `web` saudáveis.

#### Reaproveitar o login do computador (modo 2)

Se o Claude já está logado no computador, monte a pasta de configuração dele no container, em vez de logar de novo.
No `.env`:

```
CLAUDE_CONFIG_SOURCE=C:/Users/voce/.claude        # ou /home/voce/.claude
```

e `docker compose up -d`. A tela Agentes passa a mostrar **Conectado**. Cuidados:

- Só vale se o login estiver em arquivo (`.credentials.json`): Windows e Linux. No macOS ele fica no Keychain e não é reaproveitável.
- O CLI **dentro do container** passa a ler e **gravar** nessa pasta (inclusive a renovação do token) e a obedecer as
  configurações dela (hooks, plugins). Se isso incomoda, prefira o modo 1 ou o login pela tela.
- No Linux, o container roda como UID 1654: a pasta precisa ser gravável por ele.

### Modo 1 — Docker, mas o `claude` e o `git` do seu computador

Para usar exatamente o `claude` já instalado e logado (e o `git` com as suas credenciais). Um pequeno **host agent** roda
no computador e a API (no Docker) fala com ele por HTTP, autenticado por token:

```powershell
# terminal 1 (deixe aberto)
./scripts/host-agent.ps1                  # Linux/macOS: ./scripts/host-agent.sh

# terminal 2
docker compose -f docker-compose.yml -f docker-compose.host-agent.yml up -d --build
```

Na primeira vez o script cria `HOST_AGENT_TOKEN` e `HOST_WORKSPACES_ROOT` no `.env`. Os clones e worktrees ficam na pasta
`HOST_WORKSPACES_ROOT` **do computador** (padrão `.agentforge/repos`): o `git` e o `claude` rodam lá, então a URL do
repositório de um projeto e os caminhos são os do computador. A tela Agentes mostra "CLI no seu computador".

Segurança do host agent: escuta só em `127.0.0.1:8787`; exige o token; só executa `claude` e `git`; só aceita pastas
dentro de `HOST_WORKSPACES_ROOT` e nunca apaga a própria raiz. Quem tem o token (o administrador do AgentForge) consegue
mandar o `claude` rodar nessa pasta — o mesmo que já podia fazer pela aplicação — mas **proteja o token**. No Linux com
Docker nativo, o container não alcança `127.0.0.1` do host: rode `./scripts/host-agent.sh "" http://0.0.0.0:8787` (e
restrinja a porta por firewall).

### Modo 3 — tudo no computador

Sem Docker para a aplicação: a API roda nativa, serve a interface e usa o `claude`, `git` e `dotnet` do computador.
Requisitos: .NET SDK 8+, Node 20+, git, `claude` no PATH e um **PostgreSQL**.

```powershell
./scripts/run-native.ps1 -StartPostgres    # Linux/macOS: ./scripts/run-native.sh --start-postgres
```

`-StartPostgres` sobe só o banco num container (`agentforge-native-db`); se você já tem um PostgreSQL, omita e ajuste
`NATIVE_DB_CONNECTION` no `.env`. A interface fica em **http://localhost:8080** (`-Urls` para mudar) e os clones em
`.agentforge/repos`. O script compila a interface na primeira vez (`-Rebuild` para recompilar).

## Controlar o AgentForge pelo Claude Code (MCP)

O AgentForge é também um **servidor MCP** (HTTP, `POST /mcp`): o Claude Code, ou qualquer cliente MCP, pode criar projetos e
demandas, acompanhar tarefas, ler o diff e **aprovar ou rejeitar** entregas por conversa. Funciona nos três modos de execução.

1. Abra **Integração** na interface e crie um **token de acesso** (o valor aparece uma única vez). Ele vale **só no `/mcp`**,
   não abre o resto da API, é guardado como hash e pode ser revogado ali mesmo.
2. Registre o servidor (a tela já gera o comando com o seu token e o seu endereço):

   ```bash
   claude mcp add --transport http agentforge http://localhost:8080/mcp --header "Authorization: Bearer afk_..."
   ```

   Para outros clientes, o arquivo `.mcp.json`: `{"mcpServers":{"agentforge":{"type":"http","url":"http://localhost:8080/mcp","headers":{"Authorization":"Bearer afk_..."}}}}`.
3. Converse: *"crie uma demanda no projeto X para adicionar o filtro por status e inicie"*, *"o que depende de mim?"*,
   *"mostre o diff da tarefa e aprove"*.

Ferramentas (18): `list_projects`, `get_project`, `create_project`, `create_demand`, `start_demand`, `approve_plan`, `get_demand`,
`list_tasks`, `get_task`, `get_task_diff`, `get_run_output`, `approve_task`, `reject_task`, `retry_task`, `cancel_task`,
`list_pending_actions`, `list_agents`, `test_agent`. As de leitura são marcadas como somente leitura e `cancel_task` como
destrutiva, para o cliente pedir confirmação. **Não existe ferramenta que faça push, merge ou apague projeto.** Atrás de um
proxy reverso, repasse `/mcp` à API e use HTTPS (o token trafega no cabeçalho). `start_demand`/`create_demand` com `start` e
`test_agent` gastam créditos do agente.
## Primeiro uso

1. **Agentes:** confira que o Claude Code está **Conectado** e o teste passou.
2. **Projetos → Novo projeto:** nome, URL do repositório Git, branch base e (opcional) comandos de build e teste.
3. **Nova demanda → Criar e iniciar refinamento.** O Scrum Master lê o repositório e propõe as tarefas.
4. **Aprovar plano** na tela da demanda. O quadro do projeto mostra as tarefas andando; clique numa tarefa para ver o log ao
   vivo, o diff e as execuções.
5. Ao chegar em *Aguardando aprovação*, revise o diff e **aprove** ou **rejeite com comentário**. Depois do `Done`, faça o
   merge do branch `agentforge/task-<id>` como preferir.

O projeto pode ser marcado como **modo automático** (sem as duas aprovações humanas). Tarefas que voltam três vezes ao Dev
ficam *Bloqueadas* esperando a sua decisão (*Reexecutar* ou *Cancelar*). O agente de cada papel é escolhido na tela **Agentes**
e os prompts, na tela **Prompts**.

Há um projeto de exemplo (API de tarefas em .NET, com testes) em `examples/tasks-api`. No modo 2:
`docker compose exec api sh /examples/make-sample-repo.sh` cria `/data/repos/_sample/tasks-api.git`; use essa URL no projeto.

### Repositórios privados

- **HTTPS com token:** use `https://<token>@github.com/org/repo.git`. O token fica na URL do projeto (banco) e na configuração do clone.
- **Modos 1 e 3:** o `git` é o do computador, com as credenciais que você já configurou (credential manager, chave SSH).
- **Modo 2 com SSH:** a imagem tem o cliente `ssh`, mas a montagem de chaves não faz parte do compose e **não foi testada**.

## Configuração (`.env`)

| Variável | Padrão | Para quê |
|---|---|---|
| `ADMIN_USERNAME`, `ADMIN_PASSWORD` | `admin` / *(exemplo)* | Usuário único, criado no start (senha só como hash BCrypt) |
| `JWT_SECRET` | *(exemplo)* | Assinatura dos tokens; mínimo de 32 caracteres |
| `POSTGRES_USER`, `POSTGRES_PASSWORD` | `agentforge` / *(exemplo)* | Banco. Só são lidos na criação do volume |
| `ANTHROPIC_API_KEY` | vazio | Opcional: chave de API em vez do login de conta |
| `CLAUDE_CONFIG_SOURCE` | volume `claude-config` | Pasta do computador com um login existente (modo 2) |
| `HOST_AGENT_TOKEN`, `HOST_WORKSPACES_ROOT`, `HOST_AGENT_URL` | — | Modo 1 (preenchidos por `scripts/host-agent`) |
| `NATIVE_DB_CONNECTION` | Postgres local | Modo 3: connection string do banco |
| `CLAUDE_CODE_VERSION` | `2.1.316` | Versão do CLI instalada na imagem (modo 2) |
| `ORCHESTRATOR_CONCURRENCY` | `1` | Tarefas simultâneas; com mais de 1, Tech Lead e Security revisam em paralelo |
| `WEB_PORT`, `API_DEBUG_PORT` | `8080`, `8081` | Portas (a API só em `127.0.0.1`) |
| `SWAGGER_ENABLED` | `true` | Documentação em `/swagger` |
| *(tela Integração)* | — | Tokens de acesso do servidor MCP (`/mcp`) |

> Os valores de exemplo do `.env.example` bastam para experimentar, mas a API avisa no log enquanto estiverem em uso.
> **Troque `ADMIN_PASSWORD`, `JWT_SECRET` e `POSTGRES_PASSWORD` antes de expor o sistema na rede.**
> `./scripts/init-env.sh` (Linux/macOS) ou `./scripts/init-env.ps1` (Windows) criam o `.env` já com segredos aleatórios.

Os prompts de sistema dos papéis ficam em `prompts/<papel>.md` (semeiam o primeiro start) e são editados depois pela tela
**Prompts**; ficam no banco.

## Atrás de um proxy reverso

O front (`web`, Nginx) já repassa `/api`, `/hubs` (SignalR/WebSocket) e `/swagger` para a API. Aponte o seu proxy para
`web:8080` e **habilite WebSocket** em `/hubs/`. Exemplo (Nginx):

```nginx
server {
    server_name agentforge.exemplo.local;
    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_read_timeout 1h;
    }
}
```

Use HTTPS no proxy: o login e o token trafegam por ele.

## Dados, backup e atualização

Volumes nomeados: `pgdata` (banco), `repos` (clones e worktrees, modo 2) e `claude-config` (login do Claude Code, modo 2).

```bash
# backup do banco
docker compose exec -T db pg_dump -U agentforge agentforge > backup.sql
# restaurar (banco vazio)
docker compose exec -T db psql -U agentforge agentforge < backup.sql

# atualizar o AgentForge
git pull && docker compose up -d --build     # migrations aplicam sozinhas no start
```

`docker compose down` mantém os dados; `docker compose down -v` **apaga** banco, clones e login.

## Segurança do container

`db`, `api` e `web` rodam sem root (a API como UID 1654, o front com `nginx-unprivileged`), com `no-new-privileges` e sem
capabilities (`cap_drop: ALL`); o front tem sistema de arquivos somente leitura. Só o `web` é publicado na rede; o Postgres
não é exposto. No modo 2, os agentes executam comandos (build, testes) **dentro do container da API**; nos modos 1 e 3,
**no seu computador**, dentro do worktree da tarefa — rode o AgentForge com repositórios em que você confia.

## Problemas comuns

| Sintoma | Causa e solução |
|---|---|
| Faixa amarela "não está conectado" | Abra **Agentes** e conecte (link) ou use um dos outros modos |
| Execuções falham em segundos | Claude Code sem login: veja a tela Agentes; a saída da execução mostra o motivo |
| Modo 1: "Host agent inacessível" | O `scripts/host-agent` não está rodando, a porta/URL difere de `HOST_AGENT_URL`, ou (Linux) ele escuta só em loopback |
| Modo 1: "recusou o token" | `HOST_AGENT_TOKEN` do `.env` mudou depois de iniciar o host agent: reinicie os dois |
| `api` reinicia / "password authentication failed" | Você trocou `POSTGRES_PASSWORD` depois da primeira subida: `docker compose down -v` (apaga os dados) |
| O quadro não atualiza sozinho | WebSocket bloqueado no proxy: habilite `Upgrade` em `/hubs/` (cai para long polling, mais lento) |
| `defina ADMIN_PASSWORD no .env` | Falta o `.env`: `cp .env.example .env` |
| Porta 8080 ocupada | Mude `WEB_PORT` (modo 2) ou `-Urls` (modo 3) |

## Desenvolvimento

```bash
# testes do backend (Postgres via Testcontainers; rode no SDK 8)
docker run --rm -v "$PWD/backend:/src" -v /var/run/docker.sock:/var/run/docker.sock \
  -e TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal -e TESTCONTAINERS_RYUK_DISABLED=true \
  -w /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet test -o /tmp/out

# front: ng serve (proxy para a API em 127.0.0.1:8081) e testes
cd frontend && npm ci && npx ng serve
cd frontend && npx ng test --watch=false --browsers=ChromeHeadless
```

Estrutura: `backend/` (.NET 8: Api, Domain, Infrastructure, Orchestrator, Execution, HostAgent, Tests), `frontend/` (Angular),
`prompts/`, `examples/`, `scripts/`, `docs/adr/` (decisões de arquitetura), `docker-compose.yml`, `docker-compose.host-agent.yml`.
