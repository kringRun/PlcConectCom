using System.ComponentModel;
using Sunny.UI;

namespace PlcConectCom.WinFormsTest;

/// <summary>PLC BOOL 显示控件：读取布尔值并用不同颜色显示状态。</summary>
public sealed class PlcBoolLabe : UserControl
{
    private readonly UILabel _lblValue = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        Text = "False",
        BackColor = Color.Silver,
        ForeColor = Color.Black
    };

    private bool _currentValue;
    private string _trueText = "True";
    private string _falseText = "False";
    private string _plcAddress = string.Empty;
    private bool _pendingRefresh;
    private bool _isReading;
    private bool _autoReadEnabled = true;
    private readonly System.Windows.Forms.Timer _autoReadTimer = new();

    /// <summary>仅支持 BOOL 地址（如 MB100 / MX0.0）。变更后会自动重新读取。</summary>
    [Browsable(true)]
    [Category("Data")]
    [DefaultValue("")]
    public string PlcAddress
    {
        get => _plcAddress;
        set
        {
            string trimmed = value?.Trim() ?? string.Empty;
            if (string.Equals(_plcAddress, trimmed, StringComparison.Ordinal))
                return;

            _plcAddress = trimmed;
            RequestRefresh();
        }
    }

    /// <summary>是否按周期自动读取 PLC 并刷新显示。</summary>
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

    /// <summary>自动读取周期（毫秒）。</summary>
    [Browsable(true)]
    [Category("Behavior")]
    [DefaultValue(500)]
    public int AutoReadIntervalMs
    {
        get => _autoReadTimer.Interval;
        set => _autoReadTimer.Interval = Math.Clamp(value, 100, 60_000);
    }

    [Browsable(true)]
    [Category("Appearance")]
    [DefaultValue(typeof(Color), "LimeGreen")]
    public Color TrueColor
    {
        get => _trueColor;
        set
        {
            _trueColor = value;
            UpdateValueDisplay(_currentValue);
        }
    }

    private Color _trueColor = Color.LimeGreen;

    [Browsable(true)]
    [Category("Appearance")]
    [DefaultValue(typeof(Color), "Silver")]
    public Color FalseColor
    {
        get => _falseColor;
        set
        {
            _falseColor = value;
            UpdateValueDisplay(_currentValue);
        }
    }

    private Color _falseColor = Color.Silver;

    [Browsable(true)]
    [Category("Appearance")]
    [DefaultValue(typeof(Color), "Black")]
    public Color ValueForeColor
    {
        get => _valueForeColor;
        set
        {
            _valueForeColor = value;
            UpdateValueDisplay(_currentValue);
        }
    }

    private Color _valueForeColor = Color.Black;

    [Browsable(true)]
    [Category("Appearance")]
    [DefaultValue("True")]
    public string TrueText
    {
        get => _trueText;
        set
        {
            _trueText = string.IsNullOrEmpty(value) ? "True" : value;
            UpdateValueDisplay(_currentValue);
        }
    }

    [Browsable(true)]
    [Category("Appearance")]
    [DefaultValue("False")]
    public string FalseText
    {
        get => _falseText;
        set
        {
            _falseText = string.IsNullOrEmpty(value) ? "False" : value;
            UpdateValueDisplay(_currentValue);
        }
    }

    [Browsable(false)]
    public bool CurrentValue => _currentValue;

    public Func<global::PlcConectCom.PlcCommunicator?>? PlcProvider { get; set; }

    public global::PlcConectCom.PlcCommunicator? PlcInstance { get; set; }

    public Action<string>? LogAction { get; set; }

    /// <summary>布尔值显示更新后触发。</summary>
    public event Action<bool>? ValueChanged;

    public PlcBoolLabe()
    {
        Height = 34;
        _autoReadTimer.Interval = 500;
        _autoReadTimer.Tick += OnAutoReadTick;
        BuildUi();
        UpdateValueDisplay(false);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateAutoReadTimerRunning();
        if (_pendingRefresh)
        {
            _pendingRefresh = false;
            BeginInvoke(new Action(() => _ = RefreshStateAsync()));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _autoReadTimer.Stop();
            _autoReadTimer.Tick -= OnAutoReadTick;
            _autoReadTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    public async Task RefreshStateAsync()
    {
        if (_isReading)
            return;

        _isReading = true;
        try
        {
            var plc = GetConnectedPlcOrNull();
            if (plc is null)
            {
                SetEnabledState(false);
                return;
            }

            string addr = _plcAddress.Trim();
            if (string.IsNullOrWhiteSpace(addr))
            {
                SetEnabledState(false);
                return;
            }

            bool value = await plc.ReadBoolAsync(addr);
            SetEnabledState(true);
            SetValue(value);
        }
        catch (Exception ex)
        {
            SetEnabledState(false);
            LogAction?.Invoke($"PLC布尔显示读取失败 [{_plcAddress}]: {ex.Message}");
        }
        finally
        {
            _isReading = false;
        }
    }

    public void SetValue(bool value)
    {
        if (_currentValue == value)
        {
            UpdateValueDisplay(value);
            return;
        }

        _currentValue = value;
        UpdateValueDisplay(value);
        ValueChanged?.Invoke(value);
    }

    private void BuildUi()
    {
        var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0) };
        root.Controls.Add(_lblValue);
        Controls.Add(root);
    }

    private void UpdateValueDisplay(bool value)
    {
        _lblValue.BackColor = value ? _trueColor : _falseColor;
        _lblValue.ForeColor = _valueForeColor;
        _lblValue.Text = value ? _trueText : _falseText;
    }

    private void SetEnabledState(bool enabled)
    {
        _lblValue.Enabled = enabled;
    }

    private global::PlcConectCom.PlcCommunicator? GetConnectedPlcOrNull()
    {
        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is null || !plc.IsConnected)
            return null;
        return plc;
    }

    private void OnAutoReadTick(object? sender, EventArgs e)
    {
        if (DesignMode || LicenseManager.UsageMode == LicenseUsageMode.Designtime)
            return;
        _ = RefreshStateAsync();
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

        void Run()
        {
            if (IsDisposed)
                return;
            _ = RefreshStateAsync();
        }

        if (InvokeRequired)
            BeginInvoke(Run);
        else
            Run();
    }
}
