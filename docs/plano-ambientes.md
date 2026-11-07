# Plano: ambientes de execução por stack

Continuação das fases do projeto (0 a 9 concluídas; 10 a 14 feitas; falta rodar o roteiro com máquinas reais). Cada fase tem ADR, testes e commit `fase-N: <resumo>`; paramos para
validação antes da seguinte.

**Decisões já tomadas:** o token de cada máquina fica no banco, **criptografado** (Data Protection, chaves em volume próprio);
o tipo `docker-image` (container por tarefa) existe **só em máquinas com host agent que já tenham Docker** — nunca montamos o
socket do Docker no container da API (acesso ao socket equivale a administrador na máquina).

## Fase 10 — Ambientes e várias máquinas (feita; ADR 0017)
Entidade `ExecutionEnvironment`, `Project.EnvironmentId`, resolução do launcher e da pasta de clones por projeto, API e tela
**Ambientes** (cadastro, teste de conexão com as ferramentas da máquina), token criptografado, importação do GitHub com ambiente.
Pronto quando: dois projetos rodam em máquinas diferentes na mesma instalação; máquina offline deixa a execução falhando com motivo;
instalações existentes continuam funcionando sem mexer em nada.

## Fase 11 — Git e login por ambiente (feita; ADR 0018)
Clone, worktree e diff já ocorrem na máquina do projeto (fase 10). Agora: **chave SSH gerada e testada por ambiente** (o host agent
gera e guarda a dele; a tela **Git** escolhe o ambiente), importação do GitHub registrando a deploy key da máquina escolhida, aviso
ao trocar o ambiente de um projeto com clone existente (o clone não é movido) e **login do Claude por ambiente** (tela **Agentes**
escolhe o ambiente; status, teste e sessões de login isolados).
Pronto quando: o ciclo completo (clone de repositório privado, worktree, agente, diff) roda num host agent remoto e há testes de
isolamento entre ambientes.

## Fase 12 — Perfis de stack e container por tarefa (feita; ADR 0019)
- `StackProfile`: imagem ou Dockerfile, pacotes, versões (Node/.NET/Python), comandos de build e teste, rede permitida.
  Perfis prontos (Node, .NET, Python) e perfis do usuário, vários por projeto.
- Novo tipo de ambiente `docker-image` (**só host agent com Docker**): cada tarefa sobe um container do perfil com o worktree
  montado e o remove ao final. Sem `--privileged`, sem socket do Docker, com limites de CPU e memória. O host agent ganha a
  operação "subir container" e só aceita imagens dos perfis cadastrados, montando apenas o worktree.
- Build do Dockerfile na máquina do ambiente, com o log visível.
Pronto quando: um projeto Node 22 e um .NET 8 rodam lado a lado em containers diferentes, com `npm ci` e `dotnet test` dentro.

## Fase 13 — Ambiente por papel, fila e carga (feita; ADR 0020)
Sobrescrita por papel no projeto (Dev e QA no ambiente da stack; Tech Lead e Security no leve), fila e concorrência máxima por
ambiente, novas tentativas quando a máquina volta, painel de carga/estado, MCP e documentação atualizados.
Pronto quando: uma demanda usa dois ambientes entre os papéis e a queda de uma máquina não perde tarefas.

## Fase 14 — Documentação e validação ponta a ponta (feita na parte automatizável; ADR 0021)
Cenário "F" (várias máquinas e stacks) no README, no getting-started e no site; validação real com um host agent em outra máquina
e um container por tarefa; correção das lacunas.
