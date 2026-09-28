using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PlcConectCom.WinFormsTest;

/// <summary>
/// 手动操作日志查询页：分三页查看「点击按钮 / 修改位置 / 点击输出点」，支持日期区间与关键字筛选。
/// 数据来源 <see cref="ManualOperationLogStore"/>（Logs/ManualOperation 按日期分库）。
/// </summary>
public sealed class ManualOperationLogPageControl : UserControl
{
    private readonly DateTimePicker _dtFrom = new()
    {
        Width = 132,
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "yyyy-MM-dd",
        Value = DateTime.Today
    };

    private readonly DateTimePicker _dtTo = new()
    {
        Width = 132,
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "yyyy-MM-dd",
        Value = DateTime.Today
    };

    private readonly TextBox _txtKeyword = new() { Width = 180 };

    private readonly Button _btnQuery = new() { Text = "查询", Width = 76, Height = 28 };
    private readonly Button _btnToday = new() { Text = "今天", Width = 64, Height = 28 };
    private readonly Button _btnLast7 = new() { Text = "最近7天", Width = 84, Height = 28 };
    private readonly Button _btnAll = new() { Text = "全部日期", Width = 90, Height = 28 };
    private readonly Button _btnExport = new() { Text = "导出CSV", Width = 90, Height = 28 };
    private readonly Button _btnOpenDir = new() { Text = "打开日志目录", Width = 110, Height = 28 };

    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 10F) };

    private readonly Dictionary<ManualOperationCategory, DataGridView> _grids = [];
    private readonly Dictionary<ManualOperationCategory, Label> _summaries = [];

    /// <summary>true 表示忽略日期区间，查询目录下全部日志文件。</summary>
    private bool _queryAllDates;

    private DateTime _lastFrom = DateTime.Today;
    private DateTime _lastTo = DateTime.Today;
    private string _lastKeyword = string.Empty;
    private bool _lastAllDates;

    public ManualOperationLogPageControl()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildUi();
        BindEvents();
        Query();
    }

    private void BuildUi()
    {
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 44,
            Padding = new Padding(8, 7, 8, 4),
            WrapContents = false,
            AutoScroll = true,
            BackColor = Color.FromArgb(241, 245, 252)
        };
        toolbar.Controls.Add(CreateLabel("开始日期", 0));
        toolbar.Controls.Add(_dtFrom);
        toolbar.Controls.Add(CreateLabel("结束日期", 10));
        toolbar.Controls.Add(_dtTo);
        toolbar.Controls.Add(_btnQuery);
        toolbar.Controls.Add(_btnToday);
        toolbar.Controls.Add(_btnLast7);
        toolbar.Controls.Add(_btnAll);
        toolbar.Controls.Add(CreateLabel("关键字", 10));
        toolbar.Controls.Add(_txtKeyword);
        toolbar.Controls.Add(_btnExport);
        toolbar.Controls.Add(_btnOpenDir);
        _btnQuery.Margin = new Padding(10, 0, 0, 0);

        _tabs.TabPages.Add(BuildTab(
            ManualOperationCategory.Button,
            "点击按钮",
            ["时间", "设备", "轴", "按钮", "说明", "操作人"],
            [18, 10, 14, 12, 34, 12]));
        _tabs.TabPages.Add(BuildTab(
            ManualOperationCategory.Position,
            "修改位置",
            ["时间", "设备", "位置", "轴", "操作", "修改前", "修改后", "操作人"],
            [16, 9, 16, 11, 12, 10, 10, 16]));
        _tabs.TabPages.Add(BuildTab(
            ManualOperationCategory.Output,
            "点击输出点",
            ["时间", "设备", "点位", "地址", "状态变化", "说明", "操作人"],
            [16, 9, 22, 12, 12, 18, 11]));

        Controls.Add(_tabs);
        Controls.Add(toolbar);

        DataGridViewNoSortHelper.WireControlSubtreeForNoColumnSort(this);
    }

    private static Label CreateLabel(string text, int leftPadding) => new()
    {
        Text = text,
        AutoSize = true,
        Padding = new Padding(leftPadding, 7, 0, 0)
    };

    private TabPage BuildTab(ManualOperationCategory category, string title, string[] headers, int[] weights)
    {
        var page = new TabPage(title) { Padding = new Padding(6), BackColor = Color.White, UseVisualStyleBackColor = true };

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            RowTemplate = { Height = 24 },
            AllowUserToOrderColumns = true
        };
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);

        for (int i = 0; i < headers.Length; i++)
        {
            var column = new DataGridViewTextBoxColumn
            {
                HeaderText = headers[i],
                Name = $"col{i}",
                ReadOnly = true,
                FillWeight = i < weights.Length ? weights[i] : 10,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
            grid.Columns.Add(column);
        }

        var summary = new Label
        {
            Dock = DockStyle.Top,
            Height = 26,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
            ForeColor = Color.FromArgb(28, 58, 120)
        };

        page.Controls.Add(grid);
        page.Controls.Add(summary);

        _grids[category] = grid;
        _summaries[category] = summary;
        return page;
    }

    private void BindEvents()
    {
        _btnQuery.Click += (_, _) => Query();
        _btnToday.Click += (_, _) =>
        {
            _queryAllDates = false;
            _dtFrom.Value = DateTime.Today;
            _dtTo.Value = DateTime.Today;
            Query();
        };
        _btnLast7.Click += (_, _) =>
        {
            _queryAllDates = false;
            _dtFrom.Value = DateTime.Today.AddDays(-6);
            _dtTo.Value = DateTime.Today;
            Query();
        };
        _btnAll.Click += (_, _) =>
        {
            _queryAllDates = true;
            Query();
        };
        _btnExport.Click += (_, _) => ExportCurrentTab();
        _btnOpenDir.Click += (_, _) => OpenLogsDirectory();
        _txtKeyword.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter)
                return;

            e.Handled = true;
            e.SuppressKeyPress = true;
            Query();
        };
        _tabs.SelectedIndexChanged += (_, _) => UpdateSummaryForSelectedTab();
    }

    /// <summary>按当前条件重新查询三类日志（页面打开或重新切换回来时调用）。</summary>
    public void RefreshLogs() => Query();

    private void Query()
    {
        DateTime from = _dtFrom.Value.Date;
        DateTime to = _dtTo.Value.Date;
        if (to < from)
            (from, to) = (to, from);

        _lastFrom = from;
        _lastTo = to;
        _lastAllDates = _queryAllDates;
        _lastKeyword = _txtKeyword.Text.Trim();

        foreach ((ManualOperationCategory category, DataGridView grid) in _grids)
        {
            List<ManualOperationLogEntry> rows = ManualOperationLogStore.Query(
                category,
                _queryAllDates ? null : from,
                _queryAllDates ? null : to,
                _lastKeyword);

            grid.SuspendLayout();
            try
            {
                grid.Rows.Clear();
                foreach (ManualOperationLogEntry entry in rows)
                    grid.Rows.Add(BuildRowValues(category, entry));
                grid.ClearSelection();
            }
            finally
            {
                grid.ResumeLayout();
            }

            _summaries[category].Text = BuildSummary(rows.Count);
        }
    }

    private static object[] BuildRowValues(ManualOperationCategory category, ManualOperationLogEntry entry)
    {
        string time = entry.LogTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return category switch
        {
            ManualOperationCategory.Position =>
            [
                time,
                entry.Device,
                entry.Target,
                entry.SubTarget,
                entry.Action,
                entry.BeforeValue,
                entry.AfterValue,
                entry.Operator
            ],
            ManualOperationCategory.Output =>
            [
                time,
                entry.Device,
                entry.Target,
                entry.Address,
                string.IsNullOrEmpty(entry.AfterValue)
                    ? entry.BeforeValue
                    : $"{entry.BeforeValue} → {entry.AfterValue}",
                entry.Detail,
                entry.Operator
            ],
            _ =>
            [
                time,
                entry.Device,
                entry.Target,
                entry.Action,
                entry.Detail,
                entry.Operator
            ]
        };
    }

    private string BuildSummary(int count)
    {
        string range = _lastAllDates
            ? "全部日期"
            : $"{_lastFrom:yyyy-MM-dd} ~ {_lastTo:yyyy-MM-dd}";
        string keyword = string.IsNullOrEmpty(_lastKeyword) ? string.Empty : $"，关键字「{_lastKeyword}」";
        string countText = count == 0 ? "未查询到日志" : $"共 {count} 条";
        return $"{countText}（{range}{keyword}）    日志目录：{ManualOperationLogStore.LogsDirectory}";
    }

    private void UpdateSummaryForSelectedTab()
    {
        if (_tabs.SelectedTab is null || _grids.Count == 0)
            return;

        foreach (var (category, grid) in _grids)
        {
            if (!ReferenceEquals(grid.Parent, _tabs.SelectedTab))
                continue;

            _summaries[category].Text = BuildSummary(grid.Rows.Count);
            return;
        }
    }

    private void ExportCurrentTab()
    {
        (ManualOperationCategory category, DataGridView? grid) = GetSelectedGrid();
        if (grid is null)
            return;

        using var dialog = new SaveFileDialog
        {
            Title = "导出手动操作日志",
            Filter = "CSV 文件(*.csv)|*.csv",
            FileName = $"手动操作日志_{_tabs.SelectedTab?.Text}_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            AddExtension = true,
            DefaultExt = "csv"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var builder = new StringBuilder();
        var header = new List<string>();
        foreach (DataGridViewColumn column in grid.Columns)
            header.Add(EscapeCsv(column.HeaderText));
        builder.AppendLine(string.Join(",", header));

        foreach (DataGridViewRow row in grid.Rows)
        {
            var values = new List<string>();
            foreach (DataGridViewCell cell in row.Cells)
                values.Add(EscapeCsv(Convert.ToString(cell.Value, CultureInfo.InvariantCulture) ?? string.Empty));
            builder.AppendLine(string.Join(",", values));
        }

        try
        {
            File.WriteAllText(dialog.FileName, builder.ToString(), new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导出失败：{ex.Message}", "手动操作日志", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private (ManualOperationCategory Category, DataGridView? Grid) GetSelectedGrid()
    {
        if (_tabs.SelectedTab is null)
            return (ManualOperationCategory.Button, null);

        foreach (var (category, grid) in _grids)
        {
            if (ReferenceEquals(grid.Parent, _tabs.SelectedTab))
                return (category, grid);
        }

        return (ManualOperationCategory.Button, null);
    }

    private static string EscapeCsv(string text)
    {
        if (!text.Contains(',') && !text.Contains('"') && !text.Contains('\n') && !text.Contains('\r'))
            return text;

        return $"\"{text.Replace("\"", "\"\"")}\"";
    }

    private void OpenLogsDirectory()
    {
        try
        {
            string directory = ManualOperationLogStore.LogsDirectory;
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法打开日志目录：{ex.Message}", "手动操作日志", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
