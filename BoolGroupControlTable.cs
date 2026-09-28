using System.Text.Encodings.Web;
using System.Text.Json;

namespace PlcConectCom.WinFormsTest;

// BOOL 组表格项（名称 + 地址）
public sealed class BoolGroupControlItem
{
    public string Name { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;
}

// BOOL 组控制表，支持状态刷新与按钮切换写入
public sealed class BoolGroupControlTable : UserControl
{
    private const string ConfigFileName = "BoolGroupControlTable.config.json";
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

    private readonly Button _btnAdd = new() { Text = "新增", Width = 70 };
    private readonly Button _btnRemove = new() { Text = "删除", Width = 70 };
    private readonly Button _btnEditAuth = new() { Text = "编辑解锁", Width = 84 };
    private readonly Button _btnRefresh = new() { Text = "刷新状态", Width = 78 };
    private readonly FlowLayoutPanel _actions = new()
    {
        Dock = DockStyle.Fill,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        WrapContents = true,
        Padding = new Padding(0, 0, 0, 4),
        MinimumSize = new Size(0, 40)
    };

    private readonly TableLayoutPanel _root = new()
    {
        Dock = DockStyle.Fill,
        ColumnCount = 1,
        RowCount = 2,
        Padding = Padding.Empty,
        Margin = Padding.Empty
    };

    private readonly string _configPath;

    // 列顺序：名称 → 值状态 → 地址 → 按钮
    private const int ColName = 0;
    private const int ColStatus = 1;
    private const int ColAddress = 2;
    private const int ColButton = 3;

    private int _controlColumnIndex;
    private bool _suspendSave;
    private bool _canAddDelete = true;
    private bool _canModify = true;
    private bool _editUnlocked;

    public BoolGroupControlTable() : this(null) { }

    public BoolGroupControlTable(string? storageKey)
    {
        _configPath = Path.Combine(AppContext.BaseDirectory, BuildStorageFileName(ConfigFileName, storageKey));
        BuildUi();
        BindEvents();
        LoadConfigFromFile();
    }

    private static string BuildStorageFileName(string fileName, string? storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
            return fileName;
        string safeKey = string.Concat(storageKey.Where(char.IsLetterOrDigit));
        return string.IsNullOrWhiteSpace(safeKey) ? fileName : $"{Path.GetFileNameWithoutExtension(fileName)}.{safeKey}{Path.GetExtension(fileName)}";
    }

    public Func<global::PlcConectCom.PlcCommunicator?>? PlcProvider { get; set; }

    public global::PlcConectCom.PlcCommunicator? PlcInstance { get; set; }

    public Action<string>? LogAction { get; set; }

    /// <summary>设备显示名（主 PLC / 子 PLC 名称），仅用于手动操作日志归属。</summary>
    public string DeviceDisplayName { get; set; } = "主 PLC";

    public Func<bool>? EditPermissionRequest { get; set; }

    /// <summary>将「导入数据」等外部按钮放到与「编辑解锁」同一行的最右侧。</summary>
    public void PlaceImportButtonAtEnd(Control importButton)
    {
        if (importButton.Parent == _actions && _actions.Controls.Count > 0 && ReferenceEquals(_actions.Controls[^1], importButton))
            return;

        importButton.Parent?.Controls.Remove(importButton);
        _actions.Controls.Add(importButton);
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
        _btnEditAuth.Enabled = canModify;
        _btnEditAuth.Text = _editUnlocked ? "编辑锁定" : "编辑解锁";
        ApplyGridColumnReadOnly();
    }

    /// <summary>
    /// 仅「名称」「地址」列随编辑锁定；按钮列保持可点，否则整表 ReadOnly 会导致 DataGridViewButtonColumn 无法触发写入。
    /// </summary>
    private void ApplyGridColumnReadOnly()
    {
        if (_grid.Columns.Count < 4)
            return;

        bool nameAddrEditable = _canModify && _editUnlocked;
        _grid.Columns[ColName].ReadOnly = !nameAddrEditable;
        _grid.Columns[ColStatus].ReadOnly = true;
        _grid.Columns[ColAddress].ReadOnly = !nameAddrEditable;
        _grid.Columns[ColButton].ReadOnly = false;
        _grid.ReadOnly = false;
    }

    public void SetItems(IEnumerable<BoolGroupControlItem> items)
    {
        _suspendSave = true;
        _grid.Rows.Clear();
        foreach (var item in items)
            _grid.Rows.Add(item.Name, string.Empty, item.Address, "关闭");
        _suspendSave = false;
        UpdateColumnWidthsByContent();
        SaveConfigToFile();
    }

    public IReadOnlyList<BoolGroupControlItem> GetItems()
    {
        var result = new List<BoolGroupControlItem>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            string name = Convert.ToString(row.Cells[ColName].Value)?.Trim() ?? string.Empty;
            string address = Convert.ToString(row.Cells[ColAddress].Value)?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(address))
                continue;

            result.Add(new BoolGroupControlItem { Name = name, Address = address });
        }

        return result;
    }

    public async Task RefreshStatusAsync()
    {
        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
        {
            LogAction?.Invoke("请先连接 PLC。");
            foreach (DataGridViewRow row in _grid.Rows)
                SetRowState(row, false, "未连接", Color.DarkGray);
            return;
        }

        foreach (DataGridViewRow row in _grid.Rows)
        {
            string address = (Convert.ToString(row.Cells[ColAddress].Value) ?? string.Empty).Trim();
            if (!IsSupportedAddress(address))
            {
                SetRowState(row, false, "地址无效", Color.DarkGray);
                continue;
            }

            try
            {
                bool state = await plc.ReadBoolAsync(address);
                SetRowState(row, state, state ? "ON" : "OFF", state ? Color.LawnGreen : Color.Red);
            }
            catch (Exception ex)
            {
                SetRowState(row, false, "ERR", Color.DarkOrange);
                LogAction?.Invoke($"读取失败 {address}: {ex.Message}");
            }
        }
    }

    private void BuildUi()
    {
        _grid.DefaultCellStyle.SelectionBackColor = Color.Gainsboro;
        _grid.DefaultCellStyle.SelectionForeColor = Color.Black;
        _grid.RowTemplate.Height = 30;

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "名称", FillWeight = 28 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "值状态", FillWeight = 14, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "地址", FillWeight = 28 });
        _controlColumnIndex = _grid.Columns.Add(new DataGridViewButtonColumn
        {
            HeaderText = "按钮",
            FillWeight = 20,
            Text = "关闭",
            UseColumnTextForButtonValue = false
        });

        // 列宽策略：值状态/地址/按钮按内容实测宽度固定，名称列 Fill 吸收剩余空间，
        // 避免长名称显示不全而其他列大量留白
        _grid.Columns[ColName].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _grid.Columns[ColName].MinimumWidth = 120;
        foreach (int col in new[] { ColStatus, ColAddress, ColButton })
            _grid.Columns[col].AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
        UpdateColumnWidthsByContent();

        _actions.Controls.AddRange([_btnEditAuth, _btnAdd, _btnRemove, _btnRefresh]);

        _root.RowStyles.Clear();
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        _root.Controls.Add(_actions, 0, 0);
        _root.Controls.Add(_grid, 0, 1);
        Controls.Add(_root);

        ApplyGridColumnReadOnly();
    }

    /// <summary>
    /// 按内容自适应列宽：值状态/地址/按钮列取「表头与各行文本测量值」的最大值并加上内边距，
    /// 名称列保持 Fill 吸收剩余宽度，避免长名称被截断而其他列大量留白。
    /// 在数据装载、增删行、名称/地址单元格编辑后调用。
    /// </summary>
    private void UpdateColumnWidthsByContent()
    {
        if (_grid.Columns.Count < 4)
            return;

        _grid.Columns[ColStatus].Width = Math.Max(MeasureColumnRequiredWidth(ColStatus), 64);
        _grid.Columns[ColAddress].Width = Math.Max(MeasureColumnRequiredWidth(ColAddress), 80);
        _grid.Columns[ColButton].Width = Math.Max(MeasureColumnRequiredWidth(ColButton), 96);
    }

    private int MeasureColumnRequiredWidth(int columnIndex)
    {
        var column = _grid.Columns[columnIndex];
        if (column is null)
            return 60;

        var headerFont = _grid.ColumnHeadersDefaultCellStyle.Font ?? _grid.Font;
        int max = TextRenderer.MeasureText(column.HeaderText, headerFont).Width;

        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow)
                continue;

            var cell = row.Cells[columnIndex];
            string text = Convert.ToString(cell.Value) ?? string.Empty;
            if (string.IsNullOrEmpty(text))
                continue;

            var font = cell.InheritedStyle?.Font ?? _grid.Font;
            max = Math.Max(max, TextRenderer.MeasureText(text, font).Width);
        }

        return max + 20; // 单元格左右内边距余量
    }

    private void BindEvents()
    {
        _btnEditAuth.Click += (_, _) => ToggleEditAuth();
        _btnAdd.Click += (_, _) =>
        {
            _grid.Rows.Add(string.Empty, string.Empty, string.Empty, "关闭");
            UpdateColumnWidthsByContent();
            SaveConfigToFile();
        };
        _btnRemove.Click += (_, _) =>
        {
            if (_grid.SelectedRows.Count == 0)
                return;
            _grid.Rows.RemoveAt(_grid.SelectedRows[0].Index);
            UpdateColumnWidthsByContent();
            SaveConfigToFile();
        };
        _btnRefresh.Click += async (_, _) => await RefreshStatusAsync();
        _grid.CellValueChanged += (_, e) =>
        {
            if (!_editUnlocked)
                return;

            if (e.RowIndex >= 0 && (e.ColumnIndex == ColName || e.ColumnIndex == ColAddress))
            {
                UpdateColumnWidthsByContent();
                SaveConfigToFile();
            }
        };

        _grid.CellContentClick += async (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != _controlColumnIndex)
                return;

            // 写 PLC 仅需有修改权限（工程师/管理员），不要求先「编辑解锁」；解锁仅用于改名称/地址
            if (!_canModify)
                return;

            await ToggleRowAsync(_grid.Rows[e.RowIndex]);
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

    private async Task ToggleRowAsync(DataGridViewRow row)
    {
        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
        {
            LogAction?.Invoke("请先连接 PLC。");
            return;
        }

        string address = (Convert.ToString(row.Cells[ColAddress].Value) ?? string.Empty).Trim();
        if (!IsSupportedAddress(address))
        {
            SetRowState(row, false, "地址无效", Color.DarkGray);
            return;
        }

        string name = (Convert.ToString(row.Cells[ColName].Value) ?? string.Empty).Trim();

        try
        {
            bool current = await plc.ReadBoolAsync(address);
            bool next = !current;
            await plc.WriteBoolAsync(address, next);
            SetRowState(row, next, next ? "ON" : "OFF", next ? Color.LawnGreen : Color.Red);
            LogAction?.Invoke($"BOOL写入成功 {address} <= {next}");
            LogManualOutput(name, address, current, next, "点击输出点写入成功");
        }
        catch (Exception ex)
        {
            SetRowState(row, false, "ERR", Color.DarkOrange);
            LogAction?.Invoke($"BOOL写入失败 {address}: {ex.Message}");
            LogManualOutput(name, address, null, null, $"写入失败：{ex.Message}");
        }
    }

    private void LogManualOutput(string name, string address, bool? before, bool? after, string detail)
    {
        ManualOperationLogStore.Write(
            ManualOperationCategory.Output,
            DeviceDisplayName,
            "点击输出点",
            target: string.IsNullOrWhiteSpace(name) ? "(未命名)" : name,
            address: address,
            beforeValue: before is null ? string.Empty : before.Value ? "ON" : "OFF",
            afterValue: after is null ? string.Empty : after.Value ? "ON" : "OFF",
            detail: detail);
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

    private static void SetRowState(DataGridViewRow row, bool state, string statusText, Color color)
    {
        // 名称列不跟状态上色，恢复为继承行/表默认样式
        DataGridViewCell nameCell = row.Cells[ColName];
        nameCell.Style.BackColor = Color.Empty;
        nameCell.Style.ForeColor = Color.Empty;
        nameCell.Style.SelectionBackColor = Color.Empty;
        nameCell.Style.SelectionForeColor = Color.Empty;

        row.Cells[ColButton].Value = state ? "打开" : "关闭";
        row.Cells[ColButton].Style.BackColor = state ? Color.Honeydew : Color.MistyRose;
        row.Cells[ColButton].Style.ForeColor = state ? Color.ForestGreen : Color.Red;
        row.Cells[ColButton].Style.SelectionBackColor = row.Cells[ColButton].Style.BackColor;
        row.Cells[ColButton].Style.SelectionForeColor = row.Cells[ColButton].Style.ForeColor;

        row.Cells[ColStatus].Value = statusText;
        row.Cells[ColStatus].Style.ForeColor = color;
        row.Cells[ColStatus].Style.SelectionForeColor = color;
        row.Cells[ColStatus].Style.SelectionBackColor = Color.Gainsboro;
    }

    private void LoadConfigFromFile()
    {
        try
        {
            if (!File.Exists(_configPath))
                return;

            string json = File.ReadAllText(_configPath);
            var items = JsonSerializer.Deserialize<List<BoolGroupControlItem>>(json);
            if (items is null)
                return;

            _suspendSave = true;
            _grid.Rows.Clear();
            foreach (var item in items)
                _grid.Rows.Add(item.Name, string.Empty, item.Address, "关闭");
            _suspendSave = false;
            UpdateColumnWidthsByContent();
        }
        catch (Exception ex)
        {
            _suspendSave = false;
            LogAction?.Invoke($"加载BOOL组配置失败: {ex.Message}");
        }
    }

    private void SaveConfigToFile()
    {
        if (_suspendSave)
            return;

        try
        {
            var items = GetItems();
            string json = JsonSerializer.Serialize(items, JsonFileOptions);
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"保存BOOL组配置失败: {ex.Message}");
        }
    }
}
