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
| Imagens dos três perfis prontos | `docker build` real a partir do Dockerfile vindo da API (sem contexto), e `docker run` com `--cap-drop ALL`, `no-new-privileges`, `--network none`, `--user 1000`, sem `docker.sock`: `claude 2.1.316`, `git`, `node` e `dotnet 8.0`/`python 3.12` respondem | ok (fases 12 e 14) |
| Argumentos do `docker run`, recusas (imagem, rede, pasta, `..`, `:`), build uma vez, remoção do container | Host agent real com um "docker" de mentira que registra os argumentos | ok (fase 12) |
| Fila por ambiente, limite, ambientes independentes | Orquestrador real com Postgres, git e máquinas de teste | ok (fase 13) |
| Queda e retomada sem Blocked, para tarefa, refinamento e revisão em outra máquina | Idem, derrubando e subindo a máquina de teste | ok (fase 13) |
| Migrations e painel de carga na stack real | Rebuild do Docker, API conferida | ok (fases 12 e 13) |

## O que NÃO foi verificado
- **Host agent Linux/macOS com Docker real executando uma tarefa inteira** (clone montado no mesmo caminho, agente dentro do
  container, `git` do worktree dentro dele, container removido ao fim). O único recurso para isso aqui seria rodar o host agent num
  container com o socket do Docker montado, o que equivale a dar controle total do Docker a esse container; não foi feito. Os
  pedaços (build, flags, montagem, ciclo de vida) estão cobertos separadamente, mas a cadeia completa depende de você: roteiro em
  [`docs/validacao-cenario-f.md`](../validacao-cenario-f.md), passos 7 e 8.
- **Duas máquinas físicas** em rede, firewall e latência reais.
- **Execução com o Claude de verdade** nas fases 10 a 13 (gasta créditos; os testes usam CLIs de mentira com o mesmo contrato).

## Lacunas encontradas e corrigidas nesta fase
- **Documentação errada do host agent remoto:** o README mandava `./scripts/host-agent.sh <token> http://0.0.0.0:8787`, mas o
  script recebe `[pasta-dos-clones] [urls]` e gera o token sozinho. Corrigido (`./scripts/host-agent.sh "" http://0.0.0.0:8787`).
- **O script não mostrava o token**, que é justamente o que se cola na tela Ambientes da outra instalação; e a mensagem final só
  falava do cenário D. Os dois scripts agora mostram o token e distinguem os cenários D e F.
- **Faltava o cenário F** no README, no `docs/getting-started.md` e no site (`docs/index.html`): adicionado, com verificação e
  tabela de problemas, mais o roteiro de validação com máquinas reais.

## Consequências
- O critério "uma demanda usa dois ambientes entre os papéis e a queda de uma máquina não perde tarefas" está comprovado por testes
  automatizados; a confirmação com hardware real é do roteiro. Qualquer divergência encontrada nele deve virar correção + teste.
- Pendências conhecidas que não bloqueiam: o limite de concorrência da máquina leve não conta revisões movidas para ela (ADR 0020);
  perfil de stack único por projeto; quedas no meio de uma execução longa reiniciam aquela execução.
