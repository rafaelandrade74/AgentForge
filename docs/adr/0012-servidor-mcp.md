# ADR 0012 — Servidor MCP

## Decisão
- O AgentForge expõe um **servidor MCP** em `POST /mcp` (Streamable HTTP, JSON-RPC 2.0), para o Claude Code e outros clientes
  controlarem o fluxo por conversa. Implementado à mão na própria API (`AgentForge.Api/Mcp`), **sem dependência nova**:
  `initialize` (negocia 2025-06-18 / 2025-03-26 / 2024-11-05), `ping`, `tools/list`, `tools/call`, notificações (202 sem corpo) e lotes.
  Sem sessões e sem fluxo SSE: cada POST responde `application/json`; `GET`/`DELETE` no `/mcp` devolvem 405.
- **18 ferramentas**: projetos (`list_projects`, `get_project`, `create_project`), demandas (`create_demand`, `start_demand`,
  `approve_plan`, `get_demand`), tarefas (`list_tasks`, `get_task`, `get_task_diff`, `get_run_output`, `approve_task`,
  `reject_task`, `retry_task`, `cancel_task`), `list_pending_actions` ("o que depende de mim") e agentes (`list_agents`,
  `test_agent`). Reaproveitam as mesmas regras do orquestrador (`WorkflowActions`): estados inválidos viram erro claro.
  Anotações MCP: leituras `readOnlyHint`, `cancel_task` `destructiveHint`. Fora do escopo de propósito: apagar projeto, push, merge.
- **Erros**: problemas de protocolo usam códigos JSON-RPC (-32700, -32600, -32601, -32602); erro de uso de uma ferramenta
  (argumento faltando, UUID inválido, ação que não vale para o estado) volta como resultado com `isError: true`, para o
  modelo corrigir e tentar de novo. Exceções inesperadas não vazam detalhes. Corpo máximo de 512 KB.
- **Credencial duradoura**: o JWT do login dura 15 min, inviável para um cliente MCP. Surgem os **tokens de acesso**
  (`afk_...`, 256 bits aleatórios): criados na tela **Integração** (`POST /api/tokens`, valor exibido uma única vez), guardados
  só como hash SHA-256, revogáveis, com "último uso". Um `AuthenticationHandler` próprio os valida e **só o `/mcp` aceita esse
  esquema** (junto com o JWT); o resto da API, inclusive criar/listar tokens, continua exigindo o login. Assim um token vazado
  não abre a API inteira.
- Nginx do front repassa `/mcp` sem buffer; o fallback de SPA do modo 3 mantém `/mcp` como 404 quando não existe.

## Verificação
Testes (14) cobrem autenticação e revogação, escopo do token, protocolo (versões, 202, lote, erros, corpo grande), o catálogo
(18 ferramentas, anotações, nada destrutivo além de `cancel_task`) e os fluxos das ferramentas. Ao vivo: pelo Nginx, a
demanda real da rodada 2 foi lida por `get_demand`, e o **Claude Code real** como cliente (`--mcp-config`) descobriu o servidor,
chamou a ferramenta e respondeu corretamente.

## Fora do escopo
Servidor MCP por stdio, notificações servidor→cliente (`listChanged`), recursos/prompts MCP, e configurar MCPs para os agentes.
