using System.Text.Encodings.Web;
using System.Text.Json;
using PlcConectCom.WinForms.Controls;

namespace PlcConectCom.WinFormsTest;

/// <summary>主页面「PLC 按钮显示区」（横向）的单条配置：名称 + PLC 地址 + 数据类型。</summary>
public sealed class HorizontalButtonConfigItem
{
    public string DisplayName { get; set; } = string.Empty;
    public string PlcAddress { get; set; } = string.Empty;
    /// <summary>数据类型（BOOL 时点击置位、松开复位；其余类型为只读显示）。</summary>
    public PlcValueType ValueType { get; set; } = PlcValueType.BOOL;
    /// <summary>BOOL 专用：点击/按住期间写 true、松开写 false（点动自复位）。</summary>
    public bool AutoReset { get; set; } = true;
    /// <summary>
    /// BOOL 专用：勾选后状态刷新时回读 PLC 位实际值更新按钮状态（颜色/True/False 名称），
    /// 不勾选则维持原行为（点动自复位按钮常态显示松开态，普通 BOOL 点击后本地切换）。
    /// </summary>
    public bool ReadBackState { get; set; }
    /// <summary>STRING 类型使用的长度。</summary>
    public int StringLength { get; set; } = 64;
    /// <summary>BOOL 为 true 时按钮上显示的名称（留空则用显示名称）。</summary>
    public string TrueText { get; set; } = "True";
    /// <summary>BOOL 为 false 时按钮上显示的名称（留空则用显示名称）。</summary>
    public string FalseText { get; set; } = "False";
}

/// <summary>
/// 主页面 PLC 显示区内「横向按钮配置」页：配置显示名称 + PLC 地址，
/// 保存后由 Form1 重建到 <c>_mainPlcButtonPanel</c>（横向排列）。
/// </summary>
public sealed class HorizontalButtonConfigControl : UserControl
{
    private const string ConfigFileName = "MainPlcHorizontalButtons.config.json";
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

    private readonly Button _btnEditAuth = new() { Text = "编辑解锁", Width = 84 };
    private readonly Button _btnAdd = new() { Text = "新增", Width = 72 };
    private readonly Button _btnDelete = new() { Text = "删除", Width = 72 };
    private readonly Button _btnSave = new() { Text = "保存", Width = 72 };
    private readonly Button _btnReload = new() { Text = "重载", Width = 72 };
    private readonly Label _hint = new()
    {
        AutoSize = true,
        ForeColor = Color.DimGray,
        Margin = new Padding(12, 6, 0, 0),
        Text = "配置保存后立即重建到主页面「PLC 显示 → PLC 按钮显示区」（横向排列）。BOOL 类型可点击置位，松开复位；「回读状态=是」时按 PLC 位实际值刷新按钮状态；True/False 显示名称留空则沿用「显示名称」。"
    };

    private readonly string _configPath;
    private bool _suspendAutoSave;
    private bool _canAddDelete = true;
    private bool _canModify = true;
    private bool _editUnlocked;

    public HorizontalButtonConfigControl() : this(null) { }

    public HorizontalButtonConfigControl(string? storageKey)
    {
        string fileName = ConfigFileName;
        if (!string.IsNullOrWhiteSpace(storageKey))
        {
            string safeKey = string.Concat(storageKey.Where(char.IsLetterOrDigit));
            if (!string.IsNullOrWhiteSpace(safeKey))
                fileName = $"{Path.GetFileNameWithoutExtension(ConfigFileName)}.{safeKey}{Path.GetExtension(ConfigFileName)}";
        }
        _configPath = Path.Combine(AppContext.BaseDirectory, fileName);
        BuildUi();
        BindEvents();
        LoadConfig();
    }

    public Action<string>? LogAction { get; set; }
    public Func<bool>? EditPermissionRequest { get; set; }
    public event Action<IReadOnlyList<HorizontalButtonConfigItem>>? ConfigurationsChanged;

    public IReadOnlyList<HorizontalButtonConfigItem> GetConfigurations()
    {
        var list = new List<HorizontalButtonConfigItem>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            string address = GetCellString(row, 1);
            if (string.IsNullOrWhiteSpace(address))
                continue;

            string name = GetCellString(row, 0);
            if (string.IsNullOrWhiteSpace(name))
                name = address;

            list.Add(new HorizontalButtonConfigItem
            {
                DisplayName = name,
                PlcAddress = address,
                ValueType = ParseValueType(GetCellString(row, 2)),
                AutoReset = !string.Equals(GetCellString(row, 3), "否", StringComparison.Ordinal),
                ReadBackState = string.Equals(GetCellString(row, 4), "是", StringComparison.Ordinal),
                StringLength = int.TryParse(GetCellString(row, 5), out int len) ? Math.Max(1, len) : 64,
                TrueText = GetCellString(row, 6),
                FalseText = GetCellString(row, 7)
            });
        }

        return list;
    }

    public void SetConfigurations(IEnumerable<HorizontalButtonConfigItem> items)
    {
        _suspendAutoSave = true;
        _grid.Rows.Clear();
        foreach (HorizontalButtonConfigItem x in items)
        {
            _grid.Rows.Add(
                x.DisplayName,
                x.PlcAddress,
                x.ValueType.ToString(),
                x.AutoReset ? "是" : "否",
                x.ReadBackState ? "是" : "否",
                Math.Max(1, x.StringLength).ToString(),
                x.TrueText ?? string.Empty,
                x.FalseText ?? string.Empty);
        }
        _suspendAutoSave = false;
        RaiseConfigurationsChanged();
    }

    public void SetPermission(bool canAddDelete, bool canModify)
    {
        _canAddDelete = canAddDelete;
        _canModify = canModify;
        if (!canModify)
            _editUnlocked = false;

        _grid.ReadOnly = !_editUnlocked;
        _btnAdd.Enabled = _canAddDelete && _editUnlocked;
        _btnDelete.Enabled = _canAddDelete && _editUnlocked;
        _btnSave.Enabled = _canModify && _editUnlocked;
        _btnReload.Enabled = _canModify;
        _btnEditAuth.Enabled = _canModify;
        _btnEditAuth.Text = _editUnlocked ? "编辑锁定" : "编辑解锁";
    }

    private void BuildUi()
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "显示名称", FillWeight = 22 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "PLC地址", FillWeight = 22 });
        _grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            HeaderText = "数据类型",
            FillWeight = 12,
            FlatStyle = FlatStyle.Flat,
            DataSource = Enum.GetNames(typeof(PlcValueType))
        });
        _grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            HeaderText = "点动自复位",
            FillWeight = 11,
            FlatStyle = FlatStyle.Flat,
            DataSource = new[] { "是", "否" }
        });
        _grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            HeaderText = "回读状态",
            FillWeight = 10,
            FlatStyle = FlatStyle.Flat,
            DataSource = new[] { "是", "否" }
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "字符串长度", FillWeight = 9 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "True显示名称", FillWeight = 12 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "False显示名称", FillWeight = 12 });

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Padding = new Padding(0, 0, 0, 4)
        };
        toolbar.Controls.AddRange([_btnEditAuth, _btnAdd, _btnDelete, _btnSave, _btnReload, _hint]);

        Controls.Add(_grid);
        Controls.Add(toolbar);
    }

    private void BindEvents()
    {
        _btnEditAuth.Click += (_, _) => ToggleEditAuth();
        _btnAdd.Click += (_, _) =>
        {
            _grid.Rows.Add("新按钮", "MX0.0", nameof(PlcValueType.BOOL), "是", "否", "64", "True", "False");
            SaveConfig();
            RaiseConfigurationsChanged();
        };
        _btnDelete.Click += (_, _) =>
        {
            if (_grid.SelectedRows.Count == 0)
                return;
            _grid.Rows.RemoveAt(_grid.SelectedRows[0].Index);
            SaveConfig();
            RaiseConfigurationsChanged();
        };
        _btnSave.Click += (_, _) =>
        {
            SaveConfig();
            RaiseConfigurationsChanged();
            LogAction?.Invoke("横向按钮配置已保存。");
        };
        _btnReload.Click += (_, _) => LoadConfig();
        _grid.CellValueChanged += (_, _) =>
        {
            if (_suspendAutoSave || !_editUnlocked)
                return;
            SaveConfig();
            RaiseConfigurationsChanged();
        };

        SetPermission(_canAddDelete, _canModify);
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

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                SaveConfig();
                return;
            }

            string json = File.ReadAllText(_configPath);
            var list = JsonSerializer.Deserialize<List<HorizontalButtonConfigItem>>(json) ?? [];
            SetConfigurations(list);
            LogAction?.Invoke($"横向按钮配置已加载，共 {list.Count} 条。");
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"加载横向按钮配置失败: {ex.Message}");
        }
    }

    private void SaveConfig()
    {
        if (_suspendAutoSave)
            return;

        try
        {
            string json = JsonSerializer.Serialize(GetConfigurations(), JsonFileOptions);
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"保存横向按钮配置失败: {ex.Message}");
        }
    }

    private void RaiseConfigurationsChanged()
    {
        ConfigurationsChanged?.Invoke(GetConfigurations());
    }

    private static PlcValueType ParseValueType(string text)
    {
        return Enum.TryParse(text, ignoreCase: true, out PlcValueType result) ? result : PlcValueType.BOOL;
    }

    private static string GetCellString(DataGridViewRow row, int index)
    {
        return (Convert.ToString(row.Cells[index].Value) ?? string.Empty).Trim();
    }
}
