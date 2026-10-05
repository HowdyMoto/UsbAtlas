namespace UsbAtlas;

// A note is worth knowing, but nothing is affected now; a warning means something plugged in is affected
// or at risk; an error means something plugged in doesn't work.
internal enum Severity { Note, Warning, Error }

// What is wrong with one node, as the app's badges and the command line's issue list show it. The text
// names the issue; Explanations.For says what it means and what to do.
internal static class IssueRules
{
    internal static List<(Severity Severity, string Text)> For(UsbNode n)
    {
        var issues = new List<(Severity, string)>();
        // A port refused for power or bandwidth names the fault; anything else unavailable is a generic port error.
        if (n.Kind == "Unavailable") issues.Add((Severity.Error, UsbBudgets.IsPowerFault(n) || n.Status == "Insufficient bandwidth" ? n.Status : "Port error"));
        if (n.ScanIncomplete) issues.Add((Severity.Warning, "Scan incomplete"));
        if (Explanations.DriverProblemSeverity(n) is Severity problem) issues.Add((problem, Explanations.DriverProblem));
        if (HubRelationships.ReducedSpeed(n)) issues.Add((Explanations.SpeedSeverity(n), Explanations.SpeedLabel(n)));
        foreach (var warning in n.PowerWarnings) issues.Add((Explanations.PowerSeverity(n, warning), warning));
        if (n.QuickReconnects > 0) issues.Add((Severity.Warning, "Unstable connection"));
        // Firmware describing a port wrongly changes nothing plugged into it, so each finding is a note.
        foreach (var finding in n.PortMapWarnings) issues.Add((Severity.Note, finding));
        // A nearly full link still fits everything on it, so it's a note; peaks that can't all fit are a warning.
        if (UsbBudgets.LinkNearlyFull(n)) issues.Add((Severity.Note, "Link nearly full"));
        if (UsbBudgets.CouldExceedWhenStreaming(n)) issues.Add((Severity.Warning, "Could exceed when streaming"));
        if (UsbBudgets.SharedTtNearlyFull(n)) issues.Add((Severity.Note, "Shared TT nearly full"));
        if (UsbBudgets.SharedTtCouldExceed(n)) issues.Add((Severity.Warning, "Shared TT could exceed"));
        return issues;
    }
    internal static string Summary(UsbNode n) => string.Join(" · ", For(n).Select(i => i.Text));
}
