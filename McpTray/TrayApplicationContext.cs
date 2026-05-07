namespace GraphData.McpTray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly McpStatusReader _reader;
    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _timer;
    private StatusForm? _statusForm;

    public TrayApplicationContext(string statusDirectory)
    {
        _reader = new McpStatusReader(statusDirectory);
        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = CreateContextMenu(),
            Icon = SystemIcons.Information,
            Text = "graphData MCP status",
            Visible = true
        };
        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                ShowStatusWindow();
            }
        };
        _notifyIcon.DoubleClick += (_, _) => ShowStatusWindow();

        _timer = new System.Windows.Forms.Timer { Interval = 2000 };
        _timer.Tick += (_, _) => RefreshTray();
        _timer.Start();

        RefreshTray();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _statusForm?.Dispose();
        }

        base.Dispose(disposing);
    }

    private ContextMenuStrip CreateContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Status", null, (_, _) => ShowStatusWindow());
        menu.Items.Add("Refresh", null, (_, _) => RefreshStatusWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit tray", null, (_, _) => ExitThread());
        return menu;
    }

    private void ShowStatusWindow()
    {
        if (_statusForm is null || _statusForm.IsDisposed)
        {
            _statusForm = new StatusForm(_reader);
        }

        _statusForm.RefreshStatus();
        _statusForm.Show();
        _statusForm.WindowState = FormWindowState.Normal;
        _statusForm.Activate();
    }

    private void RefreshStatusWindow()
    {
        RefreshTray();
        _statusForm?.RefreshStatus();
    }

    private void RefreshTray()
    {
        var snapshot = _reader.Read();
        _notifyIcon.Icon = snapshot.HasRunningServer ? SystemIcons.Information : SystemIcons.Warning;
        _notifyIcon.Text = Truncate($"graphData MCP: {snapshot.DisplayState}", 63);
        _statusForm?.RefreshStatus();
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
