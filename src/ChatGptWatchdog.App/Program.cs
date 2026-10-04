using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using ChatGptWatchdog.App.Forms;

namespace ChatGptWatchdog.App;

internal static class Program
{
    private const string MutexName = @"Local\ChatGPT-APP-watchdog.single-instance";
    internal const string ShowEventName = @"Local\ChatGPT-APP-watchdog.show";

    [STAThread]
    private static int Main(string[] args)
    {
        using var mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // Already running: ask the running instance to show its window.
            try { using var ev = EventWaitHandle.OpenExisting(ShowEventName); ev.Set(); } catch { }
            return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetColorMode(SystemColorMode.System);

        var startMinimized = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        using var services = new Services();
        Application.ThreadException += (_, e) => services.Log.Error("Unexpected UI error: " + e.Exception.Message);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => services.Log.Error("Unexpected error: " + e.ExceptionObject);

        using var form = new MainForm(services, startMinimized);
        Application.Run(form);
        return 0;
    }
}
