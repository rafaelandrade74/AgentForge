# ADR 0009 — Deploy no homelab

## Decisão
- **Imagem da API:** `dotnet/aspnet:8.0` + Node.js 20 (copiado da imagem oficial `node:20-bookworm-slim`, sem scripts remotos) + `git`, `openssh-client` e `curl`, e o **Claude Code instalado por npm em versão fixa** (`CLAUDE_CODE_VERSION`, padrão 2.1.316, a mesma contra a qual o adapter foi testado). `DISABLE_AUTOUPDATER=1` mantém a versão fixa. O `claude --version` roda no build, então uma versão inexistente quebra a construção, não a execução.
- **Credenciais:** cada CLI faz o próprio login. O compose só monta o diretório de configuração do Claude (`CLAUDE_CONFIG_DIR=/home/app/.claude`, volume `claude-config`) e repassa `ANTHROPIC_API_KEY` por variável de ambiente. O login de conta é feito uma vez com `docker compose exec -it api claude`. O backend não lê nem armazena tokens; variáveis `ANTHROPIC_*` **vazias** (o que o Compose gera quando a chave não está definida) são removidas do ambiente do processo filho, para não atrapalhar o login salvo.
- **Sem root:** a API roda como `app` (UID 1654), o front como `nginx-unprivileged` e o Postgres com o usuário padrão da imagem oficial. `api` e `web` têm `cap_drop: ALL` e `no-new-privileges`; o `web` tem sistema de arquivos somente leitura (`tmpfs` em `/tmp`). `/data/repos` e `/home/app` pertencem ao UID da API na imagem, e volumes nomeados novos herdam isso.
- **Rede:** só o `web` é publicado (`WEB_PORT`, padrão 8080); a API aparece apenas em `127.0.0.1:API_DEBUG_PORT`; o Postgres não é exposto. O Nginx do front repassa `/api`, `/hubs` (WebSocket) e `/swagger`.
- **Operação:** `restart: unless-stopped`, healthchecks nos três serviços e `depends_on` com `service_healthy` (o `web` só sobe depois da API, que só sobe depois do banco). `name: agentforge` fixa o nome do projeto Compose.
- **Configuração:** `.env.example` documentado e funcional como está (para `cp .env.example .env && docker compose up -d`); a API **avisa no log** enquanto `ADMIN_PASSWORD`/`JWT_SECRET` tiverem o valor de exemplo, mas não se recusa a subir. `scripts/init-env.sh|ps1` geram um `.env` com segredos aleatórios. `Swagger:Enabled` (`SWAGGER_ENABLED`) liga/desliga a documentação.
- **README** com instalação, login do Claude, primeiro uso, tabela de configuração, proxy reverso com WebSocket, backup, atualização, segurança e problemas comuns.

## Verificação
Numa cópia do repositório sem `.env`, `node_modules` nem artefatos de build, com projeto Compose separado: `cp .env.example .env && docker compose up -d` construiu tudo e deixou `db`, `api` e `web` saudáveis; login na API funcionou; o container da API roda como UID 1654 sem capabilities, com `claude 2.1.316`, Node 20 e git, e com os volumes de dados e de configuração graváveis; sem login no Claude, uma demanda falha com a mensagem do CLI ("Not logged in") visível na execução.

## Limites conhecidos
- Os agentes executam build/testes **dentro do container da API** (execução em containers efêmeros por tarefa está fora do MVP).
- Repositórios privados: HTTPS com token na URL funciona por construção; chaves SSH não têm montagem no compose e não foram testadas.
- Login real no Claude e uma execução completa com ele ficam para a Fase 9.
