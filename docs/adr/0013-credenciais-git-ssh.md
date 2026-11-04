# ADR 0013 — Credenciais Git (chave SSH gerada pela interface)

## Contexto
No modo 2 (git dentro do container) não há credenciais prontas; clonar repositórios privados exigia montar chaves à mão
(não suportado e não testado até aqui).

## Decisão
- Tela **Git** (`/git`) e endpoints `/api/git/ssh` (estado, `generate`, remover, `test`), só com o login (um token do MCP não
  gera nem apaga chaves). `GitCredentialService` gera uma chave **ed25519 sem passphrase** com `ssh-keygen` (pasta `Git:SshDir`:
  volume `git-ssh` em `/home/app/.ssh` no Docker; `~/.agentforge/ssh` fora dele, **nunca** o `~/.ssh` do usuário), com
  permissões 600 (chave) e 700 (pasta). **Só a pública é devolvida**, com a impressão digital; a privada nunca sai do servidor
  nem é registrada em log. Gerar de novo exige `overwrite` explícito (a tela confirma); remover apaga os arquivos.
- O `LocalProcessLauncher` define `GIT_SSH_COMMAND` **apenas para o `git`** e **apenas quando a chave existe**:
  `ssh -i <dir>/id_ed25519 -o IdentitiesOnly=yes -o UserKnownHostsFile=<dir>/known_hosts -o StrictHostKeyChecking=accept-new -o BatchMode=yes`.
  `accept-new` confia no servidor na primeira vez e exige a mesma chave depois; `BatchMode` nunca espera digitação. Os agentes
  não recebem essa configuração (não precisam de rede: o push está bloqueado).
- **Testar acesso** roda `git ls-remote --heads -- <url>` (entrada validada: sem `-` inicial, espaços, controle; até 500
  caracteres; 40 s) e traduz os erros comuns (sem permissão, host não resolvido, host key, repositório não encontrado).
- **Modo host agent**: o git é o do computador, com as credenciais dele; a API informa `supported: false` com o motivo e a tela
  explica, sem oferecer gerar chave.
- Recomendação na tela: **deploy key somente leitura** por repositório (menor privilégio, já que ninguém faz push).

## Verificação
Testes (10) com `ssh-keygen` real (imagem `backend/Dockerfile.tests`, o SDK 8 + openssh-client): geração e permissões, só a pública
exposta, substituição com confirmação, remoção, modo host, validação do teste de acesso, `GIT_SSH_COMMAND` só para o git e
só com chave, endpoints (login obrigatório, token MCP recusado). Ao vivo no Docker: chave 600 em pasta 700 do usuário `app`,
`known_hosts` criado e o GitHub recusando a chave ainda não autorizada com a mensagem correta; URL HTTPS pública confirmada.

## Fora do escopo
Chave com passphrase, várias chaves por host/projeto, tokens HTTPS gerenciados pela tela, GitLab/Bitbucket além do SSH genérico.
