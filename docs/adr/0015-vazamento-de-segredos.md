# ADR 0015 — Contenção de vazamento de segredos

## Contexto
Uma auditoria achou três vias de exposição em tempo de execução: (1) os processos filhos (`claude`, `git`) herdavam todo o
ambiente da API e os agentes têm `Bash`, então um `env` revelaria `Auth__JwtSecret`, `Auth__AdminPassword` e a senha do
Postgres; (2) URLs de repositório com token (`https://token@host`) voltavam em texto puro na API, na interface e no MCP, e o
stderr do git podia repeti-las; (3) o login não tinha freio contra tentativas de senha.

## Decisão
- `Secrets.ScrubEnvironment` (em `AgentForge.Execution`) remove do ambiente de todo processo iniciado pelo `LocalProcessLauncher`
  as variáveis `Auth__*`, `ConnectionStrings__*`, `Agents__*`, `HostAgent__*`, `ADMIN_*`, `JWT_SECRET`, `POSTGRES_*`,
  `DATABASE_URL`, `NATIVE_DB_CONNECTION` e `HOST_AGENT_TOKEN`. `ANTHROPIC_*` e `CLAUDE_*` ficam. Vale também no host agent.
- `Secrets.MaskUrls` troca a credencial de URLs http(s) por `***` em `ProjectDto` e em `GitException`. O banco guarda a URL
  completa. No `PUT /api/projects/{id}`, reenviar exatamente a URL mascarada mantém a guardada; uma mascarada diferente, ou num
  projeto novo, é recusada (400).
- Rate limit nativo do ASP.NET Core (sem dependência nova) em `POST /api/auth/login`: `Auth:LoginAttemptsPerMinute` (padrão 10)
  por IP, janela fixa de 1 minuto, 429 acima; `0` desliga. O refresh não é limitado: exige um token aleatório de alta entropia.

## Consequências
- Limite que permanece: o agente ainda lê arquivos acessíveis ao processo (login do Claude, chave SSH do AgentForge). A mitigação
  é o container (modo 2) e restringir `AllowedTools` por papel.
- Atrás de proxy o limite depende de `X-Forwarded-For` correto; sem isso todos compartilham o IP do proxy.
- Prompts e saídas dos agentes continuam gravados no banco por desenho.
