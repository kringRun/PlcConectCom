using System.Globalization;

namespace PlcConectCom.WinFormsTest;

public sealed class ProcessRealtimeValueDisplayControl : UserControl
{
    private readonly Label _lblTitle = new() { Dock = DockStyle.Left, Width = 140, TextAlign = ContentAlignment.MiddleLeft };
    private readonly TextBox _txtValue = new() { Dock = DockStyle.Fill, ReadOnly = true, TextAlign = HorizontalAlignment.Center };
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
        Width = 360;
        Height = 34;
        Margin = new Padding(0, 0, 0, 6);
        Controls.Add(_txtValue);
        Controls.Add(_lblTitle);
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public void ApplyItem(ProcessRealtimeDataItem item)
    {
        _address = item.Address?.Trim() ?? string.Empty;
        _type = item.Type == ParameterValueType.REAL_ARRAY ? ParameterValueType.REAL : item.Type;
        _displayAsHms = item.DisplayAsHms;
        Color c = Color.FromArgb(item.DisplayColorArgb);
        _lblTitle.ForeColor = c;
        _txtValue.ForeColor = c;
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
            _txtValue.Text = "--";
            return;
        }

        _reading = true;
        try
        {
            string s = await ReadValueForDisplayAsync(plc, _type, _address, _displayAsHms);
            _txtValue.Text = _displayAsHms ? FormatSecondsToHms(s) : s;
        }
        catch (Exception ex)
        {
            _txtValue.Text = "ERR";
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
