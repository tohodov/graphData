using System.Data;
using System.Diagnostics;
using System.Text;

namespace GraphData.McpTray;

internal sealed class StatusForm : Form
{
    private const string ColumnTime = "Time";
    private const string ColumnLevel = "Level";
    private const string ColumnType = "Type";
    private const string ColumnCategory = "Category";
    private const string ColumnEventId = "EventId";
    private const string ColumnMessage = "Message";
    private const string ColumnException = "Exception";

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

    private readonly ComboBox _levelFilter = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 120
    };

    private readonly TextBox _categoryFilter = new()
    {
        Width = 180
    };

    private readonly TextBox _searchFilter = new()
    {
        Width = 260
    };

    private readonly Button _columnsButton = new()
    {
        AutoSize = true,
        Text = "Columns"
    };

    private readonly DataTable _logTable = CreateLogTable();
    private readonly BindingSource _logBindingSource = new();
    private readonly DataGridView _logGrid = new()
    {
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AutoGenerateColumns = false,
        BorderStyle = BorderStyle.FixedSingle,
        ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText,
        Dock = DockStyle.Fill,
        MultiSelect = true,
        ReadOnly = true,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect
    };

    private bool _refreshingInstances;
    private string? _loadedInstanceId;
    private int _loadedEntryCount;
    private long _loadedLogVersion;

    public StatusForm(McpLogCollector collector)
    {
        _collector = collector;

        Text = "graphData MCP logs";
        Width = 1120;
        Height = 680;
        MinimumSize = new Size(840, 480);
        StartPosition = FormStartPosition.CenterScreen;

        _levelFilter.Items.AddRange(["All", "Trace", "Debug", "Information", "Warning", "Error", "Critical", "Lifecycle"]);
        _levelFilter.SelectedIndex = 0;
        _levelFilter.SelectedIndexChanged += (_, _) => ApplyFilters();
        _categoryFilter.TextChanged += (_, _) => ApplyFilters();
        _searchFilter.TextChanged += (_, _) => ApplyFilters();
        _columnsButton.Click += (_, _) => ShowColumnMenu();

        _instances.SelectedIndexChanged += (_, _) =>
        {
            if (!_refreshingInstances)
            {
                LoadSelectedInstance(forceReload: true);
            }
        };

        ConfigureLogGrid();
        _logBindingSource.DataSource = _logTable;
        _logGrid.DataSource = _logBindingSource;

        Controls.Add(CreateLayout());
        _collector.Changed += OnCollectorChanged;
        RefreshData();
    }

    public void RefreshData()
    {
        if (!CanRefresh)
        {
            return;
        }

        var selectedId = SelectedInstanceId;
        RebuildInstanceList(selectedId);
        LoadSelectedInstance(forceReload: _loadedInstanceId != SelectedInstanceId);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _collector.Changed -= OnCollectorChanged;
            _logBindingSource.Dispose();
            _logTable.Dispose();
        }

        base.Dispose(disposing);
    }

    private Control CreateLayout()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            SplitterDistance = 300
        };

        split.Panel1.Controls.Add(_instances);
        split.Panel2.Controls.Add(CreateRightPanel());

        return split;
    }

    private Control CreateRightPanel()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(8)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(_summary, 0, 0);
        root.Controls.Add(_details, 0, 1);
        root.Controls.Add(CreateFiltersPanel(), 0, 2);
        root.Controls.Add(_logGrid, 0, 3);
        root.Controls.Add(CreateButtonsPanel(), 0, 4);

        return root;
    }

    private Control CreateFiltersPanel()
    {
        var filters = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            WrapContents = true
        };

        filters.Controls.Add(CreateFilterLabel("Level"));
        filters.Controls.Add(_levelFilter);
        filters.Controls.Add(CreateFilterLabel("Category"));
        filters.Controls.Add(_categoryFilter);
        filters.Controls.Add(CreateFilterLabel("Search"));
        filters.Controls.Add(_searchFilter);
        filters.Controls.Add(CreateButton("Clear filters", (_, _) => ClearFilters()));
        filters.Controls.Add(_columnsButton);

        return filters;
    }

    private Control CreateButtonsPanel()
    {
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft
        };

        buttons.Controls.Add(CreateButton("Refresh", (_, _) => RefreshData()));
        buttons.Controls.Add(CreateButton("Copy rows", (_, _) => CopyRows()));
        buttons.Controls.Add(CreateButton("Open storage", (_, _) => OpenPath(SelectedInstance?.GraphStorageRoot)));
        buttons.Controls.Add(CreateButton("Open server dir", (_, _) => OpenPath(SelectedInstance?.BaseDirectory)));

        return buttons;
    }

    private void ConfigureLogGrid()
    {
        _logGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = ColumnTime,
            DataPropertyName = ColumnTime,
            HeaderText = "Time",
            SortMode = DataGridViewColumnSortMode.Automatic,
            DefaultCellStyle = new DataGridViewCellStyle { Format = "yyyy-MM-dd HH:mm:ss.fff" },
            Width = 170
        });
        _logGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = ColumnLevel,
            DataPropertyName = ColumnLevel,
            HeaderText = "Level",
            SortMode = DataGridViewColumnSortMode.Automatic,
            Width = 95
        });
        _logGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = ColumnType,
            DataPropertyName = ColumnType,
            HeaderText = "Type",
            SortMode = DataGridViewColumnSortMode.Automatic,
            Width = 125
        });
        _logGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = ColumnCategory,
            DataPropertyName = ColumnCategory,
            HeaderText = "Category",
            SortMode = DataGridViewColumnSortMode.Automatic,
            Width = 220
        });
        _logGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = ColumnEventId,
            DataPropertyName = ColumnEventId,
            HeaderText = "Event",
            SortMode = DataGridViewColumnSortMode.Automatic,
            Width = 70
        });
        _logGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = ColumnMessage,
            DataPropertyName = ColumnMessage,
            HeaderText = "Message",
            SortMode = DataGridViewColumnSortMode.Automatic,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 180,
            MinimumWidth = 240
        });
        _logGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = ColumnException,
            DataPropertyName = ColumnException,
            HeaderText = "Exception",
            SortMode = DataGridViewColumnSortMode.Automatic,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 120,
            MinimumWidth = 220,
            Visible = false
        });
    }

    private void OnCollectorChanged(object? sender, McpLogCollectorChangedEventArgs args)
    {
        if (!CanRefresh)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke((MethodInvoker)(() => OnCollectorChanged(sender, args)));
            }
            catch (InvalidOperationException)
            {
            }

            return;
        }

        var preferredId = SelectedInstanceId ?? args.InstanceId;
        RebuildInstanceList(preferredId);
        LoadSelectedInstance(forceReload: _loadedInstanceId != SelectedInstanceId);
    }

    private void RebuildInstanceList(string? preferredInstanceId)
    {
        if (!CanRefresh)
        {
            return;
        }

        _refreshingInstances = true;
        _instances.BeginUpdate();
        try
        {
            _instances.Items.Clear();

            foreach (var instance in _collector.Instances)
            {
                _instances.Items.Add(new InstanceListItem(instance));
            }

            SelectInstance(preferredInstanceId);

            if (_instances.SelectedIndex < 0 && _instances.Items.Count > 0)
            {
                _instances.SelectedIndex = 0;
            }
        }
        finally
        {
            if (!_instances.IsDisposed)
            {
                _instances.EndUpdate();
            }

            _refreshingInstances = false;
        }
    }

    private void SelectInstance(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return;
        }

        for (var index = 0; index < _instances.Items.Count; index++)
        {
            if ((_instances.Items[index] as InstanceListItem)?.InstanceId == instanceId)
            {
                _instances.SelectedIndex = index;
                return;
            }
        }
    }

    private void LoadSelectedInstance(bool forceReload)
    {
        UpdateSummaryAndDetails();

        var instance = SelectedInstance;
        if (instance is null)
        {
            _loadedInstanceId = null;
            _loadedEntryCount = 0;
            _loadedLogVersion = 0;
            _logTable.Clear();
            return;
        }

        if (forceReload
            || _loadedInstanceId != instance.InstanceId
            || instance.Entries.Count < _loadedEntryCount
            || (instance.LogVersion != _loadedLogVersion && instance.Entries.Count <= _loadedEntryCount))
        {
            ReloadLogRows(instance);
            return;
        }

        AppendNewLogRows(instance);
    }

    private void ReloadLogRows(McpInstanceLog instance)
    {
        _logTable.BeginLoadData();
        try
        {
            _logTable.Clear();

            foreach (var entry in instance.Entries)
            {
                AddLogRow(entry);
            }
        }
        finally
        {
            _logTable.EndLoadData();
        }

        _loadedInstanceId = instance.InstanceId;
        _loadedEntryCount = instance.Entries.Count;
        _loadedLogVersion = instance.LogVersion;
    }

    private void AppendNewLogRows(McpInstanceLog instance)
    {
        for (var index = _loadedEntryCount; index < instance.Entries.Count; index++)
        {
            AddLogRow(instance.Entries[index]);
        }

        _loadedEntryCount = instance.Entries.Count;
        _loadedLogVersion = instance.LogVersion;
    }

    private void AddLogRow(McpLogEntry entry)
    {
        var row = _logTable.NewRow();
        row[ColumnTime] = entry.Timestamp.ToLocalTime().DateTime;
        row[ColumnLevel] = entry.Level ?? string.Empty;
        row[ColumnType] = entry.MessageType;
        row[ColumnCategory] = entry.Category ?? string.Empty;
        row[ColumnEventId] = entry.EventId is null ? DBNull.Value : entry.EventId.Value;
        row[ColumnMessage] = entry.Text ?? entry.MessageType;
        row[ColumnException] = entry.Exception ?? string.Empty;
        _logTable.Rows.Add(row);
    }

    private void UpdateSummaryAndDetails()
    {
        if (!CanRefresh)
        {
            return;
        }

        _summary.Text = $"{_collector.RunningCount} running, {_collector.Instances.Count} total";

        var instance = SelectedInstance;
        if (instance is null)
        {
            _details.Text = "No MCP instances have connected yet.";
            return;
        }

        _details.Text = $"PID {instance.ProcessId}; state {instance.State}; last seen {instance.LastSeenAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}; storage {instance.GraphStorageRoot ?? "-"}";
    }

    private void ApplyFilters()
    {
        if (_logBindingSource.DataSource is null)
        {
            return;
        }

        var filters = new List<string>();
        var level = _levelFilter.SelectedItem?.ToString();
        if (!string.IsNullOrWhiteSpace(level) && !string.Equals(level, "All", StringComparison.Ordinal))
        {
            filters.Add(string.Equals(level, "Lifecycle", StringComparison.Ordinal)
                ? $"[{ColumnType}] <> 'log'"
                : $"[{ColumnLevel}] = '{EscapeFilterValue(level)}'");
        }

        AddContainsFilter(filters, ColumnCategory, _categoryFilter.Text);

        var search = _searchFilter.Text.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var value = EscapeFilterLikeValue(search);
            filters.Add(
                $"([{ColumnLevel}] LIKE '%{value}%'"
                + $" OR [{ColumnType}] LIKE '%{value}%'"
                + $" OR [{ColumnCategory}] LIKE '%{value}%'"
                + $" OR [{ColumnMessage}] LIKE '%{value}%'"
                + $" OR [{ColumnException}] LIKE '%{value}%')");
        }

        _logBindingSource.Filter = string.Join(" AND ", filters);
    }

    private void ClearFilters()
    {
        _levelFilter.SelectedIndex = 0;
        _categoryFilter.Clear();
        _searchFilter.Clear();
        ApplyFilters();
    }

    private void ShowColumnMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Closed += (_, _) => menu.Dispose();

        foreach (DataGridViewColumn column in _logGrid.Columns)
        {
            var item = new ToolStripMenuItem(column.HeaderText)
            {
                Checked = column.Visible,
                CheckOnClick = true,
                Tag = column
            };
            item.CheckedChanged += (_, _) =>
            {
                if (item.Tag is not DataGridViewColumn gridColumn)
                {
                    return;
                }

                if (!item.Checked && VisibleColumnCount == 1)
                {
                    item.Checked = true;
                    return;
                }

                gridColumn.Visible = item.Checked;
            };
            menu.Items.Add(item);
        }

        menu.Show(_columnsButton, new Point(0, _columnsButton.Height));
    }

    private void CopyRows()
    {
        if (!CanRefresh)
        {
            return;
        }

        var rows = _logGrid.SelectedRows
            .Cast<DataGridViewRow>()
            .Where(static row => !row.IsNewRow)
            .OrderBy(static row => row.Index)
            .ToArray();

        if (rows.Length == 0)
        {
            rows = _logGrid.Rows
                .Cast<DataGridViewRow>()
                .Where(static row => !row.IsNewRow)
                .ToArray();
        }

        if (rows.Length == 0)
        {
            return;
        }

        var columns = _logGrid.Columns
            .Cast<DataGridViewColumn>()
            .Where(static column => column.Visible)
            .OrderBy(static column => column.DisplayIndex)
            .ToArray();

        var builder = new StringBuilder();
        builder.AppendLine(string.Join('\t', columns.Select(static column => column.HeaderText)));

        foreach (var row in rows)
        {
            builder.AppendLine(string.Join('\t', columns.Select(column => FormatCellValue(row.Cells[column.Index].Value))));
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

    private static Label CreateFilterLabel(string text)
    {
        return new Label
        {
            AutoSize = true,
            Margin = new Padding(8, 6, 2, 0),
            Text = text
        };
    }

    private static DataTable CreateLogTable()
    {
        var table = new DataTable("Logs")
        {
            Locale = System.Globalization.CultureInfo.InvariantCulture
        };
        table.Columns.Add(ColumnTime, typeof(DateTime));
        table.Columns.Add(ColumnLevel, typeof(string));
        table.Columns.Add(ColumnType, typeof(string));
        table.Columns.Add(ColumnCategory, typeof(string));
        table.Columns.Add(ColumnEventId, typeof(int));
        table.Columns.Add(ColumnMessage, typeof(string));
        table.Columns.Add(ColumnException, typeof(string));
        return table;
    }

    private static void AddContainsFilter(List<string> filters, string columnName, string value)
    {
        var trimmed = value.Trim();
        if (!string.IsNullOrWhiteSpace(trimmed))
        {
            filters.Add($"[{columnName}] LIKE '%{EscapeFilterLikeValue(trimmed)}%'");
        }
    }

    private static string EscapeFilterValue(string value)
    {
        return value.Replace("'", "''");
    }

    private static string EscapeFilterLikeValue(string value)
    {
        return EscapeFilterValue(value)
            .Replace("[", "[[]")
            .Replace("]", "[]]")
            .Replace("%", "[%]")
            .Replace("*", "[*]");
    }

    private static string FormatCellValue(object? value)
    {
        return value switch
        {
            null or DBNull => string.Empty,
            DateTime timestamp => timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)?
                .Replace('\r', ' ')
                .Replace('\n', ' ') ?? string.Empty
        };
    }

    private McpInstanceLog? SelectedInstance => (_instances.SelectedItem as InstanceListItem)?.Instance;

    private string? SelectedInstanceId => (_instances.SelectedItem as InstanceListItem)?.InstanceId;

    private int VisibleColumnCount => _logGrid.Columns.Cast<DataGridViewColumn>().Count(static column => column.Visible);

    private bool CanRefresh =>
        !IsDisposed
        && !Disposing
        && !_instances.IsDisposed
        && !_summary.IsDisposed
        && !_details.IsDisposed
        && !_levelFilter.IsDisposed
        && !_categoryFilter.IsDisposed
        && !_searchFilter.IsDisposed
        && !_logGrid.IsDisposed;

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
