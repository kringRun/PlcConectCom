using System.Globalization;

namespace PlcConectCom.WinFormsTest;

internal sealed class ApplicationLogQueryDialog : Form
{
    private readonly DateTimePicker _dtFrom = new()
    {
        Width = 140,
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "yyyy-MM-dd",
        ShowCheckBox = true,
        Checked = true,
        Value = DateTime.Today
    };

    private readonly DateTimePicker _dtTo = new()
    {
        Width = 140,
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "yyyy-MM-dd",
        ShowCheckBox = true,
        Checked = true,
        Value = DateTime.Today
    };

    private readonly TextBox _txtKeyword = new() { Width = 180 };
    private readonly Button _btnQuery = new() { Text = "查询", Width = 72 };
    private readonly Button _btnToday = new() { Text = "今天", Width = 60 };
    private readonly Button _btnClose = new() { Text = "关闭", Width = 72, DialogResult = DialogResult.Cancel };
    private readonly Label _lblSummary = new()
    {
        Dock = DockStyle.Top,
        Height = 28,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(8, 0, 0, 0),
        ForeColor = Color.FromArgb(28, 58, 120)
    };

    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        RowHeadersVisible = false,
        ReadOnly = true,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle
    };

    private ApplicationLogQueryDialog()
    {
        Text = "运行日志查询";
        Width = 980;
        Height = 640;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.FromArgb(241, 245, 252);
        MinimizeBox = false;
        MaximizeBox = true;

        _grid.Columns.Add("LogTime", "时间");
        _grid.Columns.Add("Message", "内容");
        _grid.Columns["LogTime"].FillWeight = 22;
        _grid.Columns["Message"].FillWeight = 78;
        _grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 44,
            Padding = new Padding(8, 6, 8, 4),
            WrapContents = false,
            AutoScroll = true
        };
        toolbar.Controls.Add(new Label { Text = "开始日期", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        toolbar.Controls.Add(_dtFrom);
        toolbar.Controls.Add(new Label { Text = "结束日期", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
        toolbar.Controls.Add(_dtTo);
        toolbar.Controls.Add(new Label { Text = "关键字", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
        toolbar.Controls.Add(_txtKeyword);
        toolbar.Controls.Add(_btnQuery);
        toolbar.Controls.Add(_btnToday);

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8)
        };
        bottom.Controls.Add(_btnClose);

        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8), BackColor = Color.White };
        content.Controls.Add(_grid);
        content.Controls.Add(_lblSummary);

        Controls.Add(content);
        Controls.Add(bottom);
        Controls.Add(toolbar);

        AcceptButton = _btnQuery;
        CancelButton = _btnClose;

        _btnQuery.Click += (_, _) => RunQuery();
        _btnToday.Click += (_, _) =>
        {
            _dtFrom.Value = DateTime.Today;
            _dtTo.Value = DateTime.Today;
            _dtFrom.Checked = true;
            _dtTo.Checked = true;
            RunQuery();
        };

        DataGridViewNoSortHelper.WireControlSubtreeForNoColumnSort(this);
        Shown += (_, _) => RunQuery();
    }

    public static void Show(IWin32Window? owner)
    {
        using var dialog = new ApplicationLogQueryDialog();
        if (owner is Control control)
            AppDialog.ShowDialog(dialog, control);
        else
            AppDialog.ShowDialog(dialog);
    }

    private void RunQuery()
    {
        DateTime fromDate = _dtFrom.Checked ? _dtFrom.Value.Date : DateTime.Today.AddDays(-30);
        DateTime toDate = _dtTo.Checked ? _dtTo.Value.Date : DateTime.Today;
        if (toDate < fromDate)
            (fromDate, toDate) = (toDate, fromDate);

        List<ApplicationLogEntry> rows = ApplicationLogStore.Query(fromDate, toDate, _txtKeyword.Text);
        _grid.Rows.Clear();
        foreach (ApplicationLogEntry entry in rows)
        {
            _grid.Rows.Add(
                entry.LogTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                entry.Message);
        }

        _lblSummary.Text = rows.Count == 0
            ? $"未查询到日志（{fromDate:yyyy-MM-dd} ~ {toDate:yyyy-MM-dd}）"
            : $"共 {rows.Count} 条（{fromDate:yyyy-MM-dd} ~ {toDate:yyyy-MM-dd}）";
    }
}
