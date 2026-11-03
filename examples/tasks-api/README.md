# TaskApi — projeto de exemplo

API mínima de tarefas em .NET 8 (armazenamento em memória), usada para validar o AgentForge de ponta a ponta.

| Rota | O que faz |
|---|---|
| `GET /tasks` | Lista as tarefas |
| `GET /tasks/{id}` | Busca uma tarefa (404 se não existir) |
| `POST /tasks` | Cria uma tarefa (`title` obrigatório; `status` opcional) |

Status possíveis: `Todo`, `InProgress`, `Done`.

```bash
dotnet build
dotnet test
dotnet run --project src/TaskApi
```

## Demanda usada na validação

> Adicionar filtro por status no endpoint de listagem: `GET /tasks?status=Done` deve devolver só as tarefas com esse status.
