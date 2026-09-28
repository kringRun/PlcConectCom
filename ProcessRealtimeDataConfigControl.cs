using System.Globalization;
using System.Text.Json;

namespace PlcConectCom.WinFormsTest;

public sealed class ProcessRealtimeDataItem
{
    public string Name { get; set; } = string.Empty;
    public ParameterValueType Type { get; set; } = ParameterValueType.REAL;
    public string Address { get; set; } = "MD0";
    public int DisplayColorArgb { get; set; } = Color.FromArgb(52, 103, 235).ToArgb();
    public bool RecordHistory { get; set; }
    public bool DisplayAsHms { get; set; }
    public string Comment { get; set; } = string.Empty;
}

file sealed class ProcessRealtimeDataConfigFile
{
    public List<ProcessRealtimeDataItem> Items { get; set; } = [];
}

public sealed class ProcessRealtimeDataConfigControl : UserControl
{
    public const string ConfigFileName = "ProcessRealtimeData.config.json";
    private readonly string _configPath;
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
    private readonly FlowLayoutPanel _toolbar = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(0, 40), Padding = new Padding(0, 0, 0, 4), WrapContents = true };
    private readonly Button _btnEditAuth = new() { Text = "编辑解锁", Width = 80 };
    private readonly Button _btnAdd = new() { Text = "新增", Width = 70 };
    private readonly Button _btnDelete = new() { Text = "删除", Width = 70 };
    private readonly Button _btnRead = new() { Text = "读取全部", Width = 80 };
    private readonly Button _btnSave = new() { Text = "保存配置", Width = 80 };
    private readonly Label _hint = new() { AutoSize = true, Padding = new Padding(10, 8, 0, 0), Text = "主界面右侧实时工艺数据与本配置同步" };

    private bool _suspendSave;
    private bool _canAddDelete = true;
    private bool _canModify = true;
    private bool _editUnlocked;

    public Func<global::PlcConectCom.PlcCommunicator?>? PlcProvider { get; set; }
    public global::PlcConectCom.PlcCommunicator? PlcInstance { get; set; }
    public Action<string>? LogAction { get; set; }
    public Func<bool>? EditPermissionRequest { get; set; }
    public event EventHandler? ConfigurationChanged;

    public ProcessRealtimeDataConfigControl() : this(null) { }

    public ProcessRealtimeDataConfigControl(string? storageKey)
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
        LoadConfigToGrid();
        SetPermission(canAddDelete: true, canModify: true);
    }

    public static IReadOnlyList<ProcessRealtimeDataItem> LoadConfiguredItems(string? storageKey = null)
    {
        try
        {
            string fileName = ConfigFileName;
            if (!string.IsNullOrWhiteSpace(storageKey))
            {
                string safeKey = string.Concat(storageKey.Where(char.IsLetterOrDigit));
                if (!string.IsNullOrWhiteSpace(safeKey))
                    fileName = $"{Path.GetFileNameWithoutExtension(ConfigFileName)}.{safeKey}{Path.GetExtension(ConfigFileName)}";
            }
            string path = Path.Combine(AppContext.BaseDirectory, fileName);
            if (!File.Exists(path))
                return Array.Empty<ProcessRealtimeDataItem>();

            var data = JsonSerializer.Deserialize<ProcessRealtimeDataConfigFile>(File.ReadAllText(path));
            if (data?.Items is null)
                return Array.Empty<ProcessRealtimeDataItem>();

            return data.Items;
        }
        catch
        {
            return Array.Empty<ProcessRealtimeDataItem>();
        }
    }

    public void SetPermission(bool canAddDelete, bool canModify)
    {
        _canAddDelete = canAddDelete;
        _canModify = canModify;
        if (!canModify)
            _editUnlocked = false;

        _btnEditAuth.Enabled = canModify;
        _btnEditAuth.Text = _editUnlocked ? "编辑锁定" : "编辑解锁";
        _btnAdd.Enabled = _editUnlocked && canAddDelete;
        _btnDelete.Enabled = _editUnlocked && canAddDelete;
        _btnSave.Enabled = _editUnlocked && canModify;
        _grid.ReadOnly = !_editUnlocked || !canModify;
        _grid.Columns[6].ReadOnly = true;
    }

    public async Task ReadAllAsync()
    {
        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
            return;

        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow)
                continue;

            string typeText = Convert.ToString(row.Cells[1].Value, CultureInfo.InvariantCulture) ?? string.Empty;
            if (!Enum.TryParse(typeText, true, out ParameterValueType t))
                t = ParameterValueType.REAL;
            string address = Convert.ToString(row.Cells[2].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(address))
                continue;

            try
            {
                bool displayAsHms = row.Cells[5].Value is bool b && b;
                string rawValue = await ReadValueForDisplayAsync(plc, t, address, displayAsHms);
                string value = displayAsHms ? FormatSecondsToHms(rawValue) : rawValue;
                row.Cells[6].Value = value;
            }
            catch (Exception ex)
            {
                LogAction?.Invoke($"读取工艺数据失败 {address}: {ex.Message}");
            }
        }
    }

    private void BuildUi()
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "名称", FillWeight = 16 });
        _grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            HeaderText = "类型",
            FillWeight = 10,
            DataSource = Enum.GetNames<ParameterValueType>()
                .Where(x => x != nameof(ParameterValueType.REAL_ARRAY))
                .ToArray()
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "地址", FillWeight = 16 });
        _grid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "颜色", Text = "取色", UseColumnTextForButtonValue = true, FillWeight = 8 });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "记录曲线", FillWeight = 8 });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "时分秒显示", FillWeight = 10 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "当前值", FillWeight = 16, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "注释", FillWeight = 18 });

        _toolbar.Controls.AddRange([_btnEditAuth, _btnAdd, _btnDelete, _btnRead, _btnSave, _hint]);
        Controls.Add(_grid);
        Controls.Add(_toolbar);
    }

    private void BindEvents()
    {
        _btnEditAuth.Click += (_, _) =>
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
        };

        _btnAdd.Click += (_, _) =>
        {
            if (!_editUnlocked || !_canAddDelete)
                return;

            int idx = _grid.Rows.Add("", ParameterValueType.REAL.ToString(), "MD0", "取色", false, false, "", "");
            _grid.Rows[idx].Tag = Color.FromArgb(52, 103, 235).ToArgb();
            SaveGridToConfig();
        };

        _btnDelete.Click += (_, _) =>
        {
            if (!_editUnlocked || !_canAddDelete || _grid.CurrentRow is null)
                return;

            _grid.Rows.RemoveAt(_grid.CurrentRow.Index);
            SaveGridToConfig();
        };

        _btnRead.Click += async (_, _) => await ReadAllAsync();
        _btnSave.Click += (_, _) => SaveGridToConfig();

        _grid.CellContentClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 3 || !_editUnlocked || !_canModify)
                return;

            DataGridViewRow row = _grid.Rows[e.RowIndex];
            int argb = row.Tag is int c ? c : Color.FromArgb(52, 103, 235).ToArgb();
            using var dlg = new ColorDialog { Color = Color.FromArgb(argb) };
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK)
                return;
            row.Tag = dlg.Color.ToArgb();
            row.Cells[0].Style.ForeColor = dlg.Color;
            row.Cells[6].Style.ForeColor = dlg.Color;
            SaveGridToConfig();
        };

        _grid.CellValueChanged += (_, e) =>
        {
            if (_suspendSave || e.RowIndex < 0)
                return;
            if (_editUnlocked && _canModify)
                SaveGridToConfig();
        };
    }

    private void LoadConfigToGrid()
    {
        try
        {
            _suspendSave = true;
            _grid.Rows.Clear();
            foreach (ProcessRealtimeDataItem item in LoadConfiguredItems())
            {
                int idx = _grid.Rows.Add(item.Name, item.Type.ToString(), item.Address, "取色", item.RecordHistory, item.DisplayAsHms, "", item.Comment);
                var row = _grid.Rows[idx];
                row.Tag = item.DisplayColorArgb;
                Color c = Color.FromArgb(item.DisplayColorArgb);
                row.Cells[0].Style.ForeColor = c;
                row.Cells[6].Style.ForeColor = c;
            }
        }
        finally
        {
            _suspendSave = false;
        }
    }

    private void SaveGridToConfig()
    {
        if (_suspendSave)
            return;

        try
        {
            var items = new List<ProcessRealtimeDataItem>();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.IsNewRow)
                    continue;

                string typeText = Convert.ToString(row.Cells[1].Value, CultureInfo.InvariantCulture) ?? string.Empty;
                if (!Enum.TryParse(typeText, true, out ParameterValueType t))
                    t = ParameterValueType.REAL;

                items.Add(new ProcessRealtimeDataItem
                {
                    Name = Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty,
                    Type = t,
                    Address = Convert.ToString(row.Cells[2].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty,
                    DisplayColorArgb = row.Tag is int c ? c : Color.FromArgb(52, 103, 235).ToArgb(),
                    RecordHistory = row.Cells[4].Value is bool b && b,
                    DisplayAsHms = row.Cells[5].Value is bool h && h,
                    Comment = Convert.ToString(row.Cells[7].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty
                });
            }

            string json = JsonSerializer.Serialize(new ProcessRealtimeDataConfigFile { Items = items }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configPath, json);
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"保存工艺实时数据配置失败: {ex.Message}");
        }
    }

    private static string FormatSecondsToHms(string raw)
    {
        if (!TryParseSecondInput(raw, out int secondinput))
            return raw;

        bool negative = secondinput < 0;
        int secondsAbs = Math.Abs(secondinput);
        int hour = secondsAbs / 3600;
        int minute = secondsAbs / 60 - hour * 60;
        int second = secondsAbs - minute * 60 - hour * 3600;
        string text = $"{hour}:{minute}:{second}";
        return negative ? $"-{text}" : text;
    }

    private static async Task<string> ReadValueForDisplayAsync(
        global::PlcConectCom.PlcCommunicator plc,
        ParameterValueType type,
        string address,
        bool displayAsHms)
    {
        if (displayAsHms)
        {
            // 时分秒模式优先按配置类型读取，避免把整秒变量按 REAL 解释后变成 0。
            if (type == ParameterValueType.DWORD)
            {
                try
                {
                    return (await plc.ReadDWordAsync(address)).ToString(CultureInfo.InvariantCulture);
                }
                catch
                {
                    // fallback below
                }
            }

            if (type == ParameterValueType.INT)
            {
                try
                {
                    return (await plc.ReadIntAsync(address)).ToString(CultureInfo.InvariantCulture);
                }
                catch
                {
                    // fallback below
                }
            }
        }

        return type switch
        {
            ParameterValueType.BOOL => (await plc.ReadBoolAsync(address)).ToString(CultureInfo.InvariantCulture),
            ParameterValueType.INT => (await plc.ReadIntAsync(address)).ToString(CultureInfo.InvariantCulture),
            ParameterValueType.DWORD => (await plc.ReadDWordAsync(address)).ToString(CultureInfo.InvariantCulture),
            ParameterValueType.REAL => (await plc.ReadDoubleAsync(address)).ToString("G9", CultureInfo.InvariantCulture),
            ParameterValueType.STRING => await plc.ReadStringAsync(address, 128),
            _ => string.Empty
        };
    }

    private static bool TryParseSecondInput(string raw, out int secondinput)
    {
        secondinput = 0;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out secondinput)
            || int.TryParse(raw, out secondinput))
            return true;

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            || double.TryParse(raw, out seconds))
        {
            secondinput = Convert.ToInt32(seconds, CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }
}
