using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.ComponentModel;
using System.Windows.Forms.DataVisualization.Charting;
using ExcelDataReader;
using Sunny.UI;
using HB_CameraUI;

namespace PlcConectCom.WinFormsTest;

// 系统用户角色
public enum UserRole
{
    管理员,
    工程师,
    作业员
}

// 主窗体：负责页面导航、权限管理、PLC连接与报警展示
public partial class HB_HMI_MainForm : UIForm
{
    // 报警历史记录模型
    private sealed class AlarmHistoryRecord
    {
        public DateTime AlarmTime { get; set; }

        public string AlarmName { get; set; } = string.Empty;

        public string AlarmCode { get; set; } = string.Empty;
    }

    private sealed class ProductionRecord
    {
        public DateTime ProducedAt { get; set; }

        public int Quantity { get; set; }
    }

    // 角色权限配置模型
    private sealed class RoleAuthConfig
    {
        public string AdminPassword { get; set; } = "admin123";

        public string EngineerPassword { get; set; } = "engineer123";
    }

    // 主 PLC（Modbus TCP）连接参数，由程序目录下 MainPlc.config.json 提供（含 IP 与心跳 BOOL 地址）
    private sealed class MainPlcConfig
    {
        public string IpAddress { get; set; } = "192.168.1.88";

        public int Port { get; set; } = 502;

        public int SlaveId { get; set; } = 1;


        /// <summary>PLC 心跳 BOOL 地址（如 MB100）。空则连接后不做双写握手，探测仍读 MB0。</summary>
        public string HeartbeatBoolAddress { get; set; } = string.Empty;
    }

    private global::PlcConectCom.PlcCommunicator? _plc;
    private const string MainPlcConfigFileName = "MainPlc.config.json";
    private const string AuthConfigFileName = "RoleAuth.config.json";
    private const string RemoteKeySignSecret = "PLC_ROLE_AUTH_2026";
    private static readonly JsonSerializerOptions JsonFileOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 1000 };
    private readonly StatusStrip _statusStrip = new() { Dock = DockStyle.Bottom };
    private readonly ToolStripStatusLabel _lblPlcStatus = new() { Text = "PLC: 未连接" };
    private readonly ToolStripStatusLabel _lblReconnectFail = new() { Text = "失败重连次数: 0" };
    private readonly ToolStripStatusLabel _lblHeartbeat = new() { Text = "心跳: ●", ForeColor = Color.DimGray };
    private int _failedReconnectCount;
    private bool _lastConnectedState;
    private bool _heartbeatFlash;
    private bool _disconnectAlreadyCounted;
    private bool _statusProbeBusy;
    private bool _refreshingRealtimeBoolLabels;
    private bool _isConnecting;
    private bool _autoConnectTriggered;
    private bool _mainPageAutoOpenedOnStartup;
    private bool _suspendRoleChanged;
    private UserRole _currentRole = UserRole.作业员;
    private readonly string _authConfigPath;
    private readonly string _mainPlcConfigPath;
    private RoleAuthConfig _authConfig = new();
    private MainPlcConfig _mainPlcConfig = new();

    // 导航区：页面切换与权限管理控件
    private readonly Panel _navPanel = new()
    {
        Dock = DockStyle.Left,
        Width = 188,
        Padding = new Padding(10, 12, 10, 12),
        BackColor = Color.FromArgb(29, 40, 56)
    };
    private readonly FlowLayoutPanel _navLeftPanel = new()
    {
        Dock = DockStyle.Fill,
        WrapContents = false,
        AutoScroll = true,
        FlowDirection = FlowDirection.TopDown,
        Padding = new Padding(0, 4, 0, 0)
    };
    private readonly FlowLayoutPanel _navRightPanel = new()
    {
        Dock = DockStyle.Bottom,
        WrapContents = false,
        AutoSize = false,
        Height = 120,
        FlowDirection = FlowDirection.TopDown,
        Padding = new Padding(0, 8, 0, 0)
    };
    private readonly Label _lblUserRole = new() { Text = "用户等级", AutoSize = true, Padding = new Padding(2, 4, 0, 0) };
    private readonly UIButton _btnPageMain = new() { Text = "主页面", Width = 164, Height = 48, Margin = new Padding(0, 0, 0, 8) };
    private readonly UIButton _btnPageDebug = new() { Text = "系统配置", Width = 164, Height = 48, Margin = new Padding(0, 0, 0, 8) };
    private readonly UIButton _btnPageDebugTools = new() { Text = "调试按钮", Width = 164, Height = 48, Margin = new Padding(0, 0, 0, 8) };
    private readonly UIButton _btnPageCameraDebug = new() { Text = "相机调试", Width = 164, Height = 48, Margin = new Padding(0, 0, 0, 8) };
    private readonly UIButton _btnPageMonitor = new() { Text = "状态监控", Width = 164, Height = 48, Margin = new Padding(0, 0, 0, 8) };
    private readonly UIButton _btnPageRecords = new() { Text = "记录查询", Width = 164, Height = 48, Margin = new Padding(0, 0, 0, 8) };
    private readonly UIComboBox _cmbUserRole = new() { Width = 164, DropDownStyle = UIDropDownStyle.DropDownList };
    private readonly UIButton _btnPasswordManage = new() { Text = "密码管理", Width = 164, Height = 34, Margin = new Padding(0, 8, 0, 0) };
    private readonly UIButton _btnButtonAreaLayoutEdit = new() { Text = "按钮区布局编辑", Width = 164, Height = 34, Margin = new Padding(0, 8, 0, 0) };

    // 页面容器：静态挂载的四个页面
    private readonly Panel _pageHost = new() { Dock = DockStyle.Fill };
    private readonly Panel _mainPage = new() { Dock = DockStyle.Fill };
    private readonly Panel _debugPage = new() { Dock = DockStyle.Fill };
    private readonly Panel _debugToolsPage = new() { Dock = DockStyle.Fill };
    private readonly UITabControl _debugToolsTab = new() { Dock = DockStyle.Fill };
    private readonly Panel _recordsPage = new() { Dock = DockStyle.Fill };
    // 记录查询页：日期筛选与导出控件
    private readonly DateTimePicker _dtRecordFrom = new() { Width = 140, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", ShowCheckBox = true, Checked = false };
    private readonly DateTimePicker _dtRecordTo = new() { Width = 140, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", ShowCheckBox = true, Checked = false };
    private readonly UIButton _btnRecordQuery = new() { Text = "日期查询", Width = 80 };
    private readonly UIButton _btnRecordAll = new() { Text = "全部", Width = 60 };
    private readonly UIButton _btnRecordExport = new() { Text = "导出", Width = 60 };
    private readonly SplitContainer _recordSplit = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 360 };
    private readonly Panel _recordAlarmSection = new() { Dock = DockStyle.Fill, Padding = new Padding(6), BackColor = Color.White };
    private readonly Panel _productionSection = new() { Dock = DockStyle.Fill, Padding = new Padding(6), BackColor = Color.White };
    private readonly Label _lblProductionTitle = new()
    {
        Text = "每日产量统计（白/夜班）",
        Dock = DockStyle.Top,
        Height = 32,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold)
    };
    private readonly FlowLayoutPanel _productionToolbar = new() { Dock = DockStyle.Top, Height = 42, Padding = new Padding(6), WrapContents = false };
    private readonly ComboBox _cmbProductionPeriod = new() { Width = 80, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly DateTimePicker _dtProductionAnchor = new() { Width = 130, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd" };
    private readonly DateTimePicker _tmDayShiftStart = new() { Width = 80, Format = DateTimePickerFormat.Time, ShowUpDown = true };
    private readonly DateTimePicker _tmDayShiftEnd = new() { Width = 80, Format = DateTimePickerFormat.Time, ShowUpDown = true };
    private readonly DateTimePicker _tmNightShiftStart = new() { Width = 80, Format = DateTimePickerFormat.Time, ShowUpDown = true };
    private readonly DateTimePicker _tmNightShiftEnd = new() { Width = 80, Format = DateTimePickerFormat.Time, ShowUpDown = true };
    private readonly UIButton _btnRefreshProductionChart = new() { Text = "刷新趋势图", Width = 96 };
    private readonly CheckBox _chkProductionAutoCollect = new() { Text = "PLC自动采集", AutoSize = true, Checked = true, Padding = new Padding(10, 8, 0, 0) };
    private readonly UITextBox _txtProductionCounterAddress = new() { Width = 90, Text = "MW100" };
    private readonly Chart _productionChart = new() { Dock = DockStyle.Fill };
    private bool _productionChartAttached;
    // 报警记录历史表格
    private readonly UIDataGridView _recordAlarmGrid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        RowHeadersVisible = false,
        ReadOnly = true,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
    };
    // 主页面实时报警表格
    private readonly UIDataGridView _mainAlarmGrid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        RowHeadersVisible = false,
        ReadOnly = true,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false
    };
    // 报警统计 Top3 标签集合
    private Label[] _topAlarmLabels = [];
    private readonly Dictionary<string, (string name, string code, int count)> _alarmStats = [];
    private readonly List<AlarmHistoryRecord> _alarmHistory = [];
    private readonly List<ProductionRecord> _productionRecords = [];
    private readonly System.Windows.Forms.Timer _productionPollTimer = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer _processRealtimeMainRebuildDebounce = new() { Interval = 400 };
    private int _lastProductionCounter = -1;
    private bool _productionPollBusy;
    // 主页面实时工艺数据容器
    private readonly FlowLayoutPanel _mainRealtimePanel = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false, FlowDirection = FlowDirection.TopDown };
    private readonly Panel _mainTopOverlayHost = new() { Dock = DockStyle.Top, Height = 210, Padding = new Padding(8, 8, 8, 4), BackColor = Color.Transparent };
    private readonly Panel _mainTopOverlayCanvas = new() { Dock = DockStyle.Fill, BackColor = Color.Transparent };
    private readonly Panel _mainButtonDisplaySection = new() { Width = 560, Height = 180, Padding = new Padding(0, 0, 0, 6), BorderStyle = BorderStyle.FixedSingle };
    private readonly Label _lblMainButtonDisplayTitle = new()
    {
        Text = "按钮显示区",
        Dock = DockStyle.Top,
        Height = 26,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold)
    };
    private readonly FlowLayoutPanel _mainButtonDisplayPanel = new()
    {
        Dock = DockStyle.Fill,
        WrapContents = false,
        AutoScroll = true,
        FlowDirection = FlowDirection.TopDown,
        Padding = new Padding(4)
    };
    private readonly Panel _mainPlcBoolLabelSection = new() { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 0, 6) };
    private readonly Label _lblMainPlcBoolLabelTitle = new()
    {
        Text = "PLC BoolLabel 显示区",
        Dock = DockStyle.Top,
        Height = 26,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold)
    };
    private readonly FlowLayoutPanel _mainPlcBoolLabelPanel = new()
    {
        Dock = DockStyle.Fill,
        WrapContents = true,
        AutoScroll = true,
        FlowDirection = FlowDirection.LeftToRight,
        Padding = new Padding(4)
    };
    private readonly Panel _mainPlcValueTextBoxSection = new() { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 0, 6) };
    private readonly Label _lblMainPlcValueTextBoxTitle = new()
    {
        Text = "PLC ValueTextBox 显示区",
        Dock = DockStyle.Top,
        Height = 26,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold)
    };
    private readonly FlowLayoutPanel _mainPlcValueTextBoxPanel = new()
    {
        Dock = DockStyle.Fill,
        WrapContents = true,
        AutoScroll = true,
        FlowDirection = FlowDirection.LeftToRight,
        Padding = new Padding(4)
    };
    private readonly List<PlcBoolLabe> _mainPlcBoolLabelControls = [];
    private readonly List<PlcValueTextBoxControl> _mainPlcValueTextBoxControls = [];
    private readonly List<PlcBoolSwitchControl> _mainBoolSwitchControls = [];
    private readonly List<PlcBoundButton> _mainBoundButtons = [];
    private readonly List<PlcBoolLabelBinding> _realtimeBoolLabelBindings = [];
    private bool _refreshingMainButtons;
    private bool _buttonAreaLayoutEditMode;
    private bool _draggingButtonArea;
    private Point _buttonAreaDragOffset;
    private readonly string _buttonAreaLayoutPath = Path.Combine(AppContext.BaseDirectory, "MainButtonArea.layout.json");
    private sealed class MainButtonAreaLayout
    {
        public int X { get; set; }
        public int Y { get; set; }
    }
    private readonly SplitContainer _mainDataSplit = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 240 };
    private readonly Panel _stationAreaPanel = new() { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(6, 0, 6, 6), AutoScroll = true };
    private readonly FlowLayoutPanel _stationDisplayPanel = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(4) };
    private readonly List<StationOperationStatusControl> _stationControls = [];
    private const int StationDisplayColumns = 6;
    private const int MainAlarmListHeight = 280;

    // 调试与监控页面核心功能控件
    private readonly ParameterTableControl _parameterTableControl = new() { Dock = DockStyle.Fill };
    // 调试页面新增：HMI显示数据参数表
    private readonly ParameterTableControl _hmiDisplayParameterTableControl = new("HmiDisplayData") { Dock = DockStyle.Fill };
    // 调试页面新增：按钮类变量参数表
    private readonly ParameterTableControl _buttonVarParameterTableControl = new("ButtonVariables") { Dock = DockStyle.Fill };
    private readonly ProcessRealtimeDataConfigControl _processRealtimeDataConfigControl = new() { Dock = DockStyle.Fill };
    private readonly UIButton _btnImportParameterData = new() { Text = "导入数据", Width = 96, Height = 30 };
    private readonly UIButton _btnImportHmiDisplayData = new() { Text = "导入数据", Width = 96, Height = 30 };
    private readonly UIButton _btnImportButtonVarData = new() { Text = "导入数据", Width = 96, Height = 30 };
    private readonly UIButton _btnImportBoolGroupData = new() { Text = "导入数据", Width = 96, Height = 30 };
    private readonly UIButton _btnImportAlarmConfigData = new() { Text = "导入数据", Width = 96, Height = 30 };
    private readonly UIButton _btnImportAlarmPopupData = new() { Text = "导入数据", Width = 96, Height = 30 };
    private readonly AxisMotionGroupTableControl _axisMotionControl;
    private readonly PositionParameterTableControl _positionParameterTableControl = new() { Dock = DockStyle.Fill };
    private readonly BoolSignalCollectionControl _boolSignalControl = new() { Dock = DockStyle.Fill };
    private readonly BoolGroupControlTable _boolGroupControlTable = new() { Dock = DockStyle.Fill };
    private readonly AlarmAddressManagerControl _alarmAddressManagerControl = new() { Dock = DockStyle.Fill };
    private readonly TabPage _tabAlarmPopupConfig = new() { Text = "报警弹窗配置", Padding = new Padding(2) };
    private readonly AlarmPopupManagerControl _alarmPopupManagerControl = new() { Dock = DockStyle.Fill };
    private readonly TaskSettingsControl _taskSettingsControl = new() { Dock = DockStyle.Fill };
    private readonly CommunicationConfigControl _communicationConfigControl = new() { Dock = DockStyle.Fill };
    private TaskPipelineRunner? _taskPipelineRunner;
    private readonly TabPage _tabStationConfig = new() { Text = "工站配置", Padding = new Padding(2) };
    private readonly StationOperationConfigManagerControl _stationConfigControl = new() { Dock = DockStyle.Fill };
    private readonly TabPage _tabButtonDisplayConfig = new() { Text = "按钮显示配置", Padding = new Padding(2) };
    private readonly TabPage _tabProcessRealtimeData = new() { Text = "工艺实时数据配置", Padding = new Padding(2) };
    private readonly TabPage _tabPlcBoolLabelDisplayConfig = new() { Text = "显示状态控件配置", Padding = new Padding(2) };
    private readonly TabPage _tabPlcValueTextBoxDisplayConfig = new() { Text = "文本显示控件配置", Padding = new Padding(2) };
    private readonly ButtonDisplayConfigControl _buttonDisplayConfigControl = new() { Dock = DockStyle.Fill };
    private readonly PlcBoolLabelDisplayConfigControl _plcBoolLabelDisplayConfigControl = new() { Dock = DockStyle.Fill };
    private readonly PlcValueTextBoxDisplayConfigControl _plcValueTextBoxDisplayConfigControl = new() { Dock = DockStyle.Fill };
    // 底部运行日志文本框
    private readonly TextBox _txtLog = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, ReadOnly = true, Dock = DockStyle.Bottom, Height = 180 };
    private MainPageWindow? _mainPageWindow;
    private SystemConfigPageWindow? _systemConfigPageWindow;
    private DebugToolsPageWindow? _debugToolsPageWindow;
    private Form? _cameraDebugWindow;
    /// <summary>由「相机调试」THCamSetHostForm 绑定的相机控件，用于线程状态面板。</summary>
    private TH_CamSet? _boundThCamSet;
    private readonly TabPage _tabCameraThreads = new() { Text = "相机线程", Padding = new Padding(6) };
    private readonly Label _lblCameraThreadHint = new()
    {
        Dock = DockStyle.Top,
        Height = 44,
        Padding = new Padding(4, 6, 4, 4),
        Text = "请先打开顶部「相机调试」窗口以绑定数据。Cam3~6 的 movY、movR 为两侧均值，便于与排版相机对齐显示。"
    };
    private readonly TableLayoutPanel _cameraThreadTable = new()
    {
        Dock = DockStyle.Fill,
        Padding = new Padding(4)
    };
    private readonly CheckBox[] _cameraThreadPauseChecks = new CheckBox[6];
    private readonly Label[] _cameraThreadAliveLabels = new Label[6];
    private readonly Label[,] _cameraThreadValueLabels = new Label[6, 4];
    private bool _syncingCameraPauseChecks;
    private readonly System.Windows.Forms.Timer _cameraThreadUiTimer = new() { Interval = 300 };
    private bool _cameraWorkThreadsStartedOnStartup;
    private MonitorPageWindow? _monitorPageWindow;
    private RecordsPageWindow? _recordsPageWindow;

    private sealed record PlcBoolLabelBinding(Control Label, string Address, string Name);
    private sealed record PlcBoolLabelBindingConfig(Control Label, string Address, string DisplayName);

    public HB_HMI_MainForm()
    {
        _authConfigPath = Path.Combine(AppContext.BaseDirectory, AuthConfigFileName);
        _mainPlcConfigPath = Path.Combine(AppContext.BaseDirectory, MainPlcConfigFileName);
        InitializeComponent();
        // 必须在 InitializeComponent 之后再构造：避免在 UIForm/句柄树未就绪时创建含 DataGridView 的复杂控件导致启动失败
        _axisMotionControl = new AxisMotionGroupTableControl { Dock = DockStyle.Fill };
        if (IsDesignHost())
        {
            BuildDesignerPreviewUi();
            return;
        }

        LoadAuthConfig();
        LoadMainPlcConfig();
        ApplySunnyUiTheme();
        ApplySunnyUiControlStyles();
        BuildUi();
        BindEvents();
    }

    private static bool IsDesignHost()
    {
        return LicenseManager.UsageMode == LicenseUsageMode.Designtime;
    }

    private void BuildDesignerPreviewUi()
    {
        ApplySunnyUiTheme();
        ApplySunnyUiControlStyles();
        ComposeStaticLayout();
        RebuildMainProcessRealtimeDisplays();
        ShowPage(_mainPage);
    }

    private void ApplySunnyUiTheme()
    {
        Style = UIStyle.Blue;
        TitleColor = Color.FromArgb(52, 103, 235);
        TitleForeColor = Color.White;
        RectColor = Color.FromArgb(52, 103, 235);
        Text = "PLC 运行平台";
    }

    private void ApplySunnyUiControlStyles()
    {
        foreach (var button in GetUiButtons())
        {
            button.Style = UIStyle.Blue;
            button.Radius = 6;
            button.FillColor = Color.FromArgb(56, 81, 110);
            button.FillHoverColor = Color.FromArgb(68, 98, 132);
            button.FillPressColor = Color.FromArgb(43, 67, 94);
            button.RectColor = Color.FromArgb(86, 116, 150);
        }

        _cmbUserRole.Style = UIStyle.Blue;
        _cmbUserRole.FillColor = Color.FromArgb(240, 246, 255);
        _cmbUserRole.RectColor = Color.FromArgb(86, 116, 150);

        ConfigureAlarmGridStyle(_mainAlarmGrid);
        ConfigureAlarmGridStyle(_recordAlarmGrid);
        ApplyMainLayoutStyle();
    }

    private IEnumerable<UIButton> GetUiButtons()
    {
        yield return _btnPageMain;
        yield return _btnPageDebug;
        yield return _btnPageDebugTools;
        yield return _btnPageCameraDebug;
        yield return _btnPageMonitor;
        yield return _btnPageRecords;
        yield return _btnPasswordManage;
        yield return _btnRecordQuery;
        yield return _btnRecordAll;
        yield return _btnRecordExport;
        yield return _btnImportParameterData;
        yield return _btnImportHmiDisplayData;
        yield return _btnImportButtonVarData;
        yield return _btnImportBoolGroupData;
        yield return _btnImportAlarmConfigData;
        yield return _btnImportAlarmPopupData;
        yield return _btnRefreshProductionChart;
    }

    private static void ConfigureAlarmGridStyle(UIDataGridView grid)
    {
        grid.Style = UIStyle.Blue;
        grid.StripeOddColor = Color.FromArgb(248, 250, 255);
        grid.StripeEvenColor = Color.White;
        grid.GridColor = Color.FromArgb(225, 232, 245);
        grid.RowTemplate.Height = 32;
    }

    private void ApplyMainLayoutStyle()
    {
        BackColor = Color.FromArgb(241, 245, 252);
        _pageHost.BackColor = Color.FromArgb(241, 245, 252);
        _navPanel.BackColor = Color.FromArgb(29, 40, 56);
        _navLeftPanel.BackColor = Color.Transparent;
        _navRightPanel.BackColor = Color.Transparent;
        _lblUserRole.ForeColor = Color.FromArgb(194, 212, 236);
        _lblUserRole.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);

        _mainPage.BackColor = Color.FromArgb(241, 245, 252);
        _mainPage.Padding = new Padding(8);

        _debugPage.BackColor = Color.FromArgb(241, 245, 252);
        _debugPage.Padding = new Padding(8);

        _recordsPage.BackColor = Color.FromArgb(241, 245, 252);
        _recordsPage.Padding = new Padding(8);

        _recordAlarmSection.BackColor = Color.White;
        _productionSection.BackColor = Color.White;

        _stationAreaPanel.BackColor = Color.White;
        _stationAreaPanel.Padding = new Padding(6, 0, 6, 6);

        _mainLeftPanel.BackColor = Color.White;
        _mainRightPanel.BackColor = Color.White;
        _mainLeftPanel.Margin = new Padding(4);
        _mainRightPanel.Margin = new Padding(4);
        _mainLeftPanel.Padding = new Padding(10);
        _mainRightPanel.Padding = new Padding(10);

        _lblMainAlarmTitle.ForeColor = Color.FromArgb(28, 58, 120);
        _lblTopAlarmTitle.ForeColor = Color.FromArgb(28, 58, 120);
        _lblRealtimeDataTitle.ForeColor = Color.FromArgb(28, 58, 120);

        ConfigureTopAlarmCards();
        ConfigureLogStyle();
    }

    private void ConfigureTopAlarmCards()
    {
        Color[] backgrounds =
        [
            Color.FromArgb(255, 237, 240),
            Color.FromArgb(255, 245, 230),
            Color.FromArgb(255, 251, 232)
        ];
        Color[] foregrounds =
        [
            Color.Firebrick,
            Color.DarkOrange,
            Color.DarkGoldenrod
        ];

        for (int i = 0; i < _topAlarmLabels.Length; i++)
        {
            _topAlarmLabels[i].BackColor = backgrounds[i];
            _topAlarmLabels[i].ForeColor = foregrounds[i];
            _topAlarmLabels[i].Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            _topAlarmLabels[i].Padding = new Padding(10, 0, 0, 0);
            _topAlarmLabels[i].Height = 40;
            _topAlarmLabels[i].Margin = new Padding(0, i == 0 ? 6 : 4, 0, 0);
            _topAlarmLabels[i].BorderStyle = BorderStyle.FixedSingle;
        }
    }

    private void ConfigureLogStyle()
    {
        _txtLog.BackColor = Color.FromArgb(20, 31, 48);
        _txtLog.ForeColor = Color.FromArgb(199, 220, 255);
        _txtLog.Font = new Font("Consolas", 10F, FontStyle.Regular);
        _txtLog.BorderStyle = BorderStyle.FixedSingle;
        _txtLog.Padding = new Padding(8);
    }

    private void BuildUi()
    {
        // 组装主界面静态布局（页面容器、导航栏、页内控件挂载）
        ComposeStaticLayout();

        // 绑定设计器中的 Top3 标签，后续用于报警排行显示
        _topAlarmLabels = [_lblTopAlarm1, _lblTopAlarm2, _lblTopAlarm3];

        if (_cmbUserRole.Items.Count == 0)
            _cmbUserRole.Items.AddRange(Enum.GetNames<UserRole>());
        _cmbUserRole.SelectedItem = _currentRole.ToString();

        // 初始化报警表格列（避免重复添加）
        EnsureAlarmGridColumns(_mainAlarmGrid, 30, 50, 20);
        EnsureAlarmGridColumns(_recordAlarmGrid, 30, 45, 25);
        InitializeProductionStatisticsUi();

        if (_statusStrip.Items.Count == 0)
        {
            _statusStrip.Items.Add(_lblPlcStatus);
            _statusStrip.Items.Add(new ToolStripSeparator());
            _statusStrip.Items.Add(_lblReconnectFail);
            _statusStrip.Items.Add(new ToolStripSeparator());
            _statusStrip.Items.Add(_lblHeartbeat);
        }

        ConfigureParameterTableControl(_parameterTableControl);
        ConfigureParameterTableControl(_hmiDisplayParameterTableControl);
        ConfigureParameterTableControl(_buttonVarParameterTableControl);
        ConfigureProcessRealtimeDataConfigControl(_processRealtimeDataConfigControl);

        _axisMotionControl.PlcProvider = () => _plc;
        _axisMotionControl.LogAction = AppendLog;


        _axisMotionControl.EditPermissionRequest = RequestTableEditAuthorization;
        if (_axisMotionControl.GetAxes().Count == 0)
            _axisMotionControl.SetAxes([new AxisMotionAddressConfig { AxisId = "Axis1" }]);
        _positionParameterTableControl.LogAction = AppendLog;
        _positionParameterTableControl.AxisNameProvider = () => _axisMotionControl.GetAxisNames();
        _positionParameterTableControl.PlcProvider = () => _plc;
        _positionParameterTableControl.EditPermissionRequest = RequestTableEditAuthorization;
        _positionParameterTableControl.AxisFieldAddressResolver = ResolveAxisFieldAddress;

        _boolSignalControl.PlcProvider = () => _plc;
        _boolSignalControl.LogAction = AppendLog;
        _boolSignalControl.EditPermissionRequest = RequestTableEditAuthorization;

        _boolGroupControlTable.PlcProvider = () => _plc;
        _boolGroupControlTable.LogAction = AppendLog;
        _boolGroupControlTable.EditPermissionRequest = RequestTableEditAuthorization;

        _alarmAddressManagerControl.PlcProvider = () => _plc;
        _alarmAddressManagerControl.LogAction = AppendLog;
        _alarmAddressManagerControl.EditPermissionRequest = RequestTableEditAuthorization;
        _alarmAddressManagerControl.AlarmTriggered += HandleAlarmTriggered;

        _alarmPopupManagerControl.PlcProvider = () => _plc;
        _alarmPopupManagerControl.LogAction = AppendLog;
        _alarmPopupManagerControl.EditPermissionRequest = RequestTableEditAuthorization;

        //plcBoolLabe1.PlcProvider = () => _plc;
        //plcBoolLabe1.LogAction = AppendLog;

        //plcBoolLabe2.PlcProvider = () => _plc;
        //plcBoolLabe2.LogAction = AppendLog;

        //plcBoolSwitchControl1.PlcProvider = () => _plc;
        //plcBoolSwitchControl1.LogAction = AppendLog;
        //plcBoolSwitchControl2.PlcProvider = () => _plc;
        //plcBoolSwitchControl2.LogAction = AppendLog;
      

        _taskSettingsControl.LogAction = AppendLog;
        _taskSettingsControl.EditPermissionRequest = RequestTableEditAuthorization;
        _taskSettingsControl.DiAddressProvider = () => _boolSignalControl.GetPoints()
            .Where(x => !string.IsNullOrWhiteSpace(x.Address))
            .Select(x => new TaskSourceAddressOption { Name = x.Name, Address = x.Address })
            .ToList();
        _taskSettingsControl.DoAddressProvider = () => _boolGroupControlTable.GetItems()
            .Where(x => !string.IsNullOrWhiteSpace(x.Address))
            .Select(x => new TaskSourceAddressOption { Name = x.Name, Address = x.Address })
            .ToList();
        _taskSettingsControl.ButtonVarAddressProvider = () => _buttonVarParameterTableControl.GetParameterNameAddressPairs()
            .Select(x => new TaskSourceAddressOption { Name = x.Key, Address = x.Value })
            .ToList();
        _taskSettingsControl.AlarmAddressProvider = () => _alarmAddressManagerControl.GetItems()
            .Where(x => !string.IsNullOrWhiteSpace(x.AlarmAddress))
            .Select(x => new TaskSourceAddressOption { Name = x.AlarmName, Address = x.AlarmAddress })
            .ToList();
        _taskSettingsControl.SocketInstanceProvider = () => _communicationConfigControl.GetSocketInstanceOptions();
        _taskSettingsControl.SocketResultFieldNamesProvider = key => _communicationConfigControl.GetSocketResultFieldNames(key);
        _taskSettingsControl.SerialInstanceProvider = () => _communicationConfigControl.GetSerialInstanceOptions();
        _taskSettingsControl.ExecuteTaskActionAsync = execution => _communicationConfigControl.ExecuteTaskActionAsync(execution);
        _taskSettingsControl.ParameterTableAddressProvider = () =>
            _parameterTableControl.GetParameterNameAddressPairs()
                .Select(kv => new TaskSourceAddressOption { Name = kv.Key, Address = kv.Value })
                .ToList();
        _taskSettingsControl.HmiDisplayAddressProvider = () =>
            _hmiDisplayParameterTableControl.GetParameterNameAddressPairs()
                .Select(kv => new TaskSourceAddressOption { Name = kv.Key, Address = kv.Value })
                .ToList();

        _communicationConfigControl.PlcProvider = () => _plc;
        _taskPipelineRunner = new TaskPipelineRunner(
            () => _plc,
            ex => _communicationConfigControl.ExecuteTaskActionAsync(ex),
            () => _taskSettingsControl.GetAutomatedTasks(),
            AppendLog,
            null);
        _taskSettingsControl.BindRunner(_taskPipelineRunner);
        _alarmAddressManagerControl.AlarmTriggered += alarm =>
            _taskPipelineRunner?.OnAlarmTriggered(alarm.AlarmCode, alarm.AlarmName);

        _communicationConfigControl.LogAction = AppendLog;
        _communicationConfigControl.EditPermissionRequest = RequestTableEditAuthorization;

        _stationConfigControl.LogAction = AppendLog;
        _stationConfigControl.EditPermissionRequest = RequestTableEditAuthorization;
        _stationConfigControl.ConfigurationsChanged += HandleStationConfigurationsChanged;
        _buttonDisplayConfigControl.LogAction = AppendLog;
        _buttonDisplayConfigControl.EditPermissionRequest = RequestTableEditAuthorization;
        _buttonDisplayConfigControl.ConfigurationsChanged += HandleButtonDisplayConfigurationsChanged;
        _plcBoolLabelDisplayConfigControl.LogAction = AppendLog;
        _plcBoolLabelDisplayConfigControl.EditPermissionRequest = RequestTableEditAuthorization;
        _plcBoolLabelDisplayConfigControl.ConfigurationsChanged += HandlePlcBoolLabelDisplayConfigurationsChanged;
        _plcValueTextBoxDisplayConfigControl.LogAction = AppendLog;
        _plcValueTextBoxDisplayConfigControl.EditPermissionRequest = RequestTableEditAuthorization;
        _plcValueTextBoxDisplayConfigControl.ConfigurationsChanged += HandlePlcValueTextBoxDisplayConfigurationsChanged;

        if (!_tabTaskSettings.Controls.Contains(_taskSettingsControl))
            _tabTaskSettings.Controls.Add(_taskSettingsControl);
        if (!_tabCommunicationConfig.Controls.Contains(_communicationConfigControl))
            _tabCommunicationConfig.Controls.Add(_communicationConfigControl);
        if (!_tabStationConfig.Controls.Contains(_stationConfigControl))
            _tabStationConfig.Controls.Add(_stationConfigControl);
        if (!_tabButtonDisplayConfig.Controls.Contains(_buttonDisplayConfigControl))
            _tabButtonDisplayConfig.Controls.Add(_buttonDisplayConfigControl);
        if (!_tabPlcBoolLabelDisplayConfig.Controls.Contains(_plcBoolLabelDisplayConfigControl))
            _tabPlcBoolLabelDisplayConfig.Controls.Add(_plcBoolLabelDisplayConfigControl);
        if (!_tabPlcValueTextBoxDisplayConfig.Controls.Contains(_plcValueTextBoxDisplayConfigControl))
            _tabPlcValueTextBoxDisplayConfig.Controls.Add(_plcValueTextBoxDisplayConfigControl);

        RebuildStationControls(_stationConfigControl.GetConfigurations());
        RebuildMainButtonDisplayControls(_buttonDisplayConfigControl.GetConfigurations());
        RebuildMainPlcBoolLabelDisplays(_plcBoolLabelDisplayConfigControl.GetConfigurations());
        RebuildMainPlcValueTextBoxDisplays(_plcValueTextBoxDisplayConfigControl.GetConfigurations());

        RebuildMainProcessRealtimeDisplays();
        InitializeRealtimeBoolLabelBindings();

        ApplyRolePermissions();
        HideAllStaticPages();
        UpdateNavigationButtonState((UIButton?)null);
        ShowPage(_mainPage);
        RefreshTopAlarmLabels();
        RefreshProductionChart();
    }

    // 静态UI装配：修复启动后页面未显示的问题
    private void ComposeStaticLayout()
    {
        SuspendLayout();
        try
        {
            ComposeNavigationBar();
            ComposeMainPage();
            ComposeDebugPage();
            ComposeRecordsPage();
            ComposeRootContainers();
        }
        finally
        {
            ResumeLayout();
        }
    }

    // 顶部导航栏按钮与权限控件挂载
    private void ComposeNavigationBar()
    {
        EnsureControlAdded(_navPanel, _navLeftPanel);
        EnsureControlAdded(_navPanel, _navRightPanel);

        EnsureControlAdded(_navLeftPanel, _btnPageMain);
        EnsureControlAdded(_navLeftPanel, _btnPageDebug);
        EnsureControlAdded(_navLeftPanel, _btnPageDebugTools);
        EnsureControlAdded(_navLeftPanel, _btnPageCameraDebug);
        EnsureControlAdded(_navLeftPanel, _btnPageMonitor);
        EnsureControlAdded(_navLeftPanel, _btnPageRecords);

        EnsureControlAdded(_navRightPanel, _lblUserRole);
        EnsureControlAdded(_navRightPanel, _cmbUserRole);
        EnsureControlAdded(_navRightPanel, _btnPasswordManage);
    }

    // 主页面内容挂载
    private void ComposeMainPage()
    {
        ConfigureMainLeftLayout();
        EnsureControlAdded(_stationAreaPanel, _stationDisplayPanel);
        EnsureControlAdded(_mainLeftPanel, _stationAreaPanel, 0, 1);
        EnsureControlAdded(_mainPlcValueTextBoxSection, _mainPlcValueTextBoxPanel);
        EnsureControlAdded(_mainPlcValueTextBoxSection, _lblMainPlcValueTextBoxTitle);
        EnsureControlAdded(_mainLeftPanel, _mainPlcValueTextBoxSection, 0, 2);
        EnsureControlAdded(_mainPlcBoolLabelSection, _mainPlcBoolLabelPanel);
        EnsureControlAdded(_mainPlcBoolLabelSection, _lblMainPlcBoolLabelTitle);
        EnsureControlAdded(_mainLeftPanel, _mainPlcBoolLabelSection, 0, 3);
        EnsureControlAdded(_mainLeftPanel, _mainAlarmGrid, 0, 4);

        EnsureControlAdded(_mainDataSplit.Panel1, _mainRealtimePanel);
        _mainDataSplit.Panel2Collapsed = true;
        EnsureControlAdded(_mainRightPanel, _mainDataSplit, 0, 5);

        EnsureControlAdded(_mainPage, _mainSplit);

        EnsureControlAdded(_mainButtonDisplaySection, _mainButtonDisplayPanel);
        EnsureControlAdded(_mainButtonDisplaySection, _lblMainButtonDisplayTitle);
        EnsureControlAdded(_mainRealtimePanel, _mainButtonDisplaySection);
        _mainRealtimePanel.Controls.SetChildIndex(_mainButtonDisplaySection, _mainRealtimePanel.Controls.Count - 1);
    }

    private void ConfigureMainLeftLayout()
    {
        _lblMainAlarmTitle.Text = "工站控制 / 实时报警";
        _mainLeftPanel.RowCount = 3;
        const int mainPlcValueTextBoxSectionHeight = 100;
        const int mainPlcBoolLabelSectionHeight = 100;
        _mainLeftPanel.RowStyles.Clear();
        _mainLeftPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
        _mainLeftPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _mainLeftPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, mainPlcValueTextBoxSectionHeight));
        _mainLeftPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, mainPlcBoolLabelSectionHeight));
        _mainLeftPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, MainAlarmListHeight));
        _mainLeftPanel.RowCount = 5;
    }

    private Point GetDefaultButtonAreaLocation()
    {
        int x = Math.Max(0, _mainTopOverlayCanvas.ClientSize.Width - _mainButtonDisplaySection.Width - 8);
        const int y = 8;
        return new Point(x, y);
    }

    private void SnapMainButtonAreaToDefaultAnchor()
    {
        _mainButtonDisplaySection.Location = GetDefaultButtonAreaLocation();
    }

    private void ClampMainButtonAreaLocation()
    {
        int maxX = Math.Max(0, _mainTopOverlayCanvas.ClientSize.Width - _mainButtonDisplaySection.Width);
        int maxY = Math.Max(0, _mainTopOverlayCanvas.ClientSize.Height - _mainButtonDisplaySection.Height);
        Point loc = _mainButtonDisplaySection.Location;
        _mainButtonDisplaySection.Location = new Point(
            Math.Clamp(loc.X, 0, maxX),
            Math.Clamp(loc.Y, 0, maxY));
    }

    private void ApplySavedButtonAreaLocation()
    {
        try
        {
            if (!File.Exists(_buttonAreaLayoutPath))
            {
                SnapMainButtonAreaToDefaultAnchor();
                return;
            }

            string json = File.ReadAllText(_buttonAreaLayoutPath);
            var model = JsonSerializer.Deserialize<MainButtonAreaLayout>(json);
            if (model is null)
            {
                SnapMainButtonAreaToDefaultAnchor();
                return;
            }

            _mainButtonDisplaySection.Location = new Point(model.X, model.Y);
            ClampMainButtonAreaLocation();
        }
        catch
        {
            SnapMainButtonAreaToDefaultAnchor();
        }
    }

    private void SaveButtonAreaLayout()
    {
        try
        {
            var model = new MainButtonAreaLayout
            {
                X = _mainButtonDisplaySection.Left,
                Y = _mainButtonDisplaySection.Top
            };
            string json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_buttonAreaLayoutPath, json);
        }
        catch
        {
            // 布局保存失败不影响主流程
        }
    }

    private void ToggleMainButtonAreaLayoutEditMode()
    {
        if (!_buttonAreaLayoutEditMode)
        {
            bool ok = RequestTableEditAuthorization();
            if (!ok)
                return;
        }

        _buttonAreaLayoutEditMode = !_buttonAreaLayoutEditMode;
        _btnButtonAreaLayoutEdit.Text = _buttonAreaLayoutEditMode ? "完成按钮区布局" : "按钮区布局编辑";
        _lblMainButtonDisplayTitle.Cursor = _buttonAreaLayoutEditMode ? Cursors.SizeAll : Cursors.Default;
        _mainButtonDisplaySection.BackColor = _buttonAreaLayoutEditMode ? Color.FromArgb(245, 250, 255) : Color.Transparent;
        if (!_buttonAreaLayoutEditMode)
        {
            _draggingButtonArea = false;
            ClampMainButtonAreaLocation();
            SaveButtonAreaLayout();
        }
    }

    private void MainButtonAreaDragMouseDown(object? sender, MouseEventArgs e)
    {
        if (!_buttonAreaLayoutEditMode || e.Button != MouseButtons.Left)
            return;

        _draggingButtonArea = true;
        _buttonAreaDragOffset = e.Location;
    }

    private void MainButtonAreaDragMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_buttonAreaLayoutEditMode || !_draggingButtonArea)
            return;

        int targetX = _mainButtonDisplaySection.Left + (e.X - _buttonAreaDragOffset.X);
        int targetY = _mainButtonDisplaySection.Top + (e.Y - _buttonAreaDragOffset.Y);
        _mainButtonDisplaySection.Location = new Point(targetX, targetY);
        ClampMainButtonAreaLocation();
    }

    private void MainButtonAreaDragMouseUp(object? sender, MouseEventArgs e)
    {
        if (!_buttonAreaLayoutEditMode || e.Button != MouseButtons.Left)
            return;

        _draggingButtonArea = false;
        SaveButtonAreaLayout();
    }

    // 调试页面内容挂载
    private void ComposeDebugPage()
    {
        if (!_debugTab.TabPages.Contains(_tabStationConfig))
            _debugTab.TabPages.Add(_tabStationConfig);
        if (!_debugTab.TabPages.Contains(_tabButtonDisplayConfig))
            _debugTab.TabPages.Add(_tabButtonDisplayConfig);
        if (!_debugTab.TabPages.Contains(_tabPlcBoolLabelDisplayConfig))
            _debugTab.TabPages.Add(_tabPlcBoolLabelDisplayConfig);
        if (!_debugTab.TabPages.Contains(_tabPlcValueTextBoxDisplayConfig))
            _debugTab.TabPages.Add(_tabPlcValueTextBoxDisplayConfig);
        if (!_debugTab.TabPages.Contains(_tabProcessRealtimeData))
        {
            int buttonVarIndex = _debugTab.TabPages.IndexOf(_tabButtonVar);
            if (buttonVarIndex >= 0)
                _debugTab.TabPages.Insert(buttonVarIndex + 1, _tabProcessRealtimeData);
            else
                _debugTab.TabPages.Add(_tabProcessRealtimeData);
        }

        EnsureControlAdded(_tabParameter, _parameterTableControl);
        EnsureControlAdded(_tabHmiDisplayData, _hmiDisplayParameterTableControl);
        EnsureControlAdded(_tabButtonVar, _buttonVarParameterTableControl);
        EnsureControlAdded(_tabProcessRealtimeData, _processRealtimeDataConfigControl);
        EnsureControlAdded(_tabAlarmConfig, _alarmAddressManagerControl);
        int alarmTabIndex = _debugTab.TabPages.IndexOf(_tabAlarmConfig);
        if (alarmTabIndex >= 0)
        {
            if (!_debugTab.TabPages.Contains(_tabAlarmPopupConfig))
                _debugTab.TabPages.Insert(alarmTabIndex + 1, _tabAlarmPopupConfig);
        }
        else if (!_debugTab.TabPages.Contains(_tabAlarmPopupConfig))
        {
            _debugTab.TabPages.Add(_tabAlarmPopupConfig);
        }

        EnsureControlAdded(_tabAlarmPopupConfig, _alarmPopupManagerControl);
        EnsureControlAdded(_tabStationConfig, _stationConfigControl);
        EnsureControlAdded(_tabButtonDisplayConfig, _buttonDisplayConfigControl);
        EnsureControlAdded(_tabPlcBoolLabelDisplayConfig, _plcBoolLabelDisplayConfigControl);
        EnsureControlAdded(_tabPlcValueTextBoxDisplayConfig, _plcValueTextBoxDisplayConfigControl);

        EnsureControlAdded(_axisSplit.Panel1, _axisMotionControl);
        EnsureControlAdded(_axisSplit.Panel2, _positionParameterTableControl);
        EnsureControlAdded(_tabBoolGroup, _boolGroupControlTable);

        if (_debugTab.TabPages.Contains(_tabAxis))
            _debugTab.TabPages.Remove(_tabAxis);
        if (_debugTab.TabPages.Contains(_tabBoolGroup))
            _debugTab.TabPages.Remove(_tabBoolGroup);

        if (!_debugToolsTab.TabPages.Contains(_tabAxis))
            _debugToolsTab.TabPages.Add(_tabAxis);
        if (!_debugToolsTab.TabPages.Contains(_tabBoolGroup))
            _debugToolsTab.TabPages.Add(_tabBoolGroup);

        BuildCameraThreadDebugTab();
        if (!_debugToolsTab.TabPages.Contains(_tabCameraThreads))
            _debugToolsTab.TabPages.Add(_tabCameraThreads);

        EnsureControlAdded(_debugPage, _debugTab);
        EnsureControlAdded(_debugToolsPage, _debugToolsTab);

        _parameterTableControl.PlaceImportButtonAtEnd(_btnImportParameterData);
        _hmiDisplayParameterTableControl.PlaceImportButtonAtEnd(_btnImportHmiDisplayData);
        _buttonVarParameterTableControl.PlaceImportButtonAtEnd(_btnImportButtonVarData);
        _alarmAddressManagerControl.PlaceImportButtonAtEnd(_btnImportAlarmConfigData);
        _alarmPopupManagerControl.PlaceImportButtonAtEnd(_btnImportAlarmPopupData);
        _boolGroupControlTable.PlaceImportButtonAtEnd(_btnImportBoolGroupData);
    }

    // 记录查询页面内容挂载
    private void ComposeRecordsPage()
    {
        EnsureControlAdded(_recordToolbar, _dtRecordFrom);
        EnsureControlAdded(_recordToolbar, _dtRecordTo);
        EnsureControlAdded(_recordToolbar, _btnRecordQuery);
        EnsureControlAdded(_recordToolbar, _btnRecordAll);
        EnsureControlAdded(_recordToolbar, _btnRecordExport);
        EnsureControlAdded(_recordAlarmSection, _recordAlarmGrid);
        EnsureControlAdded(_recordAlarmSection, _recordToolbar);
        EnsureControlAdded(_recordAlarmSection, _recordHint);

        ComposeProductionToolbar();
        EnsureControlAdded(_productionSection, _productionToolbar);
        EnsureControlAdded(_productionSection, _lblProductionTitle);

        EnsureControlAdded(_recordSplit.Panel1, _recordAlarmSection);
        EnsureControlAdded(_recordSplit.Panel2, _productionSection);
        EnsureControlAdded(_recordsPage, _recordSplit);
    }

    private void ComposeProductionToolbar()
    {
        EnsureControlAdded(_productionToolbar, new Label { Text = "范围", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        EnsureControlAdded(_productionToolbar, _cmbProductionPeriod);
        EnsureControlAdded(_productionToolbar, new Label { Text = "基准日期", AutoSize = true, Padding = new Padding(10, 8, 0, 0) });
        EnsureControlAdded(_productionToolbar, _dtProductionAnchor);
        EnsureControlAdded(_productionToolbar, new Label { Text = "白班", AutoSize = true, Padding = new Padding(10, 8, 0, 0) });
        EnsureControlAdded(_productionToolbar, _tmDayShiftStart);
        EnsureControlAdded(_productionToolbar, new Label { Text = "-", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        EnsureControlAdded(_productionToolbar, _tmDayShiftEnd);
        EnsureControlAdded(_productionToolbar, new Label { Text = "夜班", AutoSize = true, Padding = new Padding(10, 8, 0, 0) });
        EnsureControlAdded(_productionToolbar, _tmNightShiftStart);
        EnsureControlAdded(_productionToolbar, new Label { Text = "-", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        EnsureControlAdded(_productionToolbar, _tmNightShiftEnd);
        EnsureControlAdded(_productionToolbar, _chkProductionAutoCollect);
        EnsureControlAdded(_productionToolbar, new Label { Text = "计数地址", AutoSize = true, Padding = new Padding(8, 8, 0, 0) });
        EnsureControlAdded(_productionToolbar, _txtProductionCounterAddress);
        EnsureControlAdded(_productionToolbar, _btnRefreshProductionChart);
    }

    // 根容器与静态页面挂载
    private void ComposeRootContainers()
    {
        EnsureControlAdded(_pageHost, _mainPage);
        EnsureControlAdded(_pageHost, _debugPage);
        EnsureControlAdded(_pageHost, _debugToolsPage);
        EnsureControlAdded(_pageHost, _boolSignalControl);
        EnsureControlAdded(_pageHost, _recordsPage);

        // 重新按停靠顺序装配根容器，避免顶部导航遮挡调试页分页栏
        SuspendLayout();
        try
        {
            Controls.Remove(_statusStrip);
            Controls.Remove(_txtLog);
            Controls.Remove(_pageHost);
            Controls.Remove(_navPanel);

            Controls.Add(_statusStrip);
            Controls.Add(_txtLog);
            Controls.Add(_pageHost);
            Controls.Add(_navPanel);
        }
        finally
        {
            ResumeLayout();
        }
    }

    // 通用控件挂载：同一父容器下避免重复添加
    private static void EnsureControlAdded(Control parent, Control child)
    {
        if (child.Parent == parent)
            return;

        child.Parent?.Controls.Remove(child);
        try
        {
            parent.Controls.Add(child);
        }
        catch (ArgumentException) when (child is UIPanel && parent is SplitterPanel splitterPanel)
        {
            AttachUiPanelViaHost(splitterPanel, child);
        }
    }

    // 通用控件挂载（TableLayoutPanel 指定行列）
    private static void EnsureControlAdded(TableLayoutPanel parent, Control child, int column, int row)
    {
        if (child.Parent != parent)
        {
            child.Parent?.Controls.Remove(child);
            try
            {
                parent.Controls.Add(child, column, row);
            }
            catch (ArgumentException) when (child is UIPanel)
            {
                child.MinimumSize = new Size(1, 1);
                child.Size = new Size(Math.Max(1, child.Width), Math.Max(1, child.Height));
                parent.Controls.Add(child, column, row);
            }
            return;
        }

        if (parent.GetCellPosition(child) == new TableLayoutPanelCellPosition(column, row))
            return;

        parent.Controls.Remove(child);
        parent.Controls.Add(child, column, row);
    }

    private static void AttachUiPanelViaHost(SplitterPanel splitterPanel, Control child)
    {
        Panel host = splitterPanel.Controls
            .OfType<Panel>()
            .FirstOrDefault(x => ReferenceEquals(x.Tag, child))
            ?? new Panel { Dock = DockStyle.Fill, Tag = child };

        if (host.Parent != splitterPanel)
        {
            host.Parent?.Controls.Remove(host);
            splitterPanel.Controls.Add(host);
        }

        if (child.Parent == host)
            return;

        child.MinimumSize = new Size(1, 1);
        child.Size = new Size(Math.Max(1, child.Width), Math.Max(1, child.Height));
        child.Parent?.Controls.Remove(child);
        host.Controls.Add(child);
        child.Dock = DockStyle.Fill;
    }

    private static void EnsureAlarmGridColumns(DataGridView grid, int w1, int w2, int w3)
    {
        if (grid.Columns.Count > 0)
            return;

        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "报警时间", FillWeight = w1 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "报警内容", FillWeight = w2 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "报警代码", FillWeight = w3 });
    }

    private void InitializeProductionStatisticsUi()
    {
        if (_cmbProductionPeriod.Items.Count == 0)
        {
            _cmbProductionPeriod.Items.AddRange(["周显示", "月显示"]);
            _cmbProductionPeriod.SelectedIndex = 0;
        }

        if (_productionChart.ChartAreas.Count == 0)
        {
            var area = new ChartArea("Main")
            {
                BackColor = Color.White
            };
            area.AxisX.MajorGrid.Enabled = false;
            area.AxisY.MajorGrid.LineColor = Color.FromArgb(228, 235, 246);
            area.AxisX.LabelStyle.Font = new Font("Microsoft YaHei UI", 9F);
            area.AxisY.LabelStyle.Font = new Font("Microsoft YaHei UI", 9F);
            _productionChart.ChartAreas.Add(area);
        }

        if (_productionChart.Legends.Count == 0)
        {
            var legend = new Legend("Legend")
            {
                Docking = Docking.Top,
                Font = new Font("Microsoft YaHei UI", 9F)
            };
            _productionChart.Legends.Add(legend);
        }

        if (_productionChart.Series.Count == 0)
        {
            _productionChart.Series.Add(new Series("白班")
            {
                ChartType = SeriesChartType.Column,
                ChartArea = "Main",
                Legend = "Legend",
                Color = Color.FromArgb(52, 103, 235)
            });
            _productionChart.Series.Add(new Series("夜班")
            {
                ChartType = SeriesChartType.Column,
                ChartArea = "Main",
                Legend = "Legend",
                Color = Color.FromArgb(55, 193, 145)
            });
            _productionChart.Series.Add(new Series("总产量趋势")
            {
                ChartType = SeriesChartType.Line,
                ChartArea = "Main",
                Legend = "Legend",
                BorderWidth = 3,
                MarkerStyle = MarkerStyle.Circle,
                MarkerSize = 7,
                Color = Color.FromArgb(235, 102, 52)
            });
        }

        _dtProductionAnchor.Value = DateTime.Today;
        _tmDayShiftStart.Value = DateTime.Today.AddHours(8);
        _tmDayShiftEnd.Value = DateTime.Today.AddHours(20);
        _tmNightShiftStart.Value = DateTime.Today.AddHours(20);
        _tmNightShiftEnd.Value = DateTime.Today.AddDays(1).AddHours(8);

        _productionChart.MinimumSize = new Size(1, 1);
        _productionChart.Size = new Size(1, 1);
    }

    private void AttachProductionChartSafely()
    {
        if (_productionChartAttached)
            return;

        if (_productionSection.ClientSize.Width <= 1 || _productionSection.ClientSize.Height <= 1)
            return;

        EnsureControlAdded(_productionSection, _productionChart);
        _productionChartAttached = true;
    }

    private void BindEvents()
    {
        Shown += async (_, _) =>
        {
            // 首次显示时先统一轴组上下区域高度，再自动连接 PLC
            AttachProductionChartSafely();
            SetAxisSplitEqualSize();
            EnsureCameraWorkThreadsStartedOnStartup();
            await AutoConnectOnStartupAsync();
            BeginInvoke(new Action(() =>
            {
                // 启动自愈：若因异常流程被隐藏/无页面可见，则恢复到主页面
                if (!Visible)
                    Show();
                if (GetStaticPages().All(x => !x.Visible))
                    ShowPage(_mainPage);
                Activate();
                BringToFront();
            }));

            if (!_mainPageAutoOpenedOnStartup)
            {
                _mainPageAutoOpenedOnStartup = true;
                BeginInvoke(new Action(OpenMainPageWindow));
            }
        };
        // 窗口尺寸变化时保持轴组页上下区域等高
        _axisSplit.SizeChanged += (_, _) => SetAxisSplitEqualSize();

        _btnPageMain.Click += (_, _) => OpenMainPageWindow();
        _btnPageDebug.Click += (_, _) => OpenSystemConfigWindow();
        _btnPageDebugTools.Click += async (_, _) => await OpenDebugToolsWindowAsync();
        _btnPageCameraDebug.Click += (_, _) => OpenCameraDebugWindow();
        _btnPageMonitor.Click += (_, _) => OpenMonitorWindow();
        _btnPageRecords.Click += (_, _) => OpenRecordsWindow();
        _cmbUserRole.SelectedIndexChanged += (_, _) => HandleRoleSelectionChanged();
        _btnPasswordManage.Click += (_, _) => ShowPasswordManageDialog();
        _btnRecordQuery.Click += (_, _) => ApplyRecordDateFilter();
        _btnRecordAll.Click += (_, _) =>
        {
            _dtRecordFrom.Checked = false;
            _dtRecordTo.Checked = false;
            BindRecordGrid(_alarmHistory);
        };
        _btnRecordExport.Click += (_, _) => ExportAlarmRecords();
        _btnRefreshProductionChart.Click += (_, _) => RefreshProductionChart();
        _cmbProductionPeriod.SelectedIndexChanged += (_, _) => RefreshProductionChart();
        _dtProductionAnchor.ValueChanged += (_, _) => RefreshProductionChart();
        _tmDayShiftStart.ValueChanged += (_, _) => RefreshProductionChart();
        _tmDayShiftEnd.ValueChanged += (_, _) => RefreshProductionChart();
        _tmNightShiftStart.ValueChanged += (_, _) => RefreshProductionChart();
        _tmNightShiftEnd.ValueChanged += (_, _) => RefreshProductionChart();
        _chkProductionAutoCollect.CheckedChanged += (_, _) =>
        {
            _lastProductionCounter = -1;
            AppendLog(_chkProductionAutoCollect.Checked ? "产量PLC自动采集已开启。" : "产量PLC自动采集已关闭。");
        };
        _productionPollTimer.Tick += async (_, _) => await PollProductionCounterAsync();
        _productionPollTimer.Start();
        _btnImportParameterData.Click += (_, _) => ImportParameterDataFromExcel();
        _btnImportHmiDisplayData.Click += (_, _) => ImportHmiDisplayDataFromExcel();
        _btnImportButtonVarData.Click += (_, _) => ImportButtonVarDataFromExcel();
        _btnImportBoolGroupData.Click += (_, _) => ImportBoolGroupDataFromExcel();
        _btnImportAlarmConfigData.Click += (_, _) => ImportAlarmConfigDataFromExcel();
        _btnImportAlarmPopupData.Click += (_, _) => ImportAlarmPopupConfigFromExcel();
        _stationDisplayPanel.SizeChanged += (_, _) => ArrangeStationControls();
        _productionSection.SizeChanged += (_, _) => AttachProductionChartSafely();

        _statusTimer.Tick += async (_, _) =>
        {
            await RefreshStatusBarAsync();
            await RefreshRealtimeBoolLabelsAsync();
        };
        _statusTimer.Start();

        _cameraThreadUiTimer.Tick += (_, _) => RefreshCameraThreadDebugUi();
        _cameraThreadUiTimer.Start();

        _processRealtimeMainRebuildDebounce.Tick += (_, _) =>
        {
            _processRealtimeMainRebuildDebounce.Stop();
            RebuildMainProcessRealtimeDisplays();
            if (_plc is { IsConnected: true })
                StartMainProcessRealtimeDisplays();
        };
    }

    private void InitializeRealtimeBoolLabelBindings()
    {
        _realtimeBoolLabelBindings.Clear();

        // 统一在这里扩展：后续新增控件时，只需追加一行配置即可加入同一轮实时刷新
        PlcBoolLabelBindingConfig[] configs =
        [
            //new(uiLabel1, "MX2100.4", "信号1"),
            //new(uiLabel2, "MX2100.5", "信号2"),
            //new(uiLabel3, "MX2100.6", "信号3")
        ];

        foreach (PlcBoolLabelBindingConfig cfg in configs)
            AddRealtimeBoolLabelBinding(cfg.Label, cfg.Address, cfg.DisplayName);

        foreach (PlcBoolLabelBinding binding in _realtimeBoolLabelBindings)
            ApplyRealtimeBoolLabelStyle(binding.Label, binding.Name, binding.Address, null);
    }

    private void AddRealtimeBoolLabelBinding(Control label, string address, string displayName)
    {
        if (label is null)
            return;

        string normalizedAddress = address?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedAddress))
            return;

        string normalizedName = string.IsNullOrWhiteSpace(displayName) ? normalizedAddress : displayName.Trim();
        _realtimeBoolLabelBindings.Add(new PlcBoolLabelBinding(label, normalizedAddress, normalizedName));
    }

    private void ImportParameterDataFromExcel()
    {
        ImportParameterDataFromExcel(_parameterTableControl, "参数页面");
    }

    private void ImportHmiDisplayDataFromExcel()
    {
        ImportParameterDataFromExcel(_hmiDisplayParameterTableControl, "HMI显示数据页面");
    }

    private void ImportButtonVarDataFromExcel()
    {
        ImportParameterDataFromExcel(_buttonVarParameterTableControl, "按钮类变量页面");
    }

    private void ImportBoolGroupDataFromExcel()
    {
        var rows = LoadExcelRowsForImport("选择输出点位Excel文件", "输出点位页面");
        if (rows is null)
            return;

        // 输出点位仅导入名称前缀为 DO_ 的数据
        var items = rows
            .Where(x => x.Name.StartsWith("DO_", StringComparison.OrdinalIgnoreCase))
            .Select(x => new BoolGroupControlItem
            {
                Name = x.Name,
                Address = x.Address
            })
            .ToList();

        if (items.Count == 0)
        {
            MessageBox.Show(this, "未找到名称前缀为 DO_ 的数据。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _boolGroupControlTable.SetItems(items);
        AppendLog($"输出点位页面导入成功，共 {items.Count} 条。");
    }

    private void ImportAlarmConfigDataFromExcel()
    {
        var rows = LoadExcelRowsForImport("选择报警配置Excel文件", "报警配置页面");
        if (rows is null)
            return;

        var items = rows
            .Select(x => new AlarmAddressItem
            {
                AlarmName = x.Name,
                AlarmCode = string.IsNullOrWhiteSpace(x.Comment) ? x.Address : x.Comment,
                AlarmAddress = x.Address,
                DataDisplay = string.Empty
            })
            .ToList();

        _alarmAddressManagerControl.SetItems(items);
        AppendLog($"报警配置页面导入成功，共 {items.Count} 条。");
    }

    private void ImportAlarmPopupConfigFromExcel()
    {
        var rows = LoadExcelRowsForImport("选择报警弹窗配置Excel文件", "报警弹窗配置页面");
        if (rows is null)
            return;

        var tuples = rows.Select(r => (r.Name, r.Address, r.DataTypeText)).ToList();
        List<AlarmPopupGroupDefinition> groups = AlarmPopupExcelParser.ParseGroups(tuples);
        if (groups.Count == 0)
        {
            MessageBox.Show(this,
                "未解析到有效数据。请确认：\n· 列为 Name / Address / DataType；\n· 数据类型为 BOOL；\n· 名称末尾为「弹窗」的行开始一组，其后直到下一个末尾「弹窗」为止、名称末尾为「按钮」的行归入该组。",
                "提示",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        _alarmPopupManagerControl.SetGroups(groups);
        AppendLog($"报警弹窗配置导入成功，共 {groups.Count} 组。");
    }

    private void ImportParameterDataFromExcel(ParameterTableControl targetControl, string pageName)
    {
        var rows = LoadExcelRowsForImport($"选择{pageName}Excel文件", pageName);
        if (rows is null)
            return;

        var items = rows.Select(x =>
        {
            ParameterImportDataTypeResult dt = ParameterTableControl.ParseImportDataType(x.DataTypeText);
            string comment = x.Comment.Trim();
            if (dt.Type == ParameterValueType.REAL_ARRAY && dt.RealArrayLength > 0
                && !string.IsNullOrWhiteSpace(dt.OriginalTypeText))
            {
                if (string.IsNullOrWhiteSpace(comment))
                    comment = dt.OriginalTypeText!;
                else if (!comment.Contains("ARRAY", StringComparison.OrdinalIgnoreCase))
                    comment = $"{dt.OriginalTypeText} | {comment}";
            }

            return new ParameterItem
            {
                Name = x.Name,
                Address = x.Address,
                Type = dt.Type,
                RealArrayLength = dt.RealArrayLength,
                Comment = comment
            };
        }).ToList();

        targetControl.SetParameters(items);
        AppendLog($"{pageName}导入成功，共 {items.Count} 条。");
    }

    private List<ImportedExcelRow>? LoadExcelRowsForImport(string dialogTitle, string pageName)
    {
        using var dialog = new OpenFileDialog
        {
            Title = dialogTitle,
            Filter = "Excel 文件 (*.xls;*.xlsx)|*.xls;*.xlsx|所有文件 (*.*)|*.*",
            InitialDirectory = AppContext.BaseDirectory
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return null;

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            using var stream = File.Open(dialog.FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = ExcelReaderFactory.CreateReader(stream);

            var rows = new List<ImportedExcelRow>();
            bool isHeader = true;
            int nameCol = 1;
            int addressCol = 2;
            int dataTypeCol = 3;
            int commentCol = -1;
            while (reader.Read())
            {
                if (isHeader)
                {
                    int fieldCount = reader.FieldCount;
                    for (int i = 0; i < fieldCount; i++)
                    {
                        string header = Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                        if (string.Equals(header, "Name", StringComparison.OrdinalIgnoreCase))
                            nameCol = i;
                        else if (string.Equals(header, "Address", StringComparison.OrdinalIgnoreCase))
                            addressCol = i;
                        else if (string.Equals(header, "DataType", StringComparison.OrdinalIgnoreCase))
                            dataTypeCol = i;
                        else if (string.Equals(header, "Comment", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(header, "注释", StringComparison.OrdinalIgnoreCase))
                            commentCol = i;
                    }

                    isHeader = false;
                    continue;
                }

                string name = NormalizeImportedName(Convert.ToString(reader.GetValue(nameCol), CultureInfo.InvariantCulture));
                string address = NormalizeImportedAddress(Convert.ToString(reader.GetValue(addressCol), CultureInfo.InvariantCulture));
                string dataTypeText = Convert.ToString(reader.GetValue(dataTypeCol), CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                string comment = commentCol >= 0
                    ? (Convert.ToString(reader.GetValue(commentCol), CultureInfo.InvariantCulture)?.Trim() ?? string.Empty)
                    : string.Empty;

                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(address))
                    continue;

                rows.Add(new ImportedExcelRow
                {
                    Name = name,
                    Address = address,
                    DataTypeText = dataTypeText,
                    Comment = comment
                });
            }

            if (rows.Count == 0)
            {
                MessageBox.Show(this, "未读取到有效数据。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }

            AppendLog($"{pageName}Excel读取成功，共 {rows.Count} 条。 来源: {dialog.FileName}");
            return rows;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导入失败: {ex.Message}", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            AppendLog($"{pageName}导入失败: {ex.Message}");
            return null;
        }
    }

    private sealed class ImportedExcelRow
    {
        public string Name { get; init; } = string.Empty;

        public string Address { get; init; } = string.Empty;

        public string DataTypeText { get; init; } = string.Empty;

        public string Comment { get; init; } = string.Empty;
    }

    // 导入地址规范化：自动去掉Excel里常见的前缀“%”
    private static string NormalizeImportedAddress(string? rawAddress)
    {
        string address = rawAddress?.Trim() ?? string.Empty;
        if (address.StartsWith('%'))
            address = address[1..];

        return address;
    }

    // 导入名称规范化：自动去掉前缀“HMI_gb”
    private static string NormalizeImportedName(string? rawName)
    {
        string name = rawName?.Trim() ?? string.Empty;
        const string prefix = "HMI_gb";
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            name = name[prefix.Length..];

        return name.TrimStart('_');
    }

    private void ConfigureParameterTableControl(ParameterTableControl control)
    {
        control.PlcProvider = () => _plc;
        control.LogAction = AppendLog;
        control.EditPermissionRequest = RequestTableEditAuthorization;
    }

    private void ConfigureProcessRealtimeDataConfigControl(ProcessRealtimeDataConfigControl control)
    {
        control.PlcProvider = () => _plc;
        control.LogAction = AppendLog;
        control.EditPermissionRequest = RequestTableEditAuthorization;
        control.ConfigurationChanged += (_, _) =>
        {
            _processRealtimeMainRebuildDebounce.Stop();
            _processRealtimeMainRebuildDebounce.Start();
        };
    }

    private void SetAxisSplitEqualSize()
    {
        if (_axisSplit.Width <= 0 || _axisSplit.Height <= 0)
            return;

        // 按当前方向计算可分割区间；若区间无效则跳过，避免启动阶段抛出 SplitterDistance 异常
        int span = _axisSplit.Orientation == Orientation.Horizontal ? _axisSplit.Height : _axisSplit.Width;
        int min = Math.Max(1, _axisSplit.Panel1MinSize);
        int max = span - _axisSplit.SplitterWidth - _axisSplit.Panel2MinSize;
        if (max < min)
            return;

        int target = span / 2;
        _axisSplit.SplitterDistance = Math.Clamp(target, min, max);
    }

    private void ShowPage(Control page)
    {
        var role = _currentRole;
        if (role == UserRole.作业员 && (page == _debugPage || page == _debugToolsPage || page == _boolSignalControl))
        {
            AppendLog("作业员权限仅可访问主页面和记录查询。");
            return;
        }

        // 页面采用静态挂载，通过可见性切换显示
        foreach (var candidate in GetStaticPages())
            candidate.Visible = ReferenceEquals(candidate, page);

        page.BringToFront();
        UpdateNavigationButtonState(page);

        if (ReferenceEquals(page, _debugToolsPage))
            _ = RefreshDebugToolsPageAsync();
    }

    private void OpenMainPageWindow()
    {
        if (_mainPageWindow is null || _mainPageWindow.IsDisposed)
        {
            _mainPageWindow = new MainPageWindow
            {
                StartPosition = FormStartPosition.CenterScreen
            };
            _mainPageWindow.AttachPage(_mainPage);
            _mainPageWindow.FormClosed += (_, _) =>
            {
                if (!IsDisposed)
                {
                    EnsureControlAdded(_pageHost, _mainPage);
                    Show();
                    ShowPage(_mainPage);
                    Activate();
                    BringToFront();
                }

                UpdateNavigationButtonState((UIButton?)null);
                _mainPageWindow = null;
            };
        }
        else
        {
            _mainPageWindow.AttachPage(_mainPage);
        }

        _mainPage.Visible = true;
        _mainPage.BringToFront();

        if (!_mainPageWindow.Visible)
        {
            _mainPageWindow.Show();
        }
        else
        {
            _mainPageWindow.Activate();
        }

        _mainPageWindow.WindowState = FormWindowState.Maximized;
        _mainPageWindow.BringToFront();

        if (_mainPageWindow.Visible)
            Hide();
        UpdateNavigationButtonState(_btnPageMain);
    }

    private void OpenSystemConfigWindow()
    {
        if (_currentRole == UserRole.作业员)
        {
            AppendLog("作业员权限仅可访问主页面和记录查询。");
            return;
        }

        OpenOrActivatePageWindow(
            ref _systemConfigPageWindow,
            () => new SystemConfigPageWindow(),
            _debugPage,
            _btnPageDebug);
    }

    private async Task OpenDebugToolsWindowAsync()
    {
        if (_currentRole == UserRole.作业员)
        {
            AppendLog("作业员权限仅可访问主页面和记录查询。");
            return;
        }

        // 必须先弹出窗口再刷新：否则在 PLC 已连接时 Refresh 可能较慢或阻塞，用户会感觉「点了调试按钮没反应」
        OpenOrActivatePageWindow(
            ref _debugToolsPageWindow,
            () => new DebugToolsPageWindow(),
            _debugToolsPage,
            _btnPageDebugTools);

        await RefreshDebugToolsPageAsync();
    }

    private void OpenMonitorWindow()
    {
        if (_currentRole == UserRole.作业员)
        {
            AppendLog("作业员权限仅可访问主页面和记录查询。");
            return;
        }

        OpenOrActivatePageWindow(
            ref _monitorPageWindow,
            () => new MonitorPageWindow(),
            _boolSignalControl,
            _btnPageMonitor);
    }

    private void BuildCameraThreadDebugTab()
    {
        _tabCameraThreads.Controls.Clear();
        _tabCameraThreads.Controls.Add(_lblCameraThreadHint);
        _tabCameraThreads.Controls.Add(_cameraThreadTable);

        _cameraThreadTable.SuspendLayout();
        _cameraThreadTable.Controls.Clear();
        _cameraThreadTable.ColumnCount = 7;
        _cameraThreadTable.RowCount = 7;
        _cameraThreadTable.ColumnStyles.Clear();
        _cameraThreadTable.RowStyles.Clear();
        float[] colWeights = [1.4f, 0.55f, 0.5f, 1f, 1f, 1f, 1f];
        for (int c = 0; c < 7; c++)
            _cameraThreadTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, colWeights[c]));
        _cameraThreadTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        for (int r = 1; r < 7; r++)
            _cameraThreadTable.RowStyles.Add(new RowStyle(SizeType.Percent, 1f));

        string[] headers = ["相机(_cam)", "线程", "暂停", "movX", "movY", "movR", "batteryLen"];
        for (int c = 0; c < 7; c++)
        {
            var hl = new Label
            {
                Text = headers[c],
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                Dock = DockStyle.Fill
            };
            _cameraThreadTable.Controls.Add(hl, c, 0);
        }

        string[] names =
        [
            "Cam1 A侧排版",
            "Cam2 B侧排版",
            "Cam3 A移栽本侧",
            "Cam4 B移栽本侧",
            "Cam5 A移栽对侧",
            "Cam6 B移栽对侧"
        ];
        for (int i = 0; i < 6; i++)
        {
            int row = i + 1;
            int camIdx = i + 1;
            _cameraThreadTable.Controls.Add(new Label
            {
                Text = names[i],
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill,
                AutoSize = false
            }, 0, row);

            var alive = new Label { Text = "—", TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Fill };
            _cameraThreadAliveLabels[i] = alive;
            _cameraThreadTable.Controls.Add(alive, 1, row);

            var chk = new CheckBox { AutoSize = true, Margin = new Padding(0) };
            chk.CheckedChanged += (_, _) =>
            {
                if (_syncingCameraPauseChecks || _boundThCamSet is null)
                    return;
                _boundThCamSet.SetCameraWorkThreadPaused(camIdx, chk.Checked);
            };
            _cameraThreadPauseChecks[i] = chk;
            var pauseHost = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = false,
                Margin = Padding.Empty,
                Padding = new Padding(0, 6, 0, 0)
            };
            pauseHost.Controls.Add(chk);
            _cameraThreadTable.Controls.Add(pauseHost, 2, row);

            for (int v = 0; v < 4; v++)
            {
                var vl = new Label { Text = "—", TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill, AutoSize = false };
                _cameraThreadValueLabels[i, v] = vl;
                _cameraThreadTable.Controls.Add(vl, 3 + v, row);
            }
        }

        _cameraThreadTable.ResumeLayout();
    }

    private void BindThCamSetFromCameraWindow()
    {
        _boundThCamSet = _cameraDebugWindow is THCamSetHostForm host ? host.CamSetControl : null;
    }

    private void EnsureCameraWorkThreadsStartedOnStartup()
    {
        if (_cameraWorkThreadsStartedOnStartup)
            return;

        try
        {
            if (_cameraDebugWindow is not THCamSetHostForm host || host.IsDisposed)
            {
                _cameraDebugWindow?.Dispose();
                host = new THCamSetHostForm
                {
                    StartPosition = FormStartPosition.CenterScreen
                };
                _cameraDebugWindow = host;
                _cameraDebugWindow.FormClosed += (_, _) =>
                {
                    _boundThCamSet = null;
                    UpdateNavigationButtonState((UIButton?)null);
                };
            }

            _boundThCamSet = host.CamSetControl;
            _boundThCamSet.StartCamTask();
            _cameraWorkThreadsStartedOnStartup = true;
            AppendLog("相机工作线程已启动（Cam1~Cam6）。");
        }
        catch (Exception ex)
        {
            AppendLog($"启动相机工作线程失败: {ex.GetBaseException().Message}");
        }
    }

    private void RefreshCameraThreadDebugUi()
    {
        if (IsDisposed || !IsHandleCreated)
            return;

        var cam = _boundThCamSet;
        if (cam is null)
        {
            for (int i = 0; i < 6; i++)
            {
                _cameraThreadAliveLabels[i].Text = "—";
                _syncingCameraPauseChecks = true;
                try
                {
                    _cameraThreadPauseChecks[i].Checked = false;
                    _cameraThreadPauseChecks[i].Enabled = false;
                }
                finally
                {
                    _syncingCameraPauseChecks = false;
                }

                for (int v = 0; v < 4; v++)
                    _cameraThreadValueLabels[i, v].Text = "—";
            }

            return;
        }

        for (int i = 0; i < 6; i++)
        {
            int camIdx = i + 1;
            _cameraThreadPauseChecks[i].Enabled = true;
            var meters = cam.GetCameraWorkThreadLastMeters(camIdx);
            _cameraThreadAliveLabels[i].Text = cam.IsCameraWorkThreadAlive(camIdx) ? "运行" : "停止";
            _cameraThreadValueLabels[i, 0].Text = meters.MovX.ToString("F3", CultureInfo.InvariantCulture);
            _cameraThreadValueLabels[i, 1].Text = meters.MovY.ToString("F3", CultureInfo.InvariantCulture);
            _cameraThreadValueLabels[i, 2].Text = meters.MovR.ToString("F3", CultureInfo.InvariantCulture);
            _cameraThreadValueLabels[i, 3].Text = meters.BatteryLen.ToString("F3", CultureInfo.InvariantCulture);
            _syncingCameraPauseChecks = true;
            try
            {
                _cameraThreadPauseChecks[i].Checked = cam.GetCameraWorkThreadPaused(camIdx);
            }
            finally
            {
                _syncingCameraPauseChecks = false;
            }
        }
    }

    private void OpenCameraDebugWindow()
    {
        if (_currentRole == UserRole.作业员)
        {
            AppendLog("作业员权限仅可访问主页面和记录查询。");
            return;
        }

        try
        {
            string dllPath = Path.Combine(AppContext.BaseDirectory, "HB_CameraUI.dll");
            if (!File.Exists(dllPath))
            {
                AppendLog($"未找到相机调试界面库: {dllPath}");
                return;
            }

            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(x => string.Equals(x.GetName().Name, "HB_CameraUI", StringComparison.OrdinalIgnoreCase))
                ?? System.Reflection.Assembly.LoadFrom(dllPath);

            Type[] assemblyTypes = GetLoadableAssemblyTypes(assembly);
            Form? createdForm = TryCreateCameraFormInstance(assemblyTypes);
            if (createdForm is null)
            {
                if (TryInvokeCameraUiEntryMethod(assemblyTypes))
                {
                    UpdateNavigationButtonState(_btnPageCameraDebug);
                    return;
                }

                AppendLog("HB_CameraUI.dll 中未找到窗体类型或可调用入口方法。");
                return;
            }

            if (_cameraDebugWindow is null || _cameraDebugWindow.IsDisposed || _cameraDebugWindow.GetType() != createdForm.GetType())
            {
                _cameraDebugWindow?.Dispose();
                _cameraDebugWindow = createdForm;
                _cameraDebugWindow.StartPosition = FormStartPosition.CenterScreen;
                _cameraDebugWindow.FormClosed += (_, _) =>
                {
                    _boundThCamSet = null;
                    UpdateNavigationButtonState((UIButton?)null);
                };
            }
            else
            {
                createdForm.Dispose();
            }

            if (!_cameraDebugWindow.Visible)
                _cameraDebugWindow.Show(this);
            else
                _cameraDebugWindow.Activate();

            _cameraDebugWindow.BringToFront();
            BindThCamSetFromCameraWindow();
            UpdateNavigationButtonState(_btnPageCameraDebug);
        }
        catch (Exception ex)
        {
            AppendLog($"打开相机调试界面失败: {ex.GetBaseException().Message}");
        }
    }

    private static Type[] GetLoadableAssemblyTypes(System.Reflection.Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (System.Reflection.ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(x => x is not null).Cast<Type>().ToArray();
        }
    }

    private bool TryInvokeCameraUiEntryMethod(IEnumerable<Type> assemblyTypes)
    {
        var candidateMethods = assemblyTypes
            .Where(x => !x.IsAbstract)
            .SelectMany(type =>
                type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                    .Where(method =>
                        (method.Name.Contains("Show", StringComparison.OrdinalIgnoreCase)
                         || method.Name.Contains("Open", StringComparison.OrdinalIgnoreCase)
                         || method.Name.Contains("Start", StringComparison.OrdinalIgnoreCase))
                        && method.ReturnType == typeof(void)
                        && method.GetParameters().Length == 0))
            .OrderByDescending(m =>
                m.DeclaringType?.Name.Contains("Camera", StringComparison.OrdinalIgnoreCase) == true
                || m.Name.Contains("Camera", StringComparison.OrdinalIgnoreCase));

        var entryMethod = candidateMethods.FirstOrDefault();
        if (entryMethod is null)
            return false;

        entryMethod.Invoke(null, null);
        return true;
    }

    private static Form? TryCreateCameraFormInstance(IEnumerable<Type> assemblyTypes)
    {
        var formCandidates = assemblyTypes
            .Where(x => typeof(Form).IsAssignableFrom(x) && !x.IsAbstract)
            .OrderByDescending(x =>
                x.Name.Contains("Main", StringComparison.OrdinalIgnoreCase)
                || x.Name.Contains("Camera", StringComparison.OrdinalIgnoreCase)
                || x.Name.Contains("相机", StringComparison.Ordinal))
            .ThenBy(x =>
                x.Name.Contains("Fail", StringComparison.OrdinalIgnoreCase)
                || x.Name.Contains("Tip", StringComparison.OrdinalIgnoreCase)
                || x.Name.Contains("Error", StringComparison.OrdinalIgnoreCase))
            .ThenBy(x => x.Name, StringComparer.Ordinal);

        foreach (Type candidate in formCandidates)
        {
            try
            {
                if (Activator.CreateInstance(candidate, true) is Form form)
                    return form;
            }
            catch
            {
                // 跳过不能直接创建的窗体类型（例如必须注入参数的提示窗体）
            }
        }

        return null;
    }

    private void OpenRecordsWindow()
    {
        OpenOrActivatePageWindow(
            ref _recordsPageWindow,
            () => new RecordsPageWindow(),
            _recordsPage,
            _btnPageRecords);
    }

    private void OpenOrActivatePageWindow<TWindow>(
        ref TWindow? window,
        Func<TWindow> factory,
        Control pageControl,
        UIButton navButton)
        where TWindow : Form, IPageHostWindow
    {
        if (window is null || window.IsDisposed)
        {
            window = factory();
            window.StartPosition = FormStartPosition.CenterScreen;
            window.AttachPage(pageControl);
            window.FormClosed += (_, _) =>
            {
                if (!IsDisposed)
                {
                    EnsureControlAdded(_pageHost, pageControl);
                    pageControl.Visible = false;
                }
                UpdateNavigationButtonState((UIButton?)null);
            };
        }
        else
        {
            window.AttachPage(pageControl);
        }

        pageControl.Visible = true;
        pageControl.BringToFront();

        if (!window.Visible)
            window.Show(this);
        else
            window.Activate();

        window.BringToFront();
        UpdateNavigationButtonState(navButton);
    }

    private async Task RefreshDebugToolsPageAsync()
    {
        if (_plc is not { IsConnected: true })
            return;

        try
        {
            await _axisMotionControl.RefreshAllAxisDataAsync();
            await _positionParameterTableControl.RefreshDataAsync();
            await _boolGroupControlTable.RefreshStatusAsync();
            await RefreshMainButtonDisplayAsync();
        }
        catch (Exception ex)
        {
            AppendLog($"调试页面刷新失败: {ex.Message}");
        }
    }

    private void UpdateNavigationButtonState(Control? activePage)
    {
        var mapping = new Dictionary<UIButton, Control>
        {
            [_btnPageMain] = _mainPage,
            [_btnPageDebug] = _debugPage,
            [_btnPageDebugTools] = _debugToolsPage,
            [_btnPageMonitor] = _boolSignalControl,
            [_btnPageRecords] = _recordsPage
        };

        foreach (var (button, page) in mapping)
        {
            bool isActive = ReferenceEquals(page, activePage);
            button.FillColor = isActive ? Color.FromArgb(0, 161, 210) : Color.FromArgb(56, 81, 110);
            button.RectColor = isActive ? Color.FromArgb(0, 188, 238) : Color.FromArgb(86, 116, 150);
            button.FillHoverColor = isActive ? Color.FromArgb(0, 174, 224) : Color.FromArgb(68, 98, 132);
            button.Font = new Font("Microsoft YaHei UI", isActive ? 10.5F : 10F, FontStyle.Bold);
        }
    }

    private void UpdateNavigationButtonState(UIButton? activeButton)
    {
        foreach (var button in new[] { _btnPageMain, _btnPageDebug, _btnPageDebugTools, _btnPageCameraDebug, _btnPageMonitor, _btnPageRecords })
        {
            bool isActive = ReferenceEquals(button, activeButton);
            button.FillColor = isActive ? Color.FromArgb(0, 161, 210) : Color.FromArgb(56, 81, 110);
            button.RectColor = isActive ? Color.FromArgb(0, 188, 238) : Color.FromArgb(86, 116, 150);
            button.FillHoverColor = isActive ? Color.FromArgb(0, 174, 224) : Color.FromArgb(68, 98, 132);
            button.Font = new Font("Microsoft YaHei UI", isActive ? 10.5F : 10F, FontStyle.Bold);
        }
    }

    private void ApplyRolePermissions()
    {
        var role = _currentRole;
        bool canAddDelete = role == UserRole.管理员;
        bool canModify = role is UserRole.管理员 or UserRole.工程师;

        _parameterTableControl.SetPermission(canAddDelete, canModify);
        _hmiDisplayParameterTableControl.SetPermission(canAddDelete, canModify);
        _buttonVarParameterTableControl.SetPermission(canAddDelete, canModify);
        _processRealtimeDataConfigControl.SetPermission(canAddDelete, canModify);
        SetImportButtonsEnabled(canModify);
        _axisMotionControl.SetPermission(canAddDelete, canModify);
        _positionParameterTableControl.SetPermission(canAddDelete, canModify);
        _boolGroupControlTable.SetPermission(canAddDelete, canModify);
        _boolSignalControl.SetPermission(canAddDelete, canModify);
        _alarmAddressManagerControl.SetPermission(canAddDelete, canModify);
        _alarmPopupManagerControl.SetPermission(canAddDelete, canModify);
        _taskSettingsControl.SetPermission(canModify);
        _communicationConfigControl.SetPermission(canModify);
        _stationConfigControl.SetPermission(canAddDelete, canModify);
        _buttonDisplayConfigControl.SetPermission(canAddDelete, canModify);
        _plcBoolLabelDisplayConfigControl.SetPermission(canAddDelete, canModify);
        _plcValueTextBoxDisplayConfigControl.SetPermission(canAddDelete, canModify);

        _btnPageDebug.Enabled = role != UserRole.作业员;
        _btnPageDebugTools.Enabled = role != UserRole.作业员;
        _btnPageCameraDebug.Enabled = role != UserRole.作业员;
        _btnPageMonitor.Enabled = role != UserRole.作业员;
        _txtLog.Visible = role is UserRole.管理员 or UserRole.工程师;

        if (role == UserRole.作业员)
        {
            _systemConfigPageWindow?.Close();
            _debugToolsPageWindow?.Close();
            _cameraDebugWindow?.Close();
            _monitorPageWindow?.Close();
            ShowPage(_mainPage);
        }
    }

    private IEnumerable<Control> GetStaticPages()
    {
        yield return _mainPage;
        yield return _debugPage;
        yield return _debugToolsPage;
        yield return _boolSignalControl;
        yield return _recordsPage;
    }

    private void HideAllStaticPages()
    {
        foreach (var page in GetStaticPages())
            page.Visible = false;
    }

    private void SetImportButtonsEnabled(bool enabled)
    {
        foreach (var button in new[]
                 {
                     _btnImportParameterData,
                     _btnImportHmiDisplayData,
                     _btnImportButtonVarData,
                     _btnImportBoolGroupData,
                     _btnImportAlarmConfigData,
                     _btnImportAlarmPopupData
                 })
        {
            button.Enabled = enabled;
        }
    }

    private void HandleRoleSelectionChanged()
    {
        if (_suspendRoleChanged)
            return;

        string roleText = _cmbUserRole.SelectedItem?.ToString() ?? UserRole.作业员.ToString();
        if (!Enum.TryParse(roleText, out UserRole selectedRole))
            selectedRole = UserRole.作业员;

        if (selectedRole == _currentRole)
            return;

        if (selectedRole is UserRole.管理员 or UserRole.工程师)
        {
            if (!PromptRoleAuthorization(selectedRole))
            {
                _suspendRoleChanged = true;
                _cmbUserRole.SelectedItem = _currentRole.ToString();
                _suspendRoleChanged = false;
                return;
            }
        }

        _currentRole = selectedRole;
        ApplyRolePermissions();
    }

    private bool PromptRoleAuthorization(UserRole targetRole)
    {
        using var dialog = new Form
        {
            Text = $"{targetRole}权限验证",
            Width = 420,
            Height = 230,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(10)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var txtPassword = new TextBox { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
        var txtKey = new TextBox { Dock = DockStyle.Fill };

        layout.Controls.Add(new Label { Text = "密码", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(txtPassword, 1, 0);
        layout.Controls.Add(new Label { Text = "远程授权密钥", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(txtKey, 1, 1);
        layout.Controls.Add(new Label { Text = "可输入密码或密钥（密钥含有效期）", AutoSize = true, ForeColor = Color.DimGray }, 1, 2);

        var panelBtn = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var btnOk = new Button { Text = "确认", Width = 72, DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "取消", Width = 72, DialogResult = DialogResult.Cancel };
        panelBtn.Controls.Add(btnOk);
        panelBtn.Controls.Add(btnCancel);

        dialog.Controls.Add(layout);
        dialog.Controls.Add(panelBtn);
        dialog.AcceptButton = btnOk;
        dialog.CancelButton = btnCancel;

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return false;

        string password = txtPassword.Text.Trim();
        string key = txtKey.Text.Trim();

        if (!string.IsNullOrWhiteSpace(password) && ValidateRolePassword(targetRole, password))
            return true;

        if (!string.IsNullOrWhiteSpace(key) && ValidateRemoteAuthKey(targetRole, key, out string? errMsg))
            return true;

        MessageBox.Show(this, "权限验证失败。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    private bool ValidateRolePassword(UserRole role, string password)
    {
        return role switch
        {
            UserRole.管理员 => string.Equals(password, _authConfig.AdminPassword, StringComparison.Ordinal),
            UserRole.工程师 => string.Equals(password, _authConfig.EngineerPassword, StringComparison.Ordinal),
            _ => false
        };
    }

    private string? ResolveAxisFieldAddress(string axisName, string fieldKey)
    {
        var axis = _axisMotionControl.GetAxes()
            .FirstOrDefault(x => string.Equals(x.AxisId, axisName, StringComparison.OrdinalIgnoreCase));

        if (axis?.Addresses is null)
            return null;

        return axis.Addresses.TryGetValue(fieldKey, out string? address) ? address : null;
    }

    // 表格编辑授权：输入角色与密码后解锁当前控件编辑
    private bool RequestTableEditAuthorization()
    {
        using var dialog = new Form
        {
            Text = "编辑权限验证",
            Width = 380,
            Height = 220,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(10)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var txtPassword = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };

        layout.Controls.Add(new Label { Text = "管理员密码", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(txtPassword, 1, 0);
        layout.Controls.Add(new Label { Text = "仅管理员账户可解锁编辑", AutoSize = true, ForeColor = Color.DimGray }, 1, 1);

        var panelBtn = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var btnOk = new Button { Text = "确认", Width = 72, DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "取消", Width = 72, DialogResult = DialogResult.Cancel };
        panelBtn.Controls.Add(btnOk);
        panelBtn.Controls.Add(btnCancel);

        dialog.Controls.Add(layout);
        dialog.Controls.Add(panelBtn);
        dialog.AcceptButton = btnOk;
        dialog.CancelButton = btnCancel;

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return false;

        string password = txtPassword.Text.Trim();
        bool ok = !string.IsNullOrWhiteSpace(password) && ValidateRolePassword(UserRole.管理员, password);
        if (!ok)
        {
            MessageBox.Show(this, "管理员密码错误。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        AppendLog("管理员编辑权限验证通过。");
        return true;
    }

    private bool ValidateRemoteAuthKey(UserRole targetRole, string authKey, out string? error)
    {
        error = null;
        string[] parts = authKey.Split('.', 2);
        if (parts.Length != 2)
        {
            error = "密钥格式错误。";
            return false;
        }

        string payload = parts[0];
        string sign = parts[1];
        string expectedSign = ComputeSha256Hex(payload + "|" + RemoteKeySignSecret);
        if (!string.Equals(sign, expectedSign, StringComparison.OrdinalIgnoreCase))
        {
            error = "密钥签名错误。";
            return false;
        }

        string[] payloadParts = payload.Split(';');
        string rolePart = payloadParts.FirstOrDefault(x => x.StartsWith("ROLE=", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        string expPart = payloadParts.FirstOrDefault(x => x.StartsWith("EXP=", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rolePart) || string.IsNullOrWhiteSpace(expPart))
        {
            error = "密钥内容错误。";
            return false;
        }

        string roleText = rolePart.Replace("ROLE=", string.Empty, StringComparison.OrdinalIgnoreCase);
        string expText = expPart.Replace("EXP=", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (!Enum.TryParse(roleText, out UserRole keyRole))
        {
            error = "密钥角色错误。";
            return false;
        }

        if (!DateTime.TryParseExact(expText, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime expireAt))
        {
            error = "密钥时间格式错误。";
            return false;
        }

        if (DateTime.Now > expireAt)
        {
            error = "密钥已过期。";
            return false;
        }

        bool allow = targetRole switch
        {
            UserRole.管理员 => keyRole == UserRole.管理员,
            UserRole.工程师 => keyRole is UserRole.管理员 or UserRole.工程师,
            _ => false
        };

        if (!allow)
            error = "密钥权限不足。";

        return allow;
    }

    private void ShowPasswordManageDialog()
    {
        if (_currentRole == UserRole.作业员)
        {
            MessageBox.Show(this, "当前权限不可管理密码。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new Form
        {
            Text = "密码管理",
            Width = 420,
            Height = 240,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var txtAdmin = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true, Text = _authConfig.AdminPassword };
        var txtEngineer = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true, Text = _authConfig.EngineerPassword };
        layout.Controls.Add(new Label { Text = "管理员密码", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(txtAdmin, 1, 0);
        layout.Controls.Add(new Label { Text = "工程师密码", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(txtEngineer, 1, 1);
        layout.Controls.Add(new Label { Text = "密钥格式：ROLE=管理员;EXP=20261231120000.签名", AutoSize = true, ForeColor = Color.DimGray }, 1, 2);

        var btnPanel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var btnOk = new Button { Text = "保存", Width = 72, DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "取消", Width = 72, DialogResult = DialogResult.Cancel };
        btnPanel.Controls.Add(btnOk);
        btnPanel.Controls.Add(btnCancel);

        dialog.Controls.Add(layout);
        dialog.Controls.Add(btnPanel);
        dialog.AcceptButton = btnOk;
        dialog.CancelButton = btnCancel;

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        if (string.IsNullOrWhiteSpace(txtAdmin.Text) || string.IsNullOrWhiteSpace(txtEngineer.Text))
        {
            MessageBox.Show(this, "密码不能为空。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _authConfig.AdminPassword = txtAdmin.Text.Trim();
        _authConfig.EngineerPassword = txtEngineer.Text.Trim();
        SaveAuthConfig();
        AppendLog("角色密码已更新。\n");
    }

    private void LoadAuthConfig()
    {
        try
        {
            if (!File.Exists(_authConfigPath))
            {
                SaveAuthConfig();
                return;
            }

            string json = File.ReadAllText(_authConfigPath);
            var cfg = JsonSerializer.Deserialize<RoleAuthConfig>(json);
            if (cfg is not null)
                _authConfig = cfg;
        }
        catch
        {
            _authConfig = new RoleAuthConfig();
        }
    }

    private void LoadMainPlcConfig()
    {
        try
        {
            if (!File.Exists(_mainPlcConfigPath))
            {
                _mainPlcConfig = new MainPlcConfig();
                SaveMainPlcConfig();
                return;
            }

            string json = File.ReadAllText(_mainPlcConfigPath);
            var cfg = JsonSerializer.Deserialize<MainPlcConfig>(json);
            if (cfg is null)
            {
                _mainPlcConfig = new MainPlcConfig();
                return;
            }

            SanitizeMainPlcConfig(cfg);
            _mainPlcConfig = cfg;
        }
        catch
        {
            _mainPlcConfig = new MainPlcConfig();
        }
    }

    private static void SanitizeMainPlcConfig(MainPlcConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.IpAddress))
            cfg.IpAddress = "192.168.1.88";
        else
            cfg.IpAddress = cfg.IpAddress.Trim();

        if (cfg.Port is < 1 or > 65535)
            cfg.Port = 502;

        if (cfg.SlaveId is < 0 or > 255)
            cfg.SlaveId = 1;

        cfg.HeartbeatBoolAddress = cfg.HeartbeatBoolAddress?.Trim() ?? string.Empty;
    }

    private void SaveMainPlcConfig()
    {
        try
        {
            SanitizeMainPlcConfig(_mainPlcConfig);
            string json = JsonSerializer.Serialize(_mainPlcConfig, JsonFileOptions);
            File.WriteAllText(_mainPlcConfigPath, json);
        }
        catch
        {
            // 与 SaveAuthConfig 一致：写入失败时不打断界面
        }
    }

    private void SaveAuthConfig()
    {
        try
        {
            string json = JsonSerializer.Serialize(_authConfig, JsonFileOptions);
            File.WriteAllText(_authConfigPath, json);
        }
        catch
        {
            // ignore auth config write exceptions to avoid UI interruption
        }
    }

    private static string ComputeSha256Hex(string value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private async Task AutoConnectOnStartupAsync()
    {
        if (_autoConnectTriggered)
            return;

        _autoConnectTriggered = true;
        await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        if (_isConnecting)
            return;

        _isConnecting = true;
        _lblPlcStatus.Text = "PLC: 连接中...";
        _lblPlcStatus.ForeColor = Color.DarkGoldenrod;

        try
        {
            _plc?.Disconnect();
            SanitizeMainPlcConfig(_mainPlcConfig);
            _plc = new global::PlcConectCom.PlcCommunicator(_mainPlcConfig.IpAddress, _mainPlcConfig.Port, _mainPlcConfig.SlaveId);
            await _plc.ConnectAsync();

            if (!string.IsNullOrWhiteSpace(_mainPlcConfig.HeartbeatBoolAddress))
            {
                string hb = _mainPlcConfig.HeartbeatBoolAddress.Trim();
                await _plc.WriteBoolAsync(hb, true);
                await Task.Delay(1000).ConfigureAwait(true);
                await _plc.WriteBoolAsync(hb, true);
            }

            bool probeOk = await ProbePlcConnectionAsync();
            if (!probeOk)
                throw new Exception("PLC通讯探测失败，可能网线未连接或目标PLC不可达。");

            SetStatusUi(true);
            await _boolSignalControl.RefreshSignalsAsync();
            await _parameterTableControl.ReadAllAsync();
            await _hmiDisplayParameterTableControl.ReadAllAsync();
            await _buttonVarParameterTableControl.ReadAllAsync();
            await _processRealtimeDataConfigControl.ReadAllAsync();
            await _axisMotionControl.RefreshAllAxisDataAsync();
            await _positionParameterTableControl.RefreshDataAsync();
            await _boolGroupControlTable.RefreshStatusAsync();
            _alarmAddressManagerControl.StartAutoMonitor();
            _alarmPopupManagerControl.StartAutoMonitor();
            StartMainProcessRealtimeDisplays();
            await RefreshMainButtonDisplayAsync();
            await RefreshRealtimeBoolLabelsAsync();
            _disconnectAlreadyCounted = false;
            _lastProductionCounter = -1;
            await RefreshStatusBarAsync();
            AppendLog($"连接成功 {_mainPlcConfig.IpAddress}:{_mainPlcConfig.Port} 站号:{_mainPlcConfig.SlaveId}");
        }
        catch (Exception ex)
        {
            _plc?.Disconnect();
            _failedReconnectCount++;
            _lblReconnectFail.Text = $"失败重连次数: {_failedReconnectCount}";
            SetStatusUi(false);
            _lastProductionCounter = -1;
            AppendLog($"连接失败: {ex.Message}");
        }
        finally
        {
            _isConnecting = false;
        }
    }

    private void RebuildMainProcessRealtimeDisplays()
    {
        if (_mainRealtimePanel.IsDisposed)
            return;

        foreach (ProcessRealtimeValueDisplayControl old in _mainRealtimePanel.Controls.OfType<ProcessRealtimeValueDisplayControl>().ToList())
        {
            _mainRealtimePanel.Controls.Remove(old);
            old.Dispose();
        }

        IReadOnlyList<ProcessRealtimeDataItem> items = ProcessRealtimeDataConfigControl.LoadConfiguredItems();
        bool plcOk = _plc is { IsConnected: true };
        foreach (ProcessRealtimeDataItem item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Address))
                continue;

            var ctrl = new ProcessRealtimeValueDisplayControl();
            ctrl.ApplyItem(item);
            ctrl.PlcProvider = () => _plc;
            ctrl.LogAction = AppendLog;
            ctrl.AutoReadEnabled = plcOk;
            _mainRealtimePanel.Controls.Add(ctrl);
        }

        EnsureControlAdded(_mainButtonDisplaySection, _mainButtonDisplayPanel);
        EnsureControlAdded(_mainButtonDisplaySection, _lblMainButtonDisplayTitle);
        EnsureControlAdded(_mainRealtimePanel, _mainButtonDisplaySection);
        _mainRealtimePanel.Controls.SetChildIndex(_mainButtonDisplaySection, _mainRealtimePanel.Controls.Count - 1);
    }

    private void StartMainProcessRealtimeDisplays()
    {
        foreach (var control in _mainRealtimePanel.Controls.OfType<ProcessRealtimeValueDisplayControl>())
            control.AutoReadEnabled = true;
    }

    // 工站配置变化后重建主页面工站控件实例
    private void HandleStationConfigurationsChanged(IReadOnlyList<StationOperationConfig> configurations)
    {
        RebuildStationControls(configurations);
    }

    private void HandleButtonDisplayConfigurationsChanged(IReadOnlyList<ButtonDisplayConfigItem> configurations)
    {
        RebuildMainButtonDisplayControls(configurations);
    }

    private void HandlePlcBoolLabelDisplayConfigurationsChanged(IReadOnlyList<PlcBoolLabelDisplayConfigItem> configurations)
    {
        RebuildMainPlcBoolLabelDisplays(configurations);
    }

    private void HandlePlcValueTextBoxDisplayConfigurationsChanged(IReadOnlyList<PlcValueTextBoxDisplayConfigItem> configurations)
    {
        RebuildMainPlcValueTextBoxDisplays(configurations);
    }

    // 根据配置动态创建工站控件并绑定地址
    private void RebuildStationControls(IReadOnlyList<StationOperationConfig> configurations)
    {
        _stationControls.Clear();
        _stationDisplayPanel.SuspendLayout();
        _stationDisplayPanel.Controls.Clear();

        foreach (var cfg in configurations)
        {
            var control = new StationOperationStatusControl
            {
                PlcProvider = () => _plc,
                LogAction = AppendLog,
                StationName = cfg.StationName,
                StartCommandAddress = cfg.StartCommandAddress,
                PauseCommandAddress = cfg.PauseCommandAddress,
                StopCommandAddress = cfg.StopCommandAddress,
                ResetCommandAddress = cfg.ResetCommandAddress,
                AutoSignalAddress = cfg.AutoSignalAddress,
                PauseSignalAddress = cfg.PauseSignalAddress,
                InitSignalAddress = cfg.InitSignalAddress,
                InitDoneSignalAddress = cfg.InitDoneSignalAddress,
                FlowStatusIdAddress = cfg.FlowStatusIdAddress,
                FlowStatusIdValueType = cfg.FlowStatusIdValueType,
                AutoRefreshEnabled = true,
                Margin = new Padding(6)
            };

            try
            {
                if (!string.IsNullOrWhiteSpace(cfg.FlowStatusMapFilePath) && File.Exists(cfg.FlowStatusMapFilePath))
                    control.LoadFlowStatusMapFromExcel(cfg.FlowStatusMapFilePath);
            }
            catch (Exception ex)
            {
                AppendLog($"工站[{cfg.StationName}]状态字典加载失败: {ex.Message}");
            }

            _stationControls.Add(control);
            _stationDisplayPanel.Controls.Add(control);
        }

        // 统一尺寸：主页面按一行 6 个工站的宽度进行排版
        ArrangeStationControls();
        _stationDisplayPanel.ResumeLayout();
    }

    private void RebuildMainButtonDisplayControls(IReadOnlyList<ButtonDisplayConfigItem> configurations)
    {
        _mainBoolSwitchControls.Clear();
        _mainBoundButtons.Clear();
        _mainButtonDisplayPanel.SuspendLayout();
        _mainButtonDisplayPanel.Controls.Clear();

        foreach (ButtonDisplayConfigItem cfg in configurations)
        {
            if (cfg.ControlType == ButtonDisplayControlType.PLC开关)
            {
                var sw = new PlcBoolSwitchControl
                {
                    Width = 220,
                    Margin = new Padding(6),
                    Title = cfg.DisplayName,
                    PlcAddress = cfg.PlcAddress,
                    PlcProvider = () => _plc,
                    LogAction = AppendLog
                };
                _mainBoolSwitchControls.Add(sw);
                _mainButtonDisplayPanel.Controls.Add(sw);
                continue;
            }

            var button = new PlcBoundButton
            {
                Margin = new Padding(6),
                PlcProvider = () => _plc,
                LogAction = AppendLog
            };
            button.ApplyBinding(cfg.PlcAddress, cfg.ValueType, cfg.StringLength);
            button.Text = cfg.DisplayName;
            button.AutoReset = cfg.AutoReset;
            _mainBoundButtons.Add(button);
            _mainButtonDisplayPanel.Controls.Add(button);
        }

        _mainButtonDisplayPanel.ResumeLayout();
        _ = RefreshMainButtonDisplayAsync();
    }

    private void RebuildMainPlcBoolLabelDisplays(IReadOnlyList<PlcBoolLabelDisplayConfigItem> configurations)
    {
        _mainPlcBoolLabelControls.Clear();
        _mainPlcBoolLabelPanel.SuspendLayout();
        _mainPlcBoolLabelPanel.Controls.Clear();

        foreach (PlcBoolLabelDisplayConfigItem cfg in configurations)
        {
            if (string.IsNullOrWhiteSpace(cfg.PlcAddress))
                continue;

            var control = new PlcBoolLabe
            {
                Width = 180,
                Height = 35,
                Margin = new Padding(0, 0, 0, 6),
                PlcAddress = cfg.PlcAddress.Trim(),
                TrueText = string.IsNullOrWhiteSpace(cfg.TrueText) ? "True" : cfg.TrueText,
                FalseText = string.IsNullOrWhiteSpace(cfg.FalseText) ? "False" : cfg.FalseText,
                ValueForeColor = Color.FromArgb(cfg.ForeColorArgb),
                TrueColor = Color.FromArgb(cfg.TrueBackColorArgb),
                FalseColor = Color.FromArgb(cfg.FalseBackColorArgb),
                PlcProvider = () => _plc,
                LogAction = AppendLog
            };
            _mainPlcBoolLabelControls.Add(control);
            _mainPlcBoolLabelPanel.Controls.Add(control);
        }

        _mainPlcBoolLabelPanel.ResumeLayout();
    }

    private void RebuildMainPlcValueTextBoxDisplays(IReadOnlyList<PlcValueTextBoxDisplayConfigItem> configurations)
    {
        _mainPlcValueTextBoxControls.Clear();
        _mainPlcValueTextBoxPanel.SuspendLayout();
        _mainPlcValueTextBoxPanel.Controls.Clear();

        foreach (PlcValueTextBoxDisplayConfigItem cfg in configurations)
        {
            if (string.IsNullOrWhiteSpace(cfg.PlcAddress))
                continue;

            var control = new PlcValueTextBoxControl
            {
                Width = 360,
                Height = 30,
                Margin = new Padding(0, 0, 0, 6),
                Title = string.IsNullOrWhiteSpace(cfg.DisplayName) ? cfg.PlcAddress.Trim() : cfg.DisplayName.Trim(),
                PlcAddress = cfg.PlcAddress.Trim(),
                DataType = cfg.DataType,
                ControlMode = cfg.ControlMode,
                StringLength = Math.Max(1, cfg.StringLength),
                PlcProvider = () => _plc,
                LogAction = AppendLog
            };
            _mainPlcValueTextBoxControls.Add(control);
            _mainPlcValueTextBoxPanel.Controls.Add(control);
        }

        _mainPlcValueTextBoxPanel.ResumeLayout();
    }

    private async Task RefreshMainButtonDisplayAsync()
    {
        if (_refreshingMainButtons)
            return;

        _refreshingMainButtons = true;
        try
        {
            foreach (PlcBoolSwitchControl sw in _mainBoolSwitchControls)
                await sw.RefreshStateAsync();

            // await plcBoolLabe1.RefreshStateAsync();

            foreach (PlcBoundButton btn in _mainBoundButtons)
                await btn.ShowDataAsync();
        }
        finally
        {
            _refreshingMainButtons = false;
        }
    }

    // 工站区域控件尺寸重排：优先满足横向 6 列显示
    private void ArrangeStationControls()
    {
        if (_stationControls.Count == 0)
            return;

        int availableWidth = Math.Max(600, _stationDisplayPanel.ClientSize.Width - _stationDisplayPanel.Padding.Horizontal);
        int spacing = 12;
        int totalSpacing = spacing * (StationDisplayColumns + 1);
        int cardWidth = Math.Max(180, (availableWidth - totalSpacing) / StationDisplayColumns);
        int cardHeight = Math.Max(220, Math.Min(300, (int)(cardWidth * 0.95)));

        foreach (var control in _stationControls)
        {
            control.Width = cardWidth;
            control.Height = cardHeight;
            control.Margin = new Padding(spacing / 2);
        }
    }

    private void AppendLog(string message)
    {
        _txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }

    private async Task RefreshRealtimeBoolLabelsAsync()
    {
        if (_refreshingRealtimeBoolLabels)
            return;

        if (_realtimeBoolLabelBindings.Count == 0)
            return;

        _refreshingRealtimeBoolLabels = true;
        try
        {
            if (_plc is null || !_plc.IsConnected)
            {
                foreach (PlcBoolLabelBinding binding in _realtimeBoolLabelBindings)
                    ApplyRealtimeBoolLabelStyle(binding.Label, binding.Name, binding.Address, null);
                return;
            }

            foreach (PlcBoolLabelBinding binding in _realtimeBoolLabelBindings)
            {
                bool? value;
                try
                {
                    value = await _plc.ReadBoolAsync(binding.Address);
                }
                catch
                {
                    value = null;
                }

                ApplyRealtimeBoolLabelStyle(binding.Label, binding.Name, binding.Address, value);
            }
        }
        finally
        {
            _refreshingRealtimeBoolLabels = false;
        }
    }

    private static void ApplyRealtimeBoolLabelStyle(Control label, string name, string address, bool? value)
    {
        if (value is null)
        {
            label.Text = $"{name} 未连接";
            label.BackColor = Color.DimGray;
            label.ForeColor = Color.White;
            return;
        }

        if (value.Value)
        {
            label.Text = $"{name} ON";
            label.BackColor = Color.FromArgb(33, 150, 83);
            label.ForeColor = Color.White;
            return;
        }

        label.Text = $"{name} OFF";
        label.BackColor = Color.FromArgb(220, 53, 69);
        label.ForeColor = Color.White;
    }

    private async Task RefreshStatusBarAsync()
    {
        if (_statusProbeBusy)
            return;

        _statusProbeBusy = true;
        try
        {
            bool connected = await ProbePlcConnectionAsync();
            SetStatusUi(connected);
        }
        finally
        {
            _statusProbeBusy = false;
        }
    }

    private async Task<bool> ProbePlcConnectionAsync()
    {
        if (_plc is null)
            return false;

        try
        {
            string probeAddress = string.IsNullOrWhiteSpace(_mainPlcConfig.HeartbeatBoolAddress)
                ? "MX1021.6"
                : _mainPlcConfig.HeartbeatBoolAddress.Trim();

            await _plc.WriteBoolAsync(probeAddress, true);
            await Task.Delay(1000);
            bool heartbeatValue = await _plc.ReadBoolAsync(probeAddress);
            if (!heartbeatValue)
            {
                await _plc.WriteBoolAsync(probeAddress, true);
                return true;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SetStatusUi(bool connected)
    {

        if (connected)
        {
            _lblPlcStatus.Text = "PLC: 连接成功";
            _lblPlcStatus.ForeColor = Color.DarkGreen;
            _lblHeartbeat.Text = "心跳: 正常 ●";
            _disconnectAlreadyCounted = false;
            _heartbeatFlash = !_heartbeatFlash;
            _lblHeartbeat.ForeColor = _heartbeatFlash ? Color.LimeGreen : Color.SeaGreen;
        }
        else
        {
            _lblPlcStatus.Text = "PLC: 连接中断";
            _lblPlcStatus.ForeColor = Color.DarkRed;

            if (_lastConnectedState && !_disconnectAlreadyCounted)
            {
                _failedReconnectCount++;
                _disconnectAlreadyCounted = true;
            }

            _lblHeartbeat.Text = "心跳: 异常";
            _lblHeartbeat.ForeColor = Color.DarkRed;
        }

        _lblReconnectFail.Text = $"失败重连次数: {_failedReconnectCount}";
        _lastConnectedState = connected;
    }

    private void HandleAlarmTriggered(AlarmTriggeredEvent alarm)
    {
        _alarmHistory.Add(new AlarmHistoryRecord
        {
            AlarmTime = alarm.AlarmTime,
            AlarmName = alarm.AlarmName,
            AlarmCode = alarm.AlarmCode
        });

        _mainAlarmGrid.Rows.Insert(0,
            alarm.AlarmTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            alarm.AlarmName,
            alarm.AlarmCode);

        _recordAlarmGrid.Rows.Insert(0,
            alarm.AlarmTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            alarm.AlarmName,
            alarm.AlarmCode);

        string key = $"{alarm.AlarmCode}|{alarm.AlarmName}";
        if (_alarmStats.TryGetValue(key, out var stat))
            _alarmStats[key] = (stat.name, stat.code, stat.count + 1);
        else
            _alarmStats[key] = (alarm.AlarmName, alarm.AlarmCode, 1);

        RefreshTopAlarmLabels();
    }

    private void ApplyRecordDateFilter()
    {
        IEnumerable<AlarmHistoryRecord> source = _alarmHistory;
        if (_dtRecordFrom.Checked)
            source = source.Where(x => x.AlarmTime.Date >= _dtRecordFrom.Value.Date);
        if (_dtRecordTo.Checked)
            source = source.Where(x => x.AlarmTime.Date <= _dtRecordTo.Value.Date);

        BindRecordGrid(source);
    }

    private void BindRecordGrid(IEnumerable<AlarmHistoryRecord> records)
    {
        _recordAlarmGrid.Rows.Clear();
        foreach (var item in records.OrderByDescending(x => x.AlarmTime))
        {
            _recordAlarmGrid.Rows.Add(
                item.AlarmTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                item.AlarmName,
                item.AlarmCode);
        }
    }

    public void RecordProduction(int quantity, DateTime? producedAt = null)
    {
        if (quantity <= 0)
            return;

        _productionRecords.Add(new ProductionRecord
        {
            ProducedAt = producedAt ?? DateTime.Now,
            Quantity = quantity
        });
        RefreshProductionChart();
    }

    private async Task PollProductionCounterAsync()
    {
        if (_productionPollBusy || !_chkProductionAutoCollect.Checked || _plc is null)
            return;

        string address = _txtProductionCounterAddress.Text.Trim();
        if (string.IsNullOrWhiteSpace(address))
            return;

        _productionPollBusy = true;
        try
        {
            int counter = await _plc.ReadIntAsync(address);
            if (_lastProductionCounter < 0)
            {
                _lastProductionCounter = counter;
                return;
            }

            int delta = counter - _lastProductionCounter;
            if (delta > 0)
            {
                RecordProduction(delta);
            }
            else if (delta < 0)
            {
                AppendLog($"产量计数地址[{address}]发生回退，已重置基线。 当前值: {counter}");
            }

            _lastProductionCounter = counter;
        }
        catch
        {
            // 通讯中断期间忽略采集异常，待下个轮询周期重试
        }
        finally
        {
            _productionPollBusy = false;
        }
    }

    private void RefreshProductionChart()
    {
        var (startDate, dayCount) = GetProductionWindow(_dtProductionAnchor.Value.Date);
        var dayStart = _tmDayShiftStart.Value.TimeOfDay;
        var dayEnd = _tmDayShiftEnd.Value.TimeOfDay;
        var nightStart = _tmNightShiftStart.Value.TimeOfDay;
        var nightEnd = _tmNightShiftEnd.Value.TimeOfDay;

        var daySeries = _productionChart.Series["白班"];
        var nightSeries = _productionChart.Series["夜班"];
        var trendSeries = _productionChart.Series["总产量趋势"];
        daySeries.Points.Clear();
        nightSeries.Points.Clear();
        trendSeries.Points.Clear();

        for (int i = 0; i < dayCount; i++)
        {
            DateTime day = startDate.AddDays(i);
            DateTime nextDay = day.AddDays(1);
            var records = _productionRecords.Where(x => x.ProducedAt >= day && x.ProducedAt < nextDay);

            int dayShiftQty = 0;
            int nightShiftQty = 0;
            foreach (var record in records)
            {
                TimeSpan t = record.ProducedAt.TimeOfDay;
                if (IsWithinTimeRange(t, dayStart, dayEnd))
                    dayShiftQty += record.Quantity;
                else if (IsWithinTimeRange(t, nightStart, nightEnd))
                    nightShiftQty += record.Quantity;
            }

            string xLabel = day.ToString("MM-dd", CultureInfo.InvariantCulture);
            daySeries.Points.AddXY(xLabel, dayShiftQty);
            nightSeries.Points.AddXY(xLabel, nightShiftQty);
            trendSeries.Points.AddXY(xLabel, dayShiftQty + nightShiftQty);
        }
    }

    private (DateTime startDate, int dayCount) GetProductionWindow(DateTime anchorDate)
    {
        if (string.Equals(_cmbProductionPeriod.SelectedItem?.ToString(), "月显示", StringComparison.Ordinal))
        {
            DateTime monthStart = new(anchorDate.Year, anchorDate.Month, 1);
            return (monthStart, DateTime.DaysInMonth(anchorDate.Year, anchorDate.Month));
        }

        int offset = ((int)anchorDate.DayOfWeek + 6) % 7;
        DateTime weekStart = anchorDate.AddDays(-offset);
        return (weekStart, 7);
    }

    private static bool IsWithinTimeRange(TimeSpan value, TimeSpan start, TimeSpan end)
    {
        if (start == end)
            return true;

        return start < end
            ? value >= start && value < end
            : value >= start || value < end;
    }

    private void ExportAlarmRecords()
    {
        if (_recordAlarmGrid.Rows.Count == 0)
        {
            AppendLog("无报警记录可导出。");
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "导出报警记录",
            Filter = "CSV 文件 (*.csv)|*.csv|文本文件 (*.txt)|*.txt",
            FileName = $"AlarmRecords_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            InitialDirectory = AppContext.BaseDirectory
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            var lines = new List<string> { "报警时间,报警内容,报警代码" };
            foreach (DataGridViewRow row in _recordAlarmGrid.Rows)
            {
                string time = EscapeCsv(Convert.ToString(row.Cells[0].Value) ?? string.Empty);
                string content = EscapeCsv(Convert.ToString(row.Cells[1].Value) ?? string.Empty);
                string code = EscapeCsv(Convert.ToString(row.Cells[2].Value) ?? string.Empty);
                lines.Add($"{time},{content},{code}");
            }

            File.WriteAllLines(dialog.FileName, lines, Encoding.UTF8);
            AppendLog($"报警记录导出成功: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            AppendLog($"报警记录导出失败: {ex.Message}");
        }
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains('"'))
            value = value.Replace("\"", "\"\"");

        if (value.Contains(',') || value.Contains('\r') || value.Contains('\n') || value.Contains('"'))
            return $"\"{value}\"";

        return value;
    }

    private void RefreshTopAlarmLabels()
    {
        var top = _alarmStats.Values
            .OrderByDescending(x => x.count)
            .ThenBy(x => x.code)
            .Take(3)
            .ToArray();

        string[] levels = ["一级", "二级", "三级"];
        for (int i = 0; i < _topAlarmLabels.Length; i++)
        {
            if (i < top.Length)
            {
                var item = top[i];
                _topAlarmLabels[i].Text = $"{levels[i]}  {item.name} ({item.code})  次数: {item.count}";
            }
            else
            {
                _topAlarmLabels[i].Text = $"{levels[i]}  暂无数据";
            }
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _statusTimer.Stop();
        _cameraThreadUiTimer.Stop();
        _productionPollTimer.Stop();
        _processRealtimeMainRebuildDebounce.Stop();
        try
        {
            (_boundThCamSet ?? (_cameraDebugWindow as THCamSetHostForm)?.CamSetControl)?.StopCamTask();
        }
        catch (Exception ex)
        {
            AppendLog($"停止相机工作线程失败: {ex.GetBaseException().Message}");
        }
        _taskPipelineRunner?.Dispose();
        _plc?.Disconnect();
        base.OnFormClosing(e);
    }

    private async void uiButton1_Click(object sender, EventArgs e)
    {
        if (_plc is null || !_plc.IsConnected)
        {
            AppendLog("PLC未连接，无法写入。");
            return;
        }

        const string testAddress = "MX2001.1";
        try
        {
            await _plc.WriteBoolAsync(testAddress, true);
            AppendLog($"写入PLC成功: {testAddress} = true");
        }
        catch (Exception ex)
        {
            AppendLog($"写入PLC失败: {ex.GetBaseException().Message}");
        }
    }

    private async void uiButton2_Click(object sender, EventArgs e)
    {
        if (_plc is null || !_plc.IsConnected)
        {
            AppendLog("PLC未连接，无法写入。");
            return;
        }

        const string testAddress = "MX2000.0";
        try
        {
            await _plc.WriteBoolAsync(testAddress, true);
            AppendLog($"写入PLC成功: {testAddress} = true");
            Thread.Sleep(100);
            await _plc.WriteBoolAsync(testAddress, false);
            AppendLog($"写入PLC成功: {testAddress} = false");
        }
        catch (Exception ex)
        {
            AppendLog($"写入PLC失败: {ex.GetBaseException().Message}");
        }
    }

    private async void uiButton3_Click(object sender, EventArgs e)
    {
        if (_plc is null || !_plc.IsConnected)
        {
            AppendLog("PLC未连接，无法写入。");
            return;
        }

        const string testAddress = "MX2000.0";
        try
        {
            await _plc.WriteBoolAsync(testAddress, true);
            AppendLog($"写入PLC成功: {testAddress} = true");
            Thread.Sleep(100);
            await _plc.WriteBoolAsync(testAddress, false);
            AppendLog($"写入PLC成功: {testAddress} = false");
        }
        catch (Exception ex)
        {
            AppendLog($"写入PLC失败: {ex.GetBaseException().Message}");
        }
    }

    private async void uiButton4_Click(object sender, EventArgs e)
    {
        if (_plc is null || !_plc.IsConnected)
        {
            AppendLog("PLC未连接，无法写入。");
            return;
        }

        const string testAddress = "MX2000.2";
        try
        {
            await _plc.WriteBoolAsync(testAddress, true);
            AppendLog($"写入PLC成功: {testAddress} = true");
            Thread.Sleep(100);
            await _plc.WriteBoolAsync(testAddress, false);
            AppendLog($"写入PLC成功: {testAddress} = false");
        }
        catch (Exception ex)
        {
            AppendLog($"写入PLC失败: {ex.GetBaseException().Message}");
        }
    }
}
