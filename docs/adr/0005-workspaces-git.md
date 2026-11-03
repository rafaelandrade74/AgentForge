# ADR 0005 — Workspaces Git

## Decisão
- `WorkspaceManager` (`AgentForge.Orchestrator/Workspaces`) mantém, no volume `Workspaces:Root` (padrão `/data/repos`):
  - `{projectId}/repo` — um clone por projeto, feito uma vez; depois só `git fetch --prune`.
  - `{projectId}/worktrees/{workItemId}` — um `git worktree` por tarefa, no branch `agentforge/task-<id>`, criado a partir de `origin/<BaseBranch>`.
- `CreateWorktreeAsync` é idempotente: se o diretório já existe (retrabalho), devolve o mesmo; se só o branch existe, recria o worktree em cima dele, preservando os commits.
- `RemoveWorktreeAsync` remove o worktree e faz `prune`. O **branch é mantido** por padrão, porque guarda o trabalho pronto para merge; `deleteBranch: true` serve para tarefas canceladas.
- **Nada vai para o remoto:** o clone recebe `remote.origin.pushurl` inválido, então qualquer `git push` de um agente (que tem Bash) falha, inclusive para o branch base. O merge fica com o humano. Também são configurados `user.name`/`user.email` para os commits dos agentes.
- Operações que alteram o mesmo repositório (clone, fetch, worktree add/remove) são serializadas por projeto com um semáforo; projetos diferentes rodam em paralelo.
- `GitCli` chama `git` sem shell (`ArgumentList`), com `GIT_TERMINAL_PROMPT=0` (nunca fica preso pedindo credencial) e timeout de 10 min. URLs vazias ou começando com `-` são recusadas (injeção de opções); o clone usa `--`. Um clone que falha é apagado para a próxima tentativa começar limpa.

## Consequências
- A imagem da API agora instala `git` e `/data/repos` pertence ao usuário da API (não root). Um volume `repos` criado antes dessa mudança continua com dono root: remova-o (`docker volume rm agentforge_repos`).
- Repositórios privados ainda não têm autenticação: ficará para a Fase 8 (montar credenciais/chave SSH no container).
- O orquestrador (Fase 5) chama `CreateWorktreeAsync` ao iniciar a tarefa, grava `WorkItem.BranchName` e chama `RemoveWorktreeAsync` ao terminar ou cancelar.
