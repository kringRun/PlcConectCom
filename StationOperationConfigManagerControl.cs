using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PlcConectCom.WinFormsTest;

/// <summary>工站流程状态 ID 的 PLC 读取类型（与 <see cref="PlcCommunicator"/> 读法一致）。</summary>
public enum StationFlowStatusIdType
{
    /// <summary>16 位有符号（<see cref="PlcCommunicator.ReadIntAsync"/>）。</summary>
    INT = 0,

    /// <summary>16 位无符号 WORD（同一寄存器按无符号解释）。</summary>
    WORD,

    /// <summary>32 位无符号 UINT（同一寄存器按无符号解释）。</summary>
    UINT,

    /// <summary>32 位无符号 DWORD（<see cref="PlcCommunicator.ReadDWordAsync"/>）。</summary>
    DWORD
}

// 工站控件配置模型：用于实例化 StationOperationStatusControl
public sealed class StationOperationConfig
{
    public string StationName { get; set; } = string.Empty;

    public string StartCommandAddress { get; set; } = string.Empty;

    public string PauseCommandAddress { get; set; } = string.Empty;

    public string StopCommandAddress { get; set; } = string.Empty;

    public string ResetCommandAddress { get; set; } = string.Empty;

    public string AutoSignalAddress { get; set; } = string.Empty;

    public string PauseSignalAddress { get; set; } = string.Empty;

    public string InitSignalAddress { get; set; } = string.Empty;

    public string InitDoneSignalAddress { get; set; } = string.Empty;

    public string FlowStatusIdAddress { get; set; } = string.Empty;

    /// <summary>流程 ID 读取类型；默认 INT，与旧版仅 ReadIntAsync 行为一致。</summary>
    public StationFlowStatusIdType FlowStatusIdValueType { get; set; } = StationFlowStatusIdType.INT;

    public string FlowStatusMapFilePath { get; set; } = string.Empty;
}

// 工站配置管理控件：配置/保存/加载 StationOperationStatusControl 所需参数
public sealed class StationOperationConfigManagerControl : UserControl
{
    private const string DefaultConfigFileName = "StationOperation.config.json";
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
    private readonly Button _btnAdd = new() { Text = "新增工站", Width = 84 };
    private readonly Button _btnRemove = new() { Text = "删除工站", Width = 84 };
    private readonly Button _btnSelectMapFile = new() { Text = "选择状态字典", Width = 96 };
    private readonly Button _btnSave = new() { Text = "保存配置", Width = 84 };
    private readonly Button _btnReload = new() { Text = "重新加载", Width = 84 };

    private readonly string _configFilePath;
    private bool _suspendAutoSave;
    private bool _canAddDelete = true;
    private bool _canModify = true;
    private bool _editUnlocked;

    public StationOperationConfigManagerControl()
    {
        _configFilePath = Path.Combine(AppContext.BaseDirectory, DefaultConfigFileName);
        BuildUi();
        BindEvents();
        LoadConfigFromFile();
    }

    public Action<string>? LogAction { get; set; }

    public Func<bool>? EditPermissionRequest { get; set; }

    // 配置变更事件：主页面监听后重建工站控件实例
    public event Action<IReadOnlyList<StationOperationConfig>>? ConfigurationsChanged;

    public IReadOnlyList<StationOperationConfig> GetConfigurations()
    {
        var list = new List<StationOperationConfig>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            string stationName = GetCellString(row, 0);
            if (string.IsNullOrWhiteSpace(stationName))
                continue;

            list.Add(new StationOperationConfig
            {
                StationName = stationName,
                StartCommandAddress = GetCellString(row, 1),
                PauseCommandAddress = GetCellString(row, 2),
                StopCommandAddress = GetCellString(row, 3),
                ResetCommandAddress = GetCellString(row, 4),
                AutoSignalAddress = GetCellString(row, 5),
                PauseSignalAddress = GetCellString(row, 6),
                InitSignalAddress = GetCellString(row, 7),
                InitDoneSignalAddress = GetCellString(row, 8),
                FlowStatusIdAddress = GetCellString(row, 9),
                FlowStatusIdValueType = ParseFlowStatusIdType(row),
                FlowStatusMapFilePath = GetCellString(row, 11)
            });
        }

        return list;
    }

    public void SetConfigurations(IEnumerable<StationOperationConfig> configurations)
    {
        _suspendAutoSave = true;
        _grid.Rows.Clear();
        foreach (var cfg in configurations)
        {
            _grid.Rows.Add(
                cfg.StationName,
                cfg.StartCommandAddress,
                cfg.PauseCommandAddress,
                cfg.StopCommandAddress,
                cfg.ResetCommandAddress,
                cfg.AutoSignalAddress,
                cfg.PauseSignalAddress,
                cfg.InitSignalAddress,
                cfg.InitDoneSignalAddress,
                cfg.FlowStatusIdAddress,
                cfg.FlowStatusIdValueType.ToString(),
                cfg.FlowStatusMapFilePath);
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
        _btnRemove.Enabled = _canAddDelete && _editUnlocked;
        _btnSelectMapFile.Enabled = canModify;
        _btnSave.Enabled = canModify;
        _btnReload.Enabled = canModify;
        _btnEditAuth.Enabled = canModify;
        _btnEditAuth.Text = _editUnlocked ? "编辑锁定" : "编辑解锁";
    }

    private void BuildUi()
    {
        // 表格默认不可编辑，需通过编辑解锁按钮授权
        _grid.ReadOnly = true;
        _grid.DefaultCellStyle.SelectionBackColor = Color.Gainsboro;
        _grid.DefaultCellStyle.SelectionForeColor = Color.Black;
        _grid.RowTemplate.Height = 30;

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "工站名称", FillWeight = 16 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "启动地址", FillWeight = 13 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "暂停地址", FillWeight = 13 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "停止地址", FillWeight = 13 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "复位地址", FillWeight = 13 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "自动中地址", FillWeight = 13 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "暂停中地址", FillWeight = 13 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "初始化中地址", FillWeight = 13 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "初始化完成地址", FillWeight = 13 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "流程ID地址", FillWeight = 12 });
        var colFlowType = new DataGridViewComboBoxColumn
        {
            HeaderText = "流程ID类型",
            FillWeight = 10,
            FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton
        };
        colFlowType.Items.AddRange(Enum.GetNames<StationFlowStatusIdType>().Cast<object>().ToArray());
        _grid.Columns.Add(colFlowType);
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "状态字典文件", FillWeight = 20 });

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 0, 0, 4)
        };

        actions.Controls.AddRange([_btnEditAuth, _btnAdd, _btnRemove, _btnSelectMapFile, _btnSave, _btnReload]);

        Controls.Add(_grid);
        Controls.Add(actions);
    }

    private void BindEvents()
    {
        _btnEditAuth.Click += (_, _) => ToggleEditAuthorization();
        _btnAdd.Click += (_, _) =>
        {
            _grid.Rows.Add("", "", "", "", "", "", "", "", "", "", nameof(StationFlowStatusIdType.INT), "");
            SaveConfigToFile();
            RaiseConfigurationsChanged();
        };
        _btnRemove.Click += (_, _) => RemoveCurrentRow();
        _btnSelectMapFile.Click += (_, _) => SelectMapFileForCurrentRow();
        _btnSave.Click += (_, _) =>
        {
            SaveConfigToFile();
            RaiseConfigurationsChanged();
            LogAction?.Invoke("工站配置已保存。");
        };
        _btnReload.Click += (_, _) => LoadConfigFromFile();
        _grid.CellValueChanged += (_, _) =>
        {
            if (_suspendAutoSave || !_editUnlocked)
                return;

            SaveConfigToFile();
            RaiseConfigurationsChanged();
        };
    }

    private void ToggleEditAuthorization()
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

    private void RemoveCurrentRow()
    {
        if (_grid.CurrentRow is null)
            return;

        _grid.Rows.Remove(_grid.CurrentRow);
        SaveConfigToFile();
        RaiseConfigurationsChanged();
    }

    private void SelectMapFileForCurrentRow()
    {
        if (_grid.CurrentRow is null)
            return;

        using var dialog = new OpenFileDialog
        {
            Title = "选择流程状态字典Excel文件",
            Filter = "Excel 文件 (*.xls;*.xlsx)|*.xls;*.xlsx|所有文件 (*.*)|*.*",
            InitialDirectory = AppContext.BaseDirectory
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        _grid.CurrentRow.Cells[11].Value = dialog.FileName;
        SaveConfigToFile();
        RaiseConfigurationsChanged();
    }

    private void LoadConfigFromFile()
    {
        try
        {
            if (!File.Exists(_configFilePath))
            {
                SaveConfigToFile();
                SetPermission(_canAddDelete, _canModify);
                return;
            }

            string json = File.ReadAllText(_configFilePath);
            var list = JsonSerializer.Deserialize<List<StationOperationConfig>>(json) ?? [];
            SetConfigurations(list);
            LogAction?.Invoke($"工站配置已加载，共 {list.Count} 条。");
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"工站配置加载失败: {ex.Message}");
        }
        finally
        {
            SetPermission(_canAddDelete, _canModify);
        }
    }

    private void SaveConfigToFile()
    {
        if (_suspendAutoSave)
            return;

        try
        {
            string json = JsonSerializer.Serialize(GetConfigurations(), JsonFileOptions);
            File.WriteAllText(_configFilePath, json);
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"工站配置保存失败: {ex.Message}");
        }
    }

    private void RaiseConfigurationsChanged()
    {
        ConfigurationsChanged?.Invoke(GetConfigurations());
    }

    private static string GetCellString(DataGridViewRow row, int columnIndex)
    {
        return (Convert.ToString(row.Cells[columnIndex].Value) ?? string.Empty).Trim();
    }

    private static StationFlowStatusIdType ParseFlowStatusIdType(DataGridViewRow row)
    {
        string s = Convert.ToString(row.Cells[10].Value)?.Trim() ?? string.Empty;
        if (Enum.TryParse<StationFlowStatusIdType>(s, true, out var t))
            return t;
        return StationFlowStatusIdType.INT;
    }
}
