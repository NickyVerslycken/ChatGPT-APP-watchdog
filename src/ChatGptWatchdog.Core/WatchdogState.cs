using System.Text.Json;

namespace ChatGptWatchdog.Core;

/// <summary>Small persisted state: restart history and which interrupted turns were already handled.</summary>
public sealed class WatchdogState
{
    private readonly object _lock = new();
    private readonly string? _path;

    public List<DateTime> RestartTimesUtc { get; set; } = new();
    public Dictionary<string, DateTime> ResumedTurns { get; set; } = new();
    public int TotalRestarts { get; set; }
    public DateTime? LastCrashUtc { get; set; }

    public WatchdogState() { }
    private WatchdogState(string path) { _path = path; }

    public static WatchdogState Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<WatchdogState>(File.ReadAllText(path));
                if (loaded != null)
                {
                    var s = new WatchdogState(path)
                    {
                        RestartTimesUtc = loaded.RestartTimesUtc ?? new(),
                        ResumedTurns = loaded.ResumedTurns ?? new(),
                        TotalRestarts = loaded.TotalRestarts,
                        LastCrashUtc = loaded.LastCrashUtc,
                    };
                    s.Prune();
                    return s;
                }
            }
        }
        catch { }
        return new WatchdogState(path);
    }

    public void Save()
    {
        if (_path == null) return;
        try
        {
            lock (_lock)
            {
                Prune();
                File.WriteAllText(_path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch { }
    }

    private void Prune()
    {
        var cutoff = DateTime.UtcNow.AddDays(-7);
        RestartTimesUtc = RestartTimesUtc.Where(t => t > cutoff).ToList();
        foreach (var k in ResumedTurns.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList()) ResumedTurns.Remove(k);
    }

    public void RecordRestart()
    {
        lock (_lock) { RestartTimesUtc.Add(DateTime.UtcNow); TotalRestarts++; }
        Save();
    }

    public int RestartsWithin(TimeSpan window)
    {
        lock (_lock) { var from = DateTime.UtcNow - window; return RestartTimesUtc.Count(t => t >= from); }
    }

    public int RestartsToday()
    {
        lock (_lock) { var today = DateTime.Now.Date; return RestartTimesUtc.Count(t => t.ToLocalTime().Date == today); }
    }

    public bool WasResumed(string turnId) { lock (_lock) return ResumedTurns.ContainsKey(turnId); }

    public void MarkResumed(string turnId)
    {
        lock (_lock) ResumedTurns[turnId] = DateTime.UtcNow;
        Save();
    }
}
