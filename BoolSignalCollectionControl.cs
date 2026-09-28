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

    public string Comment { get; set; } = string.Empty;
}

// BOOL 信号集合监控控件，支持点动刷新与自动刷新
public sealed class BoolSignalCollectionControl : UserControl
{
    private sealed class BoolReadRequest
    {
        public required DataGridViewRow Row { get; init; }
        public required string Address { get; init; }
        public int StartBit { get; init; }
        public int BitCount { get; init; }
        public bool AnyBitTrue { get; init; }
    }

    private sealed class BoolReadBatch
    {
        public int StartBit { get; init; }
        public int EndBit { get; init; }
        public required List<BoolReadRequest> Requests { get; init; }
    }

    private const string DefaultConfigFileName = "BoolSignalCollection.config.json";
    private const int MaxBatchBitCount = 1024;
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
        AllowUserToResizeColumns = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
    };
    private readonly DataGridView _outputGrid = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        ReadOnly = true, AllowUserToResizeColumns = false, RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
    };
    private readonly TabControl _signalTabs = new() { Dock = DockStyle.Fill };

    private readonly Button _btnAdd = new() { Text = "新增点", Width = 72 };
    private readonly Button _btnRemove = new() { Text = "删除点", Width = 72 };
    private readonly Button _btnEditAuth = new() { Text = "编辑解锁", Width = 84 };
    private readonly Button _btnImportData = new() { Text = "导入数据", Width = 84 };
    private readonly Button _btnRefresh = new() { Text = "点动刷新", Width = 92 };
    private readonly Button _btnMatrixDisplay = new() { Text = "点阵显示", Width = 84 };
    private readonly Button _btnImportOutput = new() { Text = "导入输出点", Width = 92 };
    private readonly System.Windows.Forms.Timer _autoRefreshTimer = new() { Interval = 1000 };
    private readonly string _configFilePath;
    private readonly string _outputConfigFilePath;
    private bool _suspendAutoSave;
    private bool _isAutoRefreshing;
    private bool _isRefreshing;
    private bool _skipRefreshClickOnce;
    private bool _canAddDelete = true;
    private bool _canModify = true;
    private bool _editUnlocked;

    public BoolSignalCollectionControl() : this(null) { }

    public BoolSignalCollectionControl(string? storageKey)
    {
        string fileName = DefaultConfigFileName;
        if (!string.IsNullOrWhiteSpace(storageKey))
        {
            string safeKey = string.Concat(storageKey.Where(char.IsLetterOrDigit));
            if (!string.IsNullOrWhiteSpace(safeKey))
            {
                fileName = $"{Path.GetFileNameWithoutExtension(DefaultConfigFileName)}.{safeKey}{Path.GetExtension(DefaultConfigFileName)}";
                _outputConfigFilePath = Path.Combine(AppContext.BaseDirectory, $"BoolSignalOutputCollection.{safeKey}.config.json");
            }
        }
        _outputConfigFilePath ??= Path.Combine(AppContext.BaseDirectory, "BoolSignalOutputCollection.config.json");
        _configFilePath = Path.Combine(AppContext.BaseDirectory, fileName);
        BuildUi();
        BindEvents();
        LoadConfigFromFile();
        LoadOutputConfig();
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
            _grid.Rows.Add(point.Name, point.Address, point.Comment, string.Empty, string.Empty);
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
            string comment = Convert.ToString(row.Cells[2].Value) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(address))
                continue;

            points.Add(new BoolSignalPoint
            {
                Name = name.Trim(),
                Address = address.Trim(),
                Comment = comment.Trim()
            });
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
            foreach (DataGridViewRow row in _outputGrid.Rows)
            {
                row.Cells[2].Value = string.Empty;
                row.Cells[3].Value = "未连接";
                row.Cells[3].Style.ForeColor = Color.DarkGray;
            }
            _isRefreshing = false;
            return;
        }

        try
        {
            var requests = new List<BoolReadRequest>();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                string address = (Convert.ToString(row.Cells[1].Value) ?? string.Empty).Trim();
                if (!IsSupportedAddress(address))
                {
                    SetSignalRow(row, false, "地址无效", Color.DarkGray);
                    continue;
                }

                if (TryBuildBoolReadRequest(row, address, out var request))
                {
                    requests.Add(request);
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

            foreach (var batch in BuildReadBatches(requests))
            {
                bool[] values;
                string startAddress = ToMxAddress(batch.StartBit);
                int count = batch.EndBit - batch.StartBit + 1;
                try
                {
                    values = await plc.ReadBoolArrayAsync(startAddress, count);
                }
                catch (Exception ex)
                {
                    foreach (var req in batch.Requests)
                        SetSignalRow(req.Row, false, "ERR", Color.DarkOrange);
                    LogAction?.Invoke($"批量读取失败 {startAddress} +{count}bit: {ex.Message}");
                    continue;
                }

                foreach (var req in batch.Requests)
                {
                    int offset = req.StartBit - batch.StartBit;
                    bool value;
                    if (req.AnyBitTrue)
                    {
                        value = false;
                        int end = offset + req.BitCount;
                        for (int i = offset; i < end && i < values.Length; i++)
                        {
                            if (!values[i])
                                continue;
                            value = true;
                            break;
                        }
                    }
                    else
                    {
                        value = offset >= 0 && offset < values.Length && values[offset];
                    }

                    SetSignalRow(req.Row, value, value ? "ON" : "OFF", value ? Color.LawnGreen : Color.Red);
                }
            }

            await RefreshOutputSignalsAsync(plc);
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private static bool TryBuildBoolReadRequest(DataGridViewRow row, string address, out BoolReadRequest request)
    {
        request = null!;
        try
        {
            var (type, offset, bit) = global::PlcConectCom.AddressParser.ParseAddress(address);
            if (type == "MX")
            {
                request = new BoolReadRequest
                {
                    Row = row,
                    Address = address,
                    StartBit = checked(offset * 8 + bit),
                    BitCount = 1,
                    AnyBitTrue = false
                };
                return true;
            }

            if (type == "MB")
            {
                request = new BoolReadRequest
                {
                    Row = row,
                    Address = address,
                    StartBit = checked(offset * 8),
                    BitCount = 8,
                    AnyBitTrue = true
                };
                return true;
            }
        }
        catch
        {
            // Fallback to single-point read path.
        }

        return false;
    }

    private static List<BoolReadBatch> BuildReadBatches(List<BoolReadRequest> requests)
    {
        var batches = new List<BoolReadBatch>();
        if (requests.Count == 0)
            return batches;

        var ordered = requests.OrderBy(x => x.StartBit).ToList();
        int cursor = 0;
        while (cursor < ordered.Count)
        {
            int start = ordered[cursor].StartBit;
            int end = ordered[cursor].StartBit + ordered[cursor].BitCount - 1;
            var bucket = new List<BoolReadRequest> { ordered[cursor] };
            cursor++;

            while (cursor < ordered.Count)
            {
                var next = ordered[cursor];
                int nextEnd = next.StartBit + next.BitCount - 1;
                int mergedEnd = Math.Max(end, nextEnd);
                int mergedCount = mergedEnd - start + 1;
                if (mergedCount > MaxBatchBitCount)
                    break;

                end = mergedEnd;
                bucket.Add(next);
                cursor++;
            }

            batches.Add(new BoolReadBatch
            {
                StartBit = start,
                EndBit = end,
                Requests = bucket
            });
        }

        return batches;
    }

    private static string ToMxAddress(int absoluteBit)
    {
        int byteOffset = absoluteBit / 8;
        int bit = absoluteBit % 8;
        return $"MX{byteOffset}.{bit}";
    }

    private void BuildUi()
    {
        _grid.DefaultCellStyle.SelectionBackColor = Color.Gainsboro;
        _grid.DefaultCellStyle.SelectionForeColor = Color.Black;
        _grid.RowTemplate.Height = 30;

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "点名称", FillWeight = 30 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "地址(MX/MB)", FillWeight = 30 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "注释", FillWeight = 30 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "值", FillWeight = 15, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "状态", FillWeight = 15, ReadOnly = true });

        _outputGrid.DefaultCellStyle.SelectionBackColor = Color.Gainsboro;
        _outputGrid.DefaultCellStyle.SelectionForeColor = Color.Black;
        _outputGrid.RowTemplate.Height = 30;
        _outputGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "点名称", ReadOnly = true });
        _outputGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "地址(MX/MB)", ReadOnly = true });
        _outputGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "值", ReadOnly = true });
        _outputGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "状态", ReadOnly = true });
        var inputPage = new TabPage("输入状态") { Padding = new Padding(2) };
        inputPage.Controls.Add(_grid);
        var outputPage = new TabPage("输出状态") { Padding = new Padding(2) };
        var outputPanel = new Panel { Dock = DockStyle.Fill };
        var outputToolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(4) };
        outputToolbar.Controls.Add(_btnImportOutput);
        outputPanel.Controls.Add(_outputGrid);
        outputPanel.Controls.Add(outputToolbar);
        outputPage.Controls.Add(outputPanel);
        _signalTabs.TabPages.Add(inputPage);
        _signalTabs.TabPages.Add(outputPage);

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 0, 0, 4)
        };

        buttonPanel.Controls.AddRange([_btnEditAuth, _btnImportData, _btnAdd, _btnRemove, _btnRefresh, _btnMatrixDisplay]);

        Controls.Add(_signalTabs);
        Controls.Add(buttonPanel);
    }

    private void LoadOutputConfig()
    {
        try
        {
            if (!File.Exists(_outputConfigFilePath)) return;
            var points = JsonSerializer.Deserialize<List<BoolSignalPoint>>(File.ReadAllText(_outputConfigFilePath), JsonFileOptions) ?? [];
            foreach (var point in points)
                _outputGrid.Rows.Add(point.Name, point.Address, string.Empty, string.Empty);
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"输出点位配置读取失败: {ex.Message}");
        }
    }

    private async Task RefreshOutputSignalsAsync(global::PlcConectCom.PlcCommunicator plc)
    {
        foreach (DataGridViewRow row in _outputGrid.Rows)
        {
            string address = (Convert.ToString(row.Cells[1].Value) ?? string.Empty).Trim();
            if (!IsSupportedAddress(address))
            {
                row.Cells[2].Value = string.Empty;
                row.Cells[3].Value = "地址无效";
                continue;
            }
            try
            {
                bool value = await plc.ReadBoolAsync(address);
                row.Cells[2].Value = value ? "1" : "0";
                row.Cells[3].Value = value ? "ON" : "OFF";
                row.Cells[3].Style.ForeColor = value ? Color.ForestGreen : Color.Red;
            }
            catch
            {
                row.Cells[2].Value = string.Empty;
                row.Cells[3].Value = "ERR";
                row.Cells[3].Style.ForeColor = Color.DarkOrange;
            }
        }
    }

    private void BindEvents()
    {
        _btnEditAuth.Click += (_, _) => ToggleEditAuth();
        _btnAdd.Click += (_, _) =>
        {
            _grid.Rows.Add("", "", "", "", "");
            SaveConfigToFile();
        };
        _btnImportData.Click += (_, _) => ImportPointsFromExcel();
        _btnImportOutput.Click += (_, _) => ImportOutputPointsFromExcel();
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

    private void ImportOutputPointsFromExcel()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择输出状态Excel文件",
            Filter = "Excel 文件 (*.xls;*.xlsx)|*.xls;*.xlsx|所有文件 (*.*)|*.*",
            InitialDirectory = AppContext.BaseDirectory
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            using var stream = File.Open(dialog.FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = ExcelReaderFactory.CreateReader(stream);
            var points = new List<BoolSignalPoint>();
            bool isHeader = true;
            int nameCol = 1, addressCol = 2, commentCol = -1;
            while (reader.Read())
            {
                if (isHeader)
                {
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        string header = Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                        if (string.Equals(header, "Name", StringComparison.OrdinalIgnoreCase) || header.Contains("名称")) nameCol = i;
                        else if (string.Equals(header, "Address", StringComparison.OrdinalIgnoreCase) || header.Contains("地址")) addressCol = i;
                        else if (string.Equals(header, "Comment", StringComparison.OrdinalIgnoreCase) || header.Contains("注释")) commentCol = i;
                    }
                    isHeader = false;
                    continue;
                }

                if (nameCol >= reader.FieldCount || addressCol >= reader.FieldCount) continue;
                string name = NormalizeImportedName(Convert.ToString(reader.GetValue(nameCol), CultureInfo.InvariantCulture));
                string address = NormalizeImportedAddress(Convert.ToString(reader.GetValue(addressCol), CultureInfo.InvariantCulture));
                string comment = commentCol >= 0 && commentCol < reader.FieldCount
                    ? Convert.ToString(reader.GetValue(commentCol), CultureInfo.InvariantCulture)?.Trim() ?? string.Empty : string.Empty;
                if (name.StartsWith("DO", StringComparison.OrdinalIgnoreCase) && IsSupportedAddress(address))
                    points.Add(new BoolSignalPoint { Name = name, Address = address, Comment = comment });
            }

            if (points.Count == 0)
            {
                MessageBox.Show(this, "Excel中没有找到有效的输出点位（需包含点名称和MX/MB地址）。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            File.WriteAllText(_outputConfigFilePath, JsonSerializer.Serialize(points, JsonFileOptions));
            _outputGrid.Rows.Clear();
            foreach (var point in points) _outputGrid.Rows.Add(point.Name, point.Address, string.Empty, string.Empty);
            _signalTabs.SelectedIndex = 1;
            if ((PlcProvider?.Invoke() ?? PlcInstance) is { IsConnected: true } plc)
                _ = RefreshOutputSignalsAsync(plc);
            LogAction?.Invoke($"输出状态导入成功，共 {points.Count} 条。来源: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导入失败: {ex.Message}", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            LogAction?.Invoke($"输出状态导入失败: {ex.Message}");
        }
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
            int commentCol = -1;

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
                        else if (string.Equals(header, "Comment", StringComparison.OrdinalIgnoreCase))
                            commentCol = i;
                    }

                    isHeader = false;
                    continue;
                }

                string name = NormalizeImportedName(Convert.ToString(reader.GetValue(nameCol), CultureInfo.InvariantCulture));
                string address = NormalizeImportedAddress(Convert.ToString(reader.GetValue(addressCol), CultureInfo.InvariantCulture));
                string comment = commentCol >= 0
                    ? (Convert.ToString(reader.GetValue(commentCol), CultureInfo.InvariantCulture)?.Trim() ?? string.Empty)
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(address))
                    continue;

                if (!name.StartsWith("DI_", StringComparison.OrdinalIgnoreCase))
                    continue;

                points.Add(new BoolSignalPoint { Name = name, Address = address, Comment = comment });
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

        var dialog = new Form
        {
            Text = "点阵显示",
            Width = 960,
            Height = 680,
            StartPosition = FormStartPosition.CenterParent,
            TopMost = false,
            ShowInTaskbar = true,
            MinimizeBox = true
        };

        Font nameFont = new("Microsoft YaHei UI", 9F, FontStyle.Regular);
        int maxNameWidth = points
            .Select(BuildMatrixDisplayName)
            .DefaultIfEmpty(string.Empty)
            .Max(name => TextRenderer.MeasureText(name, nameFont).Width);
        int preferredItemWidth = Math.Clamp(maxNameWidth + 44, 240, 480);
        var nameTips = new ToolTip { ShowAlways = true };

        var container = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true
        };
        var matrix = new TableLayoutPanel
        {
            Dock = DockStyle.None,
            AutoSize = false,
            Padding = new Padding(10),
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize
        };

        var items = new List<(string name, string address, Panel light)>();
        var tiles = new List<TableLayoutPanel>();
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Height = 32,
                ColumnCount = 2,
                Margin = new Padding(2)
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var light = CreateCircleIndicator(Color.DarkGray);
            var lblName = new Label
            {
                Text = BuildMatrixDisplayName(p),
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = nameFont,
                Padding = new Padding(0, 3, 0, 0)
            };
            nameTips.SetToolTip(lblName, lblName.Text);

            row.Controls.Add(light, 0, 0);
            row.Controls.Add(lblName, 1, 0);
            tiles.Add(row);
            items.Add((p.Name, p.Address, light));
        }

        container.Controls.Add(matrix);
        dialog.Controls.Add(container);

        bool arranging = false;
        int arrangedWidth = -1;
        void ArrangeMatrix()
        {
            if (arranging || container.IsDisposed || container.ClientSize.Width < 60) return;
            // 始终预留纵向滚动条宽度，避免滚动条出现后触发列数来回变化。
            int width = Math.Max(40, container.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2);
            if (width == arrangedWidth) return;
            arranging = true;
            matrix.SuspendLayout();
            try
            {
                int innerWidth = Math.Max(1, width - matrix.Padding.Horizontal);
                int columnCount = Math.Max(1, Math.Min(points.Count, innerWidth / preferredItemWidth));
                int rowCount = (int)Math.Ceiling(points.Count / (double)columnCount);
                int itemWidth = Math.Max(1, innerWidth / columnCount);
                var rowHeights = Enumerable.Repeat(34, rowCount).ToArray();
                for (int i = 0; i < tiles.Count; i++)
                {
                    int textWidth = Math.Max(1, itemWidth - 44);
                    int textHeight = TextRenderer.MeasureText(BuildMatrixDisplayName(points[i]), nameFont,
                        new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
                    rowHeights[i % rowCount] = Math.Max(rowHeights[i % rowCount], textHeight + 14);
                }

                matrix.Controls.Clear();
                matrix.ColumnStyles.Clear();
                matrix.RowStyles.Clear();
                matrix.ColumnCount = columnCount;
                matrix.RowCount = rowCount;
                for (int c = 0; c < columnCount; c++)
                    matrix.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, itemWidth));
                foreach (int height in rowHeights)
                    matrix.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
                for (int i = 0; i < tiles.Count; i++)
                    matrix.Controls.Add(tiles[i], i / rowCount, i % rowCount);

                int heightTotal = matrix.Padding.Vertical + rowHeights.Sum();
                matrix.Size = new Size(width, heightTotal);
                container.AutoScrollMinSize = new Size(0, heightTotal);
                arrangedWidth = width;
            }
            finally
            {
                matrix.ResumeLayout(true);
                arranging = false;
            }
        }
        container.ClientSizeChanged += (_, _) => ArrangeMatrix();
        ArrangeMatrix();
        dialog.Disposed += (_, _) => { nameTips.Dispose(); nameFont.Dispose(); };

        bool refreshing = false;
        var timer = new System.Windows.Forms.Timer { Interval = 5 };
        timer.Tick += async (_, _) => await RefreshMatrixStatusAsync(items, () => refreshing, v => refreshing = v);
        dialog.Shown += async (_, _) =>
        {
            ArrangeMatrix();
            await RefreshMatrixStatusAsync(items, () => refreshing, v => refreshing = v);
            if (!dialog.IsDisposed && !dialog.Disposing) timer.Start();
        };
        dialog.FormClosed += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
        };

        // 非模态显示：可置于其他窗口之下，不阻塞主界面与其它页面打开
        DataGridViewNoSortHelper.WireControlSubtreeForNoColumnSort(dialog);
        Form? owner = FindForm();
        if (owner is not null && !owner.IsDisposed)
            dialog.Show(owner);
        else
            dialog.Show();
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
        row.Cells[3].Value = value ? "1" : "0";
        row.Cells[4].Value = "●";
        row.Cells[4].Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        row.Cells[4].Style.Font = new Font("Segoe UI Symbol", 14f, FontStyle.Bold);
        row.Cells[4].Style.ForeColor = stateColor;
        row.Cells[4].Style.SelectionForeColor = stateColor;
        row.Cells[4].Style.SelectionBackColor = Color.Gainsboro;
        row.Cells[4].ToolTipText = stateText;
    }

    private static string BuildMatrixDisplayName(BoolSignalPoint point)
    {
        string name = point.Name?.Trim() ?? string.Empty;
        string comment = point.Comment?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(comment))
            return $"{name}_{comment}";
        if (!string.IsNullOrWhiteSpace(name))
            return name;
        if (!string.IsNullOrWhiteSpace(comment))
            return comment;
        return "(未命名)";
    }
}
