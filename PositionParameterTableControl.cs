using System.Text.Encodings.Web;
using System.Text.Json;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ExcelDataReader;

namespace PlcConectCom.WinFormsTest;

// 位置参数项
public sealed class PositionParameterItem
{
    public string PositionName { get; set; } = string.Empty;

    public List<PositionAxisBinding> AxisBindings { get; set; } = [];

    public List<string> AxisNames { get; set; } = [];

    /// <summary>触发定位运动的 PLC 地址（BOOL）。</summary>
    public string LocateTriggerPlcAddress { get; set; } = string.Empty;

    /// <summary>点位编号的 PLC 写入地址。</summary>
    public string PointNumberPlcAddress { get; set; } = string.Empty;

    /// <summary>写入点位编号寄存器的数值（支持整数或小数，按地址类型自动选择写入方式）。</summary>
    public string PointNumberData { get; set; } = string.Empty;
}

// 位置中单轴绑定（轴名 + 地址）
public sealed class PositionAxisBinding
{
    public string AxisName { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;
}

// 数值快照：单轴的「地址 + 数值」
public sealed class PositionSnapshotEntry
{
    public string AxisName { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;

    public double Value { get; set; }
}

// 数值快照：单个位置（含其下所有轴）
public sealed class PositionSnapshotItem
{
    public string PositionName { get; set; } = string.Empty;

    public List<PositionSnapshotEntry> AxisValues { get; set; } = [];
}

/// <summary>
/// 数值快照文件。用于 PLC 保持寄存器丢数据后一键写回。
/// 只存「位置名 + 轴名 + 地址 + 数值」，不改动原有的位置参数配置。
/// </summary>
public sealed class PositionSnapshotFile
{
    public string Version { get; set; } = "1";

    /// <summary>保存时刻（本机本地时间）。</summary>
    public DateTime SavedAt { get; set; }

    public string MachineName { get; set; } = Environment.MachineName;

    public List<PositionSnapshotItem> Positions { get; set; } = [];
}

// 位置参数表控件：位置维护、轴绑定与位置数据编辑
public sealed class PositionParameterTableControl : UserControl
{
    private sealed record PositionValueReadRequest(
        DataGridViewRow Row,
        PositionParameterItem Item,
        string AxisName,
        string Address,
        int Offset);

    private sealed record PositionValueReadBatch(
        int StartOffset,
        int EndOffset,
        List<PositionValueReadRequest> Requests);

    private const string ConfigFileName = "PositionParameterTable.config.json";
    private const string KeywordFileName = "PositionParameterTable.keywords.json";
    private const string SnapshotFileName = "PositionParameterTable.snapshot.json";
    private const int MaxImportAxisCount = 5;
    /// <summary>自动快照周期（毫秒）。默认 5 分钟，PLC 已连接且当前行有数值时落盘。</summary>
    private const int AutoSnapshotIntervalMs = 5 * 60 * 1000;
    private static readonly Regex IndexedAxisCommentRegex = new(
        @"(?:^|\s)(?<index>\d+)\s*[:：]\s*(?<name>.*?)(?=\s+\d+\s*[:：]|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
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
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        ReadOnly = true,
        EditMode = DataGridViewEditMode.EditProgrammatically
    };

    // 「新增位置」「删除位置」按钮已按需求移除；AddPositionByDialog / RemoveSelectedPositionWithConfirm 保留备用
    private readonly Button _btnEdit = new() { Text = "地址绑定", Width = 82 };
    private readonly Button _btnEditData = new() { Text = "编辑数据", Width = 82 };
    private readonly Button _btnRefresh = new() { Text = "刷新数据", Width = 82 };
    private readonly Button _btnSave = new() { Text = "保存", Width = 72 };
    private readonly Button _btnLoad = new() { Text = "加载", Width = 72 };
    private readonly Button _btnImportExcel = new() { Text = "导入Excel", Width = 90 };
    // 数值本地快照 / 一键恢复
    private readonly Button _btnSaveSnapshot = new() { Text = "保存快照", Width = 82 };
    private readonly Button _btnRestoreSnapshot = new() { Text = "一键恢复", Width = 82 };
    private readonly System.Windows.Forms.Timer _autoSnapshotTimer = new() { Interval = AutoSnapshotIntervalMs };
    private readonly ComboBox _cmbKeyword = new() { Width = 150, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly Button _btnSearch = new() { Text = "关键字搜索", Width = 80 };
    private readonly Button _btnClearKeyword = new() { Text = "清除关键字", Width = 80 };
    private readonly ContextMenuStrip _rowMenu = new();
    private readonly ToolStripMenuItem _menuTeach = new("试教位置");
    private readonly ToolStripMenuItem _menuLocate = new("定位运动");
    private readonly ToolStripMenuItem _menuSavePosition = new("保存位置");

    private readonly string _configPath;
    private readonly string _keywordPath;
    private readonly string _snapshotPath;
    private readonly List<string> _keywords = [];
    private readonly Dictionary<PositionParameterItem, Dictionary<string, double>> _teachedValues = [];
    private bool _suspendAutoSave;
    private bool _canAddDelete = true;
    private bool _canModify = true;

    public PositionParameterTableControl() : this(null) { }

    public PositionParameterTableControl(string? storageKey)
    {
        _configPath = Path.Combine(AppContext.BaseDirectory, BuildStorageFileName(ConfigFileName, storageKey));
        _keywordPath = Path.Combine(AppContext.BaseDirectory, BuildStorageFileName(KeywordFileName, storageKey));
        _snapshotPath = Path.Combine(AppContext.BaseDirectory, BuildStorageFileName(SnapshotFileName, storageKey));
        BuildUi();
        BindEvents();
        LoadKeywords();
        LoadConfig();
    }

    /// <summary>释放定时快照计时器，避免控件销毁后仍触发写入/落盘。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _autoSnapshotTimer.Dispose();

        base.Dispose(disposing);
    }

    private static string BuildStorageFileName(string fileName, string? storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
            return fileName;
        string safeKey = string.Concat(storageKey.Where(char.IsLetterOrDigit));
        return string.IsNullOrWhiteSpace(safeKey) ? fileName : $"{Path.GetFileNameWithoutExtension(fileName)}.{safeKey}{Path.GetExtension(fileName)}";
    }

    public Action<string>? LogAction { get; set; }

    /// <summary>设备显示名（主 PLC / 子 PLC 名称），仅用于手动操作日志归属。</summary>
    public string DeviceDisplayName { get; set; } = "主 PLC";

    /// <summary>记录一次「修改位置」类手动操作：试教 / 编辑数据 / 保存位置 / 一键恢复。</summary>
    private void LogManualPosition(string action, string positionName, string? axisName = null, string? address = null,
        string? beforeValue = null, string? afterValue = null, string? detail = null)
    {
        ManualOperationLogStore.Write(
            ManualOperationCategory.Position,
            DeviceDisplayName,
            action,
            target: positionName,
            subTarget: axisName,
            address: address,
            beforeValue: beforeValue,
            afterValue: afterValue,
            detail: detail);
    }

    public Func<IReadOnlyList<string>>? AxisNameProvider { get; set; }

    public Func<global::PlcConectCom.PlcCommunicator?>? PlcProvider { get; set; }

    public global::PlcConectCom.PlcCommunicator? PlcInstance { get; set; }

    public Func<string, string, string?>? AxisFieldAddressResolver { get; set; }

    public void SetPermission(bool canAddDelete, bool canModify)
    {
        _canAddDelete = canAddDelete;
        _canModify = canModify;
        _btnEdit.Enabled = _canAddDelete;
        _btnEditData.Enabled = canModify;
        _btnRefresh.Enabled = canModify;
        _btnSave.Enabled = canModify;
        _btnLoad.Enabled = canModify;
        _btnImportExcel.Enabled = _canAddDelete;
        _btnSaveSnapshot.Enabled = _canModify;
        _btnRestoreSnapshot.Enabled = _canModify;
        // 关键字仅筛选行显示，与 PLC 写入无关，各权限均可使用
        _cmbKeyword.Enabled = true;
        _btnSearch.Enabled = true;
        _btnClearKeyword.Enabled = true;
        _grid.ReadOnly = true;
        UpdateRowMenuState();
    }

    public IReadOnlyList<PositionParameterItem> GetItems()
    {
        var list = new List<PositionParameterItem>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is not PositionParameterItem item)
                continue;

            string positionName = Convert.ToString(row.Cells[0].Value)?.Trim() ?? string.Empty;
            var clone = new PositionParameterItem
            {
                PositionName = positionName,
                AxisBindings = item.AxisBindings.Select(x => new PositionAxisBinding
                {
                    AxisName = x.AxisName,
                    Address = x.Address
                }).ToList(),
                LocateTriggerPlcAddress = item.LocateTriggerPlcAddress ?? string.Empty,
                PointNumberPlcAddress = item.PointNumberPlcAddress ?? string.Empty,
                PointNumberData = item.PointNumberData ?? string.Empty
            };
            clone.AxisNames = clone.AxisBindings.Select(x => x.AxisName).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();

            if (string.IsNullOrWhiteSpace(clone.PositionName) && clone.AxisBindings.Count == 0)
                continue;

            list.Add(clone);
        }

        return list;
    }

    public void SetItems(IEnumerable<PositionParameterItem> items)
    {
        _suspendAutoSave = true;
        _grid.Rows.Clear();

        foreach (var item in items)
            AddItemRow(item);

        _suspendAutoSave = false;
        SaveConfig();
        ReapplyKeywordFilterIfAny();
    }

    private void BuildUi()
    {
        _grid.DefaultCellStyle.SelectionBackColor = Color.Gainsboro;
        _grid.DefaultCellStyle.SelectionForeColor = Color.Black;
        _grid.RowTemplate.Height = 30;

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "位置名称", FillWeight = 20, ReadOnly = true });
        for (int i = 1; i <= 5; i++)
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = $"轴{i}数据", FillWeight = 16, ReadOnly = true });

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            // 按钮较多，窄屏时自动换行，避免「保存快照/一键恢复」被挤出可视区域
            WrapContents = true,
            Padding = new Padding(0, 0, 0, 4)
        };

        actions.Controls.AddRange([
            _btnEdit, _btnEditData, _btnRefresh, _btnSave, _btnLoad, _btnImportExcel,
            _btnSaveSnapshot, _btnRestoreSnapshot,
            _cmbKeyword, _btnSearch, _btnClearKeyword
        ]);

        Controls.Add(_grid);
        Controls.Add(actions);
    }

    private void BindEvents()
    {
        _menuTeach.Click += async (_, _) => await TeachSelectedRowAsync();
        _menuLocate.Click += async (_, _) => await LocateSelectedRowAsync();
        _menuSavePosition.Click += async (_, _) => await SaveSelectedRowPositionAsync();
        _rowMenu.Items.AddRange([_menuTeach, _menuLocate, _menuSavePosition]);

        _btnEdit.Click += (_, _) => EditSelectedPosition();
        _btnEditData.Click += async (_, _) => await EditSelectedRowDataAsync();
        _btnRefresh.Click += async (_, _) => await RefreshDataAsync();
        _btnSave.Click += (_, _) => SaveConfig();
        _btnLoad.Click += (_, _) => LoadConfig();
        _btnImportExcel.Click += (_, _) => ImportExcel();
        _btnSaveSnapshot.Click += async (_, _) => await SaveSnapshotAsync(auto: false);
        _btnRestoreSnapshot.Click += async (_, _) => await RestoreSnapshotAsync();

        // 定时快照：PLC 已连接时按 AutoSnapshotIntervalMs 周期落盘，供 PLC 丢数据后一键恢复
        _autoSnapshotTimer.Tick += async (_, _) => await SaveSnapshotAsync(auto: true);
        _autoSnapshotTimer.Start();
        _btnSearch.Click += (_, _) => ApplyKeywordFilter(saveKeyword: true);
        _btnClearKeyword.Click += (_, _) => ClearKeywords();
        _cmbKeyword.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                ApplyKeywordFilter(saveKeyword: true);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };
        _grid.CellBeginEdit += (_, e) => e.Cancel = true;
        _grid.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0)
                return;

            _grid.ClearSelection();
            _grid.Rows[e.RowIndex].Selected = true;
            if (e.ColumnIndex >= 0)
                _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
            _rowMenu.Show(_grid, e.Location);
        };
    }

    private void UpdateRowMenuState()
    {
        bool enabled = _canModify;
        _menuTeach.Enabled = enabled;
        _menuLocate.Enabled = enabled;
        _menuSavePosition.Enabled = enabled;
    }

    private void ReapplyKeywordFilterIfAny()
    {
        if (string.IsNullOrWhiteSpace(_cmbKeyword.Text.Trim()))
            return;
        ApplyKeywordFilter(saveKeyword: false);
    }

    private void ApplyKeywordFilter(bool saveKeyword)
    {
        string keyword = _cmbKeyword.Text.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            foreach (DataGridViewRow row in _grid.Rows)
                row.Visible = true;

            LogAction?.Invoke("已取消关键字筛选，显示全部位置。");
            return;
        }

        foreach (DataGridViewRow row in _grid.Rows)
            row.Visible = IsRowMatchKeyword(row, keyword);

        if (saveKeyword)
        {
            AddKeyword(keyword);
            SaveKeywords();
        }

        LogAction?.Invoke($"关键字筛选: {keyword}");
    }

    private static bool IsRowMatchKeyword(DataGridViewRow row, string keyword)
    {
        var parts = new List<string>();
        for (int i = 0; i < row.Cells.Count; i++)
            parts.Add(Convert.ToString(row.Cells[i].Value, CultureInfo.InvariantCulture) ?? string.Empty);

        string text = string.Join("|", parts);
        return text.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private void AddKeyword(string keyword)
    {
        _keywords.RemoveAll(x => string.Equals(x, keyword, StringComparison.OrdinalIgnoreCase));
        _keywords.Insert(0, keyword);

        if (_keywords.Count > 30)
            _keywords.RemoveRange(30, _keywords.Count - 30);

        RebindKeywordCombo();
    }

    private void ClearKeywords()
    {
        _cmbKeyword.Text = string.Empty;
        foreach (DataGridViewRow row in _grid.Rows)
            row.Visible = true;

        _keywords.Clear();
        RebindKeywordCombo();
        SaveKeywords();
        LogAction?.Invoke("已一键清除关键字并恢复全部位置显示。");
    }

    private void RebindKeywordCombo()
    {
        _cmbKeyword.Items.Clear();
        _cmbKeyword.Items.AddRange(_keywords.Cast<object>().ToArray());
    }

    private void LoadKeywords()
    {
        try
        {
            if (!File.Exists(_keywordPath))
                return;

            string json = File.ReadAllText(_keywordPath);
            List<string>? keywords = JsonSerializer.Deserialize<List<string>>(json);
            if (keywords is null)
                return;

            _keywords.Clear();
            _keywords.AddRange(keywords.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
            RebindKeywordCombo();
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"加载位置参数关键字失败: {ex.Message}");
        }
    }

    private void SaveKeywords()
    {
        try
        {
            File.WriteAllText(_keywordPath, JsonSerializer.Serialize(_keywords, JsonFileOptions));
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"保存位置参数关键字失败: {ex.Message}");
        }
    }

    private async Task TeachSelectedRowAsync()
    {
        var row = GetSelectedRow();
        if (row?.Tag is not PositionParameterItem item)
            return;

        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
        {
            LogAction?.Invoke("请先连接 PLC。");
            return;
        }

        var values = await ReadAxisCurrentPositionsAsync(plc, item);
        if (values.Count == 0)
            return;

        _teachedValues[item] = values;
        FillRowDisplay(row, item, values.ToDictionary(x => x.Key, x => x.Value.ToString("0.###", CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase));
        LogAction?.Invoke($"试教完成: {item.PositionName}");

        foreach (var pair in values)
        {
            LogManualPosition(
                "试教",
                item.PositionName,
                axisName: pair.Key,
                address: item.AxisBindings.FirstOrDefault(b => string.Equals(b.AxisName?.Trim(), pair.Key.Trim(), StringComparison.OrdinalIgnoreCase))?.Address,
                afterValue: pair.Value.ToString("0.###", CultureInfo.InvariantCulture),
                detail: "读取轴当前位置填入表格");
        }
    }

    private async Task LocateSelectedRowAsync()
    {
        var row = GetSelectedRow();
        if (row?.Tag is not PositionParameterItem item)
            return;

        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
        {
            LogAction?.Invoke("请先连接 PLC。");
            return;
        }

        if (CountEffectiveAxisBindings(item) == 1)
        {
            foreach (var binding in item.AxisBindings)
            {
                if (string.IsNullOrWhiteSpace(binding.AxisName) || string.IsNullOrWhiteSpace(binding.Address))
                    continue;

                string axisName = binding.AxisName.Trim();
                string? relMoveDistanceAddress = AxisFieldAddressResolver?.Invoke(axisName, "RelMoveDistance")?.Trim();
                string? absMoveAddress = AxisFieldAddressResolver?.Invoke(axisName, "AbsMove")?.Trim();
                if (string.IsNullOrWhiteSpace(relMoveDistanceAddress) || string.IsNullOrWhiteSpace(absMoveAddress))
                {
                    LogAction?.Invoke($"轴[{axisName}]未配置 RelMoveDistance 或 AbsMove 地址。");
                    continue;
                }

                try
                {
                    double target = await plc.ReadDoubleAsync(binding.Address.Trim());
                    await plc.WriteDoubleAsync(relMoveDistanceAddress, target);
                    await plc.WriteBoolAsync(absMoveAddress, true);
                    await Task.Delay(50);
                    await plc.WriteBoolAsync(absMoveAddress, false);
                }
                catch (Exception ex)
                {
                    LogAction?.Invoke($"定位失败 轴[{axisName}]: {ex.Message}");
                }
            }

            LogAction?.Invoke($"定位运动完成: {item.PositionName}");
            return;
        }

        string triggerAddr = (item.LocateTriggerPlcAddress ?? string.Empty).Trim();
        string pointAddr = (item.PointNumberPlcAddress ?? string.Empty).Trim();
        string pointDataText = (item.PointNumberData ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(triggerAddr) || string.IsNullOrWhiteSpace(pointAddr))
        {
            LogAction?.Invoke($"定位运动失败 [{item.PositionName}]：请在「地址绑定」中配置触发定位 PLC 地址与点位编号 PLC 地址。");
            return;
        }

        if (string.IsNullOrWhiteSpace(pointDataText))
        {
            LogAction?.Invoke($"定位运动失败 [{item.PositionName}]：点位编号数据为空。");
            return;
        }

        try
        {
            await WritePointNumberValueAsync(plc, pointAddr, pointDataText);
            await plc.WriteBoolAsync(triggerAddr, true);
            await Task.Delay(100);
            await plc.WriteBoolAsync(triggerAddr, false);
            LogAction?.Invoke($"定位运动完成: {item.PositionName}");
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"定位运动失败 [{item.PositionName}]: {ex.Message}");
        }
    }

    private static int CountEffectiveAxisBindings(PositionParameterItem item)
    {
        int n = 0;
        foreach (var b in item.AxisBindings)
        {
            if (!string.IsNullOrWhiteSpace(b.AxisName) && !string.IsNullOrWhiteSpace(b.Address))
                n++;
        }

        return n;
    }

    private static async Task WritePointNumberValueAsync(global::PlcConectCom.PlcCommunicator plc, string address, string dataText)
    {
        string s = dataText.Trim();
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long li))
        {
            if (li >= short.MinValue && li <= short.MaxValue)
                await plc.WriteIntAsync(address, (short)li);
            else if (li >= 0 && li <= uint.MaxValue)
                await plc.WriteDWordAsync(address, (uint)li);
            else
                await plc.WriteDoubleAsync(address, li);
            return;
        }

        if (double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double d))
        {
            await plc.WriteDoubleAsync(address, d);
            return;
        }

        throw new FormatException($"无法解析点位编号数据: {dataText}");
    }

    private async Task SaveSelectedRowPositionAsync()
    {
        var row = GetSelectedRow();
        if (row?.Tag is not PositionParameterItem item)
            return;

        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
        {
            LogAction?.Invoke("请先连接 PLC。");
            return;
        }

        var values = ReadDisplayedAxisValues(row, item);
        if (values.Count == 0)
        {
            LogAction?.Invoke($"保存位置失败: {item.PositionName} 当前行没有有效位置数据。");
            return;
        }

        foreach (var binding in item.AxisBindings)
        {
            if (string.IsNullOrWhiteSpace(binding.AxisName) || string.IsNullOrWhiteSpace(binding.Address))
                continue;

            if (!values.TryGetValue(binding.AxisName.Trim(), out double value))
                continue;

            try
            {
                await plc.WriteDoubleAsync(binding.Address.Trim(), value);
                LogManualPosition("保存位置", item.PositionName, binding.AxisName, binding.Address.Trim(),
                    afterValue: value.ToString("0.###"), detail: "表格数值写入 PLC");
            }
            catch (Exception ex)
            {
                LogAction?.Invoke($"保存位置失败 轴[{binding.AxisName}]: {ex.Message}");
                LogManualPosition("保存位置", item.PositionName, binding.AxisName, binding.Address.Trim(),
                    afterValue: value.ToString("0.###"), detail: $"写入失败：{ex.Message}");
            }
        }

        await RefreshDataAsync();
        LogAction?.Invoke($"位置保存完成: {item.PositionName}");

        // 试教写入成功后自动留一份快照：否则 PLC 丢数据时，周期性快照可能还是 5 分钟前的旧值
        await SaveSnapshotAsync(auto: true);
    }

    private static Dictionary<string, double> ReadDisplayedAxisValues(DataGridViewRow row, PositionParameterItem item)
    {
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < item.AxisBindings.Count; i++)
        {
            var binding = item.AxisBindings[i];
            if (string.IsNullOrWhiteSpace(binding.AxisName) || i + 1 >= row.Cells.Count)
                continue;

            string text = Convert.ToString(row.Cells[i + 1].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            int colonIndex = text.IndexOf(':');
            if (colonIndex < 0)
                colonIndex = text.IndexOf('：');

            string valueText = colonIndex >= 0 ? text[(colonIndex + 1)..].Trim() : text;
            if (string.IsNullOrWhiteSpace(valueText)
                || string.Equals(valueText, "--", StringComparison.OrdinalIgnoreCase)
                || string.Equals(valueText, "ERR", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                && !double.TryParse(valueText, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
                continue;

            values[binding.AxisName.Trim()] = value;
        }

        return values;
    }

    private async Task<Dictionary<string, double>> ReadAxisCurrentPositionsAsync(global::PlcConectCom.PlcCommunicator plc, PositionParameterItem item)
    {
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in item.AxisBindings)
        {
            if (string.IsNullOrWhiteSpace(binding.AxisName))
                continue;

            string axisName = binding.AxisName.Trim();
            string? currentPosAddress = AxisFieldAddressResolver?.Invoke(axisName, "CurrentPos")?.Trim();
            if (string.IsNullOrWhiteSpace(currentPosAddress))
            {
                LogAction?.Invoke($"轴[{axisName}]未配置 CurrentPos 地址。");
                continue;
            }

            try
            {
                values[axisName] = await plc.ReadDoubleAsync(currentPosAddress);
            }
            catch (Exception ex)
            {
                LogAction?.Invoke($"试教读取失败 轴[{axisName}]: {ex.Message}");
            }
        }

        return values;
    }

    private DataGridViewRow? GetSelectedRow()
    {
        return _grid.SelectedRows.Count == 0 ? null : _grid.SelectedRows[0];
    }

    private void RemoveSelectedPositionWithConfirm()
    {
        if (_grid.SelectedRows.Count == 0)
            return;

        var row = _grid.SelectedRows[0];
        string positionName = row.Tag is PositionParameterItem item
            ? item.PositionName
            : Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture)?.Trim() ?? "该位置";

        DialogResult confirm = MessageBox.Show(
            this,
            $"确定要删除位置「{positionName}」吗？",
            "确认删除",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);

        if (confirm != DialogResult.OK)
            return;

        _grid.Rows.RemoveAt(row.Index);
        SaveConfig();
        ReapplyKeywordFilterIfAny();
    }

    private async Task EditSelectedRowDataAsync()
    {
        if (!_canModify)
            return;

        if (_grid.SelectedRows.Count == 0)
            return;

        if (_grid.SelectedRows[0].Tag is not PositionParameterItem item || item.AxisBindings.Count == 0)
            return;

        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
        {
            LogAction?.Invoke("请先连接 PLC。");
            return;
        }

        using var dialog = new Form
        {
            Text = $"编辑位置数据 - {item.PositionName}",
            Width = 420,
            Height = 320,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = item.AxisBindings.Count,
            Padding = new Padding(10)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < item.AxisBindings.Count; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

        var inputs = new List<(string axisName, string address, TextBox input)>();
        var beforeByAddress = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in item.AxisBindings)
        {
            if (string.IsNullOrWhiteSpace(binding.AxisName) || string.IsNullOrWhiteSpace(binding.Address))
                continue;

            int rowIndex = inputs.Count;
            layout.Controls.Add(new Label { Text = binding.AxisName, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Padding = new Padding(0, 4, 8, 0) }, 0, rowIndex);
            var txt = new TextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
            try
            {
                double v = await plc.ReadDoubleAsync(binding.Address);
                txt.Text = v.ToString("0.###");
            }
            catch
            {
                txt.Text = string.Empty;
            }

            layout.Controls.Add(txt, 1, rowIndex);
            inputs.Add((binding.AxisName, binding.Address, txt));
            beforeByAddress[binding.Address.Trim()] = txt.Text;
        }

        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8)
        };
        var writeValues = new List<(string axisName, string address, double value)>();
        var btnOk = new Button { Text = "确定", Width = 72 };
        var btnCancel = new Button { Text = "取消", Width = 72, DialogResult = DialogResult.Cancel };
        btnOk.Click += (_, _) =>
        {
            writeValues.Clear();
            foreach (var x in inputs)
            {
                if (!TryParseAxisInputText(x.input.Text, out double value))
                {
                    LogAction?.Invoke($"{x.axisName} 数值格式错误。");
                    return;
                }

                writeValues.Add((x.axisName, x.address.Trim(), value));
            }

            dialog.DialogResult = DialogResult.OK;
            dialog.Close();
        };
        btnPanel.Controls.Add(btnOk);
        btnPanel.Controls.Add(btnCancel);

        dialog.Controls.Add(layout);
        dialog.Controls.Add(btnPanel);
        dialog.AcceptButton = btnOk;
        dialog.CancelButton = btnCancel;

        DataGridViewNoSortHelper.WireControlSubtreeForNoColumnSort(dialog);
        AppDialog.ShowDialog(dialog, this);
        if (writeValues.Count == 0)
            return;

        foreach (var x in writeValues)
        {
            try
            {
                await plc.WriteDoubleAsync(x.address, x.value);
            }
            catch (Exception ex)
            {
                LogAction?.Invoke($"写入 PLC 失败 轴[{x.axisName}] 地址[{x.address}]: {ex.Message}");
                LogManualPosition("编辑数据", item.PositionName, x.axisName, x.address,
                    beforeByAddress.GetValueOrDefault(x.address), x.value.ToString("0.###"),
                    $"写入失败：{ex.Message}");
                return;
            }

            LogManualPosition("编辑数据", item.PositionName, x.axisName, x.address,
                beforeByAddress.GetValueOrDefault(x.address), x.value.ToString("0.###"),
                "写入 PLC 成功");
        }

        await RefreshDataAsync();
        LogAction?.Invoke($"位置数据已写入 PLC: {item.PositionName}");
    }

    private static bool TryParseAxisInputText(string? text, out double value)
    {
        value = 0;
        string s = text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(s))
            return false;

        return double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value)
            || double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value);
    }

    public async Task RefreshDataAsync()
    {
        try
        {
            var plc = PlcProvider?.Invoke() ?? PlcInstance;
            if (plc is not { IsConnected: true })
            {
                foreach (DataGridViewRow row in _grid.Rows)
                    FillRowDisplay(row, row.Tag as PositionParameterItem, null);
                return;
            }

            var valuesByItem = new Dictionary<PositionParameterItem, Dictionary<string, string>>();
        var mdRequests = new List<PositionValueReadRequest>();
        var fallbackRequests = new List<PositionValueReadRequest>();

        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is not PositionParameterItem item)
                continue;

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            valuesByItem[item] = values;

            foreach (var binding in item.AxisBindings)
            {
                if (string.IsNullOrWhiteSpace(binding.AxisName) || string.IsNullOrWhiteSpace(binding.Address))
                    continue;

                string axisName = binding.AxisName.Trim();
                string address = binding.Address.Trim();
                try
                {
                    var (memType, offset, _) = global::PlcConectCom.AddressParser.ParseAddress(address);
                    if (string.Equals(memType, "MD", StringComparison.OrdinalIgnoreCase))
                        mdRequests.Add(new PositionValueReadRequest(row, item, axisName, address, offset));
                    else
                        fallbackRequests.Add(new PositionValueReadRequest(row, item, axisName, address, 0));
                }
                catch
                {
                    fallbackRequests.Add(new PositionValueReadRequest(row, item, axisName, address, 0));
                }
            }
        }

        List<PositionValueReadBatch> batches = BuildPositionReadBatches(mdRequests);
        foreach (var batch in batches)
        {
            string startAddress = $"MD{batch.StartOffset}";
            int count = batch.EndOffset - batch.StartOffset + 1;
            try
            {
                double[] values = await plc.ReadDoubleArrayAsync(startAddress, count);
                foreach (var request in batch.Requests)
                {
                    int index = request.Offset - batch.StartOffset;
                    if (index < 0 || index >= values.Length)
                    {
                        valuesByItem[request.Item][request.AxisName] = "ERR";
                        continue;
                    }

                    valuesByItem[request.Item][request.AxisName] = values[index].ToString("0.###", CultureInfo.InvariantCulture);
                }
            }
            catch
            {
                foreach (var request in batch.Requests)
                    fallbackRequests.Add(request);
            }
        }

        foreach (var request in fallbackRequests)
        {
            try
            {
                double value = await plc.ReadDoubleAsync(request.Address);
                valuesByItem[request.Item][request.AxisName] = value.ToString("0.###", CultureInfo.InvariantCulture);
            }
            catch
            {
                valuesByItem[request.Item][request.AxisName] = "ERR";
            }
        }

            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.Tag is not PositionParameterItem item)
                    continue;

                valuesByItem.TryGetValue(item, out Dictionary<string, string>? values);
                FillRowDisplay(row, item, values);
            }
        }
        finally
        {
            ReapplyKeywordFilterIfAny();
        }
    }

    private static List<PositionValueReadBatch> BuildPositionReadBatches(List<PositionValueReadRequest> requests)
    {
        const int maxSpan = 120;
        var batches = new List<PositionValueReadBatch>();
        if (requests.Count == 0)
            return batches;

        var ordered = requests.OrderBy(x => x.Offset).ToList();
        int cursor = 0;
        while (cursor < ordered.Count)
        {
            int start = ordered[cursor].Offset;
            int end = ordered[cursor].Offset;
            var bucket = new List<PositionValueReadRequest> { ordered[cursor] };
            cursor++;

            while (cursor < ordered.Count)
            {
                var next = ordered[cursor];
                int mergedEnd = Math.Max(end, next.Offset);
                if (mergedEnd - start + 1 > maxSpan)
                    break;

                bucket.Add(next);
                end = mergedEnd;
                cursor++;
            }

            batches.Add(new PositionValueReadBatch(start, end, bucket));
        }

        return batches;
    }

    private void AddPositionByDialog()
    {
        if (!_canModify)
            return;

        var created = ShowPositionDialog();
        if (created is null)
            return;

        AddItemRow(created);
        SaveConfig();
        _ = RefreshDataAsync();
    }

    private void EditSelectedPosition()
    {
        if (!_canModify)
            return;

        if (_grid.SelectedRows.Count == 0)
            return;

        var row = _grid.SelectedRows[0];
        if (row.Tag is not PositionParameterItem existing)
            return;

        var edited = ShowPositionDialog(existing);
        if (edited is null)
            return;

        row.Tag = edited;
        FillRowDisplay(row, edited, null);
        SaveConfig();
        _ = RefreshDataAsync();
    }

    private void ImportExcel()
    {
        if (!_canAddDelete || !_canModify)
            return;

        using var dialog = new OpenFileDialog
        {
            Title = "选择多轴位置Excel文件",
            Filter = "Excel 文件 (*.xls;*.xlsx)|*.xls;*.xlsx|所有文件 (*.*)|*.*",
            InitialDirectory = AppContext.BaseDirectory
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            List<ImportedPositionRow> rows = ReadImportedPositionRows(dialog.FileName);
            var items = rows
                .Select(BuildPositionItem)
                .Where(x => x.AxisBindings.Count > 0)
                .ToList();

            if (items.Count == 0)
            {
                MessageBox.Show(this, "未解析到有效的多轴位置数据。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SetItems(items);
            _ = RefreshDataAsync();
            LogAction?.Invoke($"位置参数Excel导入成功，共 {items.Count} 个位置。来源: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导入失败: {ex.Message}", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            LogAction?.Invoke($"位置参数Excel导入失败: {ex.Message}");
        }
    }

    private static List<ImportedPositionRow> ReadImportedPositionRows(string fileName)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        using var stream = File.Open(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateReader(stream);

        var rows = new List<ImportedPositionRow>();
        bool isHeader = true;
        int nameCol = 1;
        int addressCol = 2;
        int dataTypeCol = 3;
        int commentCol = -1;

        while (reader.Read())
        {
            if (isHeader)
            {
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    string header = GetCellText(reader, i);
                    if (string.Equals(header, "Name", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(header, "名称", StringComparison.OrdinalIgnoreCase))
                        nameCol = i;
                    else if (string.Equals(header, "Address", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(header, "Adress", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(header, "地址", StringComparison.OrdinalIgnoreCase))
                        addressCol = i;
                    else if (string.Equals(header, "DataType", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(header, "数据类型", StringComparison.OrdinalIgnoreCase))
                        dataTypeCol = i;
                    else if (string.Equals(header, "Comment", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(header, "注释", StringComparison.OrdinalIgnoreCase))
                        commentCol = i;
                }

                isHeader = false;
                continue;
            }

            string name = GetCellText(reader, nameCol);
            string address = NormalizeImportedAddress(GetCellText(reader, addressCol));
            string dataTypeText = GetCellText(reader, dataTypeCol);
            string comment = commentCol >= 0 ? GetCellText(reader, commentCol) : string.Empty;

            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(address))
                continue;

            rows.Add(new ImportedPositionRow(name, address, dataTypeText, comment));
        }

        if (rows.Count == 0)
            throw new InvalidOperationException("未读取到有效数据。");

        return rows;
    }

    private static PositionParameterItem BuildPositionItem(ImportedPositionRow row)
    {
        (string locateTrigger, string pointNumAddr, string pointNumData) =
            ExcelPositionCommentAxisGroupParser.ParseAxisGroupPlcFields(row.Comment);
        string commentForAxes = ExcelPositionCommentAxisGroupParser.CommentPrefixForIndexedAxisNames(row.Comment);

        int axisCount = InferAxisCount(row.DataTypeText, commentForAxes);
        List<string> axisNames = ParseAxisNamesFromComment(commentForAxes, axisCount);
        var bindings = new List<PositionAxisBinding>(axisNames.Count);

        for (int i = 0; i < axisNames.Count; i++)
        {
            bindings.Add(new PositionAxisBinding
            {
                AxisName = axisNames[i],
                Address = BuildAxisAddress(row.Address, i)
            });
        }

        return new PositionParameterItem
        {
            PositionName = row.Name,
            AxisBindings = bindings,
            AxisNames = bindings.Select(x => x.AxisName).ToList(),
            LocateTriggerPlcAddress = locateTrigger,
            PointNumberPlcAddress = pointNumAddr,
            PointNumberData = pointNumData
        };
    }

    private static string GetCellText(IExcelDataReader reader, int index)
    {
        if (index < 0 || index >= reader.FieldCount)
            return string.Empty;

        return Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
    }

    private static string NormalizeImportedAddress(string rawAddress)
    {
        string address = rawAddress.Trim();
        if (address.StartsWith('%'))
            address = address[1..];

        return address;
    }

    private static int InferAxisCount(string dataTypeText, string comment)
    {
        ParameterImportDataTypeResult dataType = ParameterTableControl.ParseImportDataType(dataTypeText);
        if (dataType.Type == ParameterValueType.REAL_ARRAY && dataType.RealArrayLength > 0)
            return Math.Min(dataType.RealArrayLength, MaxImportAxisCount);

        int countFromComment = IndexedAxisCommentRegex.Matches(comment).Count;
        if (countFromComment > 0)
            return Math.Min(countFromComment, MaxImportAxisCount);

        return string.Equals(dataTypeText.Trim(), "REAL", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    private static List<string> ParseAxisNamesFromComment(string comment, int axisCount)
    {
        var byIndex = new SortedDictionary<int, string>();
        foreach (Match match in IndexedAxisCommentRegex.Matches(comment))
        {
            if (!int.TryParse(match.Groups["index"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                continue;

            string name = match.Groups["name"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(name))
                byIndex[index] = name;
        }

        var names = new List<string>(axisCount);
        foreach (string name in byIndex.Values.Take(axisCount))
            names.Add(name);

        if (names.Count == 0 && axisCount == 1 && !string.IsNullOrWhiteSpace(comment))
            names.Add(comment.Trim());

        while (names.Count < axisCount)
            names.Add($"轴{names.Count + 1}");

        return names;
    }

    private static string BuildAxisAddress(string baseAddress, int indexDelta)
    {
        if (string.IsNullOrWhiteSpace(baseAddress))
            return string.Empty;

        try
        {
            var (mem, offset, _) = global::PlcConectCom.AddressParser.ParseAddress(baseAddress.Trim());
            if (string.Equals(mem, "MD", StringComparison.Ordinal))
                return $"MD{offset + indexDelta}";
        }
        catch
        {
            // 非标准地址保留首轴地址，其他轴留空，方便用户手动补全。
        }

        return indexDelta == 0 ? baseAddress.Trim() : string.Empty;
    }

    private sealed record ImportedPositionRow(string Name, string Address, string DataTypeText, string Comment);

    private PositionParameterItem? ShowPositionDialog(PositionParameterItem? existing = null)
    {
        var axisNames = AxisNameProvider?.Invoke() ?? [];
        if (axisNames.Count == 0)
        {
            LogAction?.Invoke("请先在轴配置表中新增轴。");
            return null;
        }

        using var dialog = new Form
        {
            Text = existing is null ? "新增位置参数" : "编辑位置参数",
            Width = 420,
            Height = 470,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            // 0~9 为内容行，第 10 行为空白的弹性行：吸收窗体富余高度。
            // 若不加，富余高度会被末行吃掉（实测末行被拉到 110px），
            // 标签 Anchor=Left 垂直居中下沉，而单行 TextBox 高度固定，导致标签与输入框错位
            RowCount = 11,
            Padding = new Padding(10)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int r = 0; r < 10; r++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        layout.Controls.Add(new Label { Text = "位置名称", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        var txtPositionName = new TextBox { Dock = DockStyle.Fill };
        layout.SetColumnSpan(txtPositionName, 2);
        layout.Controls.Add(txtPositionName, 1, 0);

        var axisCombos = new List<ComboBox>();
        var axisAddressInputs = new List<TextBox>();
        layout.Controls.Add(new Label { Text = "轴名称", AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font(Font, FontStyle.Bold) }, 1, 1);
        layout.Controls.Add(new Label { Text = "地址", AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font(Font, FontStyle.Bold) }, 2, 1);
        for (int i = 0; i < 5; i++)
        {
            int rowIndex = i + 2;
            layout.Controls.Add(new Label { Text = $"轴{i + 1}", AutoSize = true, Anchor = AnchorStyles.Left }, 0, rowIndex);
            var cmb = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            cmb.Items.Add(string.Empty);
            cmb.Items.AddRange(axisNames.Distinct(StringComparer.OrdinalIgnoreCase).Cast<object>().ToArray());
            cmb.SelectedIndex = 0;
            axisCombos.Add(cmb);

            var txtAddress = new TextBox { Dock = DockStyle.Fill };
            axisAddressInputs.Add(txtAddress);

            layout.Controls.Add(cmb, 1, rowIndex);
            layout.Controls.Add(txtAddress, 2, rowIndex);
        }

        const int rowLocateTrigger = 7;
        const int rowPointAddr = 8;
        const int rowPointData = 9;

        layout.Controls.Add(new Label { Text = "触发定位 PLC 地址", AutoSize = true, Anchor = AnchorStyles.Left }, 0, rowLocateTrigger);
        var txtLocateTrigger = new TextBox { Dock = DockStyle.Fill };
        layout.SetColumnSpan(txtLocateTrigger, 2);
        layout.Controls.Add(txtLocateTrigger, 1, rowLocateTrigger);

        layout.Controls.Add(new Label { Text = "点位编号 PLC 地址", AutoSize = true, Anchor = AnchorStyles.Left }, 0, rowPointAddr);
        var txtPointNumberAddr = new TextBox { Dock = DockStyle.Fill };
        layout.SetColumnSpan(txtPointNumberAddr, 2);
        layout.Controls.Add(txtPointNumberAddr, 1, rowPointAddr);

        layout.Controls.Add(new Label { Text = "点位编号数据", AutoSize = true, Anchor = AnchorStyles.Left }, 0, rowPointData);
        var txtPointNumberData = new TextBox { Dock = DockStyle.Fill };
        layout.SetColumnSpan(txtPointNumberData, 2);
        layout.Controls.Add(txtPointNumberData, 1, rowPointData);

        var panelButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8)
        };

        var btnOk = new Button { Text = "确定", Width = 72, DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "取消", Width = 72, DialogResult = DialogResult.Cancel };
        panelButtons.Controls.Add(btnOk);
        panelButtons.Controls.Add(btnCancel);

        dialog.Controls.Add(layout);
        dialog.Controls.Add(panelButtons);
        dialog.AcceptButton = btnOk;
        dialog.CancelButton = btnCancel;

        if (existing is not null)
        {
            txtPositionName.Text = existing.PositionName;
            for (int i = 0; i < Math.Min(5, existing.AxisBindings.Count); i++)
            {
                var b = existing.AxisBindings[i];
                string axisName = b.AxisName ?? string.Empty;
                int idx = axisCombos[i].Items.IndexOf(axisName);
                axisCombos[i].SelectedIndex = idx >= 0 ? idx : 0;
                axisAddressInputs[i].Text = b.Address ?? string.Empty;
            }

            txtLocateTrigger.Text = existing.LocateTriggerPlcAddress ?? string.Empty;
            txtPointNumberAddr.Text = existing.PointNumberPlcAddress ?? string.Empty;
            txtPointNumberData.Text = existing.PointNumberData ?? string.Empty;
        }

        DataGridViewNoSortHelper.WireControlSubtreeForNoColumnSort(dialog);
        if (AppDialog.ShowDialog(dialog, this) != DialogResult.OK)
            return null;

        string positionName = txtPositionName.Text.Trim();
        if (string.IsNullOrWhiteSpace(positionName))
        {
            LogAction?.Invoke("位置名称不能为空。");
            return null;
        }

        var selectedBindings = new List<PositionAxisBinding>();
        var usedAxisNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 5; i++)
        {
            string axisName = axisCombos[i].SelectedItem?.ToString() ?? string.Empty;
            string address = axisAddressInputs[i].Text.Trim();
            if (string.IsNullOrWhiteSpace(axisName) && string.IsNullOrWhiteSpace(address))
                continue;

            if (string.IsNullOrWhiteSpace(axisName))
                continue;

            if (!usedAxisNames.Add(axisName))
                continue;

            selectedBindings.Add(new PositionAxisBinding
            {
                AxisName = axisName,
                Address = address
            });
        }

        return new PositionParameterItem
        {
            PositionName = positionName,
            AxisBindings = selectedBindings,
            AxisNames = selectedBindings.Select(x => x.AxisName).ToList(),
            LocateTriggerPlcAddress = txtLocateTrigger.Text.Trim(),
            PointNumberPlcAddress = txtPointNumberAddr.Text.Trim(),
            PointNumberData = txtPointNumberData.Text.Trim()
        };
    }

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(_configPath))
                return;

            string json = File.ReadAllText(_configPath);
            var items = JsonSerializer.Deserialize<List<PositionParameterItem>>(json);
            if (items is null)
                return;

            _suspendAutoSave = true;
            _grid.Rows.Clear();
            foreach (var item in items)
                AddItemRow(item);
            _suspendAutoSave = false;
            _ = RefreshDataAsync();
        }
        catch (Exception ex)
        {
            _suspendAutoSave = false;
            LogAction?.Invoke($"加载位置参数配置失败: {ex.Message}");
        }
    }

    private void SaveConfig()
    {
        if (_suspendAutoSave)
            return;

        try
        {
            var items = GetItems();
            string json = JsonSerializer.Serialize(items, JsonFileOptions);
            File.WriteAllText(_configPath, json);
            LogAction?.Invoke("位置参数配置已保存。");
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"保存位置参数配置失败: {ex.Message}");
        }
    }

    /// <summary>快照文件位置（exe 输出目录下的 PositionParameterTable.snapshot[.键].json）。</summary>
    public string SnapshotFilePath => _snapshotPath;

    /// <summary>
    /// 把全表各轴的「地址 + 数值」存一份本地 JSON。
    /// 落盘前先从 PLC 读一次最新值，保证快照是现场实际数据而不是陈旧显示。
    /// </summary>
    /// <param name="auto">true 表示定时自动触发（无连接时静默跳过，不刷日志）。</param>
    public async Task SaveSnapshotAsync(bool auto = false)
    {
        if (!_canModify)
            return;

        // 手动保存：先弹对话框选「默认位置」或「选择路径和文件名称（默认名称）」
        string targetPath = _snapshotPath;
        if (!auto)
        {
            string? chosen = ShowSaveSnapshotDialog();
            if (chosen is null)
            {
                LogAction?.Invoke("保存快照已取消。");
                return;
            }

            targetPath = chosen;
        }

        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
        {
            if (!auto)
                LogAction?.Invoke("保存快照失败：请先连接 PLC。");

            return;
        }

        await RefreshDataAsync();

        var file = new PositionSnapshotFile { SavedAt = DateTime.Now };
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is not PositionParameterItem item)
                continue;

            var values = ReadDisplayedAxisValues(row, item);
            if (values.Count == 0)
                continue;

            string positionName = Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            var snapshotItem = new PositionSnapshotItem { PositionName = positionName };
            foreach (var binding in item.AxisBindings)
            {
                if (string.IsNullOrWhiteSpace(binding.AxisName) || string.IsNullOrWhiteSpace(binding.Address))
                    continue;

                string axisName = binding.AxisName.Trim();
                if (!values.TryGetValue(axisName, out double value))
                    continue;

                snapshotItem.AxisValues.Add(new PositionSnapshotEntry
                {
                    AxisName = axisName,
                    Address = binding.Address.Trim(),
                    Value = value
                });
            }

            if (snapshotItem.AxisValues.Count > 0)
                file.Positions.Add(snapshotItem);
        }

        int total = file.Positions.Sum(p => p.AxisValues.Count);
        if (total == 0)
        {
            if (!auto)
                LogAction?.Invoke("保存快照失败：当前没有读到任何有效的轴数值。");

            return;
        }

        // 防止「快照污染」：读回的数值若全为 0（PLC 保持寄存器丢数据的典型表现），
        // 不要把空快照覆盖掉上一份有效快照。自动模式直接跳过，手动模式弹确认。
        bool allZero = file.Positions.SelectMany(p => p.AxisValues).All(e => Math.Abs(e.Value) < double.Epsilon);
        if (allZero)
        {
            if (auto)
            {
                LogAction?.Invoke("自动快照已跳过：读回的轴数值全为 0，疑似 PLC 数据丢失，保留上一份快照。");
                return;
            }

            DialogResult zeroConfirm = MessageBox.Show(
                this,
                "当前从 PLC 读回的轴数值全为 0，保存会覆盖上一份有效快照。确定仍要保存吗？",
                "保存快照确认",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (zeroConfirm != DialogResult.OK)
            {
                LogAction?.Invoke("保存快照已取消。");
                return;
            }
        }

        try
        {
            WriteSnapshotFile(targetPath, file);
            LogAction?.Invoke($"数值快照已保存（{(auto ? "自动" : "手动")}）：{file.Positions.Count} 个位置 / {total} 个轴值，{file.SavedAt:yyyy-MM-dd HH:mm:ss} → {targetPath}");
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"保存数值快照失败: {ex.Message}");
        }
    }

    private static void WriteSnapshotFile(string path, PositionSnapshotFile file)
        => File.WriteAllText(path, JsonSerializer.Serialize(file, JsonFileOptions));

    /// <summary>
    /// 保存快照的选择对话框：默认位置保存 / 选择路径和文件名称保存（默认名称按时间生成）。
    /// 返回目标文件完整路径；用户取消返回 null。
    /// </summary>
    private string? ShowSaveSnapshotDialog()
    {
        string? chosenPath = null;

        using var dialog = new Form
        {
            Text = "保存数值快照",
            ClientSize = new Size(560, 160),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var radioDefault = new RadioButton
        {
            Text = "默认位置保存（与自动快照同一文件）",
            AutoSize = true,
            Checked = true,
            Left = 16,
            Top = 14
        };
        var lblDefaultPath = new Label
        {
            Text = _snapshotPath,
            AutoSize = true,
            Left = 36,
            Top = 38,
            ForeColor = Color.DimGray
        };
        var radioCustom = new RadioButton
        {
            Text = "选择路径和文件名称保存",
            AutoSize = true,
            Left = 16,
            Top = 68
        };
        var btnOk = new Button { Text = "确定", Width = 80, Left = 352, Top = 116 };
        var btnCancel = new Button { Text = "取消", Width = 80, Left = 448, Top = 116, DialogResult = DialogResult.Cancel };

        btnOk.Click += (_, _) =>
        {
            if (radioCustom.Checked)
            {
                using var saveDialog = new SaveFileDialog
                {
                    Title = "保存数值快照",
                    Filter = "快照文件 (*.json)|*.json|所有文件 (*.*)|*.*",
                    FileName = $"PositionTable快照_{DateTime.Now:yyyyMMdd_HHmmss}.json",
                    InitialDirectory = AppContext.BaseDirectory
                };
                if (saveDialog.ShowDialog(dialog) != DialogResult.OK)
                    return;

                chosenPath = saveDialog.FileName;
            }
            else
            {
                chosenPath = _snapshotPath;
            }

            dialog.DialogResult = DialogResult.OK;
            dialog.Close();
        };

        dialog.Controls.AddRange([radioDefault, lblDefaultPath, radioCustom, btnOk, btnCancel]);
        dialog.CancelButton = btnCancel;
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return null;

        return string.IsNullOrWhiteSpace(Path.GetExtension(chosenPath))
            ? chosenPath + ".json"
            : chosenPath;
    }

    /// <summary>按快照把数值逐条写回 PLC。写前弹确认，逐项记录成功/失败。</summary>
    public async Task RestoreSnapshotAsync()
    {
        if (!_canModify)
            return;

        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
        {
            LogAction?.Invoke("一键恢复失败：请先连接 PLC。");
            return;
        }

        // 弹对话框选恢复来源：自动快照（默认文件）或从文件加载
        string? sourcePath = ShowRestoreSourceDialog(out string sourceDesc);
        if (sourcePath is null)
        {
            LogAction?.Invoke("一键恢复已取消。");
            return;
        }

        var (file, loadError) = TryLoadSnapshotFile(sourcePath);
        if (file is null)
        {
            LogAction?.Invoke($"一键恢复加载失败（{sourceDesc}）: {loadError}");
            MessageBox.Show(
                this,
                $"加载快照失败：{loadError}\n文件：{sourcePath}",
                "一键恢复",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        int total = CountSnapshotEntries(file);
        if (file.Positions.Count == 0 || total == 0)
        {
            LogAction?.Invoke($"一键恢复中止（{sourceDesc}）：文件中没有可恢复的轴数值。");
            MessageBox.Show(
                this,
                "该文件中没有可恢复的轴数值，可能不是数值快照文件（或误选了其他 JSON）。\n请重新选择正确的快照文件。",
                "一键恢复",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        // 内容校验：快照地址与当前表格绑定的地址比对，不匹配的会被跳过并明确提示
        var knownAddresses = CollectCurrentBindingAddresses();
        var unmatchedSamples = new List<string>();
        int unmatched = 0;
        foreach (var position in file.Positions)
        {
            foreach (var entry in position.AxisValues)
            {
                if (string.IsNullOrWhiteSpace(entry.Address) || !knownAddresses.Contains(entry.Address.Trim()))
                {
                    unmatched++;
                    if (unmatchedSamples.Count < 5)
                        unmatchedSamples.Add($"[{position.PositionName}] {entry.AxisName} {entry.Address}");
                }
            }
        }

        int matched = total - unmatched;
        if (matched == 0)
        {
            LogAction?.Invoke($"一键恢复中止（{sourceDesc}）：快照 {total} 项与当前表格地址无一匹配。");
            MessageBox.Show(
                this,
                $"快照里的 {total} 项地址与当前表格完全不匹配，可能选择了错误的快照文件。\n快照时间：{file.SavedAt:yyyy-MM-dd HH:mm:ss}\n\n请重新选择。",
                "一键恢复",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        string confirmText = unmatched > 0
            ? $"快照共 {total} 项：{matched} 项与当前表格匹配、{unmatched} 项不匹配（将被跳过），例如：\n{string.Join("\n", unmatchedSamples)}\n\n快照时间：{file.SavedAt:yyyy-MM-dd HH:mm:ss}\n\n请确认设备处于安全状态，是否继续？"
            : $"即将把快照中的 {total} 个轴数值写回 PLC。\n快照时间：{file.SavedAt:yyyy-MM-dd HH:mm:ss}\n\n请确认设备处于安全状态，确认继续？";
        DialogResult confirm = MessageBox.Show(
            this,
            $"即将把快照中的 {total} 个轴数值写回 PLC。\n快照时间：{file.SavedAt:yyyy-MM-dd HH:mm:ss}\n\n请确认设备处于安全状态，确认继续？",
            "一键恢复确认",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.OK)
        {
            LogAction?.Invoke("一键恢复已取消。");
            return;
        }

        int ok = 0;
        int fail = 0;
        int skipped = 0;
        foreach (var position in file.Positions)
        {
            foreach (var entry in position.AxisValues)
            {
                if (string.IsNullOrWhiteSpace(entry.Address) || !knownAddresses.Contains(entry.Address.Trim()))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    await plc.WriteDoubleAsync(entry.Address.Trim(), entry.Value);
                    ok++;
                }
                catch (Exception ex)
                {
                    fail++;
                    LogAction?.Invoke($"恢复失败 [{position.PositionName}] 轴[{entry.AxisName}] {entry.Address}: {ex.Message}");
                }
            }
        }

        await RefreshDataAsync();
        LogAction?.Invoke($"一键恢复完成（{sourceDesc}）：成功 {ok} 项，失败 {fail} 项，跳过不匹配 {skipped} 项。");
        LogManualPosition(
            "一键恢复",
            "全部位置",
            detail: $"来源：{sourceDesc}；成功 {ok} 项，失败 {fail} 项，跳过 {skipped} 项");
    }

    /// <summary>
    /// 恢复来源选择对话框：自动快照备份（默认文件，显示上次快照时间）/ 选择路径文件加载。
    /// 返回所选文件的完整路径；用户取消返回 null。
    /// </summary>
    private string? ShowRestoreSourceDialog(out string sourceDesc)
    {
        sourceDesc = string.Empty;
        string desc = string.Empty; // lambda 内不能写 out 参数，用局部变量中转
        bool defaultExists = File.Exists(_snapshotPath);
        string defaultSavedAtText = string.Empty;
        if (defaultExists)
        {
            var (defaultFile, _) = TryLoadSnapshotFile(_snapshotPath);
            defaultSavedAtText = defaultFile is null
                ? "（文件解析失败）"
                : $"（上次快照：{defaultFile.SavedAt:yyyy-MM-dd HH:mm:ss}）";
        }

        string? chosenPath = null;

        using var dialog = new Form
        {
            Text = "一键恢复数值",
            ClientSize = new Size(560, 170),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var radioDefault = new RadioButton
        {
            Text = $"自动快照备份 {defaultSavedAtText}",
            AutoSize = true,
            Checked = defaultExists,
            Enabled = defaultExists,
            Left = 16,
            Top = 14
        };
        var lblDefaultPath = new Label
        {
            Text = _snapshotPath,
            AutoSize = true,
            Left = 36,
            Top = 38,
            ForeColor = Color.DimGray
        };
        var radioCustom = new RadioButton
        {
            Text = "选择路径文件加载…",
            AutoSize = true,
            Checked = !defaultExists,
            Left = 16,
            Top = 68
        };
        var btnOk = new Button { Text = "确定", Width = 80, Left = 352, Top = 124 };
        var btnCancel = new Button { Text = "取消", Width = 80, Left = 448, Top = 124, DialogResult = DialogResult.Cancel };

        btnOk.Click += (_, _) =>
        {
            if (radioCustom.Checked)
            {
                using var openDialog = new OpenFileDialog
                {
                    Title = "选择数值快照文件",
                    Filter = "快照文件 (*.json)|*.json|所有文件 (*.*)|*.*",
                    InitialDirectory = AppContext.BaseDirectory
                };
                if (openDialog.ShowDialog(dialog) != DialogResult.OK)
                    return;

                chosenPath = openDialog.FileName;
                desc = $"文件: {chosenPath}";
            }
            else
            {
                chosenPath = _snapshotPath;
                desc = "自动快照";
            }

            dialog.DialogResult = DialogResult.OK;
            dialog.Close();
        };

        dialog.Controls.AddRange([radioDefault, lblDefaultPath, radioCustom, btnOk, btnCancel]);
        dialog.CancelButton = btnCancel;
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return null;

        sourceDesc = desc;
        return chosenPath;
    }

    /// <summary>统计快照里的轴值总数。</summary>
    private static int CountSnapshotEntries(PositionSnapshotFile file)
        => file.Positions.Sum(p => p.AxisValues.Count);

    /// <summary>读取快照文件；文件不存在 / 内容不是有效快照格式 / 解析异常时返回错误信息。</summary>
    private static (PositionSnapshotFile? File, string? Error) TryLoadSnapshotFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return (null, "文件不存在。");

            var file = JsonSerializer.Deserialize<PositionSnapshotFile>(File.ReadAllText(path));
            return file is null ? (null, "文件内容不是有效的快照格式。") : (file, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>当前表格所有行绑定到的 PLC 地址集合（用于校验快照内容与表格是否对得上）。</summary>
    private HashSet<string> CollectCurrentBindingAddresses()
    {
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is not PositionParameterItem item)
                continue;

            foreach (var binding in item.AxisBindings)
            {
                if (!string.IsNullOrWhiteSpace(binding.Address))
                    addresses.Add(binding.Address.Trim());
            }
        }

        return addresses;
    }

    private void AddItemRow(PositionParameterItem item)
    {
        var bindings = item.AxisBindings;
        if (bindings.Count == 0 && item.AxisNames.Count > 0)
            bindings = item.AxisNames.Select(x => new PositionAxisBinding { AxisName = x }).ToList();

        int rowIndex = _grid.Rows.Add();
        var row = _grid.Rows[rowIndex];

        var normalized = new PositionParameterItem
        {
            PositionName = item.PositionName,
            AxisBindings = bindings.Take(5).Select(x => new PositionAxisBinding
            {
                AxisName = x.AxisName,
                Address = x.Address
            }).ToList(),
            LocateTriggerPlcAddress = item.LocateTriggerPlcAddress ?? string.Empty,
            PointNumberPlcAddress = item.PointNumberPlcAddress ?? string.Empty,
            PointNumberData = item.PointNumberData ?? string.Empty
        };
        normalized.AxisNames = normalized.AxisBindings.Select(x => x.AxisName).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();

        row.Tag = normalized;
        FillRowDisplay(row, normalized, null);
    }

    private static void FillRowDisplay(DataGridViewRow row, PositionParameterItem? item, Dictionary<string, string>? axisValues)
    {
        if (item is null)
            return;

        row.Cells[0].Value = item.PositionName;
        for (int i = 0; i < 5; i++)
        {
            if (i >= item.AxisBindings.Count)
            {
                row.Cells[i + 1].Value = string.Empty;
                continue;
            }

            var binding = item.AxisBindings[i];
            string axisName = binding.AxisName;
            string value = "--";
            if (axisValues is not null && axisValues.TryGetValue(axisName, out string? current))
                value = current;

            row.Cells[i + 1].Value = string.IsNullOrWhiteSpace(axisName) ? string.Empty : $"{axisName}: {value}";
        }
    }
}
