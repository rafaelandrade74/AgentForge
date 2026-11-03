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
WORKDIR /app
COPY --from=build /app .
# git: o WorkspaceManager clona repositórios e cria worktrees. /data/repos pertence ao usuário da API
# (um volume nomeado novo herda esse dono).
RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data/repos && chown $APP_UID /data/repos
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "AgentForge.Api.dll"]
