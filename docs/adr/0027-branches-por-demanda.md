# ADR 0027 — Branches por demanda (em vez de por tarefa)

**Status:** aceita  
**Data:** 2026-11-13

## Contexto

Cada tarefa tinha o seu branch (`agentforge/task-{id}`), criado a partir do branch base do projeto.
Dependências eram mergeadas no worktree no momento da criação (MergeDependencyAsync). Push e PR
eram por tarefa — uma demanda com N tarefas gerava N branches e N PRs.

Isso criava complexidade:

- Conflitos de merge na criação do worktree (ao combinar dependências).
- N branches publicados e N PRs por demanda dificulta a revisão.
- As dependências não se acumulavam naturalmente: cada tarefa precisava refazer o merge.

## Decisão

1. Cada demanda ganha um branch `agentforge/demand-{id}`, criado a partir de `origin/{baseBranch}`
   quando entra em InProgress (refinamento concluído).
2. Worktrees de tarefa nascem do branch da demanda (`git worktree add -b agentforge/task-{id} <dir> agentforge/demand-{demandId}`).
3. Quando uma tarefa conclui (Done), seu branch é mergeado de volta no branch da demanda via
   worktree temporário. A próxima tarefa já nasce com tudo que foi mergeado.
4. `MergeDependencyAsync` e o parâmetro `dependsOn` de `CreateWorktreeAsync` foram removidos.
5. Push e PR acontecem no branch da demanda (não mais por tarefa): automático ao completar a última
   tarefa, ou por ação manual (`POST /api/demands/{id}/push`, `POST /api/demands/{id}/pull-request`).
6. Pre-push hook atualizado: permite `agentforge/demand-*` além de `agentforge/task-*`; continua
   bloqueando main, develop, tags e outros branches.
7. Novos campos em `Demand`: `BranchName`, `PushedAt`, `PushError`, `PullRequestUrl`, `PullRequestNumber`.
8. Campos equivalentes em `WorkItem` mantidos para retrocompatibilidade com tarefas legadas.

## Consequências

- **Um PR por demanda** — revisão mais limpa.
- **Conflitos de merge mudam de momento:** em vez de na criação do worktree (ao combinar dependências),
  aparecem ao fazer merge da tarefa no branch da demanda (Done). O projeto-lock serializa os merges.
- **Tarefas paralelas** que tocam os mesmos arquivos podem conflitar no merge. Isso é inerente: a
  segunda tarefa foi escrita contra uma versão anterior do branch da demanda.
- **Retrocompatibilidade:** demandas/tarefas existentes (pré-migração) mantêm seus branches por tarefa.
  Endpoints por tarefa (`/work-items/{id}/push`, `/work-items/{id}/pull-request`) continuam funcionando.
- **Worktree temporário** para o merge da demanda (`_demand-merge-{demandId}`) é criado e removido
  atomicamente; o finally garante a limpeza mesmo em falha.
