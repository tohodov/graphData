using System.Diagnostics;
using System.Text;

namespace GraphData.McpTray;

internal sealed class StatusForm : Form
{
    private readonly McpStatusReader _reader;
    private readonly Label _stateValue = CreateValueLabel();
    private readonly Label _pidValue = CreateValueLabel();
    private readonly Label _startedValue = CreateValueLabel();
    private readonly Label _heartbeatValue = CreateValueLabel();
    private readonly Label _storageValue = CreateValueLabel();
    private readonly Label _serverDirValue = CreateValueLabel();
    private readonly Label _statusDirValue = CreateValueLabel();
    private readonly Label _debuggerValue = CreateValueLabel();
    private readonly TextBox _messageValue = new()
    {
        BorderStyle = BorderStyle.FixedSingle,
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical
    };

    private McpStatusSnapshot? _snapshot;

    public StatusForm(McpStatusReader reader)
    {
        _reader = reader;

        Text = "graphData MCP status";
        Width = 680;
        Height = 430;
        MinimumSize = new Size(560, 360);
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(12)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var statusGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 9
        };
        statusGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        statusGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(statusGrid, 0, "State", _stateValue);
        AddRow(statusGrid, 1, "PID", _pidValue);
        AddRow(statusGrid, 2, "Started", _startedValue);
        AddRow(statusGrid, 3, "Last heartbeat", _heartbeatValue);
        AddRow(statusGrid, 4, "Storage", _storageValue);
        AddRow(statusGrid, 5, "Server dir", _serverDirValue);
        AddRow(statusGrid, 6, "Status dir", _statusDirValue);
        AddRow(statusGrid, 7, "Debugger", _debuggerValue);
        AddRow(statusGrid, 8, "Message", _messageValue);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };

        buttons.Controls.Add(CreateButton("Refresh", (_, _) => RefreshStatus()));
        buttons.Controls.Add(CreateButton("Copy", (_, _) => CopyStatus()));
        buttons.Controls.Add(CreateButton("Open storage", (_, _) => OpenPath(CurrentServer?.GraphStorageRoot)));
        buttons.Controls.Add(CreateButton("Open server dir", (_, _) => OpenPath(CurrentServer?.BaseDirectory)));

        root.Controls.Add(statusGrid, 0, 0);
        root.Controls.Add(buttons, 0, 1);
        Controls.Add(root);

        RefreshStatus();
    }

    private McpServerStatus? CurrentServer => _snapshot?.CurrentServer;

    public void RefreshStatus()
    {
        _snapshot = _reader.Read();
        var server = _snapshot.CurrentServer;

        _stateValue.Text = _snapshot.DisplayState;
        _pidValue.Text = server?.ProcessId.ToString() ?? "-";
        _startedValue.Text = FormatDate(server?.StartedAt);
        _heartbeatValue.Text = FormatDate(server?.LastHeartbeatAt);
        _storageValue.Text = server?.GraphStorageRoot ?? "-";
        _serverDirValue.Text = server?.BaseDirectory ?? "-";
        _statusDirValue.Text = _snapshot.StatusDirectory;
        _debuggerValue.Text = server?.DebuggerAttached == true ? "Attached" : "Not attached";
        _messageValue.Text = server?.Message ?? "No status has been written yet.";
    }

    private void CopyStatus()
    {
        var snapshot = _snapshot ?? _reader.Read();
        var builder = new StringBuilder();
        builder.AppendLine($"State: {snapshot.DisplayState}");

        foreach (var server in snapshot.Servers)
        {
            builder.AppendLine();
            builder.AppendLine($"PID: {server.ProcessId}");
            builder.AppendLine($"State: {server.State}");
            builder.AppendLine($"Started: {FormatDate(server.StartedAt)}");
            builder.AppendLine($"Last heartbeat: {FormatDate(server.LastHeartbeatAt)}");
            builder.AppendLine($"Storage: {server.GraphStorageRoot}");
            builder.AppendLine($"Server dir: {server.BaseDirectory}");
            builder.AppendLine($"Status dir: {server.StatusDirectory}");
            builder.AppendLine($"Debugger: {(server.DebuggerAttached ? "Attached" : "Not attached")}");
            builder.AppendLine($"Message: {server.Message}");
        }

        Clipboard.SetText(builder.ToString());
    }

    private static void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private static Button CreateButton(string text, EventHandler onClick)
    {
        var button = new Button
        {
            AutoSize = true,
            Text = text
        };
        button.Click += onClick;
        return button;
    }

    private static void AddRow(TableLayoutPanel grid, int row, string label, Control value)
    {
        grid.RowStyles.Add(row == 8
            ? new RowStyle(SizeType.Percent, 100)
            : new RowStyle(SizeType.AutoSize));

        grid.Controls.Add(new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
            Margin = new Padding(0, 5, 8, 5),
            Text = label
        }, 0, row);

        value.Margin = new Padding(0, 5, 0, 5);
        grid.Controls.Add(value, 1, row);
    }

    private static Label CreateValueLabel()
    {
        return new Label
        {
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Text = "-"
        };
    }

    private static string FormatDate(DateTimeOffset? date)
    {
        return date?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "-";
    }
}
