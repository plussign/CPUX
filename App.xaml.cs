using System.IO;
using System.Windows;
using CpuX.Services;

namespace CpuX;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && (e.Args[0] == "--collect-internal" || e.Args[0] == "--report"))
        {
            try
            {
                var snapshot = HardwareCollector.Collect();
                File.WriteAllText(e.Args[1], e.Args[0] == "--collect-internal" || Path.GetExtension(e.Args[1]).Equals(".json", StringComparison.OrdinalIgnoreCase) ? snapshot.ToJson() : snapshot.ToReport(), new System.Text.UTF8Encoding(false));
                Shutdown(0);
            }
            catch { Shutdown(1); }
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--self-test")
        {
            var result = Tests.SelfChecks.Run();
            File.WriteAllText(e.Args[1], result.Report);
            Shutdown(result.Passed ? 0 : 1);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--layout-check")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var result = Tests.LayoutChecks.Run(e.Args[1]);
            Shutdown(result ? 0 : 1);
            return;
        }
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
