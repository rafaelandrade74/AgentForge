# ADR 0001 — Monorepo e stack

## Decisão
Monorepo com `backend/` (.NET 8, Clean Architecture leve: Api, Domain, Infrastructure, Orchestrator, Tests), `frontend/` (Angular 18, standalone components, signals), `prompts/` (um prompt de sistema por papel) e `docker-compose.yml` com web (Nginx), API e PostgreSQL 16.

## Notas
- Os projetos .NET têm `TargetFramework` net8.0. Em máquinas só com o SDK 10, os testes locais precisam de `DOTNET_ROLL_FORWARD=Major`.
- O Nginx do front faz proxy de `/api`, `/hubs` e `/health` para a API.
- A API também é publicada em `localhost:8081` para depuração.
