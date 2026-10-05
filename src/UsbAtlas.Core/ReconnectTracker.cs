namespace UsbAtlas;

// Counts quick disconnect-and-return cycles per device. A device that repeatedly drops and comes
// back within seconds is usually short of power, or on a faulty cable or connector.
internal sealed class ReconnectTracker
{
    internal static readonly TimeSpan QuickReturn = TimeSpan.FromSeconds(30), Window = TimeSpan.FromMinutes(5);
    internal const int Threshold = 3;
    private readonly Dictionary<string, DateTime> removed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DateTime>> returns = new(StringComparer.OrdinalIgnoreCase);

    internal void Removed(string id, DateTime at) => removed[id] = at;
    internal void Arrived(string id, DateTime at)
    {
        if (!removed.Remove(id, out var gone) || at - gone > QuickReturn) return;
        if (!returns.TryGetValue(id, out var times)) returns[id] = times = [];
        times.Add(at);
    }

    // A hub that drops takes everything behind it along, as switching a KVM, changing monitor inputs or
    // undocking does, so only the hub is flagged. A device behind it is flagged for its own drops: returns
    // that don't come within seconds of one of an upstream hub's. Once a burst crosses the threshold the
    // device stays flagged for the session, so earlier drops still show.
    internal static readonly TimeSpan SameDrop = TimeSpan.FromSeconds(15);
    internal void Apply(Snapshot snapshot)
    {
        if (snapshot.IsDemo) return;
        foreach (var controller in snapshot.Controllers) Mark(controller, []);
        void Mark(UsbNode node, List<DateTime> upstream)
        {
            var own = node.InstanceId.Length > 0 ? returns.GetValueOrDefault(node.InstanceId) ?? [] : [];
            var times = own.Where(t => !upstream.Any(u => (t - u).Duration() <= SameDrop)).ToList();
            if (times.Any(t => times.Count(u => u <= t && t - u <= Window) >= Threshold))
            {
                node.QuickReconnects = times.Count;
                node.QuickReconnectTimes = times;
                node.Notes.Add($"Reconnect evidence: dropped and came back within seconds {times.Count} times this session, most recently at {times[^1]:T}" + (node.Kind == "Hub" ? ", taking everything behind it along." : "."));
            }
            foreach (var child in node.Children) Mark(child, [.. upstream, .. own]);
        }
    }

    // A path like \\?\USB#VID_046D&PID_C52B#5&2a8c&0&3#{a5dcbf10-…}, or a hub's ending #{f18a0e88-…}, names the
    // instance USB\VID_046D&PID_C52B\5&2a8c&0&3.
    internal static string? InstanceIdFromPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) path = path[4..];
        int guid = path.LastIndexOf("#{", StringComparison.Ordinal);
        if (guid <= 0) return null;
        var parts = path[..guid].Split('#');
        return parts.Length == 3 && parts.All(p => p.Length > 0) ? string.Join('\\', parts) : null;
    }
}
