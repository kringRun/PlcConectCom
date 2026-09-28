using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PlcConectCom.WinFormsTest;

/// <summary>PLC 绑定数据类型（读/写）。</summary>
public enum PlcBindableValueType
{
    Bool,
    Int,
    Double,
    Dword,
    String
}

/// <summary>显示只读；输入可编辑并在右侧显示绿色勾号写入按钮。</summary>
public enum PlcValueTextBoxMode
{
    Display,
    Input
}

/// <summary>
/// 文本框绑定 PLC：支持 bool / int / double / dword / string；
/// 可设置名称；显示模式只读，输入模式右侧约 30px 绿色勾号按钮将当前文本写入 PLC。
/// </summary>
public sealed class PlcValueTextBoxControl : UserControl
{
    private sealed class IntMappingItem
    {
        public IntMappingItem(int value, string label)
        {
            Value = value;
            Label = label ?? string.Empty;
        }

        public int Value { get; }
        public string Label { get; }

        public override string ToString() => Label;
    }

    /// <summary>解析「0:无 1:A串 2:B串」形式的对照串。</summary>
    private static IReadOnlyList<(int Value, string Label)> ParseIntMappingDescription(string? spec)
    {
        var list = new List<(int, string)>();
        if (string.IsNullOrWhiteSpace(spec))
            return list;

        foreach (Match m in IntMappingPairRegex.Matches(spec.Trim()))
        {
            if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                continue;
            string label = m.Groups[2].Value.Trim();
            if (label.Length > 0)
                list.Add((v, label));
        }

        return list;
    }

    private static readonly Regex IntMappingPairRegex = new(
        @"(\d+)\s*:\s*(.+?)(?=\s+\d+\s*:|$)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private const int TitleColumnWidth = 120;
    private const int UpdateButtonColumnWidth = 30;
    private const int TitleColumnPadding = 12;
    private const int ValueMinimumWidth = 100;

    private readonly TableLayoutPanel _rootTable = new()
    {
        Dock = DockStyle.Fill,
        ColumnCount = 4,
        RowCount = 1,
        Padding = Padding.Empty,
        Margin = Padding.Empty
    };

    private readonly Label _lblTitle = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = false
    };

    private readonly Panel _valueHost = new()
    {
        Dock = DockStyle.Fill,
        Margin = Padding.Empty,
        Padding = Padding.Empty
    };

    private readonly TextBox _txtValue = new()
    {
        Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.FixedSingle
    };

    private readonly ComboBox _cmbValue = new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList,
        Visible = false,
        IntegralHeight = false
    };

    private readonly Button _btnWrite = new()
    {
        Dock = DockStyle.Fill,
        Text = "\u2713",
        FlatStyle = FlatStyle.Flat,
        UseVisualStyleBackColor = false,
        Font = new Font("Segoe UI Symbol", 11f, FontStyle.Bold),
        ForeColor = Color.FromArgb(46, 160, 67),
        BackColor = Color.White,
        Cursor = Cursors.Hand,
        TabStop = false
    };

    private readonly ToolTip _toolTip = new();
    private readonly System.Windows.Forms.Timer _autoReadTimer = new();

    private PlcValueTextBoxMode _controlMode = PlcValueTextBoxMode.Display;
    private PlcBindableValueType _dataType = PlcBindableValueType.Int;
    private bool _useIntComboMapping;
    private string _intValueMappingDescription = string.Empty;
    /// <summary>Int 下拉由用户改过选项、尚未成功写入 PLC 时，禁止定时刷新覆盖选中项。</summary>
    private bool _comboUserPendingWrite;
    private bool _comboApplyingFromPlc;
    private bool _isEditing;
    private bool _isRefreshingFromPlc;
    private bool _autoReadEnabled = true;
    private bool _pendingRefresh;
    private int _displayOffsetY = -30;
    private int _lastAppliedDisplayOffsetY;
    /// <summary>主页面扁平风格：与「功能按钮」区控件保持一致（无边框、浅灰底、粗体字体）。</summary>
    private bool _flatDisplayStyle;

    public PlcValueTextBoxControl()
    {
        _autoReadTimer.Interval = 500;
        BuildUi();
        BindEvents();
        SyncIntComboPresentation();
        ApplyMode();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDisplayOffset();
        UpdateAutoReadTimerRunning();
        if (_pendingRefresh)
        {
            _pendingRefresh = false;
            BeginInvoke(new Action(() => _ = RefreshFromPlcAsync()));
        }
    }

    /// <summary>左侧显示的名称（标题）。</summary>
    [Browsable(true)]
    [Category("Appearance")]
    [DefaultValue("")]
    [DisplayName("名称")]
    [Description("显示在文本框左侧的名称。")]
    public string Title
    {
        get => _lblTitle.Text;
        set
        {
            _lblTitle.Text = value ?? string.Empty;
            UpdateTitleLayout();
        }
    }

    /// <summary>PLC 地址。</summary>
    [Browsable(true)]
    [Category("Data")]
    [DefaultValue("")]
    public string PlcAddress { get; set; } = string.Empty;

    /// <summary>绑定数据类型。</summary>
    [Browsable(true)]
    [Category("Data")]
    [DefaultValue(PlcBindableValueType.Int)]
    public PlcBindableValueType DataType
    {
        get => _dataType;
        set
        {
            if (_dataType == value)
                return;
            _dataType = value;
            SyncIntComboPresentation();
        }
    }

    /// <summary>数据类型为 Int 且填写了对照说明时：用下拉框显示数值对应的文字。</summary>
    [Browsable(true)]
    [Category("Behavior")]
    [DefaultValue(false)]
    public bool UseIntComboMapping
    {
        get => _useIntComboMapping;
        set
        {
            if (_useIntComboMapping == value)
                return;
            _useIntComboMapping = value;
            SyncIntComboPresentation();
        }
    }

    /// <summary>形如「0:无 1:A串 2:B串」；数字与 PLC Int 一致。</summary>
    [Browsable(true)]
    [Category("Data")]
    [DefaultValue("")]
    public string IntValueMappingDescription
    {
        get => _intValueMappingDescription;
        set
        {
            string v = value ?? string.Empty;
            if (_intValueMappingDescription == v)
                return;
            _intValueMappingDescription = v;
            SyncIntComboPresentation();
        }
    }

    /// <summary>STRING 类型读写字节长度。</summary>
    [Browsable(true)]
    [Category("Data")]
    [DefaultValue(64)]
    public int StringLength { get; set; } = 64;

    /// <summary>显示：只读；输入：可编辑并显示右侧写入按钮。</summary>
    [Browsable(true)]
    [Category("Behavior")]
    [DefaultValue(PlcValueTextBoxMode.Display)]
    [DisplayName("控件模式")]
    [Description("显示：只读；输入：可编辑，右侧显示写入 PLC 按钮。")]
    public PlcValueTextBoxMode ControlMode
    {
        get => _controlMode;
        set
        {
            _controlMode = value;
            ApplyMode();
        }
    }

    public Func<global::PlcConectCom.PlcCommunicator?>? PlcProvider { get; set; }

    public global::PlcConectCom.PlcCommunicator? PlcInstance { get; set; }

    public Action<string>? LogAction { get; set; }

    [Browsable(true)]
    [Category("Behavior")]
    [DefaultValue(true)]
    public bool AutoReadEnabled
    {
        get => _autoReadEnabled;
        set
        {
            if (_autoReadEnabled == value)
                return;
            _autoReadEnabled = value;
            UpdateAutoReadTimerRunning();
        }
    }

    [Browsable(true)]
    [Category("Behavior")]
    [DefaultValue(500)]
    public int AutoReadIntervalMs
    {
        get => _autoReadTimer.Interval;
        set => _autoReadTimer.Interval = Math.Clamp(value, 100, 60_000);
    }

    /// <summary>控件显示区域垂直偏移（像素，负数上移，正数下移）。</summary>
    [Browsable(true)]
    [Category("Layout")]
    [DefaultValue(-30)]
    [DisplayName("垂直偏移")]
    public int DisplayOffsetY
    {
        get => _displayOffsetY;
        set
        {
            if (_displayOffsetY == value)
                return;

            _displayOffsetY = value;
            ApplyDisplayOffset();
        }
    }

    /// <summary>从 PLC 读取并刷新文本框显示。</summary>
    public async Task RefreshFromPlcAsync()
    {
        if (_isRefreshingFromPlc)
            return;

        // 输入模式下，用户正在编辑时不覆盖输入框 / 下拉框内容。
        if (_controlMode == PlcValueTextBoxMode.Input && _isEditing)
            return;

        // Int 映射下拉：用户已改选尚未写入时，禁止轮询读 PLC 把选中项刷回寄存器旧值。
        if (_controlMode == PlcValueTextBoxMode.Input && UseIntComboUi() && _comboUserPendingWrite)
            return;

        _isRefreshingFromPlc = true;
        try
        {
        if (string.IsNullOrWhiteSpace(PlcAddress))
        {
            _txtValue.Text = string.Empty;
            _cmbValue.SelectedIndex = -1;
            return;
        }

        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is null || !plc.IsConnected)
        {
            if (UseIntComboUi())
                _cmbValue.SelectedIndex = -1;
            else
                _txtValue.Text = "--";
            return;
        }

        string addr = PlcAddress.Trim();
        try
        {
            switch (DataType)
            {
                case PlcBindableValueType.Bool:
                    bool b = await plc.ReadBoolAsync(addr);
                    _txtValue.Text = b.ToString(CultureInfo.InvariantCulture);
                    break;
                case PlcBindableValueType.Int:
                    short s = await plc.ReadIntAsync(addr);
                    if (UseIntComboUi())
                        ApplyPlcIntToCombo(s);
                    else
                        _txtValue.Text = s.ToString(CultureInfo.InvariantCulture);
                    break;
                case PlcBindableValueType.Double:
                    double d = await plc.ReadDoubleAsync(addr);
                    _txtValue.Text = d.ToString("0.###", CultureInfo.InvariantCulture);
                    break;
                case PlcBindableValueType.Dword:
                    uint dw = await plc.ReadDWordAsync(addr);
                    _txtValue.Text = dw.ToString(CultureInfo.InvariantCulture);
                    break;
                case PlcBindableValueType.String:
                    int len = Math.Max(1, StringLength);
                    string str = await plc.ReadStringAsync(addr, len);
                    _txtValue.Text = str;
                    break;
                default:
                    _txtValue.Text = string.Empty;
                    break;
            }
        }
        catch (Exception ex)
        {
            if (UseIntComboUi())
                _cmbValue.SelectedIndex = -1;
            else
                _txtValue.Text = "ERR";
            LogAction?.Invoke($"PLC文本框读取失败 [{addr}] {DataType}: {ex.Message}");
        }
        }
        finally
        {
            _isRefreshingFromPlc = false;
        }
    }

    /// <summary>将当前文本框内容按 <see cref="DataType"/> 写入 PLC（输入模式使用）。</summary>
    public async Task<bool> WriteToPlcAsync()
    {
        if (string.IsNullOrWhiteSpace(PlcAddress))
        {
            LogAction?.Invoke("PLC文本框未配置地址。");
            return false;
        }

        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is null || !plc.IsConnected)
        {
            LogAction?.Invoke("请先连接 PLC。");
            return false;
        }

        string addr = PlcAddress.Trim();
        string raw = _txtValue.Text?.Trim() ?? string.Empty;

        try
        {
            switch (DataType)
            {
                case PlcBindableValueType.Bool:
                    if (!TryParseBoolFlexible(raw, out bool bv))
                    {
                        LogAction?.Invoke($"BOOL 写入格式错误（应为 true/false/1/0）: {raw}");
                        return false;
                    }

                    await plc.WriteBoolAsync(addr, bv);
                    break;
                case PlcBindableValueType.Int:
                    if (UseIntComboUi())
                    {
                        if (_cmbValue.SelectedItem is not IntMappingItem mi)
                        {
                            LogAction?.Invoke("Int 映射模式下请先在下拉框中选择一项。");
                            return false;
                        }

                        if (mi.Value < short.MinValue || mi.Value > short.MaxValue)
                        {
                            LogAction?.Invoke($"映射数值超出 INT 范围: {mi.Value}");
                            return false;
                        }

                        await plc.WriteIntAsync(addr, (short)mi.Value);
                    }
                    else
                    {
                        if (!short.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out short sv)
                            && !short.TryParse(raw, out sv))
                        {
                            LogAction?.Invoke($"INT 写入格式错误: {raw}");
                            return false;
                        }

                        await plc.WriteIntAsync(addr, sv);
                    }

                    break;
                case PlcBindableValueType.Double:
                    if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv)
                        && !double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out dv))
                    {
                        LogAction?.Invoke($"DOUBLE 写入格式错误: {raw}");
                        return false;
                    }

                    await plc.WriteDoubleAsync(addr, dv);
                    break;
                case PlcBindableValueType.Dword:
                    if (!uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint uv)
                        && !uint.TryParse(raw, out uv))
                    {
                        LogAction?.Invoke($"DWORD 写入格式错误: {raw}");
                        return false;
                    }

                    await plc.WriteDWordAsync(addr, uv);
                    break;
                case PlcBindableValueType.String:
                    int max = Math.Max(1, StringLength);
                    string w = raw.Length > max ? raw[..max] : raw;
                    await plc.WriteStringAsync(addr, w);
                    break;
                default:
                    return false;
            }

            string writtenDesc = raw;
            if (DataType == PlcBindableValueType.Int && UseIntComboUi() && _cmbValue.SelectedItem is IntMappingItem wmi)
                writtenDesc = $"{wmi.Value} ({wmi.Label})";
            LogAction?.Invoke($"PLC文本框写入成功 [{addr}] <= {writtenDesc}");
            _isEditing = false;
            _comboUserPendingWrite = false;
            await RefreshFromPlcAsync();
            return true;
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"PLC文本框写入失败 [{addr}]: {ex.Message}");
            return false;
        }
    }

    public void ApplyBinding(string address, PlcBindableValueType dataType, string? title = null, int? stringLength = null)
    {
        PlcAddress = address.Trim();
        DataType = dataType;
        if (stringLength.HasValue)
            StringLength = Math.Max(1, stringLength.Value);

        if (!string.IsNullOrWhiteSpace(title))
            Title = title.Trim();

        RequestRefresh();
    }

    private static bool TryParseBoolFlexible(string text, out bool value)
    {
        if (bool.TryParse(text, out value))
            return true;
        if (text == "1")
        {
            value = true;
            return true;
        }

        if (text == "0")
        {
            value = false;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 应用主页面「数值显示」区的统一风格：与「功能按钮」区按钮同字体（YaHei 10F×scale Bold）、
    /// 同高度（基准 35）、无边框扁平外观；只读态用与按钮未连接态一致的浅灰底。
    /// 宽度以 <paramref name="minWidth"/> 为下限，并按标题实际宽度自适应加宽，
    /// 保证长标题（如「当前组件状态」）不截断、数值列仍保有最小可读宽度。
    /// </summary>
    public void ApplyMainDisplayStyle(int minWidth, int height, float fontScale)
    {
        _flatDisplayStyle = true;
        Font = new Font("Microsoft YaHei UI", Math.Max(7F, 10F * fontScale), FontStyle.Bold);

        // 标题完整宽度 + 数值列最小宽度（输入模式再加写入按钮列），作为自适应下限
        string title = _lblTitle.Text ?? string.Empty;
        int titleNeed = title.Length == 0
            ? 0
            : TextRenderer.MeasureText(title, Font).Width + TitleColumnPadding;
        int buttonWidth = _controlMode == PlcValueTextBoxMode.Input ? UpdateButtonColumnWidth : 0;
        int needWidth = titleNeed + ValueMinimumWidth + buttonWidth + 2;
        int width = Math.Max(Math.Max(1, minWidth), needWidth);

        MinimumSize = new Size(width, Math.Max(24, height));
        Size = MinimumSize;
        ApplyMode();
        UpdateTitleLayout();
    }

    private void BuildUi()
    {
        Height = 30;
        MinimumSize = new Size(200, 28);

        _rootTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, TitleColumnWidth));
        _rootTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        _rootTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0f));
        _rootTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        _rootTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        _valueHost.Controls.Add(_txtValue);
        _valueHost.Controls.Add(_cmbValue);

        _rootTable.Controls.Add(_lblTitle, 0, 0);
        _rootTable.Controls.Add(_valueHost, 1, 0);
        _rootTable.Controls.Add(_btnWrite, 2, 0);

        Controls.Add(_rootTable);

        _btnWrite.FlatAppearance.BorderSize = 0;
        _btnWrite.FlatAppearance.MouseOverBackColor = Color.FromArgb(240, 252, 245);
        _btnWrite.FlatAppearance.MouseDownBackColor = Color.FromArgb(220, 248, 230);

        _toolTip.SetToolTip(_btnWrite, "将当前文本写入 PLC");
        UpdateTitleLayout();
    }

    private void BindEvents()
    {
        _btnWrite.Click += async (_, _) => await WriteToPlcAsync();
        _autoReadTimer.Tick += async (_, _) => await RefreshFromPlcAsync();
        SizeChanged += (_, _) => UpdateTitleLayout();
        FontChanged += (_, _) =>
        {
            UpdateTitleLayout();
            UpdateComboDropDownWidth(); // 字体变化（如主页面统一改粗体）后下拉宽度需按新字体重算
        };
        _cmbValue.DropDown += (_, _) =>
        {
            if (_controlMode == PlcValueTextBoxMode.Input)
                _isEditing = true;
        };
        _cmbValue.DropDownClosed += (_, _) =>
        {
            if (_controlMode == PlcValueTextBoxMode.Input)
                _isEditing = false;
        };
        _cmbValue.SelectedIndexChanged += OnComboIntMappingSelectedIndexChanged;
        _cmbValue.Leave += (_, _) => ScheduleClearComboPendingIfFocusLeftControl();
        _btnWrite.Leave += (_, _) => ScheduleClearComboPendingIfFocusLeftControl();
        _txtValue.Enter += (_, _) =>
        {
            if (_controlMode != PlcValueTextBoxMode.Input)
                return;
            _isEditing = true;
            _txtValue.SelectAll();
        };
        _txtValue.Leave += (_, _) =>
        {
            if (_controlMode != PlcValueTextBoxMode.Input)
                return;
            _isEditing = false;
        };
        _txtValue.MouseDown += (_, _) =>
        {
            if (_controlMode != PlcValueTextBoxMode.Input)
                return;
            _isEditing = true;
        };
    }

    private void ApplyMode()
    {
        bool input = _controlMode == PlcValueTextBoxMode.Input;
        _txtValue.ReadOnly = !input;
        if (_flatDisplayStyle && !input)
        {
            // 扁平风格：无边框浅灰底，与「功能按钮」区按钮未连接态配色一致
            _txtValue.BorderStyle = BorderStyle.None;
            _txtValue.BackColor = Color.FromArgb(236, 239, 242);
            _txtValue.ForeColor = Color.FromArgb(40, 50, 65);
        }
        else
        {
            _txtValue.BorderStyle = BorderStyle.FixedSingle;
            _txtValue.BackColor = input ? SystemColors.Window : SystemColors.Control;
            _txtValue.ForeColor = SystemColors.ControlText;
        }

        _btnWrite.Visible = input;
        if (!input)
        {
            _isEditing = false;
            _comboUserPendingWrite = false;
        }

        float w = input ? UpdateButtonColumnWidth : 0f;
        _rootTable.ColumnStyles[2] = new ColumnStyle(SizeType.Absolute, w);
        // 数值列占满剩余宽度（原第 4 列空占 50% 会把数值框挤到一半，导致下拉框/文本框显示不全）；
        // 输入模式下写入按钮紧贴数值框右侧
        _rootTable.ColumnStyles[1] = new ColumnStyle(SizeType.Percent, 100f);
        _rootTable.ColumnStyles[3] = new ColumnStyle(SizeType.Percent, 0f);
        UpdateTitleLayout();
    }

    private void UpdateTitleLayout()
    {
        if (_rootTable.ColumnStyles.Count < 3)
            return;

        string title = _lblTitle.Text ?? string.Empty;
        int measuredTitleWidth = string.IsNullOrEmpty(title)
            ? TitleColumnWidth
            : TextRenderer.MeasureText(title, _lblTitle.Font).Width + TitleColumnPadding;

        int buttonWidth = _btnWrite.Visible ? UpdateButtonColumnWidth : 0;
        int maxTitleWidth = Math.Max(TitleColumnWidth, Width - ValueMinimumWidth - buttonWidth);
        // 窄控件（如主页面 180×35 扁平风格）时允许标题列低于 120px，保证数值列仍有可读宽度
        int minTitleWidth = Math.Min(TitleColumnWidth, maxTitleWidth);
        int titleWidth = Math.Clamp(measuredTitleWidth, minTitleWidth, maxTitleWidth);

        _rootTable.ColumnStyles[0] = new ColumnStyle(SizeType.Absolute, titleWidth);
        MinimumSize = new Size(titleWidth + ValueMinimumWidth + buttonWidth, MinimumSize.Height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _autoReadTimer.Stop();
            _autoReadTimer.Dispose();
            _toolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnLeave(EventArgs e)
    {
        base.OnLeave(e);
        ScheduleClearComboPendingIfFocusLeftControl();
    }

    private void OnComboIntMappingSelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_comboApplyingFromPlc || _controlMode != PlcValueTextBoxMode.Input || !UseIntComboUi())
            return;
        _comboUserPendingWrite = true;
    }

    private void ScheduleClearComboPendingIfFocusLeftControl()
    {
        if (!IsHandleCreated || DesignMode)
            return;
        BeginInvoke(ClearComboPendingIfFocusOutside);
    }

    private void ClearComboPendingIfFocusOutside()
    {
        if (IsDisposed || !UseIntComboUi() || _controlMode != PlcValueTextBoxMode.Input)
            return;
        if (!ContainsFocus)
            _comboUserPendingWrite = false;
    }

    private void UpdateAutoReadTimerRunning()
    {
        if (DesignMode || LicenseManager.UsageMode == LicenseUsageMode.Designtime)
        {
            _autoReadTimer.Stop();
            return;
        }

        if (_autoReadEnabled && IsHandleCreated)
            _autoReadTimer.Start();
        else
            _autoReadTimer.Stop();
    }

    private void RequestRefresh()
    {
        if (DesignMode || LicenseManager.UsageMode == LicenseUsageMode.Designtime)
            return;

        if (!IsHandleCreated)
        {
            _pendingRefresh = true;
            return;
        }

        if (InvokeRequired)
            BeginInvoke(new Action(() => _ = RefreshFromPlcAsync()));
        else
            _ = RefreshFromPlcAsync();
    }

    private void ApplyDisplayOffset()
    {
        if (DesignMode || LicenseManager.UsageMode == LicenseUsageMode.Designtime || !IsHandleCreated)
            return;

        int delta = _displayOffsetY - _lastAppliedDisplayOffsetY;
        if (delta == 0)
            return;

        Top += delta;
        _lastAppliedDisplayOffsetY = _displayOffsetY;
    }

    private bool UseIntComboUi()
        => _dataType == PlcBindableValueType.Int && _useIntComboMapping
           && ParseIntMappingDescription(_intValueMappingDescription).Count > 0;

    private void SyncIntComboPresentation()
    {
        if (_valueHost.IsDisposed)
            return;

        _comboApplyingFromPlc = true;
        try
        {
            bool useCombo = UseIntComboUi();
            IReadOnlyList<(int Value, string Label)> pairs = ParseIntMappingDescription(_intValueMappingDescription);

            _comboUserPendingWrite = false;

            _cmbValue.BeginUpdate();
            try
            {
                _cmbValue.Items.Clear();
                if (useCombo)
                {
                    foreach ((int val, string label) in pairs)
                        _cmbValue.Items.Add(new IntMappingItem(val, label));
                }
            }
            finally
            {
                _cmbValue.EndUpdate();
            }

            _txtValue.Visible = !useCombo;
            _cmbValue.Visible = useCombo;
            _valueHost.PerformLayout();
        }
        finally
        {
            _comboApplyingFromPlc = false;
        }

        // 下拉列表宽度自适应最宽项：默认与控件同宽，长选项（如「取组件待料完成」）会被裁剪
        UpdateComboDropDownWidth();
    }

    /// <summary>按当前选项的最宽文本调整下拉列表宽度（含纵向滚动条与边距），不小于控件本身宽度。</summary>
    private void UpdateComboDropDownWidth()
    {
        if (_cmbValue.IsDisposed)
            return;

        int maxItemWidth = 0;
        foreach (object item in _cmbValue.Items)
        {
            if (item is IntMappingItem mapping)
                maxItemWidth = Math.Max(maxItemWidth, TextRenderer.MeasureText(mapping.Label, _cmbValue.Font).Width);
        }

        int dropDownWidth = maxItemWidth == 0
            ? _cmbValue.Width
            : Math.Max(_cmbValue.Width, maxItemWidth + SystemInformation.VerticalScrollBarWidth + 8);
        _cmbValue.DropDownWidth = dropDownWidth;
    }

    private void ApplyPlcIntToCombo(short plcValue)
    {
        _comboApplyingFromPlc = true;
        try
        {
            int found = -1;
            for (int i = 0; i < _cmbValue.Items.Count; i++)
            {
                if (_cmbValue.Items[i] is IntMappingItem it && it.Value == plcValue)
                {
                    found = i;
                    break;
                }
            }

            _cmbValue.SelectedIndex = found;
        }
        finally
        {
            _comboApplyingFromPlc = false;
        }
    }
}
