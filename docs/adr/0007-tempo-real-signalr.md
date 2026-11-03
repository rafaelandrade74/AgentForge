# ADR 0007 — Tempo real com SignalR

## Decisão
- Hub `/hubs/runs` (`RunsHub`, `[Authorize]`). O cliente chama `JoinProject(projectId)` / `LeaveProject(projectId)` e passa a receber só o que é daquele projeto (grupo `project:{id}`).
- Mensagens do servidor para o cliente (JSON camelCase, enums como texto):
  - `runEvent` — `AgentEvent { runId, projectId, demandId, workItemId, role, kind, text, at }`. `kind` vem do adapter (`assistant`, `tool`, `tool_result`, `system`, `result`, `raw`, `log`...) e o runner acrescenta `run_started` e `run_finished` (`text` = status final: Succeeded, Failed, Cancelled).
  - `workItemChanged` — `{ projectId, demandId, workItemId, state, reworkCount, blockedReason, branchName, at }`, a cada transição (inclusive criação em Backlog e bloqueio por erro interno).
  - `demandChanged` — `{ projectId, demandId, status, at }` (Refining, InProgress, Failed, Done).
- O orquestrador não conhece o SignalR: o runner usa `IAgentEventPublisher` e os processadores usam `IWorkflowEventPublisher` (implementações nulas por padrão). A API as substitui por `SignalRAgentEventPublisher` e `SignalRWorkflowEventPublisher`. Falha ao transmitir é só registrada em log e nunca derruba o agente.
- Latência: o evento é publicado dentro do loop de leitura do stdout, linha a linha, sem fila intermediária. Um teste mede a diferença entre a hora impressa pelo agente e a hora de chegada no cliente (limite de 2 s).
- Autenticação: WebSockets não enviam cabeçalhos, então o cliente passa o JWT em `?access_token=`; o `JwtBearerEvents.OnMessageReceived` só o aceita para caminhos `/hubs`. A query string pode aparecer em logs de proxy; é a prática padrão do SignalR e aceitável para o uso em homelab com usuário único.
- Nginx já repassa `/hubs/` com `Upgrade`/`Connection` e `proxy_read_timeout` de 1 h; `/swagger` também passa a ser repassado.
- Eventos não são persistidos nem reenviados: ao abrir uma tela, o front carrega o estado atual por REST e depois se inscreve no grupo (Fase 7). A linha do tempo das execuções continua vindo de `AgentRun`.

## Dependências novas
`Microsoft.AspNetCore.SignalR.Client` (só no projeto de testes). O servidor SignalR já faz parte do ASP.NET Core.
