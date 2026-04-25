using System.Text.Encodings.Web;
using System.Text.Json;

namespace PlcConectCom.WinFormsTest;

public enum ButtonDisplayControlType
{
    PLC开关,
    PLC绑定按钮
}

public sealed class ButtonDisplayConfigItem
{
    public string DisplayName { get; set; } = string.Empty;

    public ButtonDisplayControlType ControlType { get; set; } = ButtonDisplayControlType.PLC开关;

    public string PlcAddress { get; set; } = string.Empty;

    public PlcValueType ValueType { get; set; } = PlcValueType.BOOL;

    public int StringLength { get; set; } = 64;

    public bool AutoReset { get; set; }
}

/// <summary>主页面按钮显示配置。</summary>
public sealed class ButtonDisplayConfigControl : UserControl
{
    private const string ConfigFileName = "ButtonDisplay.config.json";
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

    public ButtonDisplayConfigControl()
    {
        _configPath = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
        BuildUi();
        BindEvents();
        LoadConfig();
    }

    public Action<string>? LogAction { get; set; }

    public Func<bool>? EditPermissionRequest { get; set; }

    public event Action<IReadOnlyList<ButtonDisplayConfigItem>>? ConfigurationsChanged;

    public IReadOnlyList<ButtonDisplayConfigItem> GetConfigurations()
    {
        var list = new List<ButtonDisplayConfigItem>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            string address = GetCellString(row, 2);
            if (string.IsNullOrWhiteSpace(address))
                continue;

            string name = GetCellString(row, 0);
            if (string.IsNullOrWhiteSpace(name))
                name = address;

            if (!Enum.TryParse(GetCellString(row, 1), true, out ButtonDisplayControlType controlType))
                controlType = ButtonDisplayControlType.PLC开关;
            if (!Enum.TryParse(GetCellString(row, 3), true, out PlcValueType valueType))
                valueType = PlcValueType.BOOL;

            int stringLength = 64;
            _ = int.TryParse(GetCellString(row, 4), out stringLength);
            stringLength = Math.Clamp(stringLength, 1, 512);
            bool autoReset = ParseBoolCell(GetCellString(row, 5));

            list.Add(new ButtonDisplayConfigItem
            {
                DisplayName = name,
                ControlType = controlType,
                PlcAddress = address,
                ValueType = valueType,
                StringLength = stringLength,
                AutoReset = autoReset
            });
        }

        return list;
    }

    public void SetConfigurations(IEnumerable<ButtonDisplayConfigItem> items)
    {
        _suspendAutoSave = true;
        _grid.Rows.Clear();
        foreach (ButtonDisplayConfigItem x in items)
            _grid.Rows.Add(x.DisplayName, x.ControlType.ToString(), x.PlcAddress, x.ValueType.ToString(), x.StringLength.ToString(), x.AutoReset);
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
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "显示名称", FillWeight = 20 });

        var colType = new DataGridViewComboBoxColumn
        {
            HeaderText = "控件类型",
            FillWeight = 16,
            FlatStyle = FlatStyle.Flat
        };
        colType.Items.AddRange(Enum.GetNames<ButtonDisplayControlType>());
        _grid.Columns.Add(colType);

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "PLC地址", FillWeight = 23 });

        var colValueType = new DataGridViewComboBoxColumn
        {
            HeaderText = "数据类型",
            FillWeight = 14,
            FlatStyle = FlatStyle.Flat
        };
        colValueType.Items.AddRange(Enum.GetNames<PlcValueType>());
        _grid.Columns.Add(colValueType);

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "字符串长度", FillWeight = 11 });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "是否自复位", FillWeight = 10 });

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
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
            _grid.Rows.Add("新按钮", ButtonDisplayControlType.PLC开关.ToString(), "MB0", PlcValueType.BOOL.ToString(), "64", false);
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
            LogAction?.Invoke("按钮显示配置已保存。");
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
            var list = JsonSerializer.Deserialize<List<ButtonDisplayConfigItem>>(json) ?? [];
            SetConfigurations(list);
            LogAction?.Invoke($"按钮显示配置已加载，共 {list.Count} 条。");
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"加载按钮显示配置失败: {ex.Message}");
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
            LogAction?.Invoke($"保存按钮显示配置失败: {ex.Message}");
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

    private static bool ParseBoolCell(string text)
    {
        if (bool.TryParse(text, out bool value))
            return value;

        return string.Equals(text, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "是", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "Y", StringComparison.OrdinalIgnoreCase);
    }
}

