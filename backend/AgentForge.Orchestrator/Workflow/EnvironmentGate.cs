namespace AgentForge.Orchestrator.Workflow;

/// <summary>Estado de um ambiente na fila: limite, quantas tarefas rodam nele e quantas esperam vaga.</summary>
public record EnvironmentSlotInfo(string Key, int Limit, int Running, int Queued);

/// <summary>
/// Concorrência máxima por ambiente. Quem não cabe NÃO prende um trabalhador: fica estacionado no ambiente e volta para a fila
/// quando uma vaga abre, então uma máquina cheia não impede que as outras trabalhem. Tudo sob um único lock, então
/// "estacionar" e "liberar" nunca se cruzam (um item não fica esquecido).
/// </summary>
public sealed class EnvironmentGate<TWork>
{
    private sealed class Slot
    {
        public int Limit = 1;
        public int Running;
        public readonly List<TWork> Parked = [];
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Slot> _slots = new();

    /// <summary>true se há vaga (e a ocupa). Sem vaga, estaciona o trabalho e devolve false.</summary>
    public bool TryEnter(string key, int limit, TWork work)
    {
        lock (_lock)
        {
            if (!_slots.TryGetValue(key, out var slot)) _slots[key] = slot = new Slot();
            slot.Limit = Math.Max(1, limit);   // o limite pode ter mudado na tela Ambientes
            if (slot.Running < slot.Limit)
            {
                slot.Running++;
                return true;
            }
            slot.Parked.Add(work);
            return false;
        }
    }

    /// <summary>Libera a vaga e devolve o que estava estacionado (quem ainda não couber estaciona de novo).</summary>
    public List<TWork> Leave(string key)
    {
        lock (_lock)
        {
            if (!_slots.TryGetValue(key, out var slot)) return [];
            slot.Running = Math.Max(0, slot.Running - 1);
            var parked = slot.Parked.ToList();
            slot.Parked.Clear();
            return parked;
        }
    }

    public IReadOnlyList<EnvironmentSlotInfo> Snapshot()
    {
        lock (_lock) return _slots.Select(s => new EnvironmentSlotInfo(s.Key, s.Value.Limit, s.Value.Running, s.Value.Parked.Count)).ToList();
    }
}
