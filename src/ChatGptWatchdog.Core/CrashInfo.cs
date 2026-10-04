using System.Diagnostics;
using System.Text;

namespace ChatGptWatchdog.Core;

/// <summary>Looks up recent crash records (Application Error / Hang events) for the app in the Windows event log.</summary>
public static class CrashInfo
{
    /// <summary>
    /// Returns a short summary of crash/hang events for <paramref name="exeName"/> in the last <paramref name="window"/>,
    /// or null if none were found.
    /// </summary>
    public static string? FindRecentCrash(string exeName, TimeSpan window)
    {
        try
        {
            var ms = (long)window.TotalMilliseconds;
            // 1000 = Application Error, 1002 = Application Hang, 1026 = .NET Runtime
            var query = $"*[System[(EventID=1000 or EventID=1002) and TimeCreated[timediff(@SystemTime) <= {ms}]]]";
            var psi = new ProcessStartInfo("wevtutil.exe", $"qe Application /q:\"{query}\" /f:text /rd:true /c:20")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);

            // Events are separated by "Event[n]:" headers.
            var events = text.Split("Event[", StringSplitOptions.RemoveEmptyEntries);
            foreach (var ev in events)
            {
                if (ev.IndexOf(exeName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                return Summarize(ev);
            }
        }
        catch { }
        return null;
    }

    private static string Summarize(string ev)
    {
        var lines = ev.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var interesting = new List<string>();
        string? date = lines.FirstOrDefault(l => l.StartsWith("Date:", StringComparison.OrdinalIgnoreCase));
        string? id = lines.FirstOrDefault(l => l.StartsWith("Event ID:", StringComparison.OrdinalIgnoreCase));
        if (id != null) interesting.Add(id);
        if (date != null) interesting.Add(date);
        foreach (var l in lines)
        {
            // English and (best effort) Dutch/French/German labels.
            if (l.Contains("Exception code", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("Uitzonderingscode", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("Code d", StringComparison.OrdinalIgnoreCase) && l.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("Ausnahmecode", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("Faulting module name", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("Faulting application name", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("module", StringComparison.OrdinalIgnoreCase) && l.Contains(':') && interesting.Count < 6)
            {
                interesting.Add(l);
            }
        }
        if (interesting.Count <= 2)
        {
            var desc = lines.SkipWhile(l => !l.StartsWith("Description:", StringComparison.OrdinalIgnoreCase)).Take(4);
            interesting.AddRange(desc);
        }
        return string.Join(" | ", interesting.Distinct().Take(8));
    }
}
