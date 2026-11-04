# ADR 0019 — Perfis de stack e container por tarefa

## Contexto
Com ambientes (ADR 0017) e credenciais por ambiente (ADR 0018), cada projeto roda numa máquina, mas na stack que ela tem
instalada. Dois projetos na mesma máquina (Node 22 e Node 18, .NET 8 e 9) brigam por versões, e o agente tem acesso a tudo o que
o usuário da máquina alcança. Precisamos de um ambiente reprodutível por stack, com isolamento, sem dar ao AgentForge o controle do
Docker de ninguém.

## Decisão
- **`StackProfile`** (tabela nova): nome único, **imagem pronta** *ou* **Dockerfile** (um dos dois), rede (`bridge`/`none`), limite de
  CPUs e de memória. Três perfis prontos (**Node 22**, **.NET 8**, **Python 3.12**) são recriados a cada start (`StackProfileSeeder`),
  não editáveis nem removíveis; o usuário cria os seus. `Project.StackProfileId` (FK `Restrict`) escolhe o perfil; exige um ambiente.
- **O agente roda DENTRO do container.** Os Dockerfiles prontos trazem git, ssh, Node 20 e o Claude Code (versão fixa) e definem
  `CLAUDE_CONFIG_DIR=/agent-config`. O login do Claude da máquina (`HostAgent:Docker:ClaudeConfigDir`, padrão `~/.claude`) é montado
  nesse caminho. Só o processo do agente vai para o container; clone, worktree e diff continuam no host.
- **Só host agent com Docker, e só quando habilitado.** Nunca montamos o socket do Docker na API. O host agent ganhou
  `POST /v1/containers` e `POST /v1/images/ensure`, **desligados por padrão** (`HostAgent:Docker:Enabled`). O pedido nunca vira uma
  linha de `docker` livre: o host agent monta os argumentos (`ContainerRules.RunArguments`) com `--cap-drop ALL`,
  `no-new-privileges`, `--pids-limit`, CPU/memória do perfil, usuário do dono da pasta (nunca root), **sem `--privileged`** e **sem
  o socket**, montando **apenas a pasta do projeto** (clone + worktrees) e a pasta de login do agente.
- **Imagens permitidas**: as construídas pelo AgentForge (`agentforge/*`) sempre valem; imagens prontas só se estiverem em
  `HostAgent:Docker:AllowedImages` (exata ou terminando em `*`). Nome de imagem que começa com `-` ou tem caracteres fora do padrão é
  recusado (viraria opção do docker). Rede só `bridge`/`none`; a pasta montada precisa estar dentro de `HostAgent:Roots` (nunca a raiz
  nem `/`), sem `..`, `:` nem `,`, e o diretório de trabalho dentro dela.
- **Build na máquina do ambiente, com log.** A imagem de um Dockerfile tem tag `agentforge/stack:<hash do conteúdo>`: editar o perfil
  gera outra tag e reconstrói sozinho. `ensure` faz `docker image inspect` e, se faltar (ou com `force`), `docker build -t <tag> -` com
  o Dockerfile pelo **stdin** (sem contexto de arquivos: a construção não enxerga nada da máquina). O runner garante a imagem **antes**
  de iniciar o agente e fora do tempo limite dele; a tela **Perfis** tem **Construir imagem** e mostra o log (cauda de 8 mil caracteres).
- **Ciclo de vida**: cada container tem nome `af-<id>` e é removido (`docker rm -f`) quando o processo termina, é cancelado ou a API
  desconecta — matar o cliente do `docker` não derruba o container, então o host agent o remove à parte.
- **Mesmo caminho dentro do container.** O worktree guarda caminhos absolutos para o repositório (`gitdir:`); por isso a pasta do projeto
  é montada **no mesmo caminho**. Consequência: container por tarefa exige host agent em **Linux ou macOS** (recusado com mensagem em
  Windows).
- **Falhas claras**: ambiente sem Docker habilitado, perfil apagado, ambiente padrão (`local`) ou host fora do ar fazem a execução
  falhar com o motivo; nunca rodam fora do container em silêncio. O teste do ambiente mostra "containers habilitados".

## Consequências
- Imagens e containers moram no Docker da máquina do ambiente; apagar um perfil não remove imagens (use `docker image prune` lá).
- O agente não tem `sudo` no container (roda com o uid do dono da pasta): dependências extras entram no perfil/Dockerfile. Perfis
  com rede `none` não instalam nada em tempo de execução.
- O login do Claude montado em `/agent-config` é gravável pelo container (o CLI renova tokens ali): quem tem um perfil malicioso e
  `Enabled` no host agent alcança esse login. Por isso a lista `AllowedImages` existe e o recurso é opt-in por máquina.
- Fora do escopo: vários perfis por projeto/papel e fila por ambiente (fase 13).
