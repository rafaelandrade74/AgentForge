# ADR 0028 — Migração de ambiente (mover projeto entre host agents)

**Status:** aceita  
**Data:** 2026-11-18

## Contexto

Mudar o `Project.EnvironmentId` aponta o projeto para outro ambiente mas não transfere os branches locais (demand e
task) que existem apenas no clone da máquina antiga. Branches com trabalho em andamento são perdidos porque vivem
somente no git local — o `pushurl` do clone é desabilitado (ADR 0005), então nada vai para o origin
automaticamente durante o desenvolvimento.

## Decisão

- **`POST /api/projects/{id}/migrate-environment`** (`MigrateEnvironmentInput { TargetEnvironmentId }`): migra o
  projeto de um ambiente para outro, usando o origin (GitHub) como intermediário para transferir branches.
- **Pré-condições**: nenhum `AgentRun` com `Status = Running` no projeto; push URL válida (`IPushUrlProvider`);
  destino ≠ origem.
- **Fluxo**:
  1. No ambiente **de origem**: fetch + push de cada branch ativo (`agentforge/demand-*` e `agentforge/task-*`) para
     o origin via a push URL do projeto (SSH ou HTTPS+token).
  2. Atualiza `Project.EnvironmentId` no banco.
  3. No ambiente **de destino**: clone/fetch (traz os branches como remote refs) + cria branches locais a partir de
     `origin/<nome>` (`EnsureLocalBranchesFromRemoteAsync`).
- **Branches transferidos**: demands `InProgress`, work items em estados ativos (`InDevelopment`..`AwaitingApproval`,
  `Blocked`), e demands/tasks `Done` ainda não publicados (`PushedAt == null`).
- **Falha parcial**: erro ao empurrar um branch é registrado como warning e não impede a migração dos demais. Se o
  destino estiver inacessível após o push, os branches já estão seguros no origin e serão obtidos na próxima execução.
- **`IWorkspaceManager.EnsureLocalBranchesFromRemoteAsync`**: novo método idempotente que cria branches locais a
  partir das remote tracking refs — necessário porque `CreateWorktreeAsync` procura branches locais.
- **Worktrees**: não são movidos. São recriados sob demanda pelo orquestrador (`CreateWorktreeAsync` é idempotente).
- **Clone antigo**: não é removido da máquina de origem (safety net; limpeza futura opcional).
- **`ProjectRoleEnvironment`**: não é alterado; a migração avisa se houver papéis apontando para o ambiente anterior.
- **`EnvironmentMigrationService`** (`AgentForge.Orchestrator.Environments`): encapsula a lógica; registrado como scoped.

## Consequências

- Migrar exige que o projeto tenha uma push URL configurada (SSH com chave de escrita, ou token HTTPS).
- Branches vão para o origin durante a migração — ficam visíveis no GitHub.
- O destino faz um clone completo na primeira migração; migrações subsequentes só fazem fetch.
- Tarefas em andamento retomam automaticamente no novo ambiente quando o orquestrador as processa.
