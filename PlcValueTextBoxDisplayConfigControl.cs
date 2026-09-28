using System.Text.Encodings.Web;
using System.Text.Json;

namespace PlcConectCom.WinFormsTest;

public sealed class PlcValueTextBoxDisplayConfigItem
{
    public string DisplayName { get; set; } = string.Empty;
    public string PlcAddress { get; set; } = string.Empty;
    public PlcBindableValueType DataType { get; set; } = PlcBindableValueType.Int;
    public PlcValueTextBoxMode ControlMode { get; set; } = PlcValueTextBoxMode.Display;
    public int StringLength { get; set; } = 64;
    /// <summary>仅当 <see cref="DataType"/> 为 Int 时有效：用下拉框显示数值对应的文字。</summary>
    public bool UseIntComboMapping { get; set; }
    /// <summary>形如「0:无 1:A串 2:B串」的对照说明；数字与 PLC 寄存器中的 Int 一致，冒号后为界面显示文案。</summary>
    public string IntValueMappingDescription { get; set; } = string.Empty;
}

/// <summary>主页面 PlcValueTextBoxControl 显示配置。</summary>
public sealed class PlcValueTextBoxDisplayConfigControl : UserControl
{
    private const string ConfigFileName = "PlcValueTextBoxDisplay.config.json";
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

    private readonly string _configPath;
    private bool _suspendAutoSave;
    private bool _canAddDelete = true;
    private bool _canModify = true;
    private bool _editUnlocked;

    public PlcValueTextBoxDisplayConfigControl() : this(null) { }

    public PlcValueTextBoxDisplayConfigControl(string? storageKey)
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
    public event Action<IReadOnlyList<PlcValueTextBoxDisplayConfigItem>>? ConfigurationsChanged;

    public IReadOnlyList<PlcValueTextBoxDisplayConfigItem> GetConfigurations()
    {
        var list = new List<PlcValueTextBoxDisplayConfigItem>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            string address = GetCellString(row, 1);
            if (string.IsNullOrWhiteSpace(address))
                continue;

            string name = GetCellString(row, 0);
            if (string.IsNullOrWhiteSpace(name))
                name = address;

            var item = new PlcValueTextBoxDisplayConfigItem
            {
                DisplayName = name,
                PlcAddress = address,
                DataType = ParseDataType(GetCellString(row, 2)),
                ControlMode = ParseControlMode(GetCellString(row, 3)),
                StringLength = ParsePositiveInt(GetCellString(row, 4), 64),
                UseIntComboMapping = GetCellBool(row, 5),
                IntValueMappingDescription = GetCellString(row, 6)
            };
            list.Add(item);
        }

        return list;
    }

    public void SetConfigurations(IEnumerable<PlcValueTextBoxDisplayConfigItem> items)
    {
        _suspendAutoSave = true;
        _grid.Rows.Clear();
        foreach (PlcValueTextBoxDisplayConfigItem x in items)
        {
            _grid.Rows.Add(
                x.DisplayName,
                x.PlcAddress,
                x.DataType.ToString(),
                x.ControlMode.ToString(),
                Math.Max(1, x.StringLength).ToString(),
                x.UseIntComboMapping,
                x.IntValueMappingDescription ?? string.Empty);
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
            FillWeight = 18,
            DataSource = Enum.GetNames(typeof(PlcBindableValueType))
        });
        _grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            HeaderText = "控件模式",
            FillWeight = 18,
            DataSource = Enum.GetNames(typeof(PlcValueTextBoxMode))
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "字符串长度", FillWeight = 14 });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            HeaderText = "Int映射下拉",
            FillWeight = 12,
            ThreeState = false
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "数字对照说明", FillWeight = 28 });

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
        AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Padding = new Padding(0, 0, 0, 4)
        };
        toolbar.Controls.AddRange([_btnEditAuth, _btnAdd, _btnDelete, _btnSave, _btnReload]);

        Controls.Add(_grid);
        Controls.Add(toolbar);
    }

    private void BindEvents()
    {
        _btnEditAuth.Click += (_, _) => ToggleEditAuth();
        _btnAdd.Click += (_, _) =>
        {
            _grid.Rows.Add("新文本框", "MW100", PlcBindableValueType.Int.ToString(), PlcValueTextBoxMode.Display.ToString(), "64", false, string.Empty);
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
            LogAction?.Invoke("PlcValueTextBox显示配置已保存。");
        };
        _btnReload.Click += (_, _) => LoadConfig();
        _grid.CellValueChanged += (_, _) =>
        {
            if (_suspendAutoSave || !_editUnlocked)
                return;
            SaveConfig();
            RaiseConfigurationsChanged();
        };
        _grid.EditingControlShowing += (_, e) =>
        {
            if (e.Control is ComboBox box)
            {
                box.DropDownStyle = ComboBoxStyle.DropDownList;
            }
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
            var list = JsonSerializer.Deserialize<List<PlcValueTextBoxDisplayConfigItem>>(json) ?? [];
            SetConfigurations(list);
            LogAction?.Invoke($"PlcValueTextBox显示配置已加载，共 {list.Count} 条。");
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"加载PlcValueTextBox显示配置失败: {ex.Message}");
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
            LogAction?.Invoke($"保存PlcValueTextBox显示配置失败: {ex.Message}");
        }
    }

    private void RaiseConfigurationsChanged()
    {
        ConfigurationsChanged?.Invoke(GetConfigurations());
    }

    private static string GetCellString(DataGridViewRow row, int index)
    {
        return (Convert.ToString(row.Cells[index].Value) ?? string.Empty).Trim();
    }

    private static bool GetCellBool(DataGridViewRow row, int index)
    {
        object? v = row.Cells[index].Value;
        return v is bool b && b;
    }

    private static int ParsePositiveInt(string value, int fallback)
    {
        if (int.TryParse(value, out int parsed))
            return Math.Max(1, parsed);
        return fallback;
    }

    private static PlcBindableValueType ParseDataType(string value)
    {
        if (Enum.TryParse<PlcBindableValueType>(value, true, out var result))
            return result;
        return PlcBindableValueType.Int;
    }

    private static PlcValueTextBoxMode ParseControlMode(string value)
    {
        if (Enum.TryParse<PlcValueTextBoxMode>(value, true, out var result))
            return result;
        return PlcValueTextBoxMode.Display;
    }
}
