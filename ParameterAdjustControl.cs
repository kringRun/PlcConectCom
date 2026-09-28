using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PlcConectCom.WinFormsTest;

/// <summary>参数调整页分类（对应系统配置中的相关页签）。</summary>
public enum ParameterAdjustCategory
{
    常规参数,
    HMI显示数据,
    按钮类变量,
    报警显示,
    报警弹窗显示
}

/// <summary>参数调整：按分类展示简化列表（无地址/类型），当前值可编辑并批量读写 PLC。</summary>
public sealed class ParameterAdjustControl : UserControl
{
    private const int StringReadLength = 128;
    private const string KeywordFileName = "ParameterAdjust.keywords.json";
    private static readonly JsonSerializerOptions JsonFileOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly Color BoolTrueForeColor = Color.FromArgb(46, 160, 90);   // 浅绿（字体）
    private static readonly Color BoolFalseForeColor = Color.FromArgb(220, 80, 80); // 浅红（字体）
    private static readonly Color ModifiedNameBackColor = Color.FromArgb(255, 243, 176); // 浅黄

    private sealed class RowBinding
    {
        public required string Name { get; init; }
        public string Extra { get; init; } = string.Empty;
        public required string Address { get; init; }
        public required ParameterValueType Type { get; init; }
        public int RealArrayLength { get; init; }
        /// <summary>加载时从 PLC 读到的原始值；用于判断是否被修改。</summary>
        public string? OriginalValue { get; set; }
    }

    private readonly ComboBox _cmbCategory = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 180,
        Height = 28,
        Font = new Font("Microsoft YaHei UI", 10F),
        Margin = new Padding(0, 2, 12, 0)
    };

    private readonly ComboBox _cmbKeyword = new()
    {
        Width = 150,
        DropDownStyle = ComboBoxStyle.DropDown,
        Font = new Font("Microsoft YaHei UI", 9.5F),
        Margin = new Padding(0, 2, 6, 0)
    };

    private readonly Button _btnSearch = new()
    {
        Text = "关键字搜索",
        Width = 88,
        Height = 28,
        Margin = new Padding(0, 2, 6, 0)
    };

    private readonly Button _btnClearKeyword = new()
    {
        Text = "清除关键字",
        Width = 88,
        Height = 28,
        Margin = new Padding(0, 2, 0, 0)
    };

    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        AllowUserToResizeColumns = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        EditMode = DataGridViewEditMode.EditOnEnter
    };

    private readonly Button _btnLoad = new() { Text = "加载", Width = 100, Height = 34 };
    private readonly Button _btnSave = new() { Text = "保存", Width = 100, Height = 34 };

    private readonly FlowLayoutPanel _topBar = new()
    {
        Dock = DockStyle.Top,
        Height = 44,
        Padding = new Padding(0, 6, 0, 6),
        WrapContents = false,
        FlowDirection = FlowDirection.LeftToRight
    };

    private readonly Panel _bottomBar = new()
    {
        Dock = DockStyle.Bottom,
        Height = 52,
        Padding = new Padding(0, 8, 0, 8)
    };

    private readonly FlowLayoutPanel _bottomActions = new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = false,
        AutoSize = false
    };

    private readonly string _keywordPath;
    private readonly List<string> _keywords = [];
    private bool _busy;

    public ParameterAdjustControl() : this(null)
    {
    }

    /// <param name="keywordFileName">关键字记忆文件名；null 使用默认。子 PLC 可传入独立文件名。</param>
    public ParameterAdjustControl(string? keywordFileName)
    {
        _keywordPath = Path.Combine(
            AppContext.BaseDirectory,
            string.IsNullOrWhiteSpace(keywordFileName) ? KeywordFileName : keywordFileName.Trim());
        BuildUi();
        BindEvents();
        LoadKeywords();
    }

    public Func<global::PlcConectCom.PlcCommunicator?>? PlcProvider { get; set; }

    public Action<string>? LogAction { get; set; }

    /// <summary>常规参数（系统配置 → 参数设置）。</summary>
    public Func<IReadOnlyList<ParameterItem>>? RegularParametersProvider { get; set; }

    /// <summary>HMI 显示数据。</summary>
    public Func<IReadOnlyList<ParameterItem>>? HmiDisplayParametersProvider { get; set; }

    /// <summary>按钮类变量。</summary>
    public Func<IReadOnlyList<ParameterItem>>? ButtonVarParametersProvider { get; set; }

    /// <summary>报警地址配置。</summary>
    public Func<IReadOnlyList<AlarmAddressItem>>? AlarmAddressProvider { get; set; }

    /// <summary>报警弹窗配置。</summary>
    public Func<IReadOnlyList<AlarmPopupGroupDefinition>>? AlarmPopupGroupsProvider { get; set; }

    public ParameterAdjustCategory SelectedCategory
    {
        get
        {
            if (_cmbCategory.SelectedItem is ParameterAdjustCategory c)
                return c;
            return ParameterAdjustCategory.常规参数;
        }
    }

    public async Task LoadFromPlcAsync()
    {
        if (_busy)
            return;

        _busy = true;
        try
        {
            RebuildRowsFromProviders();
            await ReadAllCurrentValuesAsync();
            // 重新加载后保持当前关键字筛选（与参数表等页面一致）
            if (!string.IsNullOrWhiteSpace(_cmbKeyword.Text.Trim()))
                ApplyKeywordFilter(saveKeyword: false);
        }
        finally
        {
            _busy = false;
        }
    }

    public async Task SaveToPlcAsync()
    {
        if (_busy)
            return;

        _busy = true;
        try
        {
            await WriteAllCurrentValuesAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    private void BuildUi()
    {
        _grid.DefaultCellStyle.SelectionBackColor = Color.Gainsboro;
        _grid.DefaultCellStyle.SelectionForeColor = Color.Black;
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
        _grid.DefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9.5F);
        _grid.RowTemplate.Height = 28;

        _cmbCategory.Items.AddRange([
            ParameterAdjustCategory.常规参数,
            ParameterAdjustCategory.HMI显示数据,
            ParameterAdjustCategory.按钮类变量,
            ParameterAdjustCategory.报警显示,
            ParameterAdjustCategory.报警弹窗显示
        ]);
        _cmbCategory.SelectedIndex = 0;

        _topBar.Controls.Add(_cmbCategory);
        _topBar.Controls.Add(_cmbKeyword);
        _topBar.Controls.Add(_btnSearch);
        _topBar.Controls.Add(_btnClearKeyword);

        _bottomActions.Controls.Add(_btnLoad);
        _bottomActions.Controls.Add(_btnSave);
        _bottomBar.Controls.Add(_bottomActions);

        EnsureGridColumns(showExtra: false, extraHeader: "备注");

        Controls.Add(_grid);
        Controls.Add(_bottomBar);
        Controls.Add(_topBar);

        DataGridViewNoSortHelper.WireControlSubtreeForNoColumnSort(this);
    }

    private void BindEvents()
    {
        _cmbCategory.SelectedIndexChanged += async (_, _) =>
        {
            _cmbKeyword.Text = string.Empty;
            await LoadFromPlcAsync();
        };

        _btnLoad.Click += async (_, _) => await LoadFromPlcAsync();
        _btnSave.Click += async (_, _) => await SaveToPlcAsync();

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

        _grid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex < 0)
                return;
            if (_grid.Columns[e.ColumnIndex].Name != "ColValue")
                return;
            _grid.InvalidateRow(e.RowIndex);
        };

        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        _grid.CellFormatting += Grid_CellFormatting;

        // 下拉框单元格出现值不在选项内（如 ERR 占位）时不弹错误框
        _grid.DataError += (_, e) => e.ThrowException = false;

        VisibleChanged += async (_, _) =>
        {
            if (Visible && IsHandleCreated && !_busy)
                await LoadFromPlcAsync();
        };
    }

    private void Grid_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0)
            return;
        if (e.RowIndex >= _grid.Rows.Count)
            return;

        DataGridViewRow row = _grid.Rows[e.RowIndex];
        if (row.Tag is not RowBinding binding)
            return;

        string? colName = _grid.Columns[e.ColumnIndex].Name;
        if (string.IsNullOrEmpty(colName) || e.CellStyle is null)
            return;

        string currentValue = GetRowCurrentValue(row);

        if (colName == "ColName")
        {
            if (IsValueModified(binding, currentValue))
            {
                e.CellStyle.BackColor = ModifiedNameBackColor;
                e.CellStyle.SelectionBackColor = Color.FromArgb(245, 220, 120);
            }
            return;
        }

        if (colName != "ColValue" || binding.Type != ParameterValueType.BOOL)
            return;

        if (TryParseBoolLoose(currentValue, out bool boolValue))
        {
            e.CellStyle.ForeColor = boolValue ? BoolTrueForeColor : BoolFalseForeColor;
            e.CellStyle.SelectionForeColor = boolValue ? BoolTrueForeColor : BoolFalseForeColor;
        }
    }

    private string GetRowCurrentValue(DataGridViewRow row)
    {
        var valueCol = ValueColumn;
        if (valueCol is null)
            return string.Empty;
        return Convert.ToString(row.Cells[valueCol.Index].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
    }

    private static bool IsValueModified(RowBinding binding, string currentValue)
    {
        if (binding.OriginalValue is null)
            return false;

        if (binding.Type == ParameterValueType.BOOL)
        {
            if (!TryParseBoolLoose(binding.OriginalValue, out bool original)
                || !TryParseBoolLoose(currentValue, out bool current))
                return !string.Equals(NormalizeCompareValue(binding.OriginalValue), NormalizeCompareValue(currentValue), StringComparison.Ordinal);

            return original != current;
        }

        return !string.Equals(
            NormalizeCompareValue(binding.OriginalValue),
            NormalizeCompareValue(currentValue),
            StringComparison.Ordinal);
    }

    private static string NormalizeCompareValue(string? value) =>
        (value ?? string.Empty).Trim();

    private static bool TryParseBoolLoose(string? text, out bool value)
    {
        value = false;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        text = text.Trim();
        if (bool.TryParse(text, out value))
            return true;
        if (string.Equals(text, "1", StringComparison.Ordinal)
            || string.Equals(text, "ON", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "是", StringComparison.Ordinal))
        {
            value = true;
            return true;
        }

        if (string.Equals(text, "0", StringComparison.Ordinal)
            || string.Equals(text, "OFF", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "否", StringComparison.Ordinal))
        {
            value = false;
            return true;
        }

        return false;
    }

    private void EnsureGridColumns(bool showExtra, string extraHeader)
    {
        _grid.Columns.Clear();
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "ColName",
            HeaderText = "名称",
            ReadOnly = true,
            FillWeight = showExtra ? 35 : 45
        });

        if (showExtra)
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "ColExtra",
                HeaderText = extraHeader,
                ReadOnly = true,
                FillWeight = 25
            });
        }

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "ColValue",
            HeaderText = "当前值",
            ReadOnly = false,
            FillWeight = showExtra ? 40 : 55
        });
    }

    private void RebuildRowsFromProviders()
    {
        var category = SelectedCategory;
        bool showExtra = category is ParameterAdjustCategory.常规参数
            or ParameterAdjustCategory.HMI显示数据
            or ParameterAdjustCategory.按钮类变量
            or ParameterAdjustCategory.报警显示;

        string extraHeader = category == ParameterAdjustCategory.报警显示 ? "报警代码" : "注释";
        EnsureGridColumns(showExtra, extraHeader);

        _grid.Rows.Clear();

        IReadOnlyList<RowBinding> rows = category switch
        {
            ParameterAdjustCategory.常规参数 => FromParameters(RegularParametersProvider?.Invoke()),
            ParameterAdjustCategory.HMI显示数据 => FromParameters(HmiDisplayParametersProvider?.Invoke()),
            ParameterAdjustCategory.按钮类变量 => FromParameters(ButtonVarParametersProvider?.Invoke()),
            ParameterAdjustCategory.报警显示 => FromAlarmAddresses(AlarmAddressProvider?.Invoke()),
            ParameterAdjustCategory.报警弹窗显示 => FromAlarmPopupGroups(AlarmPopupGroupsProvider?.Invoke()),
            _ => []
        };

        foreach (RowBinding binding in rows)
        {
            int index;
            if (showExtra)
                index = _grid.Rows.Add(binding.Name, binding.Extra, string.Empty);
            else
                index = _grid.Rows.Add(binding.Name, string.Empty);

            _grid.Rows[index].Tag = binding;

            // BOOL 行的「当前值」用下拉框（1/0）选择，避免手输字符；其余类型保持文本输入
            if (binding.Type == ParameterValueType.BOOL && ValueColumn is { } valueCol)
            {
                var comboCell = new DataGridViewComboBoxCell { FlatStyle = FlatStyle.Flat };
                comboCell.Items.Add("1");
                comboCell.Items.Add("0");
                _grid.Rows[index].Cells[valueCol.Index] = comboCell;
            }
        }
    }

    private static List<RowBinding> FromParameters(IReadOnlyList<ParameterItem>? items)
    {
        var list = new List<RowBinding>();
        if (items is null)
            return list;

        foreach (ParameterItem item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Address))
                continue;

            list.Add(new RowBinding
            {
                Name = string.IsNullOrWhiteSpace(item.Name) ? item.Address : item.Name,
                Extra = item.Comment ?? string.Empty,
                Address = item.Address.Trim(),
                Type = item.Type,
                RealArrayLength = item.RealArrayLength
            });
        }

        return list;
    }

    private static List<RowBinding> FromAlarmAddresses(IReadOnlyList<AlarmAddressItem>? items)
    {
        var list = new List<RowBinding>();
        if (items is null)
            return list;

        foreach (AlarmAddressItem item in items)
        {
            if (string.IsNullOrWhiteSpace(item.AlarmAddress))
                continue;

            list.Add(new RowBinding
            {
                Name = string.IsNullOrWhiteSpace(item.AlarmName) ? item.AlarmAddress : item.AlarmName,
                Extra = item.AlarmCode ?? string.Empty,
                Address = item.AlarmAddress.Trim(),
                Type = ParameterValueType.BOOL
            });
        }

        return list;
    }

    private static List<RowBinding> FromAlarmPopupGroups(IReadOnlyList<AlarmPopupGroupDefinition>? groups)
    {
        var list = new List<RowBinding>();
        if (groups is null)
            return list;

        foreach (AlarmPopupGroupDefinition group in groups)
        {
            if (!string.IsNullOrWhiteSpace(group.TriggerAddress))
            {
                list.Add(new RowBinding
                {
                    Name = string.IsNullOrWhiteSpace(group.TriggerName) ? group.TriggerAddress : group.TriggerName,
                    Address = group.TriggerAddress.Trim(),
                    Type = ParameterValueType.BOOL
                });
            }

            foreach (AlarmPopupButtonDefinition button in group.Buttons)
            {
                if (string.IsNullOrWhiteSpace(button.Address))
                    continue;

                string name = !string.IsNullOrWhiteSpace(button.DisplayName)
                    ? button.DisplayName
                    : (!string.IsNullOrWhiteSpace(button.FullName) ? button.FullName : button.Address);

                list.Add(new RowBinding
                {
                    Name = name,
                    Address = button.Address.Trim(),
                    Type = ParameterValueType.BOOL
                });
            }
        }

        return list;
    }

    private DataGridViewColumn? ValueColumn =>
        _grid.Columns.Contains("ColValue") ? _grid.Columns["ColValue"] : null;

    /// <summary>
    /// 写入「当前值」单元格。BOOL 行是下拉框单元格，值不在选项里（如读取失败时的 ERR）时
    /// 先补进选项，避免 DataGridViewComboBoxCell 触发 DataError。
    /// </summary>
    private static void SetCellValue(DataGridViewRow row, int columnIndex, string value)
    {
        if (row.Cells[columnIndex] is DataGridViewComboBoxCell combo && !combo.Items.Contains(value))
            combo.Items.Add(value);
        row.Cells[columnIndex].Value = value;
    }

    private async Task ReadAllCurrentValuesAsync()
    {
        var plc = PlcProvider?.Invoke();
        if (plc is not { IsConnected: true })
        {
            LogAction?.Invoke("参数调整加载失败：PLC 未连接。");
            return;
        }

        var valueCol = ValueColumn;
        if (valueCol is null)
            return;

        int ok = 0;
        int fail = 0;
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is not RowBinding binding)
                continue;

            try
            {
                string value = await ReadValueAsync(plc, binding);
                SetCellValue(row, valueCol.Index, value);
                binding.OriginalValue = value;
                ok++;
            }
            catch (Exception ex)
            {
                SetCellValue(row, valueCol.Index, "ERR");
                binding.OriginalValue = null;
                fail++;
                LogAction?.Invoke($"参数调整读取失败 {binding.Name}: {ex.Message}");
            }
        }

        _grid.Invalidate();
        LogAction?.Invoke($"参数调整加载完成：成功 {ok}，失败 {fail}。");
    }

    private async Task WriteAllCurrentValuesAsync()
    {
        var plc = PlcProvider?.Invoke();
        if (plc is not { IsConnected: true })
        {
            LogAction?.Invoke("参数调整保存失败：PLC 未连接。");
            return;
        }

        var valueCol = ValueColumn;
        if (valueCol is null)
            return;

        int ok = 0;
        int skip = 0;
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is not RowBinding binding)
                continue;

            string writeValue = Convert.ToString(row.Cells[valueCol.Index].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(writeValue) || string.Equals(writeValue, "ERR", StringComparison.OrdinalIgnoreCase))
            {
                skip++;
                continue;
            }

            try
            {
                await WriteValueAsync(plc, binding, writeValue);
                // 保存成功后以写入值作为新的基线，清除名称浅黄标记；BOOL 统一成 1/0 显示
                string normalized = binding.Type == ParameterValueType.BOOL
                    ? FormatBool(ParseBool(writeValue))
                    : writeValue;
                SetCellValue(row, valueCol.Index, normalized);
                binding.OriginalValue = normalized;
                ok++;
            }
            catch (Exception ex)
            {
                skip++;
                LogAction?.Invoke($"参数调整写入失败 {binding.Name}: {ex.Message}");
            }
        }

        _grid.Invalidate();
        LogAction?.Invoke($"参数调整保存完成：成功 {ok}，跳过/失败 {skip}。");
    }

    private static async Task<string> ReadValueAsync(global::PlcConectCom.PlcCommunicator plc, RowBinding binding)
    {
        return binding.Type switch
        {
            ParameterValueType.BOOL => FormatBool(await plc.ReadBoolAsync(binding.Address)),
            ParameterValueType.INT => (await plc.ReadIntAsync(binding.Address)).ToString(CultureInfo.InvariantCulture),
            ParameterValueType.DWORD => (await plc.ReadDWordAsync(binding.Address)).ToString(CultureInfo.InvariantCulture),
            ParameterValueType.REAL => (await plc.ReadDoubleAsync(binding.Address)).ToString("G9", CultureInfo.InvariantCulture),
            ParameterValueType.REAL_ARRAY when binding.RealArrayLength > 0 =>
                await ReadRealArrayDisplayAsync(plc, binding.Address, binding.RealArrayLength),
            ParameterValueType.STRING => await plc.ReadStringAsync(binding.Address, StringReadLength),
            _ => string.Empty
        };
    }

    private static async Task WriteValueAsync(global::PlcConectCom.PlcCommunicator plc, RowBinding binding, string writeValue)
    {
        switch (binding.Type)
        {
            case ParameterValueType.BOOL:
                await plc.WriteBoolAsync(binding.Address, ParseBool(writeValue));
                break;
            case ParameterValueType.INT:
                if (!short.TryParse(writeValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out short intValue)
                    && !short.TryParse(writeValue, NumberStyles.Integer, CultureInfo.CurrentCulture, out intValue))
                    throw new ArgumentException("INT 格式错误。");
                await plc.WriteIntAsync(binding.Address, intValue);
                break;
            case ParameterValueType.DWORD:
                if (!uint.TryParse(writeValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint dwordValue))
                    throw new ArgumentException("DWORD 格式错误。");
                await plc.WriteDWordAsync(binding.Address, dwordValue);
                break;
            case ParameterValueType.REAL:
                if (!double.TryParse(writeValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double realValue)
                    && !double.TryParse(writeValue, NumberStyles.Float, CultureInfo.CurrentCulture, out realValue))
                    throw new ArgumentException("REAL 格式错误。");
                await plc.WriteDoubleAsync(binding.Address, realValue);
                break;
            case ParameterValueType.REAL_ARRAY:
                if (binding.RealArrayLength <= 0)
                    throw new ArgumentException("REAL_ARRAY 元素个数无效。");
                {
                    List<double> reals = ParseSemicolonSeparatedReals(writeValue, binding.RealArrayLength);
                    for (int i = 0; i < reals.Count; i++)
                    {
                        string addr = BuildShiftedMdAddress(binding.Address, i);
                        await plc.WriteDoubleAsync(addr, reals[i]);
                    }
                }
                break;
            case ParameterValueType.STRING:
                await plc.WriteStringAsync(binding.Address, writeValue);
                break;
            default:
                throw new ArgumentException("不支持的数据类型。");
        }
    }

    private static string FormatBool(bool value) => value ? "1" : "0";

    private static bool ParseBool(string text)
    {
        text = text.Trim();
        if (bool.TryParse(text, out bool b))
            return b;
        if (string.Equals(text, "1", StringComparison.Ordinal)
            || string.Equals(text, "ON", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "是", StringComparison.Ordinal))
            return true;
        if (string.Equals(text, "0", StringComparison.Ordinal)
            || string.Equals(text, "OFF", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "否", StringComparison.Ordinal))
            return false;

        throw new ArgumentException("BOOL 格式错误，应为 true/false、1/0、ON/OFF。");
    }

    private static async Task<string> ReadRealArrayDisplayAsync(global::PlcConectCom.PlcCommunicator plc, string baseAddress, int count)
    {
        var parts = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            string addr = BuildShiftedMdAddress(baseAddress, i);
            double d = await plc.ReadDoubleAsync(addr);
            parts.Add(d.ToString("G9", CultureInfo.InvariantCulture));
        }

        return string.Join("; ", parts);
    }

    private static List<double> ParseSemicolonSeparatedReals(string writeValue, int expectedCount)
    {
        string[] parts = writeValue.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != expectedCount)
            throw new ArgumentException($"REAL_ARRAY 需要 {expectedCount} 个实数，用英文分号分隔；当前为 {parts.Length} 个。");

        var list = new List<double>(expectedCount);
        foreach (string p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                && !double.TryParse(p, NumberStyles.Float, CultureInfo.CurrentCulture, out v))
                throw new ArgumentException($"无法解析为实数: {p}");

            list.Add(v);
        }

        return list;
    }

    private static string BuildShiftedMdAddress(string baseAddress, int indexDelta)
    {
        var (mem, offset, _) = global::PlcConectCom.AddressParser.ParseAddress(baseAddress.Trim());
        if (!string.Equals(mem, "MD", StringComparison.Ordinal))
            throw new InvalidOperationException("REAL_ARRAY 仅支持 MD 基址。");

        return $"MD{offset + indexDelta}";
    }

    private void ApplyKeywordFilter(bool saveKeyword)
    {
        string keyword = _cmbKeyword.Text.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            foreach (DataGridViewRow row in _grid.Rows)
                row.Visible = true;

            LogAction?.Invoke("已取消关键字筛选，显示全部参数。");
            return;
        }

        // 筛选前结束编辑，避免当前行因编辑状态无法隐藏
        if (_grid.IsCurrentCellInEditMode)
            _grid.EndEdit();
        _grid.ClearSelection();
        _grid.CurrentCell = null;

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
        foreach (DataGridViewCell cell in row.Cells)
            parts.Add(Convert.ToString(cell.Value, CultureInfo.InvariantCulture) ?? string.Empty);

        if (row.Tag is RowBinding binding)
        {
            parts.Add(binding.Name);
            parts.Add(binding.Extra);
            parts.Add(binding.Address);
        }

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
        LogAction?.Invoke("已一键清除关键字并恢复全部参数显示。");
    }

    private void RebindKeywordCombo()
    {
        string current = _cmbKeyword.Text;
        _cmbKeyword.Items.Clear();
        _cmbKeyword.Items.AddRange(_keywords.Cast<object>().ToArray());
        _cmbKeyword.Text = current;
    }

    private void LoadKeywords()
    {
        try
        {
            if (!File.Exists(_keywordPath))
                return;

            var json = File.ReadAllText(_keywordPath);
            var keywords = JsonSerializer.Deserialize<List<string>>(json);
            if (keywords is null)
                return;

            _keywords.Clear();
            _keywords.AddRange(keywords.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
            RebindKeywordCombo();
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"加载关键字失败: {ex.Message}");
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
            LogAction?.Invoke($"保存关键字失败: {ex.Message}");
        }
    }
}
