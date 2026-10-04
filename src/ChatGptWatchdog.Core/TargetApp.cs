using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ChatGptWatchdog.Core.Interop;

namespace ChatGptWatchdog.Core;

public sealed record ProcInfo(int Pid, int ParentPid, string Name, string? Path, DateTime? StartTime, string? CommandLine = null)
{
    /// <summary>Chromium/Electron helper process (renderer, GPU, crashpad, utility...).</summary>
    public bool IsHelper => CommandLine != null && CommandLine.Contains("--type=", StringComparison.OrdinalIgnoreCase);
}

public sealed record WindowInfo(IntPtr Handle, int Pid, string Title, int Width, int Height, bool Visible, bool Minimized);

/// <summary>Finds, inspects and launches the ChatGPT/Codex desktop app.</summary>
public sealed class TargetApp
{
    public const string DefaultAumid = "OpenAI.Codex_2p2nqsd0c76g0!App";
    private readonly Func<WatchdogSettings> _settings;
    private string? _lastSeenAumid;

    public TargetApp(Func<WatchdogSettings> settings) => _settings = settings;

    // ------------------------------------------------------------------ processes

    /// <summary>All processes on the machine (name, pid, parent pid).</summary>
    public static List<ProcInfo> SnapshotProcesses(Func<string, bool>? nameFilter = null, bool withDetails = true)
    {
        var list = new List<ProcInfo>();
        var snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
        if (snap == Native.INVALID_HANDLE_VALUE) return list;
        try
        {
            var e = new Native.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<Native.PROCESSENTRY32W>() };
            if (!Native.Process32FirstW(snap, ref e)) return list;
            do
            {
                var name = e.szExeFile ?? "";
                if (nameFilter == null || nameFilter(name))
                {
                    string? path = null, cmd = null; DateTime? start = null;
                    if (withDetails) (path, start, cmd) = QueryProcess((uint)e.th32ProcessID);
                    list.Add(new ProcInfo((int)e.th32ProcessID, (int)e.th32ParentProcessID, name, path, start, cmd));
                }
            } while (Native.Process32NextW(snap, ref e));
        }
        finally { Native.CloseHandle(snap); }
        return list;
    }

    private static (string? path, DateTime? start, string? cmd) QueryProcess(uint pid)
    {
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return (null, null, null);
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            string? path = Native.QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
            DateTime? start = null;
            if (Native.GetProcessTimes(h, out var c, out _, out _, out _) && c > 0)
                start = DateTime.FromFileTime(c);
            return (path, start, QueryCommandLine(h));
        }
        finally { Native.CloseHandle(h); }
    }

    /// <summary>Command line of a process (Windows 8.1+), or null if it can't be read.</summary>
    private static string? QueryCommandLine(IntPtr h)
    {
        try
        {
            Native.NtQueryInformationProcess(h, Native.ProcessCommandLineInformation, IntPtr.Zero, 0, out var needed);
            if (needed <= 0 || needed > 1 << 20) return null;
            var buf = Marshal.AllocHGlobal(needed);
            try
            {
                if (Native.NtQueryInformationProcess(h, Native.ProcessCommandLineInformation, buf, needed, out _) != 0) return null;
                // The buffer starts with a UNICODE_STRING { ushort Length; ushort MaximumLength; IntPtr Buffer; }
                var len = (ushort)Marshal.ReadInt16(buf);
                var ptr = Marshal.ReadIntPtr(buf, IntPtr.Size);
                return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUni(ptr, len / 2);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return null; }
    }

    /// <summary>All processes that belong to the target app (main + helpers).</summary>
    public List<ProcInfo> FindAppProcesses()
    {
        var s = _settings();
        var exe = s.ProcessName + ".exe";
        var procs = SnapshotProcesses(n => n.Equals(exe, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(s.ProcessPathFilter))
        {
            // Keep processes whose path matches; if the path can't be read, keep them too (better than a false "not running").
            procs = procs.Where(p => p.Path == null ||
                                     p.Path.Contains(s.ProcessPathFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        foreach (var p in procs) RememberAumidFromPath(p.Path);
        return procs;
    }

    /// <summary>The main (browser) process: the app process whose parent is not itself an app process.</summary>
    public ProcInfo? FindMainProcess() => FindMainProcess(FindAppProcesses());

    public static ProcInfo? FindMainProcess(List<ProcInfo> procs)
    {
        if (procs.Count == 0) return null;
        // Preferred: the process without a Chromium "--type=" switch. Helper processes (e.g. the crash
        // handler) can outlive a crashed main process and must not count as "the app is running".
        if (procs.Any(p => p.CommandLine != null))
            return procs.Where(p => !p.IsHelper)
                        .OrderBy(p => p.CommandLine == null ? 1 : 0)
                        .ThenBy(p => p.StartTime ?? DateTime.MaxValue).FirstOrDefault();
        var pids = procs.Select(p => p.Pid).ToHashSet();
        var roots = procs.Where(p => !pids.Contains(p.ParentPid)).ToList();
        return roots.OrderBy(p => p.StartTime ?? DateTime.MaxValue).FirstOrDefault() ?? procs[0];
    }

    public bool IsRunning() => FindMainProcess() != null;

    // ------------------------------------------------------------------ windows

    public static List<WindowInfo> GetTopLevelWindows(ISet<int> pids)
    {
        var result = new List<WindowInfo>();
        Native.EnumWindows((h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out var pid);
            if (!pids.Contains((int)pid)) return true;
            if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true; // skip owned popups
            var len = Native.GetWindowTextLengthW(h);
            var sb = new StringBuilder(Math.Max(len + 1, 2));
            Native.GetWindowTextW(h, sb, sb.Capacity);
            Native.GetWindowRect(h, out var r);
            result.Add(new WindowInfo(h, (int)pid, sb.ToString(), r.Right - r.Left, r.Bottom - r.Top,
                Native.IsWindowVisible(h), Native.IsIconic(h)));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>The app's main window: the largest visible titled top-level window.</summary>
    public WindowInfo? FindMainWindow()
    {
        var pids = FindAppProcesses().Select(p => p.Pid).ToHashSet();
        if (pids.Count == 0) return null;
        return GetTopLevelWindows(pids)
            .Where(w => w.Visible && w.Title.Length > 0 && (w.Minimized || (w.Width > 200 && w.Height > 200)))
            .OrderByDescending(w => w.Minimized ? 0 : w.Width * w.Height)
            .FirstOrDefault();
    }

    public HashSet<int> AppPids() => FindAppProcesses().Select(p => p.Pid).ToHashSet();

    public static bool IsHung(IntPtr hwnd) => hwnd != IntPtr.Zero && Native.IsHungAppWindow(hwnd);

    // ------------------------------------------------------------------ package / AUMID

    private void RememberAumidFromPath(string? path)
    {
        if (path == null) return;
        var family = PackageFamilyFromPath(path);
        if (family != null) _lastSeenAumid = family + "!App";
    }

    /// <summary>"C:\Program Files\WindowsApps\OpenAI.Codex_26.715.4045.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe" → "OpenAI.Codex_2p2nqsd0c76g0".</summary>
    public static string? PackageFamilyFromPath(string path)
    {
        const string marker = @"\WindowsApps\";
        var i = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var rest = path[(i + marker.Length)..];
        var folder = rest.Split('\\')[0];                       // OpenAI.Codex_26.715.4045.0_x64__2p2nqsd0c76g0
        var parts = folder.Split('_');
        if (parts.Length < 5) return null;                      // name_version_arch_resource_publisherId
        return parts[0] + "_" + parts[^1];
    }

    public string ResolveAumid()
    {
        var s = _settings();
        if (!string.IsNullOrWhiteSpace(s.AppUserModelId)) return s.AppUserModelId.Trim();
        return _lastSeenAumid ?? DefaultAumid;
    }

    public static string FamilyFromAumid(string aumid) => aumid.Split('!')[0];

    /// <summary>Installed package folder of the app (e.g. C:\Program Files\WindowsApps\OpenAI.Codex_...), or null.</summary>
    public string? GetPackageInstallPath()
    {
        try
        {
            var family = FamilyFromAumid(ResolveAumid());
            uint count = 0, bufLen = 0;
            var rc = Native.GetPackagesByPackageFamily(family, ref count, null, ref bufLen, null);
            if (rc != Native.ERROR_INSUFFICIENT_BUFFER || count == 0) return null;
            var names = new IntPtr[count];
            var buffer = new char[bufLen];
            rc = Native.GetPackagesByPackageFamily(family, ref count, names, ref bufLen, buffer);
            if (rc != 0) return null;
            // The buffer holds the package full names separated by '\0'.
            var fullNames = new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            foreach (var full in fullNames)
            {
                uint len = 0;
                Native.GetPackagePathByFullName(full, ref len, null);
                if (len == 0) continue;
                var sb = new StringBuilder((int)len);
                if (Native.GetPackagePathByFullName(full, ref len, sb) == 0) return sb.ToString();
            }
        }
        catch { }
        // Fallback: derive from a running process path.
        var p = FindAppProcesses().FirstOrDefault(x => x.Path != null);
        if (p?.Path != null)
        {
            var i = p.Path.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);
            if (i >= 0)
            {
                var folder = p.Path[(i + 13)..].Split('\\')[0];
                return p.Path[..(i + 13)] + folder;
            }
        }
        return null;
    }

    // ------------------------------------------------------------------ launch

    /// <summary>Starts the app. Returns a description of how it was started.</summary>
    public string Launch()
    {
        var s = _settings();
        if (!string.IsNullOrWhiteSpace(s.CustomLaunchCommand))
        {
            Process.Start(new ProcessStartInfo(s.CustomLaunchCommand, s.CustomLaunchArguments ?? "") { UseShellExecute = true });
            return $"custom command '{s.CustomLaunchCommand}'";
        }

        var aumid = ResolveAumid();
        try
        {
            var mgr = (Native.IApplicationActivationManager)new Native.ApplicationActivationManager();
            var hr = mgr.ActivateApplication(aumid, null, 0, out var pid);
            if (hr >= 0) return $"Store app activation ({aumid}), pid {pid}";
            throw new COMException("ActivateApplication failed", hr);
        }
        catch (Exception ex)
        {
            // Fallback: let Explorer start it.
            Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{aumid}") { UseShellExecute = false });
            return $"explorer shell:AppsFolder\\{aumid} (activation API failed: {ex.Message})";
        }
    }

    /// <summary>Opens a codex:// deep link (handled by the app).</summary>
    public static void OpenUri(string uri)
    {
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    /// <summary>Kills all app processes (used for hang recovery and the "simulate crash" test).</summary>
    public int KillAll()
    {
        int n = 0;
        foreach (var p in FindAppProcesses())
        {
            try { using var proc = Process.GetProcessById(p.Pid); proc.Kill(entireProcessTree: true); n++; }
            catch { }
        }
        return n;
    }
}
