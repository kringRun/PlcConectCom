using System.Globalization;
using System.Text;
using System.Drawing.Drawing2D;
using ExcelDataReader;

namespace PlcConectCom.WinFormsTest;

// 工站操作状态复合控件：命令按钮 + 信号灯 + 流程状态文本
public sealed class StationOperationStatusControl : UserControl
{
    // 顶部：屏蔽勾选 + 工站名称
    private readonly CheckBox _chkStationEnabled = new()
    {
        Text = "屏蔽",
        AutoSize = true,
        Margin = new Padding(8, 0, 4, 0),
        ForeColor = Color.White,
        BackColor = Color.SteelBlue,
        FlatStyle = FlatStyle.Flat,
        UseVisualStyleBackColor = false
    };

    private readonly Label _lblStationName = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
        ForeColor = Color.White,
        BackColor = Color.SteelBlue
    };

    // 中部：四个操作按钮
    private readonly Button _btnStart = new() { Text = "启动", Dock = DockStyle.Fill };
    private readonly Button _btnPause = new() { Text = "暂停", Dock = DockStyle.Fill };
    private readonly Button _btnStop = new() { Text = "停止", Dock = DockStyle.Fill };
    private readonly Button _btnReset = new() { Text = "复位", Dock = DockStyle.Fill };

    // 下部：四个状态信号灯（圆形内竖排显示名称，文字裁剪在圆内）
    private readonly SignalLampIndicator _lampAuto = new("自动中");
    private readonly SignalLampIndicator _lampPause = new("暂停中");
    private readonly SignalLampIndicator _lampInit = new("初始化中");
    private readonly SignalLampIndicator _lampInitDone = new("初始完成");

    // 报警与单次 CT（与上方信号灯同列宽、同行样式）
    private readonly SignalLampIndicator _lampStationAlarm = new("工站报警", redWhenTriggered: true);
    private readonly SignalLampIndicator _lampInitTimeoutAlarm = new("超时报警", redWhenTriggered: true);
    private readonly SignalLampIndicator _lampInitSafety = new("安全条件", redWhenTriggered: true);
    private readonly SignalLampIndicator _lampInitSafetyNotMet = new("安不满足", redWhenTriggered: true);

    private readonly TextBox _txtSingleCt = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        TextAlign = System.Windows.Forms.HorizontalAlignment.Left,
        BackColor = Color.White,
        Font = new Font("Microsoft YaHei UI", 8.25f)
    };

    // 最底部：流程状态文本
    private readonly TextBox _txtFlowStatus = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        TextAlign = System.Windows.Forms.HorizontalAlignment.Left,
        BackColor = Color.White
    };

    // 自动刷新定时器
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private bool _isRefreshing;
    private bool _suppressStationEnabledCheckEvent;

    public StationOperationStatusControl()
    {
        BuildUi();
        BindEvents();
    }

    public Func<global::PlcConectCom.PlcCommunicator?>? PlcProvider { get; set; }

    public global::PlcConectCom.PlcCommunicator? PlcInstance { get; set; }

    public Action<string>? LogAction { get; set; }

    // 工站名称
    public string StationName
    {
        get => _lblStationName.Text;
        set => _lblStationName.Text = string.IsNullOrWhiteSpace(value) ? "StationName" : value.Trim();
    }

    // 四个命令按钮绑定地址
    public string StartCommandAddress { get; set; } = string.Empty;

    public string PauseCommandAddress { get; set; } = string.Empty;

    public string StopCommandAddress { get; set; } = string.Empty;

    public string ResetCommandAddress { get; set; } = string.Empty;

    // 四个信号灯绑定地址
    public string AutoSignalAddress { get; set; } = string.Empty;

    public string PauseSignalAddress { get; set; } = string.Empty;

    public string InitSignalAddress { get; set; } = string.Empty;

    public string InitDoneSignalAddress { get; set; } = string.Empty;

    public string StationAlarmAddress { get; set; } = string.Empty;

    public string InitTimeoutAlarmAddress { get; set; } = string.Empty;

    public string InitSafetyConditionAddress { get; set; } = string.Empty;

    public string InitSafetyNotMetAlarmAddress { get; set; } = string.Empty;

    /// <summary>单次 CT（REAL，MB/MW/MD 地址）。</summary>
    public string SingleCycleTimeAddress { get; set; } = string.Empty;

    /// <summary>工站屏蔽状态（与勾选同步；PLC 离线时保留上次或配置初值）。</summary>
    public bool StationEnabled { get; set; } = true;

    /// <summary>工站屏蔽 BOOL 地址（勾选写入 true，取消写入 false）。</summary>
    public string StationEnabledAddress { get; set; } = string.Empty;

    /// <summary>流程状态 ID 绑定地址（MW/MB/MD 等，与 <see cref="FlowStatusIdValueType"/> 一致）。</summary>
    public string FlowStatusIdAddress { get; set; } = string.Empty;

    /// <summary>流程 ID 的读取类型：WORD/INT 为 16 位；UINT/DWORD 为 32 位无符号。</summary>
    public StationFlowStatusIdType FlowStatusIdValueType { get; set; } = StationFlowStatusIdType.INT;

    // 流程状态字典（<int,string>）
    public Dictionary<int, string> FlowStatusMap { get; private set; } = [];

    // 定时刷新周期
    public int AutoRefreshIntervalMs
    {
        get => _timer.Interval;
        set => _timer.Interval = Math.Clamp(value, 100, 60_000);
    }

    // 启停自动刷新
    public bool AutoRefreshEnabled
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

    // 从外部直接设置状态字典
    public void SetFlowStatusMap(Dictionary<int, string> statusMap)
    {
        FlowStatusMap = statusMap ?? [];
    }

    // 从Excel导入状态字典（第一行表头，默认识别ID/Value列）。
    // 多工作表时按 stationName 匹配工作表名称；未提供或未命中时回退第一个工作表。
    public Dictionary<int, string> LoadFlowStatusMapFromExcel(string filePath)
        => LoadFlowStatusMapFromExcel(filePath, stationName: null);

    public Dictionary<int, string> LoadFlowStatusMapFromExcel(string filePath, string? stationName)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("文件路径不能为空。", nameof(filePath));

        if (!File.Exists(filePath))
            throw new FileNotFoundException("未找到状态字典文件。", filePath);

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateReader(stream);

        // 多工作表：工作表名称即工站名称，遍历匹配；未命中则回退第一个工作表（兼容单表文件）
        if (!string.IsNullOrWhiteSpace(stationName))
        {
            string target = stationName.Trim();
            bool matched = false;
            do
            {
                string sheetName = reader.Name?.Trim() ?? string.Empty;
                if (string.Equals(sheetName, target, StringComparison.OrdinalIgnoreCase))
                {
                    matched = true;
                    break;
                }
            }
            while (reader.NextResult());

            if (!matched)
                reader.Reset();
        }

        var map = new Dictionary<int, string>();
        bool isHeader = true;
        int idCol = 0;
        int valueCol = 1;

        while (reader.Read())
        {
            if (isHeader)
            {
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    string header = Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                    if (string.Equals(header, "ID", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(header, "Id", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(header, "状态ID", StringComparison.OrdinalIgnoreCase))
                        idCol = i;

                    if (string.Equals(header, "Value", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(header, "Text", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(header, "Name", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(header, "状态文本", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(header, "流程状态", StringComparison.OrdinalIgnoreCase))
                        valueCol = i;
                }

                isHeader = false;
                continue;
            }

            string idText = Convert.ToString(reader.GetValue(idCol), CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            string valueText = Convert.ToString(reader.GetValue(valueCol), CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;

            if (!int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                continue;

            if (string.IsNullOrWhiteSpace(valueText))
                continue;

            map[id] = valueText;
        }

        FlowStatusMap = map;
        return map;
    }

    // 弹窗导入状态字典
    public bool ImportFlowStatusMapByDialog(IWin32Window owner)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择流程状态字典Excel文件",
            Filter = "Excel 文件 (*.xls;*.xlsx)|*.xls;*.xlsx|所有文件 (*.*)|*.*",
            InitialDirectory = AppContext.BaseDirectory
        };

        if (dialog.ShowDialog(owner) != DialogResult.OK)
            return false;

        var map = LoadFlowStatusMapFromExcel(dialog.FileName, StationName);
        LogAction?.Invoke($"流程状态字典导入成功，共 {map.Count} 条。来源: {dialog.FileName}");
        return true;
    }

    // 立即刷新信号灯和流程文本
    public async Task RefreshStatusAsync()
    {
        if (_isRefreshing)
            return;

        _isRefreshing = true;
        try
        {
            var plc = PlcProvider?.Invoke() ?? PlcInstance;
            if (plc is null || !plc.IsConnected)
            {
                _lampAuto.SetState(false, isOffline: true);
                _lampPause.SetState(false, isOffline: true);
                _lampInit.SetState(false, isOffline: true);
                _lampInitDone.SetState(false, isOffline: true);
                _lampStationAlarm.SetState(false, isOffline: true);
                _lampInitTimeoutAlarm.SetState(false, isOffline: true);
                _lampInitSafety.SetState(false, isOffline: true);
                _lampInitSafetyNotMet.SetState(false, isOffline: true);
                UpdateStationEnabledUiForOffline();
                ApplySingleCtText(plcConnected: false, value: null);
                _txtFlowStatus.Text = "--";
                return;
            }

            _lampAuto.SetState(await ReadBoolSafeAsync(plc, AutoSignalAddress));
            _lampPause.SetState(await ReadBoolSafeAsync(plc, PauseSignalAddress));
            _lampInit.SetState(await ReadBoolSafeAsync(plc, InitSignalAddress));
            _lampInitDone.SetState(await ReadBoolSafeAsync(plc, InitDoneSignalAddress));

            _lampStationAlarm.SetState(await ReadBoolSafeAsync(plc, StationAlarmAddress));
            _lampInitTimeoutAlarm.SetState(await ReadBoolSafeAsync(plc, InitTimeoutAlarmAddress));
            _lampInitSafety.SetState(await ReadBoolSafeAsync(plc, InitSafetyConditionAddress));
            _lampInitSafetyNotMet.SetState(await ReadBoolSafeAsync(plc, InitSafetyNotMetAlarmAddress));

            await UpdateStationEnabledUiAsync(plc);

            double? ct = string.IsNullOrWhiteSpace(SingleCycleTimeAddress)
                ? null
                : await ReadDoubleSafeAsync(plc, SingleCycleTimeAddress);
            ApplySingleCtText(plcConnected: true, value: ct);

            int? flowId = await ReadFlowStatusIdAsync(plc, FlowStatusIdAddress, FlowStatusIdValueType);
            if (!flowId.HasValue)
            {
                _txtFlowStatus.Text = "";
                return;
            }

            _txtFlowStatus.Text = FlowStatusMap.TryGetValue(flowId.Value, out string? text)
                ? text
                : $"未知流程ID: {flowId.Value}";
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private async Task WriteCommandAsync(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return;

        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is null || !plc.IsConnected)
        {
            LogAction?.Invoke("命令写入失败：PLC未连接。");
            return;
        }

        string addr = address.Trim();
        try
        {
            await plc.WriteBoolAsync(addr, true);
            await Task.Delay(100);
            await plc.WriteBoolAsync(addr, false);
            LogAction?.Invoke($"命令脉冲 {addr}: True → 100ms → False");
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"命令写入失败 {addr}: {ex.Message}");
        }
    }

    private void UpdateStationEnabledUiForOffline()
    {
        bool hasAddr = !string.IsNullOrWhiteSpace(StationEnabledAddress);
        _chkStationEnabled.Enabled = false;

        if (!hasAddr)
            SetStationEnabledCheckSilently(false);
        else
            SetStationEnabledCheckSilently(StationEnabled);
    }

    private async Task UpdateStationEnabledUiAsync(global::PlcConectCom.PlcCommunicator plc)
    {
        bool hasAddr = !string.IsNullOrWhiteSpace(StationEnabledAddress);
        if (!hasAddr)
        {
            _chkStationEnabled.Enabled = false;
            SetStationEnabledCheckSilently(false);
            return;
        }

        _chkStationEnabled.Enabled = true;
        bool v = await ReadBoolSafeAsync(plc, StationEnabledAddress);
        StationEnabled = v;
        SetStationEnabledCheckSilently(v);
    }

    private void SetStationEnabledCheckSilently(bool value)
    {
        _suppressStationEnabledCheckEvent = true;
        try
        {
            _chkStationEnabled.Checked = value;
        }
        finally
        {
            _suppressStationEnabledCheckEvent = false;
        }
    }

    private void ApplySingleCtText(bool plcConnected, double? value)
    {
        if (string.IsNullOrWhiteSpace(SingleCycleTimeAddress))
        {
            _txtSingleCt.Text = "";
            return;
        }

        if (!plcConnected)
        {
            _txtSingleCt.Text = "--";
            return;
        }

        _txtSingleCt.Text = value.HasValue
            ? value.Value.ToString("0.###", CultureInfo.InvariantCulture)
            : "--";
    }

    private async Task<bool> ReadBoolSafeAsync(global::PlcConectCom.PlcCommunicator plc, string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return false;

        try
        {
            return await plc.ReadBoolAsync(address.Trim());
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"信号读取失败 {address}: {ex.Message}");
            return false;
        }
    }

    private async Task<double?> ReadDoubleSafeAsync(global::PlcConectCom.PlcCommunicator plc, string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return null;

        try
        {
            return await plc.ReadDoubleAsync(address.Trim());
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"REAL读取失败 {address}: {ex.Message}");
            return null;
        }
    }

    private async Task<int?> ReadFlowStatusIdAsync(
        global::PlcConectCom.PlcCommunicator plc,
        string address,
        StationFlowStatusIdType valueType)
    {
        if (string.IsNullOrWhiteSpace(address))
            return null;

        string addr = address.Trim();
        try
        {
            switch (valueType)
            {
                case StationFlowStatusIdType.WORD:
                    {
                        short raw = await plc.ReadIntAsync(addr);
                        return (ushort)raw;
                    }
                case StationFlowStatusIdType.INT:
                    return await plc.ReadIntAsync(addr);
                case StationFlowStatusIdType.UINT:
                    return await plc.ReadIntAsync(addr);
                case StationFlowStatusIdType.DWORD:
                    {
                        uint u = await plc.ReadDWordAsync(addr);
                        return unchecked((int)u);
                    }
                default:
                    return await plc.ReadIntAsync(addr);
            }
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"流程ID读取失败 [{valueType}] {address}: {ex.Message}");
            return null;
        }
    }

    private void BuildUi()
    {
        MinimumSize = new Size(260, 310);
        Width = 280;
        Height = 310;
        BackColor = Color.White;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 6,
            ColumnCount = 1,
            Padding = new Padding(2)
        };

        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var titleBar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.SteelBlue
        };
        titleBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        titleBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        titleBar.Controls.Add(_chkStationEnabled, 0, 0);
        titleBar.Controls.Add(_lblStationName, 1, 0);

        var commandGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 1,
            ColumnCount = 4,
            Padding = new Padding(0, 2, 0, 0)
        };

        for (int i = 0; i < 4; i++)
            commandGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

        commandGrid.Controls.Add(_btnStart, 0, 0);
        commandGrid.Controls.Add(_btnPause, 1, 0);
        commandGrid.Controls.Add(_btnStop, 2, 0);
        commandGrid.Controls.Add(_btnReset, 3, 0);

        var lampGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 1,
            ColumnCount = 4,
            Padding = new Padding(0, 0, 0, 0),
            Margin = Padding.Empty
        };

        for (int i = 0; i < 4; i++)
            lampGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

        lampGrid.Controls.Add(_lampAuto, 0, 0);
        lampGrid.Controls.Add(_lampPause, 1, 0);
        lampGrid.Controls.Add(_lampInit, 2, 0);
        lampGrid.Controls.Add(_lampInitDone, 3, 0);

        var alarmGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 1,
            ColumnCount = 4,
            Padding = new Padding(0, 0, 0, 0),
            Margin = Padding.Empty
        };

        for (int i = 0; i < 4; i++)
            alarmGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

        alarmGrid.Controls.Add(_lampStationAlarm, 0, 0);
        alarmGrid.Controls.Add(_lampInitTimeoutAlarm, 1, 0);
        alarmGrid.Controls.Add(_lampInitSafety, 2, 0);
        alarmGrid.Controls.Add(_lampInitSafetyNotMet, 3, 0);

        var ctRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        ctRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
        ctRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        ctRow.Controls.Add(new Label
        {
            Text = "单次CT",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 8.25f),
            Margin = new Padding(2, 0, 0, 0)
        }, 0, 0);
        ctRow.Controls.Add(_txtSingleCt, 1, 0);

        var statusPanel = new Panel { Dock = DockStyle.Fill, Padding = Padding.Empty, Margin = new Padding(0, 2, 0, 0) };
        statusPanel.Controls.Add(_txtFlowStatus);

        root.Controls.Add(titleBar, 0, 0);
        root.Controls.Add(commandGrid, 0, 1);
        root.Controls.Add(lampGrid, 0, 2);
        root.Controls.Add(alarmGrid, 0, 3);
        root.Controls.Add(ctRow, 0, 4);
        root.Controls.Add(statusPanel, 0, 5);

        Controls.Add(root);
        StationName = "StationName";
    }

    private void BindEvents()
    {
        _btnStart.Click += async (_, _) => await WriteCommandAsync(StartCommandAddress);
        _btnPause.Click += async (_, _) => await WriteCommandAsync(PauseCommandAddress);
        _btnStop.Click += async (_, _) => await WriteCommandAsync(StopCommandAddress);
        _btnReset.Click += async (_, _) => await WriteCommandAsync(ResetCommandAddress);
        _chkStationEnabled.CheckedChanged += async (_, _) =>
        {
            if (_suppressStationEnabledCheckEvent)
                return;

            await WriteStationEnabledToPlcAsync(_chkStationEnabled.Checked);
        };
        _timer.Tick += async (_, _) => await RefreshStatusAsync();
    }

    private async Task WriteStationEnabledToPlcAsync(bool value)
    {
        if (string.IsNullOrWhiteSpace(StationEnabledAddress))
            return;

        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is null || !plc.IsConnected)
        {
            LogAction?.Invoke("工站屏蔽写入失败：PLC未连接。");
            return;
        }

        string addr = StationEnabledAddress.Trim();
        try
        {
            await plc.WriteBoolAsync(addr, value);
            StationEnabled = value;
            LogAction?.Invoke($"工站屏蔽已写入 {addr} = {value}");
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"工站屏蔽写入失败 {addr}: {ex.Message}");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _timer.Stop();

        base.Dispose(disposing);
    }

    // 内部信号灯：圆形底色 + 圆内竖排文字（裁剪在圆内）
    private sealed class SignalLampIndicator : UserControl
    {
        private readonly LampCirclePanel _circle;

        /// <param name="redWhenTriggered">为 true 时，接通显示红色（报警）；默认绿色（状态）。</param>
        public SignalLampIndicator(string caption, bool redWhenTriggered = false)
        {
            Dock = DockStyle.Fill;
            Margin = new Padding(0);
            Padding = new Padding(0);

            _circle = new LampCirclePanel(caption, redWhenTriggered) { Dock = DockStyle.Fill };
            Controls.Add(_circle);

            MinimumSize = new Size(50, 70);
        }

        public void SetState(bool isOn, bool isOffline = false)
        {
            _circle.SetState(isOn, isOffline);
        }

        private sealed class LampCirclePanel : Panel
        {
            private readonly string _caption;
            private readonly bool _redWhenTriggered;
            private bool _isOn;
            private bool _isOffline;

            public LampCirclePanel(string caption, bool redWhenTriggered)
            {
                _caption = caption ?? string.Empty;
                _redWhenTriggered = redWhenTriggered;
                BackColor = Color.White;
                DoubleBuffered = true;
                Paint += OnPaintLamp;
            }

            public void SetState(bool isOn, bool isOffline = false)
            {
                _isOn = isOn;
                _isOffline = isOffline;
                Invalidate();
            }

            private void OnPaintLamp(object? sender, PaintEventArgs e)
            {
                if (Width <= 0 || Height <= 0)
                    return;

                int size = Math.Min(44, Math.Max(20, Math.Min(Width - 8, Height - 4)));
                int x = (Width - size) / 2;
                // 圆形靠上，缩小与上方按钮行的视觉间距
                int y = 2;
                var rect = new Rectangle(x, y, size, size);

                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var brush = new SolidBrush(GetLampColor()))
                using (var borderPen = new Pen(Color.DimGray, 1.2f))
                {
                    e.Graphics.FillEllipse(brush, rect);
                    e.Graphics.DrawEllipse(borderPen, rect);
                }

                if (string.IsNullOrEmpty(_caption))
                    return;

                using var clipPath = new GraphicsPath();
                clipPath.AddEllipse(rect);
                e.Graphics.SetClip(clipPath);

                try
                {
                    DrawVerticalCaptionInCircle(e.Graphics, rect);
                }
                finally
                {
                    e.Graphics.ResetClip();
                }
            }

            private void DrawVerticalCaptionInCircle(Graphics g, Rectangle rect)
            {
                float inner = rect.Width;
                float maxTry = Math.Min(11f, Math.Max(4f, inner / 3.2f));
                float emSize = 4f;

                for (float tryEm = maxTry; tryEm >= 4f; tryEm -= 0.5f)
                {
                    using var testFont = new Font("Microsoft YaHei UI", tryEm, FontStyle.Bold, GraphicsUnit.Pixel);
                    float totalH = 0;
                    float maxW = 0;
                    foreach (char ch in _caption)
                    {
                        SizeF sz = g.MeasureString(ch.ToString(), testFont);
                        totalH += sz.Height;
                        maxW = Math.Max(maxW, sz.Width);
                    }

                    if (totalH <= rect.Height - 4 && maxW <= rect.Width - 4)
                    {
                        emSize = tryEm;
                        break;
                    }

                    emSize = tryEm;
                }

                using var font = new Font("Microsoft YaHei UI", emSize, FontStyle.Bold, GraphicsUnit.Pixel);
                using var textBrush = new SolidBrush(GetCaptionBrushColor());

                float totalHeight = 0;
                foreach (char ch in _caption)
                {
                    SizeF sz = g.MeasureString(ch.ToString(), font);
                    totalHeight += sz.Height;
                }

                float cy = rect.Y + (rect.Height - totalHeight) / 2f;
                foreach (char ch in _caption)
                {
                    SizeF sz = g.MeasureString(ch.ToString(), font);
                    float cx = rect.X + (rect.Width - sz.Width) / 2f;
                    g.DrawString(ch.ToString(), font, textBrush, cx, cy);
                    cy += sz.Height;
                }
            }

            private Color GetCaptionBrushColor()
            {
                if (_isOffline)
                    return Color.FromArgb(230, 230, 230);

                return _isOn ? Color.FromArgb(28, 28, 28) : Color.FromArgb(36, 36, 36);
            }

            private Color GetLampColor()
            {
                if (_isOffline)
                    return Color.DarkGray;

                if (!_isOn)
                    return Color.Silver;

                return _redWhenTriggered ? Color.FromArgb(220, 38, 38) : Color.LimeGreen;
            }
        }
    }
}
