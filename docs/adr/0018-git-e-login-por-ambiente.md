# ADR 0018 — Git e login do agente por ambiente

## Contexto
A fase 10 ([ADR 0017](0017-ambientes-de-execucao.md)) fez clone, worktree, git e agentes rodarem na máquina do projeto, mas as
telas **Git** e **Agentes** só enxergavam o ambiente padrão: a chave SSH e o login do Claude de uma máquina remota tinham de ser
preparados à mão nela, e importar um repositório privado em outro ambiente não autorizava chave alguma.

## Decisão
- **Quem gera a chave é a própria máquina.** O host agent ganhou `GET /v1/ssh`, `POST /v1/ssh/generate` e `DELETE /v1/ssh`
  (atrás do mesmo token). Ele cria o par ed25519 em `HostAgent:SshDir` (padrão `~/.agentforge/ssh`, nunca o `~/.ssh` do usuário) com
  `ssh-keygen`, que **não** entra na lista de executáveis liberados. A privada nunca atravessa a rede: a API só recebe a chave
  pública, a impressão digital e a data. A lógica (`SshKeyStore`, em `AgentForge.Execution`) é a mesma do servidor.
- **O `git` da máquina usa a chave se ela existir.** O `LocalProcessLauncher` do host agent recebe `GitSshDirectory`; só quando há
  `id_ed25519` nessa pasta o `GIT_SSH_COMMAND` é definido (com `known_hosts` próprio, `accept-new`, `BatchMode`). Sem chave, valem as
  credenciais que a máquina já tem. A chave de um ambiente não vale em outro.
- **`GitCredentialService` resolve o ambiente.** Recebe `environmentId` (nulo = padrão do servidor): launcher `RemoteProcessLauncher`
  usa as rotas do host agent; `local` usa a pasta `Git:SshDir` do servidor; qualquer outro launcher continua "não suportado". Host agent
  fora do ar ou de versão antiga **não derruba a tela**: o estado volta `supported: false` com o motivo; gerar/apagar viram 409.
  O teste de acesso (`git ls-remote`) roda a partir da máquina do ambiente.
- **`AgentCliService` resolve o ambiente.** Status, teste, login por link e logout recebem `environmentId`. Cache de status, último
  teste e **sessões de login** são separados por ambiente: um login iniciado na máquina A não pode ser lido, cancelado nem
  concluído por uma requisição de B (nem pelo servidor). Novo login numa máquina só substitui o login pendente **da mesma máquina**.
  `AgentRequest.EnvironmentId` leva o ambiente ao teste de conexão (que não tem projeto). Host agent fora do ar aparece como
  "Host agent inacessível…" em vez de "CLI não encontrado".
- **API**: `?environmentId=<id>` em todas as rotas de `/api/git/ssh` e `/api/agents`. Ambiente inexistente ou com token ilegível é
  **409**, nunca um retorno silencioso ao ambiente do servidor (o que geraria a chave no lugar errado).
- **Importação do GitHub com ambiente** gera/usa a chave **da máquina escolhida** e a registra como deploy key somente leitura. Falha
  ao preparar a chave (máquina fora do ar) devolve 502 e não cria o projeto.
- **Interface**: um seletor "Ambiente" (só aparece se há ambientes cadastrados) nas telas Git e Agentes; a tela Ambientes ganhou os
  atalhos *Chave Git* e *Login do agente* (`?environmentId=`). O aviso global da barra lateral continua refletindo só o servidor.

## Consequências
- Cada máquina guarda a sua chave privada. Remover um ambiente não a apaga da máquina (ela é do host agent).
- Trocar o ambiente de um projeto continua não movendo o clone; a nova máquina precisa de acesso ao repositório (agora é só gerar a
  chave dela na tela Git e autorizá-la).
- Host agents antigos funcionam como antes para executar processos; só o gerenciamento de chave pede atualização (mensagem explícita).
- Fora do escopo, nas próximas fases: perfis de stack e container por tarefa (12), ambiente por papel e fila (13).
