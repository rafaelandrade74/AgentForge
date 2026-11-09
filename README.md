# AgentForge

Aplicação web auto-hospedada que orquestra o ciclo de desenvolvimento de um produto com agentes de IA. Você cadastra um
projeto (repositório Git), escreve uma demanda em linguagem natural e acompanha, pela interface, o Scrum Master quebrando a
demanda em tarefas, o Dev implementando, o Tech Lead e o Security revisando e o QA testando, até a tarefa chegar a **Done**
com um branch pronto para merge. Cada papel é uma execução do **Claude Code** em modo headless.

Nenhum agente faz push: o trabalho fica no branch `agentforge/task-<id>` do clone local. Quando a tarefa chega a **Done**, o próprio
AgentForge publica só esse branch no GitHub, pela **mesma chave SSH** do clone (ADR 0025); o botão *Publicar branch (git push)* cobre as que
faltam. **Você** abre o pull request (botão *Abrir PR* ou MCP) para `develop` ou a branch principal do projeto; o merge continua sendo seu.

> **Documentação online:** https://rafaelandrade74.github.io/AgentForge/ (fonte em `docs/index.html`, publicada pelo GitHub Pages a partir da pasta `/docs` da `main`).
>
> **Começando agora?** Há um tutorial passo a passo para cada cenário (Docker, banco próprio, login do computador, host agent e
> tudo nativo) em [docs/getting-started.md](docs/getting-started.md).

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

Sem usar o `init-env`, um `.env` mínimo (troque as três senhas; use só letras e números nas senhas; aspas no `.env` só valem para o `docker compose`, veja [docs/getting-started.md](docs/getting-started.md)):

```
ADMIN_PASSWORD=TroqueEstaSenha2026
JWT_SECRET=troque-por-uma-sequencia-aleatoria-de-pelo-menos-32-caracteres
POSTGRES_PASSWORD=TroqueSenhaDoBanco2026
ADMIN_USERNAME=admin
POSTGRES_USER=agentforge
COMPOSE_PROFILES=local-db
```

As demais variáveis têm padrão (veja [Configuração](#configuração-env)). O exemplo completo e um gerador de `JWT_SECRET` estão em
[docs/getting-started.md](docs/getting-started.md).

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

## Vários ambientes (várias máquinas, uma stack por projeto)

Além do ambiente padrão (o que `Agents:Launcher` define), você pode cadastrar **outras máquinas** na tela **Ambientes**. Cada
uma roda o **host agent** (`scripts/host-agent`) e tem as ferramentas que a stack do projeto exige: Node 22 numa, .NET 8 e
Python em outra. O projeto escolhe o ambiente (campo **Ambiente de execução**, também na importação do GitHub); sem escolha, vale
o padrão. Tudo do projeto acontece nessa máquina: clone, worktrees, git e agentes.

1. Na máquina, clone o repositório e suba o host agent: `./scripts/host-agent.sh "" http://0.0.0.0:8787` (Windows: `./scripts/host-agent.ps1 -Urls http://0.0.0.0:8787`). O script cria e mostra o token (e a pasta dos clones). Libere
   outras ferramentas com `HostAgent__Executables__<nome>=<binário>` e restrinja a porta por firewall ao IP do servidor.
2. Em **Ambientes → Novo ambiente**: nome, endereço (`http://192.168.0.20:8787`), token, **pasta dos clones nessa máquina**
   (`C:/agentforge`, `/home/eu/agentforge`) e rótulos da stack (informativos).
3. **Testar** mostra se a máquina responde e quais ferramentas (`claude`, `git`, `node`, `dotnet`) ela oferece.

Pontos importantes:

- O **token** fica no banco **criptografado** (ASP.NET Data Protection) e nunca volta pela API. As chaves de proteção ficam no volume
  `dp-keys`; sem ele (ou se `DataProtection:KeysDir` mudar), os tokens guardados não podem mais ser lidos e você os informa de novo.
- Cada máquina tem o **seu próprio login do Claude** e a **sua chave Git**. As telas **Agentes** e **Git** têm um seletor de
  **Ambiente** (também por link, nos botões *Login do agente* e *Chave Git* de cada ambiente): o login por link, o teste e a chave
  SSH são feitos NAQUELA máquina. A chave é gerada pelo próprio host agent (em `HostAgent:SshDir`, padrão `~/.agentforge/ssh`) e a
  privada nunca sai de lá; o `git` dessa máquina usa a chave se ela existir e, sem ela, as credenciais que a máquina já tem.
  Na importação do GitHub com um ambiente, a chave **da máquina escolhida** é a registrada como deploy key somente leitura.
  Host agent de versão antiga (sem as rotas de chave) pede atualização.
- Máquina fora do ar: a execução falha com a mensagem "Host agent inacessível…" e a tarefa vai para Blocked (ou a demanda para Failed).
- Mudar o ambiente de um projeto faz o próximo uso clonar o repositório na nova máquina (o clone antigo permanece na anterior).
- Um ambiente em uso por projetos não pode ser excluído.

### Perfis de stack (um container por tarefa)

Em vez de depender do que a máquina tem instalado, o projeto pode escolher um **perfil de stack** (tela **Perfis**; campo *Perfil de
stack* no projeto, depois de escolher o ambiente): uma imagem Docker (Node 22, .NET 8, Python 3.12 prontos, ou a sua, por imagem ou
**Dockerfile**) em que os agentes do projeto rodam, com limites de CPU/memória e rede opcional. Cada tarefa sobe um container na
máquina do ambiente, com **só a pasta do projeto** montada (mais o login do Claude), sem `--privileged`, sem o socket do Docker e sem
capacidades.

1. Na máquina (Linux ou macOS, com Docker), suba o host agent com `HostAgent:Docker:Enabled=true` (desligado por padrão). Imagens
   prontas próprias precisam estar em `HostAgent:Docker:AllowedImages`; as construídas pelo AgentForge (`agentforge/*`) sempre valem.
   O login do Claude dessa máquina (`~/.claude`, ou `HostAgent:Docker:ClaudeConfigDir`) é o usado no container.
2. **Ambientes → Testar** mostra *containers habilitados*. Em **Perfis → Construir imagem** a imagem é construída nessa máquina, com o
   log (na primeira execução ela também é construída sozinha, e muda ao editar o Dockerfile).
3. No projeto: ambiente + perfil. O agente roda dentro do container; sem Docker habilitado ou com perfil apagado a execução falha com o motivo.

Para usar uma imagem sua, ela precisa ter o `claude`, o `git` e `CLAUDE_CONFIG_DIR=/agent-config`. Decisão na [ADR 0019](docs/adr/0019-perfis-de-stack-e-container-por-tarefa.md).

### Fila por ambiente, máquina fora do ar e revisão em outra máquina

- **Concorrência por ambiente:** cada ambiente tem *Tarefas ao mesmo tempo* (1 a 32). O que não cabe espera na fila **daquele**
  ambiente sem travar os outros. Para usar várias máquinas em paralelo, suba também `ORCHESTRATOR_CONCURRENCY` (o teto global).
- **Painel de carga** na tela **Ambientes** (atualiza a cada 5 s) e a ferramenta MCP `get_environment_load`: tarefas rodando/limite,
  na fila, agentes rodando agora e o que espera uma máquina voltar.
- **Máquina fora do ar não bloqueia a tarefa:** ela continua no estado em que estava, mostra "Aguardando o ambiente voltar…" e é
  tentada de novo com espera crescente (15 s, 30 s… até 5 min) até a máquina responder — sem ação humana, também depois de
  reiniciar a API. Vale para o refinamento da demanda. Erros que pedem uma pessoa (token recusado, ambiente removido, falha do
  agente) continuam indo para Blocked/Failed. Ajustes: `Orchestrator:EnvironmentRetryInitialSeconds` e `:EnvironmentRetryMaxSeconds`.
- **Revisão em outra máquina:** no projeto, *Tech Lead roda em* e *Security roda em* mandam esses papéis para uma máquina leve. Eles
  recebem **só o diff e a descrição** (sem o repositório e sem perfil de stack) — menos contexto, menos custo na máquina pesada. Dev
  e QA ficam sempre no ambiente do projeto, onde está o worktree.

Tutorial passo a passo (cenário F) em [docs/getting-started.md](docs/getting-started.md#f-várias-máquinas-e-stacks-ambientes) e roteiro de validação com máquinas reais em [docs/validacao-cenario-f.md](docs/validacao-cenario-f.md).

Decisões nas [ADR 0017](docs/adr/0017-ambientes-de-execucao.md), [ADR 0018](docs/adr/0018-git-e-login-por-ambiente.md), [ADR 0019](docs/adr/0019-perfis-de-stack-e-container-por-tarefa.md) e [ADR 0020](docs/adr/0020-ambiente-por-papel-fila-e-carga.md); o plano completo (próxima fase: validação ponta a ponta) está em [docs/plano-ambientes.md](docs/plano-ambientes.md).

## Agentes ao vivo e chat

- **Ao vivo** (menu de cima): um cartão por agente e por máquina (pronto, trabalhando, não conectado, limite de uso com o horário de liberação, máquina inacessível) e, abaixo, o que está **rodando agora** em todos os projetos, em tempo real. Em cada cartão, **Conversar** abre um chat com aquele agente.
- **Chat:** conversa com o Claude da máquina escolhida sobre um projeto. Se o clone do projeto está na mesma máquina, o agente **lê o código** (só leitura). Peça *"proponha uma demanda para..."*: o agente sugere e você cria com **Criar demanda** ou **Criar e iniciar refinamento**; nada vira demanda sem o seu clique. As conversas ficam salvas. Cada resposta gasta créditos do agente. Veja o ADR 0026.

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

Ferramentas (21): `list_projects`, `get_project`, `create_project`, `create_demand`, `start_demand`, `approve_plan`, `get_demand`,
`list_tasks`, `get_task`, `get_task_diff`, `get_run_output`, `approve_task`, `reject_task`, `retry_task`, `cancel_task`,
`list_pending_actions`, `list_agents`, `test_agent`, `get_environment_load`, `update_project`, `open_pull_request`. As de leitura são marcadas como somente leitura e `cancel_task` como
destrutiva, para o cliente pedir confirmação. **Não existe ferramenta que faça merge ou apague projeto**, e o único push é o `open_pull_request` (só do branch da tarefa concluída, para abrir o PR). Atrás de um
proxy reverso, repasse `/mcp` à API e use HTTPS (o token trafega no cabeçalho). `start_demand`/`create_demand` com `start` e
`test_agent` gastam créditos do agente.

## Pull request ao concluir a tarefa

Depois que a tarefa passou por Dev, Tech Lead, Security e QA **e você aprovou a entrega** (estado *Done*), a tela da tarefa mostra
**Abrir PR**. O AgentForge publica só o branch `agentforge/task-<id>` no GitHub e abre o pull request para a branch de destino.
Os agentes não ganham nenhuma credencial de escrita: quem faz o push é o servidor, por ação sua.

1. No GitHub, crie um token **fine-grained** limitado ao repositório, com **Contents: Read and write** e **Pull requests: Read and write**.
2. **Projetos → Editar → Token do GitHub para PRs**: cole o token. Fica no banco criptografado (como o token dos ambientes) e nunca
   volta para a tela; *Apagar token* o remove. Se a chave de proteção mudar (volume `dp-keys`), informe-o de novo.
3. **Branch de destino dos PRs** (mesma tela): `develop`, por exemplo. Vazio = a branch base do projeto. Também dá para trocar pelo
   MCP (`update_project`, argumento `pullRequestBaseBranch`) e, na hora, no campo ao lado do botão *Abrir PR* (ou `baseBranch` em `open_pull_request`).
   **O token não passa pelo MCP** de propósito: cadastre-o só na tela.
4. Na tarefa *Done*: **Abrir PR**. Pelo MCP: `open_pull_request` com o `taskId`; `list_pending_actions` mostra, em `readyForPullRequest`,
   o que está pronto para PR.

Detalhes: só projetos em github.com; o push usa um refspec fixo (o branch da tarefa para o mesmo nome, sem `--force`), roda na máquina do
ambiente do projeto e nunca atualiza a base; abrir de novo devolve o mesmo PR; se o GitHub recusar o PR (ex.: branch de destino
inexistente), o branch já está publicado e basta corrigir e tentar de novo. Uma tarefa que depende de outras leva o trabalho delas no
branch (o PR avisa): mescle primeiro o das dependências. Decisão na [ADR 0023](docs/adr/0023-pull-request-da-tarefa-concluida.md).

## Primeiro uso

1. **Agentes:** confira que o Claude Code está **Conectado** e o teste passou.
2. **Projetos → Novo projeto:** nome, URL do repositório Git, branch base e (opcional) comandos de build e teste.
3. **Nova demanda → Criar e iniciar refinamento.** O Scrum Master lê o repositório e propõe as tarefas.
4. **Aprovar plano** na tela da demanda. O quadro do projeto mostra as tarefas andando; clique numa tarefa para ver o log ao
   vivo, o diff e as execuções. A saída e o prompt de cada execução aparecem **formatados** (títulos, listas, código, tabelas);
   o botão **Ver texto bruto** mostra o texto original.
5. Ao chegar em *Aguardando aprovação*, revise o diff e **aprove** ou **rejeite com comentário**. A página **Aprovações** (barra de cima, com o número de pendentes) lista as entregas esperando de **todos os projetos**, as mais antigas primeiro. Depois do `Done`, faça o
   merge do branch `agentforge/task-<id>` como preferir.

O projeto pode ser marcado como **modo automático** (sem as duas aprovações humanas). Tarefas que voltam três vezes ao Dev
ficam *Bloqueadas* esperando a sua decisão (*Reexecutar* ou *Cancelar*). O agente de cada papel é escolhido na tela **Agentes**
e os prompts, na tela **Prompts**.

Há um projeto de exemplo (API de tarefas em .NET, com testes) em `examples/tasks-api`. No modo 2:
`docker compose exec api sh /examples/make-sample-repo.sh` cria `/data/repos/_sample/tasks-api.git`; use essa URL no projeto.

### Repositórios privados

- **Modo 2 (Docker, git no container) — chave SSH pela interface:** abra **Git** na barra, clique em **Gerar chave SSH** e copie
  a **chave pública**. A chave é gerada no servidor (`ed25519`, no volume `git-ssh`) e a **parte privada nunca sai dele**. No GitHub:
  no repositório, *Settings → Deploy keys → Add deploy key*, cole a chave e **marque "Allow write access"** (o AgentForge
  publica o branch das tarefas concluídas com esta mesma chave; sem escrita o push é recusado e a tarefa avisa); ou, para vários repositórios, *Settings → SSH and GPG keys* da sua conta. Depois use
  **Testar acesso** com a URL SSH (`git@github.com:org/repo.git`) e cadastre o projeto com essa URL. A chave serve a todos os
  projetos e o servidor Git novo é registrado no `known_hosts` na primeira conexão. "Gerar outra" substitui a chave (e a antiga
  deixa de valer); "Remover" apaga.
- **Modos 1 e 3:** o `git` é o do computador, com as credenciais que você já configurou (credential manager, chave SSH). A tela
  Git avisa disso e não oferece gerar chave (a do AgentForge, no modo 3, fica em `~/.agentforge/ssh`, nunca no seu `~/.ssh`).
- **HTTPS com token:** use `https://<token>@github.com/org/repo.git`. O token fica na URL do projeto (banco) e na configuração do clone; a API, a interface e o MCP
  mostram `https://***@github.com/...` e mensagens de erro do git são mascaradas. Ao editar o projeto, deixar a URL mascarada mantém o
  token guardado. Prefira a chave SSH ou o import do GitHub, que não gravam token na URL.
- **Importar do GitHub (mais simples):** em **Projetos → Importar do GitHub**, cole um token pessoal (escopo `repo`), liste seus
  repositórios e escolha um. O AgentForge cria o projeto (URL SSH se privado, HTTPS se público; branch base = a padrão do
  GitHub), gera a chave SSH se faltar, a registra como **deploy key somente leitura** e **já clona**. Se o clone falhar, o projeto
  não é criado. O token é usado só nessa chamada: não é guardado nem registrado em log. Repositório público dispensa o token
  (digite `dono/nome`). Veja o ADR 0014.

## Configuração (`.env`)

| Variável | Padrão | Para quê |
|---|---|---|
| `ADMIN_USERNAME`, `ADMIN_PASSWORD` | `admin` / *(exemplo)* | Usuário único, criado no start (senha só como hash BCrypt) |
| `JWT_SECRET` | *(exemplo)* | Assinatura dos tokens; mínimo de 32 caracteres |
| `POSTGRES_USER`, `POSTGRES_PASSWORD` | `agentforge` / *(exemplo)* | Banco. Só são lidos na criação do volume |
| `COMPOSE_PROFILES` | `local-db` | Sobe o Postgres do compose; vazio = não sobe (use `DATABASE_URL`) |
| `DATABASE_URL` | vazio | String Npgsql de um Postgres existente; tem prioridade sobre o banco do compose |
| `ANTHROPIC_API_KEY` | vazio | Opcional: chave de API em vez do login de conta |
| `CLAUDE_CONFIG_SOURCE` | volume `claude-config` | Pasta do computador com um login existente (modo 2) |
| `HOST_AGENT_TOKEN`, `HOST_WORKSPACES_ROOT`, `HOST_AGENT_URL` | — | Modo 1 (preenchidos por `scripts/host-agent`) |
| `NATIVE_DB_CONNECTION` | Postgres local | Modo 3: connection string do banco |
| `CLAUDE_CODE_VERSION` | `2.1.316` | Versão do CLI instalada na imagem (modo 2) |
| `ORCHESTRATOR_CONCURRENCY` | `1` | Teto global de tarefas simultâneas; com mais de 1, Tech Lead e Security revisam em paralelo. Cada ambiente tem ainda o seu limite (tela Ambientes) |
| `WEB_PORT`, `API_DEBUG_PORT` | `8080`, `8081` | Portas (a API só em `127.0.0.1`) |
| `SWAGGER_ENABLED` | `true` | Documentação em `/swagger` |
| *(tela Integração)* | — | Tokens de acesso do servidor MCP (`/mcp`) |

> Os valores de exemplo do `.env.example` bastam para experimentar, mas a API avisa no log enquanto estiverem em uso.
> **Troque `ADMIN_PASSWORD`, `JWT_SECRET` e `POSTGRES_PASSWORD` antes de expor o sistema na rede.**
> `./scripts/init-env.sh` (Linux/macOS) ou `./scripts/init-env.ps1` (Windows) criam o `.env` já com segredos aleatórios.

### Subir o banco é opcional

O Postgres do compose está no profile `local-db`, e o `.env` escolhe se ele sobe (`COMPOSE_PROFILES`):

| Quero | No `.env` | Comando |
|---|---|---|
| O Postgres do compose (padrão) | `COMPOSE_PROFILES=local-db` | `docker compose up -d` |
| Um Postgres que já tenho | `COMPOSE_PROFILES=` (vazio) e `DATABASE_URL=Host=...;Port=5432;Database=agentforge;Username=...;Password=...` | `docker compose up -d` |

No segundo caso o serviço `db` e o volume `pgdata` nem entram. Crie o banco e o usuário vazios; as tabelas são criadas pelas
migrations no primeiro start. Para um Postgres no próprio computador, use `Host=host.docker.internal`. `POSTGRES_*` só valem
com o banco do compose. Se o seu `.env` é antigo e não tem `COMPOSE_PROFILES`, acrescente `COMPOSE_PROFILES=local-db`, senão o
banco do compose não sobe.

### Como a aplicação sabe o modo de execução

Ela **não detecta** o modo: ele vem da configuração `Agents:Launcher` (`local` ou `host`; padrão `local`).

| Modo | Como é escolhido | `Agents:Launcher` |
|---|---|---|
| 2 — Docker, CLI no container | `docker compose up` simples | `local` (padrão) |
| 1 — Docker, CLI no computador | compose com `docker-compose.host-agent.yml`, que define `Agents__Launcher=host`, `Agents__HostAgent__Url` e `Agents__HostAgent__Token` | `host` |
| 3 — tudo no computador | `scripts/run-native`, que define `Agents__Launcher=local` | `local` |

Com `host`, todo processo (`claude` e `git`) vai por HTTP para o host agent (`scripts/host-agent`), que roda no computador, só
executa os nomes da lista `HostAgent:Executables` e só em pastas de `HostAgent:Roots`. Com `local`, a própria API executa.
A tela **Git** e o import do GitHub valem para o ambiente escolhido: em `local` o AgentForge gera a chave SSH no servidor e a
registra como deploy key; em `host` quem gera e guarda a chave é o host agent do computador (ou vale o git dele, com as
credenciais que você já tem). Para trocar de modo, mude como sobe e
reinicie (não há seletor na interface).

Sobre o Claude Code: a imagem da API **instala** o CLI no build (`npm install -g @anthropic-ai/claude-code@${CLAUDE_CODE_VERSION}`);
em execução nada é baixado. Nos modos 1 e 3 usa-se o `claude` já instalado e logado no computador.

### `appsettings.json` (referência)

Todos os valores podem ser sobrescritos por variável de ambiente, trocando `:` por `__` (ex.: `Auth__JwtSecret`). O
`docker-compose.yml` já faz isso a partir do `.env`. `Git:SshDir` e `Prompts:Directory` não estão no arquivo de propósito:
vazios, anulariam o padrão do código (defina-os só se quiser mudar). Os segredos vêm vazios de propósito: a API recusa subir sem `Auth:JwtSecret`
(mínimo de 32 caracteres) e `Auth:AdminPassword`.

| Seção:chave | Padrão | Para quê |
|---|---|---|
| `ConnectionStrings:Default` | Postgres local | Banco (Npgsql). As migrations rodam sozinhas no start |
| `Auth:JwtSecret` | *(obrigatório)* | Assinatura dos JWT; mínimo de 32 caracteres |
| `Auth:Issuer` | `agentforge` | Emissor e audiência dos JWT |
| `Auth:AccessTokenMinutes`, `Auth:RefreshTokenDays` | `15`, `7` | Validade do token de acesso e do refresh (este fica no banco, em hash) |
| `Auth:LoginAttemptsPerMinute` | `10` | Tentativas de login por minuto por IP; acima disso responde 429. `0` desliga |
| `Auth:AdminUsername`, `Auth:AdminPassword` | `admin` / *(obrigatório)* | Usuário único; a senha vira hash BCrypt |
| `Agents:Launcher` | `local` | `local` ou `host` (ver acima) |
| `Agents:HostAgent:Url`, `:Token` | vazio | Obrigatórios com `host`: endereço do host agent visto pela API e segredo compartilhado (mínimo de 24 caracteres) |
| `Workspaces:Root` | `/data/repos` | Onde ficam os clones e worktrees; é um caminho do lado onde o git roda (no modo `host`, do computador) |
| `Git:SshDir` | `~/.agentforge/ssh` (Docker: `/home/app/.ssh`) | Pasta da chave SSH gerada pelo AgentForge |
| `Orchestrator:MaxConcurrency` | `1` | Teto global de tarefas simultâneas (cada ambiente tem o seu limite) |
| `Orchestrator:EnvironmentRetryInitialSeconds`, `:EnvironmentRetryMaxSeconds` | `15`, `300` | Espera antes de tentar de novo quando a máquina de um ambiente não responde (dobra até o máximo) |
| `GitHub:ApiUrl` | `https://api.github.com` | Base da API do GitHub (muda para GitHub Enterprise ou testes) |
| `Prompts:Directory` | `<app>/prompts` | De onde os prompts dos papéis semeiam o primeiro start |
| `Swagger:Enabled` | `true` | Documentação em `/swagger` |
| `Frontend:Path` | vazio | Se preenchido, a API serve a interface compilada daquela pasta (modo 3) |

O **host agent** (`backend/AgentForge.HostAgent/appsettings.json`) tem o seu próprio conjunto: `urls` (padrão
`http://127.0.0.1:8787`), `HostAgent:Token` (o mesmo de `Agents:HostAgent:Token`), `HostAgent:Roots` (pastas permitidas; ao
menos uma), `HostAgent:Executables` (nome lógico → binário; só `claude` e `git` por padrão) e `HostAgent:SshDir` (pasta da chave
SSH gerada por pedido da API; padrão `~/.agentforge/ssh`) e `HostAgent:Docker:*` (`Enabled`, `Executable`, `AllowedImages`,
`ClaudeConfigDir`: containers por tarefa, desligados por padrão). `scripts/host-agent` preenche tudo.

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

> Passo a passo completo da atualização (cada cenário, recompilar só uma parte, voltar versão, liberar espaço): seção **Atualizar a aplicação** na [documentação online](https://rafaelandrade74.github.io/AgentForge/#atualizar).

Volumes nomeados: `pgdata` (banco), `repos` (clones e worktrees, modo 2), `claude-config` (login do Claude Code, modo 2),
`git-ssh` (chave SSH do servidor) e `dp-keys` (chaves que protegem os tokens dos ambientes cadastrados — inclua no backup).

```bash
# backup do banco
docker compose exec -T db pg_dump -U agentforge agentforge > backup.sql
# restaurar (banco vazio)
docker compose exec -T db psql -U agentforge agentforge < backup.sql

# atualizar o AgentForge
git pull && docker compose up -d --build     # migrations aplicam sozinhas no start
```

`docker compose down` mantém os dados; `docker compose down -v` **apaga** banco, clones e login.

## Segredos e vazamento de dados

- **Ambiente dos agentes:** todo processo filho (`claude`, `git`) é iniciado **sem** os segredos da aplicação (`Auth__*`,
  `ConnectionStrings__*`, `Agents__*`, `HostAgent__*`, `ADMIN_*`, `JWT_SECRET`, `POSTGRES_*`, `DATABASE_URL`, `HOST_AGENT_TOKEN`).
  Os agentes têm `Bash`: sem isso, um `env` mostraria a senha do banco e a chave dos JWT. As credenciais do Claude
  (`ANTHROPIC_*`, `CLAUDE_*`) continuam, pois o CLI precisa delas.
- **O que um agente ainda alcança:** os arquivos que o processo lê, como o login do Claude em `~/.claude` e a chave SSH do
  AgentForge (que o `git` usa). Por isso o agente roda no container (modo 2) ou, nos modos 1 e 3, com as permissões do seu usuário.
  Restringir o `Bash` por papel na tela **Prompts** reduz esse alcance.
- **URLs com credencial** (`https://token@host/...`) aparecem mascaradas (`https://***@host/...`) na API, na interface, no MCP e nas
  mensagens de erro do git.
- **Login:** limitado a `Auth:LoginAttemptsPerMinute` tentativas por minuto por IP (429 acima disso). Atrás de proxy, repasse
  `X-Forwarded-For` (o compose já habilita) para o limite valer por cliente e não pelo IP do proxy.
- Prompts e saídas dos agentes ficam gravados no banco (visíveis a quem tem login ou token MCP); não coloque segredos no repositório.
- Em produção, use `SWAGGER_ENABLED=false`.

## Segurança do container

`db`, `api` e `web` rodam sem root (a API como UID 1654, o front com `nginx-unprivileged`) e com `cap_drop: ALL`; o front tem
sistema de arquivos somente leitura e usa `no-new-privileges`. Na `api`, os agentes podem instalar
dependências com `sudo` **dentro do container** (`AGENT_SUDO=true`, [ADR 0016](docs/adr/0016-agentes-instalam-dependencias.md)),
o que exige `no-new-privileges` desligado e algumas capabilities do `apt`; para endurecer, `AGENT_SUDO=false` e
`API_NO_NEW_PRIVILEGES=true` no `.env`. Só o `web` é publicado na rede; o Postgres não é exposto. No modo 2, os agentes executam comandos (build, testes) **dentro do container da API**; nos modos 1 e 3,
**no seu computador**, dentro do worktree da tarefa — rode o AgentForge com repositórios em que você confia.

## Problemas comuns

| Sintoma | Causa e solução |
|---|---|
| Faixa roxa "limite de uso atingido" | Os créditos ou a janela de horas do agente acabaram. As tarefas ficam **em espera** (não falham) e retomam sozinhas quando o agente libera; a faixa mostra o horário. Sem horário informado, o AgentForge tenta de novo a cada 5 min. Veja o ADR 0024 |
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
