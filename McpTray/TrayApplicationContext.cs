namespace GraphData.McpTray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly Control _dispatcher = new();
    private readonly McpLogCollector _collector = new();
    private readonly NamedPipeLogServer _server;
    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _timer;
    private StatusForm? _statusForm;

    public TrayApplicationContext(string pipeName)
    {
        _dispatcher.CreateControl();
        _ = _dispatcher.Handle;
        _server = new NamedPipeLogServer(pipeName, OnPipeMessage);
        _server.Start();

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = CreateContextMenu(),
            Icon = SystemIcons.Information,
            Text = "graphData MCP logs",
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
        _timer.Tick += (_, _) =>
        {
            RefreshTray();
            RefreshStatusForm();
        };
        _timer.Start();

        RefreshTray();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _server.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _statusForm?.Dispose();
            _dispatcher.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnPipeMessage(McpTrayMessage message)
    {
        if (_dispatcher.IsDisposed || !_dispatcher.IsHandleCreated)
        {
            return;
        }

        try
        {
            _dispatcher.BeginInvoke((MethodInvoker)(() =>
            {
                if (_dispatcher.IsDisposed)
                {
                    return;
                }

                _collector.Apply(message);
                RefreshTray();
            }));
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private ContextMenuStrip CreateContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Logs", null, (_, _) => ShowStatusWindow());
        menu.Items.Add("Refresh", null, (_, _) => RefreshTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit tray", null, (_, _) => ExitThread());
        return menu;
    }

    private void ShowStatusWindow()
    {
        if (_statusForm is null || _statusForm.IsDisposed)
        {
            var form = new StatusForm(_collector);
            form.FormClosed += (_, _) =>
            {
                if (ReferenceEquals(_statusForm, form))
                {
                    _statusForm = null;
                }
            };
            _statusForm = form;
        }

        _statusForm.RefreshData();
        _statusForm.Show();
        _statusForm.WindowState = FormWindowState.Normal;
        _statusForm.Activate();
    }

    private void RefreshStatusForm()
    {
        var form = _statusForm;
        if (form is null)
        {
            return;
        }

        if (form.IsDisposed || form.Disposing)
        {
            _statusForm = null;
            return;
        }

        form.RefreshData();
    }

    private void RefreshTray()
    {
        _notifyIcon.Icon = _collector.RunningCount > 0 ? SystemIcons.Information : SystemIcons.Warning;
        _notifyIcon.Text = Truncate($"graphData MCP: {_collector.RunningCount} running, {_collector.Instances.Count} total", 63);
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
