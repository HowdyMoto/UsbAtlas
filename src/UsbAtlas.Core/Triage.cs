namespace UsbAtlas;

// What to fix first, for someone who wants an answer before the details: errors before warnings, and
// among each what affects something plugged in now before what only might, each with its most likely fix.
// Notes affect nothing now, so they're left out; the issue list and Properties still have everything.
internal static class Triage
{
    // Also lists the other issues the same fix resolves, such as the devices a bus-powered hub can't power.
    internal sealed record Item(Severity Severity, string Issue, UsbNode Node, string Fix, bool AffectsNow, List<(string Issue, UsbNode Node)> Also);

    internal const int Shown = 3;

    internal static List<Item> FixFirst(Snapshot snapshot, int max = Shown)
    {
        var items = new List<(Item Item, (UsbNode, string) FixedAt)>();
        foreach (var n in snapshot.Nodes)
        {
            var path = Topology.FindPath(snapshot, n.Id);
            foreach (var (severity, issue) in IssueRules.For(n).Where(i => i.Severity > Severity.Note))
            {
                var e = Explanations.For(n, issue, path);
                items.Add((new(severity, issue, n, Fix(e), e.Affects.StartsWith("Yes", StringComparison.Ordinal), []), FixedAt(n, issue, path)));
            }
        }
        // The most pressing issue of each group leads it. OrderBy is stable, so equals keep their place in the tree.
        return items.OrderByDescending(i => i.Item.Severity).ThenByDescending(i => i.Item.AffectsNow)
            .GroupBy(i => i.FixedAt).Select(g => g.First().Item with { Also = g.Skip(1).Select(i => (i.Item.Issue, i.Item.Node)).ToList() })
            .Take(max).ToList();
    }

    // Where an issue is fixed, as the hub and the kind of fix: a device a bus-powered hub can't power, or one a
    // slower hub holds back, is fixed at that hub, together with the hub's own findings of the same kind.
    private static (UsbNode, string) FixedAt(UsbNode n, string issue, List<UsbNode> path)
    {
        if (issue is "Insufficient power" or "Power at risk" or "Over power budget" or Explanations.AdapterNotDetected)
            return (issue is "Insufficient power" or "Power at risk" && path.Count >= 2 && path[^2] is { Kind: "Hub", PowerSource: "Bus powered" } hub ? hub : n, "power");
        if (issue.StartsWith("Running at", StringComparison.Ordinal))
            return (path.SkipLast(1).LastOrDefault(p => p.Kind == "Hub" && Explanations.HeldBack(p).Contains(n)) ?? n, "speed");
        return (n, issue);
    }

    // The first step is the most likely fix; an issue without steps says what's behind it instead.
    private static string Fix(Explanations.Explanation e) => e.Steps is { Count: > 0 } steps ? steps[0] : e.Cause.Length > 0 ? e.Cause : e.What;
}
