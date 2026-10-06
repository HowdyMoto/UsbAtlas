namespace UsbAtlas;

// USB allows at most five hubs between the computer and a device; Windows refuses a hub or device past them
// ("Hub nested too deeply"). Chains get there sooner than people expect, since docks, monitors, keyboards
// and laptops have hubs inside that don't look like hubs. The root hub isn't one of the five. A USB 3 hub's
// two sides sit in separate trees, each with its own limit, so any one path meets each hub once.
internal static class HubDepth
{
    internal const int Max = 5;
    internal const string AtLimit = "At the five-hub limit";

    // The hubs between the computer and this node, nearest the computer first, not counting the node itself.
    internal static List<UsbNode> Above(IReadOnlyList<UsbNode> path) => path.SkipLast(1).Where(p => p.Kind == "Hub").ToList();

    // Records how many hubs are above every node. Snapshots are analyzed again when loaded.
    internal static void Analyze(Snapshot snapshot)
    {
        void Visit(UsbNode n, int above)
        {
            n.HubsAbove = above;
            foreach (var c in n.Children) Visit(c, above + (n.Kind == "Hub" ? 1 : 0));
        }
        foreach (var c in snapshot.Controllers) Visit(c, 0);
    }

    // A hub with four above it is the fifth: another hub plugged into it won't work. A paired hub's USB 2
    // side leaves the finding to its USB 3 side, which is the same hardware.
    internal static bool IsAtLimit(UsbNode hub) =>
        hub.Kind == "Hub" && hub.HubsAbove + 1 >= Max && !(hub.IsUsb2Companion && hub.CompanionHubId.Length > 0);

    // The hubs in a chain, as people know them: the product a built-in hub is part of, or its own name.
    internal static string Chain(IEnumerable<UsbNode> hubs) => string.Join(" › ", hubs.Select(Topology.ShortName));

    // Properties' Hubs above: how many of the five a node, or what's plugged into a port, is behind.
    internal static string Summary(UsbNode n) => $"{n.HubsAbove} of {Max}";

    internal static Explanations.Explanation Explain(UsbNode hub, IReadOnlyList<UsbNode> path)
    {
        var chain = Above(path).Append(hub).ToList();
        return new($"This hub is the fifth in a row from the computer: {Chain(chain)}. USB allows at most five hubs between the computer and a device, so a hub plugged into this one won't work.",
            "Not right now: what's plugged into it works. Another hub plugged in here won't, and that includes one inside a dock, monitor, keyboard or card reader.", "",
            ["Plug hubs, docks and monitors with USB ports into a port closer to the computer, not into this hub.",
             "Or plug one of the hubs above straight into the computer, so there are fewer in a row."]);
    }
}
