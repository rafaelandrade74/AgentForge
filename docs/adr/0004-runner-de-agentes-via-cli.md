# ADR 0004 — Runner de agentes via CLI

## Decisão
- `IAgentRunner.RunAsync(AgentRequest, CancellationToken)` é implementado por um único `CliAgentRunner` (projeto `AgentForge.Orchestrator`). Ele cuida do que é comum: iniciar o binário com `Process` (argumentos em `ArgumentList`, sem shell), usar o worktree como diretório de trabalho, enviar o prompt pelo **stdin**, ler o stdout linha a linha publicando eventos, aplicar timeout (padrão 30 min) e persistir o `AgentRun` (criado como `Running`, fechado com status, saída, custo e duração).
- O que muda por CLI fica em `ICliAgentAdapter`: `Executable`, `BuildArguments`, `ParseEvent(linha)` e `ParseResult(linhas, exitCode)`. O adapter é escolhido pelo `AgentRoleConfig.AdapterName` via `AgentAdapterRegistry` (adapters registrados no DI). O MVP só tem o `ClaudeCodeAdapter`.
- Cancelamento e timeout matam a **árvore de processos** (`Kill(entireProcessTree: true)`). Cancelado pelo chamador → `Cancelled`; timeout ou falha ao iniciar o binário → `Failed`, com o motivo no `Output`.
- `ClaudeCodeAdapter` monta `claude -p --output-format stream-json --verbose [--append-system-prompt] [--allowedTools] [--max-turns] [--model]`. Flags conferidos com `claude --help` (v2.1.316); `--max-turns` não aparece mais na ajuda, mas foi aceito num teste real. O binário pode ser trocado com `CLAUDE_BIN`.
- O JSON final é extraído da resposta por `JsonBlock.ExtractLast` (último bloco ```json válido, ou o texto inteiro se for JSON). O runner não valida schema: `AgentRunResult.Json` vem `null` quando não há JSON válido, e o orquestrador (Fase 5) aplica o schema por papel e a regra de reexecutar uma vez.
- `IAgentEventPublisher` recebe os eventos; a implementação padrão descarta. A Fase 6 liga o SignalR.

## Testes
- Adapter fake (`sh -c`) prova prompt via stdin, eventos, persistência, falha com stderr, timeout e cancelamento (inclusive processos netos). Esses testes só rodam em Linux/macOS.
- Teste real opcional: `AGENTFORGE_CLAUDE_INTEGRATION=1` e `AGENTFORGE_TEST_DB=<connection string Postgres>`; gasta créditos (cerca de US$ 0,17 por execução na primeira chamada, por causa do cache de prompt).

## Observações
- Em Windows, `claude` precisa ser um `.exe` no PATH (`UseShellExecute=false`); em produção o runner roda no container Linux.
- Ferramentas permitidas passam por `--allowedTools`; sem `--permission-mode`, ferramentas fora da lista são negadas no modo headless.
