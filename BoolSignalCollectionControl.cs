using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Drawing.Drawing2D;
using ExcelDataReader;

namespace PlcConectCom.WinFormsTest;

// BOOL 信号点定义（名称 + 地址）
public sealed class BoolSignalPoint
{
    public string Name { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;
}

// BOOL 信号集合监控控件，支持点动刷新与自动刷新
public sealed class BoolSignalCollectionControl : UserControl
{
    private const string DefaultConfigFileName = "BoolSignalCollection.config.json";
    private static readonly JsonSerializerOptions JsonFileOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
    };

    private readonly Button _btnAdd = new() { Text = "新增点", Width = 72 };
    private readonly Button _btnRemove = new() { Text = "删除点", Width = 72 };
    private readonly Button _btnEditAuth = new() { Text = "编辑解锁", Width = 84 };
    private readonly Button _btnImportData = new() { Text = "导入数据", Width = 84 };
    private readonly Button _btnRefresh = new() { Text = "点动刷新", Width = 92 };
    private readonly Button _btnMatrixDisplay = new() { Text = "点阵显示", Width = 84 };
    private readonly System.Windows.Forms.Timer _autoRefreshTimer = new() { Interval = 1000 };
    private readonly string _configFilePath;
    private bool _suspendAutoSave;
    private bool _isAutoRefreshing;
    private bool _isRefreshing;
    private bool _skipRefreshClickOnce;
    private bool _canAddDelete = true;
    private bool _canModify = true;
    private bool _editUnlocked;

    public BoolSignalCollectionControl()
    {
        _configFilePath = Path.Combine(AppContext.BaseDirectory, DefaultConfigFileName);
        BuildUi();
        BindEvents();
        LoadConfigFromFile();
    }

    public Func<global::PlcConectCom.PlcCommunicator?>? PlcProvider { get; set; }

    public global::PlcConectCom.PlcCommunicator? PlcInstance { get; set; }

    public Action<string>? LogAction { get; set; }

    public Func<bool>? EditPermissionRequest { get; set; }

    public int AutoRefreshIntervalMs
    {
        get => _autoRefreshTimer.Interval;
        set => _autoRefreshTimer.Interval = Math.Clamp(value, 200, 60_000);
    }

    public void SetPoints(IEnumerable<BoolSignalPoint> points)
    {
        ApplyPoints(points, saveAfterApply: true);
    }

    public void SetPermission(bool canAddDelete, bool canModify)
    {
        _canAddDelete = canAddDelete;
        _canModify = canModify;
        if (!canModify)
            _editUnlocked = false;

        _btnAdd.Enabled = _canAddDelete && _editUnlocked;
        _btnRemove.Enabled = _canAddDelete && _editUnlocked;
        _btnRefresh.Enabled = canModify;
        _btnImportData.Enabled = canModify;
        _btnMatrixDisplay.Enabled = canModify;
        _btnEditAuth.Enabled = canModify;
        _btnEditAuth.Text = _editUnlocked ? "编辑锁定" : "编辑解锁";
        _grid.ReadOnly = !_editUnlocked;
        if (!canModify && _isAutoRefreshing)
            StopAutoRefresh();
    }

    private void ApplyPoints(IEnumerable<BoolSignalPoint> points, bool saveAfterApply)
    {
        _suspendAutoSave = true;
        _grid.Rows.Clear();
        foreach (var point in points)
            _grid.Rows.Add(point.Name, point.Address, string.Empty, string.Empty);
        _suspendAutoSave = false;

        if (saveAfterApply)
            SaveConfigToFile();
    }

    public IReadOnlyList<BoolSignalPoint> GetPoints()
    {
        var points = new List<BoolSignalPoint>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            string name = Convert.ToString(row.Cells[0].Value) ?? string.Empty;
            string address = Convert.ToString(row.Cells[1].Value) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(address))
                continue;

            points.Add(new BoolSignalPoint { Name = name.Trim(), Address = address.Trim() });
        }

        return points;
    }

    public async Task RefreshSignalsAsync()
    {
        if (_isRefreshing)
            return;

        _isRefreshing = true;

        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
        {
            MarkAllDisconnected();
            _isRefreshing = false;
            return;
        }

        try
        {
            foreach (DataGridViewRow row in _grid.Rows)
            {
                string address = (Convert.ToString(row.Cells[1].Value) ?? string.Empty).Trim();
                if (!IsSupportedAddress(address))
                {
                    SetSignalRow(row, false, "地址无效", Color.DarkGray);
                    continue;
                }

                try
                {
                    bool value = await plc.ReadBoolAsync(address);
                    SetSignalRow(row, value, value ? "ON" : "OFF", value ? Color.LawnGreen : Color.Red);
                }
                catch (Exception ex)
                {
                    SetSignalRow(row, false, "ERR", Color.DarkOrange);
                    LogAction?.Invoke($"读取失败 {address}: {ex.Message}");
                }
            }
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void BuildUi()
    {
        _grid.DefaultCellStyle.SelectionBackColor = Color.Gainsboro;
        _grid.DefaultCellStyle.SelectionForeColor = Color.Black;
        _grid.RowTemplate.Height = 30;

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "点名称", FillWeight = 30 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "地址(MX/MB)", FillWeight = 30 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "值", FillWeight = 20, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "状态", FillWeight = 20, ReadOnly = true });

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 0, 0, 4)
        };

        buttonPanel.Controls.AddRange([_btnEditAuth, _btnImportData, _btnAdd, _btnRemove, _btnRefresh, _btnMatrixDisplay]);

        Controls.Add(_grid);
        Controls.Add(buttonPanel);
    }

    private void BindEvents()
    {
        _btnEditAuth.Click += (_, _) => ToggleEditAuth();
        _btnAdd.Click += (_, _) =>
        {
            _grid.Rows.Add("", "", "", "");
            SaveConfigToFile();
        };
        _btnImportData.Click += (_, _) => ImportPointsFromExcel();
        _btnRemove.Click += (_, _) => RemoveCurrentRow();
        _btnRefresh.Click += async (_, _) => await HandleRefreshButtonClickAsync();
        _btnMatrixDisplay.Click += (_, _) => ShowMatrixDisplayDialog();
        _btnRefresh.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && e.Clicks >= 2)
            {
                _skipRefreshClickOnce = true;
                ToggleAutoRefresh();
            }
        };
        _autoRefreshTimer.Tick += async (_, _) => await RefreshSignalsAsync();
        _grid.CellValueChanged += (_, _) =>
        {
            if (_suspendAutoSave || !_editUnlocked)
                return;

            SaveConfigToFile();
        };
    }

    private void ToggleEditAuth()
    {
        if (!_canModify)
            return;

        if (_editUnlocked)
        {
            _editUnlocked = false;
            SetPermission(_canAddDelete, _canModify);
            return;
        }

        bool ok = EditPermissionRequest?.Invoke() ?? false;
        if (!ok)
            return;

        _editUnlocked = true;
        SetPermission(_canAddDelete, _canModify);
    }

    private void ImportPointsFromExcel()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择状态监控Excel文件",
            Filter = "Excel 文件 (*.xls;*.xlsx)|*.xls;*.xlsx|所有文件 (*.*)|*.*",
            InitialDirectory = AppContext.BaseDirectory
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            using var stream = File.Open(dialog.FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = ExcelReaderFactory.CreateReader(stream);

            var points = new List<BoolSignalPoint>();
            bool isHeader = true;
            int nameCol = 1;
            int addressCol = 2;

            while (reader.Read())
            {
                if (isHeader)
                {
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        string header = Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                        if (string.Equals(header, "Name", StringComparison.OrdinalIgnoreCase))
                            nameCol = i;
                        else if (string.Equals(header, "Address", StringComparison.OrdinalIgnoreCase))
                            addressCol = i;
                    }

                    isHeader = false;
                    continue;
                }

                string name = NormalizeImportedName(Convert.ToString(reader.GetValue(nameCol), CultureInfo.InvariantCulture));
                string address = NormalizeImportedAddress(Convert.ToString(reader.GetValue(addressCol), CultureInfo.InvariantCulture));
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(address))
                    continue;

                if (!name.StartsWith("DI_", StringComparison.OrdinalIgnoreCase))
                    continue;

                points.Add(new BoolSignalPoint { Name = name, Address = address });
            }

            if (points.Count == 0)
            {
                MessageBox.Show(this, "未找到名称前缀为 DI_ 的数据。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SetPoints(points);
            LogAction?.Invoke($"状态监控导入成功，共 {points.Count} 条。来源: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导入失败: {ex.Message}", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            LogAction?.Invoke($"状态监控导入失败: {ex.Message}");
        }
    }

    private static string NormalizeImportedName(string? rawName)
    {
        string name = rawName?.Trim() ?? string.Empty;
        const string prefix = "HMI_gb";
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            name = name[prefix.Length..];

        return name.TrimStart('_');
    }

    private static string NormalizeImportedAddress(string? rawAddress)
    {
        string address = rawAddress?.Trim() ?? string.Empty;
        if (address.StartsWith('%'))
            address = address[1..];

        return address;
    }

    private async Task HandleRefreshButtonClickAsync()
    {
        if (!_canModify)
            return;

        if (_skipRefreshClickOnce)
        {
            _skipRefreshClickOnce = false;
            return;
        }

        if (_isAutoRefreshing)
        {
            StopAutoRefresh();
            return;
        }

        await RefreshSignalsAsync();
    }

    private void ToggleAutoRefresh()
    {
        if (_isAutoRefreshing)
        {
            StopAutoRefresh();
            return;
        }

        _isAutoRefreshing = true;
        _btnRefresh.Text = "自动刷新中";
        _autoRefreshTimer.Start();
        LogAction?.Invoke("BOOL信号已开启自动刷新。再次点击按钮可停止。");
    }

    private void StopAutoRefresh()
    {
        _isAutoRefreshing = false;
        _autoRefreshTimer.Stop();
        _btnRefresh.Text = "点动刷新";
        LogAction?.Invoke("BOOL信号已切换为点动刷新。");
    }

    private void RemoveCurrentRow()
    {
        if (_grid.SelectedRows.Count == 0)
            return;

        _grid.Rows.RemoveAt(_grid.SelectedRows[0].Index);
        SaveConfigToFile();
    }

    // 点阵显示：以圆形指示灯方式展示当前表格地址状态
    private void ShowMatrixDisplayDialog()
    {
        var points = GetPoints();
        if (points.Count == 0)
        {
            MessageBox.Show(this, "当前没有可显示的点位。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new Form
        {
            Text = "点阵显示",
            Width = 560,
            Height = 680,
            StartPosition = FormStartPosition.CenterParent
        };

        var container = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(10)
        };

        var items = new List<(string name, string address, Panel light)>();
        foreach (var p in points)
        {
            var row = new TableLayoutPanel
            {
                Width = 240,
                Height = 32,
                ColumnCount = 2
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var light = CreateCircleIndicator(Color.DarkGray);
            var lblName = new Label
            {
                Text = string.IsNullOrWhiteSpace(p.Name) ? "(未命名)" : p.Name,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Padding = new Padding(0, 6, 0, 0)
            };

            row.Controls.Add(light, 0, 0);
            row.Controls.Add(lblName, 1, 0);
            container.Controls.Add(row);
            items.Add((p.Name, p.Address, light));
        }

        dialog.Controls.Add(container);

        bool refreshing = false;
        var timer = new System.Windows.Forms.Timer { Interval = 500 };
        timer.Tick += async (_, _) => await RefreshMatrixStatusAsync(items, () => refreshing, v => refreshing = v);
        dialog.Shown += async (_, _) =>
        {
            await RefreshMatrixStatusAsync(items, () => refreshing, v => refreshing = v);
            timer.Start();
        };
        dialog.FormClosed += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
        };

        dialog.ShowDialog(this);
    }

    private static Panel CreateCircleIndicator(Color color)
    {
        var panel = new Panel { Width = 16, Height = 16, BackColor = color, Margin = new Padding(4, 6, 4, 4) };
        panel.Resize += (_, _) =>
        {
            using var path = new GraphicsPath();
            path.AddEllipse(0, 0, panel.Width - 1, panel.Height - 1);
            panel.Region = new Region(path);
        };
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(0, 0, panel.Width - 1, panel.Height - 1);
            panel.Region = new Region(path);
        }
        return panel;
    }

    private async Task RefreshMatrixStatusAsync(
        List<(string name, string address, Panel light)> items,
        Func<bool> isRefreshing,
        Action<bool> setRefreshing)
    {
        if (isRefreshing())
            return;

        setRefreshing(true);
        try
        {
            var plc = PlcProvider?.Invoke() ?? PlcInstance;
            if (plc is not { IsConnected: true })
            {
                foreach (var x in items)
                    x.light.BackColor = Color.DarkGray;
                return;
            }

            foreach (var x in items)
            {
                if (!IsSupportedAddress(x.address))
                {
                    x.light.BackColor = Color.DarkGray;
                    continue;
                }

                try
                {
                    bool value = await plc.ReadBoolAsync(x.address);
                    x.light.BackColor = value ? Color.LimeGreen : Color.Red;
                }
                catch
                {
                    x.light.BackColor = Color.DarkOrange;
                }
            }
        }
        finally
        {
            setRefreshing(false);
        }
    }

    private void LoadConfigFromFile()
    {
        try
        {
            if (!File.Exists(_configFilePath))
                return;

            string json = File.ReadAllText(_configFilePath);
            var points = JsonSerializer.Deserialize<List<BoolSignalPoint>>(json);
            if (points is null)
                return;

            ApplyPoints(points, saveAfterApply: false);
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"加载BOOL信号配置失败: {ex.Message}");
        }
    }

    private void SaveConfigToFile()
    {
        if (_suspendAutoSave)
            return;

        try
        {
            var points = GetPoints();
            string json = JsonSerializer.Serialize(points, JsonFileOptions);
            File.WriteAllText(_configFilePath, json);
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"保存BOOL信号配置失败: {ex.Message}");
        }
    }

    private void MarkAllDisconnected()
    {
        foreach (DataGridViewRow row in _grid.Rows)
            SetSignalRow(row, false, "未连接", Color.DarkGray);
    }

    private static bool IsSupportedAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return false;

        try
        {
            var (type, _, _) = global::PlcConectCom.AddressParser.ParseAddress(address);
            return type is "MX" or "MB";
        }
        catch
        {
            return false;
        }
    }

    private static void SetSignalRow(DataGridViewRow row, bool value, string stateText, Color stateColor)
    {
        row.Cells[2].Value = value ? "1" : "0";
        row.Cells[3].Value = "●";
        row.Cells[3].Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        row.Cells[3].Style.Font = new Font("Segoe UI Symbol", 14f, FontStyle.Bold);
        row.Cells[3].Style.ForeColor = stateColor;
        row.Cells[3].Style.SelectionForeColor = stateColor;
        row.Cells[3].Style.SelectionBackColor = Color.Gainsboro;
        row.Cells[3].ToolTipText = stateText;
    }
}
