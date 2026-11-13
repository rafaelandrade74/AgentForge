using System.Text;
using AgentForge.Domain;

namespace AgentForge.Orchestrator.Workflow;

/// <summary>Monta o prompt de cada papel, sempre com o formato de saída exigido (o orquestrador valida o JSON).</summary>
public static class PromptBuilder
{
    private const string PlanFormat = """
        Termine a resposta com UM bloco ```json neste formato (e nada depois dele):
        {"tasks":[{"key":"t1","title":"...","description":"...","acceptanceCriteria":["..."],"dependsOn":[],"estimate":3}]}
        - "key" é um identificador curto e único; "dependsOn" lista as keys das tarefas que precisam estar prontas antes.
        - Tarefas pequenas e independentes sempre que possível; sem ciclos de dependência; no máximo 30 tarefas.
        - Declare em "dependsOn" TODA tarefa cujo trabalho usa, testa ou documenta o código de outra: as tarefas anteriores são
          mergeadas no branch da demanda ao concluir, e a próxima tarefa já nasce com esse trabalho; ela não deve reimplementar
          o que a outra entrega. Duas tarefas que alteram as mesmas linhas não devem ser independentes.
        """;

    private const string DevFormat = """
        Antes de terminar, faça commit de tudo no branch atual (não faça push). Termine a resposta com UM bloco ```json:
        {"summary":"o que foi feito","files":["caminho/arquivo"],"commit":"<hash completo do commit>"}
        """;

    private const string ReviewFormat = """
        Termine a resposta com UM bloco ```json:
        {"verdict":"approved","findings":[]}
        ou, se houver problemas:
        {"verdict":"changes_requested","findings":[{"file":"caminho","line":10,"severity":"low|medium|high|critical","message":"o que corrigir"}]}
        """;

    private const string QaFormat = """
        Termine a resposta com UM bloco ```json:
        {"verdict":"passed|failed","criteria":[{"criterion":"texto do critério","passed":true,"evidence":"comando/saída que comprova"}]}
        Use "passed" apenas se TODOS os critérios passaram.
        """;

    public static string ScrumMaster(Demand demand, Project project) => $"""
        Demanda: {demand.Title}

        {demand.Description}

        Projeto: {project.Name} (branch base: {project.BaseBranch}). Leia o repositório para entender o código antes de quebrar a demanda.
        Quebre a demanda em tarefas pequenas, cada uma com critérios de aceite verificáveis e ordem de dependência.

        {PlanFormat}
        """;

    public static string Dev(WorkItem item, Project project, IEnumerable<string> dependencyTitles, IReadOnlyList<ReviewFinding> openFindings)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Tarefa: {item.Title}").AppendLine().AppendLine(item.Description).AppendLine();
        sb.AppendLine("Critérios de aceite:").AppendLine(item.AcceptanceCriteria).AppendLine();
        sb.AppendLine($"Você está no worktree do branch da tarefa (criado a partir do branch da demanda, que acumula o trabalho das tarefas anteriores). Escreva também os testes unitários.");
        if (!string.IsNullOrWhiteSpace(project.BuildCommand)) sb.AppendLine($"Build: {project.BuildCommand}");
        if (!string.IsNullOrWhiteSpace(project.TestCommand)) sb.AppendLine($"Testes: {project.TestCommand}");
        var deps = dependencyTitles.ToList();
        if (deps.Count > 0)
            sb.AppendLine("Tarefas das quais esta depende, já concluídas: " + string.Join("; ", deps) +
                          ". O código delas JÁ ESTÁ no seu branch: use-o e não o reimplemente.");

        if (openFindings.Count > 0)
        {
            sb.AppendLine().AppendLine("RETRABALHO — corrija todos os apontamentos abertos abaixo:");
            foreach (var f in openFindings)
                sb.AppendLine($"- [{f.Role}/{f.Severity}] {(f.File is null ? "" : $"{f.File}{(f.Line is null ? "" : ":" + f.Line)}: ")}{f.Message}");
        }
        sb.AppendLine().Append(DevFormat);
        return sb.ToString();
    }

    /// <summary>Quando o papel roda em outra máquina, ele NÃO tem o repositório: só o diff e a descrição da tarefa.</summary>
    private const string NoRepoNote = "ATENÇÃO: você roda numa máquina sem o repositório. Baseie-se apenas no diff e na descrição abaixo; não tente ler arquivos do projeto nem rodar build/testes. Se faltar contexto para decidir, registre isso como achado.";

    private static string RepoNote(bool repoAvailable) => repoAvailable ? "" : NoRepoNote;

    public static string TechLead(WorkItem item, Project project, string diff, bool repoAvailable = true) => $"""
        Revise a tarefa abaixo quanto a qualidade, arquitetura, legibilidade e cobertura de testes. Não altere arquivos.
        {RepoNote(repoAvailable)}

        Tarefa: {item.Title}
        Critérios de aceite:
        {item.AcceptanceCriteria}

        Diff do que ESTA tarefa mudou (o trabalho das tarefas das quais ela depende já faz parte do ponto de partida do branch; use git para ver mais contexto):
        ```diff
        {diff}
        ```

        {ReviewFormat}
        """;

    public static string Security(WorkItem item, Project project, string diff, bool repoAvailable = true) => $"""
        Revise o diff abaixo contra o OWASP Top 10, segredos expostos e dependências vulneráveis. Use scanners se estiverem disponíveis. Não altere arquivos.
        {RepoNote(repoAvailable)}

        Tarefa: {item.Title}

        Diff do que ESTA tarefa mudou (sem o trabalho das dependências):
        ```diff
        {diff}
        ```

        {ReviewFormat}
        Informe a severidade de cada achado.
        """;

    private const string ReassessFormat = """
        Termine a resposta com UM bloco ```json neste formato (e nada depois dele):
        {"actions":[{"workItemId":"<guid>","action":"remove_dependency|block","newDescription":"...","newAcceptanceCriteria":"..."}]}
        - "remove_dependency": remove a dependência cancelada e ajusta a descrição/critérios de aceite para que a tarefa faça sentido sozinha.
        - "block": a tarefa não faz sentido sem a dependência cancelada; será bloqueada para decisão humana.
        - "newDescription" e "newAcceptanceCriteria" são obrigatórios quando action é "remove_dependency" (o texto completo atualizado).
        """;

    public static string ScrumMasterReassess(WorkItem cancelled, List<WorkItem> dependents, Project project)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"A tarefa abaixo foi CANCELADA:");
        sb.AppendLine($"  Título: {cancelled.Title}");
        sb.AppendLine($"  Descrição: {cancelled.Description}");
        sb.AppendLine();
        sb.AppendLine("As seguintes tarefas dependiam dela e estão travadas:");
        foreach (var d in dependents)
        {
            sb.AppendLine($"  - [{d.Id}] {d.Title}");
            sb.AppendLine($"    Descrição: {d.Description}");
            sb.AppendLine($"    Critérios de aceite: {d.AcceptanceCriteria}");
            sb.AppendLine($"    Outras dependências: {(d.Dependencies.Where(dep => dep.Id != cancelled.Id).Select(dep => dep.Title).ToList() is { Count: > 0 } others ? string.Join("; ", others) : "nenhuma")}");
        }
        sb.AppendLine();
        sb.AppendLine($"Projeto: {project.Name}. Para cada tarefa afetada, decida:");
        sb.AppendLine("1. Se a tarefa faz sentido sem o trabalho da tarefa cancelada, escolha \"remove_dependency\" e ajuste a descrição e os critérios de aceite.");
        sb.AppendLine("2. Se a tarefa depende fundamentalmente do trabalho cancelado, escolha \"block\".");
        sb.AppendLine();
        sb.Append(ReassessFormat);
        return sb.ToString();
    }

    public static string Qa(WorkItem item, Project project) => $"""
        Valide a tarefa no branch atual: execute o build e os testes, confira CADA critério de aceite e escreva testes de integração quando faltarem.

        Tarefa: {item.Title}
        Critérios de aceite:
        {item.AcceptanceCriteria}

        {(string.IsNullOrWhiteSpace(project.BuildCommand) ? "" : $"Build: {project.BuildCommand}")}
        {(string.IsNullOrWhiteSpace(project.TestCommand) ? "" : $"Testes: {project.TestCommand}")}

        {QaFormat}
        """;
}
