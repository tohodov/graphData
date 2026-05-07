using System.Diagnostics;
using System.Text;

namespace GraphData.McpTray;

internal sealed class StatusForm : Form
{
    private readonly McpLogCollector _collector;
    private readonly ListBox _instances = new()
    {
        Dock = DockStyle.Fill,
        HorizontalScrollbar = true
    };

    private readonly Label _summary = new()
    {
        AutoEllipsis = true,
        Dock = DockStyle.Fill
    };

    private readonly Label _details = new()
    {
        AutoEllipsis = true,
        Dock = DockStyle.Fill
    };

    private readonly TextBox _log = new()
    {
        BorderStyle = BorderStyle.FixedSingle,
        Dock = DockStyle.Fill,
        Font = new Font(FontFamily.GenericMonospace, 9),
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false
    };

    public StatusForm(McpLogCollector collector)
    {
        _collector = collector;

        Text = "graphData MCP logs";
        Width = 980;
        Height = 620;
        MinimumSize = new Size(720, 440);
        StartPosition = FormStartPosition.CenterScreen;

        _instances.SelectedIndexChanged += (_, _) => RefreshSelectedLog();

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            SplitterDistance = 280
        };

        split.Panel1.Controls.Add(_instances);
        split.Panel2.Controls.Add(CreateRightPanel());

        Controls.Add(split);
        RefreshData();
    }

    public void RefreshData()
    {
        var selectedId = (_instances.SelectedItem as InstanceListItem)?.InstanceId;
        _instances.BeginUpdate();
        _instances.Items.Clear();

        foreach (var instance in _collector.Instances)
        {
            _instances.Items.Add(new InstanceListItem(instance));
        }

        if (!string.IsNullOrWhiteSpace(selectedId))
        {
            for (var index = 0; index < _instances.Items.Count; index++)
            {
                if ((_instances.Items[index] as InstanceListItem)?.InstanceId == selectedId)
                {
                    _instances.SelectedIndex = index;
                    break;
                }
            }
        }

        if (_instances.SelectedIndex < 0 && _instances.Items.Count > 0)
        {
            _instances.SelectedIndex = 0;
        }

        _instances.EndUpdate();
        _summary.Text = $"{_collector.RunningCount} running, {_collector.Instances.Count} total";
        RefreshSelectedLog();
    }

    private Control CreateRightPanel()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(8)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft
        };

        buttons.Controls.Add(CreateButton("Refresh", (_, _) => RefreshData()));
        buttons.Controls.Add(CreateButton("Copy log", (_, _) => CopySelectedLog()));
        buttons.Controls.Add(CreateButton("Open storage", (_, _) => OpenPath(SelectedInstance?.GraphStorageRoot)));
        buttons.Controls.Add(CreateButton("Open server dir", (_, _) => OpenPath(SelectedInstance?.BaseDirectory)));

        root.Controls.Add(_summary, 0, 0);
        root.Controls.Add(_details, 0, 1);
        root.Controls.Add(_log, 0, 2);
        root.Controls.Add(buttons, 0, 3);

        return root;
    }

    private McpInstanceLog? SelectedInstance => (_instances.SelectedItem as InstanceListItem)?.Instance;

    private void RefreshSelectedLog()
    {
        var instance = SelectedInstance;
        if (instance is null)
        {
            _details.Text = "No MCP instances have connected yet.";
            _log.Text = string.Empty;
            return;
        }

        _details.Text = $"PID {instance.ProcessId}; state {instance.State}; last seen {instance.LastSeenAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}; storage {instance.GraphStorageRoot ?? "-"}";
        _log.Text = instance.FormatLogText();
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private void CopySelectedLog()
    {
        var instance = SelectedInstance;
        if (instance is null)
        {
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine(instance.DisplayName);
        builder.AppendLine(_details.Text);
        builder.AppendLine();
        builder.Append(instance.FormatLogText());
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

    private sealed class InstanceListItem(McpInstanceLog instance)
    {
        public McpInstanceLog Instance { get; } = instance;

        public string InstanceId => Instance.InstanceId;

        public override string ToString()
        {
            return Instance.DisplayName;
        }
    }
}
