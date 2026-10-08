using System.IO;
using System.Text.Json;
using System.Windows;

namespace UsbAtlas;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Theme.Initialize(e.Args);
        if (e.Args.Contains("--scan"))
        {
            var path = e.Args.SkipWhile(x => x != "--scan").Skip(1).FirstOrDefault() ?? "scan.json";
            File.WriteAllText(path, JsonSerializer.Serialize(new UsbScanner().Scan(), new JsonSerializerOptions { WriteIndented = true }));
            Shutdown();
            return;
        }
        if (e.Args.Contains("--self-test"))
        {
            try { SelfTests.Run(); File.WriteAllText("self-test.txt", "All checks passed."); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText("self-test.txt", ex.ToString()); Shutdown(1); }
            return;
        }
        // Previews and checks ignore the saved layout, so they come out the same on every machine.
        bool? horizontal = e.Args.Contains("--vertical") ? false : e.Args.Contains("--horizontal") ? true : null;
        if (horizontal == null && (e.Args.Contains("--render") || e.Args.Contains("--verify-ui"))) horizontal = true;
        var window = new MainWindow(e.Args.Contains("--demo"), e.Args.Contains("--render"), e.Args.Contains("--verify-ui"), horizontal)
        {
            RenderSelection = e.Args.SkipWhile(x => x != "--select").Skip(1).FirstOrDefault(),
            RenderFocus = e.Args.SkipWhile(x => x != "--focus").Skip(1).FirstOrDefault()
        };
        if (e.Args.Contains("--compact")) { window.Width = 1050; window.Height = 650; }
        if (e.Args.Contains("--wide")) { window.Width = 3840; window.Height = 1560; }
        if (e.Args.Contains("--render"))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -30000; window.Top = 0;
            window.ShowActivated = false; window.ShowInTaskbar = false;
        }
        MainWindow = window;
        window.Show();
    }
}
