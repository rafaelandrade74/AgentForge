# ADR 0011 — Validação ponta a ponta (Fase 9)

## O que foi executado
Modo 1 (API/front no Docker, **`claude` real do computador** via host agent, Windows 11), projeto de exemplo `examples/tasks-api`
(API de tarefas em .NET, 4 testes) e a demanda:

> Adicionar filtro por status no endpoint de listagem: `GET /tasks?status=Done` devolve só as tarefas com esse status
> (sem diferenciar maiúsculas de minúsculas), status inválido retorna 400 com mensagem clara, sem o parâmetro nada muda. Cubra com testes.

Projeto com aprovações humanas ligadas (modo não automático); as duas aprovações (plano e cada entrega) foram dadas
depois de ler o plano, o diff e o relatório do QA.

## Resultado
- Demanda em **Done**: 4 tarefas em Done, **21 execuções de agentes, 0 falhas**, custo total **US$ 2,84**.
- Scrum Master: 4 tarefas com critérios de aceite e dependências (1 → 2 → 3; 4 independente).
- Papéis exercitados de verdade: Scrum Master, Dev (6 execuções), Tech Lead, Security e QA, com **retrabalho real**: o Tech Lead
  devolveu duas tarefas ao Dev (1 retorno em cada) e a revisão pegou um bug de verdade (`Enum.TryParse` aceitava `"Todo,Done"`
  como flags e devolvia 200 com lista errada). Em nenhum momento chegou ao limite de 3 retornos.
- Cada branch `agentforge/task-<id>` tem commits, código e testes; `dotnet test` em cada uma, no computador: **8, 21, 12 e 9 testes
  aprovados**. O `main` de origem continuou intacto (nada foi enviado).
- Relatório do QA (JSON com veredito e evidência por critério), apontamentos do Tech Lead, diff do branch, custo e linha do tempo
  aparecem na tela da tarefa.

## Lacunas que a validação expôs (item 1 e 2 corrigidos depois; ver ADR 0005)
1. **Tarefas dependentes não enxergam o trabalho das dependências.** O worktree de toda tarefa nasce de `origin/<base>`, então a
   tarefa 3 (testes) e a 4 (README) tiveram de **reimplementar** o filtro para fazer sentido, e as 4 branches se sobrepõem em
   `Program.cs`/`TaskItem.cs` (conflitos no merge manual). **Corrigido:** o worktree de uma tarefa com dependências agora combina as branches das dependências já em Done (conflito bloqueia
   a tarefa com o motivo) e as revisões veem só o diff próprio (`WorkItem.BaseCommit`).
2. **Dependências no plano.** O Scrum Master declarou a cadeia 1 → 2 → 3 mas deixou o README (4) independente. **Corrigido:** o prompt
   do Scrum Master (arquivo e instrução da aplicação) pede `dependsOn` sempre que uma tarefa usa, testa ou documenta código de outra.
3. O merge das branches para o base continua sendo humano (fora do MVP); agora as branches encadeadas se mesclam sem sobreposição.

## Ajustes feitos por causa da validação
- Projeto de exemplo compila no SDK 8 (container) e no SDK 10 (computador): `TargetFramework` e `Mvc.Testing` seguem o SDK em uso.
- Imagem da API (modo 2) com o SDK .NET, para os agentes buildarem projetos .NET dentro do container.

## Rodada 2 (com a correção das dependências)
Mesma demanda, projeto novo: **Done**, 3 tarefas encadeadas (store → endpoint → testes), 13 execuções, 0 falhas, **US$ 1,63**,
sem retrabalho. O worktree da tarefa 2 nasceu com o commit da 1 (verificado no disco), o diff dela mostrou só o trabalho próprio e
o Dev **reaproveitou** `store.All(parsed)` em vez de reimplementar. As três branches formam um histórico linear (a última tem os 4
commits, 29 testes aprovados) e o `main` de origem ficou intacto.