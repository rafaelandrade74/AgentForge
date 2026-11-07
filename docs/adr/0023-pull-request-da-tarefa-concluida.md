# ADR 0023 — Pull request da tarefa concluída

## Contexto
Até aqui o trabalho terminava no branch `agentforge/task-<id>` do clone do servidor ("o merge é seu"), sem caminho para o GitHub: a
deploy key é somente leitura e nenhum agente faz push. Falta o passo final natural: depois que a tarefa passou por todas as etapas
e uma pessoa aprovou a entrega, publicar o branch e abrir um **pull request** para `develop` ou para a branch principal do projeto, com
o destino configurável pela interface ou pelo MCP.

## Decisão
- **Quem faz:** o **AgentForge**, por ação **humana** — botão *Abrir PR* na tarefa concluída (Done) ou a ferramenta MCP
  `open_pull_request`. **Os agentes continuam sem credencial de escrita** e sem acesso ao token; o princípio "nenhum agente faz push"
  vale. O PR não é aberto sozinho: a aprovação da entrega continua sendo o portão, e abrir o PR é uma segunda decisão explícita.
- **Credencial:** token do GitHub **por projeto** (fine-grained: *Contents* e *Pull requests* de escrita só naquele repositório),
  guardado **protegido** (Data Protection, o mesmo `ISecretProtector` dos tokens de ambientes) em `Project.ProtectedGitHubToken`.
  Nunca volta pela API (só `hasGitHubToken`), nunca entra em log, e **não passa pelo MCP** (um segredo no MCP iria parar na
  conversa do modelo): o token só se cadastra na tela do projeto. Formato validado (`[A-Za-z0-9_-]{20,400}`) porque entra numa URL.
- **Destino:** `Project.PullRequestBaseBranch` (ex.: `develop`); vazio = a branch base do projeto. Editável na tela do projeto, em
  `PUT /api/projects`, em `create_project` e na nova ferramenta MCP `update_project` (atualização parcial: só o que foi informado). Cada
  PR ainda pode sobrescrever o destino (campo ao abrir; argumento `baseBranch`).
- **Push:** `WorkspaceManager.PushTaskBranchAsync`, na **máquina do ambiente do projeto** (onde mora o clone), para uma URL https
  explícita com o token (`https://x-access-token:TOKEN@github.com/<dono>/<repo>.git`; o clone original pode ser SSH com deploy key de
  leitura). O **refspec é fixo**: `refs/heads/agentforge/task-<id>:refs/heads/agentforge/task-<id>` — só o branch da tarefa, nunca a
  base nem outro nome, e nunca `--force`. A URL aceita só `https://` (e `file://` para testes); as exceções do git passam por
  `Secrets.MaskUrls`; o serviço não registra a mensagem do git no log. O token chega ao host agent no argumento do processo, como já
  acontece com URLs `https://token@…` de clone.
- **PR:** `GitHubClient.CreatePullRequestAsync` (título = título da tarefa; corpo = descrição, critérios de aceite, aviso de
  dependências incorporadas e a origem). Já existe PR aberto para o branch: devolve o existente (`alreadyExisted`). O número e a URL
  ficam em `WorkItem.PullRequestUrl/Number` e a operação é **idempotente**: repetir devolve o mesmo PR sem nova chamada ao GitHub.
- **Condições:** tarefa em `Done`, projeto em github.com com token cadastrado, destino válido (`^[A-Za-z0-9][A-Za-z0-9._/-]*$`, sem
  `..`). Se o push dá certo e o GitHub recusa o PR (ex.: destino inexistente), o erro diz que o branch foi publicado e a tarefa não
  registra PR, então basta corrigir e tentar de novo (o push repetido é no-op).
- **Visibilidade:** `get_task`/`get_demand` indicam `open_pull_request` como próxima ação; `list_pending_actions` ganhou
  `readyForPullRequest` (tarefas Done sem PR em projetos com token, com a branch de destino).

## Consequências
- O branch de uma tarefa com dependências carrega também o trabalho das dependências (o worktree nasce delas): o corpo do PR avisa, e
  o PR da dependência deve ser mesclado antes (ou o mesmo diff aparecerá nos dois).
- Quem tem o token do projeto pode publicar branches `agentforge/task-*` naquele repositório; o token deve ser limitado ao
  repositório e às duas permissões. Trocar a chave de proteção (volume `dp-keys`) obriga a informar o token de novo (mensagem
  explícita).
- Fora do escopo: abrir PR automaticamente, PR em GitLab/Gitea, merge pelo AgentForge, e reabrir PR depois de fechado.
