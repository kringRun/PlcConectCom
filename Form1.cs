using System.Globalization;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.ComponentModel;
using System.Windows.Forms.DataVisualization.Charting;
using ExcelDataReader;
using Microsoft.Data.Sqlite;
using Sunny.UI;
using HB_CameraUI;
using CustemerUI;
using PlcConectCom.WinForms.Controls;

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
    private sealed class StartupLoadingForm : Form
    {
        public StartupLoadingForm()
        {
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            ShowIcon = false;
            ShowInTaskbar = false;
            ControlBox = false;
            TopMost = true;
            MinimizeBox = false;
            MaximizeBox = false;
            Text = "系统启动中";
            Width = 420;
            Height = 160;

            Label tipLabel = new()
            {
                Dock = DockStyle.Top,
                Height = 58,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Microsoft YaHei UI", 11F),
                Text = "系统正在加载，请稍候..."
            };

            ProgressBar bar = new()
            {
                Dock = DockStyle.Top,
                Height = 18,
                Margin = new Padding(24, 0, 24, 0),
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 35
            };

            Panel barHost = new()
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(24, 10, 24, 0)
            };
            barHost.Controls.Add(bar);

            Controls.Add(barHost);
            Controls.Add(tipLabel);
        }
    }

    private sealed class MainProcessPlcBridge : IExternalPlcBridge
    {
        private readonly HB_HMI_MainForm _owner;

        public MainProcessPlcBridge(HB_HMI_MainForm owner)
        {
            _owner = owner;
        }

        public bool ReadBool(string address) => _owner.ReadPlcBoolFromMain(address);

        public void WriteBool(string address, bool value) => _owner.WritePlcBoolFromMain(address, value);

        public ushort ReadWord(string address) => _owner.ReadPlcWordFromMain(address);

        public void WriteWord(string address, ushort value) => _owner.WritePlcWordFromMain(address, value);

        public float ReadFloat(string address) => _owner.ReadPlcFloatFromMain(address);

        public void WriteFloat(string address, float value) => _owner.WritePlcFloatFromMain(address, value);
    }

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

        public string ShiftName { get; set; } = string.Empty;
    }

    // 角色权限配置模型
    private sealed class RoleAuthConfig
    {
        public string AdminPassword { get; set; } = "admin123";

        public string EngineerPassword { get; set; } = "engineer123";

        /// <summary>作业员密码（如玻璃电池串人工确认等）；仅工程师/管理员可在「密码管理」中修改。</summary>
        public string OperatorPassword { get; set; } = "operator123";
    }

    /// <summary>
    /// 应用级鼠标/键盘活动过滤器：把鼠标移动 / 点击 / 滚轮 / 按键消息上报给主窗体，
    /// 用于统计「主界面无操作」时长（不拦截消息，<see cref="PreFilterMessage"/> 恒返回 false）。
    /// </summary>
    /// <remarks>
    /// 重要：光标停在主页面时，PLC 实时数据 / 图表 / 看板刷新会频繁改变光标下方的内容，
    /// 系统会在<b>光标未动</b>的情况下重新补发 <c>WM_MOUSEMOVE</c>。因此移动消息必须比对
    /// 物理屏幕坐标，只有坐标真的变化了才算一次活动；点击 / 滚轮 / 按键则无条件算活动。
    /// </remarks>
    private sealed class MouseActivityMessageFilter : IMessageFilter
    {
        private const int WmKeyDown = 0x0100;
        private const int WmKeyUp = 0x0101;
        private const int WmSysKeyDown = 0x0104;
        private const int WmSysKeyUp = 0x0105;
        private const int WmMouseMove = 0x0200;
        private const int WmLButtonDown = 0x0201;
        private const int WmLButtonUp = 0x0202;
        private const int WmRButtonDown = 0x0204;
        private const int WmMButtonDown = 0x0207;
        private const int WmMouseWheel = 0x020A;

        private readonly Action _onMouseActivity;
        private Point _lastMovePosition;
        private bool _hasLastMovePosition;

        public MouseActivityMessageFilter(Action onMouseActivity)
        {
            _onMouseActivity = onMouseActivity ?? throw new ArgumentNullException(nameof(onMouseActivity));
        }

        public bool PreFilterMessage(ref Message m)
        {
            switch (m.Msg)
            {
                case WmKeyDown:
                case WmKeyUp:
                case WmSysKeyDown:
                case WmSysKeyUp:
                    _onMouseActivity();
                    break;

                case WmMouseMove:
                    // 坐标没变 = 光标未动的补发消息（布局刷新导致），不算活动
                    // 注意工程里 using 了 DataVisualization.Charting（其也有 Cursor 类），须全限定
                    Point pos = System.Windows.Forms.Cursor.Position;
                    if (_hasLastMovePosition && pos == _lastMovePosition)
                        return false;
                    _lastMovePosition = pos;
                    _hasLastMovePosition = true;
                    _onMouseActivity();
                    break;
            }

            return false;
        }
    }

    // 主 PLC（Modbus TCP）连接参数，由程序目录下 MainPlc.config.json 提供（含 IP 与心跳 BOOL 地址）
    private sealed class MainPlcConfig
    {
        public string IpAddress { get; set; } = "192.168.1.88";

        public int Port { get; set; } = 502;

        public int SlaveId { get; set; } = 1;


        /// <summary>PLC 心跳 BOOL 地址（如 MB100）。空则连接后不做双写握手，探测仍读 MB0。</summary>
        public string HeartbeatBoolAddress { get; set; } = string.Empty;

        /// <summary>每日产量统计使用的 PLC 计数地址。</summary>
        public string ProductionCounterAddress { get; set; } = "MW100";
    }

    /// <summary>主页面左侧 PLC 显示区与客户看板的组合方式（持久化到 MainPageDisplay.config.json）。</summary>
    private sealed class MainPageDisplayConfig
    {
        /// <summary>0=不显示客户看板；1=仅客户看板；2=与系统配置分页同时显示。</summary>
        public int DisplayMode { get; set; }

        /// <summary>0=排版机客户看板；1=小料机客户看板。主页面嵌入与「客户界面」按钮共用。</summary>
        public int CustomerBoard { get; set; }

        /// <summary>
        /// 自动运行信号 PLC 地址（布尔位）。读到 true 时视为设备自动运行中，
        /// 主页面导航区的「调试按钮」「工站控制」被禁用；留空表示不启用该限制。
        /// </summary>
        public string AutoRunningAddress { get; set; } = string.Empty;

        /// <summary>
        /// PLC 版本号寄存器地址（MW 字起始地址，按 ASCII 字符串读取）。
        /// 读取到的字符串显示在主页面底部状态栏；留空表示不显示。
        /// </summary>
        public string PlcVersionAddress { get; set; } = string.Empty;

        /// <summary>报警统计 Top3 的起始日期（yyyy-MM-dd）；留空表示不限制起点。</summary>
        public string TopAlarmStartDate { get; set; } = string.Empty;

        /// <summary>报警统计 Top3 的结束日期（yyyy-MM-dd，含当天）；留空表示不限制终点。</summary>
        public string TopAlarmEndDate { get; set; } = string.Empty;

        /// <summary>
        /// 主界面无鼠标操作自动降级为作业员权限的时限（秒，10~60）。
        /// 在系统配置→主页面显示配置页可调，范围外的值读取时会被夹紧。
        /// </summary>
        public int RoleAutoDowngradeSeconds { get; set; } = DefaultRoleAutoDowngradeSeconds;
    }

    private sealed class CameraThreadStartupConfig
    {
        public bool HandleCam1 { get; set; } = true;
        public bool HandleCam2 { get; set; } = true;
        public bool HandleCam3 { get; set; } = true;
        public bool HandleCam4 { get; set; } = true;
        public bool HandleCam5 { get; set; } = true;
        public bool HandleCam6 { get; set; } = true;
        public bool HandleCam1LightCheck { get; set; } = true;
        public bool HandleCam2LightCheck { get; set; } = true;
        public bool HandleCam3LightCheck { get; set; } = true;
        public bool HandleCam4LightCheck { get; set; } = true;
    }

    private global::PlcConectCom.PlcCommunicator? _plc;

    /// <summary>调试按钮窗口打开时为 true：仅调试按钮页内控件可与 PLC 读写（状态栏心跳探测仍使用 <see cref="_plc"/>）。</summary>
    private bool _debugToolsPlcExclusiveActive;

    private const string MainPlcConfigFileName = "MainPlc.config.json";
    private const string MainPageDisplayConfigFileName = "MainPageDisplay.config.json";
    private const string CameraThreadStartupConfigFileName = "CameraThreadStartup.config.json";
    private const string CameraUiSettingFileName = "SettingUser.custom.json";
    private const string AuthConfigFileName = "RoleAuth.config.json";
    private const string AlarmHistoryDbFileName = "AlarmHistory.db";
    private const string RemoteKeySignSecret = "PLC_ROLE_AUTH_2026";
    private const string ProductionModeDay = "日显示";
    private const string ProductionModeWeek = "周显示";
    private const string ProductionModeMonth = "月显示";
    /// <summary>设备未配置心跳地址时用于连通性探测的回退地址。</summary>
    private const string DefaultHeartbeatAddress = "MX1021.6";
    private static readonly JsonSerializerOptions JsonFileOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>导航按钮字体（静态复用：切页时反复 new Font 会累积 GDI 句柄）。</summary>
    private static readonly Font NavigationFont = new("Microsoft YaHei UI", 10F, FontStyle.Bold);
    private static readonly Font NavigationActiveFont = new("Microsoft YaHei UI", 10.5F, FontStyle.Bold);

    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _monitorRefreshTimer = new() { Interval = 250 };
    private readonly StatusStrip _statusStrip = new() { Dock = DockStyle.Bottom };
    private readonly ToolStripStatusLabel _lblPlcStatus = new() { Text = "PLC: 未连接" };
    private readonly ToolStripStatusLabel _lblPlcManager = new() { Text = "PLC 设备管理", IsLink = true, ToolTipText = "配置 PLC 设备及功能区绑定" };
    private readonly ToolStripStatusLabel _lblReconnectFail = new() { Text = "失败重连次数: 0" };
    private readonly ToolStripStatusLabel _lblHeartbeat = new() { Text = "心跳: ●", ForeColor = Color.DimGray };
    private readonly ToolStripStatusLabel _lblSubHeartbeat = new() { Text = "子PLC心跳: 未启用", ForeColor = Color.DimGray };
    private readonly ToolStripStatusLabel _lblAutoRunning = new() { Text = "自动运行: 未配置", ForeColor = Color.DimGray };
    private readonly ToolStripStatusLabel _lblPlcVersion = new() { Text = "PLC版本号: 未配置", ForeColor = Color.DimGray };
    /// <summary>自动运行信号为 true：禁用主页面「调试按钮」「工站控制」。</summary>
    private bool _autoRunningBlocked;
    /// <summary>自动运行地址读取失败是否已记录过日志（避免每秒刷屏）。</summary>
    private bool _autoRunningReadErrorLogged;
    /// <summary>PLC 版本号地址读取失败是否已记录过日志（避免每秒刷屏）。</summary>
    private bool _plcVersionReadErrorLogged;
    /// <summary>状态栏最近一次显示的 PLC 版本号（值未变化时跳过 UI 刷新）。</summary>
    private string _lastPlcVersionText = string.Empty;
    /// <summary>PLC 版本号字符串的最大字符数（15 个 MW 寄存器）；实际按 \0 截止，多余字符忽略。</summary>
    private const int PlcVersionMaxChars = 30;
    private int _failedReconnectCount;
    private bool _lastConnectedState;
    private bool _heartbeatFlash;
    private bool _subHeartbeatFlash;
    private bool _disconnectAlreadyCounted;
    private bool _statusProbeBusy;
    private readonly HashSet<string> _subHeartbeatFailed = new(StringComparer.OrdinalIgnoreCase);
    private bool _refreshingRealtimeBoolLabels;
    private bool _refreshingMonitorSignals;
    private bool _isConnecting;
    private bool _autoConnectTriggered;
    private bool _mainPageAutoOpenedOnStartup;
    private bool _suspendRoleChanged;
    private UserRole _currentRole = UserRole.作业员;
    /// <summary>主界面无鼠标 / 键盘操作自动降级为作业员权限的默认时限（秒）；实际值取 MainPageDisplay.config.json。</summary>
    private const int DefaultRoleAutoDowngradeSeconds = 50;
    private const int MinRoleAutoDowngradeSeconds = 10;
    private const int MaxRoleAutoDowngradeSeconds = 60;
    /// <summary>当前生效的无操作降级时限（秒），由系统配置→主页面显示配置页设置，范围 10~60。</summary>
    private int RoleAutoDowngradeSeconds => Math.Clamp(
        _mainPageDisplayConfig.RoleAutoDowngradeSeconds,
        MinRoleAutoDowngradeSeconds,
        MaxRoleAutoDowngradeSeconds);
    /// <summary>最近一次鼠标活动时间（UTC），由 <see cref="MouseActivityMessageFilter"/> 实时刷新。</summary>
    private DateTime _lastMouseActivityUtc = DateTime.UtcNow;
    private MouseActivityMessageFilter? _mouseActivityFilter;
    private readonly string _authConfigPath;
    private readonly string _mainPlcConfigPath;
    private readonly string _plcDeviceConfigPath;
    private readonly string _mainPageDisplayConfigPath;
    private readonly string _cameraThreadStartupConfigPath;
    private readonly string _cameraUiSettingPath;
    private readonly string _alarmHistoryDbPath;
    private readonly string _alarmHistoryConnectionString;
    private RoleAuthConfig _authConfig = new();
    private MainPlcConfig _mainPlcConfig = new();
    private PlcDeviceConfiguration _plcDeviceConfiguration = new();
    private readonly ConcurrentDictionary<string, global::PlcConectCom.PlcCommunicator> _plcDevices = new(StringComparer.OrdinalIgnoreCase);
    private PlcDeviceManagerWindow? _plcDeviceManagerWindow;
    private CameraThreadStartupConfig _cameraThreadStartupConfig = new();

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
    // PLC 设备管理不从导航区显示（功能保留：底部状态栏的「PLC 设备管理」链接、
    // 「PLC: 连接中断/成功」状态文字仍可打开该窗口），需要恢复显示时把 Visible 改回 true
    private readonly UIButton _btnPlcDeviceManager = new() { Text = "PLC 设备管理", Width = 164, Height = 42, Margin = new Padding(0, 0, 0, 8), Visible = false };
    private readonly UIButton _btnPageDebug = new() { Text = "系统配置", Width = 164, Height = 48, Margin = new Padding(0, 0, 0, 8) };
    private readonly UIButton _btnPageDebugTools = new() { Text = "调试按钮", Width = 164, Height = 48, Margin = new Padding(0, 0, 0, 8) };
    private readonly UIButton _btnPageStationControl = new() { Text = "工站控制", Width = 164, Height = 48, Margin = new Padding(0, 0, 0, 8) };
    private readonly UIButton _btnPageDianCiHan = new() { Text = "电磁焊", Width = 164, Height = 48, Margin = new Padding(0, 0, 0, 8) };
    // MES 入口按钮不显示在导航区（功能保留：后台自启 + OpenMesWindow 仍可打开），
    // 需要恢复显示时把 Visible 改回 true 即可，其余接线无需改动
    private readonly UIButton _btnPageMes = new() { Text = "MES", Width = 164, Height = 48, Margin = new Padding(0, 0, 0, 8), Visible = false };
    private readonly UIButton _btnPageCameraDebug = new() { Text = "相机调试", Width = 164, Height = 48, Margin = new Padding(0, 0, 0, 8) };
    private readonly UIButton _btnPageMonitor = new() { Text = "状态监控", Width = 164, Height = 48, Margin = new Padding(0, 0, 0, 8) };
    private readonly UIButton _btnPageRecords = new() { Text = "记录查询", Width = 164, Height = 48, Margin = new Padding(0, 0, 0, 8) };
    private readonly UIButton _btnPageParameterAdjust = new() { Text = "参数调整", Width = 164, Height = 48, Margin = new Padding(0, 0, 0, 8) };
    private readonly UIButton _btnParameterChangeRecords = new() { Text = "参数修改记录", Width = 164, Height = 44, Margin = new Padding(0, 0, 0, 8) };
    private readonly UIButton _btnManualOperationLog = new() { Text = "手动操作日志", Width = 164, Height = 44, Margin = new Padding(0, 0, 0, 8) };
    private readonly UIButton _btnPageProduction = new() { Text = "产能统计", Width = 164, Height = 48, Margin = new Padding(0, 0, 0, 8) };
    private readonly UIButton _btnPageCustemerUi = new() { Text = "客户界面", Width = 164, Height = 48, Margin = new Padding(0, 0, 0, 8) };
    private readonly UIComboBox _cmbUserRole = new() { Width = 164, DropDownStyle = UIDropDownStyle.DropDownList };
    private readonly UIButton _btnPasswordManage = new() { Text = "密码管理", Width = 164, Height = 34, Margin = new Padding(0, 8, 0, 0) };
    private readonly UIButton _btnButtonAreaLayoutEdit = new() { Text = "按钮区布局编辑", Width = 164, Height = 34, Margin = new Padding(0, 8, 0, 0) };

    // 页面容器：静态挂载的四个页面
    private readonly Panel _pageHost = new() { Dock = DockStyle.Fill };
    private readonly Panel _mainPage = new() { Dock = DockStyle.Fill };
    private readonly Panel _debugPage = new() { Dock = DockStyle.Fill };
    private readonly Panel _debugToolsPage = new() { Dock = DockStyle.Fill };
    private readonly UITabControl _debugToolsTab = new() { Dock = DockStyle.Fill };
    private readonly Panel _monitorPage = new() { Dock = DockStyle.Fill };
    private readonly UITabControl _monitorTab = new() { Dock = DockStyle.Fill };
    private readonly TabPage _tabMonitorSignals = new() { Text = "状态监控", Padding = new Padding(2) };
    private readonly Panel _recordsPage = new() { Dock = DockStyle.Fill };
    // 手动操作日志页（调试按钮页三类操作：点击按钮 / 修改位置 / 点击输出点）
    private readonly Panel _manualOperationLogPage = new() { Dock = DockStyle.Fill };
    private ManualOperationLogPageControl? _manualOperationLogControl;
    private readonly Panel _parameterAdjustPage = new() { Dock = DockStyle.Fill, Padding = new Padding(8) };
    private readonly ParameterAdjustControl _parameterAdjustControl = new() { Dock = DockStyle.Fill };
    private readonly Panel _productionPage = new() { Dock = DockStyle.Fill, Padding = new Padding(8) };
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
    private readonly FlowLayoutPanel _productionToolbar = new() { Dock = DockStyle.Top, Height = 86, Padding = new Padding(8), WrapContents = true, AutoScroll = true };
    private readonly ComboBox _cmbProductionPeriod = new() { Width = 80, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly DateTimePicker _dtProductionAnchor = new() { Width = 130, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd" };
    private readonly DateTimePicker _tmDayShiftStart = new() { Width = 80, Format = DateTimePickerFormat.Time, ShowUpDown = true };
    private readonly DateTimePicker _tmDayShiftEnd = new() { Width = 80, Format = DateTimePickerFormat.Time, ShowUpDown = true };
    private readonly DateTimePicker _tmNightShiftStart = new() { Width = 80, Format = DateTimePickerFormat.Time, ShowUpDown = true };
    private readonly DateTimePicker _tmNightShiftEnd = new() { Width = 80, Format = DateTimePickerFormat.Time, ShowUpDown = true };
    private readonly UIButton _btnRefreshProductionChart = new() { Text = "刷新趋势图", Width = 96 };
    private readonly UITextBox _txtProductionCounterAddress = new() { Width = 90, Text = "MW100" };
    private readonly Label _lblProductionSummary = new()
    {
        Dock = DockStyle.Top,
        Height = 28,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(8, 0, 0, 0),
        Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold),
        ForeColor = Color.FromArgb(28, 58, 120)
    };
    private readonly FlowLayoutPanel _productionColorLegend = new()
    {
        Dock = DockStyle.Top,
        Height = 30,
        Padding = new Padding(8, 4, 0, 0),
        WrapContents = false
    };

    // 产能统计配色：白班＝蓝（主色，代表有效产出），夜班＝灰（次级，不与白班抢注意力）。
    // 图例、周/月堆叠柱、日模式小时柱都引用这两个常量，改色只改这里。
    private static readonly Color ProductionDayShiftColor = Color.FromArgb(52, 103, 235);
    private static readonly Color ProductionNightShiftColor = Color.FromArgb(146, 154, 168);
    /// <summary>日模式「当班产量」累计折线：中性深灰蓝，避开白班蓝 / 夜班灰的语义。</summary>
    private static readonly Color ProductionCumulativeColor = Color.FromArgb(72, 88, 112);
    /// <summary>日模式「总产量」折线：橙色，与班次色系区分。</summary>
    private static readonly Color ProductionTotalColor = Color.FromArgb(235, 102, 52);

    private readonly Chart _productionChart = new() { Dock = DockStyle.Fill };
    private bool _productionChartAttached;
    // 报警记录历史表格
    private readonly UIDataGridView _recordAlarmGrid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeColumns = false,
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
        AllowUserToResizeColumns = false,
        RowHeadersVisible = false,
        ReadOnly = true,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false
    };
    /// <summary>主页底部报警区容器：标题在上，表格在下。</summary>
    private readonly Panel _mainAlarmGridSection = new() { Dock = DockStyle.Fill };
    private readonly Panel _mainAlarmHeaderPanel = new() { Dock = DockStyle.Top, Height = MainPageAlarmGridCaptionHeight };
    private readonly UIButton _btnMainLogQuery = new() { Text = "日志查询", Width = 88, Height = 26 };
    // 报警统计 Top3 标签集合
    private Label[] _topAlarmLabels = [];

    // 主页面右侧区域按分辨率自适应的缩放系数（1080p 及以上为 1.0）
    private float _mainRightUiScale = 1f;
    private readonly Dictionary<string, (string name, string code, int count)> _alarmStats = [];
    // Top3 统计当前所处的自然日：跨天后按配置的日期区间重新统计（区间为「至今天」时尤其必要）
    private DateTime _topAlarmStatsDay = DateTime.Today;
    private readonly System.Windows.Forms.Timer _productionPollTimer = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer _processRealtimeMainRebuildDebounce = new() { Interval = 400 };
    private bool? _lastProductionCounterSignal;
    private bool _productionPollBusy;
    // 主页面右侧实时工艺 + 按钮区：上部可滚动，按钮区 Dock.Fill 延伸至容器底部
    private readonly Panel _mainRealtimePanel = new() { Dock = DockStyle.Fill };
    private readonly Panel _mainProcessRealtimeScrollHost = new() { Dock = DockStyle.Top, AutoScroll = true, Height = 200 };
    private readonly FlowLayoutPanel _mainProcessRealtimeFlow = new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        WrapContents = false,
        FlowDirection = FlowDirection.TopDown,
        Margin = new Padding(0)
    };
    private readonly Panel _mainTopOverlayHost = new() { Dock = DockStyle.Top, Height = 210, Padding = new Padding(8, 8, 8, 4), BackColor = Color.Transparent };
    private readonly Panel _mainTopOverlayCanvas = new() { Dock = DockStyle.Fill, BackColor = Color.Transparent };
    // 按钮显示区外框：不带边框（标题字号与「实时工艺数据」一致，均为 12F 粗体深蓝）
    private readonly Panel _mainButtonDisplaySection = new() { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 0, 6) };
    private readonly Label _lblMainButtonDisplayTitle = new()
    {
        Text = "按钮显示区",
        Dock = DockStyle.Top,
        Height = 30,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold),
        ForeColor = Color.FromArgb(28, 58, 120)
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
    // 三个显示区标题条：蓝底白字（数值显示 / 功能按钮 / 状态显示），标题条本身充当分隔
    private static Label CreatePlcSectionTitle(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Top,
        Height = 26,
        TextAlign = ContentAlignment.MiddleLeft,
        BackColor = Color.FromArgb(47, 92, 158),
        ForeColor = Color.White,
        Padding = new Padding(6, 0, 0, 0),
        Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold)
    };
    private readonly Label _lblMainPlcBoolLabelTitle = CreatePlcSectionTitle("状态显示");
    private readonly FlowLayoutPanel _mainPlcBoolLabelPanel = new()
    {
        Dock = DockStyle.Fill,
        WrapContents = true,
        AutoScroll = true,
        FlowDirection = FlowDirection.LeftToRight,
        Padding = new Padding(4)
    };
    // PLC 显示区内的横向按钮显示区（标题条蓝底白字，兼作与上/下显示区的分隔）
    private readonly Panel _mainPlcButtonSection = new() { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 0, 6) };
    private readonly Label _lblMainPlcButtonTitle = CreatePlcSectionTitle("功能按钮");
    private readonly FlowLayoutPanel _mainPlcButtonPanel = new()
    {
        Dock = DockStyle.Fill,
        WrapContents = true,
        AutoScroll = true,
        FlowDirection = FlowDirection.LeftToRight,
        Padding = new Padding(4)
    };
    private readonly Panel _mainPlcValueTextBoxSection = new() { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 0, 6) };
    private readonly Label _lblMainPlcValueTextBoxTitle = CreatePlcSectionTitle("数值显示");
    private readonly FlowLayoutPanel _mainPlcValueTextBoxPanel = new()
    {
        Dock = DockStyle.Fill,
        WrapContents = true,
        AutoScroll = true,
        FlowDirection = FlowDirection.LeftToRight,
        Padding = new Padding(4)
    };
    private readonly List<PlcBoolLabe> _mainPlcBoolLabelControls = [];
    private readonly List<PlcBoundButton> _mainPlcButtonControls = [];
    private readonly List<PlcValueTextBoxControl> _mainPlcValueTextBoxControls = [];
    private readonly Panel _mainUpperDisplayHost = new() { Dock = DockStyle.Fill };
    private readonly TableLayoutPanel _panelMainSystemPlc = new()
    {
        Dock = DockStyle.Fill,
        ColumnCount = 1,
        RowCount = 2,
        Padding = new Padding(0)
    };
    private readonly Panel _parkHostForMainSystemPlc = new() { Visible = false, Size = new Size(1, 1), Margin = new Padding(0) };
    private readonly TabControl _tabMainPageDual = new() { Dock = DockStyle.Fill, Padding = new Point(4, 4) };
    private readonly Panel _panelDualTabSystemHost = new() { Dock = DockStyle.Fill };
    private readonly Panel _panelDualTabCustemerHost = new() { Dock = DockStyle.Fill };
    private readonly Panel _panelCustemerEmbedHost = new() { Dock = DockStyle.Fill };
    private readonly TabPage _tabPageMainPlcDisplay = new() { Text = "PLC 显示", Padding = new Padding(2) };
    private readonly TabPage _tabPageMainCustemer = new() { Text = "客户看板", Padding = new Padding(2) };
    private Form? _embeddedCustemerForm;
    private MainPageDisplayConfig _mainPageDisplayConfig = new();
    private readonly TabPage _tabMainPageDisplayConfig = new() { Text = "主页面显示配置", Padding = new Padding(12) };
    private readonly FlowLayoutPanel _flowMainPageDisplayConfig = new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = true,
        Padding = new Padding(8)
    };
    private readonly Label _lblMainPageDisplayTitle = new()
    {
        AutoSize = true,
        Margin = new Padding(0, 0, 0, 8),
        Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
        Text = "主页面是否显示客户看板"
    };
    private readonly UIComboBox _cmbMainPageDisplayMode = new() { Width = 360, DropDownStyle = UIDropDownStyle.DropDownList, Margin = new Padding(0, 0, 0, 12) };
    private readonly Label _lblCustomerBoardTitle = new()
    {
        AutoSize = true,
        Margin = new Padding(0, 8, 0, 8),
        Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
        Text = "客户看板（主页面嵌入 / 点击「客户界面」）"
    };
    private readonly UIComboBox _cmbCustomerBoard = new() { Width = 360, DropDownStyle = UIDropDownStyle.DropDownList, Margin = new Padding(0, 0, 0, 12) };
    private readonly Label _lblAutoRunningAddressTitle = new()
    {
        AutoSize = true,
        Margin = new Padding(0, 8, 0, 8),
        Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
        Text = "自动运行中的 PLC 地址"
    };
    private readonly UITextBox _txtAutoRunningAddress = new() { Width = 360, Margin = new Padding(0, 0, 0, 4), Text = "" };
    private readonly Label _lblAutoRunningAddressHint = new()
    {
        AutoSize = true,
        MaximumSize = new Size(520, 0),
        ForeColor = Color.DimGray,
        Margin = new Padding(0, 0, 0, 12),
        Text = "填写布尔地址（如 MX1021.6）。该地址读到 true 时，主页面导航区的「调试按钮」与「工站控制」自动禁用；留空表示不做限制。"
    };
    // PLC 版本号寄存器地址（状态栏显示）
    private readonly Label _lblPlcVersionAddressTitle = new()
    {
        AutoSize = true,
        Margin = new Padding(0, 8, 0, 8),
        Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
        Text = "PLC 版本号地址"
    };
    private readonly UITextBox _txtPlcVersionAddress = new() { Width = 360, Margin = new Padding(0, 0, 0, 4), Text = "" };
    private readonly Label _lblPlcVersionAddressHint = new()
    {
        AutoSize = true,
        MaximumSize = new Size(520, 0),
        ForeColor = Color.DimGray,
        Margin = new Padding(0, 0, 0, 12),
        Text = "填写字符串起始地址（如 MW300），按 ASCII 字符串读取（最多 30 个字符，遇 \\0 截止）。读取结果显示在主页面底部状态栏「PLC版本号」；留空表示不显示。"
    };
    // 报警统计 Top3 的日期区间（与记录查询页同款勾选式日期控件：不勾选=该侧不限制）
    private readonly Label _lblTopAlarmRangeTitle = new()
    {
        AutoSize = true,
        Margin = new Padding(0, 8, 0, 8),
        Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
        Text = "报警统计 Top3 日期区间"
    };
    private readonly FlowLayoutPanel _panelTopAlarmRange = new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Margin = new Padding(0, 0, 0, 4),
        Height = 30
    };
    private readonly DateTimePicker _dtTopAlarmFrom = new()
    {
        Width = 150,
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "yyyy-MM-dd",
        ShowCheckBox = true,
        Checked = false,
        Margin = new Padding(0)
    };
    private readonly DateTimePicker _dtTopAlarmTo = new()
    {
        Width = 150,
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "yyyy-MM-dd",
        ShowCheckBox = true,
        Checked = false,
        Margin = new Padding(0)
    };
    private readonly Label _lblTopAlarmRangeHint = new()
    {
        AutoSize = true,
        MaximumSize = new Size(520, 0),
        ForeColor = Color.DimGray,
        Margin = new Padding(0, 0, 0, 12),
        Text = "勾选左侧方框后选日期，主页面「报警统计 Top3」只统计该区间内入库的报警（含首尾两天）；"
              + "两侧都不勾选则只统计当天（跨零点自动重置）。区间不影响记录查询页的历史记录。"
    };
    private readonly Label _lblTopAlarmRangeSeparator = new()
    {
        AutoSize = true,
        Margin = new Padding(6, 6, 6, 0),
        Font = new Font("Microsoft YaHei UI", 10F),
        Text = "至"
    };
    // 主界面无鼠标操作自动降级为作业员权限的时限（10~60 秒）
    private readonly Label _lblRoleDowngradeTitle = new()
    {
        AutoSize = true,
        Margin = new Padding(0, 8, 0, 8),
        Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
        Text = "无操作自动降级时限（鼠标 / 键盘）"
    };
    private readonly NumericUpDown _numRoleDowngradeSeconds = new()
    {
        Width = 120,
        Minimum = MinRoleAutoDowngradeSeconds,
        Maximum = MaxRoleAutoDowngradeSeconds,
        Value = DefaultRoleAutoDowngradeSeconds,
        Increment = 5,
        Margin = new Padding(0, 0, 0, 4)
    };
    private readonly Label _lblRoleDowngradeHint = new()
    {
        AutoSize = true,
        MaximumSize = new Size(520, 0),
        ForeColor = Color.DimGray,
        Margin = new Padding(0, 0, 0, 12),
        Text = "主界面超过该秒数没有任何鼠标 / 键盘 / 触摸操作时，自动从工程师 / 管理员降级为作业员权限，"
              + "只统计本程序窗口内的操作（切到其它程序操作不影响降级）。范围 10~60 秒，弹窗打开期间暂停计时。"
              + "降级无需密码；再次升权仍需输入密码。"
    };
    private readonly UIButton _btnSaveMainPageDisplay = new() { Text = "保存", Width = 120, Height = 34, Margin = new Padding(0, 0, 0, 12) };
    private readonly Label _lblMainPageDisplayHint = new()
    {
        AutoSize = true,
        MaximumSize = new Size(520, 0),
        ForeColor = Color.DimGray,
        Text = "保存后立即生效。点击导航「客户界面」打开所选看板的独立窗口；主页面按上面的选项决定是否嵌入同一看板。PLC 使用客户界面绑定。"
    };

    // 相机工程配置（系统配置页）：替代原「选择相机工程」弹框。
    // 保存后主程序启动时加载，点击导航「相机调试」直接打开配置好的工程，
    // 关闭相机工程页面不影响后台常驻的相机自动流程（大华 Cam1/2/3Auto 由 DahuaAutoCamService 常驻）。
    private readonly TabPage _tabCameraProjectConfig = new() { Text = "相机工程配置", Padding = new Padding(12) };
    private readonly FlowLayoutPanel _flowCameraProjectConfig = new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = true,
        Padding = new Padding(8)
    };
    private readonly Label _lblCameraProjectTitle = new()
    {
        AutoSize = true,
        Margin = new Padding(0, 0, 0, 8),
        Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
        Text = "相机调试使用的相机工程"
    };
    private readonly UIComboBox _cmbCameraProject = new() { Width = 360, DropDownStyle = UIDropDownStyle.DropDownList, Margin = new Padding(0, 0, 0, 12) };
    private readonly UIButton _btnSaveCameraProject = new() { Text = "保存", Width = 120, Height = 34, Margin = new Padding(0, 0, 0, 12) };
    private readonly Label _lblCameraProjectHint = new()
    {
        AutoSize = true,
        MaximumSize = new Size(520, 0),
        ForeColor = Color.DimGray,
        Text = "保存后立即生效：主程序启动时自动加载本选项，点击导航区「相机调试」直接打开所选工程，不再弹出选择框。"
    };
    /// <summary>启动时从 camera.project.selection.json 加载、在系统配置保存后更新的相机工程选项。</summary>
    private CameraProjectKind _selectedCameraProject = CameraProjectKind.HikvisionLayout;
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
    /// <summary>Dock=Top + AutoSize：总高度可超过工站区域，由外层 Panel 纵向滚动；子控件上滚轮转发到该区域。</summary>
    private readonly FlowLayoutPanel _stationDisplayPanel = new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        AutoScroll = false,
        WrapContents = true,
        FlowDirection = FlowDirection.LeftToRight,
        Padding = new Padding(4),
        Margin = new Padding(0)
    };
    private readonly List<StationOperationStatusControl> _stationControls = [];
    private const int StationDisplayColumns = 6;
    private const int MainPageAlarmGridCaptionHeight = 28;
    /// <summary>主页左侧底部报警整块高度（含「实时报警」标题条 + 表格）。</summary>
    private const int MainPageAlarmGridSectionHeight = 380;

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
    private readonly UIButton _btnImportManualOperationPromptData = new() { Text = "导入Excel", Width = 96, Height = 30 };
    private readonly AxisMotionGroupTableControl _axisMotionControl;
    private readonly PositionParameterTableControl _positionParameterTableControl = new() { Dock = DockStyle.Fill };
    private readonly BoolSignalCollectionControl _boolSignalControl = new() { Dock = DockStyle.Fill };
    private readonly BoolGroupControlTable _boolGroupControlTable = new() { Dock = DockStyle.Fill };
    private readonly Panel _boolGroupTabHost = new() { Dock = DockStyle.Fill };
    private readonly SplitContainer _splitBoolGroupAndInputMatrix = new()
    {
        Dock = DockStyle.Fill,
        Orientation = Orientation.Vertical,
        SplitterWidth = 6,
        BorderStyle = BorderStyle.FixedSingle
    };
    private readonly BoolSignalMatrixEmbedControl _boolSignalInputMatrixEmbed = new() { Dock = DockStyle.Fill };
    /// <summary>输出点位页左侧表格占 SplitContainer 宽度比，右侧为输入信号点阵（约 0.62→0.49 以加宽点阵）。</summary>
    private const double BoolGroupOutputLeftPanelWidthFraction = 0.49;
    private bool _boolGroupSplitLayoutApplied;
    private readonly AlarmAddressManagerControl _alarmAddressManagerControl = new() { Dock = DockStyle.Fill };
    private readonly TabPage _tabAlarmPopupConfig = new() { Text = "报警弹窗配置", Padding = new Padding(2) };
    private readonly AlarmPopupManagerControl _alarmPopupManagerControl = new() { Dock = DockStyle.Fill };
    private readonly TabPage _tabManualOperationPromptConfig = new() { Text = "手动操作提示配置", Padding = new Padding(2) };
    private readonly ManualOperationPromptConfigControl _manualOperationPromptConfigControl = new() { Dock = DockStyle.Fill };
    private readonly TaskSettingsControl _taskSettingsControl = new() { Dock = DockStyle.Fill };
    private readonly CommunicationConfigControl _communicationConfigControl = new() { Dock = DockStyle.Fill };
    private TaskPipelineRunner? _taskPipelineRunner;
    private readonly TabPage _tabStationConfig = new() { Text = "工站配置", Padding = new Padding(2) };
    private readonly StationOperationConfigManagerControl _stationConfigControl = new() { Dock = DockStyle.Fill };
    private readonly TabPage _tabButtonDisplayConfig = new() { Text = "纵向按钮配置", Padding = new Padding(2) };
    // 主页面 PLC 显示区内「PLC 按钮显示区」（横向）的配置页
    private readonly TabPage _tabHorizontalButtonConfig = new() { Text = "横向按钮配置", Padding = new Padding(2) };
    private readonly HorizontalButtonConfigControl _horizontalButtonConfigControl = new() { Dock = DockStyle.Fill };
    private readonly TabPage _tabProcessRealtimeData = new() { Text = "工艺实时数据配置", Padding = new Padding(2) };
    private readonly TabPage _tabPlcBoolLabelDisplayConfig = new() { Text = "显示状态控件配置", Padding = new Padding(2) };
    private readonly TabPage _tabPlcValueTextBoxDisplayConfig = new() { Text = "文本显示控件配置", Padding = new Padding(2) };
    private readonly TabPage _tabCameraThreadStartupConfig = new() { Text = "相机线程启动配置", Padding = new Padding(6) };
    private readonly ButtonDisplayConfigControl _buttonDisplayConfigControl = new() { Dock = DockStyle.Fill };
    private readonly PlcBoolLabelDisplayConfigControl _plcBoolLabelDisplayConfigControl = new() { Dock = DockStyle.Fill };
    private readonly PlcValueTextBoxDisplayConfigControl _plcValueTextBoxDisplayConfigControl = new() { Dock = DockStyle.Fill };
    private sealed class ChildPlcModule
    {
        public required PlcDeviceProfile Device { get; init; }
        public required Panel SystemPage { get; init; }
        public required Panel DebugPage { get; init; }
        public required Panel StationPage { get; init; }
        public required Panel MonitorPage { get; init; }
        public required Panel ParameterAdjustPage { get; init; }
        public required ParameterAdjustControl ParameterAdjustControl { get; init; }
        public required AxisMotionGroupTableControl AxisControl { get; init; }
        public required PositionParameterTableControl PositionControl { get; init; }
        public required BoolGroupControlTable OutputControl { get; init; }
        public required BoolSignalMatrixEmbedControl InputMatrixControl { get; init; }
        public required BoolSignalCollectionControl MonitorControl { get; init; }
        public required ParameterTableControl[] ParameterTables { get; init; }
        public required UIButton[] ImportButtons { get; init; }
        public required StationOperationConfigManagerControl StationConfig { get; init; }
        public required ButtonDisplayConfigControl ButtonDisplayConfig { get; init; }
        /// <summary>子 PLC 的横向按钮配置（渲染到主页面 PLC 显示区的「功能按钮」区）。</summary>
        public required HorizontalButtonConfigControl HorizontalButtonConfig { get; init; }
        public required PlcBoolLabelDisplayConfigControl BoolDisplayConfig { get; init; }
        public required PlcValueTextBoxDisplayConfigControl ValueDisplayConfig { get; init; }
        public required ProcessRealtimeDataConfigControl ProcessDataConfig { get; init; }
        public required AlarmAddressManagerControl AlarmConfig { get; init; }
        public required AlarmPopupManagerControl AlarmPopupConfig { get; init; }
        public required ManualOperationPromptConfigControl ManualPromptConfig { get; init; }
    }

    private readonly List<ChildPlcModule> _childPlcModules = [];

    // 子 PLC 当前活跃报警快照（键为设备 Id），与主 PLC 快照合并后显示在主页实时报警表格
    private readonly Dictionary<string, (string DeviceName, IReadOnlyList<AlarmTriggeredEvent> Alarms)> _childActiveAlarms = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<AlarmTriggeredEvent> _mainActiveAlarms = [];
    private TabControl? _systemConfigDeviceTabs;
    private TabControl? _debugToolsDeviceTabs;
    private TabControl? _stationDeviceTabs;
    private TabControl? _monitorDeviceTabs;
    private TabControl? _parameterAdjustDeviceTabs;
    /// <summary>工站面板宿主的隐藏停靠点：系统配置页「工站控制」标签删除后，弹窗关闭期间暂存于此。</summary>
    private readonly Panel _stationDeviceHostParking = new() { Visible = false };

    private readonly Panel? _stationDeviceTabHost = new() { Dock = DockStyle.Fill, BackColor = Color.White };
    private readonly List<Control> _childMainDisplayGroups = [];
    private readonly List<Control> _childProcessDisplayGroups = [];
    // 从机主页按钮/开关的统一刷新通道：子 PLC 连接状态翻转或按钮区重建后刷新其连接态显示
    private readonly List<PlcBoundButton> _childBoundButtons = [];
    private readonly List<PlcBoolSwitchControl> _childBoolSwitchControls = [];
    private readonly Dictionary<string, bool> _childLastProbeOk = [];
    private bool _childButtonsRefreshPending;
    // 底部运行日志文本框
    private readonly TextBox _txtLog = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, ReadOnly = true, Dock = DockStyle.Bottom, Height = 180 };
    /// <summary>界面日志框最多保留的行数；完整日志始终写入 Logs/AppLog_yyyy-MM-dd.db，可用「日志查询」查看。</summary>
    private const int MaxLiveLogLines = 1000;
    /// <summary>超限后一次裁掉的行数（批量裁剪，避免每行都触发整框重排）。</summary>
    private const int LiveLogTrimBatchLines = 500;
    private readonly object _liveLogSync = new();
    private readonly Queue<string> _liveLogPending = new();
    private readonly Queue<string> _liveLogShown = new();
    private int _liveLogFlushScheduled;
    private volatile bool _liveLogBoxVisible = true;
    private MainPageWindow? _mainPageWindow;
    private SystemConfigPageWindow? _systemConfigPageWindow;
    private DebugToolsPageWindow? _debugToolsPageWindow;
    private Form? _cameraDebugWindow;
    private Form? _custemerUiWindow;
    private Form? _stationControlPopupForm;
    private DianCiHan.Ui.DianCiHanForm? _dianCiHanWindow;
    /// <summary>MES 独立窗口（SCADA / MES 两个分页）。</summary>
    private MesScada.Ui.MesForm? _mesWindow;
    /// <summary>MES 设备上下文（把主 PLC 信号喂给 MES 状态机）。</summary>
    private MesDeviceContext? _mesContext;
    /// <summary>MES 轮询循环的取消令牌。</summary>
    private CancellationTokenSource? _mesPollCts;
    private MesScadaConfig _mesConfig = new();
    /// <summary>由「相机调试」THCamSetHostForm 绑定的相机控件，用于线程状态面板。</summary>
    private TH_CamSet? _boundThCamSet;
    private readonly TabPage _tabCameraThreads = new() { Text = "相机线程", Padding = new Padding(6) };
    private readonly Label _lblCameraThreadHint = new()
    {
        Dock = DockStyle.Top,
        Height = 52,
        Padding = new Padding(4, 6, 4, 4),
        Text = "显示相机工作线程运行状态。Cam3~6 的 movY、movR 为两侧均值；LightCheck 线程仅显示运行状态。"
    };
    private readonly Panel _cameraThreadScrollHost = new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        Padding = new Padding(0, 4, 0, 0)
    };
    private readonly TableLayoutPanel _cameraThreadTable = new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(4)
    };
    private const int CameraWorkThreadCount = 6;
    private const int CameraLightCheckThreadCount = 4;
    private const int CameraThreadMonitorRowCount = CameraWorkThreadCount + CameraLightCheckThreadCount;
    private readonly CheckBox[] _cameraThreadPauseChecks = new CheckBox[CameraWorkThreadCount];
    private readonly Dictionary<string, CheckBox> _cameraThreadStartupChecks = new(StringComparer.Ordinal);
    private readonly Label _lblCameraThreadStartupConfigHint = new()
    {
        Dock = DockStyle.Bottom,
        Height = 28,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.DimGray,
        Text = "未保存"
    };
    private readonly Label[] _cameraThreadAliveLabels = new Label[CameraThreadMonitorRowCount];
    private readonly Label[,] _cameraThreadValueLabels = new Label[CameraThreadMonitorRowCount, 4];
    private bool _syncingCameraPauseChecks;
    private readonly System.Windows.Forms.Timer _cameraThreadUiTimer = new() { Interval = 300 };
    private bool _cameraWorkThreadsStartedOnStartup;
    private bool _startupInitialized;
    private MonitorPageWindow? _monitorPageWindow;
    private RecordsPageWindow? _recordsPageWindow;
    private ManualOperationLogPageWindow? _manualOperationLogPageWindow;
    private ParameterAdjustPageWindow? _parameterAdjustPageWindow;
    private ProductionStatisticsPageWindow? _productionStatisticsPageWindow;
    private readonly MainProcessPlcBridge _cameraUiPlcBridge;
    private readonly object _cameraBridgeLogSync = new();
    private DateTime _lastCameraBridgeOfflineLogAt = DateTime.MinValue;
    private DateTime _lastCameraBridgeExceptionLogAt = DateTime.MinValue;

    private sealed record PlcBoolLabelBinding(Control Label, string Address, string Name);
    private sealed record PlcBoolLabelBindingConfig(Control Label, string Address, string DisplayName);

    public HB_HMI_MainForm()
    {
        _cameraUiPlcBridge = new MainProcessPlcBridge(this);
        _authConfigPath = Path.Combine(AppContext.BaseDirectory, AuthConfigFileName);
        _mainPlcConfigPath = Path.Combine(AppContext.BaseDirectory, MainPlcConfigFileName);
        _plcDeviceConfigPath = Path.Combine(AppContext.BaseDirectory, "PlcDevices.config.json");
        _mainPageDisplayConfigPath = Path.Combine(AppContext.BaseDirectory, MainPageDisplayConfigFileName);
        _cameraThreadStartupConfigPath = Path.Combine(AppContext.BaseDirectory, CameraThreadStartupConfigFileName);
        _cameraUiSettingPath = Path.Combine(AppContext.BaseDirectory, CameraUiSettingFileName);
        _alarmHistoryDbPath = Path.Combine(AppContext.BaseDirectory, AlarmHistoryDbFileName);
        _alarmHistoryConnectionString = $"Data Source={_alarmHistoryDbPath};Cache=Shared";
        InitializeComponent();
        // 手动操作日志的操作人：当前角色 + Windows 账号（每次写入时实时取值）
        ManualOperationLogStore.OperatorProvider = () => $"{_currentRole}({Environment.UserName})";
        // 必须在 InitializeComponent 之后再构造：避免在 UIForm/句柄树未就绪时创建含 DataGridView 的复杂控件导致启动失败
        _axisMotionControl = new AxisMotionGroupTableControl { Dock = DockStyle.Fill };
        if (IsDesignHost())
        {
            BuildDesignerPreviewUi();
            return;
        }

        LoadAuthConfig();
        LoadMainPlcConfig();
        _plcDeviceConfiguration = PlcDeviceConfiguration.Load(_plcDeviceConfigPath, new MainPlcLegacyConfig
        {
            IpAddress = _mainPlcConfig.IpAddress,
            Port = _mainPlcConfig.Port,
            SlaveId = _mainPlcConfig.SlaveId,
            HeartbeatBoolAddress = _mainPlcConfig.HeartbeatBoolAddress
        });
        SavePlcDeviceConfiguration();
        ApplyMainPlcConfigToUi();
        LoadMainPageDisplayConfig();
        LoadCameraProjectSelection();
        LoadCameraThreadStartupConfig();
        EnsureAlarmHistoryDatabase();
        LoadAlarmStatsFromDatabase();
        ApplyTopAlarmRangeTitle();
        ApplySunnyUiTheme();
        ApplySunnyUiControlStyles();
        BuildUi();
        InitializeResponsiveLayout();
        DataGridViewNoSortHelper.WireControlSubtreeForNoColumnSort(this);
        ApplyMainPageDisplayMode();
        BindEvents();
        BindRecordGrid(QueryAlarmHistoryFromDatabase(null, null));
        RefreshTopAlarmLabels();
        CameraUiExports.ConfigureHostLogSink(HandleCameraUiLogMessage);
        CameraUiExports.ConfigureExternalPlcBridge(_cameraUiPlcBridge);
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
        DataGridViewNoSortHelper.WireControlSubtreeForNoColumnSort(this);
        ApplyMainPageDisplayMode();
    }

    private void ApplySunnyUiTheme()
    {
        Style = UIStyle.Blue;
        TitleColor = Color.FromArgb(52, 103, 235);
        TitleForeColor = Color.White;
        RectColor = Color.FromArgb(52, 103, 235);
        // 窗口标题（左上角版本号）直接取程序集名（csproj 的 AssemblyName，如 HB_HMI_V1.0.0.1），
        // 以后升版本只改 csproj 即可，标题与 exe/dll 文件名永远一致
        Text = typeof(HB_HMI_MainForm).Assembly.GetName().Name;
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

        _cmbMainPageDisplayMode.Style = UIStyle.Blue;
        _cmbMainPageDisplayMode.FillColor = Color.FromArgb(240, 246, 255);
        _cmbMainPageDisplayMode.RectColor = Color.FromArgb(86, 116, 150);
        _cmbCustomerBoard.Style = UIStyle.Blue;
        _cmbCustomerBoard.FillColor = Color.FromArgb(240, 246, 255);
        _cmbCustomerBoard.RectColor = Color.FromArgb(86, 116, 150);

        ConfigureAlarmGridStyle(_mainAlarmGrid);
        ConfigureAlarmGridStyle(_recordAlarmGrid);
        ApplyMainLayoutStyle();
    }

    private IEnumerable<UIButton> GetUiButtons()
    {
        // 导航区按钮只在这里登记一次（GetNavigationButtons 是唯一来源），
        // 避免「某个按钮漏登记 → 拿不到统一字体/配色」这类问题再次出现
        foreach (UIButton navButton in GetNavigationButtons())
            yield return navButton;
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
        yield return _btnImportManualOperationPromptData;
        yield return _btnRefreshProductionChart;
        yield return _btnMainLogQuery;
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

        _productionPage.BackColor = Color.FromArgb(241, 245, 252);
        _productionPage.Padding = new Padding(8);
        _recordAlarmSection.BackColor = Color.White;
        _productionSection.BackColor = Color.White;
        _productionSection.Padding = new Padding(12);

        _stationAreaPanel.BackColor = Color.White;
        _stationAreaPanel.Padding = new Padding(6, 0, 6, 6);

        _mainLeftPanel.BackColor = Color.White;
        _mainAlarmGridSection.BackColor = Color.White;
        _mainRightPanel.BackColor = Color.White;
        _mainLeftPanel.Margin = new Padding(4);
        _mainRightPanel.Margin = new Padding(4);
        _mainLeftPanel.Padding = new Padding(10);
        _mainRightPanel.Padding = new Padding(10);

        _lblMainAlarmTitle.ForeColor = Color.FromArgb(28, 58, 120);
        _lblTopAlarmTitle.ForeColor = Color.FromArgb(28, 58, 120);
        _lblRealtimeDataTitle.ForeColor = Color.FromArgb(28, 58, 120);

        // 主页面右侧区域按分辨率自适应：低分辨率（如 1024×768）整体缩小字体与控件
        _mainRightUiScale = GetMainRightUiScale();
        if (_mainRightUiScale < 0.995f)
            ApplyMainRightPanelScale(_mainRightUiScale);

        ConfigureTopAlarmCards(_mainRightUiScale);
        ConfigureLogStyle();
    }

    // 主页面右侧缩放系数：以物理分辨率高度 1080 为基准 1.0，向下线性缩至最低 0.7
    // （1024×768 ≈ 0.71，1920×1080 及以上为 1.0）；Bounds 受 DPI 虚拟化影响时按 DeviceDpi 折算回物理像素
    private float GetMainRightUiScale()
    {
        try
        {
            Screen? screen = Screen.PrimaryScreen;
            if (screen is null)
                return 1f;
            float dpiFactor = DeviceDpi / 96f;
            if (dpiFactor <= 0)
                dpiFactor = 1f;
            float physicalHeight = screen.Bounds.Height * dpiFactor;
            return Math.Clamp(physicalHeight / 1080f, 0.7f, 1f);
        }
        catch
        {
            return 1f;
        }
    }

    // 低分辨率下同步缩小右侧区块标题字号与按钮显示区标题（卡片/实时显示/按钮各自在构建时按 scale 处理）
    private void ApplyMainRightPanelScale(float scale)
    {
        _lblTopAlarmTitle.Font = new Font("Microsoft YaHei UI", 12F * scale, FontStyle.Bold);
        _lblRealtimeDataTitle.Font = new Font("Microsoft YaHei UI", 12F * scale, FontStyle.Bold);
        _lblMainButtonDisplayTitle.Font = new Font("Microsoft YaHei UI", 12F * scale, FontStyle.Bold);
        _lblMainButtonDisplayTitle.Height = (int)Math.Round(30 * scale);
        _mainButtonDisplaySection.Padding = new Padding(0, 0, 0, (int)Math.Round(6 * scale));
    }

    /// <summary>
    /// 主页面左侧「功能按钮」的低分辨率缩放：与右侧同一系数（1024×768 ≈ 0.71），
    /// 基准 180×35。高分屏保持原尺寸与默认字体不变。
    /// </summary>
    private void ApplyMainLeftButtonScale(PlcBoundButton button)
    {
        float scale = GetMainRightUiScale();
        if (scale >= 0.995f)
        {
            button.ApplySize(180, 35);
            return;
        }

        button.ApplyUiScale(scale); // 同步缩小字体（YaHei 10F×scale Bold）
        button.ApplySize((int)Math.Round(180 * scale), (int)Math.Round(35 * scale));
    }

    /// <summary>
    /// 主页面左侧「数值显示」的低分辨率缩放：与「功能按钮」控件保持一致
    /// （基准高度 35、YaHei 10F×scale Bold、扁平无边框），共用同一缩放系数；
    /// 宽度以 180×scale 为下限并按标题实际宽度自适应加宽，避免长标题挤压数值列。
    /// </summary>
    private void ApplyMainLeftValueTextBoxStyle(PlcValueTextBoxControl control)
    {
        float scale = GetMainRightUiScale();
        control.ApplyMainDisplayStyle(
            (int)Math.Round(180 * scale),
            (int)Math.Round(35 * scale),
            scale);
    }

    /// <summary>主页面左侧「状态显示」的低分辨率缩放：基准 180×35，字体与功能按钮一致（YaHei 10F×scale Bold）。</summary>
    private void ApplyMainLeftBoolLabelScale(PlcBoolLabe label)
    {
        float scale = GetMainRightUiScale();
        if (scale >= 0.995f)
            return;

        label.Width = (int)Math.Round(180 * scale);
        label.Height = (int)Math.Round(35 * scale);
        label.Font = new Font("Microsoft YaHei UI", Math.Max(7F, 10F * scale), FontStyle.Bold);
    }

    private void ConfigureTopAlarmCards(float scale = 1f)
    {
        // 底色：三档超浅语义色（红 / 橙 / 琥珀），低饱和不抢主页面注意力
        Color[] backgrounds =
        [
            Color.FromArgb(254, 235, 237),
            Color.FromArgb(255, 243, 224),
            Color.FromArgb(255, 251, 235)
        ];
        // 前景三档语义色：与底色同色系加深，浅底深字保证对比度
        Color[] foregrounds =
        [
            Color.FromArgb(183, 28, 28),
            Color.FromArgb(191, 87, 22),
            Color.FromArgb(144, 112, 19)
        ];

        for (int i = 0; i < _topAlarmLabels.Length; i++)
        {
            _topAlarmLabels[i].BackColor = backgrounds[i];
            _topAlarmLabels[i].ForeColor = foregrounds[i];
            // 9F 加粗：比正文（9F 常规）稍重但整体更小巧，长报警名两行也放得下
            _topAlarmLabels[i].Font = new Font("Microsoft YaHei UI", 9F * scale, FontStyle.Bold);
            _topAlarmLabels[i].Padding = new Padding((int)Math.Round(10 * scale), 0, 0, 0);
            _topAlarmLabels[i].Height = (int)Math.Round(40 * scale);
            _topAlarmLabels[i].Margin = new Padding(0, (int)Math.Round((i == 0 ? 6 : 4) * scale), 0, 0);
            // 无边框：与按钮区 / 工艺数据行的扁平风格统一（原 FixedSingle 黑边框过于生硬）
            _topAlarmLabels[i].BorderStyle = BorderStyle.None;
            // 长报警名放不下时显示省略号，避免末尾「次数」被裁掉一半
            _topAlarmLabels[i].AutoEllipsis = true;
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

    /// <summary>状态栏统一排版：字体 / 行高 / 内边距 / 分隔线 / 链接样式见 <see cref="StatusStripStyle"/>。</summary>
    private void ConfigureStatusBar() => StatusStripStyle.Apply(_statusStrip);

    private void BuildUi()
    {
        // 组装主界面静态布局（页面容器、导航栏、页内控件挂载）
        ComposeStaticLayout();

        // 绑定设计器中的 Top3 标签，后续用于报警排行显示
        _topAlarmLabels = [_lblTopAlarm1, _lblTopAlarm2, _lblTopAlarm3];

        if (_cmbUserRole.Items.Count == 0)
            _cmbUserRole.Items.AddRange(Enum.GetNames<UserRole>());
        _suspendRoleChanged = true;
        _cmbUserRole.SelectedIndex = (int)_currentRole;
        _suspendRoleChanged = false;

        // 初始化报警表格列（避免重复添加）
        EnsureAlarmGridColumns(_mainAlarmGrid, 30, 50, 20);
        EnsureAlarmGridColumns(_recordAlarmGrid, 30, 45, 25);
        InitializeProductionStatisticsUi();

        _lblPlcStatus.IsLink = true;
        _lblPlcStatus.ToolTipText = "打开 PLC 设备管理与功能区绑定";
        if (!_statusStrip.Items.Contains(_lblPlcStatus))
            _statusStrip.Items.Add(_lblPlcStatus);
        if (!_statusStrip.Items.Contains(_lblPlcManager))
        {
            _statusStrip.Items.Add(new ToolStripSeparator());
            _statusStrip.Items.Add(_lblPlcManager);
        }
        if (!_statusStrip.Items.Contains(_lblReconnectFail))
        {
            _statusStrip.Items.Add(new ToolStripSeparator());
            _statusStrip.Items.Add(_lblReconnectFail);
        }
        if (!_statusStrip.Items.Contains(_lblHeartbeat))
        {
            _statusStrip.Items.Add(new ToolStripSeparator());
            _statusStrip.Items.Add(_lblHeartbeat);
        }
        if (!_statusStrip.Items.Contains(_lblSubHeartbeat))
        {
            _statusStrip.Items.Add(new ToolStripSeparator());
            _statusStrip.Items.Add(_lblSubHeartbeat);
        }
        if (!_statusStrip.Items.Contains(_lblAutoRunning))
        {
            _statusStrip.Items.Add(new ToolStripSeparator());
            _statusStrip.Items.Add(_lblAutoRunning);
        }
        if (!_statusStrip.Items.Contains(_lblPlcVersion))
        {
            _statusStrip.Items.Add(new ToolStripSeparator());
            _statusStrip.Items.Add(_lblPlcVersion);
        }

        // 项目全部加入后再统一排版（字号 / 行高 / 内边距 / 分隔线 / 链接样式）
        ConfigureStatusBar();

        ConfigureParameterTableControl(_parameterTableControl);
        ConfigureParameterTableControl(_hmiDisplayParameterTableControl);
        ConfigureParameterTableControl(_buttonVarParameterTableControl);
        ConfigureProcessRealtimeDataConfigControl(_processRealtimeDataConfigControl);

        _axisMotionControl.PlcProvider = ProvidePlcForDebugToolsPage;
        _axisMotionControl.LogAction = AppendLog;
        _axisMotionControl.AbsResetPasswordConfirmRequest = PromptAbsResetPasswordConfirmation;
        if (_axisMotionControl.GetAxes().Count == 0)
            _axisMotionControl.SetAxes([new AxisMotionAddressConfig { AxisId = "Axis1" }]);
        _positionParameterTableControl.LogAction = AppendLog;
        _positionParameterTableControl.AxisNameProvider = () => _axisMotionControl.GetAxisNames();
        _positionParameterTableControl.PlcProvider = ProvidePlcForDebugToolsPage;
        _positionParameterTableControl.AxisFieldAddressResolver = ResolveAxisFieldAddress;

        _boolSignalControl.PlcProvider = ProvidePlcForMainUi;
        _boolSignalControl.LogAction = AppendLog;
        _boolSignalControl.EditPermissionRequest = RequestTableEditAuthorization;

        _boolGroupControlTable.PlcProvider = ProvidePlcForDebugToolsPage;
        _boolGroupControlTable.LogAction = AppendLog;
        _boolGroupControlTable.EditPermissionRequest = RequestTableEditAuthorization;

        _boolSignalInputMatrixEmbed.PlcProvider = ProvidePlcForDebugToolsPage;
        _boolSignalInputMatrixEmbed.LogAction = AppendLog;
        _boolSignalInputMatrixEmbed.ReloadFromConfigFile();

        _alarmAddressManagerControl.PlcProvider = ProvidePlcForMainUi;
        _alarmAddressManagerControl.LogAction = AppendLog;
        _alarmAddressManagerControl.EditPermissionRequest = RequestTableEditAuthorization;
        _alarmAddressManagerControl.AlarmTriggered += alarm => HandleAlarmTriggered(alarm);
        _alarmAddressManagerControl.ActiveAlarmsSnapshotUpdated += SyncMainAlarmGridFromActiveSnapshot;

        _alarmPopupManagerControl.PlcProvider = ProvidePlcForMainUi;
        _alarmPopupManagerControl.LogAction = AppendLog;
        _alarmPopupManagerControl.EditPermissionRequest = RequestTableEditAuthorization;

        ConfigureParameterAdjustControl();

        _manualOperationPromptConfigControl.PlcProvider = ProvidePlcForMainUi;
        _manualOperationPromptConfigControl.LogAction = AppendLog;
        _manualOperationPromptConfigControl.EditPermissionRequest = RequestTableEditAuthorization;

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

        _communicationConfigControl.PlcProvider = ProvidePlcForTaskUi;
        _taskPipelineRunner = new TaskPipelineRunner(
            ProvidePlcForTaskUi,
            ex => _communicationConfigControl.ExecuteTaskActionAsync(ex),
            () => _taskSettingsControl.GetAutomatedTasks(),
            AppendLog,
            null);
        _taskSettingsControl.BindRunner(_taskPipelineRunner);
        _alarmAddressManagerControl.AlarmTriggered += alarm =>
            _taskPipelineRunner?.OnAlarmTriggered(alarm.AlarmCode, alarm.AlarmName);

        _communicationConfigControl.LogAction = AppendLog;
        _communicationConfigControl.EditPermissionRequest = RequestTableEditAuthorization;
        // Socket服务端收到数据 → 按分隔符/标记分片 → 更新任务条件用的标记最新值
        _communicationConfigControl.ServerSocketMarkerValuesReceived += (instanceKey, map) =>
            _taskPipelineRunner?.UpdateSocketMarkerValues(instanceKey, map);

        _stationConfigControl.LogAction = AppendLog;
        _stationConfigControl.EditPermissionRequest = RequestTableEditAuthorization;
        _stationConfigControl.ConfigurationsChanged += HandleStationConfigurationsChanged;
        _buttonDisplayConfigControl.LogAction = AppendLog;
        _buttonDisplayConfigControl.EditPermissionRequest = RequestTableEditAuthorization;
        _buttonDisplayConfigControl.ConfigurationsChanged += HandleButtonDisplayConfigurationsChanged;
        _horizontalButtonConfigControl.LogAction = AppendLog;
        _horizontalButtonConfigControl.EditPermissionRequest = RequestTableEditAuthorization;
        _horizontalButtonConfigControl.ConfigurationsChanged += HandleHorizontalButtonConfigurationsChanged;
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
        if (!_tabHorizontalButtonConfig.Controls.Contains(_horizontalButtonConfigControl))
            _tabHorizontalButtonConfig.Controls.Add(_horizontalButtonConfigControl);
        if (!_tabPlcBoolLabelDisplayConfig.Controls.Contains(_plcBoolLabelDisplayConfigControl))
            _tabPlcBoolLabelDisplayConfig.Controls.Add(_plcBoolLabelDisplayConfigControl);
        if (!_tabPlcValueTextBoxDisplayConfig.Controls.Contains(_plcValueTextBoxDisplayConfigControl))
            _tabPlcValueTextBoxDisplayConfig.Controls.Add(_plcValueTextBoxDisplayConfigControl);

        RebuildStationControls(_stationConfigControl.GetConfigurations());
        RebuildMainButtonDisplayControls(_buttonDisplayConfigControl.GetConfigurations());
        RebuildMainPlcHorizontalButtons(_horizontalButtonConfigControl.GetConfigurations());
        RebuildMainPlcBoolLabelDisplays(_plcBoolLabelDisplayConfigControl.GetConfigurations());
        RebuildMainPlcValueTextBoxDisplays(_plcValueTextBoxDisplayConfigControl.GetConfigurations());

        RebuildMainProcessRealtimeDisplays();
        InitializeRealtimeBoolLabelBindings();
        RebuildChildPlcModulePages();

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
            ComposeMonitorPage();
            ComposeRecordsPage();
            ComposeParameterAdjustPage();
            ComposeProductionPage();
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
        EnsureControlAdded(_navLeftPanel, _btnPlcDeviceManager);
        EnsureControlAdded(_navLeftPanel, _btnPageDebug);
        EnsureControlAdded(_navLeftPanel, _btnPageDebugTools);
        EnsureControlAdded(_navLeftPanel, _btnPageStationControl);
        EnsureControlAdded(_navLeftPanel, _btnPageDianCiHan);
        // MES 按钮保持 Visible=false，仅挂载占位，不占导航区版面
        EnsureControlAdded(_navLeftPanel, _btnPageMes);
        EnsureControlAdded(_navLeftPanel, _btnPageCameraDebug);
        EnsureControlAdded(_navLeftPanel, _btnPageMonitor);
        EnsureControlAdded(_navLeftPanel, _btnPageRecords);
        EnsureControlAdded(_navLeftPanel, _btnPageParameterAdjust);
        EnsureControlAdded(_navLeftPanel, _btnParameterChangeRecords);
        EnsureControlAdded(_navLeftPanel, _btnManualOperationLog);
        EnsureControlAdded(_navLeftPanel, _btnPageProduction);
        EnsureControlAdded(_navLeftPanel, _btnPageCustemerUi);

        EnsureControlAdded(_navRightPanel, _lblUserRole);
        EnsureControlAdded(_navRightPanel, _cmbUserRole);
        EnsureControlAdded(_navRightPanel, _btnPasswordManage);
    }

    // 主页面内容挂载
    private void ComposeMainPage()
    {
        if (_lblMainAlarmTitle.Parent == _mainLeftPanel)
            _mainLeftPanel.Controls.Remove(_lblMainAlarmTitle);

        ConfigureMainLeftLayout();
        if (_panelMainSystemPlc.RowStyles.Count == 0)
        {
            _panelMainSystemPlc.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            // 三行：数值显示 / 功能按钮（横向）/ 状态显示；蓝底标题条兼作分隔
            _panelMainSystemPlc.RowStyles.Add(new RowStyle(SizeType.Percent, 34F));
            _panelMainSystemPlc.RowStyles.Add(new RowStyle(SizeType.Percent, 32F));
            _panelMainSystemPlc.RowStyles.Add(new RowStyle(SizeType.Percent, 34F));
        }

        EnsureControlAdded(_mainPlcValueTextBoxSection, _mainPlcValueTextBoxPanel);
        EnsureControlAdded(_mainPlcValueTextBoxSection, _lblMainPlcValueTextBoxTitle);
        EnsureControlAdded(_mainPlcButtonSection, _mainPlcButtonPanel);
        EnsureControlAdded(_mainPlcButtonSection, _lblMainPlcButtonTitle);
        EnsureControlAdded(_mainPlcBoolLabelSection, _mainPlcBoolLabelPanel);
        EnsureControlAdded(_mainPlcBoolLabelSection, _lblMainPlcBoolLabelTitle);
        EnsureControlAdded(_panelMainSystemPlc, _mainPlcValueTextBoxSection, 0, 0);
        EnsureControlAdded(_panelMainSystemPlc, _mainPlcButtonSection, 0, 1);
        EnsureControlAdded(_panelMainSystemPlc, _mainPlcBoolLabelSection, 0, 2);

        EnsureControlAdded(_tabPageMainPlcDisplay, _panelDualTabSystemHost);
        EnsureControlAdded(_tabPageMainCustemer, _panelDualTabCustemerHost);
        if (_tabMainPageDual.TabPages.Count == 0)
        {
            _tabMainPageDual.TabPages.Add(_tabPageMainPlcDisplay);
            _tabMainPageDual.TabPages.Add(_tabPageMainCustemer);
        }

        EnsureControlAdded(_mainUpperDisplayHost, _panelMainSystemPlc);
        EnsureControlAdded(_mainLeftPanel, _mainUpperDisplayHost, 0, 0);

        _lblMainAlarmTitle.Dock = DockStyle.Fill;
        _lblMainAlarmTitle.AutoSize = false;
        _lblMainAlarmTitle.TextAlign = ContentAlignment.MiddleLeft;
        _btnMainLogQuery.Dock = DockStyle.Right;
        _btnMainLogQuery.Margin = new Padding(0, 1, 4, 1);
        EnsureControlAdded(_mainAlarmHeaderPanel, _lblMainAlarmTitle);
        EnsureControlAdded(_mainAlarmHeaderPanel, _btnMainLogQuery);
        EnsureControlAdded(_mainAlarmGridSection, _mainAlarmGrid);
        EnsureControlAdded(_mainAlarmGridSection, _mainAlarmHeaderPanel);
        EnsureControlAdded(_mainLeftPanel, _mainAlarmGridSection, 0, 1);

        EnsureParkHostOnMainPage();

        EnsureControlAdded(_mainDataSplit.Panel1, _mainRealtimePanel);
        _mainDataSplit.Panel2Collapsed = true;
        EnsureControlAdded(_mainRightPanel, _mainDataSplit, 0, 5);

        EnsureControlAdded(_mainPage, _mainSplit);

        EnsureControlAdded(_mainProcessRealtimeScrollHost, _mainProcessRealtimeFlow);
        EnsureControlAdded(_mainButtonDisplaySection, _mainButtonDisplayPanel);
        EnsureControlAdded(_mainButtonDisplaySection, _lblMainButtonDisplayTitle);
        // Dock 顺序：先 Fill（按钮区到底部），再 Top（上部工艺数据可滚动）
        EnsureControlAdded(_mainRealtimePanel, _mainButtonDisplaySection);
        EnsureControlAdded(_mainRealtimePanel, _mainProcessRealtimeScrollHost);
        _mainRealtimePanel.Resize += MainRealtimePanel_Resize;
    }

    private void MainRealtimePanel_Resize(object? sender, EventArgs e)
    {
        int h = _mainRealtimePanel.ClientSize.Height;
        if (h <= 0)
            return;

        const int minButtonBand = 160;
        const int minProcessBand = 72;
        int maxTop = Math.Max(minProcessBand, h - minButtonBand);
        int idealTop = (h * 38) / 100;
        idealTop = Math.Clamp(idealTop, minProcessBand, maxTop);
        if (_mainProcessRealtimeScrollHost.Height != idealTop)
            _mainProcessRealtimeScrollHost.Height = idealTop;
    }

    private void ConfigureMainLeftLayout()
    {
        _lblMainAlarmTitle.Text = "实时报警";
        _mainLeftPanel.RowStyles.Clear();
        // 上部：PLC/自定义显示宿主；下部：固定高度报警区
        _mainLeftPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _mainLeftPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, MainPageAlarmGridSectionHeight));
        _mainLeftPanel.RowCount = 2;
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
        if (!_debugTab.TabPages.Contains(_tabCameraThreadStartupConfig))
            _debugTab.TabPages.Add(_tabCameraThreadStartupConfig);
        if (!_debugTab.TabPages.Contains(_tabStationConfig))
            _debugTab.TabPages.Add(_tabStationConfig);
        // 「工站控制」标签已移除：工站面板只通过导航区「工站控制」弹窗展示（见 OpenStationControlPopup）。

        EnsureControlAdded(_stationAreaPanel, _stationDisplayPanel);
        EnsureControlAdded(_stationDeviceTabHost!, _stationAreaPanel);

        if (!_debugTab.TabPages.Contains(_tabButtonDisplayConfig))
            _debugTab.TabPages.Add(_tabButtonDisplayConfig);
        if (!_debugTab.TabPages.Contains(_tabHorizontalButtonConfig))
        {
            int verticalIndex = _debugTab.TabPages.IndexOf(_tabButtonDisplayConfig);
            if (verticalIndex >= 0)
                _debugTab.TabPages.Insert(verticalIndex + 1, _tabHorizontalButtonConfig);
            else
                _debugTab.TabPages.Add(_tabHorizontalButtonConfig);
        }
        if (!_debugTab.TabPages.Contains(_tabPlcBoolLabelDisplayConfig))
            _debugTab.TabPages.Add(_tabPlcBoolLabelDisplayConfig);
        if (!_debugTab.TabPages.Contains(_tabPlcValueTextBoxDisplayConfig))
            _debugTab.TabPages.Add(_tabPlcValueTextBoxDisplayConfig);
        if (!_debugTab.TabPages.Contains(_tabMainPageDisplayConfig))
        {
            int idxMainPageDisplay = _debugTab.TabPages.IndexOf(_tabPlcValueTextBoxDisplayConfig);
            if (idxMainPageDisplay >= 0)
                _debugTab.TabPages.Insert(idxMainPageDisplay + 1, _tabMainPageDisplayConfig);
            else
                _debugTab.TabPages.Add(_tabMainPageDisplayConfig);
        }

        WireMainPageDisplayConfigTab();
        if (!_debugTab.TabPages.Contains(_tabCameraProjectConfig))
        {
            int displayConfigIndex = _debugTab.TabPages.IndexOf(_tabMainPageDisplayConfig);
            if (displayConfigIndex >= 0)
                _debugTab.TabPages.Insert(displayConfigIndex + 1, _tabCameraProjectConfig);
            else
                _debugTab.TabPages.Add(_tabCameraProjectConfig);
        }
        WireCameraProjectConfigTab();
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
        int alarmPopupTabIndex = _debugTab.TabPages.IndexOf(_tabAlarmPopupConfig);
        if (alarmPopupTabIndex >= 0)
        {
            if (!_debugTab.TabPages.Contains(_tabManualOperationPromptConfig))
                _debugTab.TabPages.Insert(alarmPopupTabIndex + 1, _tabManualOperationPromptConfig);
        }
        else if (!_debugTab.TabPages.Contains(_tabManualOperationPromptConfig))
        {
            _debugTab.TabPages.Add(_tabManualOperationPromptConfig);
        }

        EnsureControlAdded(_tabManualOperationPromptConfig, _manualOperationPromptConfigControl);
        EnsureControlAdded(_tabStationConfig, _stationConfigControl);
        EnsureControlAdded(_tabButtonDisplayConfig, _buttonDisplayConfigControl);
        EnsureControlAdded(_tabHorizontalButtonConfig, _horizontalButtonConfigControl);
        EnsureControlAdded(_tabPlcBoolLabelDisplayConfig, _plcBoolLabelDisplayConfigControl);
        EnsureControlAdded(_tabPlcValueTextBoxDisplayConfig, _plcValueTextBoxDisplayConfigControl);
        BuildCameraThreadStartupConfigTab();

        EnsureControlAdded(_axisSplit.Panel1, _axisMotionControl);
        EnsureControlAdded(_axisSplit.Panel2, _positionParameterTableControl);
        _splitBoolGroupAndInputMatrix.Panel1.Controls.Add(_boolGroupControlTable);
        _splitBoolGroupAndInputMatrix.Panel2.Controls.Add(_boolSignalInputMatrixEmbed);
        _boolGroupTabHost.Controls.Add(_splitBoolGroupAndInputMatrix);
        EnsureControlAdded(_tabBoolGroup, _boolGroupTabHost);

        if (_debugTab.TabPages.Contains(_tabAxis))
            _debugTab.TabPages.Remove(_tabAxis);
        if (_debugTab.TabPages.Contains(_tabBoolGroup))
            _debugTab.TabPages.Remove(_tabBoolGroup);

        if (!_debugToolsTab.TabPages.Contains(_tabAxis))
            _debugToolsTab.TabPages.Add(_tabAxis);
        if (!_debugToolsTab.TabPages.Contains(_tabBoolGroup))
            _debugToolsTab.TabPages.Add(_tabBoolGroup);

        EnsureControlAdded(_debugPage, _debugTab);
        EnsureControlAdded(_debugToolsPage, _debugToolsTab);

        _parameterTableControl.PlaceImportButtonAtEnd(_btnImportParameterData);
        _hmiDisplayParameterTableControl.PlaceImportButtonAtEnd(_btnImportHmiDisplayData);
        _buttonVarParameterTableControl.PlaceImportButtonAtEnd(_btnImportButtonVarData);
        _alarmAddressManagerControl.PlaceImportButtonAtEnd(_btnImportAlarmConfigData);
        _alarmPopupManagerControl.PlaceImportButtonAtEnd(_btnImportAlarmPopupData);
        _manualOperationPromptConfigControl.PlaceImportButtonAtEnd(_btnImportManualOperationPromptData);
        _boolGroupControlTable.PlaceImportButtonAtEnd(_btnImportBoolGroupData);
    }

    private void ComposeMonitorPage()
    {
        if (!_monitorTab.TabPages.Contains(_tabMonitorSignals))
            _monitorTab.TabPages.Add(_tabMonitorSignals);
        if (!_monitorTab.TabPages.Contains(_tabCameraThreads))
            _monitorTab.TabPages.Add(_tabCameraThreads);

        EnsureControlAdded(_tabMonitorSignals, _boolSignalControl);
        BuildCameraThreadDebugTab();
        EnsureControlAdded(_monitorPage, _monitorTab);
    }

    private static readonly (string Key, string DisplayName)[] CameraThreadStartupItems =
    [
        ("HandleCam1", "HandleCam1"),
        ("HandleCam2", "HandleCam2"),
        ("HandleCam3", "HandleCam3"),
        ("HandleCam4", "HandleCam4"),
        ("HandleCam5", "HandleCam5"),
        ("HandleCam6", "HandleCam6"),
        ("HandleCam1LightCheck", "HandleCam1LightCheck"),
        ("HandleCam2LightCheck", "HandleCam2LightCheck"),
        ("HandleCam3LightCheck", "HandleCam3LightCheck"),
        ("HandleCam4LightCheck", "HandleCam4LightCheck")
    ];

    private void BuildCameraThreadStartupConfigTab()
    {
        if (_tabCameraThreadStartupConfig.Controls.Count > 0)
            return;

        var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 36,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight
        };
        var btnSave = new UIButton { Text = "保存", Width = 88, Height = 30 };
        var btnReload = new UIButton { Text = "重新加载", Width = 88, Height = 30 };
        btnSave.Click += (_, _) => SaveCameraThreadStartupConfigFromUi();
        btnReload.Click += (_, _) =>
        {
            LoadCameraThreadStartupConfig();
            RefreshCameraThreadStartupConfigUiFromModel();
            _lblCameraThreadStartupConfigHint.Text = $"已加载: {Path.GetFileName(_cameraThreadStartupConfigPath)} ({DateTime.Now:HH:mm:ss})";
        };
        toolbar.Controls.Add(btnSave);
        toolbar.Controls.Add(btnReload);

        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 32,
            Text = "配置 TH_CamSet 线程是否启动（保存后重启程序生效）",
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        int row = 0;
        foreach (var (key, displayName) in CameraThreadStartupItems)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var nameLabel = new Label
            {
                Text = displayName,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(4, 8, 6, 2)
            };
            var check = new CheckBox
            {
                Text = "启动",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(4, 6, 2, 2)
            };
            _cameraThreadStartupChecks[key] = check;
            table.Controls.Add(nameLabel, 0, row);
            table.Controls.Add(check, 1, row);
            row++;
        }

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroll.Controls.Add(table);
        root.Controls.Add(scroll);
        root.Controls.Add(_lblCameraThreadStartupConfigHint);
        root.Controls.Add(toolbar);
        root.Controls.Add(title);
        _tabCameraThreadStartupConfig.Controls.Add(root);
        RefreshCameraThreadStartupConfigUiFromModel();
    }

    private void RefreshCameraThreadStartupConfigUiFromModel()
    {
        foreach (var (key, _) in CameraThreadStartupItems)
        {
            if (_cameraThreadStartupChecks.TryGetValue(key, out var check))
                check.Checked = GetCameraThreadStartupState(_cameraThreadStartupConfig, key);
        }
    }

    private void SaveCameraThreadStartupConfigFromUi()
    {
        foreach (var (key, _) in CameraThreadStartupItems)
        {
            bool enabled = _cameraThreadStartupChecks.TryGetValue(key, out var check) && check.Checked;
            SetCameraThreadStartupState(_cameraThreadStartupConfig, key, enabled);
        }

        SaveCameraThreadStartupConfig();
        _lblCameraThreadStartupConfigHint.Text = $"已保存: {Path.GetFileName(_cameraThreadStartupConfigPath)} ({DateTime.Now:HH:mm:ss})";
        AppendLog("相机线程启动配置已保存，重启程序后生效。");
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

        EnsureControlAdded(_recordsPage, _recordAlarmSection);
    }

    private void ComposeParameterAdjustPage()
    {
        _parameterAdjustPage.BackColor = Color.FromArgb(241, 245, 252);
        EnsureControlAdded(_parameterAdjustPage, _parameterAdjustControl);
    }

    private void ComposeProductionPage()
    {
        ComposeProductionToolbar();
        ComposeProductionColorLegend();
        EnsureControlAdded(_productionSection, _productionToolbar);
        EnsureControlAdded(_productionSection, _lblProductionSummary);
        EnsureControlAdded(_productionSection, _productionColorLegend);
        EnsureControlAdded(_productionSection, _lblProductionTitle);
        EnsureControlAdded(_productionPage, _productionSection);
    }

    private void ComposeProductionColorLegend()
    {
        if (_productionColorLegend.Controls.Count > 0)
            return;

        _productionColorLegend.Controls.Add(CreateProductionLegendItem("白班产量", ProductionDayShiftColor));
        _productionColorLegend.Controls.Add(CreateProductionLegendItem("夜班产量", ProductionNightShiftColor));
    }

    private static Control CreateProductionLegendItem(string text, Color color)
    {
        var host = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 0, 24, 0),
            Padding = Padding.Empty
        };
        host.Controls.Add(new Panel
        {
            Width = 18,
            Height = 12,
            BackColor = color,
            Margin = new Padding(0, 5, 6, 0)
        });
        host.Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(45, 60, 85),
            Padding = new Padding(0, 1, 0, 0)
        });

        return host;
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
        EnsureControlAdded(_productionToolbar, _btnRefreshProductionChart);
    }

    // 根容器与静态页面挂载
    private void ComposeRootContainers()
    {
        EnsureControlAdded(_pageHost, _mainPage);
        EnsureControlAdded(_pageHost, _debugPage);
        EnsureControlAdded(_pageHost, _debugToolsPage);
        EnsureControlAdded(_pageHost, _monitorPage);
        EnsureControlAdded(_pageHost, _recordsPage);
        EnsureControlAdded(_pageHost, _parameterAdjustPage);
        EnsureControlAdded(_pageHost, _productionPage);

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
            _cmbProductionPeriod.Items.AddRange([ProductionModeDay, ProductionModeWeek, ProductionModeMonth]);
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

        _tmDayShiftStart.Value = DateTime.Today.AddHours(8);
        _tmDayShiftEnd.Value = DateTime.Today.AddHours(20);
        _tmNightShiftStart.Value = DateTime.Today.AddHours(20);
        _tmNightShiftEnd.Value = DateTime.Today.AddDays(1).AddHours(8);
        // 默认定位到当前生产日（20:00 后已属次日夜班，基准日期需顺延，否则看不到当晚产量）
        _dtProductionAnchor.Value = GetCurrentProductionDate(DateTime.Now);

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
        _lblPlcStatus.Click += async (_, _) => await RequestAndOpenPlcDeviceManagerAsync();
        _lblPlcManager.Click += async (_, _) => await RequestAndOpenPlcDeviceManagerAsync();
        _btnPlcDeviceManager.Click += async (_, _) => await RequestAndOpenPlcDeviceManagerAsync();
        Shown += async (_, _) =>
        {
            if (_startupInitialized)
                return;

            _startupInitialized = true;
            Hide();
            using StartupLoadingForm loadingForm = new();
            loadingForm.Show();
            loadingForm.BringToFront();
            loadingForm.Refresh();
            await Task.Yield();

            try
            {
                // 先连主 PLC，再启动相机工作线程，避免相机侧桥接出现时主 PLC 尚未就绪（会打「主程序PLC未连接」提示）。
                AttachProductionChartSafely();
                ApplyAxisSplitLayout();
                await AutoConnectOnStartupAsync();
            }
            catch (Exception ex)
            {
                AppendLog($"启动初始化异常: {ex.GetBaseException().Message}");
            }

            // PLC 连接失败时仍尝试启动相机（与原先「相机先于连接」下线程仍会跑一致）。
            EnsureCameraWorkThreadsStartedOnStartup();
            // 大华相机自动流程（Cam1Auto/Cam2Auto/Cam3Auto）改为后台常驻，
            // 与是否打开大华工程页面无关，随主程序启动、随主程序退出。
            StartDahuaAutoCamService();
            loadingForm.Close();

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
                BeginInvoke(new Action(() =>
                {
                    OpenMainPageWindow();
                    StartDianCiHanInBackground();
                    StartMesInBackground();
                }));
            }
        };
        // 窗口尺寸变化时保持轴组页上下 2/5 : 3/5 的分割比例
        _axisSplit.SizeChanged += (_, _) => ApplyAxisSplitLayout();

        _splitBoolGroupAndInputMatrix.SizeChanged += (_, _) =>
        {
            TryApplyBoolGroupSplitLayout();
            if (!_boolGroupSplitLayoutApplied)
                ApplyDefaultBoolGroupSplitterDistance();
        };
        _debugToolsTab.SelectedIndexChanged += (_, _) =>
        {
            if (_debugToolsTab.SelectedTab == _tabBoolGroup)
                _boolSignalInputMatrixEmbed.ReloadFromConfigFile();
        };

        _btnPageMain.Click += (_, _) => OpenMainPageWindow();
        _btnPageDebug.Click += (_, _) => OpenSystemConfigWindow();
        _btnPageDebugTools.Click += async (_, _) => await OpenDebugToolsWindowAsync();
        _btnPageStationControl.Click += (_, _) => OpenStationControlPopup();
        _btnPageDianCiHan.Click += (_, _) => OpenDianCiHanWindow();
        _btnPageMes.Click += (_, _) => OpenMesWindow();
        _btnPageCameraDebug.Click += (_, _) => OpenCameraDebugWindow();
        _btnPageMonitor.Click += (_, _) => OpenMonitorWindow();
        _btnPageRecords.Click += (_, _) => OpenRecordsWindow();
        _btnPageParameterAdjust.Click += (_, _) => OpenParameterAdjustWindow();
        _btnParameterChangeRecords.Click += (_, _) => _parameterTableControl.ShowParameterChangeRecordsDialog(this);
        _btnManualOperationLog.Click += (_, _) => OpenManualOperationLogWindow();
        _btnPageProduction.Click += (_, _) => OpenProductionStatisticsWindow();
        _btnPageCustemerUi.Click += (_, _) => OpenCustemerUiWindow();
        _cmbUserRole.SelectedIndexChanged += (_, _) => HandleRoleSelectionChanged();
        _btnPasswordManage.Click += (_, _) => ShowPasswordManageDialog();
        _btnMainLogQuery.Click += (_, _) => ApplicationLogQueryDialog.Show(this);
        _btnRecordQuery.Click += (_, _) => ApplyRecordDateFilter();
        _btnRecordAll.Click += (_, _) =>
        {
            _dtRecordFrom.Checked = false;
            _dtRecordTo.Checked = false;
            BindRecordGrid(QueryAlarmHistoryFromDatabase(null, null));
        };
        _btnRecordExport.Click += (_, _) => ExportAlarmRecords();
        _btnRefreshProductionChart.Click += (_, _) => RefreshProductionChart();
        _cmbProductionPeriod.SelectedIndexChanged += (_, _) => RefreshProductionChart();
        _dtProductionAnchor.ValueChanged += (_, _) => RefreshProductionChart();
        _tmDayShiftStart.ValueChanged += (_, _) => RefreshProductionChart();
        _tmDayShiftEnd.ValueChanged += (_, _) => RefreshProductionChart();
        _tmNightShiftStart.ValueChanged += (_, _) => RefreshProductionChart();
        _tmNightShiftEnd.ValueChanged += (_, _) => RefreshProductionChart();
        _productionPollTimer.Tick += async (_, _) => await PollProductionCounterAsync();
        _productionPollTimer.Start();
        _btnImportParameterData.Click += (_, _) => ImportParameterDataFromExcel();
        _btnImportHmiDisplayData.Click += (_, _) => ImportHmiDisplayDataFromExcel();
        _btnImportButtonVarData.Click += (_, _) => ImportButtonVarDataFromExcel();
        _btnImportBoolGroupData.Click += (_, _) => ImportBoolGroupDataFromExcel(_boolGroupControlTable);
        _btnImportAlarmConfigData.Click += (_, _) => ImportAlarmConfigDataFromExcel();
        _btnImportAlarmPopupData.Click += (_, _) => ImportAlarmPopupConfigFromExcel();
        _btnImportManualOperationPromptData.Click += (_, _) => ImportManualOperationPromptFromExcel();
        _stationDisplayPanel.SizeChanged += (_, _) => ArrangeStationControls();
        _stationDisplayPanel.ControlAdded += (_, e) =>
        {
            if (e.Control is { } c)
                WireStationAreaMouseWheelRecursive(c);
        };
        _stationDisplayPanel.MouseWheel += OnStationAreaDescendantMouseWheel;
        _productionSection.SizeChanged += (_, _) => AttachProductionChartSafely();

        _statusTimer.Tick += async (_, _) =>
        {
            CheckRoleAutoDowngrade();
            ReloadTopAlarmStatsOnDayChange();
            await RefreshStatusBarAsync();
            await RefreshRealtimeBoolLabelsAsync();
        };
        // 主界面鼠标活动跟踪：超过 RoleAutoDowngradeSeconds 秒无鼠标操作自动降级为作业员权限
        _mouseActivityFilter = new MouseActivityMessageFilter(MarkMouseActivity);
        Application.AddMessageFilter(_mouseActivityFilter);
        _statusTimer.Start();

        _monitorRefreshTimer.Tick += async (_, _) => await RefreshMonitorSignalsIfVisibleAsync();
        _monitorRefreshTimer.Start();

        _cameraThreadUiTimer.Tick += (_, _) => RefreshCameraThreadDebugUi();
        _cameraThreadUiTimer.Start();

        _processRealtimeMainRebuildDebounce.Tick += (_, _) =>
        {
            _processRealtimeMainRebuildDebounce.Stop();
            RebuildMainProcessRealtimeDisplays();
            if (_plc is { IsConnected: true } && !_debugToolsPlcExclusiveActive)
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

    private UIButton CreateChildParameterImportButton(ParameterTableControl targetControl, string pageName)
    {
        var button = new UIButton
        {
            Text = _btnImportParameterData.Text,
            Width = _btnImportParameterData.Width,
            Height = _btnImportParameterData.Height,
            Style = _btnImportParameterData.Style,
            Radius = _btnImportParameterData.Radius,
            FillColor = _btnImportParameterData.FillColor,
            FillHoverColor = _btnImportParameterData.FillHoverColor,
            FillPressColor = _btnImportParameterData.FillPressColor,
            RectColor = _btnImportParameterData.RectColor,
            Enabled = _currentRole is UserRole.管理员 or UserRole.工程师
        };
        button.Click += (_, _) => ImportParameterDataFromExcel(targetControl, pageName);
        return button;
    }

    private UIButton CreateChildBoolGroupImportButton(BoolGroupControlTable targetControl)
    {
        var button = new UIButton
        {
            Text = _btnImportBoolGroupData.Text,
            Width = _btnImportBoolGroupData.Width,
            Height = _btnImportBoolGroupData.Height,
            Style = _btnImportBoolGroupData.Style,
            Radius = _btnImportBoolGroupData.Radius,
            FillColor = _btnImportBoolGroupData.FillColor,
            FillHoverColor = _btnImportBoolGroupData.FillHoverColor,
            FillPressColor = _btnImportBoolGroupData.FillPressColor,
            RectColor = _btnImportBoolGroupData.RectColor,
            Enabled = _currentRole is UserRole.管理员 or UserRole.工程师
        };
        button.Click += (_, _) => ImportBoolGroupDataFromExcel(targetControl);
        return button;
    }

    private void ImportBoolGroupDataFromExcel()
    {
        ImportBoolGroupDataFromExcel(_boolGroupControlTable);
    }

    // 子 PLC 报警页的 Excel 导入按钮：复用主 PLC 按钮的样式，点击回调由调用方给出
    private UIButton CreateChildAlarmImportButton(UIButton template, Action onClick)
    {
        var button = new UIButton
        {
            Text = template.Text,
            Width = template.Width,
            Height = template.Height,
            Style = template.Style,
            Radius = template.Radius,
            FillColor = template.FillColor,
            FillHoverColor = template.FillHoverColor,
            FillPressColor = template.FillPressColor,
            RectColor = template.RectColor,
            Enabled = _currentRole is UserRole.管理员 or UserRole.工程师
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void ImportBoolGroupDataFromExcel(BoolGroupControlTable targetControl)
    {
        var rows = LoadExcelRowsForImport("选择输出点位Excel文件", "输出点位页面");
        if (rows is null)
            return;

        // 输出点位仅导入名称前缀为 DO_ 的数据；名称后追加 Excel Comment 列（下划线连接）
        var items = rows
            .Where(x => x.Name.StartsWith("DO_", StringComparison.OrdinalIgnoreCase))
            .Select(x =>
            {
                string comment = x.Comment.Trim();
                string displayName = string.IsNullOrEmpty(comment) ? x.Name : $"{x.Name}_{comment}";
                return new BoolGroupControlItem { Name = displayName, Address = x.Address };
            })
            .ToList();

        if (items.Count == 0)
        {
            MessageBox.Show(this, "未找到名称前缀为 DO_ 的数据。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        targetControl.SetItems(items);
        AppendLog($"输出点位页面导入成功，共 {items.Count} 条。");
    }

    private void ImportAlarmConfigDataFromExcel()
    {
        ImportAlarmConfigDataFromExcel(_alarmAddressManagerControl, "报警配置页面");
    }

    private void ImportAlarmConfigDataFromExcel(AlarmAddressManagerControl targetControl, string pageName)
    {
        var rows = LoadExcelRowsForImport("选择报警配置Excel文件", pageName);
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

        targetControl.SetItems(items);
        AppendLog($"{pageName}导入成功，共 {items.Count} 条。");
    }

    private void ImportAlarmPopupConfigFromExcel()
    {
        ImportAlarmPopupConfigFromExcel(_alarmPopupManagerControl, "报警弹窗配置页面");
    }

    private void ImportAlarmPopupConfigFromExcel(AlarmPopupManagerControl targetControl, string pageName)
    {
        var rows = LoadExcelRowsForImport("选择报警弹窗配置Excel文件", pageName);
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

        targetControl.SetGroups(groups);
        AppendLog($"{pageName}导入成功，共 {groups.Count} 组。");
    }

    private void ImportManualOperationPromptFromExcel()
    {
        ImportManualOperationPromptFromExcel(_manualOperationPromptConfigControl, "手动操作提示配置");
    }

    private void ImportManualOperationPromptFromExcel(ManualOperationPromptConfigControl targetControl, string pageName)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择手动操作提示 Excel 文件",
            Filter = "Excel 文件 (*.xls;*.xlsx)|*.xls;*.xlsx|所有文件 (*.*)|*.*",
            InitialDirectory = AppContext.BaseDirectory
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        if (!ManualOperationPromptExcelImport.TryParse(dialog.FileName, out ManualOperationPromptExcelImport.Result? data, out string? err)
            || data is null)
        {
            MessageBox.Show(this, string.IsNullOrWhiteSpace(err) ? "导入失败。" : err, "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (!string.IsNullOrWhiteSpace(err))
                AppendLog($"{pageName} Excel 导入失败: {err}");
            return;
        }

        if (data.Entries.Count == 0)
        {
            MessageBox.Show(this, "已解析到 PLC 地址，但未读取到任何「数字—弹框内容」映射行。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        targetControl.ApplyExcelImport(data);
        AppendLog($"{pageName}已导入: {data.Address}，共 {data.Entries.Count} 条映射。来源: {dialog.FileName}");
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
        control.PlcProvider = ProvidePlcForParameterUi;
        control.LogAction = AppendLog;
        control.EditPermissionRequest = RequestTableEditAuthorization;
    }

    private void ConfigureParameterAdjustControl()
    {
        _parameterAdjustControl.PlcProvider = ProvidePlcForParameterUi;
        _parameterAdjustControl.LogAction = AppendLog;
        _parameterAdjustControl.RegularParametersProvider = () => _parameterTableControl.GetParameters();
        _parameterAdjustControl.HmiDisplayParametersProvider = () => _hmiDisplayParameterTableControl.GetParameters();
        _parameterAdjustControl.ButtonVarParametersProvider = () => _buttonVarParameterTableControl.GetParameters();
        _parameterAdjustControl.AlarmAddressProvider = () => _alarmAddressManagerControl.GetItems();
        _parameterAdjustControl.AlarmPopupGroupsProvider = () => _alarmPopupManagerControl.GetGroups();
    }

    private void ConfigureProcessRealtimeDataConfigControl(ProcessRealtimeDataConfigControl control)
    {
        control.PlcProvider = ProvidePlcForParameterUi;
        control.LogAction = AppendLog;
        control.EditPermissionRequest = RequestTableEditAuthorization;
        control.ConfigurationChanged += (_, _) =>
        {
            _processRealtimeMainRebuildDebounce.Stop();
            _processRealtimeMainRebuildDebounce.Start();
        };
    }

    /// <summary>轴组页上下分割比例：上方轴组运动区占 2/5，下方位置参数表区占 3/5。</summary>
    private const double AxisSplitTopRatio = 0.4;

    private void ApplyAxisSplitLayout()
    {
        if (_axisSplit.Width <= 0 || _axisSplit.Height <= 0)
            return;

        // 按当前方向计算可分割区间；若区间无效则跳过，避免启动阶段抛出 SplitterDistance 异常
        int span = _axisSplit.Orientation == Orientation.Horizontal ? _axisSplit.Height : _axisSplit.Width;
        int min = Math.Max(1, _axisSplit.Panel1MinSize);
        int max = span - _axisSplit.SplitterWidth - _axisSplit.Panel2MinSize;
        if (max < min)
            return;

        int target = (int)Math.Round(span * AxisSplitTopRatio);
        _axisSplit.SplitterDistance = Math.Clamp(target, min, max);
    }

    /// <summary>输出点位页：左侧表格、右侧输入点阵。仅在 SplitContainer 宽度足够时设置 MinSize，避免 SplitterDistance 异常。</summary>
    private void TryApplyBoolGroupSplitLayout()
    {
        if (_boolGroupSplitLayoutApplied || _splitBoolGroupAndInputMatrix.Width < 400)
            return;

        const int tableMin = 240;
        const int matrixMin = 220;
        int inner = _splitBoolGroupAndInputMatrix.Width - _splitBoolGroupAndInputMatrix.SplitterWidth;
        if (inner < tableMin + matrixMin)
            return;

        try
        {
            _splitBoolGroupAndInputMatrix.Panel1MinSize = tableMin;
            _splitBoolGroupAndInputMatrix.Panel2MinSize = matrixMin;
            int maxDist = _splitBoolGroupAndInputMatrix.Width - _splitBoolGroupAndInputMatrix.Panel2MinSize - _splitBoolGroupAndInputMatrix.SplitterWidth;
            int want = (int)(_splitBoolGroupAndInputMatrix.Width * BoolGroupOutputLeftPanelWidthFraction);
            _splitBoolGroupAndInputMatrix.SplitterDistance = Math.Clamp(want, _splitBoolGroupAndInputMatrix.Panel1MinSize, maxDist);
            _boolGroupSplitLayoutApplied = true;
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void ApplyDefaultBoolGroupSplitterDistance()
    {
        if (_splitBoolGroupAndInputMatrix.Width <= 0)
            return;

        int maxDist = _splitBoolGroupAndInputMatrix.Width - _splitBoolGroupAndInputMatrix.Panel2MinSize - _splitBoolGroupAndInputMatrix.SplitterWidth;
        if (maxDist < _splitBoolGroupAndInputMatrix.Panel1MinSize)
            return;

        int want = (int)(_splitBoolGroupAndInputMatrix.Width * BoolGroupOutputLeftPanelWidthFraction);
        try
        {
            _splitBoolGroupAndInputMatrix.SplitterDistance = Math.Clamp(want, _splitBoolGroupAndInputMatrix.Panel1MinSize, maxDist);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void ShowPage(Control page)
    {
        var role = _currentRole;
        if (role == UserRole.作业员 && (page == _debugPage || page == _debugToolsPage || page == _parameterAdjustPage))
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

    private global::PlcConectCom.PlcCommunicator? ProvidePlcForMainUi()
    {
        if (_debugToolsPlcExclusiveActive)
            return null;
        return GetMainPlc();
    }

    private global::PlcConectCom.PlcCommunicator? ProvidePlcForDebugToolsPage() => GetMainPlc();
    private global::PlcConectCom.PlcCommunicator? ProvidePlcForStationUi() => GetMainPlc();
    private global::PlcConectCom.PlcCommunicator? ProvidePlcForParameterUi() => GetMainPlc();
    private global::PlcConectCom.PlcCommunicator? ProvidePlcForTaskUi() => GetMainPlc();
    private global::PlcConectCom.PlcCommunicator? ProvidePlcForCameraUi() => GetMainPlc();
    private global::PlcConectCom.PlcCommunicator? ProvidePlcForCustomerUi() => GetMainPlc();

    private global::PlcConectCom.PlcCommunicator? GetMainPlc()
    {
        return GetPlcById(_plcDeviceConfiguration.Devices.FirstOrDefault()?.Id);
    }

    private global::PlcConectCom.PlcCommunicator? GetPlcById(string? deviceId)
    {
        return deviceId is not null && _plcDevices.TryGetValue(deviceId, out var plc) && plc.IsConnected
            ? plc
            : null;
    }

    private bool BlockMainFormDirectPlcAccess(string operationHint)
    {
        if (!_debugToolsPlcExclusiveActive)
            return false;
        AppendLog($"调试按钮页独占 PLC 通讯中，{operationHint}已禁用。");
        return true;
    }

    private void ApplyDebugToolsPlcExclusiveMode(bool exclusive)
    {
        _debugToolsPlcExclusiveActive = exclusive;
        if (exclusive)
        {
            _alarmAddressManagerControl.StopAutoMonitor();
            _alarmPopupManagerControl.StopAutoMonitor();
            _manualOperationPromptConfigControl.StopAutoMonitor();
            foreach (ProcessRealtimeValueDisplayControl c in _mainProcessRealtimeFlow.Controls.OfType<ProcessRealtimeValueDisplayControl>())
                c.AutoReadEnabled = false;
        }
        else
        {
            if (_plc is { IsConnected: true })
            {
                _alarmAddressManagerControl.StartAutoMonitor();
                _alarmPopupManagerControl.StartAutoMonitor();
                _manualOperationPromptConfigControl.StartAutoMonitor();
                StartMainProcessRealtimeDisplays();
            }
        }
    }

    private void OnDebugToolsPageWindowFormClosed(object? sender, FormClosedEventArgs e)
    {
        ApplyDebugToolsPlcExclusiveMode(false);
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
            _btnPageDebug,
            ownedByMain: false);
    }

    /// <summary>
    /// 系统配置页已不再提供「工站控制」标签；弹窗关闭或主窗退出时把工站面板宿主从弹窗摘下，
    /// 暂存到隐藏停靠容器，等待下次打开「工站控制」弹窗时重新挂载。
    /// </summary>
    private void ParkStationDeviceHost()
    {
        if (_stationDeviceTabHost is null)
            return;

        if (!ReferenceEquals(_stationDeviceTabHost.Parent, _stationDeviceHostParking))
        {
            _stationDeviceTabHost.Parent?.Controls.Remove(_stationDeviceTabHost);
            _stationDeviceHostParking.Controls.Add(_stationDeviceTabHost);
        }
    }

    private void OpenStationControlPopup()
    {
        if (_currentRole == UserRole.作业员)
        {
            AppendLog("作业员权限仅可访问主页面和记录查询。");
            return;
        }

        if (_autoRunningBlocked)
        {
            AppendLog("设备自动运行中，「工站控制」已禁用。");
            return;
        }

        if (_stationControlPopupForm is { IsDisposed: false })
        {
            _stationControlPopupForm.Activate();
            return;
        }

        var form = new Form
        {
            Text = "工站控制",
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(1100, 720),
            MinimumSize = new Size(640, 480),
            ShowInTaskbar = false,
            MaximizeBox = true,
            MinimizeBox = true
        };
        try
        {
            if (Icon is not null)
                form.Icon = Icon;
        }
        catch
        {
            // 忽略图标复制失败
        }

        form.FormClosed += (_, _) =>
        {
            ParkStationDeviceHost();
            _stationControlPopupForm = null;
            UpdateNavigationButtonState((UIButton?)null);
        };

        if (_stationDeviceTabHost is not null)
        {
            _stationDeviceTabHost.Parent?.Controls.Remove(_stationDeviceTabHost);
            _stationDeviceTabHost.Dock = DockStyle.Fill;
            form.Controls.Add(_stationDeviceTabHost);
        }

        _stationControlPopupForm = form;
        form.Show(this);
        UpdateNavigationButtonState(_btnPageStationControl);
    }

    private async Task OpenDebugToolsWindowAsync()
    {
        if (_currentRole == UserRole.作业员)
        {
            AppendLog("作业员权限仅可访问主页面和记录查询。");
            return;
        }

        if (_autoRunningBlocked)
        {
            AppendLog("设备自动运行中，「调试按钮」已禁用。");
            return;
        }

        // 必须先弹出窗口再刷新：否则在 PLC 已连接时 Refresh 可能较慢或阻塞，用户会感觉「点了调试按钮没反应」
        OpenOrActivatePageWindow(
            ref _debugToolsPageWindow,
            () => new DebugToolsPageWindow(),
            _debugToolsPage,
            _btnPageDebugTools);

        if (_debugToolsPageWindow is { IsDisposed: false })
        {
            _debugToolsPageWindow.FormClosed -= OnDebugToolsPageWindowFormClosed;
            _debugToolsPageWindow.FormClosed += OnDebugToolsPageWindowFormClosed;
        }

        ApplyDebugToolsPlcExclusiveMode(true);
        await RefreshDebugToolsPageAsync();
    }

    private void OpenMonitorWindow()
    {
        OpenOrActivatePageWindow(
            ref _monitorPageWindow,
            () => new MonitorPageWindow(),
            _monitorPage,
            _btnPageMonitor);

        // 打开监控页时立即刷新一次，避免等待定时器下一轮 Tick
        if (IsHandleCreated && !IsDisposed)
        {
            BeginInvoke(new Action(async () =>
            {
                try
                {
                    await _boolSignalControl.RefreshSignalsAsync();
                    await RefreshStatusBarAsync();
                }
                catch
                {
                    // ignore transient refresh failures while opening page
                }
            }));
        }
    }

    private void BuildCameraThreadDebugTab()
    {
        _tabCameraThreads.Controls.Clear();
        _tabCameraThreads.Controls.Add(_lblCameraThreadHint);
        _tabCameraThreads.Controls.Add(_cameraThreadScrollHost);
        _cameraThreadScrollHost.Controls.Clear();
        _cameraThreadScrollHost.Controls.Add(_cameraThreadTable);

        _cameraThreadTable.SuspendLayout();
        _cameraThreadTable.Controls.Clear();
        _cameraThreadTable.ColumnCount = 7;
        _cameraThreadTable.RowCount = CameraThreadMonitorRowCount + 1;
        _cameraThreadTable.ColumnStyles.Clear();
        _cameraThreadTable.RowStyles.Clear();
        _cameraThreadTable.GrowStyle = TableLayoutPanelGrowStyle.FixedSize;
        int[] colWidths = [220, 70, 70, 120, 120, 120, 120];
        for (int c = 0; c < colWidths.Length; c++)
            _cameraThreadTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, colWidths[c]));
        _cameraThreadTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        for (int r = 1; r <= CameraThreadMonitorRowCount; r++)
            _cameraThreadTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        _cameraThreadTable.Width = colWidths.Sum() + _cameraThreadTable.Padding.Left + _cameraThreadTable.Padding.Right;
        _cameraThreadTable.MinimumSize = new Size(_cameraThreadTable.Width, 0);

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
            "Cam6 B移栽对侧",
            "HandleCam1LightCheck",
            "HandleCam2LightCheck",
            "HandleCam3LightCheck",
            "HandleCam4LightCheck"
        ];
        for (int i = 0; i < CameraThreadMonitorRowCount; i++)
        {
            int row = i + 1;
            int camIdx = i + 1;
            bool isWorkThread = i < CameraWorkThreadCount;
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

            if (isWorkThread)
            {
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
                    Padding = new Padding(0, 8, 0, 0)
                };
                pauseHost.Controls.Add(chk);
                _cameraThreadTable.Controls.Add(pauseHost, 2, row);
            }
            else
            {
                _cameraThreadTable.Controls.Add(new Label { Text = "—", TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Fill }, 2, row);
            }

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

    private Form GetOrCreateCameraProjectWindow(CameraProjectKind kind)
    {
        Form window = CameraProjectLifecycle.Switch(_cameraDebugWindow, kind, CameraUiExports.CreateCameraProjectForm);
        if (!ReferenceEquals(window, _cameraDebugWindow))
        {
            _cameraDebugWindow = window;
            window.FormClosed += (_, _) =>
            {
                if (!ReferenceEquals(_cameraDebugWindow, window)) return;
                _cameraDebugWindow = null;
                _boundThCamSet = null;
                _cameraWorkThreadsStartedOnStartup = false;
                UpdateNavigationButtonState((UIButton?)null);
            };
        }
        BindThCamSetFromCameraWindow();
        return window;
    }

    private void StartCameraProjectRuntime(Form window)
    {
        if (window is THCamSetHostForm hikvision)
        {
            SyncCameraThreadStartupToCameraUiSettingFile();
            hikvision.CamSetControl.ApplyCameraThreadStartupConfig(BuildCameraThreadStartupStateMap());
        }
        _ = window.Handle;
        ((ICameraProjectRuntime)window).StartRuntime();
        _cameraWorkThreadsStartedOnStartup = true;
    }

    /// <summary>
    /// 启动大华相机自动流程的后台常驻宿主（DahuaAutoCamService）。
    /// 启动后 Cam1Auto/Cam2Auto/Cam3Auto 持续响应 PLC 允许拍照信号，
    /// 打开或关闭「小料机大华工程」页面都不再影响它们。
    /// </summary>
    private void StartDahuaAutoCamService()
    {
        try
        {
            if (DahuaAutoCamService.EnsureStarted())
                AppendLog("大华相机自动流程（后台常驻）已启动：页面开/关不影响其响应 PLC 允许信号。");
            else
                AppendLog("启动大华相机自动流程失败：" + DahuaAutoCamService.LastError);
        }
        catch (Exception ex)
        {
            AppendLog("启动大华相机自动流程异常：" + ex.GetBaseException().Message);
        }
    }

    private void EnsureCameraWorkThreadsStartedOnStartup()
    {
        if (_cameraWorkThreadsStartedOnStartup) return;
        try
        {
            CameraProjectKind kind = CameraProjectPickerForm.LoadSelection();
            StartCameraProjectRuntime(GetOrCreateCameraProjectWindow(kind));
            AppendLog($"相机后台流程已按记忆选择启动：{kind}");
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
            for (int i = 0; i < CameraThreadMonitorRowCount; i++)
            {
                _cameraThreadAliveLabels[i].Text = "—";
                if (i < CameraWorkThreadCount)
                {
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
                }

                for (int v = 0; v < 4; v++)
                    _cameraThreadValueLabels[i, v].Text = "—";
            }

            return;
        }

        for (int i = 0; i < CameraWorkThreadCount; i++)
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

        for (int i = 0; i < CameraLightCheckThreadCount; i++)
        {
            int rowIndex = CameraWorkThreadCount + i;
            _cameraThreadAliveLabels[rowIndex].Text = cam.IsCameraLightCheckThreadAlive(i + 1) ? "运行" : "停止";
            for (int v = 0; v < 4; v++)
                _cameraThreadValueLabels[rowIndex, v].Text = "—";
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
            // 不再弹出「选择相机工程」对话框：工程类型由系统配置→相机工程配置决定，
            // 主程序启动时已加载到 _selectedCameraProject，保存后立即生效。
            CameraProjectKind selected = _selectedCameraProject;
            Form window = GetOrCreateCameraProjectWindow(selected);
            StartCameraProjectRuntime(window);
            if (!window.Visible) window.Show(this);
            else window.Activate();
            window.BringToFront();
            UpdateNavigationButtonState(_btnPageCameraDebug);
            AppendLog($"已加载相机工程：{window.Text}");
        }
        catch (Exception ex)
        {
            AppendLog($"打开相机调试界面失败: {ex.GetBaseException().Message}");
        }
    }

    /// <summary>按系统配置打开排版机或小料机客户看板（独立子窗口）。</summary>
    private void OpenCustemerUiWindow()
    {
        try
        {
            if (_custemerUiWindow is null || _custemerUiWindow.IsDisposed || !IsSelectedCustomerBoard(_custemerUiWindow))
            {
                _custemerUiWindow?.Dispose();
                _custemerUiWindow = CreateCustomerBoardForm();
                _custemerUiWindow.StartPosition = FormStartPosition.CenterScreen;
                _custemerUiWindow.FormClosed += (_, _) =>
                {
                    _custemerUiWindow = null;
                    UpdateNavigationButtonState((UIButton?)null);
                };
            }

            if (!_custemerUiWindow.Visible)
                _custemerUiWindow.Show(this);
            else
                _custemerUiWindow.Activate();

            _custemerUiWindow.BringToFront();
            UpdateNavigationButtonState(_btnPageCustemerUi);
            AppendLog($"已打开客户看板：{CustomerBoardName(_mainPageDisplayConfig.CustomerBoard)}");
        }
        catch (Exception ex)
        {
            AppendLog($"打开客户界面失败: {ex.GetBaseException().Message}");
        }
    }

    private static string CustomerBoardName(int board) =>
        board == 1 ? "小料机客户看板" : "排版机客户看板";

    private bool IsSelectedCustomerBoard(Form form) =>
        _mainPageDisplayConfig.CustomerBoard == 1
            ? form is XiaoLiaoCustomerForm
            : form is CustemerUI.Form1;

    private Form CreateCustomerBoardForm() =>
        _mainPageDisplayConfig.CustomerBoard == 1
            ? new XiaoLiaoCustomerForm(ProvidePlcForCustomerUi)
            : new CustemerUI.Form1(ProvidePlcForCustomerUi);

    private void OpenDianCiHanWindow()
    {
        try
        {
            DianCiHan.Ui.DianCiHanForm window = GetOrCreateDianCiHanWindow();
            if (!window.Visible)
                window.Show(this);
            else
                window.Activate();

            window.BringToFront();
            UpdateNavigationButtonState(_btnPageDianCiHan);
        }
        catch (Exception ex)
        {
            AppendLog($"打开电磁焊界面失败: {ex.GetBaseException().Message}");
        }
    }

    /// <summary>打开 MES 窗口（SCADA / MES 两个分页）。</summary>
    private void OpenMesWindow()
    {
        try
        {
            MesScada.Ui.MesForm window = GetOrCreateMesWindow();
            if (!window.Visible)
                window.Show(this);
            else
                window.Activate();

            window.BringToFront();
            UpdateNavigationButtonState(_btnPageMes);
        }
        catch (Exception ex)
        {
            AppendLog($"打开 MES 界面失败: {ex.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// 后台创建 MES 窗口并跑起来（与电磁焊一致：随主程序启动，点按钮只负责显示）。
    /// </summary>
    private void StartMesInBackground()
    {
        try
        {
            MesScada.Ui.MesForm window = GetOrCreateMesWindow();
            _ = window.Handle;
            window.EnsureRunning();

            if (_mesConfig.AutoStartModbusServer && window.StartModbusServer())
                AppendLog($"MES Modbus TCP 从站已启动，端口 {_mesConfig.ModbusServerPort}");

            AppendLog("MES/SCADA 已在后台启动；界面可通过左侧“MES”按钮打开。");
        }
        catch (Exception ex)
        {
            AppendLog($"MES 后台启动失败: {ex.GetBaseException().Message}");
        }
    }

    private MesScada.Ui.MesForm GetOrCreateMesWindow()
    {
        if (_mesWindow is not null && !_mesWindow.IsDisposed)
            return _mesWindow;

        EnsureMesContextStarted();

        // Modbus 从站端口以主程序配置为准
        MesScada.MesController.Instacne.ModbusServerPort = _mesConfig.ModbusServerPort;

        _mesWindow = new MesScada.Ui.MesForm
        {
            StartPosition = FormStartPosition.CenterScreen,
            Text = "MES"
        };
        _mesWindow.FormClosed += (_, _) =>
        {
            _mesWindow = null;
            UpdateNavigationButtonState(null);
        };
        return _mesWindow;
    }

    /// <summary>
    /// 载入 MES 配置 → 注入设备上下文 → 启动 PLC 轮询。只执行一次。
    /// </summary>
    private void EnsureMesContextStarted()
    {
        if (_mesContext is not null)
            return;

        _mesConfig = LoadMesScadaConfig();
        MesDeviceContext context = new(() => _plc, _mesConfig, message => AppendLogSafe($"MES: {message}"), SaveMesScadaConfig);
        context.LoginUserName = GetCurrentLoginUserName();

        // 必须在首次访问 MesController.Instacne（会立刻拉起状态机线程）之前注入
        MesScada.MesController.Attach(context);
        _mesContext = context;

        _mesPollCts = new CancellationTokenSource();
        _ = Task.Run(() => context.RunPollLoopAsync(_mesPollCts.Token));
    }

    /// <summary>从 UI 线程之外的线程安全地写日志。</summary>
    private void AppendLogSafe(string message)
    {
        if (IsDisposed || Disposing)
            return;

        try
        {
            // AppendLog 内部已做线程安全入队 + 单次合并刷新，无需再为每条日志排一个委托
            AppendLog(message);
        }
        catch
        {
        }
    }

    private const string MesScadaConfigFileName = "MesScada.config.json";

    /// <summary>
    /// 载入 MES 配置；文件不存在时按默认生成一份（键齐全、地址为空），
    /// 便于上线前直接填地址。
    /// </summary>
    private MesScadaConfig LoadMesScadaConfig()
    {
        string path = Path.Combine(AppContext.BaseDirectory, MesScadaConfigFileName);
        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                MesScadaConfig? cfg = JsonSerializer.Deserialize<MesScadaConfig>(json, JsonFileOptions);
                if (cfg is not null)
                {
                    // 兼容旧文件：补齐新增信号键
                    foreach (string name in Enum.GetNames<MesScada.MesSignal>())
                        cfg.SignalAddresses.TryAdd(name, string.Empty);

                    cfg.PollIntervalMs = Math.Max(20, cfg.PollIntervalMs);
                    if (cfg.ModbusServerPort <= 0)
                        cfg.ModbusServerPort = 10001;

                    return cfg;
                }
            }
        }
        catch
        {
            // 配置损坏时退回默认值，不阻断启动
        }

        MesScadaConfig created = new();
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(created, JsonFileOptions));
        }
        catch
        {
        }

        return created;
    }

    /// <summary>把 MES 配置落盘（MES 配置分页「保存」按钮经 MesDeviceContext.Save 调用）。</summary>
    private void SaveMesScadaConfig(MesScadaConfig config)
    {
        string path = Path.Combine(AppContext.BaseDirectory, MesScadaConfigFileName);
        File.WriteAllText(path, JsonSerializer.Serialize(config, JsonFileOptions));
    }

    /// <summary>
    /// 当前登录用户名。沿用 XLW 的约定：空串表示未登录（作业员），
    /// MES 界面据此限制「参数设置」分页与 OEE 重置。
    /// </summary>
    private string GetCurrentLoginUserName() =>
        _currentRole == UserRole.作业员 ? string.Empty : _currentRole.ToString();

    private void StartDianCiHanInBackground()
    {
        try
        {
            DianCiHan.Ui.DianCiHanForm window = GetOrCreateDianCiHanWindow();
            _ = window.Handle;
            _ = window.Page.Handle;
            window.Page.Start(showErrorDialog: false);
            AppendLog("正在后台启动电磁焊流程；界面可通过左侧“电磁焊”按钮打开。");
        }
        catch (Exception ex)
        {
            AppendLog($"电磁焊后台启动失败: {ex.GetBaseException().Message}");
        }
    }

    private DianCiHan.Ui.DianCiHanForm GetOrCreateDianCiHanWindow()
    {
        if (_dianCiHanWindow is not null && !_dianCiHanWindow.IsDisposed)
            return _dianCiHanWindow;

        PlcDeviceProfile[] devices = _plcDeviceConfiguration.Devices.ToArray();
        PlcDeviceProfile mainDevice = devices.FirstOrDefault();
        if (mainDevice is null)
            throw new InvalidOperationException("未配置主 PLC 设备。");

        var plcSources = devices
            .Select((device, index) => new DianCiHan.Plc.DianCiHanPlcSource(
                device.Id,
                index == 0 ? $"主 PLC（{device.Name}）" : $"子 PLC - {device.Name}"))
            .ToArray();

        _dianCiHanWindow = new DianCiHan.Ui.DianCiHanForm(
            deviceId => GetPlcById(deviceId)!,
            mainDevice.Id,
            plcSources)
        {
            StartPosition = FormStartPosition.CenterScreen,
            Text = "电磁焊"
        };
        _dianCiHanWindow.FormClosed += (_, _) =>
        {
            _dianCiHanWindow = null;
            UpdateNavigationButtonState(null);
        };
        _dianCiHanWindow.Page.Logged += message =>
        {
            if (IsDisposed || Disposing)
                return;
            try
            {
                if (InvokeRequired)
                    BeginInvoke(new Action(() => AppendLog($"电磁焊: {message}")));
                else
                    AppendLog($"电磁焊: {message}");
            }
            catch
            {
            }
        };
        return _dianCiHanWindow;
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

    /// <summary>打开「手动操作日志」窗口：首次打开时把查询控件挂到页面容器上。</summary>
    private void OpenManualOperationLogWindow()
    {
        if (_manualOperationLogControl is null)
        {
            _manualOperationLogControl = new ManualOperationLogPageControl { Dock = DockStyle.Fill };
            _manualOperationLogPage.Controls.Add(_manualOperationLogControl);
        }

        OpenOrActivatePageWindow(
            ref _manualOperationLogPageWindow,
            () => new ManualOperationLogPageWindow(),
            _manualOperationLogPage,
            _btnManualOperationLog);

        // 每次打开都按当天/上次条件重新取一次，避免看到缓存的旧列表
        _manualOperationLogControl.RefreshLogs();
    }

    private void OpenParameterAdjustWindow()
    {
        if (_currentRole == UserRole.作业员)
        {
            AppendLog("作业员权限不可访问参数调整。");
            return;
        }

        OpenOrActivatePageWindow(
            ref _parameterAdjustPageWindow,
            () => new ParameterAdjustPageWindow(),
            _parameterAdjustPage,
            _btnPageParameterAdjust);
        _ = LoadSelectedParameterAdjustAsync();
    }

    private async Task LoadSelectedParameterAdjustAsync()
    {
        try
        {
            int selected = _parameterAdjustDeviceTabs?.SelectedIndex ?? 0;
            if (selected > 0 && selected - 1 < _childPlcModules.Count)
            {
                await _childPlcModules[selected - 1].ParameterAdjustControl.LoadFromPlcAsync();
                return;
            }

            await _parameterAdjustControl.LoadFromPlcAsync();
        }
        catch (Exception ex)
        {
            AppendLog($"参数调整加载失败: {ex.GetBaseException().Message}");
        }
    }

    private void OpenProductionStatisticsWindow()
    {
        OpenOrActivatePageWindow(
            ref _productionStatisticsPageWindow,
            () => new ProductionStatisticsPageWindow(),
            _productionPage,
            _btnPageProduction);
        AttachProductionChartSafely();
        RefreshProductionChart();
    }

    private void OpenOrActivatePageWindow<TWindow>(
        ref TWindow? window,
        Func<TWindow> factory,
        Control pageControl,
        UIButton navButton,
        bool ownedByMain = true)
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
        {
            if (ownedByMain)
                window.Show(this);
            else
                window.Show();
        }
        else
            window.Activate();

        window.WindowState = FormWindowState.Maximized;
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
            _boolSignalInputMatrixEmbed.ReloadFromConfigFile();
            await RefreshMainButtonDisplayAsync();
        }
        catch (Exception ex)
        {
            AppendLog($"调试页面刷新失败: {ex.Message}");
        }
    }

    private void UpdateNavigationButtonState(Control? activePage)
    {
        // 页面 → 导航按钮映射；「PLC 设备管理」不是页面（弹窗入口），命中不到就按非激活态处理
        var pageToButton = new Dictionary<Control, UIButton>
        {
            [_mainPage] = _btnPageMain,
            [_debugPage] = _btnPageDebug,
            [_debugToolsPage] = _btnPageDebugTools,
            [_monitorPage] = _btnPageMonitor,
            [_recordsPage] = _btnPageRecords,
            [_parameterAdjustPage] = _btnPageParameterAdjust,
            [_productionPage] = _btnPageProduction
        };

        UIButton? activeButton = null;
        if (activePage is not null && pageToButton.TryGetValue(activePage, out UIButton? mapped))
            activeButton = mapped;

        UpdateNavigationButtonState(activeButton);
    }

    private void UpdateNavigationButtonState(UIButton? activeButton)
    {
        foreach (UIButton button in GetNavigationButtons())
            ApplyNavigationButtonVisual(button, ReferenceEquals(button, activeButton));
    }

    /// <summary>
    /// 导航区按钮的统一外观。字体必须在这里给：UIButton 默认沿用 SunnyUI 的宋体小字，
    /// 漏掉任何一个按钮（历史上漏了「PLC 设备管理」）都会出现「同排按钮字号字重不一致、
    /// 禁用态像空白按钮」的问题。
    /// </summary>
    private static void ApplyNavigationButtonVisual(UIButton button, bool isActive)
    {
        button.FillColor = isActive ? Color.FromArgb(0, 161, 210) : Color.FromArgb(56, 81, 110);
        button.RectColor = isActive ? Color.FromArgb(0, 188, 238) : Color.FromArgb(86, 116, 150);
        button.FillHoverColor = isActive ? Color.FromArgb(0, 174, 224) : Color.FromArgb(68, 98, 132);
        button.Font = isActive ? NavigationActiveFont : NavigationFont;

        // 权限不足被禁用时 SunnyUI 会把底刷成 #F4F4F4、字刷成近白灰，看着像没渲染；
        // 这里把禁用字色压深一档，保持「不可用」但文字清楚可读。
        button.ForeDisableColor = Color.FromArgb(108, 118, 132);
    }

    /// <summary>
    /// 导航区全部按钮（顺序即界面顺序）。非页面入口（PLC 设备管理 / 参数修改记录 /
    /// 手动操作日志）也必须登记，否则拿不到导航区统一字体与配色。
    /// 其中 PLC 设备管理 / MES 当前 Visible=false，保留登记是为了恢复显示时样式不回退。
    /// </summary>
    private IEnumerable<UIButton> GetNavigationButtons()
    {
        yield return _btnPageMain;
        yield return _btnPlcDeviceManager;
        yield return _btnPageDebug;
        yield return _btnPageDebugTools;
        yield return _btnPageStationControl;
        yield return _btnPageDianCiHan;
        yield return _btnPageMes;
        yield return _btnPageCameraDebug;
        yield return _btnPageMonitor;
        yield return _btnPageRecords;
        yield return _btnPageParameterAdjust;
        yield return _btnParameterChangeRecords;
        yield return _btnManualOperationLog;
        yield return _btnPageProduction;
        yield return _btnPageCustemerUi;
    }

    /// <summary>记录最近一次鼠标活动时间（由应用级消息过滤器回调，鼠标移动/点击/滚轮均计入）。</summary>
    private void MarkMouseActivity()
    {
        _lastMouseActivityUtc = DateTime.UtcNow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>
    /// 前台窗口是否属于本进程。用于实现「只认本程序窗口内的操作」：
    /// 操作员切到同机其它程序（记事本、第三方工具等）时，那些窗口里的输入不阻止降级。
    /// </summary>
    private static bool IsForegroundWindowInOurProcess()
    {
        try
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero)
                return false;

            GetWindowThreadProcessId(fg, out uint pid);
            return pid != 0 && pid == (uint)Environment.ProcessId;
        }
        catch
        {
            // 查询失败按"前台不是本程序"处理：宁可降级也不误判成有人在操作
            return false;
        }
    }

    /// <summary>
    /// 系统级无输入秒数（最后一次鼠标 / 键盘 / 触摸输入距今的时间）。
    /// 由操作系统记录，不依赖本进程的消息泵：触摸屏操作、模态弹窗（ShowDialog 循环
    /// 不走 Application 消息过滤器）、甚至其它程序的输入都会刷新它。
    /// 获取失败返回 0（按"刚有输入"处理，宁可少降级也不误降级）。
    /// </summary>
    private static int GetSystemIdleInputSeconds()
    {
        try
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info))
                return 0;

            // dwTime 是 GetTickCount 毫秒数（uint，约 49.7 天回绕），用无符号差值比较才回绕安全
            uint now = unchecked((uint)Environment.TickCount);
            int idleMs = unchecked((int)(now - info.dwTime)) & 0x7FFFFFFF;
            return Math.Max(0, idleMs / 1000);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 主界面超过 <see cref="RoleAutoDowngradeSeconds"/> 秒无鼠标 / 键盘 / 触摸操作时，自动降级为作业员权限
    /// （降级不需要密码，只有升权才校验密码）。由 <see cref="_statusTimer"/> 每秒驱动。
    /// 判据 = 应用级消息过滤器时间戳 与 系统级最后输入时间 取并集，任一显示近期有操作就不降级；
    /// 系统级输入仅在「前台窗口属于本程序」时计入 —— 只认本程序窗口内的操作，
    /// 操作员使用同机其它程序不会阻止降级。
    /// </summary>
    private void CheckRoleAutoDowngrade()
    {
        if (_currentRole == UserRole.作业员)
            return;

        int limitSeconds = RoleAutoDowngradeSeconds;
        bool appActive = (DateTime.UtcNow - _lastMouseActivityUtc).TotalSeconds < limitSeconds;
        // 系统级输入时间只在「前台窗口属于本程序」时才计入（只认本程序窗口内的操作）：
        // 操作员去操作同机其它程序时，那些输入不会阻止降级；
        // 反之在本程序窗口内（含模态弹窗）的鼠标 / 键盘 / 触摸输入都有效
        bool systemActive = IsForegroundWindowInOurProcess() && GetSystemIdleInputSeconds() < limitSeconds;
        if (appActive || systemActive)
            return;

        // 主窗体被模态对话框占用（如权限验证、参数确认弹窗）时不降级，并把计时起点拨到当前，
        // 关闭弹窗后从零重新计时，避免"弹窗里操作了很久、一关就被立刻踢回作业员"
        if (!Enabled || (ActiveForm is { Modal: true } && ActiveForm != this))
        {
            _lastMouseActivityUtc = DateTime.UtcNow;
            return;
        }

        _currentRole = UserRole.作业员;
        _suspendRoleChanged = true;
        if (_cmbUserRole.Items.Count > (int)UserRole.作业员)
            _cmbUserRole.SelectedIndex = (int)UserRole.作业员;
        _suspendRoleChanged = false;
        ApplyRolePermissions();
        AppendLog($"主界面 {limitSeconds} 秒无鼠标 / 键盘 / 触摸操作，已自动切换为作业员权限。");
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
        _manualOperationPromptConfigControl.SetPermission(canAddDelete, canModify);
        _taskSettingsControl.SetPermission(canModify);
        _communicationConfigControl.SetPermission(canModify);
        _stationConfigControl.SetPermission(canAddDelete, canModify);
        _buttonDisplayConfigControl.SetPermission(canAddDelete, canModify);
        _horizontalButtonConfigControl.SetPermission(canAddDelete, canModify);
        _plcBoolLabelDisplayConfigControl.SetPermission(canAddDelete, canModify);
        _plcValueTextBoxDisplayConfigControl.SetPermission(canAddDelete, canModify);

        foreach (ChildPlcModule module in _childPlcModules)
        {
            module.ButtonDisplayConfig.SetPermission(canAddDelete, canModify);
            module.HorizontalButtonConfig.SetPermission(canAddDelete, canModify);
            module.BoolDisplayConfig.SetPermission(canAddDelete, canModify);
            module.ValueDisplayConfig.SetPermission(canAddDelete, canModify);
            module.ProcessDataConfig.SetPermission(canAddDelete, canModify);
            foreach (ParameterTableControl table in module.ParameterTables)
                table.SetPermission(canAddDelete, canModify);
            foreach (UIButton button in module.ImportButtons)
                button.Enabled = canModify;
            module.AxisControl.SetPermission(canAddDelete, canModify);
            module.PositionControl.SetPermission(canAddDelete, canModify);
            module.OutputControl.SetPermission(canAddDelete, canModify);
            module.StationConfig.SetPermission(canAddDelete, canModify);
            module.MonitorControl.SetPermission(canAddDelete, canModify);
            // 子 PLC 的报警配置 / 报警弹窗配置也要随角色刷新，否则会停在创建时的禁用状态
            module.AlarmConfig.SetPermission(canAddDelete, canModify);
            module.AlarmPopupConfig.SetPermission(canAddDelete, canModify);
            module.ManualPromptConfig.SetPermission(canAddDelete, canModify);
        }

        _btnPageDebug.Enabled = role != UserRole.作业员;
        // 「调试按钮」「工站控制」还受自动运行信号限制，由 ApplyNavButtonEnabledState 统一计算
        ApplyNavButtonEnabledState();
        _btnPageDianCiHan.Enabled = role != UserRole.作业员;
        _btnPageMes.Enabled = role != UserRole.作业员;
        // MES 界面用「登录用户名为空」判定未登录，角色切换后同步给设备上下文
        if (_mesContext is not null)
            _mesContext.LoginUserName = GetCurrentLoginUserName();
        _btnPageCameraDebug.Enabled = role != UserRole.作业员;
        _btnPageMonitor.Enabled = true;
        _btnPageParameterAdjust.Enabled = role != UserRole.作业员;
        _btnPageProduction.Enabled = true;
        _btnPageCustemerUi.Enabled = true;
        // PLC 设备管理（含状态栏入口）仅管理员可用；非管理员时关闭已打开的管理窗口
        bool canOpenPlcManager = role == UserRole.管理员;
        _btnPlcDeviceManager.Enabled = canOpenPlcManager;
        _lblPlcManager.Enabled = canOpenPlcManager;
        if (!canOpenPlcManager)
            _plcDeviceManagerWindow?.Close();
        // 仅工程师 / 管理员显示底部实时日志框；作业员隐藏，避免误读运行日志
        bool showLog = role is UserRole.管理员 or UserRole.工程师;
        if (_txtLog.Visible && !showLog)
        {
            // 隐藏时清空 native Edit 控件里累积的文本，释放内存；历史全量仍在 SQLite
            _txtLog.Clear();
            _liveLogShown.Clear();
            lock (_liveLogSync)
                _liveLogPending.Clear();
        }
        _txtLog.Visible = showLog;
        _liveLogBoxVisible = showLog;
        // 主页面报警区「日志查询」入口：仅工程师 / 管理员可见（作业员隐藏，避免误入运行日志）
        _btnMainLogQuery.Visible = role is UserRole.管理员 or UserRole.工程师;

        if (role == UserRole.作业员)
        {
            _systemConfigPageWindow?.Close();
            _debugToolsPageWindow?.Close();
            _parameterAdjustPageWindow?.Close();
            _stationControlPopupForm?.Close();
            _dianCiHanWindow?.Close();
            _mesWindow?.Close();
            _cameraDebugWindow?.Close();
            _custemerUiWindow?.Close();
            ShowPage(_mainPage);
        }
    }

    private void RebuildChildPlcModulePages()
    {
        ClearChildMainDisplayGroups();
        _childLastProbeOk.Clear();
        _childButtonsRefreshPending = true;
        foreach (ChildPlcModule module in _childPlcModules)
        {
            module.SystemPage.Dispose();
            module.DebugPage.Dispose();
            module.StationPage.Dispose();
            module.MonitorPage.Dispose();
            module.ParameterAdjustPage.Dispose();
        }
        _childPlcModules.Clear();

        foreach (PlcDeviceProfile device in _plcDeviceConfiguration.Devices.Skip(1))
            _childPlcModules.Add(CreateChildPlcModule(device));

        _systemConfigDeviceTabs = RebuildDeviceTabbedHost(
            _debugPage, _systemConfigDeviceTabs, _debugTab, "主 PLC", x => x.SystemPage);
        _debugToolsDeviceTabs = RebuildDeviceTabbedHost(
            _debugToolsPage, _debugToolsDeviceTabs, _debugToolsTab, "主 PLC", x => x.DebugPage);
        _monitorDeviceTabs = RebuildDeviceTabbedHost(
            _monitorPage, _monitorDeviceTabs, _monitorTab, "主 PLC", x => x.MonitorPage);
        _parameterAdjustDeviceTabs = RebuildDeviceTabbedHost(
            _parameterAdjustPage, _parameterAdjustDeviceTabs, _parameterAdjustControl, "主 PLC", x => x.ParameterAdjustPage);
        _debugToolsDeviceTabs.SelectedIndexChanged += async (_, _) => await RefreshSelectedChildDebugPageAsync();
        _monitorDeviceTabs.SelectedIndexChanged += async (_, _) => await RefreshSelectedChildMonitorPageAsync();
        _parameterAdjustDeviceTabs.SelectedIndexChanged += async (_, _) => await LoadSelectedParameterAdjustAsync();

        if (_stationDeviceTabHost is not null)
            _stationDeviceTabs = RebuildDeviceTabbedHost(
                _stationDeviceTabHost, _stationDeviceTabs, _stationAreaPanel, "主 PLC", x => x.StationPage);

        RebuildChildMainDisplayGroups();

        // 子 PLC 页是新创建的控件树，挂载后重新套一次系统配置页排版（标题/正文分级）
        ConfigPageTypography.Apply(_debugPage);
    }

    private TabControl RebuildDeviceTabbedHost(
        Control host,
        TabControl? current,
        Control primaryContent,
        string primaryName,
        Func<ChildPlcModule, Control> getChildContent)
    {
        primaryContent.Parent?.Controls.Remove(primaryContent);
        if (current is not null)
        {
            current.Parent?.Controls.Remove(current);
            current.Dispose();
        }

        host.Controls.Clear();
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var primaryTab = new TabPage(primaryName) { Padding = new Padding(2) };
        primaryContent.Dock = DockStyle.Fill;
        primaryTab.Controls.Add(primaryContent);
        tabs.TabPages.Add(primaryTab);

        foreach (ChildPlcModule module in _childPlcModules)
        {
            var tab = new TabPage(module.Device.Name) { Padding = new Padding(2) };
            Control content = getChildContent(module);
            content.Dock = DockStyle.Fill;
            tab.Controls.Add(content);
            tabs.TabPages.Add(tab);
        }

        host.Controls.Add(tabs);
        return tabs;
    }

    private ChildPlcModule CreateChildPlcModule(PlcDeviceProfile device)
    {
        string storageKey = BuildPlcStorageKey(device.Id);
        Func<global::PlcConectCom.PlcCommunicator?> provider = () => GetPlcById(device.Id);

        var parameters = new ParameterTableControl(storageKey + "_parameters") { Dock = DockStyle.Fill };
        var hmiParameters = new ParameterTableControl(storageKey + "_hmi") { Dock = DockStyle.Fill };
        var buttonVariables = new ParameterTableControl(storageKey + "_buttons") { Dock = DockStyle.Fill };
        foreach (ParameterTableControl control in new[] { parameters, hmiParameters, buttonVariables })
        {
            ConfigureParameterTableControl(control);
            control.PlcProvider = provider;
            // 实时工艺地址（整板时间/待料时间等持续变化的量）不进入参数变化记录
            control.ProcessRealtimeStorageKey = storageKey;
        }
        var importButtons = new List<UIButton>
        {
            CreateChildParameterImportButton(parameters, "参数页面"),
            CreateChildParameterImportButton(hmiParameters, "HMI显示数据页面"),
            CreateChildParameterImportButton(buttonVariables, "按钮类变量页面")
        };
        parameters.PlaceImportButtonAtEnd(importButtons[0]);
        hmiParameters.PlaceImportButtonAtEnd(importButtons[1]);
        buttonVariables.PlaceImportButtonAtEnd(importButtons[2]);

        var processData = new ProcessRealtimeDataConfigControl(storageKey) { Dock = DockStyle.Fill, PlcProvider = provider, LogAction = AppendLog, EditPermissionRequest = RequestTableEditAuthorization };
        var buttonDisplay = new ButtonDisplayConfigControl(storageKey) { Dock = DockStyle.Fill };
        var horizontalButton = new HorizontalButtonConfigControl(storageKey) { Dock = DockStyle.Fill };
        var boolDisplay = new PlcBoolLabelDisplayConfigControl(storageKey) { Dock = DockStyle.Fill };
        var valueDisplay = new PlcValueTextBoxDisplayConfigControl(storageKey) { Dock = DockStyle.Fill };
        foreach (Control control in new Control[] { buttonDisplay, horizontalButton, boolDisplay, valueDisplay })
        {
            switch (control)
            {
                case ButtonDisplayConfigControl c: c.LogAction = AppendLog; c.EditPermissionRequest = RequestTableEditAuthorization; break;
                case HorizontalButtonConfigControl c: c.LogAction = AppendLog; c.EditPermissionRequest = RequestTableEditAuthorization; break;
                case PlcBoolLabelDisplayConfigControl c: c.LogAction = AppendLog; c.EditPermissionRequest = RequestTableEditAuthorization; break;
                case PlcValueTextBoxDisplayConfigControl c: c.LogAction = AppendLog; c.EditPermissionRequest = RequestTableEditAuthorization; break;
            }
        }

        var systemTabs = new TabControl { Dock = DockStyle.Fill };
        AddDeviceModuleTab(systemTabs, "参数设置", parameters);
        AddDeviceModuleTab(systemTabs, "HMI显示数据", hmiParameters);
        AddDeviceModuleTab(systemTabs, "按钮类变量", buttonVariables);
        AddDeviceModuleTab(systemTabs, "工艺实时数据配置", processData);
        AddDeviceModuleTab(systemTabs, "纵向按钮配置", buttonDisplay);
        AddDeviceModuleTab(systemTabs, "横向按钮配置", horizontalButton);
        AddDeviceModuleTab(systemTabs, "显示状态控件配置", boolDisplay);
        AddDeviceModuleTab(systemTabs, "文本显示控件配置", valueDisplay);

        // 子 PLC 独立的报警配置 / 报警弹窗配置（配置文件按设备 storageKey 区分）
        var alarmConfig = new AlarmAddressManagerControl(storageKey)
        {
            Dock = DockStyle.Fill,
            PlcProvider = provider,
            LogAction = AppendLog,
            EditPermissionRequest = RequestTableEditAuthorization
        };
        var alarmPopupConfig = new AlarmPopupManagerControl(storageKey)
        {
            Dock = DockStyle.Fill,
            PlcProvider = provider,
            LogAction = AppendLog,
            EditPermissionRequest = RequestTableEditAuthorization
        };
        AddDeviceModuleTab(systemTabs, "报警配置", alarmConfig);
        AddDeviceModuleTab(systemTabs, "报警弹窗配置", alarmPopupConfig);

        // 子 PLC 独立的手动操作提示配置（配置文件按设备 storageKey 区分），功能与主 PLC 一致
        var manualPromptConfig = new ManualOperationPromptConfigControl(storageKey)
        {
            Dock = DockStyle.Fill,
            PlcProvider = provider,
            LogAction = AppendLog,
            EditPermissionRequest = RequestTableEditAuthorization
        };
        AddDeviceModuleTab(systemTabs, "手动操作提示配置", manualPromptConfig);

        // 子 PLC 报警配置/报警弹窗配置的 Excel 导入按钮（与主 PLC 同款样式）
        UIButton alarmImportButton = CreateChildAlarmImportButton(
            _btnImportAlarmConfigData,
            () => ImportAlarmConfigDataFromExcel(alarmConfig, device.Name + " 报警配置页面"));
        UIButton alarmPopupImportButton = CreateChildAlarmImportButton(
            _btnImportAlarmPopupData,
            () => ImportAlarmPopupConfigFromExcel(alarmPopupConfig, device.Name + " 报警弹窗配置页面"));
        alarmConfig.PlaceImportButtonAtEnd(alarmImportButton);
        alarmPopupConfig.PlaceImportButtonAtEnd(alarmPopupImportButton);
        importButtons.Add(alarmImportButton);
        importButtons.Add(alarmPopupImportButton);

        // 手动操作提示配置的 Excel 导入按钮
        UIButton manualPromptImportButton = CreateChildAlarmImportButton(
            _btnImportManualOperationPromptData,
            () => ImportManualOperationPromptFromExcel(manualPromptConfig, device.Name + " 手动操作提示配置"));
        manualPromptConfig.PlaceImportButtonAtEnd(manualPromptImportButton);
        importButtons.Add(manualPromptImportButton);

        // 子 PLC 报警并入主页实时报警表格：快照合并显示，触发时写报警记录/Top3 统计（带设备名前缀）
        _childActiveAlarms[device.Id] = (device.Name, []);
        alarmConfig.ActiveAlarmsSnapshotUpdated += snapshot =>
        {
            _childActiveAlarms[device.Id] = (device.Name, snapshot);
            SyncCombinedAlarmGrid();
        };
        alarmConfig.AlarmTriggered += alarm => HandleAlarmTriggered(alarm, device.Name);

        var axis = new AxisMotionGroupTableControl(storageKey + "_axis")
        {
            Dock = DockStyle.Fill,
            PlcProvider = provider,
            LogAction = AppendLog,
            DeviceDisplayName = device.Name,
            AbsResetPasswordConfirmRequest = PromptAbsResetPasswordConfirmation
        };
        if (axis.GetAxes().Count == 0)
            axis.SetAxes([new AxisMotionAddressConfig { AxisId = "Axis1" }]);
        var positions = new PositionParameterTableControl(storageKey + "_positions")
        {
            Dock = DockStyle.Fill,
            PlcProvider = provider,
            LogAction = AppendLog,
            DeviceDisplayName = device.Name,
            AxisNameProvider = axis.GetAxisNames,
            AxisFieldAddressResolver = (axisName, fieldKey) => axis.GetAxes()
                .FirstOrDefault(x => string.Equals(x.AxisId, axisName, StringComparison.OrdinalIgnoreCase))?.Addresses
                .GetValueOrDefault(fieldKey)
        };
        var outputs = new BoolGroupControlTable(storageKey + "_outputs") { Dock = DockStyle.Fill, PlcProvider = provider, LogAction = AppendLog, DeviceDisplayName = device.Name, EditPermissionRequest = RequestTableEditAuthorization };
        UIButton outputImportButton = CreateChildBoolGroupImportButton(outputs);
        importButtons.Add(outputImportButton);
        outputs.PlaceImportButtonAtEnd(outputImportButton);
        var inputMatrix = new BoolSignalMatrixEmbedControl(storageKey + "_monitor") { Dock = DockStyle.Fill, PlcProvider = provider, LogAction = AppendLog };
        var axisSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 6,
            BorderStyle = BorderStyle.FixedSingle
        };
        axisSplit.Panel1.Controls.Add(axis);
        axisSplit.Panel2.Controls.Add(positions);
        axisSplit.SizeChanged += (_, _) =>
        {
            if (axisSplit.Height <= 0)
                return;
            // 与主 PLC 轴组页一致：上方轴组运动占 2/5，下方位置参数表占 3/5
            int min = Math.Max(1, axisSplit.Panel1MinSize);
            int max = axisSplit.Height - axisSplit.SplitterWidth - axisSplit.Panel2MinSize;
            if (max >= min)
                axisSplit.SplitterDistance = Math.Clamp((int)Math.Round(axisSplit.Height * AxisSplitTopRatio), min, max);
        };

        var outputSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6,
            BorderStyle = BorderStyle.FixedSingle
        };
        outputSplit.Panel1.Controls.Add(outputs);
        outputSplit.Panel2.Controls.Add(inputMatrix);
        outputSplit.SizeChanged += (_, _) => ApplyChildOutputSplitLayout();

        var debugTabs = new UITabControl { Dock = DockStyle.Fill };
        AddDeviceModuleTab(debugTabs, "轴组控制", axisSplit);
        AddDeviceModuleTab(debugTabs, "输出点位", outputSplit);
        debugTabs.SelectedIndexChanged += (_, _) =>
        {
            if (debugTabs.SelectedTab?.Text == "输出点位")
                inputMatrix.ReloadFromConfigFile();
        };

        void ApplyChildOutputSplitLayout()
        {
            const int tableMin = 240;
            const int matrixMin = 220;
            if (outputSplit.Width - outputSplit.SplitterWidth < tableMin + matrixMin)
                return;

            try
            {
                outputSplit.Panel1MinSize = tableMin;
                outputSplit.Panel2MinSize = matrixMin;
                int maxDistance = outputSplit.Width - outputSplit.Panel2MinSize - outputSplit.SplitterWidth;
                int target = (int)(outputSplit.Width * BoolGroupOutputLeftPanelWidthFraction);
                outputSplit.SplitterDistance = Math.Clamp(target, outputSplit.Panel1MinSize, maxDistance);
            }
            catch (InvalidOperationException)
            {
            }
        }

        var stationConfig = new StationOperationConfigManagerControl(storageKey + "_stations") { Dock = DockStyle.Fill, LogAction = AppendLog, EditPermissionRequest = RequestTableEditAuthorization };
        var stationDisplay = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8) };
        stationDisplay.SizeChanged += (_, _) => ArrangeStationCards(stationDisplay);
        RebuildChildStationDisplay(stationDisplay, stationConfig.GetConfigurations(), provider);
        stationConfig.ConfigurationsChanged += configs => RebuildChildStationDisplay(stationDisplay, configs, provider);
        AddDeviceModuleTab(systemTabs, "工站配置", stationConfig);
        var stationTabs = new TabControl { Dock = DockStyle.Fill };
        AddDeviceModuleTab(stationTabs, "工站运行", stationDisplay);

        var signals = new BoolSignalCollectionControl(storageKey + "_monitor") { Dock = DockStyle.Fill, PlcProvider = provider, LogAction = AppendLog, EditPermissionRequest = RequestTableEditAuthorization };

        var parameterAdjust = new ParameterAdjustControl($"ParameterAdjust.keywords.{storageKey}.json")
        {
            Dock = DockStyle.Fill,
            PlcProvider = provider,
            LogAction = AppendLog,
            RegularParametersProvider = () => parameters.GetParameters(),
            HmiDisplayParametersProvider = () => hmiParameters.GetParameters(),
            ButtonVarParametersProvider = () => buttonVariables.GetParameters()
        };

        var module = new ChildPlcModule
        {
            Device = device.Copy(),
            SystemPage = CreateDeviceModulePage(device.Name, systemTabs),
            DebugPage = CreateDeviceModulePage(device.Name, debugTabs),
            StationPage = CreateDeviceModulePage(device.Name, stationTabs),
            MonitorPage = CreateDeviceModulePage(device.Name, signals),
            ParameterAdjustPage = CreateDeviceModulePage(device.Name + " 参数调整", parameterAdjust),
            ParameterAdjustControl = parameterAdjust,
            ButtonDisplayConfig = buttonDisplay,
            HorizontalButtonConfig = horizontalButton,
            BoolDisplayConfig = boolDisplay,
            ValueDisplayConfig = valueDisplay,
            ProcessDataConfig = processData,
            AlarmConfig = alarmConfig,
            AlarmPopupConfig = alarmPopupConfig,
            ManualPromptConfig = manualPromptConfig,
            AxisControl = axis,
            PositionControl = positions,
            OutputControl = outputs,
            InputMatrixControl = inputMatrix,
            MonitorControl = signals,
            ParameterTables = [parameters, hmiParameters, buttonVariables],
            ImportButtons = importButtons.ToArray(),
            StationConfig = stationConfig
        };
        buttonDisplay.ConfigurationsChanged += _ => RebuildChildMainDisplayGroups();
        horizontalButton.ConfigurationsChanged += _ => RebuildChildMainDisplayGroups();
        boolDisplay.ConfigurationsChanged += _ => RebuildChildMainDisplayGroups();
        valueDisplay.ConfigurationsChanged += _ => RebuildChildMainDisplayGroups();
        processData.ConfigurationChanged += (_, _) => RebuildChildProcessDisplays();

        ApplyPermissions();
        return module;

        void ApplyPermissions()
        {
            bool canAddDelete = _currentRole == UserRole.管理员;
            bool canModify = _currentRole is UserRole.管理员 or UserRole.工程师;
            parameters.SetPermission(canAddDelete, canModify);
            hmiParameters.SetPermission(canAddDelete, canModify);
            buttonVariables.SetPermission(canAddDelete, canModify);
            processData.SetPermission(canAddDelete, canModify);
            buttonDisplay.SetPermission(canAddDelete, canModify);
            boolDisplay.SetPermission(canAddDelete, canModify);
            valueDisplay.SetPermission(canAddDelete, canModify);
            alarmConfig.SetPermission(canAddDelete, canModify);
            alarmPopupConfig.SetPermission(canAddDelete, canModify);
            manualPromptConfig.SetPermission(canAddDelete, canModify);
            axis.SetPermission(canAddDelete, canModify);
            positions.SetPermission(canAddDelete, canModify);
            outputs.SetPermission(canAddDelete, canModify);
            stationConfig.SetPermission(canAddDelete, canModify);
            signals.SetPermission(canAddDelete, canModify);
            foreach (UIButton button in importButtons)
                button.Enabled = canModify;
        }
    }

    private static Panel CreateDeviceModulePage(string deviceName, Control content)
    {
        var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6), BackColor = Color.White };
        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Text = $"{deviceName} 独立参数与数据",
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(28, 58, 120)
        };
        content.Dock = DockStyle.Fill;
        root.Controls.Add(content);
        root.Controls.Add(title);
        return root;
    }

    private static void AddDeviceModuleTab(TabControl tabs, string title, Control content)
    {
        var page = new TabPage(title) { Padding = new Padding(2) };
        content.Dock = DockStyle.Fill;
        page.Controls.Add(content);
        tabs.TabPages.Add(page);
    }

    private static void RebuildChildStationDisplay(
        FlowLayoutPanel host,
        IReadOnlyList<StationOperationConfig> configurations,
        Func<global::PlcConectCom.PlcCommunicator?> provider)
    {
        foreach (Control old in host.Controls.Cast<Control>().ToList())
        {
            host.Controls.Remove(old);
            old.Dispose();
        }

        foreach (StationOperationConfig cfg in configurations)
        {
            var control = new StationOperationStatusControl
            {
                Width = 300,
                Margin = new Padding(6),
                PlcProvider = provider,
                StationName = cfg.StationName,
                StationEnabled = cfg.StationEnabled,
                StationEnabledAddress = cfg.StationEnabledAddress,
                StartCommandAddress = cfg.StartCommandAddress,
                PauseCommandAddress = cfg.PauseCommandAddress,
                StopCommandAddress = cfg.StopCommandAddress,
                ResetCommandAddress = cfg.ResetCommandAddress,
                AutoSignalAddress = cfg.AutoSignalAddress,
                PauseSignalAddress = cfg.PauseSignalAddress,
                InitSignalAddress = cfg.InitSignalAddress,
                InitDoneSignalAddress = cfg.InitDoneSignalAddress,
                StationAlarmAddress = cfg.StationAlarmAddress,
                InitTimeoutAlarmAddress = cfg.InitTimeoutAlarmAddress,
                InitSafetyConditionAddress = cfg.InitSafetyConditionAddress,
                InitSafetyNotMetAlarmAddress = cfg.InitSafetyNotMetAlarmAddress,
                SingleCycleTimeAddress = cfg.SingleCycleTimeAddress,
                FlowStatusIdAddress = cfg.FlowStatusIdAddress,
                FlowStatusIdValueType = cfg.FlowStatusIdValueType,
                AutoRefreshEnabled = true
            };
            try
            {
                if (!string.IsNullOrWhiteSpace(cfg.FlowStatusMapFilePath) && File.Exists(cfg.FlowStatusMapFilePath))
                    control.LoadFlowStatusMapFromExcel(cfg.FlowStatusMapFilePath, cfg.StationName);
            }
            catch { }
            host.Controls.Add(control);
        }
        ArrangeStationCards(host);
    }

    private async Task RefreshSelectedChildDebugPageAsync()
    {
        int childIndex = (_debugToolsDeviceTabs?.SelectedIndex ?? 0) - 1;
        if (childIndex < 0 || childIndex >= _childPlcModules.Count)
            return;

        ChildPlcModule module = _childPlcModules[childIndex];
        try
        {
            await module.AxisControl.RefreshAllAxisDataAsync();
            await module.PositionControl.RefreshDataAsync();
            await module.OutputControl.RefreshStatusAsync();
            module.InputMatrixControl.ReloadFromConfigFile();
        }
        catch (Exception ex)
        {
            AppendLog($"PLC[{module.Device.Name}] 调试数据刷新失败: {ex.GetBaseException().Message}");
        }
    }

    private async Task RefreshSelectedChildMonitorPageAsync()
    {
        int childIndex = (_monitorDeviceTabs?.SelectedIndex ?? 0) - 1;
        if (childIndex < 0 || childIndex >= _childPlcModules.Count)
            return;

        ChildPlcModule module = _childPlcModules[childIndex];
        try
        {
            await module.MonitorControl.RefreshSignalsAsync();
        }
        catch (Exception ex)
        {
            AppendLog($"PLC[{module.Device.Name}] 监控数据刷新失败: {ex.GetBaseException().Message}");
        }
    }

    private void RebuildChildMainDisplayGroups()
    {
        foreach (Control group in _childMainDisplayGroups)
        {
            group.Parent?.Controls.Remove(group);
            group.Dispose();
        }
        _childMainDisplayGroups.Clear();
        _childBoundButtons.Clear();
        _childBoolSwitchControls.Clear();

        foreach (ChildPlcModule module in _childPlcModules)
        {
            Func<global::PlcConectCom.PlcCommunicator?> provider = () => GetPlcById(module.Device.Id);
            _childMainDisplayGroups.AddRange(AddChildDisplayControls(module.ButtonDisplayConfig.GetConfigurations().Select(cfg =>
            {
                Control display;
                if (cfg.ControlType == ButtonDisplayControlType.PLC开关)
                {
                    var sw = new PlcBoolSwitchControl { Width = (int)Math.Round(220 * _mainRightUiScale), Title = cfg.DisplayName, PlcAddress = cfg.PlcAddress, ConditionPlcAddress = cfg.ConditionPlcAddress ?? string.Empty, ConditionWhen = cfg.ConditionWhen, PlcProvider = provider, LogAction = AppendLog };
                    sw.ApplyUiScale(_mainRightUiScale);
                    _childBoolSwitchControls.Add(sw);
                    display = sw;
                }
                else
                {
                    var button = new PlcBoundButton { PlcProvider = provider, LogAction = AppendLog, AutoReset = cfg.AutoReset };
                    button.ApplyBinding(cfg.PlcAddress, cfg.ValueType, cfg.StringLength);
                    button.ApplyUiScale(_mainRightUiScale);
                    button.Text = cfg.DisplayName;
                    _childBoundButtons.Add(button);
                    display = button;
                }
                return display;
            }), _mainButtonDisplayPanel));

            _childMainDisplayGroups.AddRange(AddChildDisplayControls(module.BoolDisplayConfig.GetConfigurations().Where(cfg => !string.IsNullOrWhiteSpace(cfg.PlcAddress)).Select(cfg => (Control)new PlcBoolLabe
            {
                Width = 180,
                Height = 35,
                PlcAddress = cfg.PlcAddress.Trim(),
                TrueText = string.IsNullOrWhiteSpace(cfg.TrueText) ? "True" : cfg.TrueText,
                FalseText = string.IsNullOrWhiteSpace(cfg.FalseText) ? "False" : cfg.FalseText,
                ValueForeColor = Color.FromArgb(cfg.ForeColorArgb),
                TrueColor = Color.FromArgb(cfg.TrueBackColorArgb),
                FalseColor = Color.FromArgb(cfg.FalseBackColorArgb),
                PlcProvider = provider,
                LogAction = AppendLog
            }), _mainPlcBoolLabelPanel));

            _childMainDisplayGroups.AddRange(AddChildDisplayControls(module.ValueDisplayConfig.GetConfigurations().Where(cfg => !string.IsNullOrWhiteSpace(cfg.PlcAddress)).Select(cfg =>
            {
                var control = new PlcValueTextBoxControl
                {
                    Title = string.IsNullOrWhiteSpace(cfg.DisplayName) ? cfg.PlcAddress.Trim() : cfg.DisplayName.Trim(),
                    PlcAddress = cfg.PlcAddress.Trim(),
                    DataType = cfg.DataType,
                    ControlMode = cfg.ControlMode,
                    StringLength = Math.Max(1, cfg.StringLength),
                    UseIntComboMapping = cfg.UseIntComboMapping,
                    IntValueMappingDescription = cfg.IntValueMappingDescription ?? string.Empty,
                    DisplayOffsetY = 0, // 主页面流式布局内不偏移，与按钮行对齐
                    PlcProvider = provider,
                    LogAction = AppendLog
                };
                ApplyMainLeftValueTextBoxStyle(control); // 与「功能按钮」控件同尺寸/同字体/同扁平风格
                return (Control)control;
            }), _mainPlcValueTextBoxPanel));

            // 子 PLC 的横向按钮 → 主页面「功能按钮」区（与状态显示 Label 同尺寸 180×35）
            _childMainDisplayGroups.AddRange(AddChildDisplayControls(
                module.HorizontalButtonConfig.GetConfigurations()
                    .Where(cfg => !string.IsNullOrWhiteSpace(cfg.PlcAddress))
                    .Select(cfg =>
                    {
                        var button = new PlcBoundButton
                        {
                            AutoReset = cfg.AutoReset,
                            ReadBackState = cfg.ReadBackState,
                            PlcProvider = provider,
                            LogAction = AppendLog,
                            TrueText = cfg.TrueText ?? string.Empty,
                            FalseText = cfg.FalseText ?? string.Empty
                        };
                        button.ApplyBinding(cfg.PlcAddress.Trim(), cfg.ValueType, Math.Max(1, cfg.StringLength));
                        ApplyMainLeftButtonScale(button);
                        button.Text = string.IsNullOrWhiteSpace(cfg.DisplayName) ? cfg.PlcAddress.Trim() : cfg.DisplayName.Trim();
                        _childBoundButtons.Add(button);
                        return (Control)button;
                    }), _mainPlcButtonPanel));
        }

        RebuildChildProcessDisplays();

        // 重建后统一刷新一次连接态显示（此时子 PLC 可能已在线，避免停留在「未连接」）
        _childButtonsRefreshPending = true;

        static List<Control> AddChildDisplayControls(IEnumerable<Control> displays, Control host)
        {
            var addedControls = new List<Control>();
            foreach (Control display in displays)
            {
                display.Margin = new Padding(5);
                host.Controls.Add(display);
                addedControls.Add(display);
            }
            return addedControls;
        }
    }

    private void ClearChildMainDisplayGroups()
    {
        foreach (Control group in _childMainDisplayGroups.Concat(_childProcessDisplayGroups).ToList())
        {
            group.Parent?.Controls.Remove(group);
            group.Dispose();
        }
        _childMainDisplayGroups.Clear();
        _childProcessDisplayGroups.Clear();
    }

    private void RebuildChildProcessDisplays()
    {
        foreach (Control group in _childProcessDisplayGroups)
        {
            group.Parent?.Controls.Remove(group);
            group.Dispose();
        }
        _childProcessDisplayGroups.Clear();
        foreach (ChildPlcModule module in _childPlcModules)
        {
            foreach (ProcessRealtimeDataItem item in ProcessRealtimeDataConfigControl.LoadConfiguredItems(BuildPlcStorageKey(module.Device.Id)))
            {
                if (string.IsNullOrWhiteSpace(item.Address))
                    continue;
                var display = new ProcessRealtimeValueDisplayControl
                {
                    Margin = new Padding(5),
                    PlcProvider = () => GetPlcById(module.Device.Id),
                    LogAction = AppendLog,
                    AutoReadEnabled = true
                };
                display.ApplyItem(item);
                _mainProcessRealtimeFlow.Controls.Add(display);
                _childProcessDisplayGroups.Add(display);
            }
        }
    }

    private static string BuildPlcStorageKey(string deviceId)
    {
        string safeId = string.Concat(deviceId.Where(char.IsLetterOrDigit));
        return $"plc_{safeId}_{ComputeSha256Hex(deviceId)[..10]}";
    }

    private IEnumerable<Control> GetStaticPages()
    {
        yield return _mainPage;
        yield return _debugPage;
        yield return _debugToolsPage;
        yield return _monitorPage;
        yield return _recordsPage;
        yield return _manualOperationLogPage;
        yield return _parameterAdjustPage;
        yield return _productionPage;
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
                     _btnImportAlarmPopupData,
                     _btnImportManualOperationPromptData
                 })
        {
            button.Enabled = enabled;
        }
    }

    private static UserRole ReadUserRoleFromCombo(UIComboBox combo)
    {
        int i = combo.SelectedIndex;
        var names = Enum.GetNames<UserRole>();
        if (i >= 0 && i < names.Length)
            return Enum.Parse<UserRole>(names[i]);

        string text = combo.SelectedItem?.ToString()?.Trim() ?? string.Empty;
        return Enum.TryParse(text, out UserRole parsed) ? parsed : UserRole.作业员;
    }

    private void HandleRoleSelectionChanged()
    {
        if (_suspendRoleChanged)
            return;

        UserRole selectedRole = ReadUserRoleFromCombo(_cmbUserRole);

        if (selectedRole == _currentRole)
            return;

        if (selectedRole is UserRole.管理员 or UserRole.工程师)
        {
            if (!PromptRoleAuthorization(selectedRole))
            {
                _suspendRoleChanged = true;
                _cmbUserRole.SelectedIndex = (int)_currentRole;
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

        DataGridViewNoSortHelper.WireControlSubtreeForNoColumnSort(dialog);
        if (AppDialog.ShowDialog(dialog, this) != DialogResult.OK)
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

    /// <summary>校验与「当前登录权限」一致的密码（含作业员密码）。</summary>
    private bool ValidateCurrentRolePassword(string password)
    {
        string p = (password ?? string.Empty).Trim();
        return _currentRole switch
        {
            UserRole.管理员 => string.Equals(p, _authConfig.AdminPassword, StringComparison.Ordinal),
            UserRole.工程师 => string.Equals(p, _authConfig.EngineerPassword, StringComparison.Ordinal),
            UserRole.作业员 => string.Equals(p, _authConfig.OperatorPassword, StringComparison.Ordinal),
            _ => false
        };
    }

    private bool PromptAbsResetPasswordConfirmation()
    {
        using var dialog = new Form
        {
            Text = "绝对值清零确认",
            ClientSize = new Size(420, 200),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 2,
            RowCount = 3
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        var lblHint = new Label
        {
            Text = $"将执行所选轴的「绝对值清零」。\r\n当前权限：{_currentRole}。请输入该级别密码以继续。",
            AutoSize = true,
            MaximumSize = new Size(388, 0)
        };
        layout.Controls.Add(lblHint, 0, 0);
        layout.SetColumnSpan(lblHint, 2);

        var txtPassword = new TextBox { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label { Text = "密码", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 10, 8, 0) }, 0, 1);
        layout.Controls.Add(txtPassword, 1, 1);
        layout.Controls.Add(new Label(), 0, 2);

        var panelBtn = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
        var btnOk = new Button { Text = "确认执行", Width = 88, DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "取消", Width = 72, DialogResult = DialogResult.Cancel };
        panelBtn.Controls.Add(btnOk);
        panelBtn.Controls.Add(btnCancel);

        dialog.Controls.Add(layout);
        dialog.Controls.Add(panelBtn);
        dialog.AcceptButton = btnOk;
        dialog.CancelButton = btnCancel;

        DataGridViewNoSortHelper.WireControlSubtreeForNoColumnSort(dialog);
        if (AppDialog.ShowDialog(dialog, this) != DialogResult.OK)
            return false;

        if (ValidateCurrentRolePassword(txtPassword.Text))
            return true;

        MessageBox.Show(this, "密码与当前权限不匹配。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
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

        DataGridViewNoSortHelper.WireControlSubtreeForNoColumnSort(dialog);
        if (AppDialog.ShowDialog(dialog, this) != DialogResult.OK)
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
            Height = 300,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Padding = new Padding(10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var txtAdmin = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true, Text = _authConfig.AdminPassword };
        var txtEngineer = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true, Text = _authConfig.EngineerPassword };
        var txtOperator = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true, Text = _authConfig.OperatorPassword };

        // 工程师仅能改工程师密码与作业员密码；管理员三项均可改。
        if (_currentRole == UserRole.工程师)
            txtAdmin.Enabled = false;

        layout.Controls.Add(new Label { Text = "管理员密码", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(txtAdmin, 1, 0);
        layout.Controls.Add(new Label { Text = "工程师密码", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(txtEngineer, 1, 1);
        layout.Controls.Add(new Label { Text = "作业员密码", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        layout.Controls.Add(txtOperator, 1, 2);
        var lblHint = new Label
        {
            Text = "作业员密码用于玻璃电池串等现场确认；仅工程师或管理员可在本界面修改。\r\n密钥格式：ROLE=管理员;EXP=20261231120000.签名",
            AutoSize = true,
            ForeColor = Color.DimGray
        };
        layout.Controls.Add(lblHint, 0, 3);
        layout.SetColumnSpan(lblHint, 2);

        var btnPanel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var btnOk = new Button { Text = "保存", Width = 72, DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "取消", Width = 72, DialogResult = DialogResult.Cancel };
        btnPanel.Controls.Add(btnOk);
        btnPanel.Controls.Add(btnCancel);

        dialog.Controls.Add(layout);
        dialog.Controls.Add(btnPanel);
        dialog.AcceptButton = btnOk;
        dialog.CancelButton = btnCancel;

        DataGridViewNoSortHelper.WireControlSubtreeForNoColumnSort(dialog);
        if (AppDialog.ShowDialog(dialog, this) != DialogResult.OK)
            return;

        if (string.IsNullOrWhiteSpace(txtAdmin.Text) || string.IsNullOrWhiteSpace(txtEngineer.Text) || string.IsNullOrWhiteSpace(txtOperator.Text))
        {
            MessageBox.Show(this, "密码不能为空。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _authConfig.AdminPassword = txtAdmin.Text.Trim();
        _authConfig.EngineerPassword = txtEngineer.Text.Trim();
        _authConfig.OperatorPassword = txtOperator.Text.Trim();
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
            {
                _authConfig = cfg;
                _authConfig.AdminPassword = (_authConfig.AdminPassword ?? string.Empty).Trim();
                _authConfig.EngineerPassword = (_authConfig.EngineerPassword ?? string.Empty).Trim();
                _authConfig.OperatorPassword = (_authConfig.OperatorPassword ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(_authConfig.OperatorPassword))
                    _authConfig.OperatorPassword = new RoleAuthConfig().OperatorPassword;
            }
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
            bool missingProductionCounterAddress = !json.Contains(nameof(MainPlcConfig.ProductionCounterAddress), StringComparison.OrdinalIgnoreCase);
            var cfg = JsonSerializer.Deserialize<MainPlcConfig>(json);
            if (cfg is null)
            {
                _mainPlcConfig = new MainPlcConfig();
                SaveMainPlcConfig();
                return;
            }

            SanitizeMainPlcConfig(cfg);
            _mainPlcConfig = cfg;
            if (missingProductionCounterAddress)
                SaveMainPlcConfig();
        }
        catch
        {
            _mainPlcConfig = new MainPlcConfig();
        }
    }

    private void LoadCameraThreadStartupConfig()
    {
        try
        {
            if (!File.Exists(_cameraThreadStartupConfigPath))
            {
                _cameraThreadStartupConfig = new CameraThreadStartupConfig();
                SaveCameraThreadStartupConfig();
                return;
            }

            string json = File.ReadAllText(_cameraThreadStartupConfigPath);
            var cfg = JsonSerializer.Deserialize<CameraThreadStartupConfig>(json);
            _cameraThreadStartupConfig = cfg ?? new CameraThreadStartupConfig();
        }
        catch
        {
            _cameraThreadStartupConfig = new CameraThreadStartupConfig();
        }
    }

    private void SaveCameraThreadStartupConfig()
    {
        try
        {
            string json = JsonSerializer.Serialize(_cameraThreadStartupConfig, JsonFileOptions);
            File.WriteAllText(_cameraThreadStartupConfigPath, json);
            SyncCameraThreadStartupToCameraUiSettingFile();
        }
        catch
        {
            // ignore write exceptions to avoid UI interruption
        }
    }

    private void SyncCameraThreadStartupToCameraUiSettingFile()
    {
        try
        {
            Dictionary<string, string> values;
            if (File.Exists(_cameraUiSettingPath))
            {
                string existingJson = File.ReadAllText(_cameraUiSettingPath);
                values = JsonSerializer.Deserialize<Dictionary<string, string>>(existingJson)
                    ?? new Dictionary<string, string>(StringComparer.Ordinal);
            }
            else
            {
                values = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            foreach (var (key, _) in CameraThreadStartupItems)
            {
                bool enabled = GetCameraThreadStartupState(_cameraThreadStartupConfig, key);
                values["CameraThreadEnabled." + key] = enabled.ToString();
            }

            string json = JsonSerializer.Serialize(values, JsonFileOptions);
            File.WriteAllText(_cameraUiSettingPath, json);
        }
        catch
        {
            // ignore sync exceptions
        }
    }

    private Dictionary<string, bool> BuildCameraThreadStartupStateMap()
    {
        var map = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (key, _) in CameraThreadStartupItems)
            map[key] = GetCameraThreadStartupState(_cameraThreadStartupConfig, key);
        return map;
    }

    private static bool GetCameraThreadStartupState(CameraThreadStartupConfig config, string key)
    {
        return key switch
        {
            "HandleCam1" => config.HandleCam1,
            "HandleCam2" => config.HandleCam2,
            "HandleCam3" => config.HandleCam3,
            "HandleCam4" => config.HandleCam4,
            "HandleCam5" => config.HandleCam5,
            "HandleCam6" => config.HandleCam6,
            "HandleCam1LightCheck" => config.HandleCam1LightCheck,
            "HandleCam2LightCheck" => config.HandleCam2LightCheck,
            "HandleCam3LightCheck" => config.HandleCam3LightCheck,
            "HandleCam4LightCheck" => config.HandleCam4LightCheck,
            _ => true
        };
    }

    private static void SetCameraThreadStartupState(CameraThreadStartupConfig config, string key, bool enabled)
    {
        switch (key)
        {
            case "HandleCam1":
                config.HandleCam1 = enabled;
                break;
            case "HandleCam2":
                config.HandleCam2 = enabled;
                break;
            case "HandleCam3":
                config.HandleCam3 = enabled;
                break;
            case "HandleCam4":
                config.HandleCam4 = enabled;
                break;
            case "HandleCam5":
                config.HandleCam5 = enabled;
                break;
            case "HandleCam6":
                config.HandleCam6 = enabled;
                break;
            case "HandleCam1LightCheck":
                config.HandleCam1LightCheck = enabled;
                break;
            case "HandleCam2LightCheck":
                config.HandleCam2LightCheck = enabled;
                break;
            case "HandleCam3LightCheck":
                config.HandleCam3LightCheck = enabled;
                break;
            case "HandleCam4LightCheck":
                config.HandleCam4LightCheck = enabled;
                break;
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

        if (string.IsNullOrWhiteSpace(cfg.ProductionCounterAddress))
            cfg.ProductionCounterAddress = "MW100";
        else
            cfg.ProductionCounterAddress = cfg.ProductionCounterAddress.Trim();
    }

    private void ApplyMainPlcConfigToUi()
    {
        _txtProductionCounterAddress.Text = _mainPlcConfig.ProductionCounterAddress;
    }

    private void SaveProductionCounterAddressToMainPlcConfig()
    {
        string address = _txtProductionCounterAddress.Text.Trim();
        if (string.IsNullOrWhiteSpace(address))
            address = "MW100";

        if (string.Equals(_mainPlcConfig.ProductionCounterAddress, address, StringComparison.Ordinal))
            return;

        _mainPlcConfig.ProductionCounterAddress = address;
        _txtProductionCounterAddress.Text = address;
        _lastProductionCounterSignal = null;
        SaveMainPlcConfig();
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

    private void SavePlcDeviceConfiguration()
    {
        try
        {
            _plcDeviceConfiguration.Save(_plcDeviceConfigPath);
        }
        catch (Exception ex)
        {
            AppendLog($"保存 PLC 设备配置失败: {ex.GetBaseException().Message}");
        }
    }

    private Task OpenPlcDeviceManagerAsync()
    {
        if (_plcDeviceManagerWindow is { IsDisposed: false })
        {
            _plcDeviceManagerWindow.Activate();
            return Task.CompletedTask;
        }

        _plcDeviceManagerWindow = new PlcDeviceManagerWindow(
            _plcDeviceConfiguration,
            ApplyPlcDeviceConfigurationAsync,
            deviceId => _plcDevices.TryGetValue(deviceId, out var plc) && plc.IsConnected);
        _plcDeviceManagerWindow.FormClosed += (_, _) => _plcDeviceManagerWindow = null;
        _plcDeviceManagerWindow.Show(this);
        return Task.CompletedTask;
    }

    private Task RequestAndOpenPlcDeviceManagerAsync()
    {
        // PLC 设备管理仅管理员可用（导航按钮与状态栏入口统一在此把关）
        if (_currentRole != UserRole.管理员)
        {
            AppendLog("PLC 设备管理仅管理员权限可用。");
            return Task.CompletedTask;
        }
        return OpenPlcDeviceManagerAsync();
    }

    private async Task<IReadOnlyList<string>> ApplyPlcDeviceConfigurationAsync(PlcDeviceConfiguration configuration)
    {
        _plcDeviceConfiguration = configuration.Copy();
        _plcDeviceConfiguration.Normalize();
        if (_plcDeviceConfiguration.Devices.Count == 0)
            throw new InvalidOperationException("至少需要配置主 PLC。");

        _plcDeviceConfiguration.EnsureDefaultBindings();

        PlcDeviceProfile primary = _plcDeviceConfiguration.Devices[0];
        _mainPlcConfig.IpAddress = primary.IpAddress;
        _mainPlcConfig.Port = primary.Port;
        _mainPlcConfig.SlaveId = primary.SlaveId;
        _mainPlcConfig.HeartbeatBoolAddress = primary.HeartbeatBoolAddress;
        SaveMainPlcConfig();

        SavePlcDeviceConfiguration();
        RebuildChildPlcModulePages();
        await ConnectAsync();

        return _plcDeviceConfiguration.Devices
            .Where(device => !_plcDevices.TryGetValue(device.Id, out var plc) || !plc.IsConnected)
            .Select(device => $"{device.Name} 未连接")
            .ToArray();
    }

    private void DisconnectPlcDevices()
    {
        foreach (global::PlcConectCom.PlcCommunicator plc in _plcDevices.Values)
        {
            try { plc.Disconnect(); }
            catch { }
        }
        _plcDevices.Clear();
        _plc = null;
        _subHeartbeatFailed.Clear();
    }

    private void LoadMainPageDisplayConfig()
    {
        try
        {
            if (!File.Exists(_mainPageDisplayConfigPath))
            {
                _mainPageDisplayConfig = new MainPageDisplayConfig { DisplayMode = 0 };
                SaveMainPageDisplayConfigFile();
                return;
            }

            string json = File.ReadAllText(_mainPageDisplayConfigPath);
            var cfg = JsonSerializer.Deserialize<MainPageDisplayConfig>(json, JsonFileOptions);
            if (cfg is null)
                _mainPageDisplayConfig = new MainPageDisplayConfig { DisplayMode = 0 };
            else
                _mainPageDisplayConfig = cfg;
            _mainPageDisplayConfig.DisplayMode = Math.Clamp(_mainPageDisplayConfig.DisplayMode, 0, 2);
            _mainPageDisplayConfig.CustomerBoard = Math.Clamp(_mainPageDisplayConfig.CustomerBoard, 0, 1);
            _mainPageDisplayConfig.AutoRunningAddress = _mainPageDisplayConfig.AutoRunningAddress?.Trim() ?? string.Empty;
            _mainPageDisplayConfig.PlcVersionAddress = _mainPageDisplayConfig.PlcVersionAddress?.Trim() ?? string.Empty;
            _mainPageDisplayConfig.RoleAutoDowngradeSeconds = Math.Clamp(
                _mainPageDisplayConfig.RoleAutoDowngradeSeconds,
                MinRoleAutoDowngradeSeconds,
                MaxRoleAutoDowngradeSeconds);
        }
        catch
        {
            _mainPageDisplayConfig = new MainPageDisplayConfig { DisplayMode = 0 };
        }
    }

    private void SaveMainPageDisplayConfigFile()
    {
        try
        {
            _mainPageDisplayConfig.DisplayMode = Math.Clamp(_mainPageDisplayConfig.DisplayMode, 0, 2);
            _mainPageDisplayConfig.CustomerBoard = Math.Clamp(_mainPageDisplayConfig.CustomerBoard, 0, 1);
            _mainPageDisplayConfig.AutoRunningAddress = _mainPageDisplayConfig.AutoRunningAddress?.Trim() ?? string.Empty;
            _mainPageDisplayConfig.PlcVersionAddress = _mainPageDisplayConfig.PlcVersionAddress?.Trim() ?? string.Empty;
            _mainPageDisplayConfig.RoleAutoDowngradeSeconds = Math.Clamp(
                _mainPageDisplayConfig.RoleAutoDowngradeSeconds,
                MinRoleAutoDowngradeSeconds,
                MaxRoleAutoDowngradeSeconds);
            string json = JsonSerializer.Serialize(_mainPageDisplayConfig, JsonFileOptions);
            File.WriteAllText(_mainPageDisplayConfigPath, json);
        }
        catch
        {
            // 忽略写入失败
        }
    }

    private void SyncMainPageDisplayComboFromConfig()
    {
        if (_cmbMainPageDisplayMode.Items.Count == 0)
        {
            _cmbMainPageDisplayMode.Items.Add("不显示客户看板");
            _cmbMainPageDisplayMode.Items.Add("仅显示客户看板");
            _cmbMainPageDisplayMode.Items.Add("同时显示系统配置与客户看板");
        }

        if (_cmbCustomerBoard.Items.Count == 0)
        {
            _cmbCustomerBoard.Items.Add("排版机客户看板");
            _cmbCustomerBoard.Items.Add("小料机客户看板");
        }

        _cmbMainPageDisplayMode.SelectedIndex = Math.Clamp(_mainPageDisplayConfig.DisplayMode, 0, 2);
        _cmbCustomerBoard.SelectedIndex = Math.Clamp(_mainPageDisplayConfig.CustomerBoard, 0, 1);
        _txtAutoRunningAddress.Text = _mainPageDisplayConfig.AutoRunningAddress ?? string.Empty;
        _txtPlcVersionAddress.Text = _mainPageDisplayConfig.PlcVersionAddress ?? string.Empty;
        _numRoleDowngradeSeconds.Value = Math.Clamp(
            _mainPageDisplayConfig.RoleAutoDowngradeSeconds,
            MinRoleAutoDowngradeSeconds,
            MaxRoleAutoDowngradeSeconds);
        SyncTopAlarmRangePickers();
    }

    // 把配置里的 Top3 日期区间回填到日期控件（未配置的一侧不勾选）
    private void SyncTopAlarmRangePickers()
    {
        DateTime? from = TryParseConfigDate(_mainPageDisplayConfig.TopAlarmStartDate);
        DateTime? to = TryParseConfigDate(_mainPageDisplayConfig.TopAlarmEndDate);

        if (from is DateTime f)
        {
            _dtTopAlarmFrom.Value = f;
            _dtTopAlarmFrom.Checked = true;
        }
        else
        {
            _dtTopAlarmFrom.Checked = false;
        }

        if (to is DateTime t)
        {
            _dtTopAlarmTo.Value = t;
            _dtTopAlarmTo.Checked = true;
        }
        else
        {
            _dtTopAlarmTo.Checked = false;
        }
    }

    private void EnsureParkHostOnMainPage()
    {
        if (_parkHostForMainSystemPlc.Parent == _mainPage)
            return;
        _parkHostForMainSystemPlc.Dock = DockStyle.None;
        _parkHostForMainSystemPlc.Visible = false;
        EnsureControlAdded(_mainPage, _parkHostForMainSystemPlc);
    }

    private void EnsureEmbeddedCustemerForm()
    {
        if (_embeddedCustemerForm is { IsDisposed: false } existing && IsSelectedCustomerBoard(existing))
            return;

        if (_embeddedCustemerForm is not null)
        {
            DetachEmbeddedCustemerForm();
            _embeddedCustemerForm.Dispose();
            _embeddedCustemerForm = null;
        }

        _embeddedCustemerForm = CreateCustomerBoardForm();
        _embeddedCustemerForm.TopLevel = false;
        _embeddedCustemerForm.FormBorderStyle = FormBorderStyle.None;
        _embeddedCustemerForm.Dock = DockStyle.Fill;
        _embeddedCustemerForm.Show();
    }

    private void AttachEmbeddedCustemerTo(Control host)
    {
        EnsureEmbeddedCustemerForm();
        if (_embeddedCustemerForm is null)
            return;

        if (_embeddedCustemerForm.Parent != host)
        {
            _embeddedCustemerForm.Parent?.Controls.Remove(_embeddedCustemerForm);
            host.Controls.Add(_embeddedCustemerForm);
            _embeddedCustemerForm.Dock = DockStyle.Fill;
        }

        _embeddedCustemerForm.Show();
    }

    private void DetachEmbeddedCustemerForm()
    {
        if (_embeddedCustemerForm is null || _embeddedCustemerForm.IsDisposed)
            return;
        _embeddedCustemerForm.Hide();
        _embeddedCustemerForm.Parent?.Controls.Remove(_embeddedCustemerForm);
    }

    /// <summary>根据配置切换主页左侧：仅 PLC 区、仅自定义窗体、或 Tab 分页两者。</summary>
    private void ApplyMainPageDisplayMode()
    {
        if (_mainUpperDisplayHost.Parent is not TableLayoutPanel)
            return;

        int mode = Math.Clamp(_mainPageDisplayConfig.DisplayMode, 0, 2);
        _mainUpperDisplayHost.SuspendLayout();
        try
        {
            switch (mode)
            {
                case 0: // 不显示客户看板
                    DetachEmbeddedCustemerForm();
                    _tabMainPageDual.Visible = false;
                    _panelMainSystemPlc.Parent?.Controls.Remove(_panelMainSystemPlc);
                    _mainUpperDisplayHost.Controls.Clear();
                    _mainUpperDisplayHost.Controls.Add(_panelMainSystemPlc);
                    _panelMainSystemPlc.Dock = DockStyle.Fill;
                    break;

                case 1: // 仅显示客户看板
                    _panelMainSystemPlc.Parent?.Controls.Remove(_panelMainSystemPlc);
                    _parkHostForMainSystemPlc.Controls.Clear();
                    _parkHostForMainSystemPlc.Controls.Add(_panelMainSystemPlc);
                    _panelMainSystemPlc.Dock = DockStyle.Fill;
                    _mainUpperDisplayHost.Controls.Clear();
                    _mainUpperDisplayHost.Controls.Add(_panelCustemerEmbedHost);
                    _panelCustemerEmbedHost.Dock = DockStyle.Fill;
                    _tabMainPageDual.Visible = false;
                    AttachEmbeddedCustemerTo(_panelCustemerEmbedHost);
                    break;

                case 2: // 同时显示系统配置与客户看板
                    _panelMainSystemPlc.Parent?.Controls.Remove(_panelMainSystemPlc);
                    _panelDualTabSystemHost.Controls.Clear();
                    _panelDualTabSystemHost.Controls.Add(_panelMainSystemPlc);
                    _panelMainSystemPlc.Dock = DockStyle.Fill;
                    _mainUpperDisplayHost.Controls.Clear();
                    _mainUpperDisplayHost.Controls.Add(_tabMainPageDual);
                    _tabMainPageDual.Dock = DockStyle.Fill;
                    _tabMainPageDual.Visible = true;
                    AttachEmbeddedCustemerTo(_panelDualTabCustemerHost);
                    break;
            }
        }
        finally
        {
            _mainUpperDisplayHost.ResumeLayout(true);
        }
    }

    private void WireMainPageDisplayConfigTab()
    {
        if (_tabMainPageDisplayConfig.Controls.Count > 0)
            return;

        _flowMainPageDisplayConfig.Controls.Add(_lblMainPageDisplayTitle);
        _flowMainPageDisplayConfig.Controls.Add(_cmbMainPageDisplayMode);
        _flowMainPageDisplayConfig.Controls.Add(_lblCustomerBoardTitle);
        _flowMainPageDisplayConfig.Controls.Add(_cmbCustomerBoard);
        _flowMainPageDisplayConfig.Controls.Add(_lblAutoRunningAddressTitle);
        _flowMainPageDisplayConfig.Controls.Add(_txtAutoRunningAddress);
        _flowMainPageDisplayConfig.Controls.Add(_lblAutoRunningAddressHint);
        _flowMainPageDisplayConfig.Controls.Add(_lblPlcVersionAddressTitle);
        _flowMainPageDisplayConfig.Controls.Add(_txtPlcVersionAddress);
        _flowMainPageDisplayConfig.Controls.Add(_lblPlcVersionAddressHint);
        _flowMainPageDisplayConfig.Controls.Add(_lblTopAlarmRangeTitle);
        _flowMainPageDisplayConfig.Controls.Add(_panelTopAlarmRange);
        _flowMainPageDisplayConfig.Controls.Add(_lblTopAlarmRangeHint);
        _flowMainPageDisplayConfig.Controls.Add(_lblRoleDowngradeTitle);
        _flowMainPageDisplayConfig.Controls.Add(_numRoleDowngradeSeconds);
        _flowMainPageDisplayConfig.Controls.Add(_lblRoleDowngradeHint);
        _flowMainPageDisplayConfig.Controls.Add(_btnSaveMainPageDisplay);
        _flowMainPageDisplayConfig.Controls.Add(_lblMainPageDisplayHint);

        // 日期区间行：起始日期 ~ 结束日期
        EnsureControlAdded(_panelTopAlarmRange, _dtTopAlarmFrom);
        EnsureControlAdded(_panelTopAlarmRange, _lblTopAlarmRangeSeparator);
        EnsureControlAdded(_panelTopAlarmRange, _dtTopAlarmTo);
        EnsureControlAdded(_tabMainPageDisplayConfig, _flowMainPageDisplayConfig);
        SyncMainPageDisplayComboFromConfig();
        _btnSaveMainPageDisplay.Click += (_, _) =>
        {
            _mainPageDisplayConfig.DisplayMode = Math.Clamp(_cmbMainPageDisplayMode.SelectedIndex, 0, 2);
            _mainPageDisplayConfig.CustomerBoard = Math.Clamp(_cmbCustomerBoard.SelectedIndex, 0, 1);

            string before = _mainPageDisplayConfig.AutoRunningAddress;
            _mainPageDisplayConfig.AutoRunningAddress = _txtAutoRunningAddress.Text.Trim();

            string beforeVersionAddress = _mainPageDisplayConfig.PlcVersionAddress;
            _mainPageDisplayConfig.PlcVersionAddress = _txtPlcVersionAddress.Text.Trim();

            string beforeStart = _mainPageDisplayConfig.TopAlarmStartDate;
            string beforeEnd = _mainPageDisplayConfig.TopAlarmEndDate;
            ApplyTopAlarmRangeToConfig();
            bool rangeChanged = !string.Equals(beforeStart, _mainPageDisplayConfig.TopAlarmStartDate, StringComparison.Ordinal)
                             || !string.Equals(beforeEnd, _mainPageDisplayConfig.TopAlarmEndDate, StringComparison.Ordinal);

            int beforeDowngradeSeconds = RoleAutoDowngradeSeconds;
            _mainPageDisplayConfig.RoleAutoDowngradeSeconds = Math.Clamp(
                (int)_numRoleDowngradeSeconds.Value,
                MinRoleAutoDowngradeSeconds,
                MaxRoleAutoDowngradeSeconds);

            SaveMainPageDisplayConfigFile();
            ApplyTopAlarmRangeTitle();
            LoadAlarmStatsFromDatabase();
            RefreshTopAlarmLabels();
            if (rangeChanged)
                AppendLog($"报警统计 Top3 日期区间已设为 [{TopAlarmRangeText()}]，已按新区间重新统计。");
            ApplyMainPageDisplayMode();
            AppendLog($"客户看板配置已保存：主页面={_cmbMainPageDisplayMode.Text}，看板={CustomerBoardName(_mainPageDisplayConfig.CustomerBoard)}。");
            if (beforeDowngradeSeconds != RoleAutoDowngradeSeconds)
                AppendLog($"无操作自动降级时限已设为 {RoleAutoDowngradeSeconds} 秒（范围 {MinRoleAutoDowngradeSeconds}~{MaxRoleAutoDowngradeSeconds}）。");

            if (string.IsNullOrWhiteSpace(_mainPageDisplayConfig.AutoRunningAddress))
            {
                // 清空地址后立即解除限制，不等下一次轮询
                _autoRunningBlocked = false;
                _autoRunningReadErrorLogged = false;
                ApplyNavButtonEnabledState();
                SetAutoRunningUi(false, false, _mainPageDisplayConfig.AutoRunningAddress);
                if (!string.IsNullOrWhiteSpace(before))
                    AppendLog("已清除自动运行地址：「调试按钮」「工站控制」的限制解除。");
            }
            else
            {
                _autoRunningReadErrorLogged = false;
                AppendLog($"自动运行地址已设为 [{_mainPageDisplayConfig.AutoRunningAddress}]：该值为 true 时禁用「调试按钮」「工站控制」。");
            }

            if (!string.Equals(beforeVersionAddress, _mainPageDisplayConfig.PlcVersionAddress, StringComparison.Ordinal))
            {
                _plcVersionReadErrorLogged = false;
                _lastPlcVersionText = string.Empty;
                AppendLog(string.IsNullOrWhiteSpace(_mainPageDisplayConfig.PlcVersionAddress)
                    ? "已清除 PLC 版本号地址：状态栏不再显示「PLC版本号」。"
                    : $"PLC 版本号地址已设为 [{_mainPageDisplayConfig.PlcVersionAddress}]：状态栏将显示该寄存器读取的版本号。");
            }
        };
    }

    /// <summary>相机工程选项的显示名（顺序与 CameraProjectKind 枚举一致）。</summary>
    private static string CameraProjectKindName(CameraProjectKind kind) =>
        kind == CameraProjectKind.DahuaXiaoLiao ? "小料机大华工程" : "排版机海康工程";

    /// <summary>主程序启动时加载已保存的相机工程选项（原「选择相机工程」弹框的记忆文件，格式兼容）。</summary>
    private void LoadCameraProjectSelection()
    {
        _selectedCameraProject = CameraProjectPickerForm.LoadSelection();
        AppendLog($"相机工程选项已加载：{CameraProjectKindName(_selectedCameraProject)}（相机调试按钮直接打开该工程）。");
    }

    private void SaveCameraProjectSelection()
    {
        CameraProjectPickerForm.SaveSelection(_selectedCameraProject);
    }

    private void WireCameraProjectConfigTab()
    {
        if (_tabCameraProjectConfig.Controls.Count > 0)
            return;

        if (_cmbCameraProject.Items.Count == 0)
        {
            _cmbCameraProject.Items.Add(CameraProjectKindName(CameraProjectKind.HikvisionLayout));
            _cmbCameraProject.Items.Add(CameraProjectKindName(CameraProjectKind.DahuaXiaoLiao));
        }
        _cmbCameraProject.SelectedIndex = _selectedCameraProject == CameraProjectKind.DahuaXiaoLiao ? 1 : 0;

        _flowCameraProjectConfig.Controls.Add(_lblCameraProjectTitle);
        _flowCameraProjectConfig.Controls.Add(_cmbCameraProject);
        _flowCameraProjectConfig.Controls.Add(_btnSaveCameraProject);
        _flowCameraProjectConfig.Controls.Add(_lblCameraProjectHint);
        EnsureControlAdded(_tabCameraProjectConfig, _flowCameraProjectConfig);

        _btnSaveCameraProject.Click += (_, _) =>
        {
            var selected = _cmbCameraProject.SelectedIndex == 1
                ? CameraProjectKind.DahuaXiaoLiao
                : CameraProjectKind.HikvisionLayout;
            bool changed = selected != _selectedCameraProject;
            _selectedCameraProject = selected;
            SaveCameraProjectSelection();
            AppendLog($"相机工程配置已保存：{CameraProjectKindName(selected)}。"
                + (changed
                    ? "点击导航区「相机调试」时将打开新工程；如当前已打开另一工程，会先停止其后台流程再切换。"
                    : "选项未变化。"));
        };
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

    private void EnsureAlarmHistoryDatabase()
    {
        try
        {
            using var connection = new SqliteConnection(_alarmHistoryConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS alarm_history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    alarm_time TEXT NOT NULL,
                    alarm_date TEXT NOT NULL,
                    alarm_name TEXT NOT NULL,
                    alarm_code TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_alarm_history_date_time
                    ON alarm_history (alarm_date, alarm_time DESC);
                CREATE INDEX IF NOT EXISTS idx_alarm_history_code_name
                    ON alarm_history (alarm_code, alarm_name);
                CREATE TABLE IF NOT EXISTS production_history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    production_time TEXT NOT NULL,
                    production_date TEXT NOT NULL,
                    shift_name TEXT NOT NULL,
                    quantity INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_production_history_time
                    ON production_history (production_time);
                CREATE INDEX IF NOT EXISTS idx_production_history_date
                    ON production_history (production_date);
                """;
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            AppendLog($"本地数据库初始化失败: {ex.Message}");
        }
    }

    private void InsertAlarmHistoryToDatabase(AlarmHistoryRecord record)
    {
        try
        {
            using var connection = new SqliteConnection(_alarmHistoryConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO alarm_history (alarm_time, alarm_date, alarm_name, alarm_code)
                VALUES ($time, $date, $name, $code);
                """;
            command.Parameters.AddWithValue("$time", record.AlarmTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$date", record.AlarmTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$name", record.AlarmName);
            command.Parameters.AddWithValue("$code", record.AlarmCode);
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            AppendLog($"报警记录入库失败: {ex.Message}");
        }
    }

    private List<AlarmHistoryRecord> QueryAlarmHistoryFromDatabase(DateTime? fromDate, DateTime? toDate)
    {
        var result = new List<AlarmHistoryRecord>();
        try
        {
            using var connection = new SqliteConnection(_alarmHistoryConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT alarm_time, alarm_name, alarm_code
                FROM alarm_history
                WHERE ($fromDate IS NULL OR alarm_date >= $fromDate)
                  AND ($toDate IS NULL OR alarm_date <= $toDate)
                ORDER BY alarm_time DESC;
                """;
            command.Parameters.AddWithValue("$fromDate", fromDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$toDate", toDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string timeText = reader.GetString(0);
                string alarmName = reader.GetString(1);
                string alarmCode = reader.GetString(2);
                if (!DateTime.TryParseExact(timeText, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime alarmTime))
                    continue;

                result.Add(new AlarmHistoryRecord
                {
                    AlarmTime = alarmTime,
                    AlarmName = alarmName,
                    AlarmCode = alarmCode
                });
            }
        }
        catch (Exception ex)
        {
            AppendLog($"报警记录查询失败: {ex.Message}");
        }

        return result;
    }

    // 报警统计 Top3 的当前日期区间；两侧都可能为 null（不限制）。
    // 区间取自配置 MainPageDisplay.config.json；首尾都不配置时只统计当天（从当天 0 点起实时累计）。
    private (DateTime? From, DateTime? To) GetTopAlarmStatRange()
    {
        DateTime? from = TryParseConfigDate(_mainPageDisplayConfig.TopAlarmStartDate);
        DateTime? to = TryParseConfigDate(_mainPageDisplayConfig.TopAlarmEndDate);
        if (from is null && to is null)
            return (DateTime.Today, DateTime.Today);
        return (from, to);
    }

    /// <summary>配置里是否勾选了任一侧日期（区分「统计当天」与「自定义区间」的显示）。</summary>
    private bool HasTopAlarmRangeConfig() =>
        !string.IsNullOrWhiteSpace(_mainPageDisplayConfig.TopAlarmStartDate)
        || !string.IsNullOrWhiteSpace(_mainPageDisplayConfig.TopAlarmEndDate);

    private static DateTime? TryParseConfigDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime value)
            ? value
            : null;
    }

    /// <summary>当前 Top3 区间的人类可读描述（用于标题与日志）。</summary>
    private string TopAlarmRangeText()
    {
        if (!HasTopAlarmRangeConfig())
            return $"当天 {DateTime.Today:yyyy-MM-dd}";

        var (from, to) = GetTopAlarmStatRange();
        string startText = from?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "最早";
        string endText = to?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "至今";
        return $"{startText} ~ {endText}";
    }

    /// <summary>把区间写进主页面标题，方便一眼看出 Top3 统计的是哪一段时间。</summary>
    private void ApplyTopAlarmRangeTitle()
    {
        _lblTopAlarmTitle.Text = $"报警统计 Top3（{TopAlarmRangeText()}）";
    }

    /// <summary>把日期控件里勾选的值写入配置（未勾选记为空；起止倒置时自动交换）。</summary>
    private void ApplyTopAlarmRangeToConfig()
    {
        if (_dtTopAlarmFrom.Checked && _dtTopAlarmTo.Checked && _dtTopAlarmFrom.Value.Date > _dtTopAlarmTo.Value.Date)
        {
            (DateTime from, DateTime to) = (_dtTopAlarmFrom.Value, _dtTopAlarmTo.Value);
            _dtTopAlarmFrom.Value = to;
            _dtTopAlarmTo.Value = from;
            AppendLog("报警统计日期区间起止倒置，已自动交换为 [" + _dtTopAlarmFrom.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                + " ~ " + _dtTopAlarmTo.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "]。");
        }

        _mainPageDisplayConfig.TopAlarmStartDate = _dtTopAlarmFrom.Checked
            ? _dtTopAlarmFrom.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : string.Empty;
        _mainPageDisplayConfig.TopAlarmEndDate = _dtTopAlarmTo.Checked
            ? _dtTopAlarmTo.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : string.Empty;
    }

    /// <summary>跨天后重算 Top3：自定义区间要收敛到新的一天，「当天」模式必须清零重计。</summary>
    private void ReloadTopAlarmStatsOnDayChange()
    {
        DateTime today = DateTime.Today;
        if (_topAlarmStatsDay == today)
            return;

        _topAlarmStatsDay = today;
        LoadAlarmStatsFromDatabase();
        RefreshTopAlarmLabels();
        AppendLog($"日期已切换到 {today:yyyy-MM-dd}，报警统计 Top3 已按当前区间重新统计。");
    }

    /// <summary>报警发生时间是否落在当前 Top3 统计区间内（区间为空时恒为 true）。</summary>
    private bool IsAlarmInTopAlarmRange(DateTime alarmTime)
    {
        var (from, to) = GetTopAlarmStatRange();
        if (from is DateTime f && alarmTime.Date < f)
            return false;
        if (to is DateTime t && alarmTime.Date > t)
            return false;
        return true;
    }

    private void LoadAlarmStatsFromDatabase()
    {
        _alarmStats.Clear();
        var (from, to) = GetTopAlarmStatRange();
        try
        {
            using var connection = new SqliteConnection(_alarmHistoryConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT alarm_name, alarm_code, COUNT(1) AS cnt
                FROM alarm_history
                WHERE ($fromDate IS NULL OR alarm_date >= $fromDate)
                  AND ($toDate IS NULL OR alarm_date <= $toDate)
                GROUP BY alarm_name, alarm_code;
                """;
            command.Parameters.AddWithValue("$fromDate", from?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$toDate", to?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string alarmName = reader.GetString(0);
                string alarmCode = reader.GetString(1);
                int count = reader.GetInt32(2);
                string key = $"{alarmCode}|{alarmName}";
                _alarmStats[key] = (alarmName, alarmCode, count);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"报警统计加载失败: {ex.Message}");
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
        StatusStripStyle.SetText(_lblPlcStatus, "PLC: 连接中...", StatusStripStyle.WarnColor);

        try
        {
            DisconnectPlcDevices();
            _plcDeviceConfiguration.Normalize();
            foreach (PlcDeviceProfile device in _plcDeviceConfiguration.Devices)
            {
                var communicator = new global::PlcConectCom.PlcCommunicator(device.IpAddress, device.Port, device.SlaveId);
                try
                {
                    await communicator.ConnectAsync();
                    if (!string.IsNullOrWhiteSpace(device.HeartbeatBoolAddress))
                    {
                        await communicator.WriteBoolAsync(device.HeartbeatBoolAddress, true);
                        await Task.Delay(1000).ConfigureAwait(true);
                        await communicator.WriteBoolAsync(device.HeartbeatBoolAddress, true);
                    }
                    _plcDevices[device.Id] = communicator;
                    AppendLog($"PLC[{device.Name}] 已连接 {device.IpAddress}:{device.Port} 站号:{device.SlaveId}");
                }
                catch (Exception deviceException)
                {
                    communicator.Disconnect();
                    AppendLog($"PLC[{device.Name}] 连接失败: {deviceException.GetBaseException().Message}");
                }
            }

            _plc = GetMainPlc();
            PlcDeviceProfile? mainDevice = _plcDeviceConfiguration.Devices.FirstOrDefault();
            if (_plc is null || mainDevice is null)
                throw new InvalidOperationException("主界面与报警绑定的 PLC 未连接，请检查设备配置和网络。");

            _mainPlcConfig.IpAddress = mainDevice.IpAddress;
            _mainPlcConfig.Port = mainDevice.Port;
            _mainPlcConfig.SlaveId = mainDevice.SlaveId;
            _mainPlcConfig.HeartbeatBoolAddress = mainDevice.HeartbeatBoolAddress;
            SaveMainPlcConfig();

            bool probeOk = await ProbePlcConnectionAsync();
            if (!probeOk)
                throw new Exception("PLC通讯探测失败，可能网线未连接或目标PLC不可达。");

            SetStatusUi(true);
            try
            {
                if (_debugToolsPlcExclusiveActive)
                {
                    await _axisMotionControl.RefreshAllAxisDataAsync();
                    await _positionParameterTableControl.RefreshDataAsync();
                    await _boolGroupControlTable.RefreshStatusAsync();
                    await RefreshMainButtonDisplayAsync();
                }
                else
                {
                    await _boolSignalControl.RefreshSignalsAsync();
                    _parameterTableControl.SuppressChangeJournal = true;
                    _hmiDisplayParameterTableControl.SuppressChangeJournal = true;
                    _buttonVarParameterTableControl.SuppressChangeJournal = true;
                    try
                    {
                        await _parameterTableControl.ReadAllAsync();
                        await _hmiDisplayParameterTableControl.ReadAllAsync();
                        await _buttonVarParameterTableControl.ReadAllAsync();
                        await _processRealtimeDataConfigControl.ReadAllAsync();
                    }
                    finally
                    {
                        _parameterTableControl.SuppressChangeJournal = false;
                        _hmiDisplayParameterTableControl.SuppressChangeJournal = false;
                        _buttonVarParameterTableControl.SuppressChangeJournal = false;
                    }

                    await _axisMotionControl.RefreshAllAxisDataAsync();
                    await _positionParameterTableControl.RefreshDataAsync();
                    await _boolGroupControlTable.RefreshStatusAsync();
                    _alarmAddressManagerControl.StartAutoMonitor();
                    _alarmPopupManagerControl.StartAutoMonitor();
                    _manualOperationPromptConfigControl.StartAutoMonitor();
                    StartMainProcessRealtimeDisplays();
                    await RefreshMainButtonDisplayAsync();
                    await RefreshRealtimeBoolLabelsAsync();
                }
            }
            catch (Exception refreshException)
            {
                AppendLog($"PLC已连接，但一个或多个功能区刷新失败: {refreshException.GetBaseException().Message}");
            }

            _disconnectAlreadyCounted = false;
            _lastProductionCounterSignal = null;
            await RefreshStatusBarAsync();
            AppendLog($"连接成功 {_mainPlcConfig.IpAddress}:{_mainPlcConfig.Port} 站号:{_mainPlcConfig.SlaveId}");
        }
        catch (Exception ex)
        {
            _plc?.Disconnect();
            _failedReconnectCount++;
            SetStatusUi(false);
            _lastProductionCounterSignal = null;
            AppendLog($"连接失败: {ex.Message}");
        }
        finally
        {
            _isConnecting = false;
        }
    }

    private void RebuildMainProcessRealtimeDisplays()
    {
        if (_mainRealtimePanel.IsDisposed || _mainProcessRealtimeFlow.IsDisposed)
            return;

        foreach (ProcessRealtimeValueDisplayControl old in _mainProcessRealtimeFlow.Controls.OfType<ProcessRealtimeValueDisplayControl>().ToList())
        {
            _mainProcessRealtimeFlow.Controls.Remove(old);
            old.Dispose();
        }

        IReadOnlyList<ProcessRealtimeDataItem> items = ProcessRealtimeDataConfigControl.LoadConfiguredItems();
        bool plcOk = _plc is { IsConnected: true } && !_debugToolsPlcExclusiveActive;
        foreach (ProcessRealtimeDataItem item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Address))
                continue;

            var ctrl = new ProcessRealtimeValueDisplayControl();
            ctrl.ApplyItem(item);
            ctrl.ApplyUiScale(_mainRightUiScale);
            ctrl.PlcProvider = ProvidePlcForMainUi;
            ctrl.LogAction = AppendLog;
            ctrl.AutoReadEnabled = plcOk;
            _mainProcessRealtimeFlow.Controls.Add(ctrl);
        }

        EnsureControlAdded(_mainProcessRealtimeScrollHost, _mainProcessRealtimeFlow);
        EnsureControlAdded(_mainButtonDisplaySection, _mainButtonDisplayPanel);
        EnsureControlAdded(_mainButtonDisplaySection, _lblMainButtonDisplayTitle);
        EnsureControlAdded(_mainRealtimePanel, _mainButtonDisplaySection);
        EnsureControlAdded(_mainRealtimePanel, _mainProcessRealtimeScrollHost);
    }

    private void StartMainProcessRealtimeDisplays()
    {
        foreach (var control in _mainProcessRealtimeFlow.Controls.OfType<ProcessRealtimeValueDisplayControl>())
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
                PlcProvider = ProvidePlcForStationUi,
                LogAction = AppendLog,
                StationName = cfg.StationName,
                StationEnabled = cfg.StationEnabled,
                StationEnabledAddress = cfg.StationEnabledAddress,
                StartCommandAddress = cfg.StartCommandAddress,
                PauseCommandAddress = cfg.PauseCommandAddress,
                StopCommandAddress = cfg.StopCommandAddress,
                ResetCommandAddress = cfg.ResetCommandAddress,
                AutoSignalAddress = cfg.AutoSignalAddress,
                PauseSignalAddress = cfg.PauseSignalAddress,
                InitSignalAddress = cfg.InitSignalAddress,
                InitDoneSignalAddress = cfg.InitDoneSignalAddress,
                StationAlarmAddress = cfg.StationAlarmAddress,
                InitTimeoutAlarmAddress = cfg.InitTimeoutAlarmAddress,
                InitSafetyConditionAddress = cfg.InitSafetyConditionAddress,
                InitSafetyNotMetAlarmAddress = cfg.InitSafetyNotMetAlarmAddress,
                SingleCycleTimeAddress = cfg.SingleCycleTimeAddress,
                FlowStatusIdAddress = cfg.FlowStatusIdAddress,
                FlowStatusIdValueType = cfg.FlowStatusIdValueType,
                AutoRefreshEnabled = true,
                Margin = new Padding(6)
            };

            try
            {
                if (!string.IsNullOrWhiteSpace(cfg.FlowStatusMapFilePath) && File.Exists(cfg.FlowStatusMapFilePath))
                    control.LoadFlowStatusMapFromExcel(cfg.FlowStatusMapFilePath, cfg.StationName);
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

        int outerMargin = (int)Math.Round(6 * _mainRightUiScale);
        foreach (ButtonDisplayConfigItem cfg in configurations)
        {
            if (cfg.ControlType == ButtonDisplayControlType.PLC开关)
            {
                var sw = new PlcBoolSwitchControl
                {
                    Width = (int)Math.Round(220 * _mainRightUiScale),
                    Margin = new Padding(outerMargin),
                    Title = cfg.DisplayName,
                    PlcAddress = cfg.PlcAddress,
                    ConditionPlcAddress = cfg.ConditionPlcAddress ?? string.Empty,
                    ConditionWhen = cfg.ConditionWhen,
                    PlcProvider = ProvidePlcForMainUi,
                    LogAction = AppendLog
                };
                sw.ApplyUiScale(_mainRightUiScale);
                _mainBoolSwitchControls.Add(sw);
                _mainButtonDisplayPanel.Controls.Add(sw);
                continue;
            }

            var button = new PlcBoundButton
            {
                Margin = new Padding(outerMargin),
                PlcProvider = ProvidePlcForMainUi,
                LogAction = AppendLog
            };
            button.ApplyBinding(cfg.PlcAddress, cfg.ValueType, cfg.StringLength);
            button.ApplyUiScale(_mainRightUiScale);
            button.Text = cfg.DisplayName;
            button.AutoReset = cfg.AutoReset;
            _mainBoundButtons.Add(button);
            _mainButtonDisplayPanel.Controls.Add(button);
        }

        _mainButtonDisplayPanel.ResumeLayout();
        _ = RefreshMainButtonDisplayAsync();
    }

    /// <summary>横 向按钮配置变更 → 重建主页面「PLC 按钮显示区」。</summary>
    private void HandleHorizontalButtonConfigurationsChanged(IReadOnlyList<HorizontalButtonConfigItem> configurations)
    {
        RebuildMainPlcHorizontalButtons(configurations);
    }

    /// <summary>
    /// 按「横向按钮配置」重建 PLC 显示区内的按钮（左到右横向排列）。
    /// BOOL 类型走 PlcBoundButton（可点击置位/点动），其余类型仅作只读值显示。
    /// </summary>
    private void RebuildMainPlcHorizontalButtons(IReadOnlyList<HorizontalButtonConfigItem> configurations)
    {
        _mainPlcButtonControls.Clear();
        _mainPlcButtonPanel.SuspendLayout();
        _mainPlcButtonPanel.Controls.Clear();

        foreach (HorizontalButtonConfigItem cfg in configurations)
        {
            if (string.IsNullOrWhiteSpace(cfg.PlcAddress))
                continue;

            var button = new PlcBoundButton
            {
                Margin = new Padding(0, 0, 0, 6),
                AutoReset = cfg.AutoReset,
                ReadBackState = cfg.ReadBackState,
                PlcProvider = ProvidePlcForMainUi,
                LogAction = AppendLog,
                // BOOL 类型按值切换名称；留空则沿用「显示名称」
                TrueText = cfg.TrueText ?? string.Empty,
                FalseText = cfg.FalseText ?? string.Empty
            };
            // 与状态显示区 PlcBoolLabe 同尺寸（低分辨率下按系数同步缩小）
            ApplyMainLeftButtonScale(button);
            button.ApplyBinding(cfg.PlcAddress.Trim(), cfg.ValueType, Math.Max(1, cfg.StringLength));
            button.Text = string.IsNullOrWhiteSpace(cfg.DisplayName) ? cfg.PlcAddress.Trim() : cfg.DisplayName.Trim();
            _mainPlcButtonControls.Add(button);
            _mainPlcButtonPanel.Controls.Add(button);
        }

        _mainPlcButtonPanel.ResumeLayout();
        // 重建后立即刷一次，避免停留在空白值
        _childButtonsRefreshPending = true;
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
                PlcProvider = ProvidePlcForMainUi,
                LogAction = AppendLog
            };
            ApplyMainLeftBoolLabelScale(control);
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
                Margin = new Padding(5),
                Title = string.IsNullOrWhiteSpace(cfg.DisplayName) ? cfg.PlcAddress.Trim() : cfg.DisplayName.Trim(),
                PlcAddress = cfg.PlcAddress.Trim(),
                DataType = cfg.DataType,
                ControlMode = cfg.ControlMode,
                StringLength = Math.Max(1, cfg.StringLength),
                UseIntComboMapping = cfg.UseIntComboMapping,
                IntValueMappingDescription = cfg.IntValueMappingDescription ?? string.Empty,
                DisplayOffsetY = 0, // 主页面流式布局内不偏移，与按钮行对齐
                PlcProvider = ProvidePlcForMainUi,
                LogAction = AppendLog
            };
            ApplyMainLeftValueTextBoxStyle(control); // 与「功能按钮」控件同尺寸/同字体/同扁平风格
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
            // 调试独占模式下主界面按钮仍使用 ProvidePlcForMainUi（null）；此处临时改为直连 PLC 以便调试页同步显示。
            if (_debugToolsPlcExclusiveActive)
            {
                foreach (PlcBoolSwitchControl sw in _mainBoolSwitchControls)
                    sw.PlcProvider = ProvidePlcForDebugToolsPage;
                foreach (PlcBoundButton btn in _mainBoundButtons)
                    btn.PlcProvider = ProvidePlcForDebugToolsPage;
                foreach (PlcBoundButton btn in _mainPlcButtonControls)
                    btn.PlcProvider = ProvidePlcForDebugToolsPage;
            }

            try
            {
                foreach (PlcBoolSwitchControl sw in _mainBoolSwitchControls)
                    await sw.RefreshStateAsync();

                foreach (PlcBoundButton btn in _mainBoundButtons)
                    await btn.ShowDataAsync();

                // PLC 显示区内的横向按钮（来自「横向按钮配置」）
                foreach (PlcBoundButton btn in _mainPlcButtonControls)
                    await btn.ShowDataAsync();
            }
            finally
            {
                if (_debugToolsPlcExclusiveActive)
                {
                    foreach (PlcBoolSwitchControl sw in _mainBoolSwitchControls)
                        sw.PlcProvider = ProvidePlcForMainUi;
                    foreach (PlcBoundButton btn in _mainBoundButtons)
                        btn.PlcProvider = ProvidePlcForMainUi;
                    foreach (PlcBoundButton btn in _mainPlcButtonControls)
                        btn.PlcProvider = ProvidePlcForMainUi;
                }
            }
        }
        finally
        {
            _refreshingMainButtons = false;
        }
    }

    /// <summary>
    /// 刷新从机（子 PLC）主页按钮/开关的连接态显示。由 <see cref="_statusTimer"/> 每秒驱动：
    /// 仅在任一子 PLC 探测状态翻转或按钮区重建后执行一次，平时无 PLC 读写开销。
    /// 修复：启动时子 PLC 尚未连接导致按钮停留「未连接」，必须手动「重载」按钮显示配置才恢复的问题。
    /// </summary>
    private async Task RefreshChildMainButtonDisplayAsync()
    {
        if (!_childButtonsRefreshPending)
            return;

        _childButtonsRefreshPending = false;
        if (_childBoundButtons.Count == 0 && _childBoolSwitchControls.Count == 0)
            return;

        try
        {
            foreach (PlcBoolSwitchControl sw in _childBoolSwitchControls)
                await sw.RefreshStateAsync();
            foreach (PlcBoundButton btn in _childBoundButtons)
                await btn.ShowDataAsync();
        }
        catch (Exception ex)
        {
            AppendLog($"从机按钮状态刷新失败: {ex.GetBaseException().Message}");
        }
    }

    private void WireStationAreaMouseWheelRecursive(Control root)
    {
        root.MouseWheel += OnStationAreaDescendantMouseWheel;
        foreach (Control child in root.Controls)
            WireStationAreaMouseWheelRecursive(child);
    }

    private void OnStationAreaDescendantMouseWheel(object? sender, MouseEventArgs e)
    {
        ScrollStationAreaByWheelDelta(e.Delta);
    }

    private void ScrollStationAreaByWheelDelta(int delta)
    {
        if (delta == 0)
            return;

        var vs = _stationAreaPanel.VerticalScroll;
        if (!vs.Visible)
            return;

        int step = Math.Max(32, Math.Abs(delta) / 4);
        int nv = vs.Value - Math.Sign(delta) * step;
        nv = Math.Clamp(nv, vs.Minimum, vs.Maximum);
        vs.Value = nv;
    }

    // 按可用宽度换行，避免为了固定列数裁掉卡片内部按钮和状态灯。
    private void ArrangeStationControls()
    {
        ArrangeStationCards(_stationDisplayPanel);
    }

    private static void ArrangeStationCards(FlowLayoutPanel host)
    {
        if (host.IsDisposed || host.ClientSize.Width <= 0) return;
        const int spacing = 12;
        const int desiredMinimumWidth = 280;
        int availableWidth = Math.Max(desiredMinimumWidth + spacing,
            host.ClientSize.Width - host.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
        int columns = Math.Clamp(availableWidth / (desiredMinimumWidth + spacing), 1, StationDisplayColumns);
        int cardWidth = Math.Max(260, availableWidth / columns - spacing);
        host.SuspendLayout();
        try
        {
            foreach (var control in host.Controls.OfType<StationOperationStatusControl>())
            {
                control.Size = new Size(cardWidth, 310);
                control.Margin = new Padding(spacing / 2);
            }
        }
        finally
        {
            host.ResumeLayout();
        }
    }

    /// <summary>
    /// 写一条运行日志：全量写入 SQLite（日志查询可查），界面日志框只保留最近
    /// <see cref="MaxLiveLogLines"/> 行，并且按批刷新，避免长期运行后 TextBox 无限膨胀卡死界面。
    /// 本方法可从任意线程调用。
    /// </summary>
    private void AppendLog(string message)
    {
        ApplicationLogStore.Append(message);

        // 作业员权限下日志框隐藏：只入库，不再往 native Edit 控件里堆文本
        if (!_liveLogBoxVisible)
            return;

        string line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        lock (_liveLogSync)
            _liveLogPending.Enqueue(line);

        ScheduleLiveLogFlush();
    }

    /// <summary>
    /// 合并待显示日志为一次 UI 刷新。旧实现每条日志都 BeginInvoke 一个委托，
    /// 后台（相机/MES）高频日志会把消息队列塞满，最小化再恢复时积压一次性执行会明显卡顿。
    /// </summary>
    private void ScheduleLiveLogFlush()
    {
        if (IsDisposed || Disposing || !IsHandleCreated)
            return;

        if (Interlocked.Exchange(ref _liveLogFlushScheduled, 1) != 0)
            return;

        try
        {
            if (InvokeRequired)
                BeginInvoke(new Action(FlushLiveLog));
            else
                FlushLiveLog();
        }
        catch
        {
            Interlocked.Exchange(ref _liveLogFlushScheduled, 0);
        }
    }

    /// <summary>窗体首次显示后补刷一次启动阶段攒下的日志（那时句柄尚未创建）。</summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ScheduleLiveLogFlush();
    }

    private void FlushLiveLog()
    {
        Interlocked.Exchange(ref _liveLogFlushScheduled, 0);

        if (IsDisposed || Disposing || _txtLog.IsDisposed || !_txtLog.Visible)
        {
            lock (_liveLogSync)
                _liveLogPending.Clear();
            return;
        }

        string[] lines;
        lock (_liveLogSync)
        {
            if (_liveLogPending.Count == 0)
                return;
            lines = _liveLogPending.ToArray();
            _liveLogPending.Clear();
        }

        // 一整批只做一次 AppendText，减少 Edit 控件的文本重排次数
        _txtLog.AppendText(string.Concat(lines));
        foreach (string line in lines)
            _liveLogShown.Enqueue(line);

        // 超过上限时一次裁掉最旧的一批；全量历史仍在 Logs/AppLog_yyyy-MM-dd.db
        if (_liveLogShown.Count > MaxLiveLogLines + LiveLogTrimBatchLines)
        {
            int chars = 0;
            for (int i = 0; i < LiveLogTrimBatchLines; i++)
                chars += _liveLogShown.Dequeue().Length;

            _txtLog.Select(0, chars);
            // ReadOnly 只对用户生效，但不同版本 Edit 控件对代码删文本的行为不一致，临时放开更保险
            _txtLog.ReadOnly = false;
            try
            {
                _txtLog.SelectedText = string.Empty;
            }
            finally
            {
                _txtLog.ReadOnly = true;
            }
            _txtLog.SelectionStart = _txtLog.TextLength;
            _txtLog.ScrollToCaret();
        }
    }

    private void HandleCameraUiLogMessage(string message)
    {
        AppendLog($"[TH_CamSet] {message}");
    }

    private global::PlcConectCom.PlcCommunicator? TryGetConnectedCameraPlc()
    {
        var plc = ProvidePlcForCameraUi();
        if (plc is { IsConnected: true })
            return plc;

        lock (_cameraBridgeLogSync)
        {
            DateTime now = DateTime.Now;
            if ((now - _lastCameraBridgeOfflineLogAt).TotalSeconds >= 3)
            {
                _lastCameraBridgeOfflineLogAt = now;
                AppendLog("相机侧PLC桥接提示：相机绑定的PLC未连接，当前相机读写按默认值/忽略处理。");
            }
        }

        return null;
    }

    private bool ReadPlcBoolFromMain(string address)
    {
        var plc = TryGetConnectedCameraPlc();
        if (plc is null)
            return false;

        try
        {
            return plc.ReadBoolAsync(address).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            LogCameraBridgeException("读取BOOL", address, ex);
            return false;
        }
    }

    private void WritePlcBoolFromMain(string address, bool value)
    {
        var plc = TryGetConnectedCameraPlc();
        if (plc is null)
            return;

        try
        {
            plc.WriteBoolAsync(address, value).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            LogCameraBridgeException("写入BOOL", address, ex);
        }
    }

    private ushort ReadPlcWordFromMain(string address)
    {
        var plc = TryGetConnectedCameraPlc();
        if (plc is null)
            return 0;

        try
        {
            return (ushort)plc.ReadIntAsync(address).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            LogCameraBridgeException("读取WORD", address, ex);
            return 0;
        }
    }

    private void WritePlcWordFromMain(string address, ushort value)
    {
        var plc = TryGetConnectedCameraPlc();
        if (plc is null)
            return;

        try
        {
            plc.WriteIntAsync(address, (short)value).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            LogCameraBridgeException("写入WORD", address, ex);
        }
    }

    private float ReadPlcFloatFromMain(string address)
    {
        var plc = TryGetConnectedCameraPlc();
        if (plc is null)
            return 0f;

        try
        {
            return (float)plc.ReadDoubleAsync(address).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            LogCameraBridgeException("读取FLOAT", address, ex);
            return 0f;
        }
    }

    private void WritePlcFloatFromMain(string address, float value)
    {
        var plc = TryGetConnectedCameraPlc();
        if (plc is null)
            return;

        try
        {
            plc.WriteDoubleAsync(address, value).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            LogCameraBridgeException("写入FLOAT", address, ex);
        }
    }

    private void LogCameraBridgeException(string operation, string address, Exception ex)
    {
        lock (_cameraBridgeLogSync)
        {
            DateTime now = DateTime.Now;
            if ((now - _lastCameraBridgeExceptionLogAt).TotalSeconds < 3)
                return;

            _lastCameraBridgeExceptionLogAt = now;
        }

        AppendLog($"相机侧PLC桥接{operation}失败 [{address}]: {ex.GetBaseException().Message}");
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
            if (_debugToolsPlcExclusiveActive || _plc is null || !_plc.IsConnected)
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

    private async Task RefreshMonitorSignalsIfVisibleAsync()
    {
        bool monitorVisible = (_monitorPageWindow?.Visible ?? false) || _boolSignalControl.Visible;
        if (!monitorVisible || _refreshingMonitorSignals || _debugToolsPlcExclusiveActive)
            return;

        _refreshingMonitorSignals = true;
        try
        {
            await _boolSignalControl.RefreshSignalsAsync();
        }
        catch (Exception ex)
        {
            AppendLog($"状态监控刷新失败: {ex.Message}");
        }
        finally
        {
            _refreshingMonitorSignals = false;
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
            // 自愈：连接流程里 StartAutoMonitor 排在各功能区刷新之后，若其中某步抛异常会漏启动，
            // 报警监控随之失活。这里每秒兜底确保监控跟随连接状态（StartAutoMonitor 幂等）。
            if (connected && !_debugToolsPlcExclusiveActive)
            {
                _alarmAddressManagerControl.StartAutoMonitor();
                _alarmPopupManagerControl.StartAutoMonitor();
                _manualOperationPromptConfigControl.StartAutoMonitor();
            }
            await ProbeSubPlcHeartbeatsAsync();
            await RefreshChildMainButtonDisplayAsync();
            await ProbeAutoRunningAsync();
            await ProbePlcVersionAsync();
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
                ? DefaultHeartbeatAddress
                : _mainPlcConfig.HeartbeatBoolAddress.Trim();

            bool heartbeatValue = await _plc.ReadBoolAsync(probeAddress);
            // 低侵入探测：优先只读；当位为 false 时再做一次写回以维持心跳。
            if (!heartbeatValue)
                await _plc.WriteBoolAsync(probeAddress, true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 对主 PLC 之外的每台设备持续维持心跳。由 <see cref="_statusTimer"/> 每秒驱动，
    /// 新增子 PLC 后无需额外登记，遍历设备配置即可自动纳入。
    /// </summary>
    private async Task ProbeSubPlcHeartbeatsAsync()
    {
        var devices = _plcDeviceConfiguration?.Devices;
        // 第 0 台是主 PLC，由 ProbePlcConnectionAsync 负责，这里只处理其余设备
        if (devices is not null)
        {
            for (int i = 1; i < devices.Count; i++)
            {
                PlcDeviceProfile device = devices[i];
                if (device is null || string.IsNullOrWhiteSpace(device.Id))
                    continue;

                bool ok = await ProbeSingleSubPlcHeartbeatAsync(device);
                // 从机主页按钮的连接态刷新：探测结果与上次不同（任一方向翻转）时标记待刷新
                if (_childLastProbeOk.TryGetValue(device.Id, out bool lastOk) && lastOk != ok)
                    _childButtonsRefreshPending = true;
                _childLastProbeOk[device.Id] = ok;
                bool wasFailed = _subHeartbeatFailed.Contains(device.Id);

                // 心跳正常时兜底启动该子 PLC 的报警/弹窗监控（幂等），保证主页实时报警能看到子 PLC 报警
                if (ok)
                {
                    ChildPlcModule? alarmModule = _childPlcModules.FirstOrDefault(m =>
                        string.Equals(m.Device.Id, device.Id, StringComparison.OrdinalIgnoreCase));
                    if (alarmModule is not null)
                    {
                        alarmModule.AlarmConfig.StartAutoMonitor();
                        alarmModule.AlarmPopupConfig.StartAutoMonitor();
                    }
                }

                // 按设备去重，只在状态翻转时记一次日志，避免每秒刷屏
                if (ok && wasFailed)
                {
                    _subHeartbeatFailed.Remove(device.Id);
                    AppendLog($"PLC[{device.Name}] 心跳已恢复");
                }
                else if (!ok && !wasFailed)
                {
                    _subHeartbeatFailed.Add(device.Id);
                    AppendLog($"PLC[{device.Name}] 心跳异常，将在后续读写时自动重连");
                }
            }
        }

        UpdateSubHeartbeatUi();
    }

    /// <summary>
    /// 对单台子 PLC 做一次心跳。配置了心跳地址时读并在为 false 时写回 true；
    /// 未配置时只读回退地址做连通性探测，不写入，避免误动作未知地址。
    /// </summary>
    private async Task<bool> ProbeSingleSubPlcHeartbeatAsync(PlcDeviceProfile device)
    {
        if (!_plcDevices.TryGetValue(device.Id, out global::PlcConectCom.PlcCommunicator? plc) || plc is null)
            return false;

        bool configured = !string.IsNullOrWhiteSpace(device.HeartbeatBoolAddress);
        string address = configured
            ? device.HeartbeatBoolAddress.Trim()
            : DefaultHeartbeatAddress;

        try
        {
            bool value = await plc.ReadBoolAsync(address);
            if (!value && configured)
                await plc.WriteBoolAsync(address, true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void UpdateSubHeartbeatUi()
    {
        var devices = _plcDeviceConfiguration?.Devices;
        int subCount = devices is null ? 0 : Math.Max(0, devices.Count - 1);

        if (subCount == 0)
        {
            StatusStripStyle.SetText(_lblSubHeartbeat, "子PLC心跳: 未启用", StatusStripStyle.MutedColor);
            return;
        }

        if (_subHeartbeatFailed.Count == 0)
        {
            // 与主 PLC 心跳一致的闪烁样式：绿点位置固定、只让颜色明暗交替，
            // 文本宽度不变，后面几项不会因为每秒重排而左右抖动
            _subHeartbeatFlash = !_subHeartbeatFlash;
            StatusStripStyle.SetText(
                _lblSubHeartbeat,
                "子PLC心跳: 正常 ●",
                _subHeartbeatFlash ? StatusStripStyle.OkColor : StatusStripStyle.OkPulseColor);
            return;
        }

        // 探测不到心跳（读写失败或未连上）：亮红色，与正常态的绿色形成强对比
        StatusStripStyle.SetText(
            _lblSubHeartbeat,
            $"子PLC心跳: 异常 {_subHeartbeatFailed.Count}/{subCount}",
            StatusStripStyle.ErrorColor);
    }

    /// <summary>
    /// 每秒读取「自动运行中的 PLC 地址」。该位为 true 时禁用主页面导航区的
    /// 「调试按钮」与「工站控制」，避免自动运行过程中被手动干预。
    /// 通讯中断或读取失败时保持上一次状态，防止误放行（读取失败只记一次日志）。
    /// </summary>
    private async Task ProbeAutoRunningAsync()
    {
        string address = _mainPageDisplayConfig.AutoRunningAddress?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(address))
        {
            _autoRunningReadErrorLogged = false;
            if (_autoRunningBlocked)
            {
                _autoRunningBlocked = false;
                ApplyNavButtonEnabledState();
            }
            SetAutoRunningUi(false, false, address);
            return;
        }

        if (_plc is not { IsConnected: true })
        {
            SetAutoRunningUi(_autoRunningBlocked, true, address);
            return;
        }

        try
        {
            bool running = await _plc.ReadBoolAsync(address);
            _autoRunningReadErrorLogged = false;

            if (running != _autoRunningBlocked)
            {
                _autoRunningBlocked = running;
                ApplyNavButtonEnabledState();

                if (running)
                {
                    AppendLog($"自动运行信号[{address}]为 ON：已禁用「调试按钮」「工站控制」。");
                    if (_stationControlPopupForm is { IsDisposed: false })
                        AppendLog("自动运行中：工站控制窗口仍处于打开状态，请谨慎操作。");
                }
                else
                {
                    AppendLog($"自动运行信号[{address}]为 OFF：已恢复「调试按钮」「工站控制」。");
                }
            }

            SetAutoRunningUi(running, false, address);
        }
        catch (Exception ex)
        {
            SetAutoRunningUi(_autoRunningBlocked, true, address);
            if (!_autoRunningReadErrorLogged)
            {
                _autoRunningReadErrorLogged = true;
                AppendLog($"读取自动运行地址[{address}]失败: {ex.Message}（维持当前限制状态）");
            }
        }
    }

    private void SetAutoRunningUi(bool running, bool commError, string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            StatusStripStyle.SetText(_lblAutoRunning, "自动运行: 未配置", StatusStripStyle.MutedColor);
            return;
        }

        string state = running ? "是" : "否";
        if (commError)
        {
            StatusStripStyle.SetText(_lblAutoRunning, $"自动运行: {state}(读取失败)", StatusStripStyle.WarnColor);
            return;
        }

        StatusStripStyle.SetText(
            _lblAutoRunning,
            $"自动运行: {state} [{address}]",
            running ? StatusStripStyle.WarnColor : StatusStripStyle.OkColor);
    }

    /// <summary>「调试按钮」「工站控制」受角色权限与自动运行信号双重限制。</summary>
    private void ApplyNavButtonEnabledState()
    {
        bool allow = _currentRole != UserRole.作业员 && !_autoRunningBlocked;
        _btnPageDebugTools.Enabled = allow;
        _btnPageStationControl.Enabled = allow;
    }

    /// <summary>
    /// 每秒读取「PLC 版本号地址」（MW 起始的字地址，按 ASCII 字符串读取），显示在状态栏。
    /// 地址留空显示「未配置」；PLC 断开或读取失败时显示「读取失败」并只记一次日志。
    /// </summary>
    private async Task ProbePlcVersionAsync()
    {
        string address = _mainPageDisplayConfig.PlcVersionAddress?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(address))
        {
            _plcVersionReadErrorLogged = false;
            SetPlcVersionUi(null, address, commError: false);
            return;
        }

        if (_plc is not { IsConnected: true })
        {
            SetPlcVersionUi(null, address, commError: true);
            return;
        }

        try
        {
            var (type, _, _) = AddressParser.ParseAddress(address);
            if (type is not ("MW" or "MD"))
            {
                // 版本号是字符串，只能存在 MW/MD 数据区；MX 位地址视为配置错误
                SetPlcVersionUi(null, address, commError: false, invalidAddress: true);
                return;
            }

            string versionText = (await _plc.ReadStringAsync(address, PlcVersionMaxChars)).Trim();
            _plcVersionReadErrorLogged = false;
            SetPlcVersionUi(string.IsNullOrEmpty(versionText) ? "(空)" : versionText, address, commError: false);
        }
        catch (Exception ex)
        {
            SetPlcVersionUi(null, address, commError: true);
            if (!_plcVersionReadErrorLogged)
            {
                _plcVersionReadErrorLogged = true;
                AppendLog($"读取PLC版本号地址[{address}]失败: {ex.Message}");
            }
        }
    }

    private void SetPlcVersionUi(string? version, string address, bool commError, bool invalidAddress = false)
    {
        // 值与显示都没变就不重设状态栏，避免每秒无谓的布局抖动
        string cacheKey = $"{version}|{address}|{commError}|{invalidAddress}";
        if (cacheKey == _lastPlcVersionText)
            return;
        _lastPlcVersionText = cacheKey;

        if (string.IsNullOrWhiteSpace(address))
        {
            StatusStripStyle.SetText(_lblPlcVersion, "PLC版本号: 未配置", StatusStripStyle.MutedColor);
            return;
        }

        if (invalidAddress)
        {
            StatusStripStyle.SetText(_lblPlcVersion, $"PLC版本号: 地址无效[{address}]", StatusStripStyle.WarnColor);
            return;
        }

        if (commError)
        {
            StatusStripStyle.SetText(_lblPlcVersion, "PLC版本号: 读取失败", StatusStripStyle.WarnColor);
            return;
        }

        StatusStripStyle.SetText(_lblPlcVersion, $"PLC版本号: {version}", StatusStripStyle.OkColor);
    }

    private void SetStatusUi(bool connected)
    {

        if (connected)
        {
            StatusStripStyle.SetText(_lblPlcStatus, "PLC: 连接成功", StatusStripStyle.OkColor);
            _disconnectAlreadyCounted = false;
            _heartbeatFlash = !_heartbeatFlash;
            StatusStripStyle.SetText(
                _lblHeartbeat,
                "心跳: 正常 ●",
                _heartbeatFlash ? StatusStripStyle.OkColor : StatusStripStyle.OkPulseColor);
        }
        else
        {
            StatusStripStyle.SetText(_lblPlcStatus, "PLC: 连接中断", StatusStripStyle.ErrorColor);

            if (_lastConnectedState && !_disconnectAlreadyCounted)
            {
                _failedReconnectCount++;
                _disconnectAlreadyCounted = true;
            }

            // 探测不到主 PLC 心跳：亮红色，与子 PLC 心跳异常态保持一致
            StatusStripStyle.SetText(_lblHeartbeat, "心跳: 异常", StatusStripStyle.ErrorColor);
        }

        StatusStripStyle.SetText(
            _lblReconnectFail,
            $"失败重连次数: {_failedReconnectCount}",
            _failedReconnectCount > 0 ? StatusStripStyle.WarnColor : StatusStripStyle.TextColor);
        _lastConnectedState = connected;
    }

    private void HandleAlarmTriggered(AlarmTriggeredEvent alarm, string? deviceName = null)
    {
        // 子 PLC 报警带 [设备名] 前缀，避免与主 PLC 同名报警混淆
        string displayName = string.IsNullOrWhiteSpace(deviceName)
            ? alarm.AlarmName
            : $"[{deviceName}] {alarm.AlarmName}";

        var record = new AlarmHistoryRecord
        {
            AlarmTime = alarm.AlarmTime,
            AlarmName = displayName,
            AlarmCode = alarm.AlarmCode
        };
        InsertAlarmHistoryToDatabase(record);

        ApplyRecordDateFilter();

        // 区间外的报警只入库（记录查询页仍可查），不进入 Top3 统计
        bool inRange = IsAlarmInTopAlarmRange(record.AlarmTime);
        if (inRange)
        {
            string key = $"{alarm.AlarmCode}|{displayName}";
            if (_alarmStats.TryGetValue(key, out var stat))
                _alarmStats[key] = (displayName, stat.code, stat.count + 1);
            else
                _alarmStats[key] = (displayName, alarm.AlarmCode, 1);
        }

        RefreshTopAlarmLabels();
    }

    private void SyncMainAlarmGridFromActiveSnapshot(IReadOnlyList<AlarmTriggeredEvent> active)
    {
        _mainActiveAlarms = active;
        SyncCombinedAlarmGrid();
    }

    // 主页实时报警表格：主 PLC + 所有子 PLC 的当前活跃报警合并显示（子 PLC 行加 [设备名] 前缀）
    private void SyncCombinedAlarmGrid()
    {
        _mainAlarmGrid.Rows.Clear();

        foreach (var a in _mainActiveAlarms)
        {
            _mainAlarmGrid.Rows.Add(
                a.AlarmTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                a.AlarmName,
                a.AlarmCode);
        }

        foreach (ChildPlcModule module in _childPlcModules)
        {
            if (!_childActiveAlarms.TryGetValue(module.Device.Id, out var entry))
                continue;

            string prefix = $"[{entry.DeviceName}] ";
            foreach (var a in entry.Alarms)
            {
                _mainAlarmGrid.Rows.Add(
                    a.AlarmTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    prefix + a.AlarmName,
                    a.AlarmCode);
            }
        }
    }

    private void ApplyRecordDateFilter()
    {
        DateTime? fromDate = _dtRecordFrom.Checked ? _dtRecordFrom.Value.Date : null;
        DateTime? toDate = _dtRecordTo.Checked ? _dtRecordTo.Value.Date : null;
        BindRecordGrid(QueryAlarmHistoryFromDatabase(fromDate, toDate));
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

        DateTime time = producedAt ?? DateTime.Now;
        var shift = GetProductionShiftWindow(time);
        InsertProductionToDatabase(new ProductionRecord
        {
            ProducedAt = time,
            Quantity = quantity,
            ShiftName = shift.Name
        });
        RefreshProductionChart();
    }

    private async Task PollProductionCounterAsync()
    {
        if (_productionPollBusy || _plc is null || _debugToolsPlcExclusiveActive)
            return;

        string address = _txtProductionCounterAddress.Text.Trim();
        if (string.IsNullOrWhiteSpace(address))
            return;

        _productionPollBusy = true;
        try
        {
            bool currentSignal = await _plc.ReadBoolAsync(address);
            if (!_lastProductionCounterSignal.HasValue)
            {
                _lastProductionCounterSignal = currentSignal;
                return;
            }

            if (!_lastProductionCounterSignal.Value && currentSignal)
                RecordProduction(1);

            _lastProductionCounterSignal = currentSignal;
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
        string mode = _cmbProductionPeriod.SelectedItem?.ToString() ?? ProductionModeDay;
        if (string.Equals(mode, ProductionModeDay, StringComparison.Ordinal))
            RefreshDailyProductionChart();
        else
            RefreshPeriodProductionChart();
    }

    private void RefreshDailyProductionChart()
    {
        ConfigureProductionChartSeries(dayMode: true);

        DateTime day = _dtProductionAnchor.Value.Date;
        var (dayStart, dayEnd, nightStart, nightEnd, cycleStart, cycleEnd) = GetProductionDayRange(day);
        int hourCount = Math.Max(1, (int)Math.Round((cycleEnd - cycleStart).TotalHours));
        int[] hourlyQuantities = QueryProductionHourly(cycleStart, hourCount);
        int dayTotal = hourlyQuantities.Sum();
        int dayShiftTotal = QueryProductionQuantity(dayStart, dayEnd);
        int nightShiftTotal = QueryProductionQuantity(nightStart, nightEnd);

        DateTime now = DateTime.Now;
        DateTime shiftReference = day == GetCurrentProductionDate(now) ? now : day.Add(now.TimeOfDay);
        var shift = GetProductionShiftWindow(shiftReference);
        int shiftTotal = QueryProductionQuantity(shift.Start, shift.End);
        int currentHourQuantity = now >= cycleStart && now < cycleEnd
            ? hourlyQuantities[Math.Min(hourCount - 1, (int)(now - cycleStart).TotalHours)]
            : 0;

        _lblProductionSummary.Text =
            $"{day:yyyy-MM-dd}  生产日 {cycleStart:MM-dd HH:mm}~{cycleEnd:MM-dd HH:mm}  " +
            $"白班: {dayShiftTotal}  夜班: {nightShiftTotal}  总产量: {dayTotal}  " +
            $"当班({shift.Name}): {shiftTotal}  当前小时: {currentHourQuantity}";

        var dayHourlySeries = _productionChart.Series["白班小时产量"];
        var nightHourlySeries = _productionChart.Series["夜班小时产量"];
        var shiftSeries = _productionChart.Series["当班产量"];
        var totalSeries = _productionChart.Series["总产量"];

        int cumulativeDay = 0;
        for (int hour = 0; hour < hourCount; hour++)
        {
            DateTime hourStart = cycleStart.AddHours(hour);
            DateTime hourEnd = hourStart.AddHours(1);
            string xLabel = $"{hourStart:HH:mm}";
            int hourlyQuantity = hourlyQuantities[hour];
            cumulativeDay += hourlyQuantity;

            bool isDayShiftHour = hourStart >= dayStart && hourStart < dayEnd;
            int dayPointIndex = dayHourlySeries.Points.AddXY(xLabel, isDayShiftHour ? hourlyQuantity : 0);
            int nightPointIndex = nightHourlySeries.Points.AddXY(xLabel, isDayShiftHour ? 0 : hourlyQuantity);
            dayHourlySeries.Points[dayPointIndex].Label = isDayShiftHour && hourlyQuantity > 0
                ? hourlyQuantity.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
            nightHourlySeries.Points[nightPointIndex].Label = !isDayShiftHour && hourlyQuantity > 0
                ? hourlyQuantity.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
            totalSeries.Points.AddXY(xLabel, cumulativeDay);

            if (hourEnd > shift.Start && hourStart < shift.End)
            {
                DateTime cumulativeEnd = hourEnd < shift.End ? hourEnd : shift.End;
                int shiftCumulative = QueryProductionQuantity(shift.Start, cumulativeEnd);
                shiftSeries.Points.AddXY(xLabel, shiftCumulative);
            }
        }
    }

    private void RefreshPeriodProductionChart()
    {
        ConfigureProductionChartSeries(dayMode: false);

        var (startDate, dayCount) = GetProductionWindow(_dtProductionAnchor.Value.Date);
        var dayShiftSeries = _productionChart.Series["白班产量"];
        var nightShiftSeries = _productionChart.Series["夜班产量"];
        var totalLabelSeries = _productionChart.Series["总产量标注"];
        int rangeTotal = 0;
        int maxDailyQuantity = 0;
        for (int i = 0; i < dayCount; i++)
        {
            DateTime day = startDate.AddDays(i);
            var (dayShiftQuantity, nightShiftQuantity) = QueryProductionShiftQuantities(day);
            int dailyQuantity = dayShiftQuantity + nightShiftQuantity;
            string xLabel = day.ToString("MM-dd", CultureInfo.InvariantCulture);
            rangeTotal += dailyQuantity;
            maxDailyQuantity = Math.Max(maxDailyQuantity, dailyQuantity);

            int dayPointIndex = dayShiftSeries.Points.AddXY(xLabel, dayShiftQuantity);
            int nightPointIndex = nightShiftSeries.Points.AddXY(xLabel, nightShiftQuantity);
            dayShiftSeries.Points[dayPointIndex].Label = dayShiftQuantity > 0
                ? $"{dayShiftQuantity.ToString(CultureInfo.InvariantCulture)}\n白班"
                : string.Empty;
            nightShiftSeries.Points[nightPointIndex].Label = nightShiftQuantity > 0
                ? $"{nightShiftQuantity.ToString(CultureInfo.InvariantCulture)}\n夜班"
                : string.Empty;

            int totalPointIndex = totalLabelSeries.Points.AddXY(xLabel, dailyQuantity);
            totalLabelSeries.Points[totalPointIndex].Label = dailyQuantity > 0 ? $"总:{dailyQuantity}" : string.Empty;
        }

        string mode = _cmbProductionPeriod.SelectedItem?.ToString() ?? ProductionModeWeek;
        DateTime endDate = startDate.AddDays(dayCount - 1);
        _lblProductionSummary.Text = $"{mode}  {startDate:yyyy-MM-dd} 至 {endDate:yyyy-MM-dd}  总产量: {rangeTotal}";
        ReserveProductionChartTopSpace(maxDailyQuantity);
    }

    private (DateTime startDate, int dayCount) GetProductionWindow(DateTime anchorDate)
    {
        if (string.Equals(_cmbProductionPeriod.SelectedItem?.ToString(), ProductionModeMonth, StringComparison.Ordinal))
        {
            DateTime monthStart = new(anchorDate.Year, anchorDate.Month, 1);
            return (monthStart, DateTime.DaysInMonth(anchorDate.Year, anchorDate.Month));
        }

        if (string.Equals(_cmbProductionPeriod.SelectedItem?.ToString(), ProductionModeDay, StringComparison.Ordinal))
            return (anchorDate, 1);

        int offset = ((int)anchorDate.DayOfWeek + 6) % 7;
        DateTime weekStart = anchorDate.AddDays(-offset);
        return (weekStart, 7);
    }

    private void ConfigureProductionChartSeries(bool dayMode)
    {
        _productionChart.Series.Clear();
        ChartArea area = _productionChart.ChartAreas["Main"];
        area.AxisX.Interval = 1;
        area.AxisY.Minimum = 0;
        area.AxisY.Maximum = double.NaN;

        if (dayMode)
        {
            AddProductionSeries("白班小时产量", SeriesChartType.Column, ProductionDayShiftColor);
            AddProductionSeries("夜班小时产量", SeriesChartType.Column, ProductionNightShiftColor);
            AddProductionSeries("当班产量", SeriesChartType.Line, ProductionCumulativeColor);
            AddProductionSeries("总产量", SeriesChartType.Line, ProductionTotalColor);
        }
        else
        {
            AddProductionSeries("白班产量", SeriesChartType.StackedColumn, ProductionDayShiftColor);
            AddProductionSeries("夜班产量", SeriesChartType.StackedColumn, ProductionNightShiftColor);
            AddProductionTotalLabelSeries();
        }
    }

    private void AddProductionTotalLabelSeries()
    {
        var series = new Series("总产量标注")
        {
            ChartType = SeriesChartType.Point,
            ChartArea = "Main",
            IsVisibleInLegend = false,
            Color = Color.Transparent,
            MarkerStyle = MarkerStyle.None,
            IsValueShownAsLabel = true,
            LabelForeColor = Color.FromArgb(30, 45, 70),
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold)
        };
        series.SmartLabelStyle.Enabled = true;
        series.SmartLabelStyle.MovingDirection = LabelAlignmentStyles.Top;
        _productionChart.Series.Add(series);
    }

    private void ReserveProductionChartTopSpace(int maxValue)
    {
        ChartArea area = _productionChart.ChartAreas["Main"];
        if (maxValue <= 0)
        {
            area.AxisY.Maximum = double.NaN;
            return;
        }

        area.AxisY.Maximum = Math.Ceiling(maxValue * 1.18D + 1D);
    }

    private void AddProductionSeries(string name, SeriesChartType chartType, Color color)
    {
        var series = new Series(name)
        {
            ChartType = chartType,
            ChartArea = "Main",
            Legend = "Legend",
            Color = color
        };

        if (chartType == SeriesChartType.Line)
        {
            series.BorderWidth = 3;
            series.MarkerStyle = MarkerStyle.Circle;
            series.MarkerSize = 7;
        }
        else if (chartType == SeriesChartType.Column)
        {
            series.IsValueShownAsLabel = true;
            series.LabelForeColor = Color.FromArgb(30, 45, 70);
        }
        else if (chartType == SeriesChartType.StackedColumn)
        {
            series.IsValueShownAsLabel = true;
            series.LabelForeColor = Color.FromArgb(30, 45, 70);
        }

        _productionChart.Series.Add(series);
    }

    private (DateTime Start, DateTime End, string Name) GetProductionShiftWindow(DateTime referenceTime)
    {
        TimeSpan dayStart = _tmDayShiftStart.Value.TimeOfDay;
        TimeSpan dayEnd = _tmDayShiftEnd.Value.TimeOfDay;
        TimeSpan nightStart = _tmNightShiftStart.Value.TimeOfDay;
        TimeSpan nightEnd = _tmNightShiftEnd.Value.TimeOfDay;

        for (int offset = -1; offset <= 1; offset++)
        {
            DateTime day = referenceTime.Date.AddDays(offset);
            var dayWindow = BuildProductionShiftWindow(day, dayStart, dayEnd, "白班");
            if (referenceTime >= dayWindow.Start && referenceTime < dayWindow.End)
                return dayWindow;

            var nightWindow = BuildProductionShiftWindow(day, nightStart, nightEnd, "夜班");
            if (referenceTime >= nightWindow.Start && referenceTime < nightWindow.End)
                return nightWindow;
        }

        return BuildProductionShiftWindow(referenceTime.Date, dayStart, dayEnd, "白班");
    }

    /// <summary>当前“生产日”= 当前班次结束日（夜班跨午夜时顺延到次日）。</summary>
    private DateTime GetCurrentProductionDate(DateTime now) => GetProductionShiftWindow(now).End.Date;

    /// <summary>
    /// 计算“生产日”范围：班次按<b>结束日</b>归属。
    /// 白班 = 当日白班时段（默认 08:00~20:00）；夜班 = 昨日夜班开始 ~ 当日夜班结束（默认 昨 20:00~当日 08:00）。
    /// 因此生产日跨度 = 昨日夜班开始 ~ 当日白班结束（默认 昨 20:00 ~ 当日 20:00，共 24 小时）。
    /// </summary>
    private (DateTime DayStart, DateTime DayEnd, DateTime NightStart, DateTime NightEnd, DateTime CycleStart, DateTime CycleEnd) GetProductionDayRange(DateTime day)
    {
        DateTime date = day.Date;
        var dayWindow = ResolveShiftWindowByEndDate(date, _tmDayShiftStart.Value.TimeOfDay, _tmDayShiftEnd.Value.TimeOfDay);
        var nightWindow = ResolveShiftWindowByEndDate(date, _tmNightShiftStart.Value.TimeOfDay, _tmNightShiftEnd.Value.TimeOfDay);

        DateTime cycleStart = dayWindow.Start < nightWindow.Start ? dayWindow.Start : nightWindow.Start;
        DateTime cycleEnd = dayWindow.End > nightWindow.End ? dayWindow.End : nightWindow.End;

        double hours = (cycleEnd - cycleStart).TotalHours;
        if (hours <= 0 || hours > 48)
        {
            // 班次时段配置异常（重复/超长）时回退到自然日，避免图表被撑到几十个小时
            cycleStart = date;
            cycleEnd = date.AddDays(1);
        }

        return (dayWindow.Start, dayWindow.End, nightWindow.Start, nightWindow.End, cycleStart, cycleEnd);
    }

    /// <summary>
    /// 取“结束时间落在指定日期”的那个班次窗口。夜班跨午夜时自然落到前一天开始，
    /// 即 2026-11-03 的夜班 = 11-02 20:00 ~ 11-03 08:00。
    /// </summary>
    private static (DateTime Start, DateTime End) ResolveShiftWindowByEndDate(DateTime date, TimeSpan start, TimeSpan end)
    {
        for (int offset = -1; offset <= 1; offset++)
        {
            var window = BuildProductionShiftWindow(date.Date.AddDays(offset), start, end, string.Empty);
            if (window.End.Date == date.Date && window.End > window.Start)
                return (window.Start, window.End);
        }

        var fallback = BuildProductionShiftWindow(date.Date, start, end, string.Empty);
        return (fallback.Start, fallback.End);
    }

    private static (DateTime Start, DateTime End, string Name) BuildProductionShiftWindow(
        DateTime date,
        TimeSpan start,
        TimeSpan end,
        string name)
    {
        DateTime shiftStart = date.Add(start);
        DateTime shiftEnd = date.Add(end);
        if (shiftEnd <= shiftStart)
            shiftEnd = shiftEnd.AddDays(1);

        return (shiftStart, shiftEnd, name);
    }

    private void InsertProductionToDatabase(ProductionRecord record)
    {
        try
        {
            using var connection = new SqliteConnection(_alarmHistoryConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO production_history (production_time, production_date, shift_name, quantity)
                VALUES ($time, $date, $shiftName, $quantity);
                """;
            command.Parameters.AddWithValue("$time", FormatSqliteDateTime(record.ProducedAt));
            command.Parameters.AddWithValue("$date", record.ProducedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$shiftName", record.ShiftName);
            command.Parameters.AddWithValue("$quantity", record.Quantity);
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            AppendLog($"产量记录入库失败: {ex.Message}");
        }
    }

    private int QueryProductionQuantity(DateTime startTime, DateTime endTime)
    {
        try
        {
            using var connection = new SqliteConnection(_alarmHistoryConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COALESCE(SUM(quantity), 0)
                FROM production_history
                WHERE production_time >= $startTime
                  AND production_time < $endTime;
                """;
            command.Parameters.AddWithValue("$startTime", FormatSqliteDateTime(startTime));
            command.Parameters.AddWithValue("$endTime", FormatSqliteDateTime(endTime));
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            AppendLog($"产量统计查询失败: {ex.Message}");
            return 0;
        }
    }

    private (int DayShiftQuantity, int NightShiftQuantity) QueryProductionShiftQuantities(DateTime day)
    {
        DateTime date = day.Date;
        var dayWindow = ResolveShiftWindowByEndDate(date, _tmDayShiftStart.Value.TimeOfDay, _tmDayShiftEnd.Value.TimeOfDay);
        // 夜班按“结束日”归属：当日夜班 = 昨日夜班开始 ~ 当日夜班结束（默认 昨 20:00 ~ 当日 08:00）
        var nightWindow = ResolveShiftWindowByEndDate(date, _tmNightShiftStart.Value.TimeOfDay, _tmNightShiftEnd.Value.TimeOfDay);

        int dayShiftQuantity = QueryProductionQuantity(dayWindow.Start, dayWindow.End);
        int nightShiftQuantity = QueryProductionQuantity(nightWindow.Start, nightWindow.End);

        // 班次时段配置重叠时扣掉重复区间，避免同一件产品被白/夜班各算一次
        if (nightWindow.Start < dayWindow.End && dayWindow.Start < nightWindow.End)
        {
            DateTime overlapStart = nightWindow.Start > dayWindow.Start ? nightWindow.Start : dayWindow.Start;
            DateTime overlapEnd = nightWindow.End < dayWindow.End ? nightWindow.End : dayWindow.End;
            nightShiftQuantity = Math.Max(0, nightShiftQuantity - QueryProductionQuantity(overlapStart, overlapEnd));
        }

        return (dayShiftQuantity, nightShiftQuantity);
    }

    private int[] QueryProductionHourly(DateTime startTime, int hourCount)
    {
        if (hourCount <= 0)
            hourCount = 1;

        int[] values = new int[hourCount];
        try
        {
            using var connection = new SqliteConnection(_alarmHistoryConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            // 按“距 startTime 的小时偏移”分桶，兼容 08:00 ~ 次日 08:00 这类跨午夜的生产日
            command.CommandText = """
                SELECT CAST((strftime('%s', production_time) - strftime('%s', $startTime)) / 3600 AS INTEGER) AS bucket,
                       COALESCE(SUM(quantity), 0)
                FROM production_history
                WHERE production_time >= $startTime
                  AND production_time < $endTime
                GROUP BY bucket;
                """;
            command.Parameters.AddWithValue("$startTime", FormatSqliteDateTime(startTime));
            command.Parameters.AddWithValue("$endTime", FormatSqliteDateTime(startTime.AddHours(hourCount)));

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                int bucket = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
                if (bucket >= 0 && bucket < hourCount)
                    values[bucket] = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"小时产量查询失败: {ex.Message}");
        }

        return values;
    }

    private static string FormatSqliteDateTime(DateTime value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

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
        // 仅拦截用户手动关闭（点 X / Alt+F4 / 系统菜单）；Windows 关机、Application.Exit 等不弹框
        if (e.CloseReason == CloseReason.UserClosing)
        {
            var choice = MessageBox.Show(this,
                "确定要退出程序吗？退出后将断开 PLC 与相机通讯。",
                "退出确认",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (choice != DialogResult.OK)
            {
                e.Cancel = true;
                return;
            }
        }

        try
        {
            if (_cameraDebugWindow is ICameraProjectRuntime runtime && !runtime.TryStopRuntime())
            {
                e.Cancel = true;
                AppendLog("相机线程尚未退出，已取消主程序关闭，请等待当前通信结束后重试。");
                return;
            }
            _cameraDebugWindow?.Close();
        }
        catch (Exception ex)
        {
            e.Cancel = true;
            AppendLog("停止相机工程失败：" + ex.Message);
            return;
        }

        // 大华相机自动流程是后台常驻的，只在这里（主程序退出）才真正停止。
        try
        {
            DahuaAutoCamService.Stop(5000);
        }
        catch (Exception ex)
        {
            AppendLog("停止大华相机自动流程失败：" + ex.GetBaseException().Message);
        }

        _stationControlPopupForm?.Close();
        _mainPageWindow?.Close();
        _systemConfigPageWindow?.Close();
        _debugToolsPageWindow?.Close();
        _monitorPageWindow?.Close();
        _recordsPageWindow?.Close();
        _parameterAdjustPageWindow?.Close();
        _productionStatisticsPageWindow?.Close();
        _dianCiHanWindow?.ShutdownAndClose();
        _mesPollCts?.Cancel();
        _mesWindow?.ShutdownAndClose();
        MesScada.MesYield.Instacne.Flush();
        MesScada.MesController.Instacne.Close();
        ParkStationDeviceHost();
        SaveProductionCounterAddressToMainPlcConfig();
        _statusTimer.Stop();
        if (_mouseActivityFilter is not null)
        {
            Application.RemoveMessageFilter(_mouseActivityFilter);
            _mouseActivityFilter = null;
        }
        _monitorRefreshTimer.Stop();
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
        _plcDeviceManagerWindow?.Close();
        DisconnectPlcDevices();
        base.OnFormClosing(e);
    }

    private async void uiButton1_Click(object sender, EventArgs e)
    {
        if (BlockMainFormDirectPlcAccess("测试写入"))
            return;

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
        if (BlockMainFormDirectPlcAccess("测试写入"))
            return;

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
            await Task.Delay(100);
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
        if (BlockMainFormDirectPlcAccess("测试写入"))
            return;

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
            await Task.Delay(100);
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
        if (BlockMainFormDirectPlcAccess("测试写入"))
            return;

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
            await Task.Delay(100);
            await _plc.WriteBoolAsync(testAddress, false);
            AppendLog($"写入PLC成功: {testAddress} = false");
        }
        catch (Exception ex)
        {
            AppendLog($"写入PLC失败: {ex.GetBaseException().Message}");
        }
    }
}
