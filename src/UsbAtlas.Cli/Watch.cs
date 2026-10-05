using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace UsbAtlas.Cli;

// Watches USB devices connect and disconnect, as the app does: it rescans once Windows's burst of
// notifications settles, reports what changed, and counts quick drop-and-return cycles. It runs for a set
// time, so a person can wiggle a cable or replug a device while it watches.
internal static class Watch
{
    // Hubs register the USB hub interface class, not the USB device one, so both are watched.
    private static readonly Guid[] UsbInterfaces = [new("A5DCBF10-6530-11D2-901F-00C04FB951ED"), new("F18A0E88-C30C-11D0-8815-00A0C906BED8")];
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(600);

    internal static JsonObject Run(Options o, TimeSpan duration, bool verbose, bool redact, Action<JsonObject> emit, CancellationToken cancel)
    {
        if (o.Has("demo") || o.Has("input")) throw new CliException("watch needs live hardware; it can't watch --demo or --input.");
        var tracker = new ReconnectTracker();
        var events = new ConcurrentQueue<(DateTime At, bool Arrived, string Instance)>();
        using var signal = new SemaphoreSlim(0);
        CmNotifyCallback callback = (_, _, action, data, size) =>
        {
            // CM_NOTIFY_EVENT_DATA: FilterType, Reserved, then the interface class GUID and its symbolic link.
            if (action is 0 or 1 && size > 24 && ReconnectTracker.InstanceIdFromPath(Marshal.PtrToStringUni(data + 24) ?? "") is string id)
            {
                events.Enqueue((DateTime.Now, action == 0, id));
                try { signal.Release(); } catch (ObjectDisposedException) { }
            }
            return 0;
        };
        var registrations = new List<IntPtr>();
        try
        {
            foreach (var guid in UsbInterfaces)
            {
                var filter = Marshal.AllocHGlobal(FilterSize);
                try
                {
                    for (int i = 0; i < FilterSize; i += 4) Marshal.WriteInt32(filter, i, 0);
                    Marshal.WriteInt32(filter, 0, FilterSize);
                    Marshal.Copy(guid.ToByteArray(), 0, filter + 16, 16);
                    int result = CM_Register_Notification(filter, IntPtr.Zero, callback, out var handle);
                    if (result != 0) throw new CliException($"Windows refused device notifications (CONFIGRET {result}).");
                    registrations.Add(handle);
                }
                finally { Marshal.FreeHGlobal(filter); }
            }

            var current = Scan(tracker, redact);
            var start = DateTime.Now;
            emit(J.Obj(("time", Time(start)), ("event", "watching"), ("for", duration == TimeSpan.Zero ? "until stopped" : $"{duration.TotalSeconds:0} s"),
                ("devices", current.Snapshot.Nodes.Count(n => n.Kind == "Device")), ("hubs", current.Snapshot.Nodes.Count(n => n.Kind == "Hub")),
                ("issues", Reports.Issues(current, Severity.Note, false)["summary"]!.DeepClone())));
            DateTime deadline = duration == TimeSpan.Zero ? DateTime.MaxValue : start + duration;
            DateTime? settleAt = null;
            int rescans = 0, changes = 0;
            // A change still settling at the deadline gets its rescan, so the summary is current.
            while (!cancel.IsCancellationRequested && (DateTime.Now < deadline || settleAt != null))
            {
                var until = settleAt is DateTime s && s < deadline ? s : deadline;
                int wait = until == DateTime.MaxValue ? Timeout.Infinite : (int)Math.Clamp((until - DateTime.Now).TotalMilliseconds, 0, int.MaxValue);
                try { signal.Wait(wait, cancel); } catch (OperationCanceledException) { break; }
                while (events.TryDequeue(out var e))
                {
                    if (e.Arrived) tracker.Arrived(e.Instance, e.At); else tracker.Removed(e.Instance, e.At);
                    if (verbose) emit(J.Obj(("time", Time(e.At)), ("event", e.Arrived ? "arrival" : "removal"), ("instanceId", redact ? Session.RedactText(e.Instance) : e.Instance)));
                    if (DateTime.Now < deadline) settleAt = DateTime.Now + Settle;
                }
                if (settleAt is DateTime due && (DateTime.Now >= due || DateTime.Now >= deadline))
                {
                    settleAt = null; rescans++;
                    var next = Scan(tracker, redact);
                    var diff = Diff.Compare(current, next);
                    if (!Diff.Empty(diff)) { changes++; diff["time"] = Time(DateTime.Now); diff["event"] = "change"; emit(diff); }
                    current = next;
                }
            }
            var unstable = current.Listed.Where(n => n.QuickReconnects > 0).ToList();
            return J.Obj(("time", Time(DateTime.Now)), ("event", "summary"), ("watchedSeconds", Math.Round((DateTime.Now - start).TotalSeconds)),
                ("rescans", rescans), ("changes", changes),
                ("unstable", J.Arr(unstable.Select(n => (JsonNode)J.Obj(("path", current.PathOf(n)), ("name", Topology.ShortName(n)), ("quickReconnects", n.QuickReconnects),
                    ("times", J.Arr(n.QuickReconnectTimes.Select(t => (JsonNode)t.ToString("HH:mm:ss")))))))),
                ("issues", Reports.Issues(current, Severity.Note, false)["summary"]!.DeepClone()));
        }
        finally
        {
            foreach (var handle in registrations) CM_Unregister_Notification(handle);
            GC.KeepAlive(callback);
        }
    }

    private static Session Scan(ReconnectTracker tracker, bool redact)
    {
        var snapshot = new UsbScanner().Scan();
        var labels = new DeviceLabels();
        if (labels.LoadError == null) labels.Apply(snapshot);
        tracker.Apply(snapshot);
        return new(redact ? Session.Redact(snapshot) : snapshot, "live") { Redacted = redact };
    }
    private static string Time(DateTime t) => t.ToString("HH:mm:ss.fff");

    // One line per event for people; --json writes one JSON object per line instead.
    internal static string Text(JsonObject e)
    {
        string time = e["time"]?.ToString() ?? "";
        switch (e["event"]?.ToString())
        {
            case "watching":
                var issues = e["issues"]!;
                return $"{time} watching for {e["for"]} · {e["devices"]} devices, {e["hubs"]} hubs · {issues["errors"]} errors, {issues["warnings"]} warnings, {issues["notes"]} notes. Plug, unplug or wiggle now.\n";
            case "arrival" or "removal":
                return $"{time} {e["event"],-8} {e["instanceId"]}\n";
            case "change":
                return string.Concat(Diff.Text(e, false).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => $"{time} {line}\n"));
            case "summary":
                var sb = new System.Text.StringBuilder($"{time} done after {e["watchedSeconds"]} s · {e["rescans"]} rescans, {e["changes"]} with changes\n");
                foreach (var u in e["unstable"]!.AsArray())
                    sb.AppendLine($"  unstable: {u!["path"]} {u["name"]} dropped and came back {u["quickReconnects"]} times ({string.Join(", ", u["times"]!.AsArray().Select(t => t!.ToString()))})");
                var final = e["issues"]!;
                sb.AppendLine($"  now: {final["errors"]} errors, {final["warnings"]} warnings, {final["notes"]} notes. usbatlas-cli issues explains them.");
                return sb.ToString();
            default:
                return Json.Write(e, false) + "\n";
        }
    }

    // CM_NOTIFY_FILTER: four DWORDs, then a union whose largest member is a 200-character instance ID.
    private const int FilterSize = 16 + 400;
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int CmNotifyCallback(IntPtr notify, IntPtr context, int action, IntPtr data, int size);
    [DllImport("cfgmgr32.dll")] private static extern int CM_Register_Notification(IntPtr filter, IntPtr context, CmNotifyCallback callback, out IntPtr notifyContext);
    [DllImport("cfgmgr32.dll")] private static extern int CM_Unregister_Notification(IntPtr notifyContext);
}
