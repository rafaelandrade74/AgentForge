# ADR 0029 — Grupos de Projetos

**Status:** Aceita  
**Data:** 2026-11-20

## Contexto

Projetos relacionados (ex.: api, front, migrations de um mesmo produto) precisam ser orquestrados juntos: uma única demanda gera tarefas distribuídas pelos repositórios membros.

## Decisão

Nova entidade `ProjectGroup` acima de `Project`. `Demand` pertence a **um** `Project` OU **um** `ProjectGroup` (exatamente um dos dois). `WorkItem` ganha `ProjectId?` obrigatório quando a demanda é de grupo (resolve o projeto-alvo).

### Entidades

- **ProjectGroup** — `Id`, `Name` (único, 200), `Description` (500), `CreatedAt`.
- **Project** — `+ProjectGroupId?` (FK → ProjectGroup, SetNull on delete).
- **Demand** — `ProjectId` → nullable; `+ProjectGroupId?` (FK, Cascade). Validação: exatamente um preenchido.
- **WorkItem** — `+ProjectId?` (FK → Project, Restrict). Todo o pipeline resolve o projeto por `item.ProjectId ?? demand.ProjectId`.

### Orquestrador

- **DemandProcessor**: para demandas de grupo, carrega os projetos do grupo, usa prompt ScrumMaster variante que lista os projetos e pede atribuição por tarefa (`"project": "<nome>"`). Cria branch de demanda em cada projeto que tem tarefas.
- **WorkItemProcessor**: resolve projeto por `item.ProjectId ?? demand.ProjectId`. Nenhuma outra mudança no pipeline (Dev, TechLead, Security, QA).
- **AgentOutputs**: `PlannedTask.Project?`; `ParsePlan` valida projeto quando `groupProjects` é fornecido.
- **PromptBuilder**: `ScrumMasterGroup` com formato JSON incluindo campo `project`.

### Frontend

- Página de projetos agrupa por grupo (seções), com CRUD de grupo inline.
- Página `/groups/:id` com projetos membros e demandas do grupo.
- `new-demand.page` aceita `?projectGroupId=` para criar demanda de grupo.
- Formulário de projeto tem seletor de grupo.

### Backward compatibility

Demandas de projeto único continuam inalteradas (`ProjectId` preenchido, `ProjectGroupId` null, `WorkItem.ProjectId` null).

## Consequências

- Branches de demanda de grupo ficam por projeto (cada um tem seu `agentforge/demand-{id}`); `Demand.BranchName` fica null.
- Push/PR de demanda de grupo precisará de lógica por projeto (futuro).
- Excluir um grupo desagrupa os projetos (SetNull), mas exclui em cascata as demandas do grupo.
