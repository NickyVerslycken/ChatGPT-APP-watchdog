using ChatGptWatchdog.Core;

// Usage: dotnet run -- [path to .codex] [hours]
// Lists recent chats and whether a turn is open, plus a few parser unit checks.
int failures = 0;
void Check(bool cond, string what)
{
    Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {what}");
    if (!cond) failures++;
}

// --- unit checks with synthetic rollout files ---
var tmp = Path.Combine(Path.GetTempPath(), "cgw-selftest-" + Guid.NewGuid().ToString("N")[..8]);
var day = Path.Combine(tmp, "sessions", "2026", "10", "04");
Directory.CreateDirectory(day);
string Meta(string id, string source = "\"vscode\"") =>
    $"{{\"timestamp\":\"2026-10-04T10:00:00.000Z\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{id}\",\"cwd\":\"C:\\\\x\",\"source\":{source}}}}}";
string Ev(string type, string turn, string time) =>
    $"{{\"timestamp\":\"{time}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"{type}\",\"turn_id\":\"{turn}\"}}}}";
const string A = "11111111-1111-7111-8111-111111111111";
const string B = "22222222-2222-7222-8222-222222222222";
const string C = "33333333-3333-7333-8333-333333333333";
const string D = "44444444-4444-7444-8444-444444444444";
File.WriteAllLines(Path.Combine(day, $"rollout-2026-10-04T10-00-00-{A}.jsonl"), new[] {
    Meta(A), Ev("task_started","t1","2026-10-04T10:00:01.000Z"), Ev("task_complete","t1","2026-10-04T10:01:00.000Z"),
    Ev("task_started","t2","2026-10-04T10:02:00.000Z"), "{\"timestamp\":\"2026-10-04T10:02:05.000Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"text\":\"mentions \\\"task_complete\\\" in text\"}}" });
File.WriteAllLines(Path.Combine(day, $"rollout-2026-10-04T10-00-00-{B}.jsonl"), new[] {
    Meta(B), Ev("task_started","t1","2026-10-04T10:00:01.000Z"), Ev("task_complete","t1","2026-10-04T10:01:00.000Z") });
File.WriteAllLines(Path.Combine(day, $"rollout-2026-10-04T10-00-00-{C}.jsonl"), new[] {
    Meta(C, "{\"subagent\":{\"thread_spawn\":{\"parent_thread_id\":\"x\"}}}"), Ev("task_started","t1","2026-10-04T10:00:01.000Z") });
File.WriteAllLines(Path.Combine(day, $"rollout-2026-10-04T10-00-00-{D}.jsonl"), new[] {
    Meta(D), Ev("task_started","t1","2026-10-04T10:00:01.000Z"), Ev("turn_aborted","t1","2026-10-04T10:00:30.000Z") });
// E: busy, but Windows reports an old file time (the app keeps the file open) -> found via the lock file
const string E = "55555555-5555-7555-8555-555555555555";
var now = DateTime.UtcNow;
var eFile = Path.Combine(day, $"rollout-2026-10-04T10-00-00-{E}.jsonl");
File.WriteAllLines(eFile, new[] { Meta(E), Ev("task_started", "t1", now.AddMinutes(-2).ToString("yyyy-MM-ddTHH:mm:ss.fffZ")) });
File.SetLastWriteTimeUtc(eFile, now.AddDays(-3));
Directory.CreateDirectory(Path.Combine(tmp, "thread-writer-locks"));
File.WriteAllText(Path.Combine(tmp, "thread-writer-locks", E + ".lock"), "");
Directory.CreateDirectory(Path.Combine(tmp, "automations", "x"));
File.WriteAllText(Path.Combine(tmp, "automations", "x", "automation.toml"), $"id = \"x\"\nkind = \"heartbeat\"\nstatus = \"ACTIVE\"\ntarget_thread_id = \"{B}\"\n");
File.WriteAllText(Path.Combine(tmp, "session_index.jsonl"), $"{{\"id\":\"{A}\",\"thread_name\":\"Old\"}}\n{{\"id\":\"{A}\",\"thread_name\":\"Chat A\"}}\n");

var settings = new WatchdogSettings { CodexHome = tmp };
var sessions = new CodexSessions(() => settings);
var threads = sessions.ScanThreads(DateTime.UtcNow.AddHours(-1)).ToDictionary(t => t.ThreadId);
Check(threads.Count == 5, "finds 5 synthetic chats");
Check(threads.ContainsKey(E) && threads[E].HasOpenTurn && threads[E].LastWriteUtc > now.AddMinutes(-3), "E: found via lock file despite stale file time; activity from content");
Check(sessions.HasTurnStartedSince(E, now.AddMinutes(-5)), "E: turn detection ignores stale file time");
Check(threads[A].HasOpenTurn && threads[A].OpenTurnId == "t2", "A: open turn t2 (text mentioning task_complete ignored)");
Check(threads[A].Title == "Chat A", "A: title from session_index (last wins)");
Check(!threads[B].HasOpenTurn && threads[B].ExcludedReason != null, "B: finished and excluded as automation target");
Check(threads[C].HasOpenTurn && threads[C].IsSubAgent && threads[C].ExcludedReason == "sub-agent thread", "C: sub-agent excluded");
Check(!threads[D].HasOpenTurn, "D: aborted turn is not open");
Check(sessions.HasTurnStartedSince(A, new DateTime(2026, 10, 4, 10, 1, 30, DateTimeKind.Utc)), "A: turn started after 10:01:30");
Check(!sessions.HasTurnStartedSince(B, new DateTime(2026, 10, 4, 10, 1, 30, DateTimeKind.Utc)), "B: no turn after 10:01:30");
var interrupted = sessions.FindInterruptedThreads(DateTime.UtcNow, TimeSpan.FromHours(1));
Check(interrupted.Select(t => t.ThreadId).OrderBy(x => x).SequenceEqual(new[] { A, C, E }), "interrupted = A, C, E (C excluded later)");
Check(TargetApp.PackageFamilyFromPath(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.715.4045.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe") == "OpenAI.Codex_2p2nqsd0c76g0", "package family from path");
var exe = @"C:\Program Files\WindowsApps\OpenAI.Codex_1_x64__p\app\ChatGPT.exe";
var mainP = new ProcInfo(10, 1, "ChatGPT.exe", exe, DateTime.Now.AddMinutes(-5), $"\"{exe}\"");
var gpu = new ProcInfo(11, 10, "ChatGPT.exe", exe, DateTime.Now.AddMinutes(-4), $"\"{exe}\" --type=gpu-process");
var crashpad = new ProcInfo(12, 99, "ChatGPT.exe", exe, DateTime.Now.AddMinutes(-6), $"\"{exe}\" --type=crashpad-handler");
Check(TargetApp.FindMainProcess(new() { crashpad, gpu, mainP })?.Pid == 10, "main process = the one without --type=");
Check(TargetApp.FindMainProcess(new() { crashpad, gpu }) == null, "orphaned helpers after a crash do not count as running");
Check(ChatGptWatchdog.Core.Resume.CodexCliLocator.SplitArgs("--a \"b c\" d").SequenceEqual(new[] { "--a", "b c", "d" }), "argument splitting");
Check(CodexSessions.CleanPath(@"\\?\C:\DEV\x") == @"C:\DEV\x", "strip \\\\?\\ prefix");
var cfg = new WatchdogSettings(); cfg.Resume.Methods = new() { ResumeMethod.CodexCli, ResumeMethod.CodexCli }; cfg.CheckIntervalSeconds = 1; cfg.Normalize();
Check(cfg.Resume.Methods.Count == 1 && cfg.CheckIntervalSeconds == 5, "settings normalize");
Directory.Delete(tmp, true);

// --- optional: real .codex folder ---
if (args.Length > 0)
{
    var hours = args.Length > 1 ? double.Parse(args[1]) : 24;
    var real = new WatchdogSettings { CodexHome = args[0] };
    var rs = new CodexSessions(() => real);
    Console.WriteLine($"\nChats active in the last {hours}h in {args[0]}:");
    foreach (var t in rs.ScanThreads(DateTime.UtcNow.AddHours(-hours)))
        Console.WriteLine($"  {t.LastWriteUtc.ToLocalTime():MM-dd HH:mm} {(t.HasOpenTurn ? "BUSY" : "idle")} {t.ThreadId} '{t.DisplayTitle}' last={t.LastTurnEvent} {(t.ExcludedReason != null ? "[skip: " + t.ExcludedReason + "]" : "")}");
}

Console.WriteLine(failures == 0 ? "\nAll checks passed." : $"\n{failures} check(s) FAILED.");
return failures == 0 ? 0 : 1;
