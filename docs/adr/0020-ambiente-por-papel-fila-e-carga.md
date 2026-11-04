# ADR 0020 — Ambiente por papel, fila por ambiente e retomada quando a máquina volta

## Contexto
Com várias máquinas (ADR 0017–0019), três problemas apareceram: (1) uma máquina cheia ou lenta atrasava todas, porque a única
fila tinha um limite global; (2) revisar o diff de uma tarefa não precisa da máquina pesada da stack; (3) se a máquina caía no meio
de um passo, a tarefa ia para **Blocked** e dependia de uma pessoa para retomar.

## Decisão
### Concorrência e fila por ambiente
- `ExecutionEnvironment.MaxConcurrency` (1 a 32, padrão 1; editável na tela Ambientes): quantas tarefas **do ambiente do projeto**
  rodam ao mesmo tempo naquela máquina. O ambiente padrão do servidor usa `Orchestrator:MaxConcurrency`. Esse valor continua sendo o
  teto **global** de trabalhadores: para usar várias máquinas ao mesmo tempo, suba `ORCHESTRATOR_CONCURRENCY`.
- `EnvironmentGate` (um lock só): o trabalho que não cabe **não prende um trabalhador**; fica estacionado no ambiente e volta à fila
  quando uma vaga abre (`Leave` devolve os estacionados). Assim uma máquina cheia não impede as outras de trabalhar, e "estacionar" e
  "liberar" nunca se cruzam (nada é esquecido). A vaga vale do começo ao fim do processamento da tarefa/demanda.
- O ambiente "de casa" do trabalho é o do projeto (onde mora o worktree); se a leitura falhar, vale o padrão.

### Máquina fora do ar não bloqueia
- `HostUnreachableException` (subclasse de `ProcessStartException`) distingue "a máquina não respondeu" de recusa/erro. O
  `RemoteProcessLauncher` a lança em qualquer falha de rede; `CompletedProcess.Unreachable` e o `GitCli` a propagam (antes virava
  `GitException` e bloqueava o item).
- O runner marca `AgentRunResult.EnvironmentDown`; o `AgentExecutor` devolve `ExecOutcome.Unavailable` **sem gastar a 2ª tentativa**;
  os processadores lançam `EnvironmentDeferredException` (`ThrowIfUnavailable`). O orquestrador solta a vaga, **mantém o estado
  persistido** e tenta de novo com backoff (`EnvironmentRetryInitialSeconds` 15 s, dobrando até `EnvironmentRetryMaxSeconds` 300 s),
  indefinidamente até a máquina voltar. Vale para tarefas e para o refinamento de demandas.
- `WorkItem.WaitingReason` (persistido e enviado no tempo real) mostra "Aguardando o ambiente voltar…" na tarefa; é limpo quando a
  tentativa recomeça ou a tarefa transita. Reiniciar a API não perde nada (`ResumeAsync` recoloca os itens). Erros que exigem uma
  pessoa (token recusado, token ilegível, ambiente removido, erro do agente) continuam indo para Blocked/Failed.

### Ambiente por papel
- `ProjectRoleEnvironment` (projeto, papel, ambiente): **só Tech Lead e Security** podem rodar em outra máquina (a API recusa
  Dev, QA e Scrum Master). Dev e QA precisam do worktree, que mora no ambiente do projeto; o refinamento lê o repositório.
- O papel movido roda no diretório temporário da outra máquina, **sem o repositório e sem perfil de stack**: recebe o diff e a
  descrição no prompt, que avisa isso (`PromptBuilder`: "sem o repositório"). Isto tem um custo de qualidade: ele não navega pelo
  código nem roda scanners; revisões que dependem de mais contexto devem apontar a falta como achado.
- `AgentRequest.EnvironmentOverride` leva o pedido ao runner; `AgentRun.EnvironmentId` registra onde cada execução rodou (sem FK,
  o histórico sobrevive ao ambiente). Um ambiente que roda papéis de algum projeto não pode ser apagado (409).

### Carga
- `GET /api/environments/load` (JWT) e a ferramenta MCP `get_environment_load` (19ª, somente leitura): por ambiente, tarefas
  rodando/limite, na fila, agentes rodando agora (`AgentRun` Running) e projetos; mais a lista do que espera uma máquina voltar. A
  tela Ambientes mostra o painel e atualiza a cada 5 s.

## Consequências
- Uma tarefa ocupa uma vaga do ambiente do projeto mesmo enquanto o Tech Lead roda na máquina leve; a vaga da máquina leve não é
  contada (só aparece em "agentes agora"). O limite da máquina leve vale apenas para as tarefas dos projetos que moram nela.
- Máquina que nunca volta deixa o item esperando indefinidamente (visível no painel e na tarefa); cancelar a tarefa resolve.
- Queda **no meio** de uma execução longa do agente reinicia o passo daquela execução (o trabalho do agente não é retomado), mas
  commits já feitos no worktree permanecem.
