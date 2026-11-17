using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using FanCtl.Shared;

namespace FanCtl.UI;

public partial class App : Application
{
    Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        // The window only ever runs as a child of the elevated FanCtl.exe, which owns the hardware
        // and the shared memory. Launched any other way, hand over to the service and quit.
        bool child = e.Args.Length > 0 && e.Args[0] == "--child";
        if (!child)
        {
            if (!Signal(Ipc.ShowEvent))
            {
                string svc = Path.Combine(AppContext.BaseDirectory, "FanCtl.exe");
                try { Process.Start(new ProcessStartInfo(svc) { UseShellExecute = true }); } catch { }
            }
            Shutdown();
            return;
        }

        _single = new Mutex(true, @"Local\FanCtl.UiInstance", out bool first);
        if (!first)
        {
            Signal(Ipc.UiFocusEvent);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUiException;
        base.OnStartup(e);
        Theme.Init(Resources);
        new MainWindow().Show();
    }

    static bool Signal(string name)
    {
        try { using var ev = EventWaitHandle.OpenExisting(name); return ev.Set(); }
        catch { return false; }
    }

    /// <summary>Log and keep the window alive: a UI glitch must not look like a crash.</summary>
    void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ConfigStore.Dir);
            File.AppendAllText(Path.Combine(ConfigStore.Dir, "ui-errors.log"), $"[{DateTime.Now:s}] {e.Exception}\n\n");
        }
        catch { }
        e.Handled = true;
    }
}
