using System.Globalization;

namespace PlcConectCom.WinFormsTest;

public sealed class ProcessRealtimeValueDisplayControl : UserControl
{
    /// <summary>名称统一深灰：与区块标题（深蓝 28,58,120）明显区分，标题归标题、条目归条目。</summary>
    private static readonly Color UnifiedNameColor = Color.FromArgb(70, 70, 70);

    // 名称统一左对齐并按内容自适应宽度（AutoSize），文字后仅留 6px 间隙；剩余宽度全部给数值框（Dock.Fill）
    private readonly Label _lblTitle = new()
    {
        Dock = DockStyle.Left,
        AutoSize = true,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(0, 0, 6, 0),
        Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
        ForeColor = UnifiedNameColor
    };
    // 值显示用白底细边框 Label：比只读 TextBox 更轻，与按钮区的扁平风格统一
    private readonly Label _lblValue = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        BackColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle
    };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private bool _reading;
    private string _address = string.Empty;
    private ParameterValueType _type = ParameterValueType.REAL;
    private bool _displayAsHms;

    public Func<global::PlcConectCom.PlcCommunicator?>? PlcProvider { get; set; }
    public global::PlcConectCom.PlcCommunicator? PlcInstance { get; set; }
    public Action<string>? LogAction { get; set; }

    public bool AutoReadEnabled
    {
        get => _timer.Enabled;
        set
        {
            if (value)
                _timer.Start();
            else
                _timer.Stop();
        }
    }

    public ProcessRealtimeValueDisplayControl()
    {
        Width = 400;
        Height = 34;
        Margin = new Padding(0, 0, 0, 6);
        Controls.Add(_lblValue);
        Controls.Add(_lblTitle);
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    /// <summary>按分辨率缩放：scale=1 为默认 400×34，标题按内容自适应；低分辨率整体缩小。</summary>
    public void ApplyUiScale(float scale)
    {
        if (scale <= 0)
            return;
        Width = (int)Math.Round(400 * scale);
        Height = (int)Math.Round(34 * scale);
        Margin = new Padding(0, 0, 0, (int)Math.Round(6 * scale));
        _lblTitle.Font = new Font("Microsoft YaHei UI", 11F * scale, FontStyle.Bold);
        _lblTitle.Padding = new Padding(0, 0, (int)Math.Round(6 * scale), 0);
        _lblValue.Font = new Font("Microsoft YaHei UI", 10F * scale, FontStyle.Bold);
        Font = new Font("Microsoft YaHei UI", 9F * scale);
    }

    public void ApplyItem(ProcessRealtimeDataItem item)
    {
        _address = item.Address?.Trim() ?? string.Empty;
        _type = item.Type == ParameterValueType.REAL_ARRAY ? ParameterValueType.REAL : item.Type;
        _displayAsHms = item.DisplayAsHms;
        // 名称统一深灰（与深蓝区块标题区分）；数值保留配置色（故障红/运行绿等语义色仍有用）
        Color c = Color.FromArgb(item.DisplayColorArgb);
        _lblTitle.ForeColor = UnifiedNameColor;
        _lblValue.ForeColor = c;
        _lblTitle.Text = !string.IsNullOrWhiteSpace(item.Name)
            ? item.Name.Trim()
            : !string.IsNullOrWhiteSpace(item.Comment) ? item.Comment.Trim() : _address;
    }

    private async Task RefreshAsync()
    {
        if (_reading || string.IsNullOrWhiteSpace(_address))
            return;
        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
        {
            _lblValue.Text = "--";
            return;
        }

        _reading = true;
        try
        {
            string s = await ReadValueForDisplayAsync(plc, _type, _address, _displayAsHms);
            _lblValue.Text = _displayAsHms ? FormatSecondsToHms(s) : s;
        }
        catch (Exception ex)
        {
            _lblValue.Text = "ERR";
            LogAction?.Invoke($"主界面工艺数据读取失败 {_address}: {ex.Message}");
        }
        finally
        {
            _reading = false;
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
