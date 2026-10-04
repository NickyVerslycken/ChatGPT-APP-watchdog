using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace ChatGptWatchdog.Core.Resume;

/// <summary>
/// Resumes a chat through the Codex app-server JSON-RPC protocol:
/// initialize → initialized → thread/resume → turn/start.
/// Either spawns "codex app-server" (stdio, JSONL) or connects to a configured ws:// endpoint.
/// The connection stays open in the background until the turn completes.
/// </summary>
public sealed class AppServerStrategy : IResumeStrategy
{
    public ResumeMethod Method => ResumeMethod.AppServer;
    public string DisplayName => ResumeMethodInfo.DisplayName(Method);

    public async Task<ResumeResult> ResumeAsync(ResumeContext ctx, CancellationToken ct)
    {
        var s = ctx.Settings;
        IRpcTransport transport;
        try
        {
            transport = string.IsNullOrWhiteSpace(s.AppServerEndpoint)
                ? StdioTransport.Start(ctx)
                : await WebSocketTransport.ConnectAsync(s.AppServerEndpoint.Trim(), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ResumeResult.Fail("could not start/connect to app-server: " + ex.Message);
        }

        var rpc = new RpcClient(transport, ctx);
        rpc.Start();
        bool keepAlive = false;
        try
        {
            var startedUtc = DateTime.UtcNow;
            var timeout = TimeSpan.FromSeconds(Math.Max(20, s.VerifyTimeoutSeconds));

            var init = await rpc.CallAsync("initialize", new JsonObject
            {
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "chatgpt_app_watchdog",
                    ["title"] = "ChatGPT APP watchdog",
                    ["version"] = (System.Reflection.Assembly.GetEntryAssembly() ?? typeof(AppServerStrategy).Assembly).GetName().Version?.ToString() ?? "0",
                },
            }, timeout, ct).ConfigureAwait(false);
            if (init.Error != null) return ResumeResult.Fail("initialize failed: " + init.Error);
            await rpc.NotifyAsync("initialized", new JsonObject()).ConfigureAwait(false);

            var resume = await rpc.CallAsync("thread/resume", new JsonObject { ["threadId"] = ctx.Thread.ThreadId }, timeout, ct).ConfigureAwait(false);
            if (resume.Error != null) return ResumeResult.Fail(ResumeMethodInfo.ExplainError("thread/resume failed: " + resume.Error));

            var turn = await rpc.CallAsync("turn/start", new JsonObject
            {
                ["threadId"] = ctx.Thread.ThreadId,
                ["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = ctx.Message }),
            }, timeout, ct).ConfigureAwait(false);
            if (turn.Error != null) return ResumeResult.Fail(ResumeMethodInfo.ExplainError("turn/start failed: " + turn.Error));

            var turnId = turn.Result?["turn"]?["id"]?.GetValue<string>();
            rpc.WatchTurn(turnId);

            // Double-check in the session file (short wait; the response already says the turn started).
            var verified = await ctx.Sessions.WaitForTurnStartedAsync(ctx.Thread.ThreadId, startedUtc,
                TimeSpan.FromSeconds(Math.Min(30, s.VerifyTimeoutSeconds)), ct).ConfigureAwait(false);
            keepAlive = true;
            _ = rpc.RunUntilTurnCompletesAsync(TimeSpan.FromHours(12)); // keeps the connection/process alive in the background
            return ResumeResult.Ok($"turn {turnId ?? "?"} started via app-server" + (verified ? "" : " (not yet visible in the session file)"));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return ResumeResult.Fail("app-server error: " + ex.Message);
        }
        finally
        {
            if (!keepAlive) rpc.Dispose();
        }
    }

    // ------------------------------------------------------------------ transports

    private interface IRpcTransport : IDisposable
    {
        Task SendAsync(string json);
        Task<string?> ReceiveAsync(CancellationToken ct);
        string Describe { get; }
    }

    private sealed class StdioTransport : IRpcTransport
    {
        private readonly Process _proc;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        public string Describe { get; }

        private StdioTransport(Process p, string describe) { _proc = p; Describe = describe; }

        public static StdioTransport Start(ResumeContext ctx)
        {
            var cli = CodexCliLocator.Locate(ctx.Settings, ctx.App, ctx.Log)
                      ?? throw new InvalidOperationException("Codex CLI (codex.exe) not found; set its path in Settings > Resume");
            var psi = CodexCliLocator.StartInfo(cli.Path, new[] { "app-server" });
            psi.StandardInputEncoding = new UTF8Encoding(false);
            var cwd = CodexSessions.CleanPath(ctx.Thread.Cwd);
            if (!string.IsNullOrEmpty(cwd) && Directory.Exists(cwd)) psi.WorkingDirectory = cwd;
            var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start codex app-server");
            var errLog = Path.Combine(ctx.Log.LogDirectory, $"appserver-{ctx.Thread.ThreadId[..8]}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) try { File.AppendAllText(errLog, e.Data + Environment.NewLine); } catch { } };
            p.BeginErrorReadLine();
            ctx.Log.Info($"App-server: started '{cli.Path} app-server' ({cli.How}), pid {p.Id}");
            return new StdioTransport(p, $"stdio pid {p.Id}");
        }

        public async Task SendAsync(string json)
        {
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _proc.StandardInput.WriteAsync(json + "\n").ConfigureAwait(false);
                await _proc.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            finally { _sendLock.Release(); }
        }

        public async Task<string?> ReceiveAsync(CancellationToken ct) =>
            await _proc.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);

        public void Dispose()
        {
            try { _proc.StandardInput.Close(); } catch { }
            try { if (!_proc.WaitForExit(3000)) _proc.Kill(entireProcessTree: true); } catch { }
            _proc.Dispose();
        }
    }

    private sealed class WebSocketTransport : IRpcTransport
    {
        private readonly ClientWebSocket _ws;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        public string Describe { get; }

        private WebSocketTransport(ClientWebSocket ws, string url) { _ws = ws; Describe = url; }

        public static async Task<WebSocketTransport> ConnectAsync(string url, CancellationToken ct)
        {
            var ws = new ClientWebSocket();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            await ws.ConnectAsync(new Uri(url), cts.Token).ConfigureAwait(false);
            return new WebSocketTransport(ws, url);
        }

        public async Task SendAsync(string json)
        {
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
            }
            finally { _sendLock.Release(); }
        }

        public async Task<string?> ReceiveAsync(CancellationToken ct)
        {
            var buffer = new byte[64 * 1024];
            using var ms = new MemoryStream();
            while (true)
            {
                var r = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                ms.Write(buffer, 0, r.Count);
                if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        public void Dispose()
        {
            try { _ws.Abort(); } catch { }
            _ws.Dispose();
        }
    }

    // ------------------------------------------------------------------ JSON-RPC client

    private sealed record RpcResponse(JsonNode? Result, string? Error);

    private sealed class RpcClient : IDisposable
    {
        private readonly IRpcTransport _t;
        private readonly ResumeContext _ctx;
        private readonly Dictionary<long, TaskCompletionSource<RpcResponse>> _pending = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource<string> _turnDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _nextId;
        private string? _watchTurnId;
        private Task? _reader;

        public RpcClient(IRpcTransport t, ResumeContext ctx) { _t = t; _ctx = ctx; }

        public void Start() => _reader = Task.Run(ReadLoopAsync);

        public void WatchTurn(string? turnId) => _watchTurnId = turnId;

        public async Task<RpcResponse> CallAsync(string method, JsonObject @params, TimeSpan timeout, CancellationToken ct)
        {
            var id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<RpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pending) _pending[id] = tcs;
            var msg = new JsonObject { ["method"] = method, ["id"] = id, ["params"] = @params };
            await _t.SendAsync(msg.ToJsonString()).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                return await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new RpcResponse(null, $"no response to {method} within {timeout.TotalSeconds:0}s");
            }
        }

        public Task NotifyAsync(string method, JsonObject @params) =>
            _t.SendAsync(new JsonObject { ["method"] = method, ["params"] = @params }.ToJsonString());

        private async Task ReadLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var line = await _t.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                    if (line == null) break;
                    if (line.Length == 0) continue;
                    JsonNode? node;
                    try { node = JsonNode.Parse(line); } catch { continue; }
                    if (node is not JsonObject obj) continue;

                    var hasId = obj.TryGetPropertyValue("id", out var idNode) && idNode != null;
                    var method = obj["method"]?.GetValue<string>();

                    if (hasId && method == null)
                    {
                        // Response to one of our calls.
                        long id;
                        try { id = idNode!.GetValue<long>(); } catch { continue; }
                        TaskCompletionSource<RpcResponse>? tcs;
                        lock (_pending) { _pending.Remove(id, out tcs); }
                        var err = obj["error"];
                        tcs?.TrySetResult(new RpcResponse(obj["result"], err == null ? null : (err["message"]?.ToString() ?? err.ToJsonString())));
                    }
                    else if (hasId && method != null)
                    {
                        await HandleServerRequestAsync(idNode!, method, obj).ConfigureAwait(false);
                    }
                    else if (method != null)
                    {
                        HandleNotification(method, obj["params"]);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _ctx.Log.Debug("App-server connection ended: " + ex.Message); }
            finally
            {
                lock (_pending)
                {
                    foreach (var p in _pending.Values) p.TrySetResult(new RpcResponse(null, "connection closed"));
                    _pending.Clear();
                }
                _turnDone.TrySetResult("connection closed");
            }
        }

        private async Task HandleServerRequestAsync(JsonNode id, string method, JsonObject obj)
        {
            JsonObject reply;
            if (method.Contains("requestApproval", StringComparison.OrdinalIgnoreCase) ||
                method.Contains("approval", StringComparison.OrdinalIgnoreCase))
            {
                var decision = _ctx.Settings.AppServerAutoApprove ? "accept" : "decline";
                _ctx.Log.Info($"App-server asked for approval ({method}) → {decision}");
                reply = new JsonObject { ["id"] = id.DeepClone(), ["result"] = new JsonObject { ["decision"] = decision } };
            }
            else
            {
                _ctx.Log.Debug($"App-server request '{method}' not supported by the watchdog");
                reply = new JsonObject
                {
                    ["id"] = id.DeepClone(),
                    ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "not supported by ChatGPT APP watchdog" },
                };
            }
            try { await _t.SendAsync(reply.ToJsonString()).ConfigureAwait(false); } catch { }
        }

        private void HandleNotification(string method, JsonNode? p)
        {
            if (method == "turn/completed")
            {
                var id = p?["turn"]?["id"]?.ToString();
                var status = p?["turn"]?["status"]?.ToString() ?? "?";
                if (_watchTurnId == null || id == _watchTurnId) _turnDone.TrySetResult(status);
            }
            else if (method == "error")
            {
                _ctx.Log.Warn("App-server error notification: " + (p?.ToJsonString() ?? ""));
            }
        }

        public async Task RunUntilTurnCompletesAsync(TimeSpan max)
        {
            try
            {
                var status = await _turnDone.Task.WaitAsync(max).ConfigureAwait(false);
                _ctx.Log.Info($"App-server: resumed turn for {_ctx.Thread} ended ({status}).");
            }
            catch (TimeoutException)
            {
                _ctx.Log.Warn($"App-server: turn for {_ctx.Thread} still running after {max.TotalHours:0}h; disconnecting.");
            }
            finally { Dispose(); }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _t.Dispose(); } catch { }
        }
    }
}
