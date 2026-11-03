# AgentForge

Aplicação web auto-hospedada que orquestra o ciclo de desenvolvimento de um produto com agentes de IA. Você cadastra um
projeto (repositório Git), escreve uma demanda em linguagem natural e acompanha, pela interface, o Scrum Master quebrando a
demanda em tarefas, o Dev implementando, o Tech Lead e o Security revisando e o QA testando, até a tarefa chegar a **Done**
com um branch pronto para merge. Cada papel é uma execução do **Claude Code** em modo headless, dentro do container da API.

Nenhum agente faz push: o trabalho fica no branch `agentforge/task-<id>` do clone local, e o merge é seu.

## Requisitos

- Docker com Compose v2 (Linux, macOS ou Windows com Docker Desktop)
- Uma conta/chave para o Claude Code (veja [Login do Claude Code](#login-do-claude-code))
- ~2 GB de disco para as imagens

## Instalação

```bash
git clone <este-repositório> agentforge && cd agentforge
cp .env.example .env          # ou ./scripts/init-env.sh para gerar segredos aleatórios
docker compose up -d
```

Abra **http://localhost:8080** e entre com `ADMIN_USERNAME` / `ADMIN_PASSWORD` do `.env`.
A primeira subida compila as imagens (alguns minutos). Depois: `docker compose ps` deve mostrar `db`, `api` e `web` como *healthy*/*running*.

> Os valores de exemplo do `.env.example` bastam para experimentar, mas a API avisa no log enquanto estiverem em uso.
> **Troque `ADMIN_PASSWORD`, `JWT_SECRET` e `POSTGRES_PASSWORD` antes de expor o sistema na rede.**
> `./scripts/init-env.sh` (Linux/macOS) ou `./scripts/init-env.ps1` (Windows) criam o `.env` já com segredos aleatórios.

## Login do Claude Code

Cada CLI cuida do próprio login; o AgentForge só monta o diretório de configuração (`claude-config`) e repassa o ambiente — nunca lê nem guarda credenciais. Escolha uma das formas:

1. **Chave de API:** defina `ANTHROPIC_API_KEY` no `.env` e rode `docker compose up -d`.
2. **Login de conta:** deixe a chave vazia e rode uma vez
   ```bash
   docker compose exec -it api claude
   ```
   siga o fluxo de login no terminal. O login fica no volume `claude-config` e sobrevive a reinícios e atualizações.

Sem login, as execuções dos agentes falham e a mensagem aparece na tela da tarefa/demanda. A versão do CLI é fixa na imagem (`CLAUDE_CODE_VERSION`); para atualizar, altere o valor e rode `docker compose up -d --build`.

## Primeiro uso

1. **Projetos → Novo projeto:** nome, URL do repositório Git, branch base e (opcional) comandos de build e teste.
2. **Nova demanda → Criar e iniciar refinamento.** O Scrum Master lê o repositório e propõe as tarefas.
3. **Aprovar plano** na tela da demanda. O quadro do projeto mostra as tarefas andando; clique numa tarefa para ver o log ao vivo, o diff e as execuções.
4. Ao chegar em *Aguardando aprovação*, revise o diff e **aprove** ou **rejeite com comentário**. Depois do `Done`, faça o merge do branch `agentforge/task-<id>` como preferir.

O projeto pode ser marcado como **modo automático** (sem as duas aprovações humanas). Tarefas que voltam três vezes ao Dev ficam *Bloqueadas* esperando a sua decisão (*Reexecutar* ou *Cancelar*).

### Repositórios privados

- **HTTPS com token:** use `https://<token>@github.com/org/repo.git` como URL. O token fica na URL do projeto (banco) e na configuração do clone (volume `repos`).
- **SSH:** a imagem tem o cliente `ssh`, mas a montagem de chaves não faz parte do compose e **não foi testada**: a pasta `~/.ssh` do usuário `app` (UID 1654) precisa ser legível por ele e ter `known_hosts`.

## Configuração (`.env`)

| Variável | Padrão | Para quê |
|---|---|---|
| `ADMIN_USERNAME`, `ADMIN_PASSWORD` | `admin` / *(exemplo)* | Usuário único, criado no start (senha só como hash BCrypt) |
| `JWT_SECRET` | *(exemplo)* | Assinatura dos tokens; mínimo de 32 caracteres |
| `POSTGRES_USER`, `POSTGRES_PASSWORD` | `agentforge` / *(exemplo)* | Banco. Só são lidos na criação do volume |
| `ANTHROPIC_API_KEY` | vazio | Chave do Claude Code (alternativa ao login) |
| `CLAUDE_CODE_VERSION` | `2.1.316` | Versão do CLI instalada na imagem |
| `ORCHESTRATOR_CONCURRENCY` | `1` | Tarefas simultâneas; com mais de 1, Tech Lead e Security revisam em paralelo |
| `WEB_PORT` | `8080` | Porta do front (único serviço exposto) |
| `API_DEBUG_PORT` | `8081` | API em `127.0.0.1` apenas, para depuração |
| `SWAGGER_ENABLED` | `true` | Documentação em `/swagger` |

Os prompts de sistema dos papéis ficam em `prompts/<papel>.md` (semeiam o primeiro start) e são editados depois pela tela **Prompts**; ficam no banco.

## Atrás de um proxy reverso

O front (`web`, Nginx) já repassa `/api`, `/hubs` (SignalR/WebSocket) e `/swagger` para a API. Aponte o seu proxy para `web:8080` e **habilite WebSocket** em `/hubs/`. Exemplo (Nginx):

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

Volumes nomeados: `pgdata` (banco), `repos` (clones e worktrees) e `claude-config` (login do Claude Code).

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

`db`, `api` e `web` rodam sem root (a API como UID 1654, o front com `nginx-unprivileged`), com `no-new-privileges` e sem capabilities (`cap_drop: ALL`); o front tem sistema de arquivos somente leitura. Só o `web` é publicado na rede; o Postgres não é exposto. Os agentes executam comandos (build, testes) **dentro do container da API**, dentro do worktree da tarefa — rode o AgentForge com repositórios em que você confia.

## Problemas comuns

| Sintoma | Causa e solução |
|---|---|
| `api` reinicia / "password authentication failed" | Você trocou `POSTGRES_PASSWORD` depois da primeira subida: `docker compose down -v` (apaga os dados) ou ajuste a senha no banco |
| Execuções falham em segundos | Claude Code sem login: veja [Login](#login-do-claude-code); a saída da execução mostra o motivo |
| O quadro não atualiza sozinho | WebSocket bloqueado no proxy: habilite `Upgrade` em `/hubs/` (o app cai para long polling, mais lento) |
| `defina ADMIN_PASSWORD no .env` | Falta o `.env`: `cp .env.example .env` |
| Porta 8080 ocupada | Mude `WEB_PORT` |

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

Estrutura: `backend/` (.NET 8: Api, Domain, Infrastructure, Orchestrator, Tests), `frontend/` (Angular), `prompts/`, `docs/adr/` (decisões de arquitetura), `docker-compose.yml`.
