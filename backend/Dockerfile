ARG CLAUDE_CODE_VERSION=2.1.316

# Node.js oficial, só para copiar o runtime para a imagem da API (os CLIs dos agentes são pacotes npm)
FROM node:20-bookworm-slim AS node

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY AgentForge.sln ./
COPY AgentForge.Api/*.csproj AgentForge.Api/
COPY AgentForge.Domain/*.csproj AgentForge.Domain/
COPY AgentForge.Infrastructure/*.csproj AgentForge.Infrastructure/
COPY AgentForge.Orchestrator/*.csproj AgentForge.Orchestrator/
COPY AgentForge.Tests/*.csproj AgentForge.Tests/
RUN dotnet restore
COPY . .
RUN dotnet publish AgentForge.Api -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
ARG CLAUDE_CODE_VERSION

# Ferramentas que os agentes usam: git (worktrees), ssh (repositórios por SSH), curl (healthcheck) e Node.js
# com o CLI do Claude Code em versão FIXA. Para outro agente, instale o CLI dele aqui e registre um ICliAgentAdapter.
COPY --from=node /usr/local/bin/node /usr/local/bin/node
COPY --from=node /usr/local/lib/node_modules /usr/local/lib/node_modules
RUN ln -s ../lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \
    && ln -s ../lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx \
    && apt-get update \
    && apt-get install -y --no-install-recommends git openssh-client ca-certificates curl \
    && rm -rf /var/lib/apt/lists/* \
    && npm install -g "@anthropic-ai/claude-code@${CLAUDE_CODE_VERSION}" \
    && npm cache clean --force \
    && claude --version

# /data/repos (clones e worktrees) e a pasta de configuração/login do Claude Code pertencem ao usuário sem
# privilégios da API; volumes nomeados novos herdam esse dono. O Claude guarda tudo em CLAUDE_CONFIG_DIR.
ENV HOME=/home/app \
    CLAUDE_CONFIG_DIR=/home/app/.claude \
    DISABLE_AUTOUPDATER=1
RUN mkdir -p /data/repos /home/app/.claude \
    && chown -R $APP_UID /data/repos /home/app

WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "AgentForge.Api.dll"]
