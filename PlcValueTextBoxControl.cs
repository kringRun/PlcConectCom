using System.ComponentModel;
using System.Globalization;

namespace PlcConectCom.WinFormsTest;

/// <summary>PLC 绑定数据类型（读/写）。</summary>
public enum PlcBindableValueType
{
    Bool,
    Int,
    Double,
    String
}

/// <summary>显示只读；输入可编辑并在右侧显示绿色勾号写入按钮。</summary>
public enum PlcValueTextBoxMode
{
    Display,
    Input
}

/// <summary>
/// 文本框绑定 PLC：支持 bool / int / double / string；
/// 可设置名称；显示模式只读，输入模式右侧约 30px 绿色勾号按钮将当前文本写入 PLC。
/// </summary>
public sealed class PlcValueTextBoxControl : UserControl
{
    private const int TitleColumnWidth = 120;
    private const int UpdateButtonColumnWidth = 30;

    private readonly TableLayoutPanel _rootTable = new()
    {
        Dock = DockStyle.Fill,
        ColumnCount = 3,
        RowCount = 1,
        Padding = Padding.Empty,
        Margin = Padding.Empty
    };

    private readonly Label _lblTitle = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true
    };

    private readonly TextBox _txtValue = new()
    {
        Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.FixedSingle
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
    private bool _isEditing;
    private bool _isRefreshingFromPlc;
    private bool _autoReadEnabled = true;
    private bool _pendingRefresh;

    public PlcValueTextBoxControl()
    {
        _autoReadTimer.Interval = 500;
        BuildUi();
        BindEvents();
        ApplyMode();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
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
        set => _lblTitle.Text = value ?? string.Empty;
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
    public PlcBindableValueType DataType { get; set; } = PlcBindableValueType.Int;

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

    /// <summary>从 PLC 读取并刷新文本框显示。</summary>
    public async Task RefreshFromPlcAsync()
    {
        if (_isRefreshingFromPlc)
            return;

        // 输入模式下，用户正在编辑时不覆盖输入框内容。
        if (_controlMode == PlcValueTextBoxMode.Input && _isEditing)
            return;

        _isRefreshingFromPlc = true;
        try
        {
        if (string.IsNullOrWhiteSpace(PlcAddress))
        {
            _txtValue.Text = string.Empty;
            return;
        }

        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is null || !plc.IsConnected)
        {
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
                    _txtValue.Text = s.ToString(CultureInfo.InvariantCulture);
                    break;
                case PlcBindableValueType.Double:
                    double d = await plc.ReadDoubleAsync(addr);
                    _txtValue.Text = d.ToString("0.###", CultureInfo.InvariantCulture);
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
                    if (!short.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out short sv)
                        && !short.TryParse(raw, out sv))
                    {
                        LogAction?.Invoke($"INT 写入格式错误: {raw}");
                        return false;
                    }

                    await plc.WriteIntAsync(addr, sv);
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
                case PlcBindableValueType.String:
                    int max = Math.Max(1, StringLength);
                    string w = raw.Length > max ? raw[..max] : raw;
                    await plc.WriteStringAsync(addr, w);
                    break;
                default:
                    return false;
            }

            LogAction?.Invoke($"PLC文本框写入成功 [{addr}] <= {raw}");
            _isEditing = false;
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

    private void BuildUi()
    {
        Height = 30;
        MinimumSize = new Size(200, 28);

        _rootTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, TitleColumnWidth));
        _rootTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _rootTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0f));
        _rootTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        _rootTable.Controls.Add(_lblTitle, 0, 0);
        _rootTable.Controls.Add(_txtValue, 1, 0);
        _rootTable.Controls.Add(_btnWrite, 2, 0);

        Controls.Add(_rootTable);

        _btnWrite.FlatAppearance.BorderSize = 0;
        _btnWrite.FlatAppearance.MouseOverBackColor = Color.FromArgb(240, 252, 245);
        _btnWrite.FlatAppearance.MouseDownBackColor = Color.FromArgb(220, 248, 230);

        _toolTip.SetToolTip(_btnWrite, "将当前文本写入 PLC");
    }

    private void BindEvents()
    {
        _btnWrite.Click += async (_, _) => await WriteToPlcAsync();
        _autoReadTimer.Tick += async (_, _) => await RefreshFromPlcAsync();
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
        _txtValue.BackColor = input ? SystemColors.Window : SystemColors.Control;
        _btnWrite.Visible = input;
        if (!input)
            _isEditing = false;

        float w = input ? UpdateButtonColumnWidth : 0f;
        _rootTable.ColumnStyles[2] = new ColumnStyle(SizeType.Absolute, w);
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
}
