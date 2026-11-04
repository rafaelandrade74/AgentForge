# ADR 0016 — Agentes podem instalar dependências no container

## Contexto
Para evoluir um projeto, o agente precisa rodar e buildar a aplicação: Node em versão específica, Python, pacotes do
sistema etc. A imagem da API roda como usuário sem privilégios e não permitia `apt install`.

## Decisão
- A imagem da API instala `sudo` e, com `AGENT_SUDO=true` (padrão), concede ao usuário `app` sudo sem senha
  **dentro do container**. Os agentes (`claude`, `git`, `npm`, `dotnet`...) passam a poder fazer `sudo apt-get install`,
  instalar outro Node (nvm/n), Python, etc.
- No compose, `no-new-privileges` fica controlado por `API_NO_NEW_PRIVILEGES` (padrão `false`, necessário ao sudo) e
  o container recebe só as capacidades que apt/dpkg usam (`SETUID`, `SETGID`, `CHOWN`, `DAC_OVERRIDE`, `FOWNER`,
  `FSETID`, `KILL`, `AUDIT_WRITE`). Continua sem socket do Docker, sem `--privileged` e com `cap_drop: ALL` de base.
- Endurecer: `AGENT_SUDO=false` e `API_NO_NEW_PRIVILEGES=true` no `.env`, depois `docker compose up -d --build api`.

## Consequências
- Um agente pode alterar qualquer coisa **no container**: ler o login do Claude (volume `claude-config`) e a chave SSH
  (volume `git-ssh`), que já eram legíveis pelo usuário `app`; agora também pode mudar binários e configuração do
  sistema do container. Não alcança o computador nem outros containers (sem socket do Docker).
- O que é instalado vive na camada do container: some quando a imagem é recriada (`--build`) ou o container é
  recriado. Dependências permanentes devem entrar no `backend/Dockerfile`.
- Modos 1 e 3 (host agent / nativo): o agente roda com as permissões do seu usuário no computador. A aplicação não
  concede `sudo` no computador; se quiser, configure o `sudoers` por sua conta e risco.
