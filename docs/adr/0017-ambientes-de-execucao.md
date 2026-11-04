# ADR 0017 — Ambientes de execução (várias máquinas)

## Contexto
Até a fase 9, uma única chave global (`Agents:Launcher` = `local` | `host`) decidia onde todos os CLIs rodavam. Projetos de
stacks diferentes (Node 22, .NET 8, Python…) e máquinas diferentes não conviviam. A ideia é um cadastro de **ambientes**:
cada projeto roda na máquina cuja configuração serve à sua stack.

## Decisão
- **Entidade `ExecutionEnvironment`**: nome único, URL e token de um host agent, pasta dos clones **nessa máquina**
  (`WorkspaceRoot`) e rótulos livres da stack. O **ambiente padrão** (o launcher de `Agents:Launcher`, com `Workspaces:Root`)
  não é gravado: vale para todo projeto com `EnvironmentId` nulo, então instalações existentes seguem iguais, sem migração de dados.
- **`Project.EnvironmentId`** (nulo = padrão), com FK `Restrict`: um ambiente em uso não pode ser excluído (a API devolve 409),
  porque o clone do projeto mora nele.
- **`IEnvironmentLaunchers`** resolve o alvo (`EnvironmentTarget`: launcher + raiz dos clones) por projeto. `WorkspaceManager`,
  `CliAgentRunner` (pelo `ProjectId` da requisição) e `WorkItemProcessor` (validação do commit e diff) passam a usar o ambiente do
  projeto. O `RemoteProcessLauncher` de cada ambiente é reaproveitado enquanto URL e token não mudam. Os construtores antigos
  (que recebem um `IProcessLauncher`) continuam, via `SingleEnvironment`, para os testes.
- **Token do host agent**: guardado no banco **protegido** pelo Data Protection do ASP.NET (`ISecretProtector`); nunca é devolvido
  pela API nem aparece em log ou nas mensagens de erro. As chaves de proteção ficam fora do banco (`DataProtection:KeysDir`; no
  Docker, o volume `dp-keys` em `/home/app/.agentforge`), então um dump do Postgres, sozinho, não revela os tokens. Se a chave se
  perder, o ambiente avisa "informe o token de novo" em vez de falhar de forma obscura.
- **API** `/api/environments` (JWT somente: tokens de acesso do MCP não alcançam): CRUD, `POST /{id}/test` e `POST /default/test`
  (versões de `claude`, `git`, `node`, `dotnet` e existência da pasta). A URL aceita só `http(s)` sem usuário e senha.
- **Máquina fora do ar**: o `RemoteProcessLauncher` já traduz falha de rede em `ProcessStartException` ("Host agent inacessível…").
  Na execução do agente isso vira run `Failed` com a mensagem; no fluxo da tarefa vai para Blocked com o motivo; na demanda, Failed.
- **Importação do GitHub** aceita `environmentId`; em ambiente próprio a chave SSH do servidor não é gerada nem autorizada (a máquina
  precisa ter o seu acesso ao repositório — fase 11 traz as credenciais por ambiente).

## Consequências
- O servidor passa a enviar o token a URLs que o administrador cadastra (SSRF controlado): só quem tem login (JWT) cria
  ambientes, e o host agent continua exigindo token, lista de executáveis e pastas permitidas.
- Mudar o ambiente de um projeto não move o clone; o próximo uso clona na nova máquina.
- **Fora do escopo desta fase**, previsto no [plano](../plano-ambientes.md): credenciais Git e login do Claude por ambiente na
  interface (fase 11), perfis de stack e container por tarefa (fase 12), ambiente por papel, fila e concorrência por ambiente (fase 13).
  A tela **Agentes** e a tela **Git** continuam mostrando o ambiente padrão.
