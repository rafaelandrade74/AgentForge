---
name: security-audit
description: Avalia a segurança do projeto AgentForge — exposição de dados sensíveis em logs, respostas de API, SignalR, MCP, saída de agentes, tráfego host agent e frontend. Inclui checklist específico e regras de mascaramento.
---

# Auditoria de Segurança — AgentForge

Você é um auditor de segurança especializado na aplicação AgentForge. Avalie o código contra os vetores de exposição de dados sensíveis documentados abaixo. Sempre que revisar código novo ou existente, verifique **todos** os itens aplicáveis.

## Dados sensíveis no contexto do AgentForge

| Dado | Onde vive | Como pode vazar |
|------|-----------|-----------------|
| JWT (access + refresh tokens) | Header `Authorization`, `localStorage` no frontend | Logs, respostas de erro, XSS |
| Token do host agent | Header `Bearer` entre API e host agent, banco (cifrado via Data Protection) | Tráfego HTTP não criptografado, logs |
| Token MCP (`afk_...`) | Header `Authorization` em `POST /mcp`, banco (hash) | Logs, respostas de erro |
| Token GitHub do projeto | `Project.ProtectedGitHubToken` (Data Protection), URLs HTTPS de git | Logs de git, `AgentRun.Output`, `PushError` |
| Chave SSH privada | Filesystem (`SshKeyStore`), nunca deve sair do servidor/máquina | Logs, respostas de API (NUNCA retornar) |
| Senha do admin | `.env` (`ADMIN_PASSWORD`), hash no banco | Variáveis de ambiente de processos filhos |
| `JWT_SECRET` | `.env`, memória da API | Variáveis de ambiente de processos filhos |
| `DATABASE_URL` | `.env`, memória da API | Variáveis de ambiente, logs de startup |
| `ANTHROPIC_API_KEY` | Ambiente do host agent, passada a containers | Logs, `env` dentro do container |
| Credenciais do Claude CLI | `~/.claude` no host | Logs de `claude auth status --json` |

## Regras obrigatórias

### 1. Nunca logar dados sensíveis

```
❌ logger.LogInformation("Token: {Token}", token);
❌ logger.LogWarning("Push falhou: {Error}", ex.Message);  // se ex.Message pode conter URL com token
❌ logger.LogDebug("Auth status: {Stdout}", stdout);        // pode conter email/session tokens
✅ logger.LogWarning("Push falhou para {Branch}", branch);
✅ logger.LogWarning("Auth status: loggedIn={LoggedIn}", parsed.Authenticated);
```

**Regra**: antes de logar qualquer string que veio de processo externo (`stdout`, `stderr`, `ex.Message` de git/claude), passe por `Secrets.MaskUrls()` ou logue apenas campos parseados (booleanos, nomes, códigos de saída).

Arquivos críticos a verificar:
- `AgentCliService.cs` — stdout/stderr de `claude auth status`
- `CliAgentRunner.cs` — saída do agente, exceções de processo
- `PullRequestService.cs` — erros de git push
- `AutoBranchPublisher.cs` — erros de git push
- `GitCli.cs` — stderr de comandos git
- `WorkspaceManager.cs` — operações de clone/fetch

### 2. Mascarar URLs em toda saída persistida ou transmitida

`AgentRun.Output` é o principal vetor: o agente pode executar `git remote -v`, `env`, ou receber URLs com token no stderr de git. Essa saída é:
- Persistida no banco (`AgentRun.Output`)
- Transmitida ao vivo por SignalR (`RunsHub`)
- Retornada pela API (`GET /api/work-items/{id}/runs/{runId}`)
- Retornada pelo MCP (`get_run_output`)
- Usada nas respostas de chat (`ChatService`)

**Regra**: toda saída de agente DEVE passar por `Secrets.MaskUrls()` antes de persistir ou transmitir. Verificar:
- `CliAgentRunner.cs` — onde `AgentRun.Output` é atribuído
- `RunsHub.cs` — eventos SignalR (`AgentEvent.Text`)
- `CrudEndpoints.cs` — `RunDto` que retorna `run.Output` e `run.Prompt`
- `McpTools.cs` — `get_run_output`
- `ChatService.cs` — respostas de chat com output do agente

### 3. `PushError` sempre mascarado

`WorkItem.PushError` e `Demand.PushError` são retornados pela API e podem conter mensagens de erro de git com URLs.

**Regra**: todo valor atribuído a `PushError` DEVE passar por `Secrets.MaskUrls()`. Verificar:
- `AutoBranchPublisher.cs`
- `PullRequestService.cs`
- `DemandBranchService.cs`

### 4. Respostas de erro da API não vazam internals

```
❌ return Results.Problem(ex.ToString());           // stack trace + connection string
❌ return Results.Json(new { error = ex.Message });  // pode conter token
✅ return Results.Json(new { error = "Falha ao publicar o branch." }, statusCode: 500);
```

**Regra**: exceções não tratadas devem gerar mensagem genérica. `ex.Message` só pode ir para a resposta se o tipo da exceção é controlado (ex: `AgentNotFoundException`, `ProcessStartException` que nunca carrega tokens).

### 5. Tráfego host agent

O host agent escuta HTTP por padrão (`http://127.0.0.1:8787`). O token de autenticação viaja como `Bearer` em todas as requisições.

**Regra**: documentar que em redes não confiáveis (host agent em outra máquina) o tráfego DEVE usar HTTPS ou túnel seguro. Nunca logar o token do host agent.

Arquivos:
- `HostAgentApp.cs` — middleware de autenticação
- `RemoteProcessLauncher.cs` — `DefaultRequestHeaders.Authorization`

### 6. Variáveis de ambiente de processos filhos

`Secrets.ScrubEnvironment()` remove variáveis listadas em `SecretPrefixes` e `SecretNames`. A lista é fixa.

**Regra**: ao adicionar nova variável sensível ao `.env` ou `appsettings.json`, adicionar também a `Secrets.SecretNames` ou `SecretPrefixes`. Verificar que `ANTHROPIC_API_KEY` vazia é removida (já feito em `LocalProcessLauncher`).

### 7. Frontend — armazenamento de tokens

JWT em `localStorage` é vulnerável a XSS. Mitigação atual: a aplicação é auto-hospedada (homelab), sem scripts de terceiros.

**Regra**: nunca adicionar scripts de terceiros (analytics, CDNs) sem avaliar o risco de exfiltração de `localStorage`. Nunca logar tokens no console.

Arquivos:
- `frontend/src/app/auth/auth.service.ts` — `localStorage` com access/refresh tokens
- Nenhum `console.log` de dados sensíveis deve existir no código de produção

### 8. Chave SSH — a privada nunca sai

A chave SSH privada (`id_ed25519`) é gerada e usada no servidor/host agent. NUNCA deve ser retornada pela API.

**Regra**: endpoints de SSH (`/api/git/ssh`, `/v1/ssh`) retornam apenas: chave pública, fingerprint, status (existe/não existe). Verificar que `SshKeyStore` nunca expõe a chave privada.

### 9. MCP — superfície de ataque

Tokens MCP (`afk_...`) dão acesso a 21 ferramentas. Algumas retornam dados que podem conter tokens.

Ferramentas de risco:
- `get_run_output` — saída bruta do agente (pode conter tokens de git)
- `update_project` — aceita `repositoryUrl` que pode conter token (esperado, mas deve ser mascarada em respostas)
- `create_demand`, `create_work_item` — conteúdo vai para o prompt do agente

**Regra**: toda ferramenta MCP que retorna `AgentRun.Output`, `PushError` ou `RepositoryUrl` DEVE mascarar com `Secrets.MaskUrls()`.

### 10. Swagger em produção

Swagger está habilitado por padrão (`Swagger:Enabled`).

**Regra**: em deploy exposto à rede, desabilitar Swagger ou protegê-lo com autenticação.

## Checklist de revisão

Ao revisar código que toca nos fluxos acima, verifique:

1. [ ] Nenhum `logger.Log*` inclui stdout/stderr bruto sem mascarar?
2. [ ] `AgentRun.Output` é mascarado antes de persistir e transmitir?
3. [ ] `PushError` é mascarado em todos os caminhos de atribuição?
4. [ ] Respostas de erro da API não incluem `ex.Message` de exceções não controladas?
5. [ ] Respostas de erro da API não incluem stack traces?
6. [ ] Novas variáveis sensíveis estão em `Secrets.SecretNames/SecretPrefixes`?
7. [ ] Nenhum endpoint retorna chave SSH privada, token GitHub, ou token de host agent?
8. [ ] MCP tools que retornam output de agente mascaram URLs?
9. [ ] Frontend não loga tokens no console?
10. [ ] URLs com token são mascaradas em `GitException`, `PushError` e logs?

## Achados conhecidos (a corrigir)

Estes são problemas identificados no código atual que devem ser corrigidos:

1. **`AgentRun.Output` não é mascarado** — `CliAgentRunner` persiste a saída bruta; API, MCP e SignalR a retransmitem sem `MaskUrls`. Tokens de git podem estar no banco.
2. **`get_run_output` (MCP) retorna output sem mascarar** — qualquer client MCP com `afk_...` pode ler tokens.
3. **`RunDto` retorna output e prompt sem mascarar** — `CrudEndpoints.cs`.
4. **`ChatService` retorna output sem mascarar** — respostas de chat com saída do agente.
5. **SignalR `AgentEvent.Text` pode conter tokens** — truncado a 500 chars mas não mascarado.
6. **`AgentCliService` loga stdout/stderr brutos** de `claude auth status` — pode conter dados de sessão.

## Como rodar esta auditoria

1. Execute `grep -rn 'logger\.\(Log\|Warning\|Error\|Information\|Debug\)' backend/ --include='*.cs'` e revise cada ocorrência contra a regra 1.
2. Busque por `\.Output` e `\.PushError` em atribuições e retornos — verifique mascaramento.
3. Busque por `ex\.Message` em respostas HTTP — verifique se o tipo da exceção é controlado.
4. Busque por `Secrets.MaskUrls` — verifique se todos os pontos de saída estão cobertos.
5. Revise `Secrets.SecretNames` e `SecretPrefixes` contra as variáveis do `.env` e `appsettings.json` atuais.
