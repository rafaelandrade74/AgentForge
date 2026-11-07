# ADR 0025 — Publicação automática do branch da tarefa concluída (push pela mesma chave do pull)

## Contexto
Pela ADR 0023, o push do branch da tarefa só acontecia quando uma pessoa clicava em *Abrir PR*, e exigia o token do GitHub no projeto.
Na prática o trabalho ficava só no clone da máquina do AgentForge, e quem usa o sistema espera ver o branch no GitHub assim que a
tarefa conclui. Além disso, o clone já usa a chave SSH configurada (tela Git); pedir um token só para o push era redundante.

## Decisão
- **Quando:** ao chegar em **Done** (aprovada por uma pessoa, ou direto no modo automático), o servidor publica o branch
  `agentforge/task-<id>`. `IBranchPublisher` (Orchestrator) é chamado pelo `WorkItemProcessor` depois de remover o worktree;
  `AutoBranchPublisher` (Api) faz o push. Falha nunca desfaz o Done: o motivo fica em `WorkItem.PushError` e o sucesso em
  `WorkItem.PushedAt` (migration `AddBranchPush`).
- **Como (mesma chave do pull):** em projeto com URL **SSH** (`git@github.com:...`), o push vai para essa **URL SSH do projeto**, ou seja, pela
  **mesma chave SSH** do clone/pull (`GIT_SSH_COMMAND` do launcher; nos modos 1 e 3, as credenciais do git do computador). **Sem token.**
  O `origin` do clone segue com `pushurl` inválido (`NoPushUrl`): um `git push` comum feito por um agente continua falhando.
  Em projeto **HTTPS**, continua a URL `https://x-access-token:<token>@github.com/...` com o token do projeto.
- **O que continua valendo:** refspec fixo `refs/heads/<branch>:refs/heads/<branch>` — só o branch da tarefa, nunca a base. Os agentes
  continuam sem fazer push (o git deles não é chamado para isso; quem publica é o servidor). **Abrir o PR segue sendo ação humana**
  e exige o token do GitHub (é chamada à API, não ao git).
- **Botão:** `POST /api/work-items/{id}/push` e o botão *Publicar branch (git push)* na tarefa Done, para as que concluíram antes desta
  mudança ou cuja publicação automática falhou. *Abrir PR* publica antes, se preciso, e registra `PushedAt`.

## Consequências
- **A chave SSH precisa ter permissão de escrita.** A deploy key criada pelo *Importar do GitHub* é **somente leitura** (ADR 0014) e não
  consegue publicar: o GitHub responde "read only" e a tarefa mostra como corrigir (apagar a deploy key e adicioná-la de novo marcando
  *Allow write access*, ou usar uma chave da conta). O importador **não** foi alterado para criar chave de escrita.
- **Risco a ter em mente:** com a chave gravável, qualquer processo que rode como o mesmo usuário na mesma máquina e herde o
  `GIT_SSH_COMMAND` (inclusive o `git` chamado por um agente) tecnicamente consegue fazer push **se informar a URL SSH explicitamente** (um `git push` comum falha, porque o `origin` do clone não
  aceita push). A regra "agentes não fazem push" passa a depender também do prompt e da disciplina do agente, não só de falta de credencial. Em projeto HTTPS com token, o token não é exposto aos agentes
  (`Secrets.ScrubEnvironment`). Preferir deploy key **por repositório**, limitar o repositório e proteger a branch base no GitHub
  (branch protection) para a base nunca ser sobrescrita.
- Sem token e sem SSH (projeto HTTPS sem token), nada é publicado e a tarefa explica o motivo.
