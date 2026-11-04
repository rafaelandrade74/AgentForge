# Primeiros passos — um tutorial para cada cenário

Escolha o cenário que descreve a sua máquina e siga **só** aquele tutorial até o fim. Depois, vá para
[Depois de subir: do primeiro projeto ao merge](#depois-de-subir-do-primeiro-projeto-ao-merge).

| Cenário | Quando escolher |
|---|---|
| [A. Docker completo (padrão)](#a-docker-completo-padrão) | Quer o caminho mais simples: tudo em containers, login do Claude pela interface |
| [B. Docker com o seu Postgres](#b-docker-com-o-seu-postgres) | Já tem um Postgres e não quer outro banco no compose |
| [C. Docker reaproveitando o login do Claude do computador](#c-docker-reaproveitando-o-login-do-claude-do-computador) | O Claude já está logado no computador e você não quer logar de novo |
| [D. Docker + `claude` e `git` do computador (host agent)](#d-docker--claude-e-git-do-computador-host-agent) | Quer usar exatamente o binário, o login e as credenciais Git do computador |
| [E. Tudo no computador, sem Docker para a aplicação](#e-tudo-no-computador-sem-docker-para-a-aplicação) | Não quer containers para a aplicação |

Como a aplicação sabe o cenário: pela configuração `Agents:Launcher`. `local` (A, B, C, E) executa `claude` e `git` no próprio
processo/container; `host` (D) manda a execução para o host agent no computador. Detalhes no README.

---

## A. Docker completo (padrão)

**O que você terá:** `db` (Postgres), `api` (com o `claude` e o `git` instalados na imagem) e `web` (interface) em containers.
O login do Claude é feito pela interface e fica num volume.

### Pré-requisitos
- Docker com Compose v2.20 ou mais novo (`docker compose version`). No Windows/macOS, o Docker Desktop; no Linux, o Docker Engine + plugin Compose.
- Git, para clonar este repositório.
- Uma conta Claude (a mesma que você usaria no Claude Code). **Não precisa de chave de API.**
- Portas livres: `8080` (interface) e `8081` (API, só em `127.0.0.1`, para depuração).

### Passo a passo
1. Clone o repositório e entre nele:
   ```bash
   git clone <este-repositório> agentforge
   cd agentforge
   ```
2. Crie o `.env`. Prefira o script que gera segredos aleatórios:
   ```bash
   ./scripts/init-env.sh        # Linux/macOS
   ```
   ```powershell
   ./scripts/init-env.ps1       # Windows (PowerShell)
   ```
   **Sem rodar o script:** crie à mão um arquivo chamado exatamente `.env` (sem `.txt`) na raiz do projeto com este exemplo e
   troque os três valores marcados:
   ```
   # --- TROQUE estes três ---
   ADMIN_PASSWORD=TroqueEstaSenha2026
   JWT_SECRET=troque-por-uma-sequencia-aleatoria-de-pelo-menos-32-caracteres
   POSTGRES_PASSWORD=TroqueSenhaDoBanco2026

   # --- pode deixar como está ---
   ADMIN_USERNAME=admin
   POSTGRES_USER=agentforge
   COMPOSE_PROFILES=local-db
   ANTHROPIC_API_KEY=
   CLAUDE_CODE_VERSION=2.1.316
   ORCHESTRATOR_CONCURRENCY=1
   WEB_PORT=8080
   API_DEBUG_PORT=8081
   SWAGGER_ENABLED=true
   ```
   Para gerar um `JWT_SECRET` aleatório: `openssl rand -base64 48 | tr -d '/+='` (Linux/macOS) ou, no Windows PowerShell (5.1 ou 7),
   `$b = New-Object byte[] 48; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b) -replace '[/+=]',''`.
   Use só letras e números nas senhas: em `POSTGRES_PASSWORD`, `;` e `=` quebram a string de conexão e `$` é interpretado pelo
   Docker Compose. **Aspas:** o `docker compose` aceita (`DATABASE_URL="Host=...;Password=..."`; com aspas simples o `$` fica literal, com duplas é interpolado), mas os scripts `init-env`, `host-agent` e `run-native` leem o `.env` à mão e **não removem as aspas**: nos cenários D e E, deixe sem aspas. `cp .env.example .env` também funciona, mas troque as mesmas três senhas. Os
   valores de exemplo servem para experimentar; a API avisa no log enquanto estiverem em uso.
3. Confira no `.env` que existe a linha `COMPOSE_PROFILES=local-db`. É ela que faz o Postgres do compose subir.
   (O `.env.example` já traz; só um `.env` antigo pode não ter.)
4. Suba tudo (a primeira vez compila as imagens e leva alguns minutos):
   ```bash
   docker compose up -d --build
   ```
5. Verifique:
   ```bash
   docker compose ps
   ```
   `db`, `api` e `web` devem estar `healthy`. A API aplica as migrations sozinha ao iniciar.
6. Abra **http://localhost:8080** e entre com `ADMIN_USERNAME` e `ADMIN_PASSWORD` do `.env` (o usuário padrão é `admin`).
7. Abra **Agentes** na barra:
   - Se o Claude Code aparecer **Conectado**, o teste roda sozinho. Pronto.
   - Se aparecer **Não conectado**, clique em **Conectar**, abra o link que a tela mostra, entre na sua conta Claude e cole o
     código de volta na tela. O login fica no volume `claude-config` e sobrevive a reinícios.
8. Clique em **Testar** no cartão do Claude. Deve responder com sucesso (é uma chamada mínima, que consome um pouco de crédito).

### Se algo der errado
| Sintoma | Causa e solução |
|---|---|
| `docker compose` reclama de `ADMIN_PASSWORD`/`JWT_SECRET` | O `.env` não existe ou está sem essas variáveis. Refaça o passo 2 |
| `api` não fica saudável e o log fala de senha do banco | Você trocou `POSTGRES_PASSWORD` depois do primeiro start. O Postgres só lê a senha ao criar o volume: `docker compose down -v` (apaga os dados) e suba de novo |
| Porta 8080 em uso | Defina `WEB_PORT=8081` (ou outra) no `.env` e suba de novo |
| Agentes mostra "CLI não instalado" | Rebuild da imagem: `docker compose up -d --build api` |
| Login por link não conclui | Gere o link de novo; ele expira. Não feche a janela do código antes de colar |

Para parar: `docker compose down` (mantém os dados). Para apagar tudo, incluindo o banco e o login: `docker compose down -v`.

---

## B. Docker com o seu Postgres

**O que você terá:** o mesmo do cenário A, mas **sem** o container `db`. A API usa um Postgres que você já mantém.

### Pré-requisitos
Os do cenário A, mais um Postgres 14+ acessível a partir do container da API.

### Passo a passo
1. Clone o repositório (passo 1 do cenário A).
2. No seu Postgres, crie um banco e um usuário **vazios**. Exemplo:
   ```sql
   CREATE USER agentforge WITH PASSWORD 'uma-senha-forte';
   CREATE DATABASE agentforge OWNER agentforge;
   ```
   O usuário precisa poder criar tabelas nesse banco (o dono pode). As tabelas são criadas pelas migrations no primeiro start.
3. Crie o `.env` assim (arquivo completo para este cenário):
   ```
   # --- TROQUE ---
   ADMIN_PASSWORD=TroqueEstaSenha2026
   JWT_SECRET=troque-por-uma-sequencia-aleatoria-de-pelo-menos-32-caracteres
   ADMIN_USERNAME=admin

   # --- banco externo: COMPOSE_PROFILES VAZIO + string de conexão do SEU Postgres ---
   COMPOSE_PROFILES=
   DATABASE_URL=Host=meu-postgres;Port=5432;Database=agentforge;Username=agentforge;Password=uma-senha-forte

   # --- pode deixar como está ---
   ANTHROPIC_API_KEY=
   CLAUDE_CODE_VERSION=2.1.316
   ORCHESTRATOR_CONCURRENCY=1
   WEB_PORT=8080
   API_DEBUG_PORT=8081
   SWAGGER_ENABLED=true
   ```
   - `COMPOSE_PROFILES=` **vazio** é o que impede o `db` do compose de subir.
   - Se o Postgres roda **no próprio computador**, use `Host=host.docker.internal`. Ele precisa aceitar conexões que não sejam
     só de `127.0.0.1` (`listen_addresses` e `pg_hba.conf`).
   - Se o Postgres está em outro container, ponha os dois na mesma rede Docker e use o nome do container em `Host=`.
   - `POSTGRES_USER` e `POSTGRES_PASSWORD` do `.env` deixam de ser usados.
4. Suba:
   ```bash
   docker compose up -d --build
   ```
5. `docker compose ps` deve mostrar só `api` e `web` saudáveis (não há `db`).
6. Siga os passos 6 a 8 do cenário A (entrar, conectar o Claude, testar).

### Se algo der errado
| Sintoma | Causa e solução |
|---|---|
| `api` reinicia em loop | Veja `docker compose logs api`. Quase sempre é a `DATABASE_URL` (host, porta, usuário ou senha) ou o firewall/`pg_hba.conf` do Postgres |
| "database does not exist" | O banco precisa existir antes; as migrations criam as tabelas, não o banco |
| O `db` do compose subiu mesmo assim | `COMPOSE_PROFILES` não está vazio no `.env` (ou está exportado no seu terminal) |

---

## C. Docker reaproveitando o login do Claude do computador

**O que você terá:** o cenário A, mas o Claude **dentro do container** usa o login que já existe no seu computador.

### Pré-requisitos
- Os do cenário A.
- O Claude Code já instalado e **logado** no computador, com o login em arquivo: Windows e Linux (existe o arquivo
  `.credentials.json` dentro de `~/.claude`). **No macOS não funciona**: o login fica no Keychain. Use o cenário A.

### Passo a passo
1. Confirme que o login existe como arquivo:
   ```powershell
   Test-Path "$env:USERPROFILE\.claude\.credentials.json"      # Windows: deve dar True
   ```
   ```bash
   ls ~/.claude/.credentials.json                              # Linux
   ```
2. Clone o repositório (passo 1 do cenário A).
3. Crie o `.env` assim (o do cenário A mais `CLAUDE_CONFIG_SOURCE`, com barras `/` mesmo no Windows):
   ```
   # --- TROQUE ---
   ADMIN_PASSWORD=TroqueEstaSenha2026
   JWT_SECRET=troque-por-uma-sequencia-aleatoria-de-pelo-menos-32-caracteres
   POSTGRES_PASSWORD=TroqueSenhaDoBanco2026

   # --- login do computador: pasta ~/.claude (Linux: /home/voce/.claude) ---
   CLAUDE_CONFIG_SOURCE=C:/Users/voce/.claude

   # --- pode deixar como está ---
   ADMIN_USERNAME=admin
   POSTGRES_USER=agentforge
   COMPOSE_PROFILES=local-db
   ANTHROPIC_API_KEY=
   CLAUDE_CODE_VERSION=2.1.316
   ORCHESTRATOR_CONCURRENCY=1
   WEB_PORT=8080
   API_DEBUG_PORT=8081
   SWAGGER_ENABLED=true
   ```
   Deixe `ANTHROPIC_API_KEY` vazio: com uma chave definida, o CLI usa a chave e ignora o login.
4. No Linux, o container roda com o UID `1654`: a pasta precisa ser gravável por ele
   (por exemplo `chmod -R g+rw` e um grupo compartilhado, ou `setfacl -R -m u:1654:rwX ~/.claude`).
5. Suba: `docker compose up -d --build` e siga os passos 5 a 8 do cenário A. Em **Agentes** o Claude já deve aparecer **Conectado**.

### Cuidados
- O Claude do container passa a **ler e gravar** nessa pasta (inclusive a renovação do token) e a obedecer as configurações
  dela (hooks, plugins). Se isso incomoda, use o cenário D ou o login pela tela (A).
- Em dúvida, comece pelo A: dá menos acoplamento com a sua máquina.

---

## D. Docker + `claude` e `git` do computador (host agent)

**O que você terá:** `api` e `web` em containers, mas todo `claude` e `git` roda **no seu computador**, por um pequeno serviço
(o *host agent*). Usa o binário, o login e as credenciais Git (SSH, credential manager) que já existem aí.

### Pré-requisitos
- Os do cenário A (Docker + Compose).
- No computador: **.NET SDK 8 ou mais novo** (o host agent roda com `dotnet run`), **git** e **`claude`** no PATH,
  com o Claude **já logado**. Teste:
  ```bash
  dotnet --version
  git --version
  claude --version
  ```
  Se o Claude não estiver logado, rode `claude` uma vez e faça `/login`.
- Porta `8787` livre em `127.0.0.1`.

### Passo a passo
1. Clone o repositório (passo 1 do cenário A) e crie o `.env`. Os três itens do host agent **podem ficar de fora**: o script
   do passo seguinte os cria. Se preferir, preencha à mão:
   ```
   # --- TROQUE ---
   ADMIN_PASSWORD=TroqueEstaSenha2026
   JWT_SECRET=troque-por-uma-sequencia-aleatoria-de-pelo-menos-32-caracteres
   POSTGRES_PASSWORD=TroqueSenhaDoBanco2026

   # --- host agent (o script preenche se faltar) ---
   HOST_AGENT_TOKEN=troque-por-um-token-aleatorio-de-40-letras-e-numeros
   HOST_WORKSPACES_ROOT=C:/Users/voce/agentforge/repos
   HOST_AGENT_URL=http://host.docker.internal:8787

   # --- pode deixar como está ---
   ADMIN_USERNAME=admin
   POSTGRES_USER=agentforge
   COMPOSE_PROFILES=local-db
   CLAUDE_CODE_VERSION=2.1.316
   ORCHESTRATOR_CONCURRENCY=1
   WEB_PORT=8080
   API_DEBUG_PORT=8081
   SWAGGER_ENABLED=true
   ```
   - `HOST_AGENT_TOKEN`: mínimo de **24** caracteres, só letras e números.
   - `HOST_WORKSPACES_ROOT`: pasta **do computador**, caminho absoluto com `/` (Linux: `/home/voce/agentforge/repos`). Os
     clones e worktrees ficam aqui.
   - `HOST_AGENT_URL`: no Docker Desktop (Windows/macOS) é este mesmo. Linux com Docker nativo: veja a seção abaixo.
   - Sem `ANTHROPIC_API_KEY`: o `claude` do computador usa o login dele.
2. **Terminal 1 — inicie o host agent e deixe aberto:**
   ```powershell
   ./scripts/host-agent.ps1                  # Windows
   ```
   ```bash
   ./scripts/host-agent.sh                   # Linux/macOS
   ```
   Na primeira vez o script cria `HOST_AGENT_TOKEN` e `HOST_WORKSPACES_ROOT` no `.env`. `HOST_WORKSPACES_ROOT` é a pasta **do
   computador** onde ficam os clones e worktrees (padrão: `.agentforge/repos` dentro do repositório). Para outra pasta:
   `./scripts/host-agent.ps1 -Root D:/agentforge/repos`. Se aparecer aviso de que `dotnet`, `git` ou `claude` não estão no
   PATH, instale/ajuste antes de seguir.
3. **Terminal 2 — suba a aplicação com os dois arquivos do compose** (o segundo é só um override da `api`; o `web` e o banco
   continuam vindo do primeiro):
   ```bash
   docker compose -f docker-compose.yml -f docker-compose.host-agent.yml up -d --build
   ```
   Se faltar `HOST_AGENT_TOKEN`, o compose para com uma mensagem pedindo para rodar o script do passo 2.
4. Abra **http://localhost:8080** e entre. Em **Agentes**, o Claude aparece como **CLI no seu computador** e **Conectado**
   (é o login que você já tinha). Clique em **Testar**.
5. A URL do repositório de um projeto e os caminhos são os **do computador** (o `git` roda lá). Use as credenciais Git que você
   já configurou. A tela **Git** avisa que o git é o do computador e não oferece gerar chave.

### Linux com Docker nativo
O container não alcança o `127.0.0.1` do host. Inicie o host agent escutando em todas as interfaces e **restrinja a porta 8787
por firewall** (o token protege, mas não exponha à rede):
```bash
./scripts/host-agent.sh "" http://0.0.0.0:8787
```

### Se algo der errado
| Sintoma | Causa e solução |
|---|---|
| Agentes mostra erro de conexão com o host agent | O terminal 1 não está rodando, ou a porta/URL não bate (`HOST_AGENT_URL` no `.env`) |
| 401 do host agent | `HOST_AGENT_TOKEN` diferente entre o `.env` do host agent e o da API: pare os dois, rode o script de novo e suba o compose |
| "caminho fora das pastas permitidas" | O projeto/clone está fora de `HOST_WORKSPACES_ROOT`. Ajuste a pasta e reinicie o host agent |
| Front não abre | Faltou o primeiro `-f docker-compose.yml`: o override sozinho não define o `web` |

Segurança: o host agent escuta só em `127.0.0.1`, exige o token, executa apenas `claude` e `git` e só aceita pastas dentro de
`HOST_WORKSPACES_ROOT`. Proteja o `.env`: quem tem o token consegue mandar o `claude` rodar nessa pasta.

---

## E. Tudo no computador, sem Docker para a aplicação

**O que você terá:** a API roda nativa (e serve a própria interface), usando o `dotnet`, `git` e `claude` do computador.
Só o Postgres pode ficar num container.

### Pré-requisitos
- **.NET SDK 8+**, **Node 20+** (com npm), **git** e **`claude`** no PATH, com o Claude já logado.
- Um **PostgreSQL**: um que você já tenha, ou o script sobe um container (precisa de Docker só para isso).
- Porta `8080` livre (ou escolha outra com `-Urls`).

### Passo a passo
1. Clone o repositório (passo 1 do cenário A) e crie o `.env` **antes** de rodar o script (ele cria um com os valores de
   exemplo se faltar). Aqui só estas variáveis são lidas; portas e `COMPOSE_PROFILES` não valem (a porta vem de `-Urls`):
   ```
   # --- TROQUE ---
   ADMIN_PASSWORD=TroqueEstaSenha2026
   JWT_SECRET=troque-por-uma-sequencia-aleatoria-de-pelo-menos-32-caracteres
   ADMIN_USERNAME=admin

   # --- banco: com -StartPostgres / --start-postgres o script sobe um Postgres com estes dados ---
   POSTGRES_USER=agentforge
   POSTGRES_PASSWORD=TroqueSenhaDoBanco2026

   # --- OU um Postgres seu (descomente; tem prioridade e dispensa -StartPostgres) ---
   # NATIVE_DB_CONNECTION=Host=localhost;Port=5432;Database=agentforge;Username=agentforge;Password=troque

   ORCHESTRATOR_CONCURRENCY=1
   ```
2. Suba, escolhendo **uma** das opções de banco:
   - **Sem Postgres instalado** (o script sobe um container `agentforge-native-db` só em `127.0.0.1:5432`):
     ```powershell
     ./scripts/run-native.ps1 -StartPostgres        # Windows
     ```
     ```bash
     ./scripts/run-native.sh --start-postgres       # Linux/macOS
     ```
   - **Com um Postgres que você já tem:** crie banco e usuário vazios, ponha no `.env`
     `NATIVE_DB_CONNECTION=Host=localhost;Port=5432;Database=agentforge;Username=...;Password=...` e rode o script **sem** a
     opção de banco.
3. Na primeira vez o script compila a interface (`npm ci` + `ng build`), o que demora um pouco. Para recompilar depois:
   `-Rebuild` (`--rebuild` no `.sh`). Só no `.ps1`: outra porta com `-Urls http://localhost:9090` e outra pasta de clones
   com `-Workspaces D:/repos` (no `.sh` use `ASPNETCORE_URLS` e `Workspaces__Root` no ambiente, ou edite o script).
4. Abra **http://localhost:8080**, entre com o `ADMIN_USERNAME`/`ADMIN_PASSWORD` do `.env` e vá em **Agentes**: o Claude deve
   aparecer **Conectado** (login do computador). Clique em **Testar**.
5. Os clones ficam em `.agentforge/repos` no repositório (padrão). A chave SSH que o AgentForge gerar, se você usar a tela Git,
   fica em `~/.agentforge/ssh`, nunca no seu `~/.ssh`. As migrations rodam sozinhas ao iniciar.

Para parar, `Ctrl+C` no terminal. O container do banco (`agentforge-native-db`) continua de pé; pare com
`docker stop agentforge-native-db`.

### Se algo der errado
| Sintoma | Causa e solução |
|---|---|
| "'dotnet' não está no PATH" (ou node/git) | Instale a ferramenta e abra um terminal novo |
| Agentes: "CLI não instalado" | `claude` fora do PATH do terminal onde você rodou o script |
| Erro de conexão com o banco | Postgres não está de pé, ou `NATIVE_DB_CONNECTION`/`POSTGRES_*` não batem. Com `-StartPostgres`, se você trocou a senha depois do primeiro start, recrie o volume: `docker rm -f agentforge-native-db; docker volume rm agentforge-native-pgdata` |
| Porta 8080 ocupada | Use `-Urls http://localhost:<outra>` (no `.ps1`) |

---

## Depois de subir: do primeiro projeto ao merge

Vale para qualquer cenário.

1. **Agentes:** confira que o Claude Code está **Conectado** e que o **Testar** passou.
2. **Cadastre um projeto.** Escolha o caminho que combina com o repositório:
   - **Projeto de exemplo** (no cenário A/B/C): `docker compose exec api sh /examples/make-sample-repo.sh` cria
     `/data/repos/_sample/tasks-api.git`. Use essa URL em **Projetos → Novo projeto**.
   - **Importar do GitHub** (o mais simples): **Projetos → Importar do GitHub**. Cole um token pessoal do GitHub
     (escopo `repo`), clique em **Listar meus repositórios**, escolha um e **Importar e clonar**. Para repositório público
     basta digitar `dono/nome`, sem token. O AgentForge cria o projeto, usa a branch padrão do GitHub e **já clona**.
     Se for privado e o git roda no container (A, B, C), ele gera a chave SSH e a registra como *deploy key* somente leitura.
     O token só é usado nessa chamada, não é guardado.
   - **Manual:** **Projetos → Novo projeto** com nome, URL do repositório, branch base e (opcional) comandos de build e teste.
3. **Repositório privado, passo a passo manual** (cenários A, B, C):
   1. Abra **Git** e clique em **Gerar chave SSH**. Copie a **chave pública** (a privada nunca sai do servidor).
   2. No GitHub, no repositório: *Settings → Deploy keys → Add deploy key*. Cole a chave e **deixe "Allow write access"
      desmarcado**. (Para vários repositórios, use *Settings → SSH and GPG keys* da conta.)
   3. De volta na tela **Git**, use **Testar acesso** com a URL SSH (`git@github.com:org/repo.git`).
   4. Cadastre o projeto com essa URL SSH.
   Nos cenários D e E o git é o do computador: use as credenciais que você já tem (chave SSH, credential manager).
4. **Nova demanda → Criar e iniciar refinamento.** Descreva em linguagem natural o que quer. O Scrum Master lê o repositório
   e propõe as tarefas.
5. **Aprovar plano** na tela da demanda. O quadro do projeto mostra as tarefas andando; clique numa tarefa para ver o log ao
   vivo, o diff e as execuções.
6. Em **Aguardando aprovação**, revise o diff e **aprove** ou **rejeite com comentário** (a tarefa volta ao Dev). Tarefas que
   voltam três vezes ficam **Bloqueadas** esperando a sua decisão (*Reexecutar* ou *Cancelar*).
7. Depois de **Done**, o trabalho está no branch `agentforge/task-<id>` do clone local. **Nenhum agente faz push**: o merge é seu.
   Os clones ficam em `<raiz>/<id-do-projeto>/repo` e os worktrees em `<raiz>/<id-do-projeto>/worktrees/<id-da-tarefa>`, onde
   a raiz é `/data/repos` no volume `repos` (A, B, C), `HOST_WORKSPACES_ROOT` (D) ou `.agentforge/repos` (E).

Dica: marque o projeto como **modo automático** para dispensar as duas aprovações humanas (use só em projetos de teste).

### Opcional: controlar pelo Claude Code (MCP)
1. Abra **Integração** e crie um **token de acesso** (o valor aparece uma única vez; vale só no `/mcp`).
2. Registre no seu Claude Code (a tela já gera o comando com o token e o endereço):
   ```bash
   claude mcp add --transport http agentforge http://localhost:8080/mcp --header "Authorization: Bearer afk_..."
   ```
3. Converse: *"crie uma demanda no projeto X para adicionar o filtro por status e inicie"*, *"o que depende de mim?"*.

### Manutenção
- Atualizar a aplicação: `git pull`, depois `docker compose up -d --build` (D: com os dois `-f`). As migrations rodam no start.
  **Faça backup do volume `pgdata` antes** de atualizar um ambiente com dados que importam (veja o README, "Dados, backup e atualização").
- Atualizar o Claude do container (A, B, C): mude `CLAUDE_CODE_VERSION` no `.env` e rode `docker compose up -d --build api`.
- Mais de uma tarefa ao mesmo tempo: `ORCHESTRATOR_CONCURRENCY=2` (ou mais) no `.env`.
