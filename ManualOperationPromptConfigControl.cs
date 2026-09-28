using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using ExcelDataReader;

namespace PlcConectCom.WinFormsTest;

public enum ManualOperationPromptDataKind
{
    Word,
    DWord
}

public sealed class ManualOperationPromptEntry
{
    public long Code { get; set; }

    public string Message { get; set; } = string.Empty;
}

public sealed class ManualOperationPromptConfigRoot
{
    public string VariableName { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;

    /// <summary>存储为 <see cref="ManualOperationPromptDataKind"/> 枚举名。</summary>
    public string DataKind { get; set; } = nameof(ManualOperationPromptDataKind.Word);

    public List<ManualOperationPromptEntry> Entries { get; set; } = [];
}

/// <summary>解析 PLC 导出 Excel：首段行为 Name/Address/DataType，其后可为 Number/Comment 子表头，再为映射行。</summary>
public static class ManualOperationPromptExcelImport
{
    public sealed class Result
    {
        public string VariableName { get; init; } = string.Empty;

        public string Address { get; init; } = string.Empty;

        public ManualOperationPromptDataKind DataKind { get; init; }

        public List<ManualOperationPromptEntry> Entries { get; init; } = [];
    }

    public static bool TryParse(string filePath, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Result? result, out string? errorMessage)
    {
        result = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            errorMessage = "文件不存在。";
            return false;
        }

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = ExcelReaderFactory.CreateReader(stream);

            var sheetRows = new List<string[]>();
            while (reader.Read())
            {
                int n = reader.FieldCount;
                var row = new string[n];
                for (int i = 0; i < n; i++)
                    row[i] = Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                sheetRows.Add(row);
            }

            if (sheetRows.Count == 0)
            {
                errorMessage = "工作表为空。";
                return false;
            }

            int headerIdx = -1;
            int nameCol = 0;
            int addrCol = 1;
            int dtCol = 2;
            int commentCol = 4;

            for (int r = 0; r < sheetRows.Count; r++)
            {
                string[] row = sheetRows[r];
                int nc = -1, ac = -1, dc = -1, cc = -1;
                for (int c = 0; c < row.Length; c++)
                {
                    string h = row[c].Trim();
                    if (string.Equals(h, "Name", StringComparison.OrdinalIgnoreCase))
                        nc = c;
                    else if (string.Equals(h, "Address", StringComparison.OrdinalIgnoreCase))
                        ac = c;
                    else if (string.Equals(h, "DataType", StringComparison.OrdinalIgnoreCase))
                        dc = c;
                    else if (string.Equals(h, "Comment", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(h, "注释", StringComparison.OrdinalIgnoreCase))
                        cc = c;
                }

                if (nc >= 0 && ac >= 0 && dc >= 0)
                {
                    headerIdx = r;
                    nameCol = nc;
                    addrCol = ac;
                    dtCol = dc;
                    if (cc >= 0)
                        commentCol = cc;
                    break;
                }
            }

            if (headerIdx < 0)
            {
                errorMessage = "未找到表头（需包含 Name、Address、DataType 列）。";
                return false;
            }

            string plcName = string.Empty;
            string plcAddr = string.Empty;
            ManualOperationPromptDataKind kind = ManualOperationPromptDataKind.Word;
            bool plcFound = false;

            int mapNumCol = addrCol;
            int mapCommentCol = commentCol;
            bool mapColsLocked = false;

            var entries = new List<ManualOperationPromptEntry>();

            for (int r = headerIdx + 1; r < sheetRows.Count; r++)
            {
                string[] row = sheetRows[r];
                string name = GetCell(row, nameCol);
                string addr = NormalizeAddr(GetCell(row, addrCol));
                string dt = GetCell(row, dtCol);
                string comment = GetCell(row, commentCol);

                if (!plcFound)
                {
                    if (string.IsNullOrWhiteSpace(addr) || !IsWordOrDwordType(dt))
                        continue;

                    plcName = name.Trim();
                    plcAddr = addr;
                    kind = ClassifyDataKind(dt);
                    plcFound = true;
                    continue;
                }

                if (IsMappingHeaderRow(row, addrCol, commentCol))
                {
                    int? numCol = FindColumnIndex(row, h =>
                        string.Equals(h, "Number", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(h, "序号", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(h, "编号", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(h, "代码", StringComparison.OrdinalIgnoreCase));

                    int? txtCol = FindColumnIndex(row, h =>
                        string.Equals(h, "Comment", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(h, "注释", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(h, "提示", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(h, "内容", StringComparison.OrdinalIgnoreCase));

                    if (numCol is int ni && ni >= 0)
                        mapNumCol = ni;
                    if (txtCol is int ti && ti >= 0)
                        mapCommentCol = ti;

                    mapColsLocked = true;
                    continue;
                }

                string numText = GetCell(row, mapNumCol);
                string msgText = GetCell(row, mapCommentCol);

                if (!long.TryParse(numText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long code))
                {
                    if (mapColsLocked && string.IsNullOrWhiteSpace(numText) && string.IsNullOrWhiteSpace(msgText))
                        continue;

                    if (!mapColsLocked && string.IsNullOrWhiteSpace(numText) && string.IsNullOrWhiteSpace(msgText))
                        continue;

                    continue;
                }

                if (string.IsNullOrWhiteSpace(msgText))
                    continue;

                entries.Add(new ManualOperationPromptEntry { Code = code, Message = msgText });
            }

            if (!plcFound || string.IsNullOrWhiteSpace(plcAddr))
            {
                errorMessage = "未解析到 PLC 地址行（DataType 须为 INT/WORD 或 DWORD/DINT 等字类型）。";
                return false;
            }

            result = new Result
            {
                VariableName = plcName,
                Address = plcAddr,
                DataKind = kind,
                Entries = entries
            };
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static bool IsMappingHeaderRow(string[] row, int defaultAddrCol, int defaultCommentCol)
    {
        string atAddr = GetCell(row, defaultAddrCol);
        if (string.Equals(atAddr, "Number", StringComparison.OrdinalIgnoreCase))
            return true;

        for (int i = 0; i < row.Length; i++)
        {
            string t = row[i].Trim();
            if (string.Equals(t, "Number", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static int? FindColumnIndex(string[] row, Func<string, bool> predicate)
    {
        for (int i = 0; i < row.Length; i++)
        {
            if (predicate(row[i].Trim()))
                return i;
        }

        return null;
    }

    private static string GetCell(string[] row, int col)
    {
        if (col < 0 || col >= row.Length)
            return string.Empty;
        return row[col]?.Trim() ?? string.Empty;
    }

    private static string NormalizeAddr(string raw)
    {
        string a = raw.Trim();
        if (a.StartsWith('%'))
            a = a[1..];
        return a.Trim();
    }

    private static bool IsWordOrDwordType(string dt)
    {
        string t = dt.Trim();
        return string.Equals(t, "INT", StringComparison.OrdinalIgnoreCase)
               || string.Equals(t, "WORD", StringComparison.OrdinalIgnoreCase)
               || string.Equals(t, "UINT", StringComparison.OrdinalIgnoreCase)
               || string.Equals(t, "DINT", StringComparison.OrdinalIgnoreCase)
               || string.Equals(t, "DWORD", StringComparison.OrdinalIgnoreCase);
    }

    private static ManualOperationPromptDataKind ClassifyDataKind(string dt)
    {
        string t = dt.Trim();
        if (string.Equals(t, "DWORD", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "DINT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "UINT", StringComparison.OrdinalIgnoreCase))
            return ManualOperationPromptDataKind.DWord;

        return ManualOperationPromptDataKind.Word;
    }
}

/// <summary>手动操作提示：监视一个字地址，非 0 且在映射表中则弹出提示（0 不弹窗）。</summary>
public sealed class ManualOperationPromptConfigControl : UserControl
{
    private const string DefaultConfigFileName = "ManualOperationPrompt.config.json";

    private static readonly JsonSerializerOptions JsonFileOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly TableLayoutPanel _plcPanel = new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        Padding = new Padding(0, 0, 0, 8),
        ColumnCount = 4
    };

    private readonly TextBox _txtName = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right };
    private readonly TextBox _txtAddress = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right };
    private readonly ComboBox _cmbDataKind = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 160
    };

    private readonly Label _lblLiveValue = new()
    {
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.DimGray,
        Text = "当前PLC值: —"
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

    private readonly FlowLayoutPanel _actionsRow = new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        WrapContents = true,
        Padding = new Padding(0, 0, 0, 4)
    };

    private readonly Button _btnEditAuth = new() { Text = "编辑解锁", Width = 84 };
    private readonly Button _btnAdd = new() { Text = "新增行", Width = 72 };
    private readonly Button _btnDelete = new() { Text = "删除行", Width = 72 };
    private readonly Button _btnLoad = new() { Text = "加载配置", Width = 84 };
    private readonly Button _btnSave = new() { Text = "保存本地", Width = 84 };
    private readonly Button _btnAutoMonitor = new() { Text = "自动监控:关", Width = 92 };

    private readonly System.Windows.Forms.Timer _monitorTimer = new() { Interval = 1000 };
    private readonly string _defaultConfigPath;
    private readonly SemaphoreSlim _pollGate = new(1, 1);

    private ManualOperationPromptNoticeForm? _noticeForm;
    private long? _suppressedPromptCode;
    private long _lastOpenedPromptCode;
    private bool _suspendAutoSave;
    private bool _canAddDelete = true;
    private bool _canModify = true;
    private bool _editUnlocked;

    /// <summary>
    /// 构造手动操作提示配置控件。
    /// 不传 <paramref name="storageKey"/> 时使用默认配置文件 ManualOperationPrompt.config.json（主 PLC）；
    /// 传入时使用 ManualOperationPrompt.{storageKey}.config.json（子 PLC 每设备独立配置）。
    /// </summary>
    public ManualOperationPromptConfigControl(string? storageKey = null)
    {
        string fileName = string.IsNullOrWhiteSpace(storageKey)
            ? DefaultConfigFileName
            : $"ManualOperationPrompt.{storageKey}.config.json";
        _defaultConfigPath = Path.Combine(AppContext.BaseDirectory, fileName);
        _cmbDataKind.Items.AddRange(new object[] { "Word (INT)", "DWord (DWORD/DINT)" });
        _cmbDataKind.SelectedIndex = 0;

        BuildLayout();
        BindEvents();
        LoadDefaultConfig();
    }

    public Action<string>? LogAction { get; set; }

    public Func<global::PlcConectCom.PlcCommunicator?>? PlcProvider { get; set; }

    public Func<bool>? EditPermissionRequest { get; set; }

    public void PlaceImportButtonAtEnd(Control importButton)
    {
        if (importButton.Parent == _actionsRow && _actionsRow.Controls.Count > 0 && ReferenceEquals(_actionsRow.Controls[^1], importButton))
            return;

        importButton.Parent?.Controls.Remove(importButton);
        _actionsRow.Controls.Add(importButton);
    }

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
        _btnAutoMonitor.Enabled = canModify;
        _btnEditAuth.Enabled = canModify;
        _btnEditAuth.Text = _editUnlocked ? "编辑锁定" : "编辑解锁";
        _grid.ReadOnly = !_editUnlocked;
        _txtName.ReadOnly = !_editUnlocked;
        _txtAddress.ReadOnly = !_editUnlocked;
        _cmbDataKind.Enabled = _editUnlocked;
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
        CloseNoticeUi();
        _lblLiveValue.Text = "当前PLC值: —";
    }

    public void ApplyExcelImport(ManualOperationPromptExcelImport.Result data)
    {
        _suspendAutoSave = true;
        _txtName.Text = data.VariableName;
        _txtAddress.Text = data.Address;
        _cmbDataKind.SelectedIndex = data.DataKind == ManualOperationPromptDataKind.DWord ? 1 : 0;

        _grid.Rows.Clear();
        foreach (ManualOperationPromptEntry e in data.Entries.OrderBy(x => x.Code))
            _grid.Rows.Add(e.Code.ToString(CultureInfo.InvariantCulture), e.Message);

        _suspendAutoSave = false;
        SaveDefaultConfig();
    }

    private void BuildLayout()
    {
        _plcPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        _plcPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        _plcPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        _plcPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));

        void AddRow(string label1, Control c1, string label2, Control c2)
        {
            int r = _plcPanel.RowCount++;
            _plcPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _plcPanel.Controls.Add(new Label { Text = label1, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 8, 0) }, 0, r);
            _plcPanel.Controls.Add(c1, 1, r);
            _plcPanel.Controls.Add(new Label { Text = label2, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(12, 8, 8, 0) }, 2, r);
            _plcPanel.Controls.Add(c2, 3, r);
        }

        AddRow("名称", _txtName, "PLC地址", _txtAddress);
        AddRow("数据类型", _cmbDataKind, "监视", _lblLiveValue);

        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Code", HeaderText = "数字", FillWeight = 22 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Msg", HeaderText = "弹框内容", FillWeight = 78 });

        _actionsRow.Controls.AddRange([
            _btnEditAuth, _btnAdd, _btnDelete, _btnLoad, _btnSave, _btnAutoMonitor
        ]);

        Controls.Add(_grid);
        Controls.Add(_actionsRow);
        Controls.Add(_plcPanel);
    }

    private void BindEvents()
    {
        _btnEditAuth.Click += (_, _) => ToggleEditAuth();
        _btnAdd.Click += (_, _) =>
        {
            _grid.Rows.Add("0", string.Empty);
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
        _btnAutoMonitor.Click += (_, _) => ToggleAutoMonitor();
        _monitorTimer.Tick += async (_, _) => await PollPlcAsync();

        _grid.CellValueChanged += (_, e) =>
        {
            if (_suspendAutoSave || !_editUnlocked)
                return;

            if (e.RowIndex >= 0)
                SaveDefaultConfig();
        };

        _txtName.TextChanged += (_, _) =>
        {
            if (!_suspendAutoSave && _editUnlocked)
                SaveDefaultConfig();
        };

        _txtAddress.TextChanged += (_, _) =>
        {
            if (!_suspendAutoSave && _editUnlocked)
                SaveDefaultConfig();
        };

        _cmbDataKind.SelectedIndexChanged += (_, _) =>
        {
            if (!_suspendAutoSave && _editUnlocked)
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

    private void ToggleAutoMonitor()
    {
        if (!_canModify)
            return;

        if (_monitorTimer.Enabled)
        {
            StopAutoMonitor();
            return;
        }

        _monitorTimer.Start();
        _btnAutoMonitor.Text = "自动监控:开";
    }

    private async Task PollPlcAsync()
    {
        await _pollGate.WaitAsync().ConfigureAwait(true);

        try
        {
            var plc = PlcProvider?.Invoke();
            if (plc is null || !plc.IsConnected)
            {
                if (InvokeRequired)
                    BeginInvoke(() => _lblLiveValue.Text = "当前PLC值: — (离线)");
                else
                    _lblLiveValue.Text = "当前PLC值: — (离线)";

                CloseNoticeUi();
                return;
            }

            string addr = _txtAddress.Text.Trim();
            if (string.IsNullOrWhiteSpace(addr))
            {
                if (InvokeRequired)
                    BeginInvoke(() => _lblLiveValue.Text = "当前PLC值: — (未配置地址)");
                else
                    _lblLiveValue.Text = "当前PLC值: — (未配置地址)";

                CloseNoticeUi();
                return;
            }

            long raw;
            try
            {
                raw = await ReadPlcValueForPollAsync(plc, addr).ConfigureAwait(true);
            }
            catch
            {
                if (InvokeRequired)
                    BeginInvoke(() => _lblLiveValue.Text = "当前PLC值: 读取失败");
                else
                    _lblLiveValue.Text = "当前PLC值: 读取失败";

                return;
            }

            void updateLabel()
            {
                _lblLiveValue.Text = $"当前PLC值: {raw}";
            }

            if (InvokeRequired)
                BeginInvoke(updateLabel);
            else
                updateLabel();

            if (raw == 0)
            {
                _suppressedPromptCode = null;
                CloseNoticeUi();
                return;
            }

            Dictionary<long, string> map = BuildMapFromGrid();
            if (!map.TryGetValue(raw, out string? message))
            {
                CloseNoticeUi();
                return;
            }

            if (_suppressedPromptCode == raw)
                return;

            void showUi()
            {
                try
                {
                    if (_noticeForm is null || _noticeForm.IsDisposed)
                    {
                        Form? owner = FindForm() ?? TopLevelControl as Form;
                        var f = new ManualOperationPromptNoticeForm();
                        _lastOpenedPromptCode = raw;
                        f.FormClosed += OnManualPromptFormClosed;
                        _noticeForm = f;
                        if (owner is not null)
                            f.Show(owner);
                        else
                            f.Show();
                    }

                    _noticeForm!.SetContent(_txtName.Text.Trim(), message);
                }
                catch
                {
                    // ignore
                }
            }

            if (InvokeRequired)
                BeginInvoke(showUi);
            else
                showUi();
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private void OnManualPromptFormClosed(object? sender, FormClosedEventArgs e)
    {
        if (!ReferenceEquals(sender, _noticeForm))
            return;

        _noticeForm = null;
        if (sender is ManualOperationPromptNoticeForm f && f.ClosedByProgram)
            return;

        _suppressedPromptCode = _lastOpenedPromptCode;
    }

    private async Task<long> ReadPlcValueForPollAsync(global::PlcConectCom.PlcCommunicator plc, string address)
    {
        bool dword = _cmbDataKind.SelectedIndex == 1;
        if (dword)
            return await plc.ReadDWordAsync(address).ConfigureAwait(true);

        short w = await plc.ReadIntAsync(address).ConfigureAwait(true);
        return w;
    }

    private void CloseNoticeUi()
    {
        void close()
        {
            try
            {
                if (_noticeForm is null || _noticeForm.IsDisposed)
                    return;

                _noticeForm.CloseFromProgram();
                _noticeForm = null;
            }
            catch
            {
                // ignore
            }
        }

        if (InvokeRequired)
            BeginInvoke(close);
        else
            close();
    }

    private Dictionary<long, string> BuildMapFromGrid()
    {
        var map = new Dictionary<long, string>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow)
                continue;

            string codeText = Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            string msg = Convert.ToString(row.Cells[1].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            if (!long.TryParse(codeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long code))
                continue;

            if (string.IsNullOrWhiteSpace(msg))
                continue;

            map[code] = msg;
        }

        return map;
    }

    private ManualOperationPromptDataKind GetSelectedKind() =>
        _cmbDataKind.SelectedIndex == 1 ? ManualOperationPromptDataKind.DWord : ManualOperationPromptDataKind.Word;

    private void LoadDefaultConfig()
    {
        if (!File.Exists(_defaultConfigPath))
            return;

        try
        {
            string json = File.ReadAllText(_defaultConfigPath);
            var root = JsonSerializer.Deserialize<ManualOperationPromptConfigRoot>(json);
            if (root is null)
                return;

            ApplyRoot(root, persist: false);
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"加载手动操作提示配置失败: {ex.Message}");
        }
    }

    private void LoadFromSelectedFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择手动操作提示配置文件",
            Filter = "JSON文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            InitialDirectory = AppContext.BaseDirectory
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            string json = File.ReadAllText(dialog.FileName);
            var root = JsonSerializer.Deserialize<ManualOperationPromptConfigRoot>(json);
            if (root is null)
            {
                MessageBox.Show(this, "JSON 解析失败。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            ApplyRoot(root, persist: false);
            SaveDefaultConfig();
            LogAction?.Invoke($"已加载手动操作提示配置: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"加载失败: {ex.Message}", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ApplyRoot(ManualOperationPromptConfigRoot root, bool persist)
    {
        _suspendAutoSave = true;
        _txtName.Text = root.VariableName ?? string.Empty;
        _txtAddress.Text = root.Address ?? string.Empty;

        if (Enum.TryParse(root.DataKind, ignoreCase: true, out ManualOperationPromptDataKind k))
            _cmbDataKind.SelectedIndex = k == ManualOperationPromptDataKind.DWord ? 1 : 0;
        else
            _cmbDataKind.SelectedIndex = 0;

        _grid.Rows.Clear();
        foreach (ManualOperationPromptEntry e in root.Entries.OrderBy(x => x.Code))
            _grid.Rows.Add(e.Code.ToString(CultureInfo.InvariantCulture), e.Message);

        _suspendAutoSave = false;
        if (persist)
            SaveDefaultConfig();
    }

    private void SaveDefaultConfig()
    {
        if (_suspendAutoSave)
            return;

        try
        {
            var root = new ManualOperationPromptConfigRoot
            {
                VariableName = _txtName.Text.Trim(),
                Address = _txtAddress.Text.Trim(),
                DataKind = GetSelectedKind().ToString(),
                Entries = []
            };

            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.IsNewRow)
                    continue;

                string codeText = Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                string msg = Convert.ToString(row.Cells[1].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                if (!long.TryParse(codeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long code))
                    continue;

                if (string.IsNullOrWhiteSpace(msg))
                    continue;

                root.Entries.Add(new ManualOperationPromptEntry { Code = code, Message = msg });
            }

            string json = JsonSerializer.Serialize(root, JsonFileOptions);
            File.WriteAllText(_defaultConfigPath, json);
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"保存手动操作提示配置失败: {ex.Message}");
        }
    }

    private sealed class ManualOperationPromptNoticeForm : Form
    {
        private readonly Label _titleLabel = new()
        {
            Dock = DockStyle.Top,
            Height = 40,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(16, 10, 16, 0),
            Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold),
            BackColor = Color.FromArgb(211, 47, 47),
            ForeColor = Color.White
        };

        private readonly Label _bodyLabel = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 16, 20, 12),
            Font = new Font("Microsoft YaHei UI", 11f),
            ForeColor = Color.FromArgb(62, 39, 35)
        };

        private readonly Button _btnOk = new()
        {
            Text = "知道了",
            Dock = DockStyle.Bottom,
            Height = 44,
            DialogResult = DialogResult.OK
        };

        internal bool ClosedByProgram { get; private set; }

        public ManualOperationPromptNoticeForm()
        {
            Text = "手动操作提示";
            Width = 520;
            Height = 260;
            MinimumSize = new Size(440, 200);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(255, 248, 225);
            Controls.Add(_bodyLabel);
            Controls.Add(_btnOk);
            Controls.Add(_titleLabel);
            AcceptButton = _btnOk;
            _btnOk.Click += (_, _) => Close();
        }

        internal void CloseFromProgram()
        {
            ClosedByProgram = true;
            try
            {
                if (!IsDisposed)
                    Close();
            }
            finally
            {
                ClosedByProgram = false;
            }
        }

        public void SetContent(string titleSuffix, string body)
        {
            string t = string.IsNullOrWhiteSpace(titleSuffix) ? "手动操作提示" : $"手动操作提示 — {titleSuffix}";
            if (Text != t)
                Text = t;
            _titleLabel.Text = "提示";
            if (_bodyLabel.Text != body)
                _bodyLabel.Text = body;
        }

    }
}
