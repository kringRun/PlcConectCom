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
        WrapContents = false,
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
    private int _controlColumnIndex;
    private bool _suspendSave;
    private bool _canAddDelete = true;
    private bool _canModify = true;
    private bool _editUnlocked;

    public BoolGroupControlTable()
    {
        _configPath = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
        BuildUi();
        BindEvents();
        LoadConfigFromFile();
    }

    public Func<global::PlcConectCom.PlcCommunicator?>? PlcProvider { get; set; }

    public global::PlcConectCom.PlcCommunicator? PlcInstance { get; set; }

    public Action<string>? LogAction { get; set; }

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
        _grid.Columns[0].ReadOnly = !nameAddrEditable;
        _grid.Columns[1].ReadOnly = !nameAddrEditable;
        _grid.Columns[2].ReadOnly = false;
        _grid.Columns[3].ReadOnly = true;
        _grid.ReadOnly = false;
    }

    public void SetItems(IEnumerable<BoolGroupControlItem> items)
    {
        _suspendSave = true;
        _grid.Rows.Clear();
        foreach (var item in items)
            _grid.Rows.Add(item.Name, item.Address, "关闭", string.Empty);
        _suspendSave = false;
        SaveConfigToFile();
    }

    public IReadOnlyList<BoolGroupControlItem> GetItems()
    {
        var result = new List<BoolGroupControlItem>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            string name = Convert.ToString(row.Cells[0].Value)?.Trim() ?? string.Empty;
            string address = Convert.ToString(row.Cells[1].Value)?.Trim() ?? string.Empty;
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
            string address = (Convert.ToString(row.Cells[1].Value) ?? string.Empty).Trim();
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

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "名称", FillWeight = 30 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "地址", FillWeight = 30 });
        _controlColumnIndex = _grid.Columns.Add(new DataGridViewButtonColumn
        {
            HeaderText = "按钮",
            FillWeight = 20,
            Text = "关闭",
            UseColumnTextForButtonValue = false
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "值状态", FillWeight = 20, ReadOnly = true });

        _actions.Controls.AddRange([_btnEditAuth, _btnAdd, _btnRemove, _btnRefresh]);

        _root.RowStyles.Clear();
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        _root.Controls.Add(_actions, 0, 0);
        _root.Controls.Add(_grid, 0, 1);
        Controls.Add(_root);

        ApplyGridColumnReadOnly();
    }

    private void BindEvents()
    {
        _btnEditAuth.Click += (_, _) => ToggleEditAuth();
        _btnAdd.Click += (_, _) =>
        {
            _grid.Rows.Add(string.Empty, string.Empty, "关闭", string.Empty);
            SaveConfigToFile();
        };
        _btnRemove.Click += (_, _) =>
        {
            if (_grid.SelectedRows.Count == 0)
                return;
            _grid.Rows.RemoveAt(_grid.SelectedRows[0].Index);
            SaveConfigToFile();
        };
        _btnRefresh.Click += async (_, _) => await RefreshStatusAsync();
        _grid.CellValueChanged += (_, e) =>
        {
            if (!_editUnlocked)
                return;

            if (e.RowIndex >= 0 && e.ColumnIndex <= 1)
                SaveConfigToFile();
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

        string address = (Convert.ToString(row.Cells[1].Value) ?? string.Empty).Trim();
        if (!IsSupportedAddress(address))
        {
            SetRowState(row, false, "地址无效", Color.DarkGray);
            return;
        }

        try
        {
            bool current = await plc.ReadBoolAsync(address);
            bool next = !current;
            await plc.WriteBoolAsync(address, next);
            SetRowState(row, next, next ? "ON" : "OFF", next ? Color.LawnGreen : Color.Red);
            LogAction?.Invoke($"BOOL写入成功 {address} <= {next}");
        }
        catch (Exception ex)
        {
            SetRowState(row, false, "ERR", Color.DarkOrange);
            LogAction?.Invoke($"BOOL写入失败 {address}: {ex.Message}");
        }
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
        row.Cells[2].Value = state ? "打开" : "关闭";
        row.Cells[2].Style.BackColor = state ? Color.Honeydew : Color.MistyRose;
        row.Cells[2].Style.ForeColor = state ? Color.ForestGreen : Color.Red;
        row.Cells[2].Style.SelectionBackColor = row.Cells[2].Style.BackColor;
        row.Cells[2].Style.SelectionForeColor = row.Cells[2].Style.ForeColor;

        row.Cells[3].Value = statusText;
        row.Cells[3].Style.ForeColor = color;
        row.Cells[3].Style.SelectionForeColor = color;
        row.Cells[3].Style.SelectionBackColor = Color.Gainsboro;
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
                _grid.Rows.Add(item.Name, item.Address, "关闭", string.Empty);
            _suspendSave = false;
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
