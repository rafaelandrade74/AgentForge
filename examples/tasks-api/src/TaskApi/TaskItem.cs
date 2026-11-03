namespace TaskApi;

public enum TaskStatus
{
    Todo,
    InProgress,
    Done,
}

public record TaskItem(int Id, string Title, TaskStatus Status);

public record CreateTaskRequest(string Title, TaskStatus Status = TaskStatus.Todo);

/// <summary>Armazenamento em memória, com algumas tarefas de exemplo.</summary>
public class TaskStore
{
    private readonly List<TaskItem> _tasks =
    [
        new(1, "Escrever o README", TaskStatus.Todo),
        new(2, "Corrigir o bug do login", TaskStatus.InProgress),
        new(3, "Publicar a versão 1.0", TaskStatus.Done),
    ];

    private int _nextId = 4;
    private readonly object _lock = new();

    public IReadOnlyList<TaskItem> All()
    {
        lock (_lock) return _tasks.ToList();
    }

    public TaskItem? Find(int id)
    {
        lock (_lock) return _tasks.FirstOrDefault(t => t.Id == id);
    }

    public TaskItem Add(string title, TaskStatus status)
    {
        lock (_lock)
        {
            var task = new TaskItem(_nextId++, title, status);
            _tasks.Add(task);
            return task;
        }
    }
}
