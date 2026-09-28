using System.Text.Encodings.Web;
using System.Text.Json;

namespace PlcConectCom.WinFormsTest;

// 报警地址配置项
public sealed class AlarmAddressItem
{
    public string AlarmName { get; set; } = string.Empty;

    public string AlarmCode { get; set; } = string.Empty;

    public string AlarmAddress { get; set; } = string.Empty;

    public string DataDisplay { get; set; } = string.Empty;
}

// 报警触发事件数据
public sealed class AlarmTriggeredEvent
{
    public DateTime AlarmTime { get; set; }

    public string AlarmName { get; set; } = string.Empty;

    public string AlarmCode { get; set; } = string.Empty;
}

// 报警地址管理控件：配置报警地址并执行轮询监控
public sealed class AlarmAddressManagerControl : UserControl
{
    private const string DefaultConfigFileName = "AlarmAddress.config.json";
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

    private readonly Button _btnAdd = new() { Text = "新增", Width = 72 };
    private readonly Button _btnDelete = new() { Text = "删除", Width = 72 };
    private readonly Button _btnEditAuth = new() { Text = "编辑解锁", Width = 84 };
    private readonly Button _btnLoad = new() { Text = "加载配置", Width = 84 };
    private readonly Button _btnSave = new() { Text = "保存本地", Width = 84 };
    private readonly Button _btnCheckAlarm = new() { Text = "检查报警", Width = 84 };
    private readonly Button _btnAutoMonitor = new() { Text = "自动监控:关", Width = 92 };
    private readonly System.Windows.Forms.Timer _monitorTimer = new() { Interval = 500 };

    private readonly FlowLayoutPanel _actionsRow = new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        WrapContents = true,
        Padding = new Padding(0, 0, 0, 4)
    };

    private readonly string _defaultConfigPath;
    /// <summary>当前仍为 ON 的报警会话；键为 报警代码|地址，值为首次变为 ON 的时间（用于主页实时列表）。</summary>
    private readonly Dictionary<string, (string Name, string Code, DateTime FirstSeen)> _activeAlarmSessions = [];
    private bool _suspendAutoSave;
    private bool _isChecking;
    private bool _canAddDelete = true;
    private bool _canModify = true;
    private bool _editUnlocked;

    /// <summary>
    /// 构造报警地址管理控件。
    /// 不传 <paramref name="storageKey"/> 时使用默认配置文件 AlarmAddress.config.json（主 PLC）；
    /// 传入时使用 AlarmAddress.{storageKey}.config.json（子 PLC 每设备独立配置）。
    /// </summary>
    public AlarmAddressManagerControl(string? storageKey = null)
    {
        string fileName = string.IsNullOrWhiteSpace(storageKey)
            ? DefaultConfigFileName
            : $"AlarmAddress.{storageKey}.config.json";
        _defaultConfigPath = Path.Combine(AppContext.BaseDirectory, fileName);
        BuildUi();
        BindEvents();
        LoadDefaultConfig();
    }

    public Action<string>? LogAction { get; set; }

    public Func<global::PlcConectCom.PlcCommunicator?>? PlcProvider { get; set; }

    public global::PlcConectCom.PlcCommunicator? PlcInstance { get; set; }

    public Func<bool>? EditPermissionRequest { get; set; }

    public event Action<AlarmTriggeredEvent>? AlarmTriggered;

    /// <summary>每次轮询结束后发出当前仍为 ON 的报警列表（主页实时表格据此刷新；无报警则为空）。</summary>
    public event Action<IReadOnlyList<AlarmTriggeredEvent>>? ActiveAlarmsSnapshotUpdated;

    public void SetPermission(bool canAddDelete, bool canModify)
    {
        _canAddDelete = canAddDelete;
        _canModify = canModify;
        if (!canModify)
            _editUnlocked = false;

        _btnAdd.Enabled = _canAddDelete && _editUnlocked;
        _btnDelete.Enabled = _canAddDelete && _editUnlocked;
        _btnLoad.Enabled = canModify && _editUnlocked;
        _btnSave.Enabled = canModify && _editUnlocked;
        _btnCheckAlarm.Enabled = canModify;
        _btnAutoMonitor.Enabled = canModify;
        _btnEditAuth.Enabled = canModify;
        _btnEditAuth.Text = _editUnlocked ? "编辑锁定" : "编辑解锁";
        _grid.ReadOnly = !_editUnlocked;
    }

    public void StartAutoMonitor()
    {
        if (_monitorTimer.Enabled)
            return;

        _monitorTimer.Start();
        _btnAutoMonitor.Text = "自动监控:开";
    }

    public void StopAutoMonitor()
    {
        if (!_monitorTimer.Enabled)
            return;

        _monitorTimer.Stop();
        _btnAutoMonitor.Text = "自动监控:关";
    }

    public IReadOnlyList<AlarmAddressItem> GetItems()
    {
        var list = new List<AlarmAddressItem>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            string name = Convert.ToString(row.Cells[0].Value)?.Trim() ?? string.Empty;
            string alarmCode = Convert.ToString(row.Cells[1].Value)?.Trim() ?? string.Empty;
            string address = Convert.ToString(row.Cells[2].Value)?.Trim() ?? string.Empty;
            string dataDisplay = Convert.ToString(row.Cells[3].Value)?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(alarmCode) && string.IsNullOrWhiteSpace(address))
                continue;

            list.Add(new AlarmAddressItem
            {
                AlarmName = name,
                AlarmCode = alarmCode,
                AlarmAddress = address,
                DataDisplay = dataDisplay
            });
        }

        return list;
    }

    public void SetItems(IEnumerable<AlarmAddressItem> items)
    {
        _suspendAutoSave = true;
        _grid.Rows.Clear();
        foreach (var item in items)
            _grid.Rows.Add(item.AlarmName, item.AlarmCode, item.AlarmAddress, item.DataDisplay);
        _suspendAutoSave = false;

        SaveDefaultConfig();
    }

    private void BuildUi()
    {
        _grid.DefaultCellStyle.SelectionBackColor = Color.Gainsboro;
        _grid.DefaultCellStyle.SelectionForeColor = Color.Black;

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "报警名称", FillWeight = 30 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "报警代码", FillWeight = 20 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "数据地址", FillWeight = 30 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "数据显示", FillWeight = 20, ReadOnly = true });

        _actionsRow.Controls.AddRange([_btnEditAuth, _btnAdd, _btnDelete, _btnLoad, _btnSave, _btnCheckAlarm, _btnAutoMonitor]);

        Controls.Add(_grid);
        Controls.Add(_actionsRow);
    }

    /// <summary>将「导入数据」等外部按钮放到与「编辑解锁」同一行的最右侧。</summary>
    public void PlaceImportButtonAtEnd(Control importButton)
    {
        if (importButton.Parent == _actionsRow && _actionsRow.Controls.Count > 0 && ReferenceEquals(_actionsRow.Controls[^1], importButton))
            return;

        importButton.Parent?.Controls.Remove(importButton);
        _actionsRow.Controls.Add(importButton);
    }

    private void BindEvents()
    {
        _btnEditAuth.Click += (_, _) => ToggleEditAuth();
        _btnAdd.Click += (_, _) =>
        {
            _grid.Rows.Add(string.Empty, string.Empty, string.Empty, string.Empty);
            SaveDefaultConfig();
        };

        _btnDelete.Click += (_, _) =>
        {
            if (_grid.SelectedRows.Count == 0)
                return;

            _grid.Rows.RemoveAt(_grid.SelectedRows[0].Index);
            SaveDefaultConfig();
        };

        _btnLoad.Click += (_, _) => LoadFromSelectedFile();
        _btnSave.Click += (_, _) => SaveDefaultConfig();
        _btnCheckAlarm.Click += async (_, _) => await CheckAlarmsAsync();
        _btnAutoMonitor.Click += (_, _) => ToggleAutoMonitor();
        _monitorTimer.Tick += async (_, _) => await CheckAlarmsAsync();

        _grid.CellValueChanged += (_, e) =>
        {
            if (_suspendAutoSave || !_editUnlocked)
                return;

            if (e.RowIndex >= 0 && e.ColumnIndex <= 2)
                SaveDefaultConfig();
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

    private void LoadDefaultConfig()
    {
        if (!File.Exists(_defaultConfigPath))
            return;

        try
        {
            var json = File.ReadAllText(_defaultConfigPath);
            var items = JsonSerializer.Deserialize<List<AlarmAddressItem>>(json);
            if (items is null)
                return;

            _suspendAutoSave = true;
            _grid.Rows.Clear();
            foreach (var item in items)
                _grid.Rows.Add(item.AlarmName, item.AlarmCode, item.AlarmAddress, item.DataDisplay);
            _suspendAutoSave = false;
        }
        catch (Exception ex)
        {
            _suspendAutoSave = false;
            LogAction?.Invoke($"加载本地报警配置失败: {ex.Message}");
        }
    }

    private void LoadFromSelectedFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择报警配置文件",
            Filter = "JSON文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            InitialDirectory = AppContext.BaseDirectory
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            var json = File.ReadAllText(dialog.FileName);
            var items = JsonSerializer.Deserialize<List<AlarmAddressItem>>(json);
            if (items is null)
                return;

            SetItems(items);
            LogAction?.Invoke($"已加载报警配置: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"加载报警配置失败: {ex.Message}");
        }
    }

    private void SaveDefaultConfig()
    {
        if (_suspendAutoSave)
            return;

        try
        {
            var items = GetItems();
            var json = JsonSerializer.Serialize(items, JsonFileOptions);
            File.WriteAllText(_defaultConfigPath, json);
            LogAction?.Invoke("报警地址配置已保存到本地。");
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"保存报警配置失败: {ex.Message}");
        }
    }

    private void ToggleAutoMonitor()
    {
        if (!_canModify)
            return;

        if (_monitorTimer.Enabled)
        {
            _monitorTimer.Stop();
            _btnAutoMonitor.Text = "自动监控:关";
            return;
        }

        _monitorTimer.Start();
        _btnAutoMonitor.Text = "自动监控:开";
    }

    private async Task CheckAlarmsAsync()
    {
        if (_isChecking)
            return;

        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is null || !plc.IsConnected)
        {
            ActiveAlarmsSnapshotUpdated?.Invoke([]);
            return;
        }

        _isChecking = true;
        try
        {
            // 先收集本轮要读的行，再按地址块批量读取：
            // 逐条 ReadBoolAsync 时 N 条报警 = N 次 Modbus 往返（全局信号量串行），
            // 地址多时单轮可达数秒，是主页面实时报警滞后的主因
            var rowInfos = new List<(DataGridViewRow Row, string AlarmName, string AlarmCodeRaw, string Address)>();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                string alarmName = Convert.ToString(row.Cells[0].Value)?.Trim() ?? string.Empty;
                string alarmCodeRaw = Convert.ToString(row.Cells[1].Value)?.Trim() ?? string.Empty;
                string alarmAddress = Convert.ToString(row.Cells[2].Value)?.Trim() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(alarmAddress))
                {
                    row.Cells[3].Value = string.Empty;
                    continue;
                }

                if (!IsSupportedBoolAddress(alarmAddress))
                {
                    row.Cells[3].Value = "地址无效";
                    continue;
                }

                rowInfos.Add((row, alarmName, alarmCodeRaw, alarmAddress));
            }

            Dictionary<string, bool> states = await PlcBoolBatchReader.ReadBoolStatesAsync(
                plc, rowInfos.Select(x => x.Address));

            foreach (var info in rowInfos)
            {
                if (!states.TryGetValue(info.Address, out bool state))
                {
                    info.Row.Cells[3].Value = "ERR";
                    LogAction?.Invoke($"报警地址读取失败 {info.Address}");
                    continue;
                }

                info.Row.Cells[3].Value = state ? "ON" : "OFF";

                string alarmCode = string.IsNullOrWhiteSpace(info.AlarmCodeRaw) ? info.Address : info.AlarmCodeRaw;
                string key = $"{alarmCode}|{info.Address}";

                if (state)
                {
                    if (_activeAlarmSessions.ContainsKey(key))
                        continue;

                    DateTime firstSeen = DateTime.Now;
                    _activeAlarmSessions[key] = (info.AlarmName, alarmCode, firstSeen);
                    var alarmEvent = new AlarmTriggeredEvent
                    {
                        AlarmTime = firstSeen,
                        AlarmName = info.AlarmName,
                        AlarmCode = alarmCode
                    };

                    AlarmTriggered?.Invoke(alarmEvent);
                }
                else
                {
                    _activeAlarmSessions.Remove(key);
                }
            }

            PublishActiveAlarmsSnapshot();
        }
        finally
        {
            _isChecking = false;
        }
    }

    private void PublishActiveAlarmsSnapshot()
    {
        if (_activeAlarmSessions.Count == 0)
        {
            ActiveAlarmsSnapshotUpdated?.Invoke([]);
            return;
        }

        var list = _activeAlarmSessions.Values
            .Select(v => new AlarmTriggeredEvent
            {
                AlarmTime = v.FirstSeen,
                AlarmName = v.Name,
                AlarmCode = v.Code
            })
            .OrderByDescending(x => x.AlarmTime)
            .ToList();

        ActiveAlarmsSnapshotUpdated?.Invoke(list);
    }

    private static bool IsSupportedBoolAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return false;

        try
        {
            var (type, _, _) = global::PlcConectCom.AddressParser.ParseAddress(address);
            return type is "MB" or "MX";
        }
        catch
        {
            return false;
        }
    }
}
