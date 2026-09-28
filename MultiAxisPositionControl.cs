using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExcelDataReader;

namespace PlcConectCom.WinFormsTest;

// 多轴点位管理控件：页签展示点位并提供试教/定位/保存/加载动作
public sealed class MultiAxisPositionControl : UserControl
{
    /// <summary>与 AxisMotionGroupTableControl 中「当前位置」列对应的字段键（AxisMotionFieldDefs.CurrentPos）。</summary>
    private const string AxisMotionCurrentPosFieldKey = "CurrentPos";

    private const string ConfigFileName = "MultiAxisPosition.config.json";
    private const string KeywordFileName = "MultiAxisPosition.keywords.json";
    private const string AuthConfigFileName = "RoleAuth.config.json";
    private const int MaxImportAxisCount = 256;
    private static readonly Regex IndexedAxisCommentRegex = new(
        @"(?:^|\s)(?<index>\d+)\s*[:：]\s*(?<name>.*?)(?=\s+\d+\s*[:：]|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonFileOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly TabControl _tabControl = new() { Dock = DockStyle.Fill };
    private readonly FlowLayoutPanel _teachInputPanel = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false };
    private readonly FlowLayoutPanel _locateInputPanel = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false };
    private readonly Button _btnTeach = new() { Text = "试教位置", Width = 90 };
    private readonly Button _btnLocate = new() { Text = "定位位置", Width = 90 };
    private readonly Button _btnSave = new() { Text = "保存位置", Width = 90 };
    private readonly Button _btnLoad = new() { Text = "加载位置", Width = 90 };
    private readonly Button _btnEditAuth = new() { Text = "编辑解锁", Width = 90 };
    private readonly Button _btnImportExcel = new() { Text = "导入Excel", Width = 90 };
    private readonly ComboBox _cmbKeyword = new() { Width = 150, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly Button _btnSearch = new() { Text = "关键字搜索", Width = 80 };
    private readonly Button _btnClearKeyword = new() { Text = "清除关键字", Width = 80 };
    private readonly Dictionary<string, TextBox> _teachInputs = [];
    private readonly Dictionary<string, TextBox> _locateInputs = [];
    private readonly List<AxisPageDefinition> _pages = [];
    private readonly List<string> _keywords = [];
    private readonly string _configPath;
    private readonly string _keywordPath;
    private bool _suspendAutoSave;
    private bool _canModify = true;
    private bool _editUnlocked;

    public MultiAxisPositionControl() : this(null) { }

    public MultiAxisPositionControl(string? storageKey)
    {
        _configPath = Path.Combine(AppContext.BaseDirectory, BuildStorageFileName(ConfigFileName, storageKey));
        _keywordPath = Path.Combine(AppContext.BaseDirectory, BuildStorageFileName(KeywordFileName, storageKey));
        BuildUi();
        BindEvents();
        LoadKeywords();
        SetPermission(canModify: true);
        LoadConfigFromFile();
    }

    private static string BuildStorageFileName(string fileName, string? storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
            return fileName;
        string safeKey = string.Concat(storageKey.Where(char.IsLetterOrDigit));
        return string.IsNullOrWhiteSpace(safeKey) ? fileName : $"{Path.GetFileNameWithoutExtension(fileName)}.{safeKey}{Path.GetExtension(fileName)}";
    }

    public event Func<MultiAxisActionContext, Task>? TeachPositionRequested;

    public event Func<MultiAxisActionContext, Task>? LocatePositionRequested;

    public event Func<MultiAxisActionContext, Task>? SavePositionRequested;

    public event Func<MultiAxisActionContext, Task>? LoadPositionRequested;

    public Action<string>? LogAction { get; set; }

    public Func<bool>? EditPermissionRequest { get; set; }

    public Func<global::PlcConectCom.PlcCommunicator?>? PlcProvider { get; set; }

    public global::PlcConectCom.PlcCommunicator? PlcInstance { get; set; }

    /// <summary>解析轴在 AxisMotionGroupTableControl 中某字段的 PLC 地址，例如 fieldKey=<see cref="AxisMotionCurrentPosFieldKey"/> 表示「当前位置」。</summary>
    public Func<string, string, string?>? AxisFieldAddressResolver { get; set; }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(4)
        };

        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var topActionPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 0, 0, 4)
        };
        topActionPanel.Controls.Add(_btnImportExcel);
        topActionPanel.Controls.Add(_cmbKeyword);
        topActionPanel.Controls.Add(_btnSearch);
        topActionPanel.Controls.Add(_btnClearKeyword);

        var positionPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            Padding = new Padding(0, 4, 0, 4)
        };

        positionPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        positionPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        positionPanel.Controls.Add(CreateInputRow("试教位置", _teachInputPanel), 0, 0);
        positionPanel.Controls.Add(CreateInputRow("定位位置", _locateInputPanel), 0, 1);

        var actionPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 2, 0, 0)
        };

        actionPanel.Controls.AddRange([_btnEditAuth, _btnTeach, _btnLocate, _btnSave, _btnLoad]);

        root.Controls.Add(topActionPanel, 0, 0);
        root.Controls.Add(_tabControl, 0, 1);
        root.Controls.Add(positionPanel, 0, 2);
        root.Controls.Add(actionPanel, 0, 3);

        Controls.Add(root);
    }

    private void BindEvents()
    {
        _btnEditAuth.Click += (_, _) => ToggleEditAuth();
        _tabControl.SelectedIndexChanged += (_, _) => RebuildPositionInputsForCurrentPage();
        _btnTeach.Click += async (_, _) => await HandleTeachAsync();
        _btnLocate.Click += async (_, _) => await HandleLocateAsync();
        _btnSave.Click += async (_, _) => await HandleSaveAsync();
        _btnLoad.Click += async (_, _) => await HandleLoadAsync();
        _btnImportExcel.Click += (_, _) => ImportPagesFromExcel();
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
    }

    public void SetPermission(bool canModify)
    {
        _canModify = canModify;
        if (!canModify)
            _editUnlocked = false;

        _btnEditAuth.Enabled = canModify;
        _btnEditAuth.Text = _editUnlocked ? "编辑锁定" : "编辑解锁";
        _btnTeach.Enabled = canModify && _editUnlocked;
        _btnLocate.Enabled = canModify && _editUnlocked;
        _btnSave.Enabled = canModify && _editUnlocked;
        _btnLoad.Enabled = canModify;
        _btnImportExcel.Enabled = canModify && _editUnlocked;
        _cmbKeyword.Enabled = canModify && _editUnlocked;
        _btnSearch.Enabled = canModify && _editUnlocked;
        _btnClearKeyword.Enabled = canModify && _editUnlocked;
        UpdateInputEditorsState();
    }

    public void SetPages(IEnumerable<AxisPageDefinition> pages)
    {
        SetPages(pages, saveAfterSet: true);
    }

    private void SetPages(IEnumerable<AxisPageDefinition> pages, bool saveAfterSet)
    {
        _pages.Clear();
        _pages.AddRange(pages);

        _tabControl.TabPages.Clear();
        foreach (var page in _pages)
        {
            var grid = CreateGridForPage(page);
            var tabPage = new TabPage(page.PageName) { Tag = page };
            tabPage.Controls.Add(grid);
            _tabControl.TabPages.Add(tabPage);
        }

        RebuildPositionInputsForCurrentPage();

        if (!string.IsNullOrWhiteSpace(_cmbKeyword.Text.Trim()))
            ApplyKeywordFilter(saveKeyword: false);

        if (saveAfterSet && !_suspendAutoSave)
            SaveConfigToFile();
    }

    public void LoadDemoPages()
    {
        SetPages([
            new AxisPageDefinition
            {
                PageName = "前后焊头",
                Axes = [
                    new AxisDefinition { AxisName = "X1", DisplayName = "X1" },
                    new AxisDefinition { AxisName = "X2", DisplayName = "X2" },
                    new AxisDefinition { AxisName = "Z", DisplayName = "Z" }
                ],
                Points = [
                    new AxisPointItem { PointName = "点位1", AxisPositions = new Dictionary<string, double> { ["X1"] = 100, ["X2"] = 100, ["Z"] = 20 } },
                    new AxisPointItem { PointName = "点位2", AxisPositions = new Dictionary<string, double> { ["X1"] = 120, ["X2"] = 118, ["Z"] = 30 } }
                ]
            },
            new AxisPageDefinition
            {
                PageName = "中焊头",
                Axes = [
                    new AxisDefinition { AxisName = "X", DisplayName = "X" },
                    new AxisDefinition { AxisName = "Y", DisplayName = "Y" }
                ],
                Points = [
                    new AxisPointItem { PointName = "点位1", AxisPositions = new Dictionary<string, double> { ["X"] = 50, ["Y"] = 10 } }
                ]
            }
        ]);
    }

    private void ImportPagesFromExcel()
    {
        if (!_canModify || !_editUnlocked)
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
            List<AxisPageDefinition> pages = BuildPagesFromImportedRows(rows);
            if (pages.Count == 0)
            {
                MessageBox.Show(this, "未解析到有效的多轴位置数据。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SetPages(pages);
            LogAction?.Invoke($"多轴位置Excel导入成功，共 {pages.Sum(x => x.Points.Count)} 个点位，{pages.Count} 个页签。来源: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导入失败: {ex.Message}", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            LogAction?.Invoke($"多轴位置Excel导入失败: {ex.Message}");
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

    private static List<AxisPageDefinition> BuildPagesFromImportedRows(IEnumerable<ImportedPositionRow> rows)
    {
        var groups = new Dictionary<string, ImportedPositionGroup>(StringComparer.OrdinalIgnoreCase);
        var orderedGroups = new List<ImportedPositionGroup>();

        foreach (var row in rows)
        {
            (string locateTrigger, string pointNumAddr, string pointNumData) =
                ExcelPositionCommentAxisGroupParser.ParseAxisGroupPlcFields(row.Comment);
            string commentForAxes = ExcelPositionCommentAxisGroupParser.CommentPrefixForIndexedAxisNames(row.Comment);

            int axisCount = InferAxisCount(row.DataTypeText, commentForAxes);
            if (axisCount <= 0)
                continue;

            List<string> axisNames = ParseAxisNamesFromComment(commentForAxes, axisCount);
            string groupKey = string.Join('\u001F', axisNames);
            if (!groups.TryGetValue(groupKey, out var group))
            {
                group = new ImportedPositionGroup(DerivePageName(axisNames), axisNames);
                groups[groupKey] = group;
                orderedGroups.Add(group);
            }

            Dictionary<string, string> axisAddresses = BuildAxisAddresses(row.Address, axisNames);
            group.Points.Add(new AxisPointItem
            {
                PointName = row.Name,
                BaseAddress = row.Address,
                DataTypeText = row.DataTypeText,
                Comment = row.Comment,
                LocateTriggerPlcAddress = locateTrigger,
                PointNumberPlcAddress = pointNumAddr,
                PointNumberData = pointNumData,
                AxisAddresses = axisAddresses,
                AxisPositions = axisNames.ToDictionary(x => x, _ => 0d, StringComparer.OrdinalIgnoreCase)
            });
        }

        var usedPageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pages = new List<AxisPageDefinition>();
        foreach (var group in orderedGroups)
        {
            string pageName = MakeUniquePageName(group.PageName, usedPageNames);
            pages.Add(new AxisPageDefinition
            {
                PageName = pageName,
                Axes = group.AxisNames.Select(x => new AxisDefinition { AxisName = x, DisplayName = x }).ToList(),
                Points = group.Points
            });
        }

        return pages;
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
            return dataType.RealArrayLength;

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

    private static Dictionary<string, string> BuildAxisAddresses(string baseAddress, IReadOnlyList<string> axisNames)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < axisNames.Count; i++)
        {
            string address = string.Empty;
            if (!string.IsNullOrWhiteSpace(baseAddress))
            {
                try
                {
                    address = BuildShiftedMdAddress(baseAddress, i);
                }
                catch
                {
                    address = i == 0 ? baseAddress : string.Empty;
                }
            }

            result[axisNames[i]] = address;
        }

        return result;
    }

    private static string BuildShiftedMdAddress(string baseAddress, int indexDelta)
    {
        var (mem, offset, _) = global::PlcConectCom.AddressParser.ParseAddress(baseAddress.Trim());
        if (!string.Equals(mem, "MD", StringComparison.Ordinal))
            throw new InvalidOperationException("REAL_ARRAY 仅支持 MD 基址。");

        return $"MD{offset + indexDelta}";
    }

    private static string DerivePageName(IReadOnlyList<string> axisNames)
    {
        if (axisNames.Count == 0)
            return "多轴位置";

        string prefix = axisNames[0];
        foreach (string axisName in axisNames.Skip(1))
        {
            int len = 0;
            int max = Math.Min(prefix.Length, axisName.Length);
            while (len < max && prefix[len] == axisName[len])
                len++;

            prefix = prefix[..len];
            if (prefix.Length == 0)
                break;
        }

        prefix = prefix.Trim().TrimEnd('_', '-', '－', '/', '\\', ' ');
        if (!string.IsNullOrWhiteSpace(prefix))
            return prefix;

        return $"{axisNames.Count}轴位置";
    }

    private static string MakeUniquePageName(string pageName, HashSet<string> usedPageNames)
    {
        string baseName = string.IsNullOrWhiteSpace(pageName) ? "多轴位置" : pageName.Trim();
        string candidate = baseName;
        int index = 2;
        while (!usedPageNames.Add(candidate))
            candidate = $"{baseName}{index++}";

        return candidate;
    }

    private sealed record ImportedPositionRow(string Name, string Address, string DataTypeText, string Comment);

    private sealed record ImportedPositionGroup(string PageName, List<string> AxisNames)
    {
        public List<AxisPointItem> Points { get; } = [];
    }

    private IEnumerable<DataGridView> EnumeratePositionGrids()
    {
        foreach (TabPage tab in _tabControl.TabPages)
        {
            foreach (Control c in tab.Controls)
            {
                if (c is DataGridView dgv)
                    yield return dgv;
            }
        }
    }

    private void ApplyKeywordFilter(bool saveKeyword)
    {
        string keyword = _cmbKeyword.Text.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            foreach (DataGridView grid in EnumeratePositionGrids())
            {
                foreach (DataGridViewRow row in grid.Rows)
                    row.Visible = true;
            }

            LogAction?.Invoke("已取消关键字筛选，显示全部点位。");
            return;
        }

        foreach (DataGridView grid in EnumeratePositionGrids())
        {
            foreach (DataGridViewRow row in grid.Rows)
                row.Visible = IsRowMatchKeyword(row, keyword);
        }

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
        DataGridView? view = row.DataGridView;
        for (int c = 0; c < row.Cells.Count && view is not null; c++)
        {
            parts.Add(view.Columns[c].HeaderText ?? string.Empty);
            parts.Add(Convert.ToString(row.Cells[c].Value, CultureInfo.InvariantCulture) ?? string.Empty);
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
        foreach (DataGridView grid in EnumeratePositionGrids())
        {
            foreach (DataGridViewRow row in grid.Rows)
                row.Visible = true;
        }

        _keywords.Clear();
        RebindKeywordCombo();
        SaveKeywords();
        LogAction?.Invoke("已一键清除关键字并恢复全部点位显示。");
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
            LogAction?.Invoke($"加载多轴位置关键字失败: {ex.Message}");
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
            LogAction?.Invoke($"保存多轴位置关键字失败: {ex.Message}");
        }
    }

    private Control CreateInputRow(string title, FlowLayoutPanel inputPanel)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            AutoSize = true
        };

        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 8, 0) }, 0, 0);
        row.Controls.Add(inputPanel, 1, 0);

        return row;
    }

    private DataGridView CreateGridForPage(AxisPageDefinition page)
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeColumns = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            MultiSelect = false,
            ReadOnly = true,
            EditMode = DataGridViewEditMode.EditProgrammatically
        };

        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "PointName",
            HeaderText = "点位名称",
            Width = 120,
            ReadOnly = true
        });

        foreach (var axis in page.Axes)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = axis.AxisName,
                HeaderText = string.IsNullOrWhiteSpace(axis.DisplayName) ? axis.AxisName : axis.DisplayName,
                Width = 80,
                ReadOnly = true
            });
        }

        foreach (var point in page.Points)
        {
            int rowIndex = grid.Rows.Add();
            var row = grid.Rows[rowIndex];
            row.Cells[0].Value = point.PointName;
            for (int i = 0; i < page.Axes.Count; i++)
            {
                string axisName = page.Axes[i].AxisName;
                if (point.AxisPositions.TryGetValue(axisName, out double value))
                    row.Cells[i + 1].Value = value.ToString("0.###", CultureInfo.InvariantCulture);
                else
                    row.Cells[i + 1].Value = "0";
            }
        }

        grid.Tag = page;
        grid.CellBeginEdit += (_, e) => e.Cancel = true;
        grid.SelectionChanged += (_, _) => SyncInputsFromSelectedRow(grid, page);

        var rowMenu = new ContextMenuStrip();
        var menuTeach = new ToolStripMenuItem("试教位置");
        menuTeach.Click += async (_, _) => await TeachFromAxisMotionCurrentPosAsync(page, grid);
        rowMenu.Items.Add(menuTeach);
        rowMenu.Opening += (_, e) =>
        {
            menuTeach.Enabled = _canModify && _editUnlocked;
            if (grid.SelectedRows.Count == 0)
                e.Cancel = true;
        };
        grid.ContextMenuStrip = rowMenu;
        grid.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right)
                return;

            var hit = grid.HitTest(e.X, e.Y);
            if (hit.RowIndex >= 0 && hit.RowIndex < grid.Rows.Count)
            {
                grid.ClearSelection();
                grid.Rows[hit.RowIndex].Selected = true;
            }
        };

        if (grid.Rows.Count > 0)
            grid.Rows[0].Selected = true;

        return grid;
    }

    private void RebuildPositionInputsForCurrentPage()
    {
        _teachInputs.Clear();
        _locateInputs.Clear();
        _teachInputPanel.Controls.Clear();
        _locateInputPanel.Controls.Clear();

        var page = GetCurrentPageDefinition();
        if (page is null)
            return;

        foreach (var axis in page.Axes)
        {
            string title = string.IsNullOrWhiteSpace(axis.DisplayName) ? axis.AxisName : axis.DisplayName;

            var teachInput = CreateAxisInputControl(title);
            var locateInput = CreateAxisInputControl(title);

            _teachInputPanel.Controls.Add(teachInput.container);
            _locateInputPanel.Controls.Add(locateInput.container);

            _teachInputs[axis.AxisName] = teachInput.textBox;
            _locateInputs[axis.AxisName] = locateInput.textBox;
        }

        if (_tabControl.SelectedTab?.Controls.OfType<DataGridView>().FirstOrDefault() is DataGridView grid)
            SyncInputsFromSelectedRow(grid, page);

        UpdateInputEditorsState();
    }

    private void ToggleEditAuth()
    {
        if (!_canModify)
        {
            MessageBox.Show(this, "当前权限不可编辑。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_editUnlocked)
        {
            _editUnlocked = false;
            SetPermission(_canModify);
            return;
        }

        bool ok;
        try
        {
            ok = RequestEditAuthorization();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"编辑解锁失败：{ex.Message}", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!ok)
        {
            MessageBox.Show(this, "编辑权限验证未通过。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _editUnlocked = true;
        SetPermission(_canModify);
        LogAction?.Invoke("MultiAxisPositionControl 已解锁编辑。");
    }

    private bool RequestEditAuthorization()
    {
        if (EditPermissionRequest is not null)
            return EditPermissionRequest();

        var (adminPassword, _) = LoadRolePasswords();

        using var dialog = new Form
        {
            Text = "编辑权限验证",
            Width = 360,
            Height = 210,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(10)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var txtPassword = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };

        layout.Controls.Add(new Label { Text = "管理员密码", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(txtPassword, 1, 0);
        layout.Controls.Add(new Label { Text = "仅管理员账户可解锁编辑", AutoSize = true, ForeColor = Color.DimGray }, 1, 1);

        var panelBtn = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var btnOk = new Button { Text = "确认", Width = 72, DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "取消", Width = 72, DialogResult = DialogResult.Cancel };
        panelBtn.Controls.Add(btnOk);
        panelBtn.Controls.Add(btnCancel);

        dialog.Controls.Add(layout);
        dialog.Controls.Add(panelBtn);
        dialog.AcceptButton = btnOk;
        dialog.CancelButton = btnCancel;

        DataGridViewNoSortHelper.WireControlSubtreeForNoColumnSort(dialog);
        if (AppDialog.ShowDialog(dialog, this) != DialogResult.OK)
            return false;

        string password = txtPassword.Text.Trim();
        bool pass = string.Equals(password, adminPassword, StringComparison.Ordinal);

        if (!pass)
        {
            MessageBox.Show(this, "管理员密码错误。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        return true;
    }

    private static (string adminPassword, string engineerPassword) LoadRolePasswords()
    {
        const string defaultAdmin = "admin123";
        const string defaultEngineer = "engineer123";

        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, AuthConfigFileName);
            if (!File.Exists(path))
                return (defaultAdmin, defaultEngineer);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            string admin = root.TryGetProperty("AdminPassword", out var adminProp)
                ? (adminProp.GetString() ?? defaultAdmin)
                : defaultAdmin;
            string engineer = root.TryGetProperty("EngineerPassword", out var engProp)
                ? (engProp.GetString() ?? defaultEngineer)
                : defaultEngineer;
            return (admin, engineer);
        }
        catch
        {
            return (defaultAdmin, defaultEngineer);
        }
    }

    private void UpdateInputEditorsState()
    {
        bool readOnly = !_canModify || !_editUnlocked;
        foreach (var textBox in _teachInputs.Values)
            textBox.ReadOnly = readOnly;
        foreach (var textBox in _locateInputs.Values)
            textBox.ReadOnly = readOnly;
    }

    private static (Control container, TextBox textBox) CreateAxisInputControl(string axisTitle)
    {
        var panel = new Panel { Width = 125, Height = 28 };
        var label = new Label { Text = axisTitle, AutoSize = true, Location = new Point(0, 6) };
        var text = new TextBox { Width = 78, Location = new Point(40, 2) };

        panel.Controls.Add(label);
        panel.Controls.Add(text);

        return (panel, text);
    }

    private void SyncInputsFromSelectedRow(DataGridView grid, AxisPageDefinition page)
    {
        if (grid.SelectedRows.Count == 0)
            return;

        var row = grid.SelectedRows[0];
        for (int i = 0; i < page.Axes.Count; i++)
        {
            string axisName = page.Axes[i].AxisName;
            string value = Convert.ToString(row.Cells[i + 1].Value, CultureInfo.InvariantCulture) ?? "0";

            if (_teachInputs.TryGetValue(axisName, out var teachInput))
                teachInput.Text = value;
            if (_locateInputs.TryGetValue(axisName, out var locateInput))
                locateInput.Text = value;
        }
    }

    private async Task HandleTeachAsync()
    {
        if (!_canModify || !_editUnlocked)
            return;

        var page = GetCurrentPageDefinition();
        if (page is null)
            return;

        if (_tabControl.SelectedTab?.Controls.OfType<DataGridView>().FirstOrDefault() is not DataGridView grid)
            return;

        await TeachFromAxisMotionCurrentPosAsync(page, grid);
    }

    /// <summary>从 AxisMotion 表中各轴「当前位置」地址读取实时值写入试教框，并触发 <see cref="TeachPositionRequested"/>。</summary>
    private async Task TeachFromAxisMotionCurrentPosAsync(AxisPageDefinition page, DataGridView grid)
    {
        if (grid.SelectedRows.Count == 0)
        {
            LogAction?.Invoke("未选中点位。");
            return;
        }

        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
        {
            LogAction?.Invoke("请先连接 PLC。");
            return;
        }

        if (AxisFieldAddressResolver is null)
        {
            LogAction?.Invoke("未配置轴字段地址解析（AxisFieldAddressResolver），无法读取当前位置。");
            return;
        }

        foreach (var axis in page.Axes)
        {
            string axisName = axis.AxisName;
            string? addr = AxisFieldAddressResolver.Invoke(axisName, AxisMotionCurrentPosFieldKey)?.Trim();
            if (string.IsNullOrWhiteSpace(addr))
            {
                LogAction?.Invoke($"轴[{axisName}]未配置「当前位置」(CurrentPos) 地址。");
                continue;
            }

            try
            {
                double value = await plc.ReadDoubleAsync(addr);
                if (_teachInputs.TryGetValue(axisName, out var teachBox))
                    teachBox.Text = value.ToString("0.###", CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                LogAction?.Invoke($"试教读取失败 轴[{axisName}]: {ex.Message}");
            }
        }

        var context = BuildActionContext();
        if (context is null)
            return;

        if (TeachPositionRequested is not null)
            await TeachPositionRequested(context);

        LogAction?.Invoke($"试教位置: {context.PageName}/{context.PointName}");
    }

    private async Task HandleLocateAsync()
    {
        if (!_canModify || !_editUnlocked)
            return;

        var context = BuildActionContext();
        if (context is null)
            return;

        foreach (var (axisName, value) in context.TeachPositions)
        {
            if (_locateInputs.TryGetValue(axisName, out var locateInput))
                locateInput.Text = value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        if (LocatePositionRequested is not null)
            await LocatePositionRequested(context);

        LogAction?.Invoke($"定位位置: {context.PageName}/{context.PointName}");
    }

    private async Task HandleSaveAsync()
    {
        if (!_canModify || !_editUnlocked)
            return;

        var context = BuildActionContext();
        if (context is null)
            return;

        if (_tabControl.SelectedTab?.Controls.OfType<DataGridView>().FirstOrDefault() is DataGridView grid
            && grid.SelectedRows.Count > 0
            && GetCurrentPageDefinition() is AxisPageDefinition page)
        {
            var row = grid.SelectedRows[0];
            for (int i = 0; i < page.Axes.Count; i++)
            {
                string axisName = page.Axes[i].AxisName;
                if (context.LocatePositions.TryGetValue(axisName, out double value))
                    row.Cells[i + 1].Value = value.ToString("0.###", CultureInfo.InvariantCulture);
            }

            var point = page.Points.FirstOrDefault(p => p.PointName == context.PointName);
            if (point is not null)
            {
                foreach (var (axisName, value) in context.LocatePositions)
                    point.AxisPositions[axisName] = value;
            }
        }

        if (SavePositionRequested is not null)
            await SavePositionRequested(context);

        SaveConfigToFile();
        LogAction?.Invoke($"保存位置: {context.PageName}/{context.PointName}");
    }

    private async Task HandleLoadAsync()
    {
        var context = BuildActionContext();
        if (context is null)
            return;

        if (_tabControl.SelectedTab?.Controls.OfType<DataGridView>().FirstOrDefault() is DataGridView grid
            && GetCurrentPageDefinition() is AxisPageDefinition page)
        {
            SyncInputsFromSelectedRow(grid, page);
        }

        if (LoadPositionRequested is not null)
            await LoadPositionRequested(context);

        LogAction?.Invoke($"加载位置: {context.PageName}/{context.PointName}");
    }

    private MultiAxisActionContext? BuildActionContext()
    {
        var page = GetCurrentPageDefinition();
        if (page is null)
            return null;

        string pointName = GetSelectedPointName() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(pointName))
        {
            LogAction?.Invoke("未选中点位。");
            return null;
        }

        var teach = ReadValues(_teachInputs);
        var locate = ReadValues(_locateInputs);

        return new MultiAxisActionContext
        {
            PageName = page.PageName,
            PointName = pointName,
            TeachPositions = teach,
            LocatePositions = locate
        };
    }

    private static Dictionary<string, double> ReadValues(Dictionary<string, TextBox> inputs)
    {
        var values = new Dictionary<string, double>();
        foreach (var (axisName, textBox) in inputs)
        {
            if (!double.TryParse(textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                && !double.TryParse(textBox.Text, out value))
                value = 0;

            values[axisName] = value;
        }

        return values;
    }

    private AxisPageDefinition? GetCurrentPageDefinition()
    {
        return _tabControl.SelectedTab?.Tag as AxisPageDefinition;
    }

    private string? GetSelectedPointName()
    {
        if (_tabControl.SelectedTab?.Controls.OfType<DataGridView>().FirstOrDefault() is not DataGridView grid)
            return null;
        if (grid.SelectedRows.Count == 0)
            return null;
        return Convert.ToString(grid.SelectedRows[0].Cells[0].Value, CultureInfo.InvariantCulture);
    }

    private void LoadConfigFromFile()
    {
        try
        {
            if (!File.Exists(_configPath))
                return;

            string json = File.ReadAllText(_configPath);
            var pages = JsonSerializer.Deserialize<List<AxisPageDefinition>>(json);
            if (pages is null || pages.Count == 0)
                return;

            _suspendAutoSave = true;
            SetPages(pages, saveAfterSet: false);
            _suspendAutoSave = false;
        }
        catch (Exception ex)
        {
            _suspendAutoSave = false;
            LogAction?.Invoke($"加载多轴位置配置失败: {ex.Message}");
        }
    }

    private void SaveConfigToFile()
    {
        if (_suspendAutoSave)
            return;

        try
        {
            string json = JsonSerializer.Serialize(_pages, JsonFileOptions);
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"保存多轴位置配置失败: {ex.Message}");
        }
    }
}
