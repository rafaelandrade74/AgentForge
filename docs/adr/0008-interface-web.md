# ADR 0008 — Interface web

## Decisão
- Angular 18 (standalone, signals, `withComponentInputBinding`: os parâmetros de rota entram como `input()`), **Angular Material** com tema escuro (M3 via `define-theme`; sem fontes externas, para funcionar em homelab sem internet). Páginas carregadas sob demanda (`loadComponent`).
- Telas: login; projetos (lista, cadastro, edição, exclusão); projeto (demandas + **quadro kanban** por estado); nova demanda; demanda (plano, aprovar plano, log ao vivo do Scrum Master); tarefa (detalhe); prompts dos papéis.
- Tarefa: estado e retrabalho, custo acumulado (soma das execuções), apontamentos de revisão (abertos/tratados), **log ao vivo**, linha do tempo de execuções (clique para ver saída e prompt), **diff do branch** colorido e as ações: aprovar, rejeitar (com comentário), reexecutar e cancelar.
- "Reexecutar" = `retry` de uma tarefa `Blocked` (volta ao Dev) ou reiniciar o refinamento de uma demanda `Failed`.
- Tempo real: `RealtimeService` mantém **uma** conexão com `/hubs/runs` (WebSockets, com fallback para long polling), entra no grupo do projeto da tela aberta (`bindProject`), renova o JWT antes de reconectar e, ao reconectar, entra de novo nos grupos e avisa as telas para recarregar por REST (eventos perdidos não são reenviados). O ponto no canto da barra mostra se está conectado.
- A tela nunca depende só do tempo real: carrega o estado por REST e usa os eventos para atualizar.
- Backend acrescentado para a UI: `GET /api/work-items?projectId=`, `GET /api/work-items/{id}/diff` (texto, `git diff origin/<base>...<branch>`, funciona mesmo depois que o worktree foi removido) e `GET /api/agent-runs` passou a devolver uma versão leve (sem prompt/saída; o GET por id traz tudo).
- Edição de prompts grava em `AgentRoleConfig` (banco); vale a partir da próxima execução. Os arquivos `prompts/*.md` só semeiam o primeiro start.

## Dependências novas
`@angular/material`, `@angular/cdk`, `@microsoft/signalr`.

## Pontos de atenção
- `bindProject` roda `join/leave` fora do contexto reativo (`untracked`): escrever em signal dentro de um `effect` dá NG0600 e derrubava a conexão em tempo real. Há teste de regressão (`realtime.service.spec.ts`).
- `ng serve` usa `proxy.conf.json` (API em `localhost:8081`).
- O token fica em `localStorage` (ADR 0002).
