using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ChatGptWatchdog.Core;
using ChatGptWatchdog.Core.Resume;

namespace ChatGptWatchdog.App.Forms;

/// <summary>Settings dialog: general options in a property grid, resume options on their own tab.</summary>
internal sealed class SettingsForm : Form
{
    public WatchdogSettings Result { get; }

    private readonly CheckBox _resumeEnabled = new() { Text = "Resume interrupted chats after a crash", AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
    private readonly CheckedListBox _methods = new() { CheckOnClick = true, IntegralHeight = false, Height = 92, Dock = DockStyle.Fill };
    private readonly Label _methodInfo = new() { AutoSize = false, Dock = DockStyle.Fill, Height = 54 };
    private readonly TextBox _message = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 80, Dock = DockStyle.Fill, AcceptsReturn = true };

    public SettingsForm(WatchdogSettings settings, bool autoStartActuallyEnabled)
    {
        Result = settings;
        Result.StartWithWindows = autoStartActuallyEnabled || Result.StartWithWindows;

        Text = "Settings — ChatGPT APP watchdog";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(760, 800);
        MinimumSize = new Size(560, 520);
        Font = new Font("Segoe UI", 9f);
        ShowInTaskbar = false;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var general = new TabPage("General") { Padding = new Padding(6) };
        general.Controls.Add(new PropertyGrid
        {
            Dock = DockStyle.Fill, SelectedObject = Result, PropertySort = PropertySort.Categorized, ToolbarVisible = false, HelpVisible = true,
        });
        tabs.TabPages.Add(general);
        tabs.TabPages.Add(BuildResumeTab());

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;
        ok.Click += (_, _) => Collect();

        Controls.Add(tabs);
        Controls.Add(buttons);
    }

    private TabPage BuildResumeTab()
    {
        var page = new TabPage("Resume chats") { Padding = new Padding(10) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        int row = 0;
        void AddRow(Control c, SizeType st = SizeType.AutoSize, float h = 0, int span = 2)
        {
            layout.RowStyles.Add(new RowStyle(st, h));
            layout.Controls.Add(c, 0, row);
            layout.SetColumnSpan(c, span);
            row++;
        }

        _resumeEnabled.Checked = Result.Resume.Enabled;
        AddRow(_resumeEnabled);
        AddRow(new Label
        {
            AutoSize = true, MaximumSize = new Size(640, 0), Margin = new Padding(3, 2, 3, 8),
            Text = "When ChatGPT stops while a chat is busy (a turn was started but never finished), the watchdog restarts the app and then " +
                   "tries the methods below in order. The first one that works wins; the others are fallbacks. Chats driven by automations are skipped.",
        });
        AddRow(new Label { Text = "Methods (checked = used; order = fallback order):", AutoSize = true });

        // Ordered list: configured methods first (checked), then the rest (unchecked).
        var ordered = Result.Resume.Methods.Concat(Enum.GetValues<ResumeMethod>().Except(Result.Resume.Methods)).ToList();
        foreach (var m in ordered) _methods.Items.Add(new MethodItem(m), Result.Resume.Methods.Contains(m));
        _methods.SelectedIndexChanged += (_, _) => ShowMethodInfo();

        var upDown = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true };
        var up = new Button { Text = "▲ Up", AutoSize = true };
        var down = new Button { Text = "▼ Down", AutoSize = true };
        up.Click += (_, _) => MoveMethod(-1);
        down.Click += (_, _) => MoveMethod(1);
        upDown.Controls.Add(up);
        upDown.Controls.Add(down);

        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        layout.Controls.Add(_methods, 0, row);
        layout.Controls.Add(upDown, 1, row);
        row++;
        AddRow(_methodInfo, SizeType.Absolute, 58);

        var msgHeader = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
        msgHeader.Controls.Add(new Label { Text = "Message sent to the interrupted chat:", AutoSize = true, Margin = new Padding(3, 7, 3, 3) });
        var reset = new Button { Text = "Default text", AutoSize = true };
        reset.Click += (_, _) => _message.Text = ResumeSettings.DefaultMessage;
        msgHeader.Controls.Add(reset);
        AddRow(msgHeader);
        _message.Text = Result.Resume.Message;
        AddRow(_message, SizeType.Absolute, 86);

        AddRow(new Label { Text = "Advanced:", AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
        var grid = new PropertyGrid
        {
            Dock = DockStyle.Fill, SelectedObject = Result.Resume, PropertySort = PropertySort.Categorized, ToolbarVisible = false, HelpVisible = true,
        };
        AddRow(grid, SizeType.Percent, 100);

        page.Controls.Add(layout);
        if (_methods.Items.Count > 0) _methods.SelectedIndex = 0;
        return page;
    }

    private void MoveMethod(int delta)
    {
        var i = _methods.SelectedIndex;
        var j = i + delta;
        if (i < 0 || j < 0 || j >= _methods.Items.Count) return;
        var item = _methods.Items[i];
        var check = _methods.GetItemChecked(i);
        _methods.Items.RemoveAt(i);
        _methods.Items.Insert(j, item);
        _methods.SetItemChecked(j, check);
        _methods.SelectedIndex = j;
    }

    private void ShowMethodInfo()
    {
        if (_methods.SelectedItem is MethodItem mi) _methodInfo.Text = ResumeMethodInfo.Description(mi.Method);
    }

    private void Collect()
    {
        Result.Resume.Enabled = _resumeEnabled.Checked;
        Result.Resume.Methods = Enumerable.Range(0, _methods.Items.Count)
            .Where(_methods.GetItemChecked)
            .Select(i => ((MethodItem)_methods.Items[i]).Method)
            .ToList();
        Result.Resume.Message = _message.Text.Trim();
        Result.Normalize();
    }

    private sealed record MethodItem(ResumeMethod Method)
    {
        public override string ToString() => ResumeMethodInfo.DisplayName(Method);
    }
}
