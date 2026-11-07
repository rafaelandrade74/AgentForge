# ADR 0024 — Limite de uso do agente: esperar a liberação em vez de falhar

## Contexto
A conta do agente (assinatura do Claude Code) tem uma janela de uso de poucas horas e um limite semanal. Ao esgotar, o CLI passa a
terminar com erro. Isso era tratado como falha comum: o `AgentExecutor` gastava a 2ª tentativa, a tarefa ia para **Blocked** depois
de algumas voltas e uma pessoa precisava descobrir que era só o limite, e retomar à mão. Faltava saber **se estamos esperando
créditos ou o reset do tempo de uso**, e quando.

## Decisão
### Detectar
- `ClaudeCodeAdapter.ParseResult` devolve `AgentResult.Limit` (`UsageLimit`) quando a execução falhou **e** há sinal de limite:
  evento `rate_limit_event` com `status: "rejected"` (traz `resetsAt` em segundos e `rateLimitType`), ou o texto do resultado
  (`usage limit reached|<epoch>`, `hit your limit · resets 3pm (America/Sao_Paulo)`, `resets Nov 12, 9am`, `rate limit`).
  `UsageLimitParser` lê o horário (com fuso quando há; data opcional). Só vale em execução com erro, então um sucesso que fala de
  "rate limit" no texto nunca é limite.
- `UsageLimitRules.Create`: reset válido (futuro e a no máximo 8 dias) → `RetryAt = reset + 60 s`; ilegível, passado ou distante →
  **sem horário**, e o sistema **sonda** a cada 5 min (a próxima execução serve de sonda).
- Tipos: `session` (janela de horas), `weekly`, `rate` (429 passageiro) e `unknown`.

### Esperar
- `IUsageLimitTracker` (singleton, em memória) guarda o limite **por ambiente e agente** (cada máquina tem a sua conta). Enquanto
  `RetryAt` está no futuro, o `CliAgentRunner` **nem inicia o CLI**: a execução termina na hora como limite em vigor, sem gastar
  tentativa nem crédito. Passado o `RetryAt`, a execução seguinte é a sonda: se passar, o limite é limpo; se falhar, é registrado de
  novo. Reiniciar a API só custa uma sonda.
- `AgentRunResult.UsageLimit` → `AgentExecutor` devolve `ExecOutcome.Unavailable` com o limite (sem gastar a 2ª tentativa) →
  `EnvironmentDeferredException.Limit`. O mesmo mecanismo da ADR 0020 solta a vaga, **mantém o estado** e agenda a nova tentativa,
  mas para **`RetryAt` do limite** (e não pelo backoff curto). Vale para tarefas e refinamento de demandas; nada vira Blocked.
- `WorkItem.WaitingReason` ganha o texto "Aguardando o agente liberar (limite da janela de uso; libera por volta de dd/MM HH:mm UTC)…".
  Sem coluna nova nem migration.

### Mostrar
- `GET /api/environments/usage-limits` e `usageLimits` em `GET /api/environments/load` (e na ferramenta MCP `get_environment_load`):
  ambiente, agente, tipo, `resetsAt` (nulo = sem horário), `retryAt` e a mensagem do CLI. `WaitingDto.cause` diz `environment` ou
  `usage-limit`.
- Faixa roxa global na barra ("Claude Code (…): limite da janela de uso atingido — libera às 15:00"), atualizada a cada 30 s, e
  selo "aguardando o limite de uso" na tela Ambientes. Horários no fuso do navegador.

## Consequências
- Créditos esgotados ou janela cheia deixam as tarefas **em espera, retomando sozinhas**; não há mais Blocked por causa disso.
- O formato exato da mensagem/evento de limite do Claude Code **não é contratado** pela Anthropic e pode mudar. Por isso a leitura é
  tolerante e o pior caso é "sem horário": o sistema sonda a cada 5 min em vez de acertar o reset. Ajustar `UsageLimitParser` e o
  `ClaudeCodeAdapterTests` se o CLI mudar.
- O limite é memória do processo: não aparece em outra instância da API (o projeto roda uma só).
- Execuções de tarefas já em andamento quando o limite estoura terminam como falha da execução (a saída explica); a tarefa continua no
  mesmo estado e repete o passo ao liberar.
