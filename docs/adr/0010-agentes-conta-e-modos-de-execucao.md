# ADR 0010 — Tela Agentes (conta) e modos de execução

## Contexto
Exigir chave de API ou um comando manual de login era burocrático. Além disso, quem já tem o Claude instalado e logado no
computador queria usá-lo, e nem todo mundo quer Docker. Três cenários: **(1)** aplicação no Docker + CLI no computador,
**(2)** aplicação no Docker + CLI no container, **(3)** tudo no computador.

## Decisão — conta e login pela aplicação
- `ICliAgentAccount` é uma capacidade opcional do adapter: `claude auth status --json` (instalado/conectado/conta), comando
  de login, extração do link e teste. `AgentCliService` (singleton) faz o resto de forma genérica.
- **Tela Agentes** (`/agents`, `GET /api/agents`): mostra por agente se o CLI está instalado, se está conectado e a conta;
  **testa de verdade** com uma chamada mínima (`POST /{name}/test`, sem gravar `AgentRun`; o resultado fica em memória e a
  tela só testa sozinha quando ainda não há resultado); permite **desconectar** e **escolher o agente de cada papel**
  (a `AgentRoleConfig.AdapterName` só aceita agentes registrados). Faixa de aviso global quando um agente usado por algum
  papel não está pronto.
- **Login por link**: `claude auth login` roda sem TTY, imprime o link e espera o código no stdin. A API devolve o link; a
  pessoa autoriza no navegador e cola o código na tela; a API o repassa ao stdin do CLI e confere o resultado com
  `auth status` (não confia só no código de saída). Uma sessão por agente (iniciar outra cancela a anterior), timeout de
  10 min, código validado (sem quebras de linha, até 600 caracteres). O CLI guarda a credencial no próprio diretório; a
  aplicação **nunca lê nem guarda token** nem registra o código.

## Decisão — onde os CLIs rodam
- `IProcessLauncher` (projeto `AgentForge.Execution`) abstrai "iniciar um processo e ler stdout/stderr linha a linha":
  `LocalProcessLauncher` (neste processo/container) e `RemoteProcessLauncher` (no computador, via **host agent**). Runner,
  git (`GitCli`), `WorkspaceManager`, status/teste/login e o diff passam todos por ele; os caminhos são sempre do lado em
  que o processo roda (barras normais, que o Windows também aceita).
- Seleção por `Agents:Launcher` = `local` | `host` (+ `Agents:HostAgent:Url/Token`). `GET /api/agents/environment` informa o
  modo para a tela.
- **Host agent** (`AgentForge.HostAgent`, ASP.NET mínimo, `RollForward=LatestMajor`): protocolo HTTP/NDJSON
  (`POST /v1/processes`, `GET /v1/processes/{id}/events`, `POST .../stdin`, `DELETE`, `POST /v1/fs/{exists,mkdir,delete}`,
  `GET /v1/info`). Segurança: token (comparação em tempo constante), só executa os nomes liberados (`claude`, `git`), só
  roda/cria/apaga dentro de `HostAgent:Roots` (caminhos normalizados, sem `..`; nunca apaga a raiz), escuta em loopback por
  padrão. Se a API desconecta (cancelamento, queda), o host agent **mata a árvore de processos**; um processo que ninguém lê
  em 2 min também é morto.
- **Modo 3**: a API serve a interface compilada (`Frontend:Path`, com fallback de SPA que mantém `/api`, `/hubs`, `/swagger`
  e `/health` como 404 quando não existem) e roda com `Agents:Launcher=local`; precisa só de um PostgreSQL.
- **Reaproveitar o login do computador no modo 2**: `CLAUDE_CONFIG_SOURCE` troca o volume `claude-config` por uma pasta do
  computador (só funciona onde o login é arquivo; leia as ressalvas no README).
- Imagem da API: base `dotnet/sdk` por padrão (`API_BASE_IMAGE`), porque no modo 2 os agentes rodam `dotnet build/test` dentro
  do container; o override do modo 1 usa `aspnet` (o container só orquestra).

## Consequências
- O `claude` do computador (modo 1) é executado a pedido da API: o token do host agent equivale a "rodar `claude`/`git`
  nas pastas de trabalho". Documentado no README.
- Cada execução remota tem a latência de uma ida e volta HTTP por chamada de `git`; irrelevante frente ao tempo dos agentes.
- Os testes sobem o host agent real (Kestrel em porta aleatória) e exercitam streaming, kill, desconexão, restrições de
  caminho, runner, workspaces/git e login pelo launcher remoto (somente Linux/macOS: usam `sh`).
- Dependência nova: `Microsoft.Extensions.Configuration.Binder` (Orchestrator).
