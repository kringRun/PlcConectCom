using PlcConectCom.WinForms.Controls;

namespace PlcConectCom.WinFormsTest;

// PLC 绑定按钮组控件，支持批量地址映射与自适应排版
public sealed class PlcBoundButtonGroupControl : UserControl
{
    private readonly List<PlcBoundButton> _boundButtons = [];
    private readonly Panel _buttonPanel = new() { Dock = DockStyle.Fill, Padding = new Padding(8), AutoScroll = true };

    private string _startAddress = "MW200";
    private PlcValueType _dataType = PlcValueType.INT;
    private int _stringLength = 64;
    private int _addressCount = 8;
    private double _buttonAspectRatio = 2.2d;

    public PlcBoundButtonGroupControl()
    {
        Controls.Add(_buttonPanel);
        _buttonPanel.Resize += (_, _) => ArrangeBoundButtons();
    }

    public Func<global::PlcConectCom.PlcCommunicator?>? PlcProvider { get; set; }

    /// <summary>下发给每个 <see cref="PlcBoundButton"/>，异常写入主界面日志区。</summary>
    public Action<string>? LogAction { get; set; }

    public Func<string>? WriteValueProvider { get; set; }

    public Action<string>? ValueChangedByButton { get; set; }

    public event Action<string>? LogMessageGenerated;

    public string StartAddress
    {
        get => _startAddress;
        set => _startAddress = value.Trim();
    }

    public PlcValueType DataType
    {
        get => _dataType;
        set => _dataType = value;
    }

    public int StringLength
    {
        get => _stringLength;
        set => _stringLength = Math.Max(1, value);
    }

    public int AddressCount
    {
        get => _addressCount;
        set => _addressCount = Math.Clamp(value, 1, 1024);
    }

    public double ButtonAspectRatio
    {
        get => _buttonAspectRatio;
        set => _buttonAspectRatio = Math.Max(0.2d, value);
    }

    public void RefreshButtons()
    {
        _boundButtons.Clear();
        _buttonPanel.SuspendLayout();
        _buttonPanel.Controls.Clear();

        for (int i = 0; i < AddressCount; i++)
        {
            var button = new PlcBoundButton();
            button.PlcProvider = PlcProvider;
            button.LogAction = LogAction;
            button.ApplyBinding(GetAddressByIndex(StartAddress, i), DataType, StringLength);
            button.ActionMouseUp += async (_, e) => await HandleBoundButtonMouseUpAsync(button, e.Button);
            _boundButtons.Add(button);
            _buttonPanel.Controls.Add(button);
        }

        _buttonPanel.ResumeLayout();
        ArrangeBoundButtons();
    }

    public void RearrangeButtons()
    {
        ArrangeBoundButtons();
    }

    public void RefreshConnectionStates()
    {
        foreach (var button in _boundButtons)
            button.RefreshConnectionState();
    }

    private async Task HandleBoundButtonMouseUpAsync(PlcBoundButton boundButton, MouseButtons button)
    {
        boundButton.DataType = DataType;
        boundButton.StringLength = StringLength;

        try
        {
            if (button == MouseButtons.Left)
            {
                string? value = await boundButton.ShowDataAsync();
                if (value is null)
                    return;

                ValueChangedByButton?.Invoke(value);
                LogMessageGenerated?.Invoke($"按钮组读取成功 {boundButton.PlcAddress} => {value}");
            }
            else if (button == MouseButtons.Right)
            {
                string inputValue = WriteValueProvider?.Invoke()?.Trim() ?? string.Empty;
                if (!await boundButton.SaveDataAsync(inputValue))
                    return;

                ValueChangedByButton?.Invoke(boundButton.CurrentValue);
                LogMessageGenerated?.Invoke($"按钮组写入成功 {boundButton.PlcAddress} <= {boundButton.CurrentValue}");
            }
        }
        catch (Exception ex)
        {
            LogMessageGenerated?.Invoke($"按钮组操作失败: {ex.Message}");
        }
    }

    private void ArrangeBoundButtons()
    {
        int count = _boundButtons.Count;
        if (count == 0)
            return;

        int availableWidth = Math.Max(120, _buttonPanel.ClientSize.Width - _buttonPanel.Padding.Horizontal);
        int availableHeight = Math.Max(80, _buttonPanel.ClientSize.Height - _buttonPanel.Padding.Vertical);
        double targetRatio = ButtonAspectRatio;
        int spacing = 6;

        int bestColumns = 1;
        double bestScore = double.MinValue;

        for (int columns = 1; columns <= count; columns++)
        {
            int rows = (int)Math.Ceiling(count / (double)columns);
            double cellWidth = Math.Max(1d, (availableWidth - spacing * (columns - 1)) / (double)columns);
            double cellHeight = Math.Max(1d, (availableHeight - spacing * (rows - 1)) / (double)rows);

            double fitWidth = Math.Min(cellWidth, cellHeight * targetRatio);
            double fitHeight = fitWidth / targetRatio;
            double areaScore = fitWidth * fitHeight;

            if (areaScore > bestScore)
            {
                bestScore = areaScore;
                bestColumns = columns;
            }
        }

        int finalRows = (int)Math.Ceiling(count / (double)bestColumns);
        int finalCellWidth = Math.Max(1, (availableWidth - spacing * (bestColumns - 1)) / bestColumns);
        int finalCellHeight = Math.Max(1, (availableHeight - spacing * (finalRows - 1)) / finalRows);
        int buttonWidth = Math.Max(140, Math.Min(finalCellWidth, (int)Math.Round(finalCellHeight * targetRatio)));
        int buttonHeight = Math.Max(44, Math.Min(finalCellHeight, (int)Math.Round(buttonWidth / targetRatio)));

        for (int i = 0; i < count; i++)
        {
            int row = i / bestColumns;
            int col = i % bestColumns;

            int cellX = _buttonPanel.Padding.Left + col * (finalCellWidth + spacing);
            int cellY = _buttonPanel.Padding.Top + row * (finalCellHeight + spacing);
            int x = cellX + Math.Max(0, (finalCellWidth - buttonWidth) / 2);
            int y = cellY + Math.Max(0, (finalCellHeight - buttonHeight) / 2);

            _boundButtons[i].Bounds = new Rectangle(x, y, buttonWidth, buttonHeight);
        }
    }

    private static string GetAddressByIndex(string startAddress, int index)
    {
        if (index <= 0)
            return startAddress;

        try
        {
            var (type, offset, bit) = global::PlcConectCom.AddressParser.ParseAddress(startAddress);
            if (type == "MX")
            {
                int absoluteBit = offset * 8 + bit + index;
                int byteOffset = absoluteBit / 8;
                int bitOffset = absoluteBit % 8;
                return $"MX{byteOffset}.{bitOffset}";
            }

            return $"{type}{offset + index}";
        }
        catch
        {
            return startAddress;
        }
    }
}
