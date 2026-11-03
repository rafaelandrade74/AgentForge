# ADR 0006 — Orquestrador

## Máquina de estados
`WorkflowDefinition` é a única tabela de transições (estado + evento → próximo estado). Fluxo feliz:

`Backlog → (aprovação humana 1) → Todo → InDevelopment → TechLeadReview → SecurityReview → Qa → AwaitingApproval → (aprovação humana 2) → Done`

- Qualquer revisão ou QA reprovado devolve a tarefa ao Dev com os apontamentos; no **terceiro** retorno ela vai para `Blocked` (com `BlockedReason`) e espera decisão humana (`retry`, que zera o contador, ou `cancel`).
- A rejeição humana também volta ao Dev (o comentário vira um apontamento do papel `Human`), mas **não** conta para o limite de 3.
- As duas aprovações humanas (plano e entrega) são puladas quando `Project.AutoMode` é true: o orquestrador aplica o evento sozinho.
- Uma tarefa só sai de `Todo` quando todas as dependências estão em `Done`; ao concluir uma tarefa, as dependentes são reenfileiradas. Quando todas as tarefas da demanda terminam, a demanda vai para `Done`.
- Estados terminais: `Done` e `Cancelled`. Qualquer outro estado aceita `Cancel`.

## Execução
- `OrchestratorService` (`BackgroundService`) consome um `Channel` com `Orchestrator:MaxConcurrency` trabalhadores (padrão 1; um trabalhador ocupado por uma tarefa não olha as outras). O mesmo item nunca roda em dois trabalhadores; avisos que chegam enquanto ele está pendente geram uma nova passada.
- `WorkItemProcessor.StepAsync` executa **um** passo do estado atual e grava a transição (relendo o estado antes: se a tarefa foi cancelada nesse meio tempo, não faz nada). Por isso, **retomar = repetir o passo do estado salvo**.
- No start, o serviço encerra como `Failed` as execuções que ficaram `Running` ("Interrompida por reinício da API.") e reenfileira demandas em `Refining` e tarefas em estados retomáveis. Desligar a API cancela o agente em andamento sem mudar o estado da tarefa.
- Com concorrência maior que 1, Tech Lead e Security revisam **em paralelo** (evento `ReviewsApproved`; os achados dos dois contam como um único retorno). Com 1, as revisões são em sequência.
- Cada execução de agente usa um escopo de DI próprio (DbContext próprio), por isso revisões em paralelo são seguras.

## Saídas dos agentes
`OutputParsers` valida o JSON final de cada papel (planos sem ciclos e com dependências existentes, veredito coerente com os achados/critérios). Saída inválida ou execução com falha é **reexecutada uma vez** com o motivo anexado ao prompt; a segunda falha bloqueia a tarefa. O commit informado pelo Dev é conferido no worktree (existe, está no branch e há commits novos em relação à base). O diff para as revisões é embutido no prompt (limite de 100 mil caracteres).

## Ações humanas (API)
`POST /api/demands/{id}/start`, `/approve-plan`; `POST /api/work-items/{id}/approve`, `/reject`, `/retry`, `/cancel`. Cancelar mata o agente em execução, remove o worktree e apaga o branch. Concluir remove o worktree e mantém o branch.

## Consequências
- Novos estados: ajustar `WorkflowDefinition` e acrescentar o passo em `WorkItemProcessor`.
- Foram acrescentados `AgentRole.Human`, `DemandStatus.Failed` e `WorkItem.BlockedReason` (migration `AddBlockedReason`).
- Testes de orquestração rodam com Postgres e git reais e um CLI falso (`sh`), só em Linux.
- Eventos de mudança de estado para o front (SignalR) ficam para a Fase 6.
