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
        // A port Windows is still setting up usually finishes within seconds, so it's a note.
        if (n.Kind == "Unavailable" && n.Status is "Enumerating" or "Resetting") issues.Add((Severity.Note, "Still connecting"));
        else if (n.Kind == "Unavailable") issues.Add((Severity.Error, UsbBudgets.IsPowerFault(n) || n.Status == "Insufficient bandwidth" ? n.Status : "Port error"));
        if (n.ScanIncomplete) issues.Add((Severity.Warning, "Scan incomplete"));
        if (Explanations.DriverProblemSeverity(n) is Severity problem) issues.Add((problem, Explanations.DriverProblem));
        if (LinuxProblems.SeverityOf(n) is Severity kernel) issues.Add((kernel, n.KernelProblem));
        if (HubRelationships.ReducedSpeed(n)) issues.Add((Explanations.SpeedSeverity(n), Explanations.SpeedLabel(n)));
        foreach (var warning in n.PowerWarnings) issues.Add((Explanations.PowerSeverity(n, warning), warning));
        // A hub that drops takes everything behind it along, as switching a KVM, changing monitor inputs or
        // undocking does, so it's a note; a device dropping on its own is much more likely a fault.
        if (n.QuickReconnects > 0) issues.Add((n.Kind == "Hub" ? Severity.Note : Severity.Warning, "Unstable connection"));
        if (UsbBudgets.UplinkSeverity(n) is Severity uplink) issues.Add((uplink, "Limited by PCIe link"));
        if (UsbBudgets.EndpointsRunningHigh(n)) issues.Add((Severity.Note, "Many endpoints in use"));
        // A USB-C alternate mode that failed loses what it carries, such as the picture; one never asked for may be intended.
        if (Billboard.FailedModes(n).Count > 0) issues.Add((Severity.Warning, Billboard.Failed));
        else if (Billboard.NoneEntered(n)) issues.Add((Severity.Note, Billboard.NotEntered));
        if (n.Display != null) issues.Add((Displays.SeverityOf(n), Displays.NotShowing));
        // Unrelated hardware grouped as one device by Windows still works, so it's a note.
        if (n.ContainerIdShared) issues.Add((Severity.Note, Containers.SharedId));
        // Radio noise isn't measured, so a receiver beside a fast drive is a note.
        if (n.NoisyNeighbor != null) issues.Add((Severity.Note, Interference.Nearby));
        // A drive that could use UAS but doesn't is slower than it could be, so it's a warning.
        if (Uas.NotUsed(n)) issues.Add((Severity.Warning, Uas.NotInUse));
        // A hub at the limit works; only another hub plugged into it won't.
        if (HubDepth.IsAtLimit(n)) issues.Add((Severity.Note, HubDepth.AtLimit));
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
