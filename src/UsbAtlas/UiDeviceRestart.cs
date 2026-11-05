using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace UsbAtlas;

public partial class MainWindow
{
    private bool restartingDevice;
    private sealed record RestartAttempt(DateTime At, string Result, bool StillUnavailable);
    private readonly Dictionary<string, RestartAttempt> restartResults = [];
    private static string RestartKey(UsbNode node) => node.Id + "|" + node.InstanceId.ToUpperInvariant();

    private static bool CanRestart(UsbNode node) => node.Kind is "Device" or "Unavailable"
        && node.InstanceId.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase)
        && !node.InstanceId.Any(c => c == '"' || char.IsControl(c))
        && node.Children.Count == 0;

    private void VerifyDeviceRestart()
    {
        static void Check(bool value) { if (!value) throw new InvalidOperationException("Device restart eligibility check failed."); }
        var target = new UsbNode { Kind = "Unavailable", InstanceId = @"USB\VID_0000&PID_0002\test" };
        Check(CanRestart(target));
        foreach (var kind in new[] { "Controller", "Root hub", "Hub", "Empty port" })
        {
            target.Kind = kind;
            Check(!CanRestart(target));
        }
        target.Kind = "Device";
        Check(CanRestart(target));
        target.Children.Add(new UsbNode());
        Check(!CanRestart(target));
        target.Children.Clear();
        foreach (var id in new[] { "", @"PCI\VEN_1022\test", "USB\\VID_test\" /reboot", "USB\\VID_test\n" })
        {
            target.InstanceId = id;
            Check(!CanRestart(target));
        }
    }

    private void AddDeviceRestart(UsbNode node)
    {
        if (demo || snapshot.IsDemo || !CanRestart(node)) return;
        int start = Details.Children.Count;
        bool unhealthy = node.Kind == "Unavailable" || node.DriverProblems.Count > 0;
        restartResults.TryGetValue(RestartKey(node), out var attempt);
        if (unhealthy) Text("Recovery", 15);
        if (attempt != null)
        {
            Text("Last attempt · " + attempt.At.ToString("t"), 12, "TextSecondary");
            Text(attempt.Result, 14);
            if (attempt.StillUnavailable && node.Kind == "Unavailable")
            {
                Text("Next: disconnect the device’s power", 14);
                var steps = new StackPanel();
                steps.Children.Add(OverviewText("For a plug-in device, unplug it and its power supply, then reconnect. For a built-in adapter, save your work, shut down the PC and disconnect its external power. Follow the computer maker’s power-reset instructions.", 14));
                steps.Children.Add(OverviewText("A software restart does not guarantee power is removed. Recovery is not guaranteed; a device that keeps failing may need hardware service.", 13, "TextSecondary"));
                Details.Children.Add(new Expander { Header = "Power-reset guidance", Content = steps, FontSize = 14, Margin = new Thickness(0, 3, 0, 6) });
            }
        }
        var button = new Button
        {
            Content = restartingDevice ? "Restart in progress…" : attempt != null ? "Try restart again…" : "Try device restart…", HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 6), Padding = new Thickness(10, 5, 10, 5),
            IsEnabled = !restartingDevice && !render,
            ToolTip = "Ask Windows to restart only this device. Requires administrator approval and briefly disconnects its functions. Does not guarantee a power reset."
        };
        button.Click += async (_, _) => await RestartDevice(node);
        Details.Children.Add(button);
        if (!unhealthy) FoldInspectorContent(start, "Device actions", "device-actions");
    }

    private async Task RestartDevice(UsbNode node)
    {
        if (busy || restartingDevice || demo || render || snapshot.IsDemo || !CanRestart(node)) return;
        if (MessageBox.Show(this,
            $"Restart {node.DisplayName} on {pathLabels.GetValueOrDefault(node.Id, "this port")}?\n\n"
            + "This briefly disconnects all functions of this device, including anything attached through it if it is an unidentified hub. Finish any transfers first. Windows will ask for administrator approval.\n\n"
            + "This does not guarantee a power reset. If the device stays unresponsive, unplugging power may still be necessary.",
            "Try device restart", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;

        restartingDevice = true;
        busy = true;
        RefreshButton.IsEnabled = false;
        ShowDetails();
        string result;
        DateTime verifiedAt = DateTime.MinValue;
        int? exitCode = null;
        try
        {
            StatusText.Text = "Checking the device before restart…";
            var live = await Task.Run(() => new UsbScanner().Scan());
            var target = live.Nodes.SingleOrDefault(n => n.Id == node.Id);
            if (target == null || !CanRestart(target) || !target.InstanceId.Equals(node.InstanceId, StringComparison.OrdinalIgnoreCase))
                result = "Restart skipped: the device at this port changed. Refresh and select it again.";
            else
            {
                var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe"))
                {
                    UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
                };
                start.ArgumentList.Add("/restart-device");
                start.ArgumentList.Add(target.InstanceId);
                StatusText.Text = "Waiting for Windows device restart…";
                using var process = await Task.Run(() => Process.Start(start))
                    ?? throw new InvalidOperationException("Windows did not start the restart command.");
                await process.WaitForExitAsync();
                exitCode = process.ExitCode;
                result = exitCode switch
                {
                    0 => "Windows accepted the restart.",
                    3010 => "Windows requires a computer restart to complete this action. USB Atlas will not reboot automatically.",
                    _ => $"Windows could not complete the restart (exit code {exitCode})."
                };
                await Task.Delay(1500);
                verifiedAt = DateTime.Now;
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { result = "Restart canceled at the Windows administrator prompt."; }
        catch (Exception ex) { result = "Restart failed: " + ex.Message; }
        finally { busy = false; RefreshButton.IsEnabled = true; }

        try
        {
            await Refresh();
            if (exitCode == 0)
            {
                var after = snapshot.Nodes.FirstOrDefault(n => n.Id == node.Id);
                result += snapshot.CapturedAt < verifiedAt ? " Could not verify recovery; refresh again."
                    : after is { Kind: "Device" } && after.DriverProblems.Count == 0
                        ? $" Windows now detects {after.DisplayName} without a reported device problem."
                    : after == null || after.Kind == "Empty port" ? " No device is currently detected at this port."
                    : " The device still reports a problem. A full power disconnect may be needed.";
            }
            var current = snapshot.Nodes.FirstOrDefault(n => n.Id == node.Id);
            restartResults[RestartKey(node)] = new(DateTime.Now, result,
                exitCode == 0 && snapshot.CapturedAt >= verifiedAt && current?.Kind == "Unavailable"
                && current.InstanceId.Equals(node.InstanceId, StringComparison.OrdinalIgnoreCase));
            StatusText.Text = result;
            AddActivity(result, exitCode == 0 ? Severity.Note : Severity.Warning, node.Id);
        }
        finally { restartingDevice = false; ShowDetails(); }
    }
}
