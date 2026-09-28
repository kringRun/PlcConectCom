using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PlcConectCom.WinFormsTest;

/// <summary>兼容旧版 JSON 中 Type 为数字（0 BOOL / 1 INT / 2 REAL / 3 STRING）；新版写入枚举名字符串。</summary>
public sealed class ParameterValueTypeCompatJsonConverter : JsonConverter<ParameterValueType>
{
    public override ParameterValueType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
            {
                string? s = reader.GetString();
                return Enum.TryParse(s, true, out ParameterValueType parsed) ? parsed : ParameterValueType.INT;
            }
            case JsonTokenType.Number:
            {
                if (!reader.TryGetInt32(out int n))
                    return ParameterValueType.INT;

                return n switch
                {
                    0 => ParameterValueType.BOOL,
                    1 => ParameterValueType.INT,
                    2 => ParameterValueType.REAL,
                    3 => ParameterValueType.STRING,
                    _ => ParameterValueType.INT
                };
            }
            default:
                return ParameterValueType.INT;
        }
    }

    public override void Write(Utf8JsonWriter writer, ParameterValueType value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}

// 参数表支持的数据类型
[JsonConverter(typeof(ParameterValueTypeCompatJsonConverter))]
public enum ParameterValueType
{
    BOOL,
    INT,
    DWORD,
    REAL,
    REAL_ARRAY,
    STRING
}

/// <summary>Excel DataType 列解析结果（含 <c>ARRAY [lo..hi] OF REAL</c>）。</summary>
public readonly record struct ParameterImportDataTypeResult(ParameterValueType Type, int RealArrayLength, string? OriginalTypeText);

// 参数配置项
public sealed class ParameterItem
{
    public string Name { get; set; } = string.Empty;

    public ParameterValueType Type { get; set; } = ParameterValueType.INT;

    public string Address { get; set; } = "MW0";

    /// <summary>当 <see cref="Type"/> 为 <see cref="ParameterValueType.REAL_ARRAY"/> 时，连续 REAL 元素个数（对应 MD 基址下标递增）。</summary>
    public int RealArrayLength { get; set; }

    public string Comment { get; set; } = string.Empty;
}

// 参数变化记录
public sealed class ParameterChangeRecord
{
    public DateTime Timestamp { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public string OldValue { get; set; } = string.Empty;

    public string NewValue { get; set; } = string.Empty;
}

// 参数配方定义
public sealed class ParameterRecipe
{
    public string RecipeName { get; set; } = string.Empty;

    public DateTime SavedAt { get; set; }

    public List<ParameterRecipeEntry> Entries { get; set; } = [];
}

// 参数配方单条项
public sealed class ParameterRecipeEntry
{
    public string Name { get; set; } = string.Empty;

    public ParameterValueType Type { get; set; }

    public string Address { get; set; } = string.Empty;

    /// <summary>与参数配置一致：仅当 REAL_ARRAY 时有效。</summary>
    public int RealArrayLength { get; set; }

    public string Comment { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

// 参数表控件：参数维护、读写、记录与配方管理
public sealed class ParameterTableControl : UserControl
{
    private const string ConfigFileName = "ParameterTable.config.json";
    private const string RecordFileName = "ParameterTable.records.json";
    private const string RecipeFileName = "ParameterTable.recipes.json";
    private const string KeywordFileName = "ParameterTable.keywords.json";
    private static readonly JsonSerializerOptions JsonFileOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private const int StringReadLength = 128;

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

    private readonly Button _btnAdd = new() { Text = "新增参数", Width = 80 };
    private readonly Button _btnRemove = new() { Text = "删除参数", Width = 80 };
    private readonly Button _btnReadAll = new() { Text = "读取全部", Width = 80 };
    private readonly Button _btnWriteSelected = new() { Text = "改写选中", Width = 80 };
    private readonly Button _btnSaveRecipe = new() { Text = "保存配方", Width = 80 };
    private readonly Button _btnLoadRecipe = new() { Text = "加载配方", Width = 80 };
    private readonly Button _btnSaveAll = new() { Text = "保存全部", Width = 80 };
    private readonly Button _btnViewRecords = new() { Text = "查看记录", Width = 80 };
    private readonly ComboBox _cmbKeyword = new() { Width = 150, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly Button _btnSearch = new() { Text = "关键字搜索", Width = 80 };
    private readonly Button _btnClearKeyword = new() { Text = "清除关键字", Width = 80 };
    private readonly Button _btnEditAuth = new() { Text = "编辑解锁", Width = 80 };

    private readonly FlowLayoutPanel _actionsRow = new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        WrapContents = true,
        Padding = new Padding(0, 0, 0, 4)
    };

    private readonly string _configPath;
    private readonly string _recordPath;
    private readonly string _recipePath;
    private readonly string _keywordPath;
    private readonly List<ParameterChangeRecord> _records = [];
    private readonly List<ParameterRecipe> _recipes = [];
    private readonly List<string> _keywords = [];

    private static readonly Color ModifiedNameBackColor = Color.FromArgb(255, 243, 176); // 浅黄：相对加载基线有未保存改动

    private bool _suspendSave;
    private bool _canAddDelete = true;
    private bool _canModify = true;
    private bool _editUnlocked;
    private Color _actionButtonBackColor = SystemColors.Control;
    private Color _actionButtonForeColor = SystemColors.ControlText;

    public const int MaxRealArrayElements = 256;

    /// <summary>参数表顶部操作按钮背景色。</summary>
    public Color ActionButtonBackColor
    {
        get => _actionButtonBackColor;
        set
        {
            _actionButtonBackColor = value;
            ApplyActionButtonColors();
        }
    }

    /// <summary>参数表顶部操作按钮前景色。</summary>
    public Color ActionButtonForeColor
    {
        get => _actionButtonForeColor;
        set
        {
            _actionButtonForeColor = value;
            ApplyActionButtonColors();
        }
    }

    private static readonly Regex RealArrayOfRealRegex = new(
        @"ARRAY\s*\[\s*(?<lo>\d+)\s*\.\.\s*(?<hi>\d+)\s*\]\s*OF\s*REAL",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public ParameterTableControl(string? storageKey = null)
    {
        // 支持按实例使用独立存储文件，避免多个参数表共用同一份配置/配方/记录
        _configPath = Path.Combine(AppContext.BaseDirectory, BuildStorageFileName(ConfigFileName, storageKey));
        _recordPath = Path.Combine(AppContext.BaseDirectory, BuildStorageFileName(RecordFileName, storageKey));
        _recipePath = Path.Combine(AppContext.BaseDirectory, BuildStorageFileName(RecipeFileName, storageKey));
        _keywordPath = Path.Combine(AppContext.BaseDirectory, BuildStorageFileName(KeywordFileName, storageKey));

        BuildUi();
        BindEvents();
        LoadConfig();
        LoadRecords();
        LoadRecipes();
        LoadKeywords();
    }

    private static string BuildStorageFileName(string baseFileName, string? storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
            return baseFileName;

        string safeKey = new(storageKey.Trim().Where(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-').ToArray());
        if (string.IsNullOrWhiteSpace(safeKey))
            return baseFileName;

        int dotIndex = baseFileName.LastIndexOf('.');
        if (dotIndex <= 0)
            return $"{baseFileName}.{safeKey}";

        return $"{baseFileName[..dotIndex]}.{safeKey}{baseFileName[dotIndex..]}";
    }

    /// <summary>解析 Excel「DataType」列：支持 <c>ARRAY [0..5] OF REAL</c> 等固定长度实数数组，其余走标量规则。</summary>
    public static ParameterImportDataTypeResult ParseImportDataType(string? dataTypeText)
    {
        string trimmed = dataTypeText?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed))
            return new ParameterImportDataTypeResult(ParameterValueType.INT, 0, null);

        Match m = RealArrayOfRealRegex.Match(trimmed);
        if (m.Success
            && int.TryParse(m.Groups["lo"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lo)
            && int.TryParse(m.Groups["hi"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int hi))
        {
            int n = hi - lo + 1;
            if (n > 0 && n <= MaxRealArrayElements)
                return new ParameterImportDataTypeResult(ParameterValueType.REAL_ARRAY, n, trimmed);
        }

        return new ParameterImportDataTypeResult(ParseScalarImportDataType(trimmed), 0, null);
    }

    private static ParameterValueType ParseScalarImportDataType(string text)
    {
        if (string.Equals(text, "BOOL", StringComparison.OrdinalIgnoreCase))
            return ParameterValueType.BOOL;
        if (string.Equals(text, "INT", StringComparison.OrdinalIgnoreCase))
            return ParameterValueType.INT;
        if (string.Equals(text, "DWORD", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "UINT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "UDINT", StringComparison.OrdinalIgnoreCase))
            return ParameterValueType.DWORD;
        if (string.Equals(text, "REAL", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "DOUBLE", StringComparison.OrdinalIgnoreCase))
            return ParameterValueType.REAL;
        if (string.Equals(text, "STRING", StringComparison.OrdinalIgnoreCase))
            return ParameterValueType.STRING;

        return ParameterValueType.INT;
    }

    private static int TryInferRealArrayLengthFromComment(string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment))
            return 0;

        Match m = RealArrayOfRealRegex.Match(comment.Trim());
        if (!m.Success
            || !int.TryParse(m.Groups["lo"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lo)
            || !int.TryParse(m.Groups["hi"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int hi))
            return 0;

        int n = hi - lo + 1;
        return n > 0 && n <= MaxRealArrayElements ? n : 0;
    }

    private static string BuildShiftedMdAddress(string baseAddress, int indexDelta)
    {
        var (mem, offset, _) = global::PlcConectCom.AddressParser.ParseAddress(baseAddress.Trim());
        if (!string.Equals(mem, "MD", StringComparison.Ordinal))
            throw new InvalidOperationException("REAL_ARRAY 仅支持 MD 基址（每个元素占一个 MD，如 MD2910、MD2911…）。");

        return $"MD{offset + indexDelta}";
    }

    private static async Task<string> ReadRealArrayDisplayAsync(global::PlcConectCom.PlcCommunicator plc, string baseAddress, int count)
    {
        var parts = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            string addr = BuildShiftedMdAddress(baseAddress, i);
            double d = await plc.ReadDoubleAsync(addr);
            parts.Add(d.ToString("F3", CultureInfo.InvariantCulture));
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

    public Func<global::PlcConectCom.PlcCommunicator?>? PlcProvider { get; set; }

    public global::PlcConectCom.PlcCommunicator? PlcInstance { get; set; }

    public Action<string>? LogAction { get; set; }

    public Func<bool>? EditPermissionRequest { get; set; }

    /// <summary>
    /// 为 true 时不将参数变化写入记录文件、不写日志（如程序启动后首次从 PLC 批量读取，避免刷屏与污染历史）。
    /// </summary>
    public bool SuppressChangeJournal { get; set; }

    /// <summary>
    /// 实时工艺数据配置的存储键（与 <see cref="ProcessRealtimeDataConfigControl"/> 一致）。
    /// 设置后，参数地址落在实时工艺地址集合内的变化不再写入参数变化记录
    /// （整板时间/待料时间等量持续跳动，会把记录刷满）。
    /// </summary>
    public string? ProcessRealtimeStorageKey { get; set; }

    private HashSet<string>? _realtimeExcludeAddresses;
    private DateTime _realtimeExcludeFileUtc;

    public IReadOnlyList<ParameterChangeRecord> GetRecords() => _records;

    // 获取参数表中的地址列表，用于其他页面作为数据源选择
    public IReadOnlyList<string> GetParameterAddresses()
    {
        var list = new List<string>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            string address = Convert.ToString(row.Cells[2].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(address))
                continue;

            list.Add(address);
        }

        return list
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // 获取参数表中的名称+地址映射，用于下拉显示名称并绑定地址
    public IReadOnlyList<KeyValuePair<string, string>> GetParameterNameAddressPairs()
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            string name = Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            string address = Convert.ToString(row.Cells[2].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(address))
                continue;

            if (string.IsNullOrWhiteSpace(name))
                name = address;

            list.Add(new KeyValuePair<string, string>(name, address));
        }

        return list
            .GroupBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();
    }

    public void SetPermission(bool canAddDelete, bool canModify)
    {
        _canAddDelete = canAddDelete;
        _canModify = canModify;
        if (!canModify)
            _editUnlocked = false;

        _btnAdd.Enabled = _canAddDelete && _editUnlocked;
        _btnRemove.Enabled = _canAddDelete && _editUnlocked;
        _btnReadAll.Enabled = canModify;
        _btnWriteSelected.Enabled = canModify && _editUnlocked;
        _btnSaveRecipe.Enabled = canModify && _editUnlocked;
        _btnLoadRecipe.Enabled = canModify && _editUnlocked;
        _btnSaveAll.Enabled = canModify && _editUnlocked;
        _btnViewRecords.Enabled = true;
        _cmbKeyword.Enabled = canModify && _editUnlocked;
        _btnSearch.Enabled = canModify && _editUnlocked;
        _btnClearKeyword.Enabled = canModify && _editUnlocked;
        _btnEditAuth.Enabled = canModify;
        _btnEditAuth.Text = _editUnlocked ? "编辑锁定" : "编辑解锁";
        _grid.ReadOnly = !canModify || !_editUnlocked;
    }

    public void SetParameters(IEnumerable<ParameterItem> items)
    {
        _suspendSave = true;
        _grid.Rows.Clear();
        foreach (var item in items)
        {
            int rowIndex = _grid.Rows.Add(item.Name, item.Type.ToString(), item.Address, string.Empty, string.Empty, item.Comment);
            DataGridViewRow row = _grid.Rows[rowIndex];
            if (item.Type == ParameterValueType.REAL_ARRAY && item.RealArrayLength > 0)
                row.Tag = item.RealArrayLength;
            else
                row.Tag = null;

            UpdateWriteValueEditor(row);
        }
        _suspendSave = false;

        SaveConfig();
    }

    /// <summary>读取当前表格中的参数定义（不含当前值/改写值），供参数调整等页面复用。</summary>
    public IReadOnlyList<ParameterItem> GetParameters()
    {
        return _grid.Rows.Cast<DataGridViewRow>()
            .Select(ReadParameterItem)
            .Where(x => x is not null)
            .Cast<ParameterItem>()
            .Select(CloneParameterItem)
            .ToList();
    }

    private static ParameterItem CloneParameterItem(ParameterItem item) => new()
    {
        Name = item.Name,
        Type = item.Type,
        Address = item.Address,
        RealArrayLength = item.RealArrayLength,
        Comment = item.Comment
    };

    private void BuildUi()
    {
        // 「参数名」按单元格内容自动列宽：参数名长度参差且在表格宽度中占比最高，
        // 该列用 AllCells（取所有行内容的最宽值），其余列仍由 Fill 按 FillWeight 均分剩余宽度。
        // AllowUserToResizeColumns=false 时用户无法手动拖拽列宽，拖动 ColumnWidthChanged 无副作用。
        var nameColumn = new DataGridViewTextBoxColumn
        {
            HeaderText = "参数名",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells,
            MinimumWidth = 140
        };
        _grid.Columns.Add(nameColumn);

        var typeColumn = new DataGridViewComboBoxColumn
        {
            HeaderText = "类型",
            FillWeight = 9,
            DataSource = Enum.GetNames<ParameterValueType>()
        };
        _grid.Columns.Add(typeColumn);

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "地址", FillWeight = 13 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "当前值", FillWeight = 10, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "改写值", FillWeight = 10 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "注释", FillWeight = 20 });

        _actionsRow.Controls.AddRange([_btnEditAuth, _btnAdd, _btnRemove, _btnReadAll, _btnWriteSelected, _btnSaveRecipe, _btnLoadRecipe, _btnSaveAll, _btnViewRecords, _cmbKeyword, _btnSearch, _btnClearKeyword]);
        ApplyActionButtonColors();

        Controls.Add(_grid);
        Controls.Add(_actionsRow);

        _grid.CellFormatting += Grid_CellFormatting;
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
    }

    private void Grid_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != 0 || e.CellStyle is null)
            return;
        if (e.RowIndex >= _grid.Rows.Count)
            return;

        DataGridViewRow row = _grid.Rows[e.RowIndex];
        if (!IsRowValueModified(row))
            return;

        e.CellStyle.BackColor = ModifiedNameBackColor;
        e.CellStyle.SelectionBackColor = Color.FromArgb(245, 220, 120);
    }

    /// <summary>加载基线保存在名称列 Tag；有效值（优先改写值，否则当前值）相对基线有变化则视为已修改。</summary>
    private static bool IsRowValueModified(DataGridViewRow row)
    {
        if (row.Cells.Count <= 4)
            return false;
        if (row.Cells[0].Tag is not string original)
            return false;

        string writeValue = Convert.ToString(row.Cells[4].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        string currentValue = Convert.ToString(row.Cells[3].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        string effective = !string.IsNullOrWhiteSpace(writeValue) ? writeValue : currentValue;

        string typeText = Convert.ToString(row.Cells[1].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        if (Enum.TryParse(typeText, true, out ParameterValueType type) && type == ParameterValueType.BOOL)
        {
            if (TryParseBoolLoose(original, out bool originalBool) && TryParseBoolLoose(effective, out bool effectiveBool))
                return originalBool != effectiveBool;
        }

        return !string.Equals(original.Trim(), effective, StringComparison.Ordinal);
    }

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

    private static void SetLoadedBaseline(DataGridViewRow row, string? value)
    {
        if (row.Cells.Count > 0)
            row.Cells[0].Tag = value;
    }

    /// <summary>统一应用顶部操作按钮颜色（不影响关键字下拉框）。</summary>
    public void ApplyActionButtonColors()
    {
        foreach (Button button in EnumerateActionButtons())
        {
            button.UseVisualStyleBackColor = false;
            button.BackColor = _actionButtonBackColor;
            button.ForeColor = _actionButtonForeColor;
        }
    }

    private IEnumerable<Button> EnumerateActionButtons()
    {
        yield return _btnEditAuth;
        yield return _btnAdd;
        yield return _btnRemove;
        yield return _btnReadAll;
        yield return _btnWriteSelected;
        yield return _btnSaveRecipe;
        yield return _btnLoadRecipe;
        yield return _btnSaveAll;
        yield return _btnViewRecords;
        yield return _btnSearch;
        yield return _btnClearKeyword;
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
            int rowIndex = _grid.Rows.Add("", ParameterValueType.INT.ToString(), "MW0", string.Empty, string.Empty, string.Empty);
            UpdateWriteValueEditor(_grid.Rows[rowIndex]);
            SaveConfig();
        };
        _btnRemove.Click += (_, _) => RemoveCurrentRow();
        _btnReadAll.Click += async (_, _) => await ReadAllAsync();
        _btnWriteSelected.Click += async (_, _) => await WriteSelectedAsync();
        _btnSaveRecipe.Click += (_, _) => SaveRecipeFromGrid();
        _btnLoadRecipe.Click += (_, _) => LoadRecipeToGrid();
        _btnSaveAll.Click += async (_, _) => await SaveAllToPlcAndLocalAsync();
        _btnViewRecords.Click += (_, _) => ShowParameterChangeRecordsDialog(FindForm());
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
            if (e.RowIndex >= 0 && (e.ColumnIndex == 3 || e.ColumnIndex == 4))
                _grid.InvalidateCell(0, e.RowIndex);

            if (_suspendSave)
                return;

            if (!_editUnlocked)
                return;

            if (e.RowIndex >= 0 && e.ColumnIndex == 1)
            {
                DataGridViewRow row = _grid.Rows[e.RowIndex];
                string typeText = Convert.ToString(row.Cells[1].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                if (!Enum.TryParse(typeText, true, out ParameterValueType t))
                    t = ParameterValueType.INT;

                if (t != ParameterValueType.REAL_ARRAY)
                    row.Tag = null;
                else
                {
                    int fromComment = TryInferRealArrayLengthFromComment(
                        Convert.ToString(row.Cells[5].Value, CultureInfo.InvariantCulture));
                    if (fromComment > 0)
                        row.Tag = fromComment;
                    else if (row.Tag is not int len || len <= 0)
                        row.Tag = 1;
                }

                UpdateWriteValueEditor(row);
            }

            SaveConfig();
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

        foreach (DataGridViewRow row in _grid.Rows)
            row.Visible = IsRowMatchKeyword(row, keyword);

        if (saveKeyword)
        {
            AddKeyword(keyword);
            SaveKeywords();
        }

        LogAction?.Invoke($"关键字筛选: {keyword}");
    }

    private bool IsRowMatchKeyword(DataGridViewRow row, string keyword)
    {
        var text = string.Join("|",
            Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture) ?? string.Empty,
            Convert.ToString(row.Cells[1].Value, CultureInfo.InvariantCulture) ?? string.Empty,
            Convert.ToString(row.Cells[2].Value, CultureInfo.InvariantCulture) ?? string.Empty,
            Convert.ToString(row.Cells[5].Value, CultureInfo.InvariantCulture) ?? string.Empty);

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
        _cmbKeyword.Items.Clear();
        _cmbKeyword.Items.AddRange(_keywords.Cast<object>().ToArray());
    }

    public async Task ReadAllAsync()
    {
        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
            return;

        foreach (DataGridViewRow row in _grid.Rows)
            await ReadRowAsync(plc, row);

        _grid.InvalidateColumn(0);
    }

    public async Task WriteSelectedAsync()
    {
        if (!_canModify)
            return;

        if (_grid.SelectedRows.Count == 0)
            return;

        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
            return;

        await WriteRowAsync(plc, _grid.SelectedRows[0]);
    }

    private async Task SaveAllToPlcAndLocalAsync()
    {
        if (!_canModify || !_editUnlocked)
            return;

        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
        {
            LogAction?.Invoke("保存全部失败：PLC未连接。");
            return;
        }

        int okCount = 0;
        int skipCount = 0;
        foreach (DataGridViewRow row in _grid.Rows)
        {
            var item = ReadParameterItem(row);
            if (item is null)
                continue;

            string writeValue = Convert.ToString(row.Cells[4].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(writeValue))
                writeValue = Convert.ToString(row.Cells[3].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(writeValue))
            {
                skipCount++;
                continue;
            }

            bool ok = await WriteRowWithValueAsync(plc, row, item, writeValue);
            if (ok)
                okCount++;
            else
                skipCount++;
        }

        SaveConfig();
        _grid.InvalidateColumn(0);
        LogAction?.Invoke($"保存全部完成：成功 {okCount} 条，跳过/失败 {skipCount} 条；已保存本地参数文件。");
    }

    private async Task ReadRowAsync(global::PlcConectCom.PlcCommunicator plc, DataGridViewRow row)
    {
        var item = ReadParameterItem(row);
        if (item is null)
            return;

        string oldValue = Convert.ToString(row.Cells[3].Value, CultureInfo.InvariantCulture) ?? string.Empty;
        string newValue;

        try
        {
            newValue = item.Type switch
            {
                ParameterValueType.BOOL => (await plc.ReadBoolAsync(item.Address)).ToString(CultureInfo.InvariantCulture),
                ParameterValueType.INT => (await plc.ReadIntAsync(item.Address)).ToString(CultureInfo.InvariantCulture),
                ParameterValueType.DWORD => (await plc.ReadDWordAsync(item.Address)).ToString(CultureInfo.InvariantCulture),
                ParameterValueType.REAL => (await plc.ReadDoubleAsync(item.Address)).ToString("F3", CultureInfo.InvariantCulture),
                ParameterValueType.REAL_ARRAY when item.RealArrayLength > 0 =>
                    await ReadRealArrayDisplayAsync(plc, item.Address, item.RealArrayLength),
                ParameterValueType.STRING => await plc.ReadStringAsync(item.Address, StringReadLength),
                _ => string.Empty
            };

            row.Cells[3].Value = newValue;
            SetLoadedBaseline(row, newValue);

            if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
                AppendRecord(item, "读取变化", oldValue, newValue);
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"读取失败 {item.Address}: {ex.Message}");
        }
    }

    private async Task WriteRowAsync(global::PlcConectCom.PlcCommunicator plc, DataGridViewRow row)
    {
        var item = ReadParameterItem(row);
        if (item is null)
            return;

        string writeValue = Convert.ToString(row.Cells[4].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(writeValue))
            return;

        await WriteRowWithValueAsync(plc, row, item, writeValue);
    }

    private async Task<bool> WriteRowWithValueAsync(
        global::PlcConectCom.PlcCommunicator plc,
        DataGridViewRow row,
        ParameterItem item,
        string writeValue)
    {
        writeValue = writeValue.Trim();
        if (string.IsNullOrWhiteSpace(writeValue))
            return false;

        string oldValue = Convert.ToString(row.Cells[3].Value, CultureInfo.InvariantCulture) ?? string.Empty;

        try
        {
            switch (item.Type)
            {
                case ParameterValueType.BOOL:
                    if (!bool.TryParse(writeValue, out bool boolValue))
                        throw new ArgumentException("BOOL改写值格式错误，应为true/false。");
                    await plc.WriteBoolAsync(item.Address, boolValue);
                    break;
                case ParameterValueType.INT:
                    if (!short.TryParse(writeValue, out short intValue))
                        throw new ArgumentException("INT改写值格式错误。");
                    await plc.WriteIntAsync(item.Address, intValue);
                    break;
                case ParameterValueType.DWORD:
                    if (!uint.TryParse(writeValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint dwordValue))
                        throw new ArgumentException("DWORD改写值格式错误，应为 0～4294967295 的无符号整数。");
                    await plc.WriteDWordAsync(item.Address, dwordValue);
                    break;
                case ParameterValueType.REAL:
                    if (!double.TryParse(writeValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double realValue)
                        && !double.TryParse(writeValue, out realValue))
                        throw new ArgumentException("REAL改写值格式错误。");
                    await plc.WriteDoubleAsync(item.Address, realValue);
                    break;
                case ParameterValueType.REAL_ARRAY:
                    if (item.RealArrayLength <= 0)
                        throw new ArgumentException("REAL_ARRAY 元素个数无效。");
                    {
                        List<double> reals = ParseSemicolonSeparatedReals(writeValue, item.RealArrayLength);
                        for (int i = 0; i < reals.Count; i++)
                        {
                            string addr = BuildShiftedMdAddress(item.Address, i);
                            await plc.WriteDoubleAsync(addr, reals[i]);
                        }
                    }

                    break;
                case ParameterValueType.STRING:
                    await plc.WriteStringAsync(item.Address, writeValue);
                    break;
            }

            row.Cells[3].Value = writeValue;
            SetLoadedBaseline(row, writeValue);
            _grid.InvalidateCell(0, row.Index);

            if (!string.Equals(oldValue, writeValue, StringComparison.Ordinal))
                AppendRecord(item, "改写变化", oldValue, writeValue);

            LogAction?.Invoke($"改写成功 {item.Address} <= {writeValue}");
            return true;
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"改写失败 {item.Address}: {ex.Message}");
            return false;
        }
    }

    private ParameterItem? ReadParameterItem(DataGridViewRow row)
    {
        string name = Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        string typeText = Convert.ToString(row.Cells[1].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        string address = Convert.ToString(row.Cells[2].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        string comment = Convert.ToString(row.Cells[5].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;

        if (!Enum.TryParse(typeText, true, out ParameterValueType type))
            return null;
        if (string.IsNullOrWhiteSpace(address))
            return null;

        int realArrayLength = 0;
        if (type == ParameterValueType.REAL_ARRAY)
        {
            if (row.Tag is int tagLen && tagLen > 0)
                realArrayLength = tagLen;
            else
                realArrayLength = TryInferRealArrayLengthFromComment(comment);

            if (realArrayLength <= 0)
                realArrayLength = 1;
        }

        return new ParameterItem
        {
            Name = string.IsNullOrWhiteSpace(name) ? address : name,
            Type = type,
            Address = address,
            RealArrayLength = realArrayLength,
            Comment = comment
        };
    }

    private void UpdateWriteValueEditor(DataGridViewRow row)
    {
        if (row.Cells.Count <= 4)
            return;

        string typeText = Convert.ToString(row.Cells[1].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        if (!Enum.TryParse(typeText, true, out ParameterValueType type))
            type = ParameterValueType.INT;

        string currentValue = Convert.ToString(row.Cells[4].Value, CultureInfo.InvariantCulture) ?? string.Empty;

        if (type == ParameterValueType.BOOL)
        {
            var comboCell = new DataGridViewComboBoxCell();
            comboCell.Items.AddRange("true", "false");

            if (string.Equals(currentValue, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(currentValue, "false", StringComparison.OrdinalIgnoreCase))
            {
                comboCell.Value = currentValue.ToLowerInvariant();
            }

            row.Cells[4] = comboCell;
            return;
        }

        row.Cells[4] = new DataGridViewTextBoxCell
        {
            Value = currentValue
        };
    }

    private void AppendRecord(ParameterItem item, string action, string oldValue, string newValue)
    {
        if (SuppressChangeJournal)
            return;

        // 实时工艺地址（整板时间/待料时间等每秒都在变的量）不进入参数变化记录；
        // 静默跳过，不写主日志，避免把运行日志也刷满。
        if (IsRealtimeExcludedAddress(item.Address))
            return;

        var record = new ParameterChangeRecord
        {
            Timestamp = DateTime.Now,
            Name = item.Name,
            Address = item.Address,
            Action = action,
            OldValue = oldValue,
            NewValue = newValue
        };

        _records.Add(record);
        SaveRecords();
        LogAction?.Invoke($"参数变化 {record.Address}: {oldValue} -> {newValue} ({action})");
    }

    /// <summary>判断地址是否属于实时工艺数据配置（命中则不记参数变化）。</summary>
    private bool IsRealtimeExcludedAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(ProcessRealtimeStorageKey) || string.IsNullOrWhiteSpace(address))
            return false;

        RefreshRealtimeExcludeCache();
        if (_realtimeExcludeAddresses is not { Count: > 0 })
            return false;

        string trimmed = address.Trim();
        if (_realtimeExcludeAddresses.Contains(trimmed))
            return true;

        try
        {
            // 规范化比较，避免 MD4101 / md4101 之类写法差异漏判
            var (type, offset, bit) = global::PlcConectCom.AddressParser.ParseAddress(trimmed);
            return _realtimeExcludeAddresses.Contains($"{type}:{offset}:{bit}");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>按文件最后写入时间缓存实时工艺地址集合；配置文件变更后自动重载。</summary>
    private void RefreshRealtimeExcludeCache()
    {
        string fileName = ProcessRealtimeDataConfigControl.ConfigFileName;
        if (!string.IsNullOrWhiteSpace(ProcessRealtimeStorageKey))
        {
            // 与 ProcessRealtimeDataConfigControl 的存储键规则保持一致（仅保留字母数字）
            string safeKey = string.Concat(ProcessRealtimeStorageKey.Where(char.IsLetterOrDigit));
            if (!string.IsNullOrWhiteSpace(safeKey))
                fileName = $"{Path.GetFileNameWithoutExtension(fileName)}.{safeKey}{Path.GetExtension(fileName)}";
        }

        string path = Path.Combine(AppContext.BaseDirectory, fileName);
        DateTime lastUtc = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        if (_realtimeExcludeAddresses is not null && lastUtc == _realtimeExcludeFileUtc)
            return;

        _realtimeExcludeFileUtc = lastUtc;
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var realtimeItem in ProcessRealtimeDataConfigControl.LoadConfiguredItems(ProcessRealtimeStorageKey))
        {
            if (string.IsNullOrWhiteSpace(realtimeItem.Address))
                continue;

            addresses.Add(realtimeItem.Address.Trim());
            try
            {
                var (type, offset, bit) = global::PlcConectCom.AddressParser.ParseAddress(realtimeItem.Address.Trim());
                addresses.Add($"{type}:{offset}:{bit}");
            }
            catch
            {
                // 无法解析的地址只按原文比较
            }
        }

        _realtimeExcludeAddresses = addresses;
    }

    private void RemoveCurrentRow()
    {
        if (_grid.SelectedRows.Count == 0)
            return;

        _grid.Rows.RemoveAt(_grid.SelectedRows[0].Index);
        SaveConfig();
    }

    /// <summary>弹出「参数变化记录」窗口（与「查看记录」相同）；打开前从磁盘重新加载记录文件。</summary>
    public void ShowParameterChangeRecordsDialog(IWin32Window? owner)
    {
        LoadRecords();
        ShowParameterChangeRecordsDialogCore(owner);
    }

    private void ShowParameterChangeRecordsDialogCore(IWin32Window? owner)
    {
        IWin32Window showOwner = owner ?? (IWin32Window)this;
        using var dialog = new Form
        {
            Text = "参数变化记录",
            Width = 900,
            Height = 450,
            StartPosition = FormStartPosition.CenterParent
        };

        var recordGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeColumns = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };

        var toolPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(6)
        };

        var dateFrom = new DateTimePicker
        {
            Width = 140,
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "yyyy-MM-dd",
            ShowCheckBox = true,
            Checked = false
        };

        var dateTo = new DateTimePicker
        {
            Width = 140,
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "yyyy-MM-dd",
            ShowCheckBox = true,
            Checked = false
        };

        var btnQuery = new Button { Text = "日期查询", Width = 72 };
        var btnClear = new Button { Text = "全部", Width = 56 };
        var btnClearRecords = new Button { Text = "清空记录", Width = 72 };

        recordGrid.Columns.Add("Timestamp", "时间");
        recordGrid.Columns.Add("Name", "参数名");
        recordGrid.Columns.Add("Address", "地址");
        recordGrid.Columns.Add("Action", "动作");
        recordGrid.Columns.Add("OldValue", "旧值");
        recordGrid.Columns.Add("NewValue", "新值");

        void BindRecords(IEnumerable<ParameterChangeRecord> source)
        {
            recordGrid.Rows.Clear();
            foreach (var record in source.OrderByDescending(r => r.Timestamp))
            {
                recordGrid.Rows.Add(
                    record.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    record.Name,
                    record.Address,
                    record.Action,
                    record.OldValue,
                    record.NewValue);
            }
        }

        btnQuery.Click += (_, _) =>
        {
            IEnumerable<ParameterChangeRecord> source = _records;
            if (dateFrom.Checked)
                source = source.Where(r => r.Timestamp.Date >= dateFrom.Value.Date);
            if (dateTo.Checked)
                source = source.Where(r => r.Timestamp.Date <= dateTo.Value.Date);
            BindRecords(source);
        };

        btnClear.Click += (_, _) =>
        {
            dateFrom.Checked = false;
            dateTo.Checked = false;
            BindRecords(_records);
        };

        btnClearRecords.Click += (_, _) =>
        {
            if (_records.Count == 0)
                return;

            var result = MessageBox.Show(dialog, "确认清空所有参数变化记录吗？", "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (result != DialogResult.Yes)
                return;

            _records.Clear();
            SaveRecords();
            dateFrom.Checked = false;
            dateTo.Checked = false;
            BindRecords(_records);
            LogAction?.Invoke("参数变化记录已清空。");
        };

        toolPanel.Controls.Add(new Label { Text = "开始日期", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        toolPanel.Controls.Add(dateFrom);
        toolPanel.Controls.Add(new Label { Text = "结束日期", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
        toolPanel.Controls.Add(dateTo);
        toolPanel.Controls.Add(btnQuery);
        toolPanel.Controls.Add(btnClear);
        toolPanel.Controls.Add(btnClearRecords);

        BindRecords(_records);
        dialog.Controls.Add(recordGrid);
        dialog.Controls.Add(toolPanel);
        DataGridViewNoSortHelper.WireControlSubtreeForNoColumnSort(dialog);
        AppDialog.ShowDialog(dialog, showOwner);
    }

    private void SaveRecipeFromGrid()
    {
        string? recipeName = PromptText("保存参数配方", "请输入配方名称：");
        if (string.IsNullOrWhiteSpace(recipeName))
            return;

        var entries = new List<ParameterRecipeEntry>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            var item = ReadParameterItem(row);
            if (item is null)
                continue;

            string value = (Convert.ToString(row.Cells[3].Value, CultureInfo.InvariantCulture)
                ?? Convert.ToString(row.Cells[4].Value, CultureInfo.InvariantCulture)
                ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(value))
                continue;

            entries.Add(new ParameterRecipeEntry
            {
                Name = item.Name,
                Type = item.Type,
                Address = item.Address,
                RealArrayLength = item.RealArrayLength,
                Comment = item.Comment,
                Value = NormalizeRecipeValue(item.Type, value)
            });
        }

        if (entries.Count == 0)
        {
            LogAction?.Invoke("没有可保存的参数值。请先读取或填写改写值。");
            return;
        }

        var recipe = new ParameterRecipe
        {
            RecipeName = recipeName.Trim(),
            SavedAt = DateTime.Now,
            Entries = entries
        };

        _recipes.RemoveAll(x => string.Equals(x.RecipeName, recipe.RecipeName, StringComparison.OrdinalIgnoreCase));
        _recipes.Add(recipe);
        SaveRecipes();
        LogAction?.Invoke($"参数配方已保存: {recipe.RecipeName}");
    }

    private void LoadRecipeToGrid()
    {
        if (_recipes.Count == 0)
        {
            LogAction?.Invoke("没有可加载的参数配方。");
            return;
        }

        string? selected = PromptSelectRecipe(_recipes.OrderByDescending(x => x.SavedAt).Select(x => x.RecipeName).ToList());
        if (string.IsNullOrWhiteSpace(selected))
            return;

        var recipe = _recipes.FirstOrDefault(x => string.Equals(x.RecipeName, selected, StringComparison.OrdinalIgnoreCase));
        if (recipe is null)
            return;

        foreach (DataGridViewRow row in _grid.Rows)
        {
            var item = ReadParameterItem(row);
            if (item is null)
                continue;

            var entry = recipe.Entries.FirstOrDefault(x =>
                x.Type == item.Type
                && string.Equals(x.Address, item.Address, StringComparison.OrdinalIgnoreCase)
                && (item.Type != ParameterValueType.REAL_ARRAY || x.RealArrayLength == item.RealArrayLength));
            if (entry is null)
                continue;

            row.Cells[4].Value = NormalizeRecipeValue(item.Type, entry.Value);
        }

        LogAction?.Invoke($"参数配方已加载到改写值: {recipe.RecipeName}");
    }

    private static string? PromptText(string title, string label)
    {
        using var dialog = new Form
        {
            Text = title,
            Width = 360,
            Height = 150,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var lbl = new Label { Text = label, Left = 12, Top = 16, Width = 320 };
        var txt = new TextBox { Left = 12, Top = 40, Width = 320 };
        var btnOk = new Button { Text = "确定", Left = 176, Top = 72, Width = 72, DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "取消", Left = 260, Top = 72, Width = 72, DialogResult = DialogResult.Cancel };

        dialog.Controls.Add(lbl);
        dialog.Controls.Add(txt);
        dialog.Controls.Add(btnOk);
        dialog.Controls.Add(btnCancel);
        dialog.AcceptButton = btnOk;
        dialog.CancelButton = btnCancel;

        return AppDialog.ShowDialog(dialog) == DialogResult.OK ? txt.Text : null;
    }

    private static string NormalizeRecipeValue(ParameterValueType type, string? value)
    {
        string raw = value?.Trim() ?? string.Empty;
        if (type != ParameterValueType.BOOL)
            return raw;

        if (string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "on", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "高", StringComparison.OrdinalIgnoreCase))
            return "true";

        if (string.Equals(raw, "0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "off", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "低", StringComparison.OrdinalIgnoreCase))
            return "false";

        if (bool.TryParse(raw, out bool b))
            return b ? "true" : "false";

        return "false";
    }

    private string? PromptSelectRecipe(IReadOnlyList<string> recipeNames)
    {
        using var dialog = new Form
        {
            Text = "选择参数配方",
            Width = 360,
            Height = 160,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var cmb = new ComboBox
        {
            Left = 12,
            Top = 24,
            Width = 320,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        cmb.Items.AddRange(recipeNames.Cast<object>().ToArray());
        if (cmb.Items.Count > 0)
            cmb.SelectedIndex = 0;

        var btnOk = new Button { Text = "加载", Left = 176, Top = 64, Width = 72, DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "取消", Left = 260, Top = 64, Width = 72, DialogResult = DialogResult.Cancel };

        dialog.Controls.Add(cmb);
        dialog.Controls.Add(btnOk);
        dialog.Controls.Add(btnCancel);
        dialog.AcceptButton = btnOk;
        dialog.CancelButton = btnCancel;

        return AppDialog.ShowDialog(dialog, this) == DialogResult.OK ? cmb.SelectedItem?.ToString() : null;
    }

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(_configPath))
                return;

            var json = File.ReadAllText(_configPath);
            var items = JsonSerializer.Deserialize<List<ParameterItem>>(json);
            if (items is null)
                return;

            SetParameters(items);
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"加载参数配置失败: {ex.Message}");
        }
    }

    private void SaveConfig()
    {
        if (_suspendSave)
            return;

        try
        {
            var items = _grid.Rows.Cast<DataGridViewRow>()
                .Select(ReadParameterItem)
                .Where(x => x is not null)
                .Cast<ParameterItem>()
                .ToList();

            File.WriteAllText(_configPath, JsonSerializer.Serialize(items, JsonFileOptions));
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"保存参数配置失败: {ex.Message}");
        }
    }

    private void LoadRecords()
    {
        try
        {
            if (!File.Exists(_recordPath))
                return;

            var json = File.ReadAllText(_recordPath);
            var records = JsonSerializer.Deserialize<List<ParameterChangeRecord>>(json);
            if (records is null)
                return;

            _records.Clear();
            _records.AddRange(records);
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"加载参数记录失败: {ex.Message}");
        }
    }

    private void SaveRecords()
    {
        try
        {
            File.WriteAllText(_recordPath, JsonSerializer.Serialize(_records, JsonFileOptions));
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"保存参数记录失败: {ex.Message}");
        }
    }

    private void LoadRecipes()
    {
        try
        {
            if (!File.Exists(_recipePath))
                return;

            var json = File.ReadAllText(_recipePath);
            var recipes = JsonSerializer.Deserialize<List<ParameterRecipe>>(json);
            if (recipes is null)
                return;

            _recipes.Clear();
            _recipes.AddRange(recipes);
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"加载参数配方失败: {ex.Message}");
        }
    }

    private void SaveRecipes()
    {
        try
        {
            File.WriteAllText(_recipePath, JsonSerializer.Serialize(_recipes, JsonFileOptions));
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"保存参数配方失败: {ex.Message}");
        }
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
