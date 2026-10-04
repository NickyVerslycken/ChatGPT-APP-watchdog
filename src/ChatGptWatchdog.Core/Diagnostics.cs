using System.Diagnostics;
using System.Text;
using ChatGptWatchdog.Core.Resume;

namespace ChatGptWatchdog.Core;

/// <summary>Builds a plain-text diagnostics report (useful for bug reports and for tuning settings).</summary>
public static class DiagnosticsReport
{
    public static string Build(WatchdogSettings s, TargetApp app, CodexSessions sessions)
    {
        var sb = new StringBuilder();
        void Line(string t = "") => sb.AppendLine(t);

        Line($"ChatGPT APP watchdog diagnostics — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Line($"Watchdog version: {(System.Reflection.Assembly.GetEntryAssembly() ?? typeof(DiagnosticsReport).Assembly).GetName().Version}");
        Line($"OS: {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}), .NET {Environment.Version}");
        Line($"Data folder: {AppPaths.DataDirectory}{(AppPaths.IsPortable ? " (portable)" : "")}");
        Line();

        Line("== App processes ==");
        var procs = Safe(() => app.FindAppProcesses(), new List<ProcInfo>());
        var main = TargetApp.FindMainProcess(procs);
        foreach (var p in procs.OrderBy(p => p.StartTime))
            Line($"  pid {p.Pid,-6} parent {p.ParentPid,-6} {(p == main ? "[MAIN] " : p.IsHelper ? "[helper] " : "")}{p.StartTime:HH:mm:ss}  {p.Path ?? "(path not readable)"}{(p.CommandLine == null ? "  (command line not readable)" : "")}");
        if (procs.Count == 0) Line("  (not running)");
        var all = Safe(() => TargetApp.SnapshotProcesses(n => n.StartsWith("codex", StringComparison.OrdinalIgnoreCase)), new List<ProcInfo>());
        foreach (var p in all)
            Line($"  codex: pid {p.Pid,-6} parent {p.ParentPid,-6} {p.Path ?? "(path not readable)"}");
        Line();

        Line("== Windows ==");
        var pids = procs.Select(p => p.Pid).ToHashSet();
        foreach (var w in Safe(() => TargetApp.GetTopLevelWindows(pids), new List<WindowInfo>()).Where(w => w.Visible))
            Line($"  hwnd 0x{w.Handle.ToInt64():X} pid {w.Pid} {w.Width}x{w.Height}{(w.Minimized ? " minimized" : "")} hung={TargetApp.IsHung(w.Handle)} \"{w.Title}\"");
        var mw = Safe(() => app.FindMainWindow(), null);
        Line($"  main window: {(mw == null ? "(none)" : $"0x{mw.Handle.ToInt64():X} \"{mw.Title}\"")}");
        Line();

        Line("== Launch ==");
        Line($"  AppUserModelId: {app.ResolveAumid()}");
        Line($"  Package folder: {Safe(() => app.GetPackageInstallPath(), null) ?? "(not found)"}");
        Line($"  Custom launch command: {(string.IsNullOrWhiteSpace(s.CustomLaunchCommand) ? "(none)" : s.CustomLaunchCommand)}");
        Line();

        Line("== Codex CLI ==");
        var found = Safe(() => CodexCliLocator.Locate(s.Resume, app), null);
        Line($"  Selected: {(found == null ? "(not found)" : $"{found.Path} ({found.How})")}");
        foreach (var c in Safe(() => CodexCliLocator.FindAllCandidates(app), new List<string>())) Line($"  candidate: {c}");
        Line();

        Line("== Process command lines (PowerShell/CIM) ==");
        Line(Indent(Safe(GetCommandLines, "(failed)")));
        Line();

        Line("== Codex sessions ==");
        Line($"  Codex home: {sessions.CodexHome} (exists: {Directory.Exists(sessions.CodexHome)})");
        var targets = Safe(() => sessions.ReadAutomationTargetThreads(), new HashSet<string>());
        Line($"  Automation target chats: {(targets.Count == 0 ? "(none)" : string.Join(", ", targets))}");
        var runs = Safe(() => sessions.ReadAutomationRunThreads(), new HashSet<string>());
        Line($"  Automation run chats (from codex-dev.db): {runs.Count}");
        var locked = Safe(() => sessions.ReadLockedThreads(), new HashSet<string>());
        Line($"  Chats open in the app (lock files): {locked.Count}");
        string? dbErr = null;
        var recent = Safe(() => sessions.ReadRecentlyUpdatedThreads(DateTime.UtcNow.AddHours(-24), out dbErr), new HashSet<string>());
        Line($"  Chats updated in 24h per state database: {recent.Count}{(dbErr != null ? $" (database: {dbErr})" : "")}");
        var threads = Safe(() => sessions.ScanThreads(DateTime.UtcNow.AddHours(-24)), new List<CodexThread>());
        Line($"  Chats active in the last 24h: {threads.Count}");
        foreach (var t in threads)
        {
            Line($"  - {t.LastWriteUtc.ToLocalTime():MM-dd HH:mm}  {(t.HasOpenTurn ? "BUSY " : "idle ")} {t.ThreadId}  \"{Trim(t.DisplayTitle, 50)}\"" +
                 $"{(t.ExcludedReason != null ? $"  [skip: {t.ExcludedReason}]" : "")}  last={t.LastTurnEvent}@{t.LastTurnEventUtc?.ToLocalTime():HH:mm:ss}");
        }
        Line();
        Line("== Settings ==");
        Line(Indent(s.ToJson()));
        return sb.ToString();
    }

    private static string GetCommandLines()
    {
        const string script =
            "Get-CimInstance Win32_Process -Filter \"Name='ChatGPT.exe' or Name like 'codex%'\" | " +
            "ForEach-Object { '{0} {1} {2}' -f $_.ProcessId, $_.ParentProcessId, $_.CommandLine }";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " + encoded)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        var outp = p.StandardOutput.ReadToEnd();
        p.WaitForExit(20000);
        // Keep it readable: Chromium helper command lines are very long.
        return string.Join(Environment.NewLine, outp.Split('\n').Select(l => Trim(l.TrimEnd(), 400)));
    }

    private static string Indent(string s) => "  " + s.Replace("\n", "\n  ");
    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private static T Safe<T>(Func<T> f, T fallback)
    {
        try { return f(); } catch (Exception ex) { Debug.WriteLine(ex); return fallback; }
    }
}
