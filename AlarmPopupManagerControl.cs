using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;

namespace PlcConectCom.WinFormsTest;

/// <summary>报警弹窗组内的一个操作按钮。</summary>
public sealed class AlarmPopupButtonDefinition
{
    public string FullName { get; set; } = string.Empty;

    /// <summary>对话框上显示的短名称（一般为「按钮」前与弹窗名去重后的尾部）。</summary>
    public string DisplayName { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;
}

/// <summary>一条「弹窗」及其下属「按钮」行。</summary>
public sealed class AlarmPopupGroupDefinition
{
    public string TriggerName { get; set; } = string.Empty;

    public string TriggerAddress { get; set; } = string.Empty;

    public List<AlarmPopupButtonDefinition> Buttons { get; set; } = [];
}

/// <summary>从 Excel 导入行解析报警弹窗分组（按行顺序：名称末尾为「弹窗」开启新组，其后直到下一个末尾「弹窗」为止、名称末尾为「按钮」的行归入当前组）。</summary>
public static class AlarmPopupExcelParser
{
    /// <summary>名称去掉首尾空白后，最后两个字符为「弹窗」。</summary>
    public static bool IsPopupTriggerName(string name)
    {
        name = name.Trim();
        return name.Length >= 2 && name.EndsWith("弹窗", StringComparison.Ordinal);
    }

    /// <summary>名称去掉首尾空白后，最后两个字符为「按钮」。</summary>
    public static bool IsButtonActionName(string name)
    {
        name = name.Trim();
        return name.Length >= 2 && name.EndsWith("按钮", StringComparison.Ordinal);
    }

    public static List<AlarmPopupGroupDefinition> ParseGroups(IReadOnlyList<(string Name, string Address, string DataType)> rows)
    {
        var result = new List<AlarmPopupGroupDefinition>();
        AlarmPopupGroupDefinition? current = null;

        foreach ((string name, string address, string dataType) in rows)
        {
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(address))
                continue;

            if (!IsBoolType(dataType))
                continue;

            string addr = address.Trim();
            if (string.IsNullOrWhiteSpace(addr))
                continue;

            string nameTrim = name.Trim();
            if (string.IsNullOrWhiteSpace(nameTrim))
                continue;

            if (IsPopupTriggerName(nameTrim))
            {
                if (current is { TriggerAddress: not "" })
                    result.Add(current);

                current = new AlarmPopupGroupDefinition
                {
                    TriggerName = nameTrim,
                    TriggerAddress = addr,
                    Buttons = []
                };
                continue;
            }

            if (IsButtonActionName(nameTrim))
            {
                if (current is null)
                    continue;

                string display = ComputeButtonDisplayName(current.TriggerName, nameTrim);
                current.Buttons.Add(new AlarmPopupButtonDefinition
                {
                    FullName = nameTrim,
                    DisplayName = string.IsNullOrWhiteSpace(display) ? nameTrim : display,
                    Address = addr
                });
            }
        }

        if (current is { TriggerAddress: not "" })
            result.Add(current);

        return result;
    }

    private static bool IsBoolType(string dataTypeText)
    {
        string t = dataTypeText.Trim();
        return string.Equals(t, "BOOL", StringComparison.OrdinalIgnoreCase)
               || string.Equals(t, "BIT", StringComparison.OrdinalIgnoreCase);
    }

    public static string ComputeButtonDisplayName(string triggerName, string buttonFullName)
    {
        int idx = buttonFullName.LastIndexOf("按钮", StringComparison.Ordinal);
        if (idx < 0)
            return buttonFullName;

        string withoutBtn = buttonFullName[..idx].Trim();
        if (withoutBtn.Length == 0)
            return buttonFullName;

        int lcp = LongestCommonPrefixLength(triggerName, withoutBtn);
        string tail = lcp < withoutBtn.Length ? withoutBtn[lcp..].Trim() : withoutBtn;
        return string.IsNullOrWhiteSpace(tail) ? withoutBtn : tail;
    }

    private static int LongestCommonPrefixLength(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length);
        int i = 0;
        while (i < n && a[i] == b[i])
            i++;
        return i;
    }
}

/// <summary>系统配置：报警弹窗（PLC 位为 true 时非模态警告样式提示，位为 false 时关闭；任意按钮点击后关窗并再检测，若弹窗位仍为 true 会再打开）。</summary>
public sealed class AlarmPopupManagerControl : UserControl
{
    private const string DefaultConfigFileName = "AlarmPopup.config.json";
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
    private readonly Button _btnAutoMonitor = new() { Text = "自动监控:关", Width = 92 };
    private readonly System.Windows.Forms.Timer _monitorTimer = new() { Interval = 1000 };

    private readonly FlowLayoutPanel _actionsRow = new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        WrapContents = true,
        Padding = new Padding(0, 0, 0, 4)
    };

    private readonly string _defaultConfigPath;
    private readonly Dictionary<string, Form> _openAlarmForms = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingAlarmOpens = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _popupCheckGate = new(1, 1);
    private bool _suspendAutoSave;
    private bool _canAddDelete = true;
    private bool _canModify = true;
    private bool _editUnlocked;

    /// <summary>
    /// 构造报警弹窗管理控件。
    /// 不传 <paramref name="storageKey"/> 时使用默认配置文件 AlarmPopup.config.json（主 PLC）；
    /// 传入时使用 AlarmPopup.{storageKey}.config.json（子 PLC 每设备独立配置）。
    /// </summary>
    public AlarmPopupManagerControl(string? storageKey = null)
    {
        string fileName = string.IsNullOrWhiteSpace(storageKey)
            ? DefaultConfigFileName
            : $"AlarmPopup.{storageKey}.config.json";
        _defaultConfigPath = Path.Combine(AppContext.BaseDirectory, fileName);
        BuildUi();
        BindEvents();
        LoadDefaultConfig();
    }

    public Action<string>? LogAction { get; set; }

    public Func<global::PlcConectCom.PlcCommunicator?>? PlcProvider { get; set; }

    public Func<bool>? EditPermissionRequest { get; set; }

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

    public IReadOnlyList<AlarmPopupGroupDefinition> GetGroups() => BuildGroupsFromGrid();

    public void SetGroups(IReadOnlyList<AlarmPopupGroupDefinition> groups)
    {
        _suspendAutoSave = true;
        RebuildGridFromGroups(groups);
        _suspendAutoSave = false;
        SaveDefaultConfig();
    }

    private void BuildUi()
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "类型(弹窗/按钮)", FillWeight = 18 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "名称", FillWeight = 38 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "地址", FillWeight = 28 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "按钮显示名", FillWeight = 16 });

        _actionsRow.Controls.AddRange([_btnEditAuth, _btnAdd, _btnDelete, _btnLoad, _btnSave, _btnAutoMonitor]);

        Controls.Add(_grid);
        Controls.Add(_actionsRow);
    }

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
            _grid.Rows.Add("按钮", string.Empty, string.Empty, string.Empty);
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
        _monitorTimer.Tick += async (_, _) => await CheckPopupsAsync();

        _grid.CellValueChanged += (_, e) =>
        {
            if (_suspendAutoSave || !_editUnlocked)
                return;

            if (e.RowIndex >= 0 && e.ColumnIndex <= 3)
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
            string json = File.ReadAllText(_defaultConfigPath);
            var groups = JsonSerializer.Deserialize<List<AlarmPopupGroupDefinition>>(json);
            if (groups is null)
                return;

            _suspendAutoSave = true;
            RebuildGridFromGroups(groups);
            _suspendAutoSave = false;
        }
        catch (Exception ex)
        {
            _suspendAutoSave = false;
            LogAction?.Invoke($"加载报警弹窗配置失败: {ex.Message}");
        }
    }

    private void LoadFromSelectedFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择报警弹窗配置文件",
            Filter = "JSON文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            InitialDirectory = AppContext.BaseDirectory
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            string json = File.ReadAllText(dialog.FileName);
            var groups = JsonSerializer.Deserialize<List<AlarmPopupGroupDefinition>>(json);
            if (groups is null)
                return;

            SetGroups(groups);
            LogAction?.Invoke($"已加载报警弹窗配置: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"加载报警弹窗配置失败: {ex.Message}");
        }
    }

    private void SaveDefaultConfig()
    {
        if (_suspendAutoSave)
            return;

        try
        {
            var groups = BuildGroupsFromGrid();
            string json = JsonSerializer.Serialize(groups, JsonFileOptions);
            File.WriteAllText(_defaultConfigPath, json);
            LogAction?.Invoke("报警弹窗配置已保存到本地。");
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"保存报警弹窗配置失败: {ex.Message}");
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

    private static string GroupKey(string triggerAddress) =>
        triggerAddress.Trim();

    private async Task CheckPopupsAsync()
    {
        await _popupCheckGate.WaitAsync().ConfigureAwait(true);

        var plc = PlcProvider?.Invoke();
        if (plc is null || !plc.IsConnected)
        {
            _popupCheckGate.Release();
            return;
        }

        try
        {
            List<AlarmPopupGroupDefinition> groups = BuildGroupsFromGrid();

            // 弹窗触发位统一批量读取（同报警配置页：逐条读会因 Modbus 串行往返造成弹窗滞后）
            List<AlarmPopupGroupDefinition> validGroups = groups
                .Where(g => !string.IsNullOrWhiteSpace(g.TriggerAddress) && IsSupportedBoolAddress(g.TriggerAddress))
                .ToList();
            Dictionary<string, bool> triggerStates = await PlcBoolBatchReader.ReadBoolStatesAsync(
                plc, validGroups.Select(g => g.TriggerAddress!.Trim())).ConfigureAwait(true);

            foreach (AlarmPopupGroupDefinition g in validGroups)
            {
                string addr = g.TriggerAddress!.Trim();
                string key = GroupKey(addr);
                if (!triggerStates.TryGetValue(addr, out bool on))
                    continue;

                if (!on)
                {
                    _pendingAlarmOpens.Remove(key);
                    if (_openAlarmForms.TryGetValue(key, out Form? openForm))
                    {
                        void closeAlarm()
                        {
                            try
                            {
                                if (openForm.IsDisposed)
                                    return;
                                if (openForm is AlarmNoticeForm notice)
                                    notice.CloseFromManager();
                                else
                                    openForm.Close();
                            }
                            catch
                            {
                                // ignore
                            }
                        }

                        if (InvokeRequired)
                            BeginInvoke(closeAlarm);
                        else
                            closeAlarm();
                    }

                    continue;
                }

                if (_openAlarmForms.ContainsKey(key) || _pendingAlarmOpens.Contains(key))
                    continue;

                _pendingAlarmOpens.Add(key);
                AlarmPopupGroupDefinition groupCopy = g;

                void openAlarm()
                {
                    try
                    {
                        if (_openAlarmForms.ContainsKey(key))
                            return;

                        Form? owner = FindForm() ?? TopLevelControl as Form;
                        AlarmNoticeForm f = CreateAlarmNoticeForm(groupCopy, owner);
                        _openAlarmForms[key] = f;
                        f.FormClosed += (_, _) => _openAlarmForms.Remove(key);
                        if (owner is not null)
                            f.Show(owner);
                        else
                            f.Show();
                    }
                    finally
                    {
                        _pendingAlarmOpens.Remove(key);
                    }
                }

                if (InvokeRequired)
                    BeginInvoke(openAlarm);
                else
                    openAlarm();
            }
        }
        finally
        {
            _popupCheckGate.Release();
        }
    }

    private AlarmNoticeForm CreateAlarmNoticeForm(AlarmPopupGroupDefinition group, Form? owner)
    {
        Color warnRed = Color.FromArgb(183, 28, 28);
        Color warnHeader = Color.FromArgb(211, 47, 47);
        Color warnPaper = Color.FromArgb(255, 248, 225);
        Color warnPaperBorder = Color.FromArgb(255, 193, 7);
        Color warnText = Color.FromArgb(62, 39, 35);
        Color btnFace = Color.FromArgb(255, 167, 38);
        Color btnFacePressed = Color.FromArgb(255, 213, 160);

        var f = new AlarmNoticeForm
        {
            Text = "报警",
            Width = 580,
            Height = 280,
            MinimumSize = new Size(480, 220),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedSingle,
            MaximizeBox = false,
            MinimizeBox = false,
            ControlBox = false,
            ShowInTaskbar = false,
            Owner = owner,
            BackColor = warnPaper,
            Padding = new Padding(0)
        };

        Font headerFont = new("Microsoft YaHei UI", 15.75f, FontStyle.Bold, GraphicsUnit.Point);
        Font titleFont = new("Microsoft YaHei UI", 12f, FontStyle.Bold, GraphicsUnit.Point);
        Font bodyFont = new("Microsoft YaHei UI", 11.25f, FontStyle.Regular, GraphicsUnit.Point);
        Font btnFont = new("Microsoft YaHei UI", 13f, FontStyle.Bold, GraphicsUnit.Point);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = group.Buttons.Count > 0 ? 3 : 2,
            BackColor = warnPaper,
            Padding = new Padding(0)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        if (group.Buttons.Count > 0)
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112f));

        var header = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = warnHeader,
            Padding = new Padding(16, 0, 16, 0),
            Margin = new Padding(0)
        };
        var headerLbl = new Label
        {
            Text = "警告 — ",
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            Font = headerFont,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.Transparent,
            AutoSize = false
        };
        header.Controls.Add(headerLbl);

        var body = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = warnPaper,
            Padding = new Padding(20, 16, 20, 12),
            Margin = new Padding(2, 0, 2, 0)
        };
        body.Paint += (_, e) =>
        {
            using var pen = new Pen(warnPaperBorder, 2f);
            var r = body.ClientRectangle;
            r.Inflate(-1, -1);
            e.Graphics.DrawRectangle(pen, r);
        };
        var titleLbl = new Label
        {
            Text = "报警内容",
            AutoSize = true,
            ForeColor = warnRed,
            Font = titleFont,
            Location = new Point(0, 0)
        };
        var msgLbl = new Label
        {
            Text = group.Buttons.Count == 0
                ? group.TriggerName + Environment.NewLine + Environment.NewLine + "（无按钮配置：报警位复位为 false 后本窗自动关闭）"
                : group.TriggerName,
            AutoSize = false,
            ForeColor = warnText,
            Font = bodyFont,
            Location = new Point(0, 32),
            Size = new Size(200, 120),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };
        body.Controls.Add(msgLbl);
        body.Controls.Add(titleLbl);
        void LayoutBody(object? _, EventArgs __)
        {
            int padL = body.Padding.Left;
            int padT = body.Padding.Top;
            int padR = body.Padding.Right;
            int padB = body.Padding.Bottom;
            int w = Math.Max(60, body.ClientSize.Width - padL - padR);
            titleLbl.Location = new Point(padL, padT);
            int topMsg = titleLbl.Bottom + 8;
            int bottom = body.ClientSize.Height - padB;
            msgLbl.SetBounds(padL, topMsg, w, Math.Max(40, bottom - topMsg));
        }

        body.Resize += LayoutBody;
        body.HandleCreated += (_, _) => LayoutBody(null, EventArgs.Empty);

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(body, 0, 1);

        int buttonCount = group.Buttons.Count;

        if (buttonCount > 0)
        {
            var btnStrip = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = buttonCount,
                RowCount = 1,
                BackColor = Color.FromArgb(255, 236, 200),
                Padding = new Padding(16, 10, 16, 16),
                Margin = new Padding(0)
            };
            for (int c = 0; c < buttonCount; c++)
                btnStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / buttonCount));

            for (int i = 0; i < buttonCount; i++)
            {
                AlarmPopupButtonDefinition b = group.Buttons[i];
                var btn = new Button
                {
                    Text = string.IsNullOrWhiteSpace(b.DisplayName) ? b.FullName : b.DisplayName,
                    Dock = DockStyle.Fill,
                    Margin = new Padding(12, 6, 12, 6),
                    MinimumSize = new Size(140, 64),
                    Font = btnFont,
                    Cursor = Cursors.Hand,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = btnFace,
                    ForeColor = Color.FromArgb(55, 30, 10),
                    Tag = b
                };
                btn.FlatAppearance.BorderColor = Color.FromArgb(230, 81, 0);
                btn.FlatAppearance.BorderSize = 2;

                btn.Click += async (_, _) =>
                {
                    bool writeOk = false;
                    try
                    {
                        var plc = PlcProvider?.Invoke();
                        if (plc is { IsConnected: true })
                        {
                            await plc.WriteBoolAsync(b.Address.Trim(), true).ConfigureAwait(true);
                            LogAction?.Invoke($"报警弹窗「{group.TriggerName}」按钮「{btn.Text}」→ {b.Address} = TRUE");
                            writeOk = true;
                        }
                        else
                            LogAction?.Invoke($"报警弹窗「{group.TriggerName}」按钮「{btn.Text}」未写入：PLC 未连接");
                    }
                    catch (Exception ex)
                    {
                        LogAction?.Invoke($"报警弹窗按钮写入失败 {b.Address}: {ex.Message}");
                    }

                    if (!writeOk)
                        return;

                    btn.BackColor = btnFacePressed;
                    btn.Enabled = false;

                    try
                    {
                        if (f is AlarmNoticeForm notice && !f.IsDisposed)
                            notice.CloseFromManager();
                    }
                    catch
                    {
                        // ignore
                    }

                    await CheckPopupsAsync().ConfigureAwait(true);
                };

                btnStrip.Controls.Add(btn, i, 0);
            }

            root.Controls.Add(btnStrip, 0, 2);
        }

        f.Controls.Add(root);
        return f;
    }

    /// <summary>报警提示窗：默认拦截用户关闭，仅允许 <see cref="CloseFromManager"/> 关闭。</summary>
    private sealed class AlarmNoticeForm : Form
    {
        public void CloseFromManager()
        {
            AllowManagerClose = true;
            Close();
        }

        private bool AllowManagerClose { get; set; }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!AllowManagerClose)
                e.Cancel = true;
            base.OnFormClosing(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(Color.FromArgb(183, 28, 28), 3f);
            Rectangle r = ClientRectangle;
            r.Width -= 1;
            r.Height -= 1;
            e.Graphics.DrawRectangle(pen, r);
        }
    }

    private void RebuildGridFromGroups(IReadOnlyList<AlarmPopupGroupDefinition> groups)
    {
        _grid.Rows.Clear();
        foreach (AlarmPopupGroupDefinition g in groups)
        {
            _grid.Rows.Add("弹窗", g.TriggerName, g.TriggerAddress, string.Empty);
            foreach (AlarmPopupButtonDefinition b in g.Buttons)
                _grid.Rows.Add("按钮", b.FullName, b.Address, b.DisplayName);
        }
    }

    private List<AlarmPopupGroupDefinition> BuildGroupsFromGrid()
    {
        var list = new List<AlarmPopupGroupDefinition>();
        AlarmPopupGroupDefinition? current = null;

        foreach (DataGridViewRow row in _grid.Rows)
        {
            string kind = Convert.ToString(row.Cells[0].Value)?.Trim() ?? string.Empty;
            string name = Convert.ToString(row.Cells[1].Value)?.Trim() ?? string.Empty;
            string address = Convert.ToString(row.Cells[2].Value)?.Trim() ?? string.Empty;
            string display = Convert.ToString(row.Cells[3].Value)?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(kind) && string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(address))
                continue;

            if (kind.Contains("弹窗", StringComparison.Ordinal) || string.Equals(kind, "弹窗", StringComparison.OrdinalIgnoreCase))
            {
                if (current is { TriggerAddress: not "" })
                    list.Add(current);

                current = new AlarmPopupGroupDefinition
                {
                    TriggerName = name,
                    TriggerAddress = address,
                    Buttons = []
                };
                continue;
            }

            if (current is null)
                continue;

            if (kind.Contains("按钮", StringComparison.Ordinal) || string.Equals(kind, "按钮", StringComparison.OrdinalIgnoreCase))
            {
                string disp = string.IsNullOrWhiteSpace(display)
                    ? AlarmPopupExcelParser.ComputeButtonDisplayName(current.TriggerName, name)
                    : display;

                current.Buttons.Add(new AlarmPopupButtonDefinition
                {
                    FullName = name,
                    DisplayName = string.IsNullOrWhiteSpace(disp) ? name : disp,
                    Address = address
                });
            }
        }

        if (current is { TriggerAddress: not "" })
            list.Add(current);

        return list;
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
