using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ChatGptWatchdog.Core;
using ChatGptWatchdog.Core.Resume;

namespace ChatGptWatchdog.App.Forms;

/// <summary>Lets you try a resume method on a chat of your choice, without a crash.</summary>
internal sealed class TestResumeForm : Form
{
    private readonly Services _svc;
    private readonly ListView _chats = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
    private readonly ComboBox _method = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
    private readonly TextBox _message = new() { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, Height = 70 };
    private readonly Button _run = new() { Text = "Run test", AutoSize = true };
    private readonly Label _result = new() { AutoSize = true, MaximumSize = new Size(700, 0) };
    private CancellationTokenSource? _cts;

    public TestResumeForm(Services svc)
    {
        _svc = svc;
        Text = "Test resume — ChatGPT APP watchdog";
        Size = new Size(860, 560);
        MinimumSize = new Size(640, 420);
        StartPosition = FormStartPosition.Manual;
        Font = new Font("Segoe UI", 9f);
        ShowInTaskbar = false;

        _chats.Columns.Add("Last activity", 110);
        _chats.Columns.Add("State", 60);
        _chats.Columns.Add("Chat", 380);
        _chats.Columns.Add("Auto-resume", 200);

        _method.Items.Add("Configured order (" + string.Join(" → ", svc.Settings.Resume.Methods) + ")");
        foreach (var m in Enum.GetValues<ResumeMethod>()) _method.Items.Add(new MethodItem(m));
        _method.SelectedIndex = 0;
        _message.Text = "Watchdog test: please reply with just the word OK.";

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 8, 8, 0) };
        top.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(800, 0),
            Text = "Pick a chat (ideally a throw-away test chat), a method and a message, then click Run test. " +
                   "The result is verified the same way as after a real crash: a new turn must appear in the chat's session file.",
        });
        var refresh = new Button { Text = "Refresh list", AutoSize = true };
        refresh.Click += (_, _) => LoadChats();
        top.Controls.Add(refresh);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 1, Padding = new Padding(8) };
        var methodRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        methodRow.Controls.Add(new Label { Text = "Method:", AutoSize = true, Margin = new Padding(3, 7, 3, 3) });
        methodRow.Controls.Add(_method);
        bottom.Controls.Add(methodRow);
        bottom.Controls.Add(new Label { Text = "Message:", AutoSize = true });
        bottom.Controls.Add(_message);
        var runRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        runRow.Controls.Add(_run);
        runRow.Controls.Add(_result);
        bottom.Controls.Add(runRow);

        var mid = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        mid.Controls.Add(_chats);
        Controls.Add(mid);
        Controls.Add(bottom);
        Controls.Add(top);

        _run.Click += async (_, _) => await RunAsync();
        Load += (_, _) =>
        {
            // Center on the main window (CenterParent only works for modal dialogs).
            var area = Owner != null && Owner.Visible ? Owner.Bounds : Screen.PrimaryScreen!.WorkingArea;
            Location = new System.Drawing.Point(area.Left + (area.Width - Width) / 2, Math.Max(area.Top, area.Top + (area.Height - Height) / 2));
        };
        Load += (_, _) => LoadChats();
        FormClosed += (_, _) => _cts?.Cancel();
    }

    private void LoadChats()
    {
        _chats.Items.Clear();
        List<CodexThread> list;
        try { list = _svc.Sessions.ScanThreads(DateTime.UtcNow.AddDays(-3)); }
        catch (Exception ex) { _result.Text = "Could not read sessions: " + ex.Message; return; }
        foreach (var t in list)
        {
            var it = new ListViewItem(new[]
            {
                t.LastWriteUtc.ToLocalTime().ToString("dd/MM HH:mm"),
                t.HasOpenTurn ? "busy" : "idle",
                t.DisplayTitle,
                t.ExcludedReason == null ? "yes" : "skipped: " + t.ExcludedReason,
            }) { Tag = t };
            if (t.ExcludedReason != null) it.ForeColor = Color.Gray;
            _chats.Items.Add(it);
        }
        if (_chats.Items.Count > 0) _chats.Items[0].Selected = true;
        _result.Text = $"{list.Count} chats active in the last 3 days.";
    }

    private async Task RunAsync()
    {
        if (_chats.SelectedItems.Count == 0 || _chats.SelectedItems[0].Tag is not CodexThread thread)
        {
            _result.Text = "Select a chat first.";
            return;
        }
        var chain = _method.SelectedItem is MethodItem mi ? new List<ResumeMethod> { mi.Method } : _svc.Settings.Resume.Methods.ToList();
        var msg = _message.Text.Trim();
        if (msg.Length == 0) { _result.Text = "Enter a message."; return; }

        _run.Enabled = false;
        _result.Text = "Running… (see the main log)";
        _cts = new CancellationTokenSource();
        try
        {
            var ok = await Task.Run(() => _svc.Resume.ResumeOneAsync(thread, msg, chain, _svc.Settings.Resume, _cts.Token));
            _result.Text = ok ? "Success — see the main log for details." : "Failed — see the main log for details.";
        }
        catch (OperationCanceledException) { _result.Text = "Cancelled."; }
        catch (Exception ex) { _result.Text = "Error: " + ex.Message; }
        finally { if (!IsDisposed) _run.Enabled = true; }
    }

    private sealed record MethodItem(ResumeMethod Method)
    {
        public override string ToString() => ResumeMethodInfo.DisplayName(Method);
    }
}
