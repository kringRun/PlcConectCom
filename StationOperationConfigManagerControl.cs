using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ExcelDataReader;

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

    /// <summary>工站是否屏蔽（勾选为屏蔽）；状态写入 <see cref="StationEnabledAddress"/>。</summary>
    public bool StationEnabled { get; set; } = true;

    /// <summary>工站屏蔽 BOOL 地址（勾选写入 true，取消写入 false）。</summary>
    public string StationEnabledAddress { get; set; } = string.Empty;

    public string StartCommandAddress { get; set; } = string.Empty;

    public string PauseCommandAddress { get; set; } = string.Empty;

    public string StopCommandAddress { get; set; } = string.Empty;

    public string ResetCommandAddress { get; set; } = string.Empty;

    public string AutoSignalAddress { get; set; } = string.Empty;

    public string PauseSignalAddress { get; set; } = string.Empty;

    public string InitSignalAddress { get; set; } = string.Empty;

    public string InitDoneSignalAddress { get; set; } = string.Empty;

    /// <summary>工站报警（BOOL 地址）。</summary>
    public string StationAlarmAddress { get; set; } = string.Empty;

    /// <summary>初始化超时报警（BOOL 地址）。</summary>
    public string InitTimeoutAlarmAddress { get; set; } = string.Empty;

    /// <summary>初始化安全条件（BOOL 地址）。</summary>
    public string InitSafetyConditionAddress { get; set; } = string.Empty;

    /// <summary>初始化安全条件不满足报警（BOOL 地址）。</summary>
    public string InitSafetyNotMetAlarmAddress { get; set; } = string.Empty;

    /// <summary>单次 CT（REAL 地址）。</summary>
    public string SingleCycleTimeAddress { get; set; } = string.Empty;

    public string FlowStatusIdAddress { get; set; } = string.Empty;

    /// <summary>流程 ID 读取类型；默认 INT，与旧版仅 ReadIntAsync 行为一致。</summary>
    public StationFlowStatusIdType FlowStatusIdValueType { get; set; } = StationFlowStatusIdType.INT;

    public string FlowStatusMapFilePath { get; set; } = string.Empty;
}

// 工站配置管理控件：配置/保存/加载 StationOperationStatusControl 所需参数
public sealed class StationOperationConfigManagerControl : UserControl
{
    private const int ColStationName = 0;
    private const int ColStationEnabled = 1;
    private const int ColStationEnabledAddr = 2;
    private const int ColStartCmd = 3;
    private const int ColPauseCmd = 4;
    private const int ColStopCmd = 5;
    private const int ColResetCmd = 6;
    private const int ColAutoSig = 7;
    private const int ColPauseSig = 8;
    private const int ColInitSig = 9;
    private const int ColInitDoneSig = 10;
    private const int ColStationAlarm = 11;
    private const int ColInitTimeoutAlarm = 12;
    private const int ColInitSafetyCond = 13;
    private const int ColInitSafetyNotMetAlarm = 14;
    private const int ColSingleCt = 15;
    private const int ColFlowStatusId = 16;
    private const int ColFlowStatusIdType = 17;
    private const int ColMapFile = 18;

    private const string DefaultConfigFileName = "StationOperation.config.json";

    // ===== 导入Excel 的地址自动分配规则（依据 PLC st_Uint 结构体，预留 20 个工站）=====
    // st_Uint[i] 起始 = MB(1500 + 20*i)，工站号取 Excel 的 InitValue 列。
    // 结构体成员偏移：启动+0 暂停+1 停止+2 复位+3 自动中+4 暂停中+5 初始化中+6
    //                初始化完成+7 工站报警+9 口工站屏蔽+8 初始化超时报警+10
    //                初始化安全条件不足报警+11 初始化条件不满足报警+12
    //                自动状态信息(INT)+14 单次循环时间(REAL)+16
    // 注：工站报警(+9)与口工站屏蔽(+8)按用户手填行分配（与结构体声明顺序相反）。
    private const int StationBaseAddress = 1500;
    private const int StationAddressStride = 20;
    private const int StationCapacity = 20;
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
        AllowUserToResizeColumns = true,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
        ScrollBars = ScrollBars.Both,
        ShowCellToolTips = true
    };

    private readonly Button _btnEditAuth = new() { Text = "编辑解锁", Width = 84 };
    private readonly Button _btnAdd = new() { Text = "新增工站", Width = 84 };
    private readonly Button _btnRemove = new() { Text = "删除工站", Width = 84 };
    private readonly Button _btnSelectMapFile = new() { Text = "选择状态字典", Width = 96 };
    private readonly Button _btnSave = new() { Text = "保存配置", Width = 84 };
    private readonly Button _btnReload = new() { Text = "重新加载", Width = 84 };
    private readonly Button _btnImportExcel = new() { Text = "导入Excel", Width = 84 };

    private readonly string _configFilePath;
    private bool _suspendAutoSave;
    private bool _canAddDelete = true;
    private bool _canModify = true;
    private bool _editUnlocked;

    public StationOperationConfigManagerControl() : this(null) { }

    public StationOperationConfigManagerControl(string? storageKey)
    {
        string fileName = DefaultConfigFileName;
        if (!string.IsNullOrWhiteSpace(storageKey))
        {
            string safeKey = string.Concat(storageKey.Where(char.IsLetterOrDigit));
            if (!string.IsNullOrWhiteSpace(safeKey))
                fileName = $"{Path.GetFileNameWithoutExtension(DefaultConfigFileName)}.{safeKey}{Path.GetExtension(DefaultConfigFileName)}";
        }
        _configFilePath = Path.Combine(AppContext.BaseDirectory, fileName);
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
            string stationName = GetCellString(row, ColStationName);
            if (string.IsNullOrWhiteSpace(stationName))
                continue;

            list.Add(new StationOperationConfig
            {
                StationName = stationName,
                StationEnabled = GetCellBool(row, ColStationEnabled),
                StationEnabledAddress = GetCellString(row, ColStationEnabledAddr),
                StartCommandAddress = GetCellString(row, ColStartCmd),
                PauseCommandAddress = GetCellString(row, ColPauseCmd),
                StopCommandAddress = GetCellString(row, ColStopCmd),
                ResetCommandAddress = GetCellString(row, ColResetCmd),
                AutoSignalAddress = GetCellString(row, ColAutoSig),
                PauseSignalAddress = GetCellString(row, ColPauseSig),
                InitSignalAddress = GetCellString(row, ColInitSig),
                InitDoneSignalAddress = GetCellString(row, ColInitDoneSig),
                StationAlarmAddress = GetCellString(row, ColStationAlarm),
                InitTimeoutAlarmAddress = GetCellString(row, ColInitTimeoutAlarm),
                InitSafetyConditionAddress = GetCellString(row, ColInitSafetyCond),
                InitSafetyNotMetAlarmAddress = GetCellString(row, ColInitSafetyNotMetAlarm),
                SingleCycleTimeAddress = GetCellString(row, ColSingleCt),
                FlowStatusIdAddress = GetCellString(row, ColFlowStatusId),
                FlowStatusIdValueType = ParseFlowStatusIdType(row),
                FlowStatusMapFilePath = GetCellString(row, ColMapFile)
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
                cfg.StationEnabled,
                cfg.StationEnabledAddress,
                cfg.StartCommandAddress,
                cfg.PauseCommandAddress,
                cfg.StopCommandAddress,
                cfg.ResetCommandAddress,
                cfg.AutoSignalAddress,
                cfg.PauseSignalAddress,
                cfg.InitSignalAddress,
                cfg.InitDoneSignalAddress,
                cfg.StationAlarmAddress,
                cfg.InitTimeoutAlarmAddress,
                cfg.InitSafetyConditionAddress,
                cfg.InitSafetyNotMetAlarmAddress,
                cfg.SingleCycleTimeAddress,
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
        _btnImportExcel.Enabled = _canAddDelete && canModify && _editUnlocked;
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
        _grid.RowTemplate.MinimumHeight = 32;
        _grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _grid.DefaultCellStyle.Padding = new Padding(3, 4, 3, 4);
        _grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
        _grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "工站名称", FillWeight = 14 });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "屏蔽", FillWeight = 6 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "屏蔽地址", FillWeight = 11 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "启动地址", FillWeight = 13 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "暂停地址", FillWeight = 13 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "停止地址", FillWeight = 13 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "复位地址", FillWeight = 13 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "自动中地址", FillWeight = 13 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "暂停中地址", FillWeight = 13 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "初始化中地址", FillWeight = 13 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "初始化完成地址", FillWeight = 13 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "工站报警地址", FillWeight = 11 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "初始化超时报警地址", FillWeight = 11 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "初始化安全条件地址", FillWeight = 11 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "初安件不满足报警地址", FillWeight = 11 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "单次CT地址", FillWeight = 10 });
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

        // 地址列不随屏幕宽度被压缩，超出的列可横向滚动；长内容换行后自动增加行高。
        foreach (DataGridViewColumn column in _grid.Columns)
        {
            column.MinimumWidth = 100;
            column.Width = 130;
        }
        _grid.Columns[ColStationName].Width = 160;
        _grid.Columns[ColStationName].Frozen = true;
        _grid.Columns[ColStationEnabled].MinimumWidth = 60;
        _grid.Columns[ColStationEnabled].Width = 60;
        _grid.Columns[ColStationEnabled].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _grid.Columns[ColMapFile].Width = 260;

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Padding = new Padding(0, 0, 0, 4)
        };

        actions.Controls.AddRange([_btnEditAuth, _btnAdd, _btnRemove, _btnSelectMapFile, _btnSave, _btnReload, _btnImportExcel]);

        Controls.Add(_grid);
        Controls.Add(actions);
    }

    private void BindEvents()
    {
        _btnEditAuth.Click += (_, _) => ToggleEditAuthorization();
        _btnAdd.Click += (_, _) =>
        {
            _grid.Rows.Add("", false, "", "", "", "", "", "", "", "", "", "", "", "", "", nameof(StationFlowStatusIdType.INT), "");
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
        _btnImportExcel.Click += (_, _) => ImportStationsFromExcel();
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

        _grid.CurrentRow.Cells[ColMapFile].Value = dialog.FileName;
        SaveConfigToFile();
        RaiseConfigurationsChanged();
    }

    /// <summary>
    /// 导入Excel：读取工站名称（Name 列）与工站号（InitValue 列），
    /// 按 st_Uint 结构体自动分配地址，整表替换当前配置。
    /// </summary>
    private void ImportStationsFromExcel()
    {
        if (!_canAddDelete || !_canModify || !_editUnlocked)
            return;

        using var dialog = new OpenFileDialog
        {
            Title = "选择工站名称Excel文件",
            Filter = "Excel 文件 (*.xls;*.xlsx)|*.xls;*.xlsx|所有文件 (*.*)|*.*",
            InitialDirectory = AppContext.BaseDirectory
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        List<StationOperationConfig> configs;
        try
        {
            var rows = ReadStationNamesFromExcel(dialog.FileName);
            var errors = new List<string>();
            var seenNumbers = new HashSet<int>();
            configs = [];

            foreach (var (name, number) in rows)
            {
                if (number < 0 || number >= StationCapacity)
                {
                    errors.Add($"{name}: 工站号 {number} 超出 0~{StationCapacity - 1}");
                    continue;
                }

                if (!seenNumbers.Add(number))
                {
                    errors.Add($"{name}: 工站号 {number} 重复");
                    continue;
                }

                configs.Add(BuildStationConfig(name, number));
            }

            if (configs.Count == 0)
            {
                string detail = errors.Count > 0 ? $"\n{string.Join("\n", errors.Take(5))}" : string.Empty;
                MessageBox.Show(this, $"未解析到有效的工站数据。{detail}", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var first = configs[0];
            DialogResult confirm = MessageBox.Show(
                this,
                $"将清空现有 {_grid.Rows.Count} 行，整表替换为 {configs.Count} 个工站。\n" +
                $"地址分配：起始 MB{StationBaseAddress}，每工站占 {StationAddressStride} 字节。\n" +
                $"首个工站「{first.StationName}」：启动 {first.StartCommandAddress}，工站报警 {first.StationAlarmAddress}。\n\n确认继续？",
                "导入Excel确认",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.OK)
            {
                LogAction?.Invoke("工站Excel导入已取消。");
                return;
            }

            if (errors.Count > 0)
                LogAction?.Invoke($"工站Excel导入跳过 {errors.Count} 行：{string.Join("；", errors.Take(3))}");

            SetConfigurations(configs);
            SaveConfigToFile();
            LogAction?.Invoke($"工站Excel导入完成：共 {configs.Count} 个工站，起始 MB{StationBaseAddress}，每工站 {StationAddressStride} 字节。来源: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导入失败: {ex.Message}", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            LogAction?.Invoke($"工站Excel导入失败: {ex.Message}");
        }
    }

    /// <summary>按工站号生成整套自动分配的地址（偏移见 StationBaseAddress/StationAddressStride 注释）。</summary>
    private static StationOperationConfig BuildStationConfig(string name, int stationNumber)
    {
        int baseOffset = StationBaseAddress + StationAddressStride * stationNumber;
        string Mb(int offset) => $"MB{offset}";

        return new StationOperationConfig
        {
            StationName = name,
            StationEnabled = true,
            StationEnabledAddress = Mb(baseOffset + 8),      // 口工站屏蔽（与工站报警对调后按手填行：屏蔽=+8）
            StartCommandAddress = Mb(baseOffset),            // 启动
            PauseCommandAddress = Mb(baseOffset + 1),        // 暂停
            StopCommandAddress = Mb(baseOffset + 2),         // 停止
            ResetCommandAddress = Mb(baseOffset + 3),        // 复位
            AutoSignalAddress = Mb(baseOffset + 4),          // 自动中
            PauseSignalAddress = Mb(baseOffset + 5),         // 暂停中
            InitSignalAddress = Mb(baseOffset + 6),          // 初始化中
            InitDoneSignalAddress = Mb(baseOffset + 7),      // 初始化完成
            StationAlarmAddress = Mb(baseOffset + 9),        // 工站报警（与屏蔽地址对调后按手填行：报警=+9）
            InitTimeoutAlarmAddress = Mb(baseOffset + 10),   // 初始化超时报警
            InitSafetyConditionAddress = Mb(baseOffset + 11),// 初始化安全条件不足报警
            InitSafetyNotMetAlarmAddress = Mb(baseOffset + 12), // 初始化条件不满足报警
            FlowStatusIdAddress = Mb(baseOffset + 14),       // 自动状态信息(INT)
            FlowStatusIdValueType = StationFlowStatusIdType.INT,
            SingleCycleTimeAddress = Mb(baseOffset + 16)     // 单次循环时间(REAL)
        };
    }

    /// <summary>
    /// 读取工站名称与工站号。表头（Name/名称/工站名称 + InitValue/工站号）在前 10 行内自动识别；
    /// 认不到表头时退回第 1/2 列；InitValue 缺失或非数字时按出现顺序编号。
    /// </summary>
    private static List<(string Name, int StationNumber)> ReadStationNamesFromExcel(string fileName)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        using var stream = File.Open(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateReader(stream);

        int nameCol = -1;
        int numberCol = -1;
        bool headerFound = false;
        int scannedRows = 0;
        int dataRowIndex = 0;
        var rows = new List<(string Name, int StationNumber)>();

        while (reader.Read())
        {
            if (!headerFound)
            {
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    string header = GetExcelCellText(reader, i);
                    if (string.Equals(header, "Name", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(header, "名称", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(header, "工站名称", StringComparison.OrdinalIgnoreCase))
                        nameCol = i;
                    else if (string.Equals(header, "InitValue", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(header, "工站号", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(header, "工站编号", StringComparison.OrdinalIgnoreCase))
                        numberCol = i;
                }

                if (nameCol >= 0)
                {
                    headerFound = true;
                }
                else if (++scannedRows >= 10)
                {
                    // 前 10 行都没有表头：退回第 1/2 列，从当前行开始按数据解析
                    nameCol = 0;
                    numberCol = 1;
                    headerFound = true;
                }

                continue;
            }

            string name = GetExcelCellText(reader, nameCol);
            if (string.IsNullOrWhiteSpace(name)
                || string.Equals(name, "Name", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "名称", StringComparison.OrdinalIgnoreCase))
                continue;

            int number = dataRowIndex;
            string numberText = GetExcelCellText(reader, numberCol);
            if (int.TryParse(numberText, out int parsed))
                number = parsed;

            rows.Add((name, number));
            dataRowIndex++;
        }

        return rows;
    }

    private static string GetExcelCellText(global::ExcelDataReader.IExcelDataReader reader, int index)
    {
        object? value = index >= 0 && index < reader.FieldCount ? reader.GetValue(index) : null;
        return (Convert.ToString(value) ?? string.Empty).Trim();
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

    private static bool GetCellBool(DataGridViewRow row, int columnIndex)
    {
        object? v = row.Cells[columnIndex].Value;
        return v is bool b && b;
    }

    private static StationFlowStatusIdType ParseFlowStatusIdType(DataGridViewRow row)
    {
        string s = Convert.ToString(row.Cells[ColFlowStatusIdType].Value)?.Trim() ?? string.Empty;
        if (Enum.TryParse<StationFlowStatusIdType>(s, true, out var t))
            return t;
        return StationFlowStatusIdType.INT;
    }
}
