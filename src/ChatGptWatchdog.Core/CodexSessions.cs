using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatGptWatchdog.Core.Interop;

namespace ChatGptWatchdog.Core;

/// <summary>What the watchdog knows about one Codex chat (thread).</summary>
public sealed class CodexThread
{
    public required string ThreadId { get; init; }
    public required string RolloutPath { get; init; }
    /// <summary>Last activity: newest of the file time and the last timestamp inside the session file.</summary>
    public DateTime LastWriteUtc { get; set; }
    public string? Title { get; set; }
    public string? Cwd { get; set; }
    public string? Source { get; set; }
    public bool IsSubAgent { get; set; }
    public string? ParentThreadId { get; set; }

    /// <summary>True when the last turn was started but never completed/aborted.</summary>
    public bool HasOpenTurn { get; set; }
    public string? OpenTurnId { get; set; }
    public DateTime? OpenTurnStartedUtc { get; set; }
    public DateTime? LastTurnEventUtc { get; set; }
    public string? LastTurnEvent { get; set; }

    /// <summary>Why this chat is not resumed automatically (null = eligible).</summary>
    public string? ExcludedReason { get; set; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? ThreadId : Title!;
    public override string ToString() => $"'{DisplayTitle}' ({ThreadId})";
}

/// <summary>Reads Codex's local session files (~/.codex) to find chats that were busy when the app died.</summary>
public sealed class CodexSessions
{
    private static readonly Regex RolloutName = new(
        @"^rollout-\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}-(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})(?:_(?<win>[0-9a-fA-F-]{36}))?\.jsonl$",
        RegexOptions.Compiled);

    private readonly Func<WatchdogSettings> _settings;
    private readonly Logger? _log;

    public CodexSessions(Func<WatchdogSettings> settings, Logger? log = null)
    {
        _settings = settings;
        _log = log;
    }

    public string CodexHome
    {
        get
        {
            var s = _settings().CodexHome;
            if (!string.IsNullOrWhiteSpace(s)) return Environment.ExpandEnvironmentVariables(s.Trim());
            var env = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }
    }

    public string SessionsDir => Path.Combine(CodexHome, "sessions");

    // ------------------------------------------------------------------ discovery
    //
    // Note: Windows updates a file's "last modified" time lazily while another process keeps the file open
    // for writing (the Codex app does). So file times are only a hint; the real activity time comes from
    // the timestamps inside the session file, and threads the app has open are found via its lock files
    // and its state database as well.

    private static string RolloutSortKey(FileInfo f) => f.Name; // "rollout-<yyyy-MM-ddTHH-mm-ss>-..." sorts by creation time

    /// <summary>Thread id → all of its rollout files (newest first).</summary>
    private Dictionary<string, List<FileInfo>> IndexRollouts()
    {
        var map = new Dictionary<string, List<FileInfo>>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(SessionsDir)) return map;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var path in Directory.EnumerateFiles(SessionsDir, "rollout-*.jsonl", opts))
        {
            FileInfo fi;
            try { fi = new FileInfo(path); } catch { continue; }
            var m = RolloutName.Match(fi.Name);
            if (!m.Success) continue;
            var id = m.Groups["id"].Value.ToLowerInvariant();
            if (!map.TryGetValue(id, out var list)) map[id] = list = new List<FileInfo>();
            list.Add(fi);
        }
        foreach (var list in map.Values)
            list.Sort((x, y) => string.CompareOrdinal(RolloutSortKey(y), RolloutSortKey(x)));
        return map;
    }

    /// <summary>All rollout files of one thread, newest first (a long chat can span several files).</summary>
    public List<FileInfo> FindRolloutsForThread(string threadId)
    {
        var list = new List<FileInfo>();
        if (!Directory.Exists(SessionsDir)) return list;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var path in Directory.EnumerateFiles(SessionsDir, $"rollout-*{threadId}*.jsonl", opts))
        {
            try
            {
                var fi = new FileInfo(path);
                var m = RolloutName.Match(fi.Name);
                if (m.Success && m.Groups["id"].Value.Equals(threadId, StringComparison.OrdinalIgnoreCase)) list.Add(fi);
            }
            catch { }
        }
        list.Sort((x, y) => string.CompareOrdinal(RolloutSortKey(y), RolloutSortKey(x)));
        return list;
    }

    /// <summary>Threads the app currently has open for writing (~/.codex/thread-writer-locks/&lt;id&gt;.lock).</summary>
    public HashSet<string> ReadLockedThreads()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var dir = Path.Combine(CodexHome, "thread-writer-locks");
            if (Directory.Exists(dir))
                foreach (var f in Directory.EnumerateFiles(dir, "*.lock"))
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    if (Guid.TryParse(name, out _)) set.Add(name.ToLowerInvariant());
                }
        }
        catch { }
        return set;
    }

    /// <summary>Threads updated since <paramref name="sinceUtc"/> according to the app's state database (state_*.sqlite).</summary>
    public HashSet<string> ReadRecentlyUpdatedThreads(DateTime sinceUtc, out string? error)
    {
        error = null;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var db = Directory.EnumerateFiles(CodexHome, "state_*.sqlite").OrderByDescending(f => f).FirstOrDefault();
            if (db == null) { error = "no state database"; return set; }
            var since = new DateTimeOffset(sinceUtc).ToUnixTimeSeconds();
            foreach (var id in WinSqlite.QueryColumn(db, $"select id from threads where archived = 0 and updated_at >= {since}", out error))
                set.Add(id.ToLowerInvariant());
        }
        catch (Exception ex) { error = ex.Message; }
        return set;
    }

    /// <summary>Threads active since <paramref name="sinceUtc"/>, with their turn state and exclusion reason.</summary>
    public List<CodexThread> ScanThreads(DateTime sinceUtc)
    {
        var index = IndexRollouts();
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, files) in index)
            if (files.Any(f => f.LastWriteTimeUtc >= sinceUtc)) candidates.Add(id);
        candidates.UnionWith(ReadLockedThreads().Where(index.ContainsKey));
        candidates.UnionWith(ReadRecentlyUpdatedThreads(sinceUtc, out _).Where(index.ContainsKey));

        var titles = ReadTitles();
        var automationTargets = ReadAutomationTargetThreads();
        var automationRuns = ReadAutomationRunThreads();
        var s = _settings().Resume;

        var list = new List<CodexThread>();
        foreach (var id in candidates)
        {
            var fi = index[id][0];
            var t = new CodexThread { ThreadId = id, RolloutPath = fi.FullName, LastWriteUtc = fi.LastWriteTimeUtc };
            titles.TryGetValue(id, out var title);
            t.Title = title;
            try
            {
                ReadMeta(t);
                ReadTurnState(t);
            }
            catch (Exception ex)
            {
                _log?.Debug($"Could not read session file {fi.Name}: {ex.Message}");
                continue;
            }
            if (t.LastWriteUtc < sinceUtc) continue; // not active within the window after all

            if (automationTargets.Contains(id)) t.ExcludedReason = "driven by an automation (heartbeat)";
            else if (automationRuns.Contains(id)) t.ExcludedReason = "automation run";
            else if (t.IsSubAgent) t.ExcludedReason = "sub-agent thread";

            if (t.IsSubAgent && !s.SkipSubAgentChats && t.ExcludedReason == "sub-agent thread") t.ExcludedReason = null;
            if (!s.SkipAutomationChats && (automationTargets.Contains(id) || automationRuns.Contains(id))) t.ExcludedReason = null;
            list.Add(t);
        }
        return list.OrderByDescending(t => t.LastWriteUtc).ToList();
    }

    /// <summary>Chats that were busy (open turn) and active within the look-back window before <paramref name="crashUtc"/>.</summary>
    public List<CodexThread> FindInterruptedThreads(DateTime crashUtc, TimeSpan lookback)
    {
        return ScanThreads(crashUtc - lookback).Where(t => t.HasOpenTurn).ToList();
    }

    public CodexThread? GetThread(string threadId)
    {
        var file = FindRolloutsForThread(threadId).FirstOrDefault();
        if (file == null) return null;
        var t = new CodexThread { ThreadId = threadId.ToLowerInvariant(), RolloutPath = file.FullName, LastWriteUtc = file.LastWriteTimeUtc };
        ReadTitles().TryGetValue(t.ThreadId, out var title);
        t.Title = title;
        ReadMeta(t);
        ReadTurnState(t);
        return t;
    }

    // ------------------------------------------------------------------ verification

    /// <summary>True if the thread's newest rollout files contain a task_started event after <paramref name="sinceUtc"/>.</summary>
    public bool HasTurnStartedSince(string threadId, DateTime sinceUtc)
    {
        foreach (var fi in FindRolloutsForThread(threadId).Take(2))
        {
            try
            {
                var (events, _) = ReadLifecycleEvents(fi.FullName, 2 * 1024 * 1024);
                if (events.Any(ev => ev.Type == "task_started" && ev.TimeUtc >= sinceUtc)) return true;
            }
            catch { }
        }
        return false;
    }

    public async Task<bool> WaitForTurnStartedAsync(string threadId, DateTime sinceUtc, TimeSpan timeout, CancellationToken ct)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            ct.ThrowIfCancellationRequested();
            if (HasTurnStartedSince(threadId, sinceUtc)) return true;
            await Task.Delay(2000, ct).ConfigureAwait(false);
        }
        return HasTurnStartedSince(threadId, sinceUtc);
    }

    // ------------------------------------------------------------------ parsing

    private sealed record Lifecycle(string Type, string? TurnId, DateTime TimeUtc);

    private void ReadMeta(CodexThread t)
    {
        using var fs = new FileStream(t.RolloutPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        var first = sr.ReadLine();
        if (string.IsNullOrEmpty(first)) return;
        using var doc = JsonDocument.Parse(first);
        var root = doc.RootElement;
        if (!root.TryGetProperty("type", out var type) || type.GetString() != "session_meta") return;
        if (!root.TryGetProperty("payload", out var p)) return;
        if (p.TryGetProperty("cwd", out var cwd)) t.Cwd = cwd.GetString();
        if (p.TryGetProperty("source", out var src))
        {
            if (src.ValueKind == JsonValueKind.String) t.Source = src.GetString();
            else
            {
                t.Source = src.ValueKind == JsonValueKind.Object && src.TryGetProperty("subagent", out _) ? "subagent" : src.GetRawText();
                if (t.Source == "subagent") t.IsSubAgent = true;
            }
        }
        if (p.TryGetProperty("thread_source", out var ts) && ts.ValueKind == JsonValueKind.String && ts.GetString() == "subagent")
            t.IsSubAgent = true;
        if (p.TryGetProperty("parent_thread_id", out var parent) && parent.ValueKind == JsonValueKind.String)
            t.ParentThreadId = parent.GetString();
    }

    private void ReadTurnState(CodexThread t)
    {
        var (events, lastLineUtc) = ReadLifecycleEvents(t.RolloutPath, 512 * 1024);
        if (lastLineUtc != null && lastLineUtc > t.LastWriteUtc) t.LastWriteUtc = lastLineUtc.Value;
        var last = events.LastOrDefault();
        if (last == null) return;
        t.LastTurnEvent = last.Type;
        t.LastTurnEventUtc = last.TimeUtc;
        if (last.Type == "task_started")
        {
            t.HasOpenTurn = true;
            t.OpenTurnId = last.TurnId;
            t.OpenTurnStartedUtc = last.TimeUtc;
        }
    }

    /// <summary>
    /// Reads turn lifecycle events (task_started / task_complete / turn_aborted) from the end of a rollout file.
    /// Starts with <paramref name="initialTail"/> bytes and grows until at least one event is found or the whole file is read.
    /// </summary>
    private static (List<Lifecycle> events, DateTime? lastLineUtc) ReadLifecycleEvents(string path, long initialTail)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long len = fs.Length;
        long tail = Math.Min(initialTail, len);
        DateTime? lastLine = null;
        while (true)
        {
            var events = ParseTail(fs, len, tail, ref lastLine);
            if (events.Count > 0 || tail >= len) return (events, lastLine);
            tail = Math.Min(len, tail * 4);
        }
    }

    /// <summary>Reads the "timestamp" at the start of a rollout line without parsing the whole (possibly huge) line.</summary>
    private static DateTime? LineTimestamp(string line)
    {
        const string prefix = "{\"timestamp\":\"";
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var end = line.IndexOf('"', prefix.Length);
        if (end < 0) return null;
        return DateTime.TryParse(line.AsSpan(prefix.Length, end - prefix.Length), null,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt)
            ? dt : null;
    }

    private static List<Lifecycle> ParseTail(FileStream fs, long len, long tail, ref DateTime? lastLine)
    {
        var events = new List<Lifecycle>();
        fs.Seek(len - tail, SeekOrigin.Begin);
        using var sr = new StreamReader(fs, Encoding.UTF8, false, 1 << 16, leaveOpen: true);
        if (tail < len) sr.ReadLine(); // skip the partial first line
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            var ts0 = LineTimestamp(line);
            if (ts0 != null && (lastLine == null || ts0 > lastLine)) lastLine = ts0;
            // Cheap pre-filter before parsing JSON (lines can be megabytes long).
            if (!line.Contains("\"task_started\"") && !line.Contains("\"task_complete\"") && !line.Contains("\"turn_aborted\""))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var type) || type.GetString() != "event_msg") continue;
                if (!root.TryGetProperty("payload", out var p) || !p.TryGetProperty("type", out var pt)) continue;
                var kind = pt.GetString();
                if (kind is not ("task_started" or "task_complete" or "turn_aborted")) continue;
                string? turnId = p.TryGetProperty("turn_id", out var tid) && tid.ValueKind == JsonValueKind.String ? tid.GetString() : null;
                var time = root.TryGetProperty("timestamp", out var ts) && ts.TryGetDateTime(out var dt)
                    ? dt.ToUniversalTime() : DateTime.MinValue;
                events.Add(new Lifecycle(kind!, turnId, time));
            }
            catch { /* partial or odd line */ }
        }
        return events;
    }

    // ------------------------------------------------------------------ metadata files

    /// <summary>Thread titles from session_index.jsonl (last entry per id wins).</summary>
    public Dictionary<string, string> ReadTitles()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(CodexHome, "session_index.jsonl");
        try
        {
            if (!File.Exists(path)) return map;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    if (r.TryGetProperty("id", out var id) && r.TryGetProperty("thread_name", out var name))
                        map[id.GetString() ?? ""] = name.GetString() ?? "";
                }
                catch { }
            }
        }
        catch { }
        return map;
    }

    /// <summary>Threads that active automations post into (automations/*/automation.toml → target_thread_id).</summary>
    public HashSet<string> ReadAutomationTargetThreads()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dir = Path.Combine(CodexHome, "automations");
        try
        {
            if (!Directory.Exists(dir)) return set;
            foreach (var file in Directory.EnumerateFiles(dir, "automation.toml", SearchOption.AllDirectories))
            {
                string? target = null, status = null;
                foreach (var raw in File.ReadLines(file))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("target_thread_id", StringComparison.Ordinal)) target = TomlString(line);
                    else if (line.StartsWith("status", StringComparison.Ordinal) && line.Contains('=')) status ??= TomlString(line);
                }
                if (!string.IsNullOrEmpty(target) && !string.Equals(status, "PAUSED", StringComparison.OrdinalIgnoreCase))
                    set.Add(target);
            }
        }
        catch { }
        // The app's own database knows them too.
        foreach (var id in WinSqlite.QueryColumn(Path.Combine(CodexHome, "sqlite", "codex-dev.db"),
                     "select target_thread_id from automations where target_thread_id is not null and status <> 'PAUSED'", out _))
            if (!string.IsNullOrEmpty(id)) set.Add(id);
        return set;
    }

    /// <summary>Threads created by scheduled automation runs (from the app's codex-dev.db, if readable).</summary>
    public HashSet<string> ReadAutomationRunThreads()
    {
        var ids = WinSqlite.QueryColumn(Path.Combine(CodexHome, "sqlite", "codex-dev.db"),
            "select thread_id from automation_runs", out _);
        return new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
    }

    private static string? TomlString(string line)
    {
        var i = line.IndexOf('=');
        if (i < 0) return null;
        var v = line[(i + 1)..].Trim();
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = v[1..^1];
        return v;
    }

    /// <summary>Removes the \\?\ prefix Windows long paths sometimes carry.</summary>
    public static string? CleanPath(string? p)
    {
        if (p == null) return null;
        if (p.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + p[8..];
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) return p[4..];
        return p;
    }
}
