# ADR 0021 — Validação do cenário F (várias máquinas, perfis e containers)

## Contexto
A fase 14 pede validar ponta a ponta o que as fases 10 a 13 entregaram (ambientes, credenciais por ambiente, perfis de stack com
container por tarefa, fila por ambiente, ambiente por papel e retomada) e corrigir as lacunas. Este ambiente de trabalho é um
único computador Windows com Docker Desktop: não há uma segunda máquina nem um host agent Linux nativo.

## O que foi verificado (e como)
| Item | Verificação | Resultado |
|---|---|---|
| Ambientes, token criptografado e persistência da chave (volume `dp-keys`) | Stack real no Docker, reinício e recriação do container da API | ok (fase 10) |
| Host agent real em **outra máquina** (aqui: processo nativo no Windows, falando com a API do Docker) | Cadastro, teste, chave SSH gerada **na máquina** (privada não sai de lá), `git` usando a chave (`known_hosts` criado lá), login do Claude daquela máquina, teste de acesso | ok (fase 11) |
| Isolamento entre ambientes (chave, login, cache, sessões) | Testes automatizados com dois host agents reais em processo | ok (fases 11 e 13) |
| Host agent **Linux** com Docker real: build, tarefa inteira em container, papel em outro ambiente, queda e retomada | Ver "Validação com host agent Linux e Docker reais" | ok |
| Imagens dos três perfis prontos | `docker build` real a partir do Dockerfile vindo da API (sem contexto), e `docker run` com `--cap-drop ALL`, `no-new-privileges`, `--network none`, `--user 1000`, sem `docker.sock`: `claude 2.1.316`, `git`, `node` e `dotnet 8.0`/`python 3.12` respondem | ok (fases 12 e 14) |
| Argumentos do `docker run`, recusas (imagem, rede, pasta, `..`, `:`), build uma vez, remoção do container | Host agent real com um "docker" de mentira que registra os argumentos | ok (fase 12) |
| Fila por ambiente, limite, ambientes independentes | Orquestrador real com Postgres, git e máquinas de teste | ok (fase 13) |
| Queda e retomada sem Blocked, para tarefa, refinamento e revisão em outra máquina | Idem, derrubando e subindo a máquina de teste | ok (fase 13) |
| Migrations e painel de carga na stack real | Rebuild do Docker, API conferida | ok (fases 12 e 13) |

## Validação com host agent Linux e Docker reais (executada)
Com autorização explícita do dono do computador, o host agent **Linux** (imagem publicada de `AgentForge.HostAgent`, com o CLI do
Docker, git e um `claude` de mentira) rodou num container com o socket do Docker montado e a porta publicada **só em 127.0.0.1**, como
"outra máquina" com Docker de verdade; a API (stack real do compose) o cadastrou por `host.docker.internal`. O container foi removido
no fim, junto com imagens, ambientes, perfil, projeto e pastas de teste. O "claude" de mentira responde no formato `stream-json` do
adapter (zero créditos) e escreve no resultado o `cwd`, o `uid`, se há `docker.sock` e se `/agent-config` existe.

Resultado (demanda de 2 tarefas, a 2ª dependente da 1ª, modo automático, perfil com Dockerfile próprio, ambiente com limite 1):
- **Teste do ambiente:** conectado, `containersEnabled = true`; **Construir imagem** construiu pelo docker real (stdin, sem contexto) e
  a 2ª chamada não reconstruiu.
- **Ciclo completo até Done** (Scrum Master, Dev, Tech Lead, Security, QA, nas duas tarefas): o clone e os worktrees nasceram **na
  máquina Linux** (nenhum clone novo no servidor); cada execução do agente rodou num container novo (hostname distinto), com `cwd` =
  worktree **no mesmo caminho**, `uid` do host agent, **sem `docker.sock`**, `/agent-config` montado; `docker inspect`:
  `Privileged=false`, `CapDrop=[ALL]`, `no-new-privileges`, 1 GiB, 1 CPU e **exatamente dois binds** (pasta do projeto e login do
  agente). O branch da tarefa B continha o commit da A (dependência dentro do container). Ao fim, **nenhum container `af-*` sobrou**.
- **Ambiente por papel:** o Tech Lead (movido para um segundo ambiente) rodou fora do container, no diretório temporário da outra
  entrada, sem o repositório; Security, Dev e QA ficaram no ambiente e container do projeto.
- **Queda e retomada:** com o Dev rodando, `docker stop` do host agent: a tarefa **não** virou Bloqueada, o painel listou a espera
  ("Host agent inacessível… Network is unreachable") com tentativas crescentes; **reiniciar a API durante a espera** não perdeu nada;
  ao subir o host agent de novo a demanda terminou em Done sozinha. Duas execuções do Dev ficaram `Failed` (a que a queda
  interrompeu e a que o reinício da API interrompeu); a tarefa foi refeita, como previsto na ADR 0020.

## O que NÃO foi verificado
- **Duas máquinas físicas** em rede, firewall e latência reais (aqui o "outro computador" é um container no mesmo Docker).
- **Execução com o Claude de verdade** nas fases 10 a 13 (gasta créditos; usamos um CLI de mentira com o mesmo contrato). O roteiro
  [`docs/validacao-cenario-f.md`](../validacao-cenario-f.md) cobre isso.
- **Host agent fora de container** em Linux nativo, e o usuário do container quando o host agent não é root (aqui era root, então o
  `--user` foi `0:0`; o caso não-root está coberto só por teste unitário dos argumentos).

## Lacunas encontradas e corrigidas nesta fase
- **Documentação errada do host agent remoto:** o README mandava `./scripts/host-agent.sh <token> http://0.0.0.0:8787`, mas o
  script recebe `[pasta-dos-clones] [urls]` e gera o token sozinho. Corrigido (`./scripts/host-agent.sh "" http://0.0.0.0:8787`).
- **O script não mostrava o token**, que é justamente o que se cola na tela Ambientes da outra instalação; e a mensagem final só
  falava do cenário D. Os dois scripts agora mostram o token e distinguem os cenários D e F.
- **Construção de imagem quebrava em Docker sem buildx** (achado só na validação real): o host agent passava `--progress=plain`,
  flag que o `docker build` legado (pacote `docker.io` das distros, CLI sem buildx) **recusa**. Removido; teste ajustado para garantir
  que nenhuma flag opcional é usada.
- **Faltava o cenário F** no README, no `docs/getting-started.md` e no site (`docs/index.html`): adicionado, com verificação e
  tabela de problemas, mais o roteiro de validação com máquinas reais.

## Consequências
- O critério "uma demanda usa dois ambientes entre os papéis e a queda de uma máquina não perde tarefas" está comprovado por testes
  automatizados; a confirmação com hardware real é do roteiro. Qualquer divergência encontrada nele deve virar correção + teste.
- Pendências conhecidas que não bloqueiam: o limite de concorrência da máquina leve não conta revisões movidas para ela (ADR 0020);
  perfil de stack único por projeto; quedas no meio de uma execução longa reiniciam aquela execução.
