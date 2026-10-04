using System.Collections.Concurrent;
using System.Text;

namespace ChatGptWatchdog.Core;

public enum LogLevel { Debug, Info, Success, Warning, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message);

/// <summary>Thread-safe logger: raises an event for the UI and appends to a daily log file.</summary>
public sealed class Logger
{
    private readonly object _fileLock = new();
    private readonly ConcurrentQueue<LogEntry> _recent = new();
    private const int MaxRecent = 3000;

    public string LogDirectory { get; }
    public event Action<LogEntry>? EntryAdded;

    public Logger(string logDirectory)
    {
        LogDirectory = logDirectory;
        Directory.CreateDirectory(logDirectory);
    }

    public IReadOnlyCollection<LogEntry> Recent => _recent.ToArray();

    public string CurrentLogFile => Path.Combine(LogDirectory, $"watchdog-{DateTime.Now:yyyy-MM-dd}.log");

    public void Debug(string m) => Write(LogLevel.Debug, m);
    public void Info(string m) => Write(LogLevel.Info, m);
    public void Success(string m) => Write(LogLevel.Success, m);
    public void Warn(string m) => Write(LogLevel.Warning, m);
    public void Error(string m) => Write(LogLevel.Error, m);

    public void Write(LogLevel level, string message)
    {
        var e = new LogEntry(DateTime.Now, level, message);
        _recent.Enqueue(e);
        while (_recent.Count > MaxRecent && _recent.TryDequeue(out _)) { }

        try
        {
            lock (_fileLock)
            {
                File.AppendAllText(CurrentLogFile,
                    $"{e.Time:yyyy-MM-dd HH:mm:ss} [{LevelTag(level)}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch { /* logging must never crash the watchdog */ }

        try { EntryAdded?.Invoke(e); } catch { }
    }

    public static string LevelTag(LogLevel l) => l switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO ",
        LogLevel.Success => "OK   ",
        LogLevel.Warning => "WARN ",
        LogLevel.Error => "ERROR",
        _ => "?    ",
    };

    public void CleanupOldLogs(int keepDays)
    {
        try
        {
            var limit = DateTime.Now.AddDays(-keepDays);
            foreach (var f in Directory.EnumerateFiles(LogDirectory, "*.log"))
            {
                if (File.GetLastWriteTime(f) < limit) File.Delete(f);
            }
        }
        catch { }
    }
}

/// <summary>Where the watchdog keeps its settings, state and logs.</summary>
public static class AppPaths
{
    /// <summary>
    /// Portable mode: if a file named "portable.txt" (or settings.json) sits next to the exe,
    /// everything is stored next to the exe. Otherwise in %LOCALAPPDATA%\ChatGPT-APP-watchdog.
    /// </summary>
    public static string DataDirectory { get; } = ResolveDataDirectory();

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string StateFile => Path.Combine(DataDirectory, "state.json");
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
    public static bool IsPortable { get; private set; }

    private static string ResolveDataDirectory()
    {
        var exeDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(exeDir, "portable.txt")) || File.Exists(Path.Combine(exeDir, "settings.json")))
        {
            IsPortable = true;
            return exeDir;
        }
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChatGPT-APP-watchdog");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
