using System.Globalization;

namespace PlcConectCom.WinFormsTest;

// PLC 数值显示类型
public enum PlcNumericDisplayType
{
    INT,
    UINT,
    REAL
}

// 自动读取 PLC 地址并显示文本结果的控件
public sealed class PlcAutoTextDisplayControl : UserControl
{
    private readonly Label _lblTitle = new() { Dock = DockStyle.Left, Width = 120, TextAlign = ContentAlignment.MiddleLeft };
    private readonly TextBox _txtValue = new() { Dock = DockStyle.Fill, ReadOnly = true, TextAlign = HorizontalAlignment.Center };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };

    private bool _isReading;
    private bool _hasValidValue;

    public PlcAutoTextDisplayControl()
    {
        BuildUi();
        BindEvents();
        UpdateTitle();
    }

    public string Title
    {
        get => _lblTitle.Text;
        set => _lblTitle.Text = value;
    }

    public string PlcAddress { get; set; } = string.Empty;

    public PlcNumericDisplayType DataType { get; set; } = PlcNumericDisplayType.INT;

    public int AutoReadIntervalMs
    {
        get => _timer.Interval;
        set => _timer.Interval = Math.Clamp(value, 100, 60_000);
    }

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

    public string CurrentValue
    {
        get => _txtValue.Text;
        private set => _txtValue.Text = value;
    }

    public Func<global::PlcConectCom.PlcCommunicator?>? PlcProvider { get; set; }

    public global::PlcConectCom.PlcCommunicator? PlcInstance { get; set; }

    public Action<string>? LogAction { get; set; }

    public async Task RefreshValueAsync()
    {
        if (_isReading)
            return;

        if (string.IsNullOrWhiteSpace(PlcAddress))
        {
            if (!_hasValidValue)
                CurrentValue = string.Empty;
            return;
        }

        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is null || !plc.IsConnected)
        {
            if (!_hasValidValue)
                CurrentValue = "--";
            return;
        }

        _isReading = true;
        try
        {
            object value = DataType switch
            {
                PlcNumericDisplayType.INT => await plc.ReadIntAsync(PlcAddress),
                PlcNumericDisplayType.UINT => await plc.ReadDWordAsync(PlcAddress),
                PlcNumericDisplayType.REAL => await plc.ReadDoubleAsync(PlcAddress),
                _ => string.Empty
            };

            CurrentValue = value switch
            {
                double d => d.ToString("0.###", CultureInfo.InvariantCulture),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
            };
            _hasValidValue = true;
        }
        catch (Exception ex)
        {
            if (!_hasValidValue)
                CurrentValue = "ERR";
            LogAction?.Invoke($"文本显示控件读取失败 {PlcAddress}: {ex.Message}");
        }
        finally
        {
            _isReading = false;
        }
    }

    public void ApplyBinding(string address, PlcNumericDisplayType dataType, string? title = null)
    {
        PlcAddress = address.Trim();
        DataType = dataType;
        _hasValidValue = false;
        CurrentValue = string.Empty;
        if (!string.IsNullOrWhiteSpace(title))
            Title = title.Trim();

        UpdateTitle();
    }

    private void BuildUi()
    {
        Height = 32;
        MinimumSize = new Size(180, 32);
        Controls.Add(_txtValue);
        Controls.Add(_lblTitle);
    }

    private void BindEvents()
    {
        _timer.Tick += async (_, _) => await RefreshValueAsync();
    }

    private void UpdateTitle()
    {
        if (string.IsNullOrWhiteSpace(_lblTitle.Text))
            _lblTitle.Text = string.IsNullOrWhiteSpace(PlcAddress) ? "数据" : $"{PlcAddress} ({DataType})";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _timer.Stop();

        base.Dispose(disposing);
    }
}
