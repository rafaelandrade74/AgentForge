# ADR 0022 — Reavaliação de dependentes ao cancelar uma tarefa

**Status**: Aceito  
**Data**: 2026-11-07

## Contexto

Quando uma tarefa é cancelada, as tarefas que dependem dela ficam presas em `Todo` para sempre: a condição `Dependencies.All(d => d.State == Done)` nunca é satisfeita. O orquestrador não fazia nada com as dependentes ao cancelar.

## Decisão

1. Ao entrar em `Cancelled`, o orquestrador busca todas as tarefas não-terminais que dependiam da tarefa cancelada.
2. Se houver, chama o **Scrum Master** com um prompt de reavaliação contendo a tarefa cancelada e as dependentes afetadas.
3. O Scrum Master decide, para cada dependente:
   - **`remove_dependency`**: a dependência cancelada é removida e a descrição/critérios de aceite são ajustados para a tarefa funcionar sozinha. A tarefa é re-enfileirada.
   - **`block`**: a tarefa é bloqueada (`Blocked`) para decisão humana.
4. Se o Scrum Master falhar (agente indisponível, JSON inválido após 2 tentativas), **fallback**: todas as dependentes vão para `Blocked` com motivo claro.
5. Nova transição: `Todo + AgentFailed → Blocked` (antes não existia).

## Consequências

- Nenhuma tarefa fica presa silenciosamente após um cancelamento.
- O Scrum Master tem chance de reorganizar o plano (coerente com seu papel).
- No pior caso, o humano vê `Blocked` no painel com a razão, em vez de um `Todo` que nunca anda.
- Cascata natural: se uma dependente for cancelada pelo bloco, suas dependentes também são reavaliadas.
