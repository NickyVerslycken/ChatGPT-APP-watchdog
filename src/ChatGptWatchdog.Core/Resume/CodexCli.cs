using System.Diagnostics;
using System.Text;

namespace ChatGptWatchdog.Core.Resume;

/// <summary>Finds the Codex CLI executable.</summary>
public static class CodexCliLocator
{
    public sealed record Found(string Path, string How);

    public static Found? Locate(ResumeSettings s, TargetApp app, Logger? log = null)
    {
        if (!string.IsNullOrWhiteSpace(s.CodexCliPath))
        {
            var p = Environment.ExpandEnvironmentVariables(s.CodexCliPath.Trim().Trim('"'));
            if (File.Exists(p)) return new Found(p, "settings");
            log?.Warn($"Configured codex.exe path does not exist: {p}");
        }
        return Candidates(app).FirstOrDefault();
    }

    public static List<string> FindAllCandidates(TargetApp app) => Candidates(app).Select(c => $"{c.Path} ({c.How})").ToList();

    /// <summary>Possible codex.exe locations, best first.</summary>
    private static List<Found> Candidates(TargetApp app)
    {
        var list = new List<Found>();
        void Add(string? path, string how)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            if (list.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
            list.Add(new Found(path, how));
        }

        // 1. The exact codex.exe the running desktop app uses (its app-server child process).
        try
        {
            var appPids = app.FindAppProcesses().Select(p => p.Pid).ToHashSet();
            foreach (var p in TargetApp.SnapshotProcesses(n => n.Equals("codex.exe", StringComparison.OrdinalIgnoreCase)))
                if (appPids.Contains(p.ParentPid)) Add(p.Path, "used by the running app");
        }
        catch { }

        // 2. The desktop app's own CLI copy: %LOCALAPPDATA%\OpenAI\Codex\bin\<hash>\codex.exe (newest first).
        try
        {
            var bin = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
            if (Directory.Exists(bin))
                foreach (var f in Directory.EnumerateFiles(bin, "codex.exe", SearchOption.AllDirectories)
                             .OrderByDescending(File.GetLastWriteTimeUtc))
                    Add(f, "app's CLI folder");
        }
        catch { }

        // 3. A separately installed CLI on PATH (npm: codex.cmd, or a standalone codex.exe).
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in new[] { "codex.exe", "codex.cmd" })
                try { Add(System.IO.Path.Combine(dir.Trim().Trim('"'), name), "PATH"); } catch { }

        // 4. Inside the app package (app\resources\codex.exe). Note: app\Codex.exe is the desktop app itself, not the CLI.
        var pkg = app.GetPackageInstallPath();
        if (pkg != null) Add(System.IO.Path.Combine(pkg, "app", "resources", "codex.exe"), "app package");
        return list;
    }

    /// <summary>Splits a command-line string into arguments (supports double quotes).</summary>
    public static List<string> SplitArgs(string s)
    {
        var args = new List<string>();
        var cur = new StringBuilder();
        bool inQ = false, any = false;
        foreach (var c in s)
        {
            if (c == '"') { inQ = !inQ; any = true; continue; }
            if (char.IsWhiteSpace(c) && !inQ)
            {
                if (any || cur.Length > 0) { args.Add(cur.ToString()); cur.Clear(); any = false; }
                continue;
            }
            cur.Append(c);
        }
        if (any || cur.Length > 0) args.Add(cur.ToString());
        return args;
    }

    /// <summary>ProcessStartInfo for running codex with the given arguments (handles codex.cmd shims).</summary>
    public static ProcessStartInfo StartInfo(string codexPath, IEnumerable<string> args)
    {
        ProcessStartInfo psi;
        if (codexPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || codexPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            psi = new ProcessStartInfo("cmd.exe");
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/s");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(codexPath);
        }
        else psi = new ProcessStartInfo(codexPath);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        return psi;
    }
}

/// <summary>Resumes a chat with "codex exec resume &lt;id&gt; &lt;message&gt;" in the background.</summary>
public sealed class CodexCliStrategy : IResumeStrategy
{
    public ResumeMethod Method => ResumeMethod.CodexCli;
    public string DisplayName => ResumeMethodInfo.DisplayName(Method);

    public async Task<ResumeResult> ResumeAsync(ResumeContext ctx, CancellationToken ct)
    {
        var s = ctx.Settings;
        var cli = CodexCliLocator.Locate(s, ctx.App, ctx.Log);
        if (cli == null) return ResumeResult.Fail("Codex CLI (codex.exe) not found; set its path in Settings > Resume");

        var args = new List<string> { "exec" };
        args.AddRange(CodexCliLocator.SplitArgs(s.CliExtraArguments));
        args.Add("resume");
        args.Add(ctx.Thread.ThreadId);
        args.Add(ctx.Message);

        var psi = CodexCliLocator.StartInfo(cli.Path, args);
        var cwd = CodexSessions.CleanPath(ctx.Thread.Cwd);
        if (!string.IsNullOrEmpty(cwd) && Directory.Exists(cwd)) psi.WorkingDirectory = cwd;

        var logFile = Path.Combine(ctx.Log.LogDirectory,
            $"cli-{ctx.Thread.ThreadId[..8]}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        var startedUtc = DateTime.UtcNow;
        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception ex)
        {
            return ResumeResult.Fail($"could not start {cli.Path}: {ex.Message}");
        }
        ctx.Log.Info($"Codex CLI ({cli.How}): started pid {proc.Id}; output → {Path.GetFileName(logFile)}");

        try { proc.StandardInput.Close(); } catch { }
        var output = new StringBuilder();
        var writer = new StreamWriter(logFile, append: true, Encoding.UTF8) { AutoFlush = true };
        writer.WriteLine($"# {cli.Path} {string.Join(' ', args.Take(args.Count - 1))} \"<message>\"");
        writer.WriteLine($"# cwd: {psi.WorkingDirectory}");
        object gate = new();
        void OnLine(string? line)
        {
            if (line == null) return;
            lock (gate)
            {
                writer.WriteLine(line);
                if (output.Length < 8000) output.AppendLine(line);
            }
        }
        proc.OutputDataReceived += (_, e) => OnLine(e.Data);
        proc.ErrorDataReceived += (_, e) => OnLine(e.Data);
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            try
            {
                lock (gate) { writer.WriteLine($"# exited with code {proc.ExitCode}"); writer.Dispose(); }
                ctx.Log.Info($"Codex CLI for {ctx.Thread} finished (exit code {proc.ExitCode}).");
            }
            catch { }
        };

        // Verify: a new turn must appear in the session file. Fail fast if the CLI exits with an error.
        var end = DateTime.UtcNow.AddSeconds(s.VerifyTimeoutSeconds);
        while (DateTime.UtcNow < end)
        {
            if (ctx.Sessions.HasTurnStartedSince(ctx.Thread.ThreadId, startedUtc))
                return ResumeResult.Ok($"CLI is continuing the chat in the background (pid {proc.Id})");
            if (proc.HasExited && proc.ExitCode != 0)
            {
                string tail;
                lock (gate) tail = LastLines(output.ToString(), 4);
                return ResumeResult.Fail(ResumeMethodInfo.ExplainError($"CLI exited with code {proc.ExitCode}: {tail}"));
            }
            await Task.Delay(2000, ct).ConfigureAwait(false);
        }
        if (proc.HasExited)
        {
            string tail;
            lock (gate) tail = LastLines(output.ToString(), 4);
            return ResumeResult.Fail(ResumeMethodInfo.ExplainError($"CLI exited (code {proc.ExitCode}) without starting a turn: {tail}"));
        }
        // Stop it so a fallback method doesn't run the work twice.
        try { proc.Kill(entireProcessTree: true); } catch { }
        return ResumeResult.Fail($"CLI started but no new turn appeared within {s.VerifyTimeoutSeconds}s (stopped it)");
    }

    private static string LastLines(string s, int n) =>
        string.Join(" / ", s.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).TakeLast(n));
}
