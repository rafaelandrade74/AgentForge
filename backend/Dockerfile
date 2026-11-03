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
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "AgentForge.Api.dll"]
