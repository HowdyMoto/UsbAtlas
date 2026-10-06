using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace UsbAtlas.Cli;

// Watches USB devices connect and disconnect, as the app does: it rescans once Windows's burst of
// notifications settles, reports what changed, and counts quick drop-and-return cycles. It runs for a set
// time, so a person can wiggle a cable or replug a device while it watches. When the computer sleeps and
// wakes, it reports what didn't come back, or came back on a slower link.
internal static class Watch
{
    // Hubs register the USB hub interface class, not the USB device one, so both are watched.
    private static readonly Guid[] UsbInterfaces = [new("A5DCBF10-6530-11D2-901F-00C04FB951ED"), new("F18A0E88-C30C-11D0-8815-00A0C906BED8")];
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(600);
    // Devices that lost power while asleep take a few seconds to be set up again.
    private static readonly TimeSpan WakeSettle = TimeSpan.FromSeconds(8);
    // A device Windows is still setting up changes from scan to scan: its descriptor and driver arrive, its power
    // and pipes appear, a passing problem code clears. Rescanning until two scans agree reports where it settles.
    private static readonly TimeSpan Steady = TimeSpan.FromMilliseconds(1500);
    internal const int SteadyTries = 4;

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
        var power = new ConcurrentQueue<(DateTime At, bool Woke)>();
        PowerCallback powerCallback = (_, type, _) =>
        {
            // PBT_APMSUSPEND before sleep; PBT_APMRESUMEAUTOMATIC after, and PBT_APMRESUMESUSPEND when a person woke it.
            if (type is 4 or 7 or 0x12)
            {
                power.Enqueue((DateTime.Now, type != 4));
                try { signal.Release(); } catch (ObjectDisposedException) { }
            }
            return 0;
        };
        var registrations = new List<IntPtr>();
        IntPtr powerRegistration = IntPtr.Zero;
        EtwSession? etw = null;
        try
        {
            // DEVICE_NOTIFY_CALLBACK. Watching goes on without sleep tracking if Windows refuses.
            var subscription = new PowerSubscription { Callback = Marshal.GetFunctionPointerForDelegate(powerCallback) };
            if (PowerRegisterSuspendResumeNotification(2, ref subscription, out powerRegistration) != 0) powerRegistration = IntPtr.Zero;
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
            // Issues already announced, so a device that leaves and returns with one doesn't announce it again.
            var reported = Diff.IssueKeys(current);
            var start = DateTime.Now;
            // --trace adds the hub driver's own account of each change, placed by the latest scan.
            UsbTrace? trace = null;
            if (o.Has("trace"))
            {
                trace = new UsbTrace(() => current);
                etw = UsbTrace.Start();
                etw.Read(e => { if (trace.Interpret(e, verbose) is JsonObject line) emit(line); });
            }
            emit(J.Obj(("time", Time(start)), ("event", "watching"), ("for", duration == TimeSpan.Zero ? "until stopped" : $"{duration.TotalSeconds:0} s"),
                ("devices", current.Snapshot.Nodes.Count(n => n.Kind == "Device")), ("hubs", current.Snapshot.Nodes.Count(n => n.Kind == "Hub")),
                ("issues", Reports.Issues(current, Severity.Note, false)["summary"]!.DeepClone()), ("tracksSleep", powerRegistration != IntPtr.Zero)));
            DateTime deadline = duration == TimeSpan.Zero ? DateTime.MaxValue : start + duration;
            DateTime? settleAt = null, wakeCheckAt = null, lastWake = null;
            Session? beforeSleep = null;
            int rescans = 0, changes = 0, sleeps = 0, notBack = 0, slower = 0;
            Session Settled() => ScanUntilSteady(current, () => { rescans++; return Scan(tracker, redact); }, () => !cancel.WaitHandle.WaitOne(Steady));
            // A change still settling at the deadline gets its rescan, and a wake its check, so the summary is current.
            while (!cancel.IsCancellationRequested && (DateTime.Now < deadline || settleAt != null || wakeCheckAt != null))
            {
                var until = settleAt is DateTime s && s < deadline ? s : deadline;
                if (wakeCheckAt is DateTime w && w < until) until = w;
                int wait = until == DateTime.MaxValue ? Timeout.Infinite : (int)Math.Clamp((until - DateTime.Now).TotalMilliseconds, 0, int.MaxValue);
                try { signal.Wait(wait, cancel); } catch (OperationCanceledException) { break; }
                // A wake is taken first, so the devices returning after it aren't counted as drops.
                while (power.TryDequeue(out var p))
                {
                    if (!p.Woke) { beforeSleep = current; sleeps++; emit(J.Obj(("time", Time(p.At)), ("event", "sleep"))); continue; }
                    // Both wake notifications arrive when a person wakes the computer; one check serves them.
                    if (wakeCheckAt != null || lastWake is DateTime last && p.At - last < WakeSettle) continue;
                    lastWake = p.At; tracker.Woke(p.At);
                    wakeCheckAt = DateTime.Now + WakeSettle;
                    emit(J.Obj(("time", Time(p.At)), ("event", "wake"), ("checkingIn", $"{WakeSettle.TotalSeconds:0} s")));
                }
                while (events.TryDequeue(out var e))
                {
                    if (e.Arrived) tracker.Arrived(e.Instance, e.At); else tracker.Removed(e.Instance, e.At);
                    if (verbose) emit(J.Obj(("time", Time(e.At)), ("event", e.Arrived ? "arrival" : "removal"), ("instanceId", redact ? Session.RedactText(e.Instance) : e.Instance)));
                    if (DateTime.Now < deadline) settleAt = DateTime.Now + Settle;
                }
                if (wakeCheckAt is DateTime check && DateTime.Now >= check)
                {
                    wakeCheckAt = null; settleAt = null;
                    var next = Settled();
                    var diff = Diff.Compare(current, next, reported);
                    if (!Diff.Empty(diff)) { changes++; diff["time"] = Time(DateTime.Now); diff["event"] = "change"; emit(diff); }
                    var after = AfterWaking(beforeSleep ?? current, next, DateTime.Now);
                    notBack += after["notBack"]!.AsArray().Count; slower += after["slower"]!.AsArray().Count;
                    emit(after);
                    current = next; beforeSleep = null;
                }
                if (settleAt is DateTime due && (DateTime.Now >= due || DateTime.Now >= deadline))
                {
                    settleAt = null;
                    var next = Settled();
                    var diff = Diff.Compare(current, next, reported);
                    if (!Diff.Empty(diff)) { changes++; diff["time"] = Time(DateTime.Now); diff["event"] = "change"; emit(diff); }
                    current = next;
                }
            }
            if (trace != null) { etw!.Dispose(); etw = null; emit(trace.Summary(start, current)); }
            var unstable = current.Listed.Where(n => n.QuickReconnects > 0).ToList();
            return J.Obj(("time", Time(DateTime.Now)), ("event", "summary"), ("watchedSeconds", Math.Round((DateTime.Now - start).TotalSeconds)),
                ("rescans", rescans), ("changes", changes),
                ("sleeps", sleeps > 0 ? sleeps : null), ("notBackAfterWaking", sleeps > 0 ? notBack : null), ("slowerAfterWaking", sleeps > 0 ? slower : null),
                ("unstable", J.Arr(unstable.Select(n => (JsonNode)J.Obj(("path", current.PathOf(n)), ("name", Topology.ShortName(n)), ("quickReconnects", n.QuickReconnects),
                    ("times", J.Arr(n.QuickReconnectTimes.Select(t => (JsonNode)t.ToString("HH:mm:ss")))))))),
                ("issues", Reports.Issues(current, Severity.Note, false)["summary"]!.DeepClone()));
        }
        finally
        {
            etw?.Dispose();
            foreach (var handle in registrations) CM_Unregister_Notification(handle);
            if (powerRegistration != IntPtr.Zero) PowerUnregisterSuspendResumeNotification(powerRegistration);
            GC.KeepAlive(callback); GC.KeepAlive(powerCallback);
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

    // Scans until two scans in a row agree, pausing between them, up to SteadyTries more scans.
    internal static Session ScanUntilSteady(Session current, Func<Session> scan, Func<bool> pause)
    {
        var next = scan();
        for (int i = 0; i < SteadyTries && !Diff.Empty(Diff.Compare(current, next)); i++)
        {
            if (!pause()) break;
            var again = scan();
            bool steady = Diff.Empty(Diff.Compare(next, again));
            next = again;
            if (steady) break;
        }
        return next;
    }
    private static string Count(JsonNode? n, string noun) => n?.GetValue<int>() == 1 ? $"1 {noun}" : $"{n} {noun}s";

    // What a sleep cost: anything connected before it that isn't back, or is back on a slower link.
    internal static JsonObject AfterWaking(Session before, Session after, DateTime at)
    {
        var was = Diff.Occupants(before).Where(p => p.Value.Kind is "Device" or "Hub").ToList();
        var now = Diff.Occupants(after);
        var gone = was.Where(p => !now.ContainsKey(p.Key)).Select(p => p.Value).ToList();
        var slowed = was.Where(p => now.TryGetValue(p.Key, out var m) && m.LinkMbps < p.Value.LinkMbps).Select(p => (Before: p.Value, After: now[p.Key])).ToList();
        return J.Obj(("time", Time(at)), ("event", "after-waking"), ("back", was.Count - gone.Count),
            ("notBack", J.Arr(gone.Select(n => (JsonNode)Reports.Ref(before, n)))),
            ("slower", J.Arr(slowed.Select(p => (JsonNode)J.Obj(("path", after.PathOf(p.After)), ("name", Topology.ShortName(p.After)), ("before", Topology.ShortSpeed(p.Before)), ("after", Topology.ShortSpeed(p.After)))))));
    }

    // One line per event for people; --json writes one JSON object per line instead.
    internal static string Text(JsonObject e)
    {
        string time = e["time"]?.ToString() ?? "";
        switch (e["event"]?.ToString())
        {
            case "watching":
                var issues = e["issues"]!;
                string span = e["for"]?.ToString() == "until stopped" ? "until stopped" : $"for {e["for"]}";
                return $"{time} watching {span} · {Count(e["devices"], "device")}, {Count(e["hubs"], "hub")} · {Count(issues["errors"], "error")}, {Count(issues["warnings"], "warning")}, {Count(issues["notes"], "note")}{(e["tracksSleep"]?.GetValue<bool>() == false ? " · Windows refused sleep notifications, so sleep isn't tracked" : "")}. Plug, unplug or wiggle now.\n";
            case "usb" or "trace-start" or "trace-summary":
                return UsbTrace.Text(e);
            case "arrival" or "removal":
                return $"{time} {e["event"],-8} {e["instanceId"]}\n";
            case "change":
                return string.Concat(Diff.Text(e, false).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => $"{time} {line}\n"));
            case "sleep":
                return $"{time} sleep    the computer is going to sleep\n";
            case "wake":
                return $"{time} wake     the computer woke; checking what came back in {e["checkingIn"]}\n";
            case "after-waking":
                var lines = new System.Text.StringBuilder();
                foreach (var n in e["notBack"]!.AsArray()) lines.AppendLine($"{time} ! not back after waking: {n!["path"]} {n["name"]} ({n["kind"]}{(n["vidPid"] is JsonNode id ? ", " + id : "")})");
                foreach (var n in e["slower"]!.AsArray()) lines.AppendLine($"{time} ! slower after waking: {n!["path"]} {n["name"]} {n["before"]} → {n["after"]}");
                if (lines.Length == 0) lines.AppendLine($"{time} ✓ after waking, all {e["back"]} devices and hubs are back at the link rates they had");
                return lines.ToString();
            case "summary":
                var sb = new System.Text.StringBuilder($"{time} done after {e["watchedSeconds"]} s · {Count(e["rescans"], "rescan")}, {e["changes"]} with changes\n");
                if (e["sleeps"] != null) sb.AppendLine($"  slept {(e["sleeps"]!.GetValue<int>() == 1 ? "once" : e["sleeps"] + " times")}: {e["notBackAfterWaking"]} not back after waking, {e["slowerAfterWaking"]} back slower");
                foreach (var u in e["unstable"]!.AsArray())
                    sb.AppendLine($"  unstable: {u!["path"]} {u["name"]} dropped and came back {u["quickReconnects"]} times ({string.Join(", ", u["times"]!.AsArray().Select(t => t!.ToString()))})");
                var final = e["issues"]!;
                sb.AppendLine($"  now: {Count(final["errors"], "error")}, {Count(final["warnings"], "warning")}, {Count(final["notes"], "note")}. usbatlas-cli issues explains them.");
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
    // DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS and its callback, which Windows calls as the computer sleeps and wakes.
    [StructLayout(LayoutKind.Sequential)] private struct PowerSubscription { public IntPtr Callback; public IntPtr Context; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int PowerCallback(IntPtr context, int type, IntPtr setting);
    [DllImport("powrprof.dll")] private static extern int PowerRegisterSuspendResumeNotification(int flags, ref PowerSubscription recipient, out IntPtr handle);
    [DllImport("powrprof.dll")] private static extern int PowerUnregisterSuspendResumeNotification(IntPtr handle);
}
