# Melhorias Propostas

Lista de melhorias identificadas para avaliação. Marcar com `[x]` as aprovadas.

---

## Operacional

### 1. Timeout e feedback de processos longos
- [ ] Ajustar a comunicação do host agent com o backend para dar heartbeat/feedback em execuções longas
- [ ] Botão no frontend para interromper a execução quando demora demais
- **Impacto:** alto — hoje execuções longas podem cair por timeout sem feedback

### 2. Dashboard de custos
- [ ] Tela com gasto acumulado por projeto, demanda e papel de agente
- [ ] Gráfico por período (dia/semana/mês)
- [ ] Os dados já existem em `AgentRun.CostUsd`
- **Impacto:** médio — visibilidade financeira para controlar gastos com IA

### 3. Logs estruturados do agente em tempo real
- [ ] Streaming da saída do CLI no frontend durante a execução (não só pós-execução)
- [ ] Permitir acompanhar o que o agente está fazendo ao vivo
- **Impacto:** médio — melhor observabilidade durante execuções

---

## Fluxo de trabalho

### 4. Retry parcial em revisões paralelas
- [ ] Quando TechLead e Security rodam em paralelo e só um falha, re-rodar apenas o que falhou
- [ ] Hoje ambos são reexecutados mesmo que um já tenha aprovado
- **Impacto:** médio — economia de tempo e créditos

### 5. Prioridade nas tarefas
- [ ] Campo de prioridade no WorkItem (ex.: crítica, alta, normal, baixa)
- [ ] Orquestrador processa as mais urgentes primeiro na fila
- **Impacto:** médio — controle sobre a ordem de execução

### 6. Notificações
- [ ] Webhook, email ou Slack quando uma tarefa precisa de aprovação humana ou fica bloqueada
- [ ] Configurável por projeto
- **Impacto:** alto — reduz tempo de espera por ação humana

---

## Robustez

### 7. Health check do host agent
- [ ] Ping periódico do backend para os host agents cadastrados
- [ ] Status visível na tela de ambientes (online/offline/degradado)
- [ ] Hoje só descobre que está fora do ar ao tentar executar uma tarefa
- **Impacto:** médio — detecção proativa de problemas

### 8. Histórico de transições (audit log)
- [ ] Tabela com cada mudança de estado do WorkItem: quem, quando, de/para, motivo
- [ ] Visualização na tela da tarefa como timeline
- [ ] Hoje só se vê o estado atual e o motivo do bloqueio
- **Impacto:** médio — rastreabilidade e debugging do fluxo

### 9. Backup automático do banco
- [ ] Cron job no docker compose para pg_dump periódico
- [ ] Retenção configurável (últimos N backups)
- [ ] Script de restore documentado
- **Impacto:** alto — proteção contra perda de dados

---

## DX (Developer Experience)

### 10. Templates de demanda
- [ ] Demandas pré-configuradas para tarefas comuns (bug fix, nova feature, refactor)
- [ ] Prompts otimizados por tipo de trabalho
- [ ] Seleção na criação da demanda
- **Impacto:** baixo — conveniência, reduz repetição
