using System.ComponentModel;
using System.Globalization;

namespace PlcConectCom.WinFormsTest;

// PLC 按钮绑定支持的数据类型
public enum PlcValueType
{
    BOOL,
    BYTE,
    INT,
    REAL,
    DWORD,
    STRING
}

// 单个 PLC 地址读写按钮控件（左键读、BOOL 点击切换、右键写）
public sealed class PlcBoundButton : UserControl
{
    private const int FixedButtonWidth = 280;
    private const int FixedButtonHeight = 44;

    private readonly Button _btnAction = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        AutoSize = false,
        UseVisualStyleBackColor = false,
        UseCompatibleTextRendering = true
    };

    /// <summary>标题文案，与 PLC 数据显示在同一按钮内（左右并排）。</summary>
    private string _text = string.Empty;

    /// <summary>标题与 PLC 数据显示在同一按钮范围内；控件尺寸固定，改标题不改变大小。</summary>
    [Browsable(true)]
    [Category("Appearance")]
    [DefaultValue("")]
    [Localizable(true)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
    [Description("标题文字，与 PLC 数据显示在同一按钮内（左右并排，中间竖线分隔）；BOOL 时以背景色区分开/关。")]
    public new string Text
    {
        get => _text;
        set
        {
            _text = value ?? string.Empty;
            UpdateButtonText();
        }
    }

    /// <summary>供设计器序列化：非空时才生成 InitializeComponent 中的 Text 赋值。</summary>
    private bool ShouldSerializeText() => !string.IsNullOrWhiteSpace(_text);

    /// <summary>供设计器“重置”及基类 <see cref="Control.ResetText"/>。</summary>
    public override void ResetText()
    {
        base.ResetText();
        _text = string.Empty;
        UpdateButtonText();
    }

    public string PlcAddress { get; set; } = string.Empty;

    public PlcValueType DataType { get; set; } = PlcValueType.INT;

    public int StringLength { get; set; } = 64;

    /// <summary>仅 BOOL 生效：点击后写入 true，延时后自动写回 false。</summary>
    [Browsable(true)]
    [Category("Behavior")]
    [DefaultValue(false)]
    public bool AutoReset { get; set; }

    /// <summary>自复位延时（毫秒），仅 BOOL 且 <see cref="AutoReset"/> 为 true 时生效。</summary>
    [Browsable(true)]
    [Category("Behavior")]
    [DefaultValue(100)]
    public int AutoResetDelayMs { get; set; } = 200;

    /// <summary>自复位目标值（仅 BOOL 且 <see cref="AutoReset"/> 为 true 时生效）。</summary>
    [Browsable(true)]
    [Category("Behavior")]
    [DefaultValue(false)]
    public bool AutoResetTargetValue { get; set; }

    public string CurrentValue { get; private set; } = string.Empty;

    public Func<global::PlcConectCom.PlcCommunicator?>? PlcProvider { get; set; }

    /// <summary>异常与提示输出到主界面底部日志区（由 Form1 绑定 <c>AppendLog</c>）。</summary>
    public Action<string>? LogAction { get; set; }

    public event EventHandler<MouseEventArgs>? ActionMouseUp;

    public PlcBoundButton()
    {
        BuildUi();
        BindEvents();
        UpdateButtonText();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // 部分设计器/序列化路径会把文案写在基类 Control.Text 上，未进入本控件的 new Text，运行前在此合并。
        TryImportCaptionFromBaseText();
        UpdateButtonText();
    }

    private void TryImportCaptionFromBaseText()
    {
        if (!string.IsNullOrWhiteSpace(_text))
            return;

        string fromBase = base.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(fromBase))
            return;

        _text = fromBase;
        base.Text = string.Empty;
    }

    public void RefreshConnectionState()
    {
        UpdateButtonText();
    }

    public static bool TryParseDataType(string? value, out PlcValueType dataType)
    {
        return Enum.TryParse(value, true, out dataType);
    }

    public void ApplyBinding(string address, PlcValueType dataType, int stringLength)
    {
        PlcAddress = address.Trim();
        DataType = dataType;
        StringLength = stringLength;
        UpdateButtonText();
    }

    /// <summary>从 PLC 读取并刷新显示。失败时写日志并返回 <c>null</c>（不抛异常）。</summary>
    public async Task<string?> ShowDataAsync()
    {
        try
        {
            var plc = GetConnectedPlc();
            object value = await ReadPlcValueAsync(plc);

            CurrentValue = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            UpdateButtonText();
            return CurrentValue;
        }
        catch (Exception ex)
        {
            LogOperationException("读取", ex);
            return null;
        }
    }

    /// <summary>写入 PLC。失败时写日志并返回 <c>false</c>（不抛异常）。</summary>
    public async Task<bool> SaveDataAsync(string value)
    {
        try
        {
            var plc = GetConnectedPlc();

            switch (DataType)
            {
                case PlcValueType.BOOL:
                    if (!bool.TryParse(value, out bool boolValue))
                    {
                        LogAction?.Invoke($"PLC绑定按钮写入 [{PlcAddress.Trim()}] BOOL: 值应为 true/false。");
                        return false;
                    }

                    await plc.WriteBoolAsync(PlcAddress, boolValue);
                    break;
                case PlcValueType.BYTE:
                    if (!byte.TryParse(value, out byte byteValue))
                    {
                        LogAction?.Invoke($"PLC绑定按钮写入 [{PlcAddress.Trim()}] BYTE: 格式错误。");
                        return false;
                    }

                    await plc.WriteByteAsync(PlcAddress, byteValue);
                    break;
                case PlcValueType.INT:
                    if (!short.TryParse(value, out short intValue))
                    {
                        LogAction?.Invoke($"PLC绑定按钮写入 [{PlcAddress.Trim()}] INT: 格式错误。");
                        return false;
                    }

                    await plc.WriteIntAsync(PlcAddress, intValue);
                    break;
                case PlcValueType.REAL:
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double realValue)
                        && !double.TryParse(value, out realValue))
                    {
                        LogAction?.Invoke($"PLC绑定按钮写入 [{PlcAddress.Trim()}] REAL: 格式错误。");
                        return false;
                    }

                    await plc.WriteDoubleAsync(PlcAddress, realValue);
                    break;
                case PlcValueType.DWORD:
                    if (!uint.TryParse(value, out uint dwordValue))
                    {
                        LogAction?.Invoke($"PLC绑定按钮写入 [{PlcAddress.Trim()}] DWORD: 格式错误。");
                        return false;
                    }

                    await plc.WriteDWordAsync(PlcAddress, dwordValue);
                    break;
                case PlcValueType.STRING:
                    string writeValue = value.Length > StringLength ? value[..StringLength] : value;
                    await plc.WriteStringAsync(PlcAddress, writeValue);
                    value = writeValue;
                    break;
                default:
                    LogAction?.Invoke($"PLC绑定按钮写入 [{PlcAddress.Trim()}] {DataType}: 不支持的数据类型。");
                    return false;
            }

            CurrentValue = value;
            UpdateButtonText();
            return true;
        }
        catch (Exception ex)
        {
            LogOperationException("写入", ex);
            return false;
        }
    }

    private void BuildUi()
    {
        Controls.Add(_btnAction);
        ApplyFixedButtonSize();
    }

    private void ApplyFixedButtonSize()
    {
        MinimumSize = new Size(FixedButtonWidth, FixedButtonHeight);
        MaximumSize = new Size(FixedButtonWidth, FixedButtonHeight);
        Size = new Size(FixedButtonWidth, FixedButtonHeight);
    }

    /// <summary>标题与数据行合并为按钮上的单行文本（无标题时仅数据行）。</summary>
    private string FormatButtonText(string dataLine)
    {
        string line = dataLine ?? string.Empty;
        string title = _text.Trim();
        if (string.IsNullOrEmpty(title))
            return line;

        return $"{title} | {line}";
    }

    private void BindEvents()
    {
        _btnAction.Click += async (_, _) => await HandleButtonClickAsync();
        _btnAction.MouseUp += (_, e) => ActionMouseUp?.Invoke(this, e);
        MouseUp += (_, e) => ActionMouseUp?.Invoke(this, e);
    }

    private async Task HandleButtonClickAsync()
    {
        if (DataType != PlcValueType.BOOL)
        {
            await ShowDataAsync();
            return;
        }

        try
        {
            var plc = GetConnectedPlc();
            if (AutoReset)
            {
                int delayMs = Math.Clamp(AutoResetDelayMs, 10, 60_000);
                await plc.WriteBoolAsync(PlcAddress, true);
                await Task.Delay(delayMs);
                await plc.WriteBoolAsync(PlcAddress, AutoResetTargetValue);
                CurrentValue = AutoResetTargetValue.ToString(CultureInfo.InvariantCulture);
                UpdateButtonText();
                return;
            }

            bool current = await plc.ReadBoolAsync(PlcAddress);
            bool next = !current;
            await plc.WriteBoolAsync(PlcAddress, next);
            CurrentValue = next.ToString(CultureInfo.InvariantCulture);
            UpdateButtonText();
        }
        catch (Exception ex)
        {
            LogOperationException("BOOL单击切换", ex);
        }
    }

    private async Task<object> ReadPlcValueAsync(global::PlcConectCom.PlcCommunicator plc)
    {
        return DataType switch
        {
            PlcValueType.BOOL => await plc.ReadBoolAsync(PlcAddress),
            PlcValueType.BYTE => await plc.ReadByteAsync(PlcAddress),
            PlcValueType.INT => await plc.ReadIntAsync(PlcAddress),
            PlcValueType.REAL => await plc.ReadDoubleAsync(PlcAddress),
            PlcValueType.DWORD => await plc.ReadDWordAsync(PlcAddress),
            PlcValueType.STRING => await plc.ReadStringAsync(PlcAddress, StringLength),
            _ => throw new InvalidOperationException("不支持的数据类型。") // 由 ShowDataAsync 捕获并记日志
        };
    }

    private void LogOperationException(string verb, Exception ex)
    {
        string addr = string.IsNullOrWhiteSpace(PlcAddress) ? "(未绑定地址)" : PlcAddress.Trim();
        LogAction?.Invoke($"PLC绑定按钮{verb} [{addr}] {DataType}: {ex.Message}");
    }

    private global::PlcConectCom.PlcCommunicator GetConnectedPlc()
    {
        if (string.IsNullOrWhiteSpace(PlcAddress))
            throw new InvalidOperationException("请先绑定PLC地址。");

        var plc = PlcProvider?.Invoke();
        if (plc is null || !plc.IsConnected)
            throw new InvalidOperationException("请先连接 PLC。");

        return plc;
    }

    /// <summary>更新按钮上的标题+数据、对齐与背景色（尺寸始终固定）。</summary>
    private void UpdateButtonText()
    {
        var plc = PlcProvider?.Invoke();
        bool connected = plc is not null && plc.IsConnected;

        if (!connected)
        {
            _btnAction.Text = FormatButtonText("未连接");
            _btnAction.TextAlign = ContentAlignment.MiddleCenter;
            _btnAction.BackColor = Color.LightGray;
            _btnAction.ForeColor = Color.DimGray;
            return;
        }

        if (DataType == PlcValueType.BOOL && bool.TryParse(CurrentValue, out bool boolState))
        {
            _btnAction.Text = FormatButtonText(boolState ? "打开" : "关闭");
            _btnAction.TextAlign = ContentAlignment.MiddleCenter;
            _btnAction.BackColor = boolState ? Color.LimeGreen : Color.IndianRed;
            _btnAction.ForeColor = Color.Black;
            return;
        }

        if (string.IsNullOrWhiteSpace(CurrentValue))
        {
            string placeholder = string.IsNullOrWhiteSpace(PlcAddress) ? "—" : PlcAddress.Trim();
            _btnAction.Text = FormatButtonText(placeholder);
            _btnAction.TextAlign = ContentAlignment.MiddleCenter;
            _btnAction.BackColor = SystemColors.Control;
            _btnAction.ForeColor = SystemColors.GrayText;
            return;
        }

        _btnAction.Text = FormatButtonText(CurrentValue);
        _btnAction.TextAlign = ContentAlignment.MiddleCenter;
        _btnAction.BackColor = SystemColors.Control;
        _btnAction.ForeColor = SystemColors.ControlText;
    }
}
