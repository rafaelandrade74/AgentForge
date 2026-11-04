ARG CLAUDE_CODE_VERSION=2.1.316
# Base da API. O padrão é o SDK do .NET: os agentes rodam build/testes dos projetos (o de exemplo é .NET) dentro
# deste container. Para um container menor e sem build de .NET, use mcr.microsoft.com/dotnet/aspnet:8.0.
ARG API_BASE_IMAGE=mcr.microsoft.com/dotnet/sdk:8.0

# Node.js oficial, só para copiar o runtime para a imagem da API (os CLIs dos agentes são pacotes npm)
FROM node:20-bookworm-slim AS node

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY AgentForge.sln ./
COPY AgentForge.Api/*.csproj AgentForge.Api/
COPY AgentForge.Domain/*.csproj AgentForge.Domain/
COPY AgentForge.Infrastructure/*.csproj AgentForge.Infrastructure/
COPY AgentForge.Orchestrator/*.csproj AgentForge.Orchestrator/
COPY AgentForge.Execution/*.csproj AgentForge.Execution/
COPY AgentForge.HostAgent/*.csproj AgentForge.HostAgent/
COPY AgentForge.Tests/*.csproj AgentForge.Tests/
RUN dotnet restore
COPY . .
RUN dotnet publish AgentForge.Api -c Release -o /app --no-restore

FROM ${API_BASE_IMAGE} AS runtime
ARG CLAUDE_CODE_VERSION
ARG APP_UID=1654

# Ferramentas que os agentes usam: git (worktrees), ssh (repositórios por SSH), curl (healthcheck) e Node.js
# com o CLI do Claude Code em versão FIXA. Para outro agente, instale o CLI dele aqui e registre um ICliAgentAdapter.
COPY --from=node /usr/local/bin/node /usr/local/bin/node
COPY --from=node /usr/local/lib/node_modules /usr/local/lib/node_modules
RUN ln -s ../lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \
    && ln -s ../lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx \
    && apt-get update \
    && apt-get install -y --no-install-recommends git openssh-client ca-certificates curl sudo \
    && rm -rf /var/lib/apt/lists/* \
    && npm install -g "@anthropic-ai/claude-code@${CLAUDE_CODE_VERSION}" \
    && npm cache clean --force \
    && claude --version

# /data/repos (clones e worktrees) e a pasta de configuração/login do Claude Code pertencem ao usuário sem
# privilégios da API; volumes nomeados novos herdam esse dono. O Claude guarda tudo em CLAUDE_CONFIG_DIR.
ENV HOME=/home/app \
    CLAUDE_CONFIG_DIR=/home/app/.claude \
    DISABLE_AUTOUPDATER=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1
RUN mkdir -p /data/repos /home/app/.claude /home/app/.ssh \
    && chmod 700 /home/app/.ssh \
    && chown -R $APP_UID /data/repos /home/app

# Os agentes precisam instalar o que o projeto exige (apt, outra versão de Node, Python...). Com AGENT_SUDO=true
# (padrão) o usuário da API ganha sudo sem senha DENTRO deste container. Efeito: um agente pode alterar tudo no
# container (não no computador). Para desligar: AGENT_SUDO=false e API_NO_NEW_PRIVILEGES=true no .env. Ver ADR 0016.
ARG AGENT_SUDO=true
RUN if [ "$AGENT_SUDO" = "true" ]; then \
      echo "#${APP_UID} ALL=(ALL) NOPASSWD:ALL" > /etc/sudoers.d/agentforge && chmod 440 /etc/sudoers.d/agentforge; \
    fi

WORKDIR /app
COPY --from=build /app .
USER ${APP_UID}
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "AgentForge.Api.dll"]
