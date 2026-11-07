# Validação do cenário F com máquinas reais

Roteiro para conferir, **numa instalação sua**, o que as fases 10 a 13 entregam. O que já foi verificado sem máquinas físicas e o
que fica por sua conta está no fim ([ADR 0021](adr/0021-validacao-cenario-f.md)). Gasta créditos do Claude (poucas execuções
pequenas); use um repositório de teste e o **modo automático** do projeto.

Material: o servidor (qualquer cenário A–E) e **uma outra máquina Linux ou macOS com Docker** (a "B"). Se só tiver Windows na
outra máquina, faça os passos 1 a 6 e 9 a 10 (sem container).

## 1. Máquina B: subir o host agent
```bash
git clone <este repositório> && cd AgentForge
export HostAgent__Docker__Enabled=true                      # só Linux/macOS com Docker
export HostAgent__Executables__node=node HostAgent__Executables__dotnet=dotnet
./scripts/host-agent.sh "" http://0.0.0.0:8787
```
Anote o token e a pasta dos clones. Libere a porta `8787` só para o IP do servidor.
**Esperado:** a mensagem `Host agent em http://0.0.0.0:8787 | pasta de trabalho: …`.

## 2. Servidor: cadastrar e testar
**Ambientes → Novo ambiente** (endereço, token, pasta dos clones **dentro** da pasta do passo 1, *Tarefas ao mesmo tempo* = 2) → **Testar**.
**Esperado:** *conectado*, `git`/`claude` com versão, *containers habilitados*. Errar o token deve dizer que o host agent recusou o token.

## 3. Login do Claude e chave Git da máquina B
**Agentes** → escolher o ambiente → deve aparecer *Conectado* (login que já existe em B) e **Testar** passar; se não, conecte pelo link.
**Git** → escolher o ambiente → **Gerar chave SSH**.
**Esperado:** chave `ssh-ed25519 … agentforge@<máquina B>` (diferente da do servidor) e o arquivo em `~/.agentforge/ssh` **em B**. Autorize-a
no GitHub (deploy key) se o repositório for privado e use **Testar acesso**.

## 4. Projeto na máquina B
Importe ou cadastre um repositório pequeno com *Ambiente de execução* = B (a importação do GitHub registra a deploy key de B).
**Esperado:** o clone aparece em `<pasta dos clones de B>/<id-do-projeto>/repo` **em B**, e não no servidor.

## 5. Sem container: uma demanda até Done
Crie uma demanda simples, aprove o plano (ou use modo automático).
**Esperado:** as tarefas andam até *Aguardando aprovação*/*Done*; worktrees em `…/worktrees/<id-da-tarefa>` **em B**; os commits no branch
`agentforge/task-<id>` do clone de B; **nenhum** push. O painel de **Carga** mostra a tarefa rodando em B.

## 6. Fila e limite
Com *Tarefas ao mesmo tempo* = 1 em B e `ORCHESTRATOR_CONCURRENCY=4`, inicie duas demandas de projetos de B.
**Esperado:** o painel mostra `1 / 1` rodando e `1 esperando vaga`; um projeto do ambiente padrão anda ao mesmo tempo.

## 7. Container por tarefa (B com Docker)
**Perfis → Construir imagem** (ambiente B) para *Node 22* (ou o que o projeto usa). Edite o projeto: *Perfil de stack* = esse perfil. Rode uma demanda.
**Esperado:**
- o log da construção aparece na tela (primeira vez: alguns minutos);
- durante a tarefa, em B, `docker ps` mostra `af-…` e `docker inspect <id> --format '{{.HostConfig.Privileged}} {{.HostConfig.CapDrop}} {{.HostConfig.Binds}}'`
  dá `false [ALL]` e **dois** binds: a pasta do projeto (mesmo caminho) e `…/.claude:/agent-config`; **sem** `docker.sock`;
- ao terminar, o container some (`docker ps -a | grep af-` vazio);
- a tarefa chega a *Aguardando aprovação* com commit no worktree (o `git` dentro do container enxerga o repositório).

## 8. Revisão em outra máquina
Edite o projeto: *Tech Lead roda em* = o ambiente padrão (ou outra máquina leve). Rode uma tarefa.
**Esperado:** o Tech Lead aprova/reprova com base no diff; a execução dele aparece como rodada **no outro ambiente** e o prompt diz que
não há repositório; Dev e QA continuam em B.

## 9. Queda e retomada
Com uma tarefa em andamento, pare o host agent em B (`Ctrl+C`; com container, espere o passo atual falhar).
**Esperado:** a tarefa **não** vira Bloqueada; mostra *Aguardando o ambiente voltar…*; o painel lista a espera com tentativas crescentes
(15 s, 30 s…). Suba o host agent de novo: a tarefa retoma sozinha e conclui. Repita **reiniciando a API** durante a espera: nada se perde.

## 10. Limpeza
Cancele as tarefas de teste; apague o projeto e o ambiente (um ambiente em uso por projeto ou papel não apaga: 409). Na máquina B,
`docker image prune` remove as imagens `agentforge/stack:*`.

Se algum **Esperado** não acontecer, anote o passo, o texto da tela e `docker compose logs api` (servidor) e o terminal do host agent (B).
