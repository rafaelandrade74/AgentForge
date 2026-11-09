# ADR 0026 — Página "Agentes ao vivo" e chat com o agente (criação de demandas por conversa)

## Contexto
Para saber quais agentes estão disponíveis era preciso abrir a tela **Agentes** de uma máquina por vez, e não havia como ver, num só lugar, o
que está rodando agora. Também não havia como conversar com um agente (tirar dúvidas, refinar uma ideia) e transformar o resultado em
demanda: toda demanda nascia de um formulário.

## Decisão
### Agentes ao vivo (`/live`, `GET /api/live/agents`)
- Um cartão por **agente × máquina** (ambiente padrão do servidor e cada ambiente cadastrado): máquina acessível, instalado, conectado,
  versão, quantas execuções rodam agora e, se houver, o **limite de uso** (ADR 0024) com o horário em que libera. Estado do mais grave ao
  normal: inacessível → não instalado → não conectado → limite de uso → trabalhando → pronto.
- Máquina fora do ar ou lenta (12 s) não derruba o painel: aparece como inacessível, com o motivo.
- **Executando agora:** as `AgentRun` em andamento de qualquer projeto (papel, tarefa, projeto, máquina, tempo) com a última linha do log.
- **Tempo real:** o hub `/hubs/runs` ganhou o grupo `live` (`JoinLive`/`LeaveLive`): quem está na página recebe `liveRunEvent` com os
  eventos de TODOS os projetos. `run_started`/`run_finished` recarregam o painel; sem SignalR, a página atualiza a cada 10 s.

### Chat (`/chat`, `/api/chats`)
- `ChatSession` (projeto, máquina, agente, id da sessão do CLI) e `ChatMessage` (migration `AddChat`). A conversa fica salva e retomável.
- Cada mensagem é uma execução `claude -p --resume <sessão>` na máquina escolhida (`AgentRequest.ResumeSessionId`; o id volta em
  `AgentResult.SessionId`), **em segundo plano**: `POST .../messages` responde 202 e a resposta chega por `chatChanged` (grupo `chat:<id>`)
  ou recarregando. Uma mensagem por vez por conversa (409 se o agente ainda responde). A execução é avulsa (`Ephemeral`): não cria `AgentRun`.
- **Leitura do código:** se o clone do projeto mora na mesma máquina da conversa, o agente recebe só `Read,Glob,Grep` no clone (nunca edita nem
  roda comandos). Em outra máquina, conversa sem acesso ao código, só com o contexto do projeto no prompt de sistema.
- **O agente propõe, a pessoa cria.** O prompt manda terminar a resposta com `{"demands":[{title, description}]}` (até 5). A API tira o bloco
  do texto e guarda as propostas na mensagem; a tela mostra **Criar demanda** e **Criar e iniciar refinamento**. Só o clique cria a `Demand`
  (`POST .../messages/{id}/demands/{i}`), de forma idempotente (409 na repetição), mantendo "humano no controle". Nenhum token do AgentForge
  é entregue ao agente e o MCP não é usado: funciona igual em qualquer modo de execução.
- Limite de uso e falhas aparecem como mensagem do agente marcada como falha; a conversa continua utilizável.

## Consequências
- Custo: cada resposta gasta créditos do agente (o custo da resposta fica na mensagem).
- A resposta não é transmitida token a token: aparece inteira ao fim (o CLI roda em modo `-p`).
- Reiniciar a API durante uma resposta deixa a mensagem sem resposta; a tela avisa e basta reenviar.
- A sessão do CLI vive no login da máquina (`~/.claude`): trocar de conta ou apagar o volume `claude-config` invalida o `--resume`
  (a resposta falha com o motivo; abra uma nova conversa).
