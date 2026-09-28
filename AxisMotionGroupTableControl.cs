using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExcelDataReader;

namespace PlcConectCom.WinFormsTest;

// 轴运动字段数据类型
public enum AxisMotionFieldType
{
    Bool,
    Real,
    String
}

// 轴运动地址配置
public sealed class AxisMotionAddressConfig
{
    public string AxisId { get; set; } = string.Empty;

    public int? AxisNumber { get; set; }

    public bool IsAbsEncorder { get; set; }

    /// <summary>当前轴的同步轴编号（AxisId）。为空/null 表示未配置同步轴。</summary>
    public string? SyncAxisId { get; set; }

    public Dictionary<string, string> Addresses { get; set; } = [];
}

// 轴组运动表控件：统一管理轴地址配置、状态显示与控制写入
public sealed partial class AxisMotionGroupTableControl : UserControl
{
    private enum AutoRefreshScope
    {
        SingleAxis,
        Global
    }
    private const string ConfigFileName = "AxisMotionGroup.config.json";
    private static readonly JsonSerializerOptions JsonFileOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private sealed record FieldDef(string Key, string DisplayName, AxisMotionFieldType FieldType);
    private sealed record FieldReadRequest(
        DataGridViewRow Row,
        DataGridViewCell Cell,
        FieldDef Field,
        string Address,
        string MemType,
        int Offset,
        int Bit);

    private sealed record FieldReadBatch<TRequest>(int Start, int End, List<TRequest> Requests);

    private enum AxisFieldUiMode
    {
        Display,
        BoolControl,
        BoolStatus
    }

    private static readonly HashSet<string> BoolControlFields =
    [
        "AxisEnable", "AxisHome", "JogPlus", "JogMinus", "AbsMove", "RelMove", "Stop", "AbsReset"
    ];

    /// <summary>写 true，经 <see cref="BoolPulseHoldMs"/> 后写 false。</summary>
    private static readonly HashSet<string> PulseTriggerFieldKeys =
    [
        "AxisHome", "AbsMove", "Stop", "AbsReset"
    ];

    /// <summary>
    /// 脉冲保持时长（毫秒）。HOME / STOP / AbsReset（经 PulseBoolFieldOnRowAsync）
    /// 与 Step±（ExecuteRelMoveSignedAsync 阶段 3）、「轴停止运动」共用此值。
    /// </summary>
    private const int BoolPulseHoldMs = 100;
    /// <summary>侧栏「手动速度」下拉默认选中项索引（与 <see cref="BuildMotionSidePanel"/> 中 Items 顺序一致）。</summary>
    private const int DefaultManualSpeedComboIndex = 5;
    /// <summary>调试页左侧手动区占用宽度（像素）。</summary>
    private const int MotionSidePanelOuterWidth = 576;
    private const int MotionPanelButtonHeight = 42;
    private const int MotionPairRowMarginBottom = 6;
    private const int AutoRefreshIntervalMs = 10;
    private const int MaxBatchBitCount = 512;
    private const int MaxBatchByteCount = 128;//240
    private const int MaxBatchDoubleCount = 24; //60;
    private static readonly Font StatusDotFont = new("Segoe UI Symbol", 14f, FontStyle.Bold);
    private static readonly string[] SidePanelStatusFieldOrder = ["DiPot", "DiNot", "DiHome", "Enabling", "HomeDone", "Alarm"];

    private static readonly HashSet<string> BoolStatusFields =
    [
        "DiPot", "DiNot", "DiHome", "Enabling", "HomeDone", "Alarm"
    ];

    private static readonly HashSet<string> CompactStatusColumnFields =
    [
        "DiPot", "DiNot", "DiHome", "Enabling", "HomeDone", "Alarm"
    ];

    private static readonly string[] ObsoleteAddressKeys = ["StepPlus", "StepMinus"];

    /// <summary>表格中可编辑并随「保存配置」写入 PLC 的速度/加减速字段（顺序与列含义一致）。</summary>
    private static readonly string[] SpeedProfilePlcWriteFieldKeys =
    [
        "IdleSpeed", "WorkSpeed", "CreepSpeed", "HomeSpeed", "Acceleration", "Deceleration"
    ];

    /// <summary>「编辑数据」弹框包含的字段：六项速度/加减速 + 绝对定位软正/负限位。</summary>
    private static readonly string[] EditDialogFieldKeys =
    [
        .. SpeedProfilePlcWriteFieldKeys,
        "AbsSoftLimitPos", "AbsSoftLimitNeg"
    ];

    /// <summary>仅在点击「刷新数据」时读取，不参与自动刷新。</summary>
    private static readonly string[] OnDemandRefreshFieldKeys =
    [
        "IdleSpeed", "WorkSpeed", "CreepSpeed", "HomeSpeed", "Acceleration", "Deceleration",
        "AbsSoftLimitPos", "AbsSoftLimitNeg"
    ];

    /// <summary>「单轴刷新」定时器只读这些字段（共 9 个 PLC 地址），减少往返。</summary>
    private static readonly string[] SingleAxisRealtimeFieldKeys =
    [
        "DiPot", "DiNot", "DiHome", "Enabling", "HomeDone", "Alarm",
        "CurrentPos", "CurrentSpeed", "CurrentTorque"
    ];

    private static void RemoveObsoleteAddressKeys(Dictionary<string, string> map)
    {
        if (map.TryGetValue("AlarmCode", out string? alarmAddress) && !string.IsNullOrWhiteSpace(alarmAddress))
            map["Alarm"] = alarmAddress;

        foreach (string key in ObsoleteAddressKeys)
            map.Remove(key);

        map.Remove("AlarmCode");
        map.Remove("IsAbsEncorder");
    }

    private static readonly FieldDef[] FieldDefinitions =
    [
        new("AxisEnable", "轴使能", AxisMotionFieldType.Bool),
        new("AxisHome", "轴回零", AxisMotionFieldType.Bool),
        new("JogPlus", "JOG+", AxisMotionFieldType.Bool),
        new("JogMinus", "JOG-", AxisMotionFieldType.Bool),
        new("AbsMove", "AbsMove", AxisMotionFieldType.Bool),
        new("RelMove", "RelMove", AxisMotionFieldType.Bool),
        new("RelMoveDistance", "相对位移量", AxisMotionFieldType.Real),
        new("Stop", "Stop", AxisMotionFieldType.Bool),
        new("AbsReset", "绝对值清零", AxisMotionFieldType.Bool),
        new("DiPot", "正极限", AxisMotionFieldType.Bool),
        new("DiNot", "负极限", AxisMotionFieldType.Bool),
        new("DiHome", "原点", AxisMotionFieldType.Bool),
        new("Enabling", "使能", AxisMotionFieldType.Bool),
        new("HomeDone", "回零完成", AxisMotionFieldType.Bool),
        new("Alarm", "报警", AxisMotionFieldType.Bool),
        new("IsAbsEncorder", "绝对值轴", AxisMotionFieldType.Bool),
        new("ManualSpeed", "手动速度", AxisMotionFieldType.Real),
        new("IdleSpeed", "空载速度", AxisMotionFieldType.Real),
        new("WorkSpeed", "工作速度", AxisMotionFieldType.Real),
        new("CreepSpeed", "缓行速度", AxisMotionFieldType.Real),
        new("HomeSpeed", "回零速度", AxisMotionFieldType.Real),
        new("Acceleration", "加速度", AxisMotionFieldType.Real),
        new("Deceleration", "减速度", AxisMotionFieldType.Real),
        new("AbsSoftLimitPos", "绝对定位软正限位", AxisMotionFieldType.Real),
        new("AbsSoftLimitNeg", "绝对定位软负限位", AxisMotionFieldType.Real),
        new("CurrentPos", "当前位置", AxisMotionFieldType.Real),
        new("CurrentSpeed", "当前速度", AxisMotionFieldType.Real),
        new("CurrentTorque", "当前扭矩", AxisMotionFieldType.Real)
    ];

    /// <summary>「表格显示」弹窗已按需求移除；表格不再展示，仅作为每行轴配置的数据载体，
    /// 这里提供一个隐藏父容器以避免无句柄时的异常。</summary>
    private readonly Panel _gridStorage = new()
    {
        Visible = false,
        Size = new Size(1, 1),
        Margin = Padding.Empty,
        Padding = Padding.Empty
    };

    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeColumns = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = true,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        ScrollBars = ScrollBars.Both
    };

    // 「新增轴」「删除轴」按钮已按需求移除；轴只能通过配置文件 / 导入轴编号维护。
    // 「地址配置」原为表格内每行的按钮列，后按需求移到工具栏（刷新数据之后），对当前选中轴生效。
    private readonly Button _btnRefreshData = CreateToolbarButton("刷新数据");
    private readonly Button _btnAddressConfig = CreateToolbarButton("地址配置");
    private readonly Button _btnEditSpeedProfile = CreateToolbarButton("编辑数据");
    private readonly Button _btnSaveConfig = CreateToolbarButton("保存配置");
    private readonly Button _btnLoadConfig = CreateToolbarButton("加载配置");
    private readonly Button _btnImportTable = CreateToolbarButton("导入表格配置");
    private readonly Button _btnImportAxisNumbers = CreateToolbarButton("导入轴编号");
    // 「实时刷新」范围下拉框已按需求移除，固定为单轴刷新（见 _autoRefreshScope）

    private readonly TableLayoutPanel _rootTable = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(0) };
    private readonly Panel _rootTableSpacer = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(8, 6, 8, 8), BackColor = Color.WhiteSmoke };
    private readonly TableLayoutPanel _motionCommandTable = new() { Dock = DockStyle.Top, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty };
    private readonly Panel _motionPanel = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(8, 6, 8, 8), BackColor = Color.WhiteSmoke };
    private readonly Label _lblPanelAxis = new() { Text = "轴编号", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly ComboBox _cmbPanelAxis = new() { DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left | AnchorStyles.Right };
    /// <summary>绝对值轴（IsAbsEncorder）勾选框：放在侧栏「手动速度」下方，取值仍存于该轴的隐藏表格列。</summary>
    private readonly Label _lblIsAbsEncorder = new() { Text = "绝对值轴", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly CheckBox _chkIsAbsEncorder = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private bool _programmaticIsAbsEncorder;
    private readonly CheckBox _chkSyncMotion = new() { Text = "同步运动", AutoSize = true, Anchor = AnchorStyles.Left, Visible = false };
    private readonly Label _lblSyncAxis = new() { Text = "同步轴", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly ComboBox _cmbSyncAxis = new() { DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left | AnchorStyles.Right };
    /// <summary>同步轴下拉框的「未配置」占位项。</summary>
    private const string SyncAxisNoneItem = "(无)";
    /// <summary>轴编号 → 同步轴编号（空串表示未配置）。随 AxisMotionGroup.config.json 持久化。</summary>
    private readonly Dictionary<string, string> _syncAxisByAxisId = new(StringComparer.Ordinal);
    private bool _programmaticSyncAxisChange;
    private bool _programmaticSyncMotionCheck;
    private readonly FlowLayoutPanel _pnlSideBoolStatus = new()
    {
        AutoSize = true,
        WrapContents = true,
        FlowDirection = FlowDirection.LeftToRight,
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
        Margin = new Padding(0, 3, 0, 6)
    };
    private readonly Label[] _lblSideStatusDots = new Label[SidePanelStatusFieldOrder.Length];
    private readonly Label _lblDisplacement = new() { Text = "位移", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly ComboBox _cmbDisplacement = new() { DropDownStyle = ComboBoxStyle.DropDown, Anchor = AnchorStyles.Left | AnchorStyles.Right };
    private readonly Label _lblManualSpeedSide = new() { Text = "手动速度", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly ComboBox _cmbManualSpeed = new() { DropDownStyle = ComboBoxStyle.DropDown, Anchor = AnchorStyles.Left | AnchorStyles.Right };
    // 侧栏三个实时标签（当前速度/位置/扭矩）统一 AutoSize + 相同边距，保证行高与垂直间距一致
    private readonly Label _lblPanelStatus = new() { Text = "当前速度: —", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, MaximumSize = new Size(552, 0) };
    private readonly Label _lblPanelCurrentPos = new() { Text = "当前位置: —", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, MaximumSize = new Size(552, 0) };
    private readonly Label _lblPanelCurrentTorque = new() { Text = "当前扭矩: —", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, MaximumSize = new Size(552, 0) };
    private readonly Button _btnPanelStepMinus = new() { Text = "Step−", Height = MotionPanelButtonHeight, Margin = new Padding(0), AutoSize = false };
    private readonly Button _btnPanelStepPlus = new() { Text = "Step+", Height = MotionPanelButtonHeight, Margin = new Padding(0), AutoSize = false };
    private readonly Button _btnPanelJogMinus = new() { Text = "JOG−", Height = MotionPanelButtonHeight, Margin = new Padding(0), AutoSize = false, BackColor = Color.FromArgb(46, 160, 67), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly Button _btnPanelJogPlus = new() { Text = "JOG+", Height = MotionPanelButtonHeight, Margin = new Padding(0), AutoSize = false, BackColor = Color.FromArgb(46, 160, 67), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly Button _btnPanelHome = new() { Text = "HOME", Height = MotionPanelButtonHeight, Margin = new Padding(0), AutoSize = false, BackColor = Color.FromArgb(0, 150, 160), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly Button _btnPanelStop = new() { Text = "STOP", Height = MotionPanelButtonHeight, Margin = new Padding(0), AutoSize = false, BackColor = Color.FromArgb(220, 120, 40), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly Button _btnPanelAbsReset = new() { Text = "绝对值清零", Height = MotionPanelButtonHeight, Margin = new Padding(0), AutoSize = false, BackColor = Color.FromArgb(120, 80, 160), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly Button _btnPanelEnable = new() { Text = "使能", Height = MotionPanelButtonHeight, Margin = new Padding(0), AutoSize = false, BackColor = Color.FromArgb(140, 40, 40), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly Button _btnPanelStopAll = new() { Text = "轴停止运动", Height = MotionPanelButtonHeight, Margin = new Padding(0), AutoSize = false, BackColor = Color.FromArgb(200, 40, 40), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly System.Windows.Forms.Timer _manualSpeedDebounce = new() { Interval = 400 };
    private readonly System.Windows.Forms.Timer _autoRefreshTimer = new() { Interval = AutoRefreshIntervalMs };
    private bool _programmaticManualSpeedChange;
    private bool _autoRefreshInProgress;
    /// <summary>实时刷新范围：原由工具栏下拉框切换，现固定为单轴刷新（只刷侧栏当前轴）。</summary>
    private AutoRefreshScope _autoRefreshScope = AutoRefreshScope.SingleAxis;

    private readonly Dictionary<string, int> _addressColumnIndexByField = [];
    private readonly Dictionary<string, int> _valueColumnIndexByField = [];
    private readonly Dictionary<string, AxisFieldUiMode> _uiModeByField = [];
    private readonly string _configPath;
    private int _isAbsEncorderColumnIndex = -1;
    private bool _suspendAutoSave;
    private bool _canAddDelete = true;
    private bool _canModify = true;
    private bool _programmaticPanelAxisChange;

    public AxisMotionGroupTableControl() : this(null) { }

    public AxisMotionGroupTableControl(string? storageKey)
    {
        string fileName = ConfigFileName;
        if (!string.IsNullOrWhiteSpace(storageKey))
        {
            string safeKey = string.Concat(storageKey.Where(char.IsLetterOrDigit));
            if (!string.IsNullOrWhiteSpace(safeKey))
                fileName = $"{Path.GetFileNameWithoutExtension(ConfigFileName)}.{safeKey}{Path.GetExtension(ConfigFileName)}";
        }
        _configPath = Path.Combine(AppContext.BaseDirectory, fileName);
        BuildUi();
        BindEvents();
        LoadConfigFromFile();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyInitialGridSelectionAndSync();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible || !IsHandleCreated || IsDisposed)
            return;

        BeginInvoke(new MethodInvoker(() =>
        {
            if (IsDisposed || !Visible)
                return;

            int dataRows = _grid.Rows.Cast<DataGridViewRow>().Count(r => !r.IsNewRow);
            if (dataRows == 0)
                return;

            if (_cmbPanelAxis.Items.Count == 0 || _cmbPanelAxis.Items.Count != dataRows)
                RebuildPanelAxisCombo();
        }));
    }

    /// <summary>执行「绝对值清零」前由宿主提供密码确认；返回 false 则取消操作。未赋值时不校验（兼容无宿主场景）。</summary>
    public Func<bool>? AbsResetPasswordConfirmRequest { get; set; }

    public Func<global::PlcConectCom.PlcCommunicator?>? PlcProvider { get; set; }

    public global::PlcConectCom.PlcCommunicator? PlcInstance { get; set; }

    public Action<string>? LogAction { get; set; }

    /// <summary>设备显示名（主 PLC / 子 PLC 名称），仅用于手动操作日志归属。</summary>
    public string DeviceDisplayName { get; set; } = "主 PLC";

    public int StringReadLength { get; set; } = 64;

    /// <summary>记录一次「点击按钮」类手动操作：轴侧栏按钮（Step±/JOG/HOME/STOP/使能/停止运动/手动速度）。</summary>
    private void LogManualButton(string action, IEnumerable<string> axes, string? detail = null, string? address = null)
    {
        var names = axes
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        ManualOperationLogStore.Write(
            ManualOperationCategory.Button,
            DeviceDisplayName,
            action,
            target: names.Count == 0 ? "—" : string.Join("，", names),
            address: address,
            detail: detail);
    }

    public void SetPermission(bool canAddDelete, bool canModify)
    {
        _canAddDelete = canAddDelete;
        _canModify = canModify;
        _btnSaveConfig.Enabled = _canAddDelete;
        _btnLoadConfig.Enabled = _canAddDelete;
        _btnImportTable.Enabled = _canAddDelete;
        _btnAddressConfig.Enabled = _canAddDelete;
        _btnImportAxisNumbers.Enabled = _canAddDelete && _canModify;
        _btnRefreshData.Enabled = canModify;
        _btnEditSpeedProfile.Enabled = canModify;
        _grid.ReadOnly = !canModify;
        ApplyGridColumnReadOnlyForSpeedProfileCells();
        UpdateMotionPanelEnabledState();
        UpdateSidePanelIsAbsEncorder();
    }

    /// <summary>表格显示值保持只读，仅允许 IsAbsEncorder 勾选交互（地址配置已移至工具栏按钮）。</summary>
    private void ApplyGridColumnReadOnlyForSpeedProfileCells()
    {
        if (_grid.ReadOnly)
            return;

        foreach (DataGridViewColumn col in _grid.Columns)
        {
            if (string.Equals(col.Name, "AxisId", StringComparison.Ordinal))
            {
                col.ReadOnly = true;
                continue;
            }

            if (col is DataGridViewButtonColumn)
            {
                col.ReadOnly = true;
                continue;
            }

            if (!string.IsNullOrEmpty(col.Name) && col.Name.EndsWith("_Val", StringComparison.Ordinal))
            {
                string fieldKey = col.Name[..^"_Val".Length];
                col.ReadOnly = !string.Equals(fieldKey, "IsAbsEncorder", StringComparison.Ordinal);
                continue;
            }

            col.ReadOnly = true;
        }
    }

    private static string GetFieldDisplayName(string fieldKey) =>
        FieldDefinitions.FirstOrDefault(f => string.Equals(f.Key, fieldKey, StringComparison.Ordinal))?.DisplayName
        ?? fieldKey;

    private static bool ShowFieldColumnInMainGrid(FieldDef field)
    {
        if (BoolControlFields.Contains(field.Key))
            return false;
        if (string.Equals(field.Key, "ManualSpeed", StringComparison.Ordinal))
            return false;
        if (string.Equals(field.Key, "RelMoveDistance", StringComparison.Ordinal))
            return false;
        return true;
    }

    private static Button CreateToolbarButton(string text)
    {
        return new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(TextRenderer.MeasureText(text, SystemFonts.DefaultFont).Width + 18, 0),
            Padding = new Padding(6, 0, 6, 0),
            Margin = new Padding(3, 0, 3, 3)
        };
    }

    private void BuildUi()
    {
        _grid.DefaultCellStyle.SelectionBackColor = Color.Gainsboro;
        _grid.DefaultCellStyle.SelectionForeColor = Color.Black;
        _grid.RowTemplate.Height = 32;
        _grid.Dock = DockStyle.Fill;

        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "AxisId", HeaderText = "轴编号", Width = 90, MinimumWidth = 60 });

        foreach (var field in FieldDefinitions)
        {
            AxisFieldUiMode uiMode = GetUiMode(field);
            _uiModeByField[field.Key] = uiMode;

            if (!ShowFieldColumnInMainGrid(field))
                continue;

            int columnWidth = CompactStatusColumnFields.Contains(field.Key) ? 60 : 90;
            bool isAbsEncorder = string.Equals(field.Key, "IsAbsEncorder", StringComparison.Ordinal);
            DataGridViewColumn column = isAbsEncorder
                // IsAbsEncorder 改由侧栏「轴编号」下方的勾选框操作；表格列仅作为每行取值的存储位，隐藏不显示
                ? new DataGridViewCheckBoxColumn
                {
                    Name = $"{field.Key}_Val",
                    HeaderText = field.DisplayName,
                    Width = 110,
                    ValueType = typeof(bool),
                    Visible = false
                }
                : new DataGridViewTextBoxColumn
                {
                    Name = $"{field.Key}_Val",
                    HeaderText = field.DisplayName,
                    Width = columnWidth
                };

            int valueIndex = _grid.Columns.Add(column);
            if (isAbsEncorder)
                _isAbsEncorderColumnIndex = valueIndex;

            _valueColumnIndexByField[field.Key] = valueIndex;
        }

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Padding = new Padding(0, 0, 0, 4)
        };

        // 「实时刷新」范围下拉框已按需求移除：固定为单轴刷新（仅刷新侧栏当前轴的实时字段）
        actions.Controls.AddRange([_btnRefreshData, _btnAddressConfig, _btnEditSpeedProfile, _btnSaveConfig, _btnLoadConfig, _btnImportAxisNumbers, _btnImportTable]);

        BuildMotionSidePanel();

        _gridStorage.Controls.Add(_grid);
        _rootTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, MotionSidePanelOuterWidth));
        _rootTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _rootTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _rootTable.Controls.Add(_motionPanel, 0, 0);
        _rootTable.Controls.Add(_rootTableSpacer, 1, 0);

        Controls.Add(_gridStorage);
        Controls.Add(_rootTable);
        Controls.Add(actions);
        AutoScroll = true;
        _grid.ReadOnly = true;
        _rootTable.SizeChanged += (_, _) => ApplyMotionPanelLayout();
        ApplyMotionPanelLayout();
    }

    private void BuildMotionSidePanel()
    {
        foreach (var b in new[]
                 {
                     _btnPanelJogMinus, _btnPanelJogPlus, _btnPanelHome, _btnPanelStop, _btnPanelAbsReset,
                    _btnPanelEnable, _btnPanelStopAll
                 })
        {
            b.FlatAppearance.BorderSize = 0;
            b.UseVisualStyleBackColor = false;
        }

        _cmbDisplacement.Dock = DockStyle.Fill;
        _cmbDisplacement.Items.AddRange(["0.1", "0.5", "1", "5", "10", "50", "100"]);
        if (_cmbDisplacement.Items.Count > 0)
            _cmbDisplacement.SelectedIndex = 2;

        _cmbManualSpeed.Dock = DockStyle.Fill;
        _cmbManualSpeed.Items.AddRange(["1", "5", "10", "20", "30", "50", "80", "100"]);
        if (_cmbManualSpeed.Items.Count > 0)
            _cmbManualSpeed.SelectedIndex = Math.Min(DefaultManualSpeedComboIndex, _cmbManualSpeed.Items.Count - 1);

        _cmbPanelAxis.Dock = DockStyle.Fill;

        for (int i = 0; i < SidePanelStatusFieldOrder.Length; i++)
        {
            string key = SidePanelStatusFieldOrder[i];
            var rowPanel = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0, 0, 10, 2)
            };
            var nameLbl = new Label
            {
                Text = GetFieldDisplayName(key),
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 2, 4, 0)
            };
            _lblSideStatusDots[i] = new Label
            {
                Text = "●",
                AutoSize = true,
                Font = StatusDotFont,
                ForeColor = Color.Gray,
                Margin = new Padding(0, 0, 0, 0)
            };
            rowPanel.Controls.Add(nameLbl);
            rowPanel.Controls.Add(_lblSideStatusDots[i]);
            _pnlSideBoolStatus.Controls.Add(rowPanel);
        }

        var tlp = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 10,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0)
        };
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84F));
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (int i = 0; i < 10; i++)
            tlp.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        int r = 0;
        tlp.Controls.Add(_lblPanelAxis, 0, r);
        tlp.Controls.Add(_cmbPanelAxis, 1, r++);

        // 同步运动勾选：仅当前轴配置了同步轴时可见（见 UpdateSyncMotionUi）。
        _chkSyncMotion.Margin = new Padding(0, 2, 0, 2);
        tlp.Controls.Add(_chkSyncMotion, 0, r);
        tlp.SetColumnSpan(_chkSyncMotion, 2);
        r++;

        tlp.Controls.Add(_lblSyncAxis, 0, r);
        tlp.Controls.Add(_cmbSyncAxis, 1, r++);

        tlp.Controls.Add(_lblDisplacement, 0, r);
        tlp.Controls.Add(_cmbDisplacement, 1, r++);

        tlp.Controls.Add(_lblManualSpeedSide, 0, r);
        tlp.Controls.Add(_cmbManualSpeed, 1, r++);

        // 绝对值轴（IsAbsEncorder）勾选：紧跟「手动速度」下方
        tlp.Controls.Add(_lblIsAbsEncorder, 0, r);
        tlp.Controls.Add(_chkIsAbsEncorder, 1, r++);

        // 三个实时标签用同一组边距：行与行之间 3+3=6px，间距均匀
        _lblPanelStatus.Margin = new Padding(0, 3, 0, 3);
        tlp.Controls.Add(_lblPanelStatus, 0, r);
        tlp.SetColumnSpan(_lblPanelStatus, 2);
        r++;

        _lblPanelCurrentPos.Margin = new Padding(0, 3, 0, 3);
        tlp.Controls.Add(_lblPanelCurrentPos, 0, r);
        tlp.SetColumnSpan(_lblPanelCurrentPos, 2);
        r++;

        _lblPanelCurrentTorque.Margin = new Padding(0, 3, 0, 3);
        tlp.Controls.Add(_lblPanelCurrentTorque, 0, r);
        tlp.SetColumnSpan(_lblPanelCurrentTorque, 2);
        r++;

        tlp.Controls.Add(_pnlSideBoolStatus, 0, r);
        tlp.SetColumnSpan(_pnlSideBoolStatus, 2);
        r++;

        _motionCommandTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (int i = 0; i < 5; i++)
            _motionCommandTable.RowStyles.Add(new RowStyle(SizeType.Absolute, MotionPanelButtonHeight + MotionPairRowMarginBottom));
        _motionCommandTable.Controls.Add(CreateSymmetricPairRow(_btnPanelStepMinus, _btnPanelStepPlus), 0, 0);
        _motionCommandTable.Controls.Add(CreateSymmetricPairRow(_btnPanelJogMinus, _btnPanelJogPlus), 0, 1);
        _motionCommandTable.Controls.Add(CreateSymmetricPairRow(_btnPanelHome, _btnPanelStop), 0, 2);
        _btnPanelAbsReset.Dock = DockStyle.Fill;
        _btnPanelAbsReset.Margin = new Padding(0, 0, 0, MotionPairRowMarginBottom);
        _motionCommandTable.Controls.Add(_btnPanelAbsReset, 0, 3);
        var enableRow = CreateSymmetricPairRow(_btnPanelEnable, _btnPanelStopAll);
        _motionCommandTable.Controls.Add(enableRow, 0, 4);
        _rootTableSpacer.Controls.Add(_motionCommandTable);
        _motionPanel.Controls.Add(tlp);
    }

    private void ApplyMotionPanelLayout()
    {
        if (_rootTable.ColumnStyles.Count < 2 || _rootTable.ClientSize.Width <= 0)
            return;

        bool compact = _rootTable.ClientSize.Width < 1280 || _rootTable.ClientSize.Height < 360;
        float parameterWidth = compact
            ? Math.Min(MotionSidePanelOuterWidth, _rootTable.ClientSize.Width * 0.55f)
            : MotionSidePanelOuterWidth;
        if (_rootTable.ColumnStyles[0].Width != parameterWidth)
            _rootTable.ColumnStyles[0].Width = parameterWidth;

        int buttonHeight = compact ? 32 : MotionPanelButtonHeight;
        int rowHeight = buttonHeight + MotionPairRowMarginBottom;
        foreach (RowStyle row in _motionCommandTable.RowStyles)
            row.Height = rowHeight;
        foreach (TableLayoutPanel pair in _motionCommandTable.Controls.OfType<TableLayoutPanel>())
            pair.RowStyles[0].Height = buttonHeight;
        _motionCommandTable.Height = rowHeight * 5;
    }

    /// <summary>左右两个按钮各占 50% 宽度，高度一致（避免落在「标签窄列」上导致一大一小）。</summary>
    private TableLayoutPanel CreateSymmetricPairRow(Button left, Button right)
    {
        left.Dock = DockStyle.Fill;
        right.Dock = DockStyle.Fill;
        left.Margin = new Padding(0, 0, 3, 0);
        right.Margin = new Padding(3, 0, 0, 0);
        var row = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(0),
            Margin = new Padding(0, 0, 0, MotionPairRowMarginBottom)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, MotionPanelButtonHeight));
        row.Controls.Add(left, 0, 0);
        row.Controls.Add(right, 1, 0);
        return row;
    }

    private void RebuildPanelAxisCombo(string? preferAxisId = null)
    {
        string? preserve = preferAxisId;
        if (preserve is null && _cmbPanelAxis.SelectedItem is string sel && !string.IsNullOrWhiteSpace(sel))
            preserve = sel;

        _programmaticPanelAxisChange = true;
        try
        {
            _cmbPanelAxis.Items.Clear();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.IsNewRow)
                    continue;
                string id = GetAxisId(row);
                if (!string.IsNullOrWhiteSpace(id))
                    _cmbPanelAxis.Items.Add(id);
            }

            int idx = -1;
            if (!string.IsNullOrWhiteSpace(preserve))
            {
                for (int i = 0; i < _cmbPanelAxis.Items.Count; i++)
                {
                    if (_cmbPanelAxis.Items[i] is string s && string.Equals(s, preserve, StringComparison.Ordinal))
                    {
                        idx = i;
                        break;
                    }
                }
            }

            if (idx < 0 && _cmbPanelAxis.Items.Count > 0)
                idx = 0;

            _cmbPanelAxis.SelectedIndex = idx;
        }
        finally
        {
            _programmaticPanelAxisChange = false;
        }

        UpdatePanelStatusLabel();
        UpdateMotionPanelEnabledState();
        UpdateSyncMotionUi();
        UpdateSidePanelIsAbsEncorder();
    }

    private DataGridViewRow? GetSidePanelAxisRow()
    {
        if (_cmbPanelAxis.SelectedItem is not string axisId || string.IsNullOrWhiteSpace(axisId))
            return null;
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow)
                continue;
            if (string.Equals(GetAxisId(row), axisId, StringComparison.Ordinal))
                return row;
        }
        return null;
    }

    private void UpdatePanelBoolStatusIndicators()
    {
        var row = GetSidePanelAxisRow();
        for (int i = 0; i < SidePanelStatusFieldOrder.Length; i++)
        {
            string key = SidePanelStatusFieldOrder[i];
            Label dot = _lblSideStatusDots[i];
            if (row is null || !_valueColumnIndexByField.TryGetValue(key, out int col) || col < 0 || col >= row.Cells.Count)
            {
                dot.ForeColor = Color.Gray;
                continue;
            }

            DataGridViewCell cell = row.Cells[col];
            string? txt = Convert.ToString(cell.Value, CultureInfo.InvariantCulture)?.Trim();
            if (string.Equals(txt, "ERR", StringComparison.OrdinalIgnoreCase))
            {
                dot.ForeColor = Color.DarkOrange;
                continue;
            }

            if (string.Equals(key, "Alarm", StringComparison.Ordinal))
            {
                dot.ForeColor = cell.Style.ForeColor == Color.Red ? Color.Red : Color.Gray;
                continue;
            }

            dot.ForeColor = cell.Style.ForeColor == Color.LawnGreen ? Color.LawnGreen : Color.Gray;
        }
    }

    private void BindEvents()
    {
        _btnRefreshData.Click += async (_, _) => await RefreshAllAxisDataAsync(includeSpeedProfileFields: true);
        _btnEditSpeedProfile.Click += async (_, _) => await OnEditSpeedProfileClickedAsync();
        _btnSaveConfig.Click += async (_, _) => await OnSaveConfigClickedAsync();
        _btnLoadConfig.Click += async (_, _) =>
        {
            LoadConfigFromFile();
            await RefreshAllAxisDataAsync();
        };
        _btnImportTable.Click += (_, _) => ImportAxisAddressTableFromExcel();
        _btnImportAxisNumbers.Click += (_, _) => ImportAxisNumbersFromExcel();
        _cmbPanelAxis.SelectedIndexChanged += (_, _) =>
        {
            if (_programmaticPanelAxisChange)
                return;
            UpdatePanelStatusLabel();
            UpdateMotionPanelEnabledState();
            UpdateSyncMotionUi();
            UpdateSidePanelIsAbsEncorder();
        };
        _chkIsAbsEncorder.CheckedChanged += (_, _) =>
        {
            if (_programmaticIsAbsEncorder)
                return;

            DataGridViewRow? row = GetSidePanelAxisRow();
            if (row is null)
                return;

            if (!_canModify)
            {
                UpdateSidePanelIsAbsEncorder(); // 无权限时回滚显示
                return;
            }

            if (_isAbsEncorderColumnIndex >= 0 && _isAbsEncorderColumnIndex < row.Cells.Count)
                row.Cells[_isAbsEncorderColumnIndex].Value = _chkIsAbsEncorder.Checked;

            SaveConfigToFile();
            UpdateMotionPanelEnabledState();
            LogAction?.Invoke($"轴[{GetAxisId(row)}] 绝对值轴 = {_chkIsAbsEncorder.Checked}。");
        };
        _cmbSyncAxis.SelectedIndexChanged += (_, _) =>
        {
            if (_programmaticSyncAxisChange)
                return;

            DataGridViewRow? side = GetSidePanelAxisRow();
            if (side is null || string.IsNullOrWhiteSpace(GetAxisId(side)))
                return;

            string axisId = GetAxisId(side);
            string syncId = _cmbSyncAxis.SelectedIndex > 0 && _cmbSyncAxis.SelectedItem is string s
                ? s
                : string.Empty;

            _syncAxisByAxisId[axisId] = syncId;
            UpdateSyncMotionUi();
            LogAction?.Invoke(string.IsNullOrWhiteSpace(syncId)
                ? $"轴[{axisId}] 已清除同步轴。"
                : $"轴[{axisId}] 同步轴设为 [{syncId}]。");
            SaveConfigToFile();
        };
        _chkSyncMotion.CheckedChanged += (_, _) =>
        {
            if (_programmaticSyncMotionCheck)
                return;

            var rows = GetMotionSyncRows();
            string desc = string.Join(" + ", rows.Select(GetAxisId));
            LogAction?.Invoke(_chkSyncMotion.Checked
                ? $"同步运动开启: {desc}"
                : "同步运动关闭: 仅控制当前轴。");
        };
        // 「地址配置」移到工具栏后：对侧栏当前轴（或表格选中行）打开配置弹框
        _btnAddressConfig.Click += (_, _) =>
        {
            DataGridViewRow? row = GetSidePanelAxisRow()
                ?? (_grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0] : null);
            if (row is null)
            {
                LogAction?.Invoke("请先选择要配置地址的轴。");
                return;
            }

            ShowAddressConfigDialog(row);
        };
        _grid.CellBeginEdit += (_, e) =>
        {
            bool canEditIsAbsEncorder = _canModify
                && e.ColumnIndex == _isAbsEncorderColumnIndex;
            if (!canEditIsAbsEncorder)
                e.Cancel = true;
        };
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell?.ColumnIndex == _isAbsEncorderColumnIndex)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += (_, e) =>
        {
            if (_suspendAutoSave || !_canModify || e.RowIndex < 0)
                return;

            SaveConfigToFile();
            if (e.ColumnIndex == _isAbsEncorderColumnIndex)
                UpdateMotionPanelEnabledState();
        };
        _grid.SelectionChanged += (_, _) => _ = OnGridSelectionChangedAsync();

        _btnPanelStepMinus.Click += async (_, _) => await ExecuteRelMoveSignedAsync(-1);
        _btnPanelStepPlus.Click += async (_, _) => await ExecuteRelMoveSignedAsync(1);
        _btnPanelJogMinus.MouseDown += (_, e) => OnJogMouseDown(_btnPanelJogMinus, e, "JogMinus");
        _btnPanelJogMinus.MouseUp += (_, e) => OnJogMouseUp(_btnPanelJogMinus, e, "JogMinus");
        _btnPanelJogMinus.LostFocus += (_, _) => _ = OnJogCancelAsync(_btnPanelJogMinus, "JogMinus");
        _btnPanelJogPlus.MouseDown += (_, e) => OnJogMouseDown(_btnPanelJogPlus, e, "JogPlus");
        _btnPanelJogPlus.MouseUp += (_, e) => OnJogMouseUp(_btnPanelJogPlus, e, "JogPlus");
        _btnPanelJogPlus.LostFocus += (_, _) => _ = OnJogCancelAsync(_btnPanelJogPlus, "JogPlus");
        _btnPanelHome.Click += async (_, _) => await PulseBoolFieldOnMotionSyncRowsAsync("AxisHome");
        // STOP 与 JOG/Step/HOME 保持一致：勾选「同步运动」（或表格多选 ≥2 行）时对同步行各发一次 Stop 脉冲，
        // 否则退化为侧栏当前轴的单行脉冲。
        _btnPanelStop.Click += async (_, _) => await PulseBoolFieldOnMotionSyncRowsAsync("Stop");
        _btnPanelAbsReset.Click += async (_, _) =>
        {
            if (AbsResetPasswordConfirmRequest is { } confirm && !confirm())
                return;
            await PulseBoolFieldOnSelectedRowAsync("AbsReset");
        };
        _btnPanelEnable.Click += async (_, _) => await InvertAxisEnableOnSelectedRowAsync();
        _btnPanelStopAll.Click += async (_, _) => await PulseStopOnAllAxesAsync();

        _cmbManualSpeed.TextChanged += (_, _) =>
        {
            if (_programmaticManualSpeedChange)
                return;
            _manualSpeedDebounce.Stop();
            _manualSpeedDebounce.Start();
        };

        _manualSpeedDebounce.Tick += async (_, _) =>
        {
            _manualSpeedDebounce.Stop();
            await CommitManualSpeedFromComboAsync();
        };

        _autoRefreshTimer.Tick += async (_, _) => await HandleAutoRefreshTickAsync();
        _autoRefreshTimer.Start();
    }

    private async Task HandleAutoRefreshTickAsync()
    {
        if (_autoRefreshInProgress || IsDisposed || !IsHandleCreated || !Visible || _grid.IsCurrentCellInEditMode)
            return;

        _autoRefreshInProgress = true;
        try
        {
            if (_autoRefreshScope == AutoRefreshScope.Global)
            {
                await RefreshAllAxisDataAsync();
            }
            else
            {
                DataGridViewRow? row = GetSidePanelAxisRow();
                if (row is not null)
                {
                    await RefreshSingleRowRealtimeFieldsAsync(row);
                    UpdateMotionPanelEnabledState();
                    await SyncSidePanelFromPlcAsync(row);
                }
            }
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"轴表自动刷新失败: {ex.Message}");
        }
        finally
        {
            _autoRefreshInProgress = false;
            UpdatePanelStatusLabel();
        }
    }

    private async Task OnGridSelectionChangedAsync()
    {
        UpdatePanelStatusLabel();
        UpdateMotionPanelEnabledState();
        await SyncSidePanelFromPlcAsync();
    }

    private void UpdatePanelStatusLabel()
    {
        UpdatePanelCurrentSpeedLabel();
        UpdatePanelCurrentPositionLabel();
        UpdatePanelCurrentTorqueLabel();
        UpdatePanelBoolStatusIndicators();
    }

    /// <summary>侧栏第一行：当前速度，与表格 CurrentSpeed 列一致。</summary>
    private void UpdatePanelCurrentSpeedLabel()
    {
        DataGridViewRow? row = GetSidePanelAxisRow();
        if (row is null
            || !_valueColumnIndexByField.TryGetValue("CurrentSpeed", out int col)
            || col < 0
            || col >= row.Cells.Count)
        {
            _lblPanelStatus.Text = "当前速度: —";
            return;
        }

        string raw = Convert.ToString(row.Cells[col].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(raw))
        {
            _lblPanelStatus.Text = "当前速度: —";
            return;
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            _lblPanelStatus.Text = $"当前速度: {v.ToString("F3", CultureInfo.InvariantCulture)}";
        else
            _lblPanelStatus.Text = $"当前速度: {raw}";
    }

    /// <summary>侧栏「当前位置」：取自当前主行的「当前位置」列（与表格 CurrentPos 一致）。</summary>
    private void UpdatePanelCurrentPositionLabel()
    {
        DataGridViewRow? row = GetSidePanelAxisRow();
        if (row is null
            || !_valueColumnIndexByField.TryGetValue("CurrentPos", out int col)
            || col < 0
            || col >= row.Cells.Count)
        {
            _lblPanelCurrentPos.Text = "当前位置: —";
            return;
        }

        string raw = Convert.ToString(row.Cells[col].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(raw))
        {
            _lblPanelCurrentPos.Text = "当前位置: —";
            return;
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double pos))
            _lblPanelCurrentPos.Text = $"当前位置: {pos.ToString("F3", CultureInfo.InvariantCulture)}";
        else
            _lblPanelCurrentPos.Text = $"当前位置: {raw}";
    }

    /// <summary>侧栏「当前扭矩」：取自当前主行的「当前扭矩」列（与表格 CurrentTorque 一致）。</summary>
    private void UpdatePanelCurrentTorqueLabel()
    {
        DataGridViewRow? row = GetSidePanelAxisRow();
        if (row is null
            || !_valueColumnIndexByField.TryGetValue("CurrentTorque", out int col)
            || col < 0
            || col >= row.Cells.Count)
        {
            _lblPanelCurrentTorque.Text = "当前扭矩: —";
            return;
        }

        string raw = Convert.ToString(row.Cells[col].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(raw))
        {
            _lblPanelCurrentTorque.Text = "当前扭矩: —";
            return;
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double torque))
            _lblPanelCurrentTorque.Text = $"当前扭矩: {torque.ToString("F3", CultureInfo.InvariantCulture)}";
        else
            _lblPanelCurrentTorque.Text = $"当前扭矩: {raw}";
    }

    /// <summary>按表头与各轴编号文本测量宽度，自动调整「轴编号」列宽。</summary>
    private void ResizeAxisIdColumnToFitContent()
    {
        if (_grid.IsDisposed)
            return;

        DataGridViewColumn? axisCol = _grid.Columns
            .Cast<DataGridViewColumn>()
            .FirstOrDefault(c => string.Equals(c.Name, "AxisId", StringComparison.Ordinal));
        if (axisCol is null)
            return;
        Font font = _grid.Font;
        int maxPx = TextRenderer.MeasureText(axisCol.HeaderText, font).Width;

        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow)
                continue;

            string text = Convert.ToString(row.Cells[axisCol.Index].Value, CultureInfo.InvariantCulture) ?? string.Empty;
            int w = TextRenderer.MeasureText(text, font).Width;
            if (w > maxPx)
                maxPx = w;
        }

        const int padding = 28;
        axisCol.Width = Math.Clamp(maxPx + padding, axisCol.MinimumWidth, 480);
    }

    /// <summary>按表格行号排序的已选数据行（不含新行占位）。</summary>
    private List<DataGridViewRow> GetOrderedSelectedDataRows()
    {
        var rows = new List<DataGridViewRow>();
        foreach (DataGridViewRow r in _grid.SelectedRows)
        {
            if (!r.IsNewRow && r.Index >= 0)
                rows.Add(r);
        }

        rows.Sort((x, y) => x.Index.CompareTo(y.Index));
        return rows;
    }

    /// <summary>
    /// 侧栏 JOG / Step / HOME 的目标轴。
    /// 当前轴配置了同步轴且「同步运动」已勾选时，返回「当前轴 + 同步轴」两行（双轴同时运动）；
    /// 否则表格多选 ≥2 行时按「多轴同步」处理；再回退到侧栏「轴编号」选中的单行。
    /// </summary>
    private IReadOnlyList<DataGridViewRow> GetMotionSyncRows()
    {
        DataGridViewRow? side = GetSidePanelAxisRow();
        if (side is not null && _chkSyncMotion.Visible && _chkSyncMotion.Checked)
        {
            DataGridViewRow? syncRow = GetSyncAxisRowFor(side);
            if (syncRow is not null)
                return new[] { side, syncRow };
        }

        List<DataGridViewRow> selectedRows = GetOrderedSelectedDataRows();
        if (selectedRows.Count > 1)
            return selectedRows;

        return side is null ? Array.Empty<DataGridViewRow>() : new[] { side };
    }

    /// <summary>取侧栏当前轴已配置的同步轴编号；未配置返回 null。</summary>
    private string? GetConfiguredSyncAxisId(DataGridViewRow? side)
    {
        if (side is null)
            return null;

        string axisId = GetAxisId(side);
        if (string.IsNullOrWhiteSpace(axisId) || !_syncAxisByAxisId.TryGetValue(axisId, out string? syncId))
            return null;

        return string.IsNullOrWhiteSpace(syncId) ? null : syncId;
    }

    /// <summary>
    /// 写入「轴 → 同步轴」映射：忽略空白/自指；若目标轴被删除后的悬空引用由调用方清理。
    /// </summary>
    private void StoreSyncAxisMapping(string? axisId, string? syncAxisId)
    {
        if (string.IsNullOrWhiteSpace(axisId))
            return;

        string trimmed = syncAxisId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed) || string.Equals(trimmed, axisId.Trim(), StringComparison.Ordinal))
            _syncAxisByAxisId[axisId.Trim()] = string.Empty;
        else
            _syncAxisByAxisId[axisId.Trim()] = trimmed;
    }

    /// <summary>按 side 行已配置的同步轴编号定位表格行；找不到（含指向自身）返回 null。</summary>
    private DataGridViewRow? GetSyncAxisRowFor(DataGridViewRow side)
    {
        string? syncId = GetConfiguredSyncAxisId(side);
        if (string.IsNullOrWhiteSpace(syncId))
            return null;

        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow || ReferenceEquals(row, side))
                continue;
            if (string.Equals(GetAxisId(row), syncId, StringComparison.Ordinal))
                return row;
        }

        return null;
    }

    /// <summary>
    /// 依据侧栏当前轴刷新「同步运动」勾选框可见性与「同步轴」下拉框内容。
    /// 未配置同步轴时勾选框隐藏并强制取消勾选；切换轴后勾选状态复位（不跨轴保留）。
    /// </summary>
    private void UpdateSyncMotionUi()
    {
        DataGridViewRow? side = GetSidePanelAxisRow();
        string axisId = side is null ? string.Empty : GetAxisId(side);

        _programmaticSyncAxisChange = true;
        try
        {
            _cmbSyncAxis.Items.Clear();
            _cmbSyncAxis.Items.Add(SyncAxisNoneItem);

            if (side is not null && !string.IsNullOrWhiteSpace(axisId))
            {
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    if (row.IsNewRow || ReferenceEquals(row, side))
                        continue;
                    string id = GetAxisId(row);
                    if (!string.IsNullOrWhiteSpace(id))
                        _cmbSyncAxis.Items.Add(id);
                }

                string? configured = GetConfiguredSyncAxisId(side);
                int idx = 0;
                if (!string.IsNullOrWhiteSpace(configured))
                {
                    for (int i = 1; i < _cmbSyncAxis.Items.Count; i++)
                    {
                        if (_cmbSyncAxis.Items[i] is string s && string.Equals(s, configured, StringComparison.Ordinal))
                        {
                            idx = i;
                            break;
                        }
                    }
                }

                if (_cmbSyncAxis.SelectedIndex != idx)
                    _cmbSyncAxis.SelectedIndex = idx;
                _cmbSyncAxis.Enabled = _canModify;
            }
            else
            {
                if (_cmbSyncAxis.SelectedIndex != 0)
                    _cmbSyncAxis.SelectedIndex = 0;
                _cmbSyncAxis.Enabled = false;
            }
        }
        finally
        {
            _programmaticSyncAxisChange = false;
        }

        bool hasSync = GetConfiguredSyncAxisId(side) is not null;
        _chkSyncMotion.Visible = hasSync;
        _programmaticSyncMotionCheck = true;
        _chkSyncMotion.Checked = false;
        _programmaticSyncMotionCheck = false;
    }

    /// <summary>编辑对话框等：优先侧栏所选轴，否则沿用表格当前选中行。</summary>
    private DataGridViewRow? GetSelectedAxisRow()
    {
        DataGridViewRow? side = GetSidePanelAxisRow();
        if (side is not null)
            return side;

        var ordered = GetOrderedSelectedDataRows();
        if (ordered.Count > 0)
            return ordered[0];

        return _grid.CurrentRow is { IsNewRow: false, Index: >= 0 } cur ? cur : null;
    }

    private void UpdateMotionPanelEnabledState()
    {
        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        bool plcOk = plc is { IsConnected: true };
        bool allow = _canModify && plcOk;
        bool rowOk = GetMotionSyncRows().Count > 0;
        bool selectedIsAbsEncorder = GetSidePanelAxisRow() is DataGridViewRow selectedRow && IsAbsEncorderRow(selectedRow);

        foreach (var b in new[]
                 {
                     _btnPanelStepMinus, _btnPanelStepPlus, _btnPanelJogMinus, _btnPanelJogPlus, _btnPanelHome, _btnPanelStop,
                     _btnPanelAbsReset, _btnPanelEnable
                 })
            b.Enabled = allow && rowOk;
        _btnPanelStopAll.Enabled = _canModify && plcOk && _grid.Rows.Count > 0;

        _btnPanelHome.Visible = !selectedIsAbsEncorder;
        _btnPanelAbsReset.Visible = selectedIsAbsEncorder;

        _cmbManualSpeed.Enabled = allow && rowOk;
        _cmbDisplacement.Enabled = allow && rowOk;

        if (!rowOk || !plcOk)
        {
            _programmaticManualSpeedChange = true;
            _cmbManualSpeed.Text = string.Empty;
            _programmaticManualSpeedChange = false;
        }
        else if (_cmbManualSpeed.Items.Count > 0
                 && _cmbManualSpeed.SelectedIndex < 0
                 && string.IsNullOrWhiteSpace(_cmbManualSpeed.Text))
        {
            // 清空后侧栏不从 PLC 回填手动速度；恢复可用时补回默认档位，避免调试页打开后下拉框空白。
            _programmaticManualSpeedChange = true;
            _cmbManualSpeed.SelectedIndex = Math.Min(DefaultManualSpeedComboIndex, _cmbManualSpeed.Items.Count - 1);
            _programmaticManualSpeedChange = false;
        }
    }

    /// <summary>按侧栏当前轴回填 IsAbsEncorder 勾选框；无轴时禁用。</summary>
    private void UpdateSidePanelIsAbsEncorder()
    {
        DataGridViewRow? row = GetSidePanelAxisRow();
        _programmaticIsAbsEncorder = true;
        try
        {
            _chkIsAbsEncorder.Checked = row is not null && IsAbsEncorderRow(row);
            _chkIsAbsEncorder.Enabled = _canModify && row is not null;
        }
        finally
        {
            _programmaticIsAbsEncorder = false;
        }
    }

    private bool IsAbsEncorderRow(DataGridViewRow row)
    {
        if (_isAbsEncorderColumnIndex < 0 || _isAbsEncorderColumnIndex >= row.Cells.Count)
            return false;

        object? value = row.Cells[_isAbsEncorderColumnIndex].Value;
        return value is bool b && b;
    }

    private async Task PulseBoolFieldOnSelectedRowAsync(string fieldKey)
    {
        var row = GetSelectedAxisRow();
        if (row is null)
            return;

        await PulseBoolFieldOnRowAsync(row, fieldKey);
    }

    /// <summary>JOG/Step/HOME 同步：对 <see cref="GetMotionSyncRows"/> 中的每一行各发一次脉冲。</summary>
    private async Task PulseBoolFieldOnMotionSyncRowsAsync(string fieldKey)
    {
        var rows = GetMotionSyncRows();
        if (rows.Count == 0)
            return;

        await Task.WhenAll(rows.Select(r => PulseBoolFieldOnRowAsync(r, fieldKey)));
    }

    private async Task InvertAxisEnableOnSelectedRowAsync()
    {
        var row = GetSelectedAxisRow();
        if (row is null)
            return;

        await InvertAxisEnableOnRowAsync(row);
    }

    private async Task PulseStopOnAllAxesAsync()
    {
        if (!_canModify)
            return;

        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
            return;

        var targets = new List<(string axisId, string address)>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow)
                continue;

            string axisId = Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture)?.Trim() ?? "?";
            var addresses = row.Tag as Dictionary<string, string> ?? new Dictionary<string, string>();
            if (!addresses.TryGetValue("Stop", out string? address) || string.IsNullOrWhiteSpace(address))
            {
                LogAction?.Invoke($"轴[{axisId}] Stop 未配置地址。");
                continue;
            }

            targets.Add((axisId, address.Trim()));
        }

        if (targets.Count == 0)
            return;

        foreach (var target in targets)
        {
            try
            {
                await plc.WriteBoolAsync(target.address, true);
            }
            catch (Exception ex)
            {
                LogAction?.Invoke($"轴[{target.axisId}] Stop 写入True失败 {target.address}: {ex.Message}");
            }
        }

        // 与 STOP 按钮同一脉宽，统一由 BoolPulseHoldMs 控制
        await Task.Delay(BoolPulseHoldMs);

        foreach (var target in targets)
        {
            try
            {
                await plc.WriteBoolAsync(target.address, false);
            }
            catch (Exception ex)
            {
                LogAction?.Invoke($"轴[{target.axisId}] Stop 写入False失败 {target.address}: {ex.Message}");
            }
        }

        LogManualButton("轴停止运动", targets.Select(t => t.axisId), detail: $"全部轴 Stop 脉冲（{targets.Count} 轴）");
    }

    private void OnJogMouseDown(Button btn, MouseEventArgs e, string fieldKey)
    {
        if (e.Button != MouseButtons.Left)
            return;

        btn.Capture = true;
        _ = WritePanelBoolFieldAsync(fieldKey, true);
    }

    private void OnJogMouseUp(Button btn, MouseEventArgs e, string fieldKey)
    {
        if (e.Button != MouseButtons.Left)
            return;

        _ = JogMouseUpReleaseAsync(btn, fieldKey);
    }

    private async Task JogMouseUpReleaseAsync(Button btn, string fieldKey)
    {
        try
        {
            await WritePanelBoolFieldAsync(fieldKey, false);
        }
        finally
        {
            if (btn.Capture)
                btn.Capture = false;
        }
    }

    private async Task OnJogCancelAsync(Button btn, string fieldKey)
    {
        await WritePanelBoolFieldAsync(fieldKey, false);
        if (btn.Capture)
            btn.Capture = false;
    }

    /// <summary>侧栏 JOG：直接写 BOOL，不按位取反。</summary>
    private async Task WritePanelBoolFieldAsync(string fieldKey, bool value)
    {
        if (!_canModify)
            return;

        if (!BoolControlFields.Contains(fieldKey))
            return;

        var rows = GetMotionSyncRows();
        if (rows.Count == 0)
            return;

        FieldDef? field = FieldDefinitions.FirstOrDefault(f => f.Key == fieldKey);
        if (field is null)
            return;

        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
            return;

        foreach (var row in rows)
        {
            var addresses = row.Tag as Dictionary<string, string> ?? new Dictionary<string, string>();
            if (!addresses.TryGetValue(fieldKey, out string? address) || string.IsNullOrWhiteSpace(address))
            {
                string axisId = Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture)?.Trim() ?? "?";
                LogAction?.Invoke($"轴[{axisId}] {field.DisplayName} 未配置地址。");
                continue;
            }

            try
            {
                await plc.WriteBoolAsync(address.Trim(), value);
            }
            catch (Exception ex)
            {
                LogAction?.Invoke($"{field.DisplayName} 写入 {value} 失败 {address}: {ex.Message}");
            }
        }

        // JOG 只在按下（value=true）时记一条，松开复位不重复记录
        if (value)
        {
            LogManualButton(
                field.DisplayName,
                rows.Select(r => Convert.ToString(r.Cells[0].Value, CultureInfo.InvariantCulture) ?? "?"),
                detail: "按下点动（松开自动复位）");
        }
    }

    private async Task PulseBoolFieldOnRowAsync(DataGridViewRow row, string fieldKey)
    {
        if (!_canModify)
            return;

        if (!PulseTriggerFieldKeys.Contains(fieldKey))
            return;

        FieldDef? field = FieldDefinitions.FirstOrDefault(f => f.Key == fieldKey);
        if (field is null)
            return;

        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
            return;

        var addresses = row.Tag as Dictionary<string, string> ?? new Dictionary<string, string>();
        addresses.TryGetValue(fieldKey, out string? address);
        address = address?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(address))
        {
            LogAction?.Invoke($"{field.DisplayName} 未配置地址。");
            return;
        }

        try
        {
            await plc.WriteBoolAsync(address, true);
            await Task.Delay(BoolPulseHoldMs);
            await plc.WriteBoolAsync(address, false);
            await RefreshSingleRowDataAsync(row);
            await SyncSidePanelFromPlcAsync(row);
            LogManualButton(
                field.DisplayName,
                [Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture) ?? "?"],
                detail: $"脉冲写入 {address}",
                address: address);
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"{field.DisplayName} 脉冲写入失败 {address}: {ex.Message}");
            LogManualButton(
                field.DisplayName,
                [Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture) ?? "?"],
                detail: $"写入失败：{ex.Message}",
                address: address);
        }
    }

    /// <summary>轴使能：读取当前位，取反后写回。</summary>
    private async Task InvertAxisEnableOnRowAsync(DataGridViewRow row)
    {
        if (!_canModify)
            return;

        const string fieldKey = "AxisEnable";
        FieldDef? field = FieldDefinitions.FirstOrDefault(f => f.Key == fieldKey);
        if (field is null)
            return;

        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
            return;

        var addresses = row.Tag as Dictionary<string, string> ?? new Dictionary<string, string>();
        addresses.TryGetValue(fieldKey, out string? address);
        address = address?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(address))
        {
            LogAction?.Invoke($"{field.DisplayName} 未配置地址。");
            return;
        }

        try
        {
            bool current = await plc.ReadBoolAsync(address);
            bool next = !current;
            await plc.WriteBoolAsync(address, next);
            await RefreshSingleRowDataAsync(row);
            await SyncSidePanelFromPlcAsync(row);
            LogManualButton(
                "使能切换",
                [Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture) ?? "?"],
                detail: next ? "轴使能 ON" : "轴使能 OFF",
                address: address);
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"{field.DisplayName} 写入失败 {address}: {ex.Message}");
            LogManualButton(
                "使能切换",
                [Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture) ?? "?"],
                detail: $"写入失败：{ex.Message}",
                address: address);
        }
    }

    /// <summary>
    /// Step−/Step+：相对运动。单轴时行为与原来一致；
    /// 多轴（表格多选 ≥2 行）时按「先写位移 → 统一触发 → 统一复位」分阶段执行，
    /// 避免各轴各自跑完整个序列造成启动时刻漂移，使多轴近似同步启停。
    /// </summary>
    private async Task ExecuteRelMoveSignedAsync(int sign)
    {
        if (sign != 1 && sign != -1)
            return;

        if (!_canModify)
            return;

        var rows = GetMotionSyncRows();
        if (rows.Count == 0)
            return;

        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
            return;

        if (!TryParseDisplacementMagnitude(out double magnitude, out string? err))
        {
            LogAction?.Invoke(err ?? "位移无效。");
            return;
        }

        if (magnitude <= 0)
        {
            LogAction?.Invoke("位移量须大于 0。");
            return;
        }

        double signedDistance = sign * magnitude;

        // 阶段 0：先校验所有目标轴地址，避免出现「部分轴已走、部分轴报错」的中途状态
        var targets = new List<(DataGridViewRow Row, string AxisId, string RelAddr, string DistAddr)>();
        foreach (DataGridViewRow row in rows)
        {
            var addresses = row.Tag as Dictionary<string, string> ?? new Dictionary<string, string>();
            string axisId = Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture)?.Trim() ?? "?";

            if (!addresses.TryGetValue("RelMove", out string? relAddr) || string.IsNullOrWhiteSpace(relAddr))
            {
                LogAction?.Invoke($"轴[{axisId}] RelMove 未配置地址，本次已跳过该轴。");
                continue;
            }

            if (!addresses.TryGetValue("RelMoveDistance", out string? distAddr) || string.IsNullOrWhiteSpace(distAddr))
            {
                LogAction?.Invoke($"轴[{axisId}] 相对位移量(RelMoveDistance) 未配置地址，本次已跳过该轴。");
                continue;
            }

            targets.Add((row, axisId, relAddr.Trim(), distAddr.Trim()));
        }

        if (targets.Count == 0)
            return;

        // 阶段 1：给所有轴写相对位移量（此时均未触发运动）
        var armed = new List<(DataGridViewRow Row, string AxisId, string RelAddr)>();
        foreach (var target in targets)
        {
            try
            {
                await plc.WriteDoubleAsync(target.DistAddr, signedDistance);
                armed.Add((target.Row, target.AxisId, target.RelAddr));
            }
            catch (Exception ex)
            {
                LogAction?.Invoke($"轴[{target.AxisId}] 写相对位移量失败: {ex.Message}");
            }
        }

        if (armed.Count == 0)
            return;

        // 阶段 2：统一置位 RelMove，各轴在同一批写入内先后触发
        foreach (var target in armed)
        {
            try
            {
                await plc.WriteBoolAsync(target.RelAddr, true);
            }
            catch (Exception ex)
            {
                LogAction?.Invoke($"轴[{target.AxisId}] RelMove 触发失败: {ex.Message}");
            }
        }

        // 阶段 3：保持脉宽后统一复位
        await Task.Delay(BoolPulseHoldMs);
        foreach (var target in armed)
        {
            try
            {
                await plc.WriteBoolAsync(target.RelAddr, false);
            }
            catch (Exception ex)
            {
                LogAction?.Invoke($"轴[{target.AxisId}] RelMove 复位失败: {ex.Message}");
            }
        }

        // 阶段 4：回读刷新
        foreach (var target in armed)
            await RefreshSingleRowDataAsync(target.Row);

        await SyncSidePanelFromPlcAsync(GetSelectedAxisRow());

        LogManualButton(
            sign > 0 ? "Step+" : "Step−",
            armed.Select(a => a.AxisId),
            detail: $"相对位移 {(signedDistance >= 0 ? "+" : string.Empty)}{signedDistance:0.###}");
    }

    private bool TryParseDisplacementMagnitude(out double magnitude, out string? error)
    {
        magnitude = 0;
        error = null;
        string text = (_cmbDisplacement.SelectedItem ?? _cmbDisplacement.Text)?.ToString()?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "请选择或输入位移量。";
            return false;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out magnitude))
            return true;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out magnitude))
            return true;

        error = $"无法解析位移: {text}";
        return false;
    }

    private async Task RefreshSingleRowDataAsync(DataGridViewRow row)
    {
        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
            return;

        string axisId = Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture) ?? string.Empty;
        var addresses = row.Tag as Dictionary<string, string> ?? new Dictionary<string, string>();

        var mbBoolRequests = new List<FieldReadRequest>();
        var mxBoolRequests = new List<FieldReadRequest>();
        var mdRealRequests = new List<FieldReadRequest>();
        var fallbackRequests = new List<FieldReadRequest>();

        foreach (var field in FieldDefinitions)
        {
            if (!_valueColumnIndexByField.TryGetValue(field.Key, out int colIndex))
                continue;

            if (string.Equals(field.Key, "IsAbsEncorder", StringComparison.Ordinal))
                continue;
            if (IsSpeedProfileField(field.Key))
                continue;

            addresses.TryGetValue(field.Key, out string? address);
            address = address?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(address))
            {
                SetFieldDisplay(row.Cells[colIndex], field, null, null);
                continue;
            }

            var request = BuildFieldReadRequest(row, row.Cells[colIndex], field, address);
            if (request is null)
                continue;

            if (request.Field.FieldType == AxisMotionFieldType.Bool && request.MemType == "MB")
                mbBoolRequests.Add(request);
            else if (request.Field.FieldType == AxisMotionFieldType.Bool && request.MemType == "MX")
                mxBoolRequests.Add(request);
            else if (request.Field.FieldType == AxisMotionFieldType.Real && request.MemType == "MD")
                mdRealRequests.Add(request);
            else
                fallbackRequests.Add(request);
        }

        // 使用已有的批量读取函数批量读取并更新单元格，减少与 PLC 的往返次数
        await Task.WhenAll(
            ReadMbBoolRequestsAsync(plc, mbBoolRequests),
            ReadMxBoolRequestsAsync(plc, mxBoolRequests),
            ReadMdRealRequestsAsync(plc, mdRealRequests),
            ReadFallbackRequestsAsync(plc, fallbackRequests)
        );
    }

    /// <summary>仅刷新单行上的实时状态/反馈字段（9 个地址），每个字段单独按地址单点读取；读完后一次性写回表格避免多次重绘。</summary>
    private async Task RefreshSingleRowRealtimeFieldsAsync(DataGridViewRow row)
    {
        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
            return;

        string axisId = Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture) ?? string.Empty;
        var addresses = row.Tag as Dictionary<string, string> ?? new Dictionary<string, string>();

        var updates = new List<(FieldDef Field, int ColIndex, object? Value, string? Error)>();

        foreach (string fieldKey in SingleAxisRealtimeFieldKeys)
        {
            FieldDef? field = FieldDefinitions.FirstOrDefault(f => string.Equals(f.Key, fieldKey, StringComparison.Ordinal));
            if (field is null)
                continue;
            if (!_valueColumnIndexByField.TryGetValue(field.Key, out int colIndex))
                continue;

            addresses.TryGetValue(field.Key, out string? address);
            address = address?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(address))
            {
                updates.Add((field, colIndex, null, null));
                continue;
            }

            try
            {
                object value = await ReadFieldValueAsync(plc, field.FieldType, address);
                updates.Add((field, colIndex, value, null));
            }
            catch (Exception ex)
            {
                updates.Add((field, colIndex, null, "ERR"));
                LogAction?.Invoke($"轴[{axisId}] {field.DisplayName} 读取失败 {address}: {ex.Message}");
            }
        }

        _grid.SuspendLayout();
        try
        {
            foreach (var u in updates)
                SetFieldDisplay(row.Cells[u.ColIndex], u.Field, u.Value, u.Error);
        }
        finally
        {
            _grid.ResumeLayout(performLayout: true);
        }
    }

    private async Task SyncSidePanelFromPlcAsync(DataGridViewRow? row = null)
    {
        // 手动速度下拉框只负责写入 PLC，不从 PLC 反读覆盖用户选择。
        await Task.CompletedTask;
    }

    private async Task CommitManualSpeedFromComboAsync()
    {
        if (!_canModify)
            return;

        var rows = GetMotionSyncRows();
        if (rows.Count == 0)
            return;

        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
            return;

        string text = _cmbManualSpeed.Text.Trim();
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            && !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            LogAction?.Invoke($"手动速度格式错误: {text}");
            return;
        }

        foreach (var row in rows)
        {
            var addresses = row.Tag as Dictionary<string, string> ?? new Dictionary<string, string>();
            if (!addresses.TryGetValue("ManualSpeed", out string? addr) || string.IsNullOrWhiteSpace(addr))
                continue;

            string axisId = Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture)?.Trim() ?? "?";
            try
            {
                await plc.WriteDoubleAsync(addr.Trim(), value);
            }
            catch (Exception ex)
            {
                LogAction?.Invoke($"轴[{axisId}] 手动速度写入失败: {ex.Message}");
            }
        }

        LogManualButton(
            "手动速度",
            rows.Select(r => Convert.ToString(r.Cells[0].Value, CultureInfo.InvariantCulture) ?? "?"),
            detail: $"写入手动速度 {value:0.###}");
    }

    public void AddAxis(string? axisId = null)
    {
        if (!_canAddDelete)
            return;

        int next = _grid.Rows.Count + 1;
        int rowIndex = _grid.Rows.Add(axisId ?? $"Axis{next}");
        _grid.Rows[rowIndex].Tag = new Dictionary<string, string>();
        if (_isAbsEncorderColumnIndex >= 0)
            _grid.Rows[rowIndex].Cells[_isAbsEncorderColumnIndex].Value = false;
        string addedId = GetAxisId(_grid.Rows[rowIndex]);
        RebuildPanelAxisCombo(addedId);
        ResizeAxisIdColumnToFitContent();
        SaveConfigToFile();
    }

    public void RemoveCurrentAxis()
    {
        if (!_canAddDelete)
            return;

        if (_grid.SelectedRows.Count == 0)
            return;

        string removedId = GetAxisId(_grid.SelectedRows[0]);
        _grid.Rows.RemoveAt(_grid.SelectedRows[0].Index);

        // 清理被删轴的同步映射，以及其它轴指向它的悬空引用。
        _syncAxisByAxisId.Remove(removedId);
        foreach (string key in _syncAxisByAxisId
                     .Where(kv => string.Equals(kv.Value, removedId, StringComparison.Ordinal))
                     .Select(kv => kv.Key)
                     .ToList())
        {
            _syncAxisByAxisId[key] = string.Empty;
        }

        RebuildPanelAxisCombo();
        ResizeAxisIdColumnToFitContent();
        SaveConfigToFile();
    }

    public void SetAxes(IEnumerable<AxisMotionAddressConfig> configs)
    {
        _suspendAutoSave = true;
        _grid.Rows.Clear();
        _syncAxisByAxisId.Clear();
        foreach (var cfg in configs)
        {
            int rowIndex = _grid.Rows.Add(cfg.AxisId);
            var row = _grid.Rows[rowIndex];
            row.Cells[0].Tag = cfg.AxisNumber;
            var addr = cfg.Addresses.ToDictionary(x => x.Key, x => x.Value);
            RemoveObsoleteAddressKeys(addr);
            row.Tag = addr;
            if (_isAbsEncorderColumnIndex >= 0)
                row.Cells[_isAbsEncorderColumnIndex].Value = cfg.IsAbsEncorder;
            StoreSyncAxisMapping(cfg.AxisId, cfg.SyncAxisId);
        }
        _suspendAutoSave = false;
        SaveConfigToFile();
        ResizeAxisIdColumnToFitContent();
        RequestApplyInitialGridSelectionAndSync();
    }

    /// <summary>
    /// 在句柄创建前不得操作 DataGridView 的 CurrentCell/Selected，否则主窗体构造阶段会抛错导致界面无法显示。
    /// </summary>
    private void RequestApplyInitialGridSelectionAndSync()
    {
        if (!IsHandleCreated || IsDisposed)
            return;
        BeginInvoke(new MethodInvoker(ApplyInitialGridSelectionAndSync));
    }

    private void ApplyInitialGridSelectionAndSync()
    {
        if (IsDisposed)
            return;

        if (_grid.IsHandleCreated)
        {
            UpdateMotionPanelEnabledState();
            try
            {
                if (_grid.Rows.Count > 0)
                {
                    _grid.ClearSelection();
                    _grid.CurrentCell = _grid.Rows[0].Cells[0];
                }
            }
            catch (Exception ex)
            {
                LogAction?.Invoke($"轴表初始选行: {ex.Message}");
            }
        }
        else
            UpdateMotionPanelEnabledState();

        RebuildPanelAxisCombo();
        if (_grid.IsHandleCreated)
            ResizeAxisIdColumnToFitContent();
        _ = SyncSidePanelFromPlcAsync();
        _ = KickRealtimeUiForSidePanelAxisAsync();
    }

    private async Task KickRealtimeUiForSidePanelAxisAsync()
    {
        try
        {
            DataGridViewRow? row = GetSidePanelAxisRow();
            if (row is null)
                return;

            await RefreshSingleRowRealtimeFieldsAsync(row);
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"侧栏初值刷新: {ex.Message}");
        }

        if (IsDisposed || !IsHandleCreated)
            return;

        void RefreshLabels()
        {
            UpdatePanelStatusLabel();
            UpdateMotionPanelEnabledState();
        }

        if (InvokeRequired)
            BeginInvoke(new MethodInvoker(RefreshLabels));
        else
            RefreshLabels();
    }

    public IReadOnlyList<AxisMotionAddressConfig> GetAxes()
    {
        var result = new List<AxisMotionAddressConfig>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            var cfg = new AxisMotionAddressConfig
            {
                AxisId = Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty,
                AxisNumber = row.Cells[0].Tag is int number ? number : null,
                IsAbsEncorder = IsAbsEncorderRow(row),
                SyncAxisId = GetConfiguredSyncAxisId(row)
            };

            if (row.Tag is Dictionary<string, string> addresses)
                cfg.Addresses = addresses.ToDictionary(x => x.Key, x => x.Value);

            result.Add(cfg);
        }

        return result;
    }

    public IReadOnlyList<string> GetAxisNames()
    {
        var names = new List<string>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            string axisId = Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(axisId))
                names.Add(axisId);
        }

        return names;
    }

    public async Task RefreshAllAxisDataAsync(bool includeSpeedProfileFields = false)
    {
        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
            return;

        var mbBoolRequests = new List<FieldReadRequest>();
        var mxBoolRequests = new List<FieldReadRequest>();
        var mdRealRequests = new List<FieldReadRequest>();
        var fallbackRequests = new List<FieldReadRequest>();

        foreach (DataGridViewRow row in _grid.Rows)
        {
            var addresses = row.Tag as Dictionary<string, string> ?? new Dictionary<string, string>();
            foreach (var field in FieldDefinitions)
            {
                if (!_valueColumnIndexByField.TryGetValue(field.Key, out int colIndex))
                    continue;

                if (string.Equals(field.Key, "IsAbsEncorder", StringComparison.Ordinal))
                    continue;
                if (!includeSpeedProfileFields && IsSpeedProfileField(field.Key))
                    continue;

                addresses.TryGetValue(field.Key, out string? address);
                address = address?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(address))
                {
                    SetFieldDisplay(row.Cells[colIndex], field, null, null);
                    continue;
                }

                var request = BuildFieldReadRequest(row, row.Cells[colIndex], field, address);
                if (request is null)
                    continue;

                if (request.Field.FieldType == AxisMotionFieldType.Bool && request.MemType == "MB")
                    mbBoolRequests.Add(request);
                else if (request.Field.FieldType == AxisMotionFieldType.Bool && request.MemType == "MX")
                    mxBoolRequests.Add(request);
                else if (request.Field.FieldType == AxisMotionFieldType.Real && request.MemType == "MD")
                    mdRealRequests.Add(request);
                else
                    fallbackRequests.Add(request);
            }
        }

        int axisCount = _grid.Rows.Cast<DataGridViewRow>().Count(r => !r.IsNewRow);
        mbBoolRequests = await TryReadWholeFieldContiguousMbAsync(plc, mbBoolRequests, axisCount);
        mxBoolRequests = await TryReadWholeFieldContiguousMxAsync(plc, mxBoolRequests, axisCount);
        mdRealRequests = await TryReadWholeFieldContiguousMdAsync(plc, mdRealRequests, axisCount);

        await Task.WhenAll(
            ReadMbBoolRequestsAsync(plc, mbBoolRequests),
            ReadMxBoolRequestsAsync(plc, mxBoolRequests),
            ReadMdRealRequestsAsync(plc, mdRealRequests));
        await ReadFallbackRequestsAsync(plc, fallbackRequests);

        UpdateMotionPanelEnabledState();
        await SyncSidePanelFromPlcAsync();
        UpdatePanelCurrentPositionLabel();
    }

    private static bool IsSpeedProfileField(string fieldKey)
    {
        return OnDemandRefreshFieldKeys.Contains(fieldKey, StringComparer.Ordinal);
    }

    private static FieldReadRequest? BuildFieldReadRequest(DataGridViewRow row, DataGridViewCell cell, FieldDef field, string address)
    {
        try
        {
            var (memType, offset, bit) = global::PlcConectCom.AddressParser.ParseAddress(address);
            return new FieldReadRequest(row, cell, field, address, memType.ToUpperInvariant(), offset, bit);
        }
        catch
        {
            return new FieldReadRequest(row, cell, field, address, string.Empty, 0, 0);
        }
    }

    private async Task<List<FieldReadRequest>> TryReadWholeFieldContiguousMbAsync(
        global::PlcConectCom.PlcCommunicator plc,
        List<FieldReadRequest> requests,
        int axisCount)
    {
        if (requests.Count == 0 || axisCount <= 1)
            return requests;

        var consumed = new HashSet<FieldReadRequest>();
        foreach (var group in requests.GroupBy(x => x.Field.Key, StringComparer.Ordinal))
        {
            var byRow = group.OrderBy(x => x.Row.Index).ToList();
            if (byRow.Count != axisCount)
                continue;

            int start = byRow[0].Offset;
            bool contiguous = true;
            for (int i = 0; i < byRow.Count; i++)
            {
                if (byRow[i].Offset != start + i)
                {
                    contiguous = false;
                    break;
                }
            }

            if (!contiguous)
                continue;

            try
            {
                byte[] values = await plc.ReadByteArrayAsync($"MB{start}", axisCount);
                for (int i = 0; i < byRow.Count; i++)
                {
                    bool value = i < values.Length && values[i] != 0;
                    SetFieldDisplay(byRow[i].Cell, byRow[i].Field, value, null);
                    consumed.Add(byRow[i]);
                }
            }
            catch
            {
                // 保留给后续通用批量读取
            }
        }

        return requests.Where(x => !consumed.Contains(x)).ToList();
    }

    private async Task<List<FieldReadRequest>> TryReadWholeFieldContiguousMxAsync(
        global::PlcConectCom.PlcCommunicator plc,
        List<FieldReadRequest> requests,
        int axisCount)
    {
        if (requests.Count == 0 || axisCount <= 1)
            return requests;

        var consumed = new HashSet<FieldReadRequest>();
        foreach (var group in requests.GroupBy(x => x.Field.Key, StringComparer.Ordinal))
        {
            var byRow = group.OrderBy(x => x.Row.Index).ToList();
            if (byRow.Count != axisCount)
                continue;

            int startBit = checked(byRow[0].Offset * 8 + byRow[0].Bit);
            bool contiguousByBit = true;
            for (int i = 0; i < byRow.Count; i++)
            {
                int bit = checked(byRow[i].Offset * 8 + byRow[i].Bit);
                if (bit != startBit + i)
                {
                    contiguousByBit = false;
                    break;
                }
            }

            try
            {
                if (contiguousByBit)
                {
                    bool[] values = await plc.ReadBoolArrayAsync(ToMxAddress(startBit), axisCount);
                    for (int i = 0; i < byRow.Count; i++)
                    {
                        bool value = i < values.Length && values[i];
                        SetFieldDisplay(byRow[i].Cell, byRow[i].Field, value, null);
                        consumed.Add(byRow[i]);
                    }
                    continue;
                }

                // 兼容 MXn.0 / MX(n+1).0 这种“跨字节同位”排布，仍然一次读取整列字段。
                int startByte = byRow[0].Offset;
                int fixedBit = byRow[0].Bit;
                bool contiguousByByteSameBit = true;
                for (int i = 0; i < byRow.Count; i++)
                {
                    if (byRow[i].Bit != fixedBit || byRow[i].Offset != startByte + i)
                    {
                        contiguousByByteSameBit = false;
                        break;
                    }
                }

                if (!contiguousByByteSameBit)
                    continue;

                int bitSpan = checked((axisCount - 1) * 8 + 1);
                bool[] spanValues = await plc.ReadBoolArrayAsync(ToMxAddress(startBit), bitSpan);
                for (int i = 0; i < byRow.Count; i++)
                {
                    int idx = checked(i * 8);
                    bool value = idx >= 0 && idx < spanValues.Length && spanValues[idx];
                    SetFieldDisplay(byRow[i].Cell, byRow[i].Field, value, null);
                    consumed.Add(byRow[i]);
                }
            }
            catch
            {
                // 保留给后续通用批量读取
            }
        }

        return requests.Where(x => !consumed.Contains(x)).ToList();
    }

    private async Task<List<FieldReadRequest>> TryReadWholeFieldContiguousMdAsync(
        global::PlcConectCom.PlcCommunicator plc,
        List<FieldReadRequest> requests,
        int axisCount)
    {
        if (requests.Count == 0 || axisCount <= 1)
            return requests;

        var consumed = new HashSet<FieldReadRequest>();
        foreach (var group in requests.GroupBy(x => x.Field.Key, StringComparer.Ordinal))
        {
            var byRow = group.OrderBy(x => x.Row.Index).ToList();
            if (byRow.Count != axisCount)
                continue;

            int start = byRow[0].Offset;
            bool contiguous = true;
            for (int i = 0; i < byRow.Count; i++)
            {
                if (byRow[i].Offset != start + i)
                {
                    contiguous = false;
                    break;
                }
            }

            if (!contiguous)
                continue;

            try
            {
                double[] values = await plc.ReadDoubleArrayAsync($"MD{start}", axisCount);
                for (int i = 0; i < byRow.Count; i++)
                {
                    if (i < values.Length)
                    {
                        SetFieldDisplay(byRow[i].Cell, byRow[i].Field, values[i], null);
                        consumed.Add(byRow[i]);
                    }
                }
            }
            catch
            {
                // 保留给后续通用批量读取
            }
        }

        return requests.Where(x => !consumed.Contains(x)).ToList();
    }

    private async Task ReadMbBoolRequestsAsync(global::PlcConectCom.PlcCommunicator plc, List<FieldReadRequest> requests)
    {
        foreach (var batch in BuildOffsetBatches(requests, MaxBatchByteCount))
        {
            string startAddress = $"MB{batch.Start}";
            int count = batch.End - batch.Start + 1;
            try
            {
                byte[] values = await plc.ReadByteArrayAsync(startAddress, count);
                foreach (var request in batch.Requests)
                {
                    int index = request.Offset - batch.Start;
                    bool value = index >= 0 && index < values.Length && values[index] != 0;
                    SetFieldDisplay(request.Cell, request.Field, value, null);
                }
            }
            catch (Exception ex)
            {
                SetBatchError(batch.Requests, ex);
                LogAction?.Invoke($"轴表批量读取 BOOL(MB) 失败 {startAddress}+{count}: {ex.Message}");
            }
        }
    }

    private async Task ReadMxBoolRequestsAsync(global::PlcConectCom.PlcCommunicator plc, List<FieldReadRequest> requests)
    {
        var bitRequests = requests
            .Select(x => x with { Offset = checked(x.Offset * 8 + x.Bit), Bit = 0 })
            .ToList();

        foreach (var batch in BuildOffsetBatches(bitRequests, MaxBatchBitCount))
        {
            string startAddress = ToMxAddress(batch.Start);
            int count = batch.End - batch.Start + 1;
            try
            {
                bool[] values = await plc.ReadBoolArrayAsync(startAddress, count);
                foreach (var request in batch.Requests)
                {
                    int index = request.Offset - batch.Start;
                    bool value = index >= 0 && index < values.Length && values[index];
                    SetFieldDisplay(request.Cell, request.Field, value, null);
                }
            }
            catch (Exception ex)
            {
                SetBatchError(batch.Requests, ex);
                LogAction?.Invoke($"轴表批量读取 BOOL(MX) 失败 {startAddress}+{count}: {ex.Message}");
            }
        }
    }

    private async Task ReadMdRealRequestsAsync(global::PlcConectCom.PlcCommunicator plc, List<FieldReadRequest> requests)
    {
        foreach (var batch in BuildOffsetBatches(requests, MaxBatchDoubleCount))
        {
            string startAddress = $"MD{batch.Start}";
            int count = batch.End - batch.Start + 1;
            try
            {
                double[] values = await plc.ReadDoubleArrayAsync(startAddress, count);
                foreach (var request in batch.Requests)
                {
                    int index = request.Offset - batch.Start;
                    if (index >= 0 && index < values.Length)
                        SetFieldDisplay(request.Cell, request.Field, values[index], null);
                }
            }
            catch (Exception ex)
            {
                SetBatchError(batch.Requests, ex);
                LogAction?.Invoke($"轴表批量读取 REAL(MD) 失败 {startAddress}+{count}: {ex.Message}");
            }
        }
    }

    private async Task ReadFallbackRequestsAsync(global::PlcConectCom.PlcCommunicator plc, List<FieldReadRequest> requests)
    {
        foreach (var request in requests)
        {
            try
            {
                object value = await ReadFieldValueAsync(plc, request.Field.FieldType, request.Address);
                SetFieldDisplay(request.Cell, request.Field, value, null);
            }
            catch (Exception ex)
            {
                SetFieldDisplay(request.Cell, request.Field, null, "ERR");
                LogAction?.Invoke($"轴[{GetAxisId(request.Row)}] {request.Field.DisplayName} 读取失败 {request.Address}: {ex.Message}");
            }
        }
    }

    private static List<FieldReadBatch<FieldReadRequest>> BuildOffsetBatches(List<FieldReadRequest> requests, int maxSpan)
    {
        var batches = new List<FieldReadBatch<FieldReadRequest>>();
        if (requests.Count == 0)
            return batches;

        var ordered = requests.OrderBy(x => x.Offset).ToList();
        int cursor = 0;
        while (cursor < ordered.Count)
        {
            int start = ordered[cursor].Offset;
            int end = ordered[cursor].Offset;
            var bucket = new List<FieldReadRequest> { ordered[cursor] };
            cursor++;

            while (cursor < ordered.Count)
            {
                var next = ordered[cursor];
                int mergedEnd = Math.Max(end, next.Offset);
                if (mergedEnd - start + 1 > maxSpan)
                    break;

                end = mergedEnd;
                bucket.Add(next);
                cursor++;
            }

            batches.Add(new FieldReadBatch<FieldReadRequest>(start, end, bucket));
        }

        return batches;
    }

    private void SetBatchError(IEnumerable<FieldReadRequest> requests, Exception ex)
    {
        foreach (var request in requests)
        {
            SetFieldDisplay(request.Cell, request.Field, null, "ERR");
            LogAction?.Invoke($"轴[{GetAxisId(request.Row)}] {request.Field.DisplayName} 读取失败 {request.Address}: {ex.Message}");
        }
    }

    private static string ToMxAddress(int absoluteBit)
    {
        int byteOffset = absoluteBit / 8;
        int bit = absoluteBit % 8;
        return $"MX{byteOffset}.{bit}";
    }

    private static string GetAxisId(DataGridViewRow row)
    {
        return Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture)?.Trim() ?? "?";
    }

    /// <summary>
    /// Real 字段显示文本。PLC 的 Real 是 32 位浮点，0.1 读回来会带尾差
    /// （0.10000000149011612）；加速度/减速度按需求最多显示 3 位小数。
    /// </summary>
    private static string FormatRealFieldText(string fieldKey, object? value)
    {
        string text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        bool isAccOrDec = string.Equals(fieldKey, "Acceleration", StringComparison.Ordinal)
            || string.Equals(fieldKey, "Deceleration", StringComparison.Ordinal);
        if (isAccOrDec && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            text = v.ToString("0.###", CultureInfo.InvariantCulture);
        return text;
    }

    private void SetFieldDisplay(DataGridViewCell cell, FieldDef field, object? value, string? error)
    {
        cell.Style.BackColor = Color.White;
        cell.Style.ForeColor = Color.Black;

        if (!string.IsNullOrWhiteSpace(error))
        {
            cell.Value = error;
            cell.Style.BackColor = Color.MistyRose;
            cell.Style.ForeColor = Color.DarkRed;
            return;
        }

        if (value is null)
        {
            cell.Value = string.Empty;
            return;
        }

        if (field.FieldType == AxisMotionFieldType.Bool)
        {
            bool state = value is bool b && b;
            AxisFieldUiMode uiMode = _uiModeByField[field.Key];
            if (uiMode == AxisFieldUiMode.BoolControl)
            {
                cell.Value = state ? "打开" : "关闭";
                cell.Style.BackColor = state ? Color.Honeydew : Color.MistyRose;
                cell.Style.ForeColor = state ? Color.ForestGreen : Color.Red;
                cell.Style.SelectionBackColor = cell.Style.BackColor;
                cell.Style.SelectionForeColor = cell.Style.ForeColor;
            }
            else
            {
                cell.Value = "●";
                cell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
                cell.Style.Font = StatusDotFont;
                cell.Style.ForeColor = state
                    ? (string.Equals(field.Key, "Alarm", StringComparison.Ordinal) ? Color.Red : Color.LawnGreen)
                    : Color.Gray;
                cell.Style.SelectionForeColor = cell.Style.ForeColor;
                cell.Style.SelectionBackColor = Color.Gainsboro;
            }

            return;
        }

        cell.Value = FormatRealFieldText(field.Key, value);
    }

    private static AxisFieldUiMode GetUiMode(FieldDef field)
    {
        if (field.FieldType != AxisMotionFieldType.Bool)
            return AxisFieldUiMode.Display;

        if (BoolControlFields.Contains(field.Key))
            return AxisFieldUiMode.BoolControl;

        if (BoolStatusFields.Contains(field.Key))
            return AxisFieldUiMode.BoolStatus;

        return AxisFieldUiMode.Display;
    }

    private void ShowAddressConfigDialog(DataGridViewRow row)
    {
        var map = row.Tag as Dictionary<string, string> ?? new Dictionary<string, string>();

        using var dialog = new Form
        {
            Text = $"地址配置 - {Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture) ?? "轴"}",
            Width = 520,
            Height = 620,
            StartPosition = FormStartPosition.CenterParent
        };

        var cfgGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeColumns = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };

        cfgGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "字段", ReadOnly = true, FillWeight = 38 });
        cfgGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "地址", FillWeight = 62 });

        foreach (var field in FieldDefinitions)
        {
            if (string.Equals(field.Key, "IsAbsEncorder", StringComparison.Ordinal))
                continue;

            map.TryGetValue(field.Key, out string? addr);
            int r = cfgGrid.Rows.Add(field.DisplayName, addr ?? string.Empty);
            cfgGrid.Rows[r].Tag = field.Key;
        }

        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8)
        };

        var btnOk = new Button { Text = "确定", Width = 72, DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "取消", Width = 72, DialogResult = DialogResult.Cancel };
        panel.Controls.Add(btnOk);
        panel.Controls.Add(btnCancel);

        dialog.Controls.Add(cfgGrid);
        dialog.Controls.Add(panel);
        dialog.AcceptButton = btnOk;
        dialog.CancelButton = btnCancel;

        DataGridViewNoSortHelper.WireControlSubtreeForNoColumnSort(dialog);
        if (AppDialog.ShowDialog(dialog, this) != DialogResult.OK)
            return;

        var newMap = new Dictionary<string, string>();
        foreach (DataGridViewRow cfgRow in cfgGrid.Rows)
        {
            if (cfgRow.Tag is not string key)
                continue;

            string addr = Convert.ToString(cfgRow.Cells[1].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(addr))
                newMap[key] = addr;
        }

        row.Tag = newMap;
        SaveConfigToFile();
    }

    // Excel 表「变量名」与内部字段键的对应（与 PLC 导出 HMI_gb / HMI_gr / HMI_ 命名一致）
    private static readonly (string[] Fragments, string FieldKey)[] ExcelNameFieldMappings =
    [
        (["轴断使能"], "AxisEnable"),
        (["单独回原", "轴单独回原"], "AxisHome"),
        (["正向点动"], "JogPlus"),
        (["反向点动"], "JogMinus"),
        (["相对位移量", "相对位移", "单步位移", "轴手动示教当前位置"], "RelMoveDistance"),
        (["GoABS"], "AbsMove"),
        (["GoRel"], "RelMove"),
        (["定位停止"], "Stop"),
        (["绝对值轴清零"], "AbsReset"),
        (["正极限"], "DiPot"),
        (["负极限"], "DiNot"),
        (["原点信号"], "DiHome"),
        (["使能中"], "Enabling"),
        (["回零完成"], "HomeDone"),
        (["手动速度"], "ManualSpeed"),
        (["空载速度"], "IdleSpeed"),
        (["工作速度"], "WorkSpeed"),
        (["缓行速度"], "CreepSpeed"),
        (["回零速度"], "HomeSpeed"),
        (["加速度"], "Acceleration"),
        (["减速度"], "Deceleration"),
        (["绝对定位软正限位", "软正限位"], "AbsSoftLimitPos"),
        (["绝对定位软负限位", "软负限位"], "AbsSoftLimitNeg"),
        (["当前位置"], "CurrentPos"),
        (["当前速度"], "CurrentSpeed"),
        (["当前扭矩"], "CurrentTorque"),
        (["报警", "报警码", "轴报警"], "Alarm")
    ];

    private void ImportAxisAddressTableFromExcel()
    {
        if (!_canAddDelete || !_canModify)
            return;

        if (_grid.Rows.Count == 0)
        {
            MessageBox.Show(this, "请先新增至少一根轴，再导入地址。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = "选择轴组地址表（Excel）",
            Filter = "Excel 文件 (*.xls;*.xlsx)|*.xls;*.xlsx|所有文件 (*.*)|*.*",
            InitialDirectory = AppContext.BaseDirectory
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            using var stream = File.Open(dialog.FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = ExcelReaderFactory.CreateReader(stream);

            int nameCol = 0;
            int addressCol = 1;
            int dataTypeCol = 2;
            bool headerDone = false;
            var importRows = new List<(string Name, string Address, string DataType)>();

            while (reader.Read())
            {
                if (!headerDone)
                {
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        string h = Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                        if (string.Equals(h, "Name", StringComparison.OrdinalIgnoreCase))
                            nameCol = i;
                        else if (string.Equals(h, "Address", StringComparison.OrdinalIgnoreCase))
                            addressCol = i;
                        else if (string.Equals(h, "DataType", StringComparison.OrdinalIgnoreCase))
                            dataTypeCol = i;
                    }

                    headerDone = true;
                    continue;
                }

                string name = Convert.ToString(reader.GetValue(nameCol), CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                string address = NormalizePercentAddress(Convert.ToString(reader.GetValue(addressCol), CultureInfo.InvariantCulture));
                string dataType = Convert.ToString(reader.GetValue(dataTypeCol), CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(address))
                    continue;

                importRows.Add((name, address, dataType));
            }

            if (importRows.Count == 0)
            {
                MessageBox.Show(this, "未读取到有效数据行。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var fieldTypeByKey = FieldDefinitions.ToDictionary(x => x.Key, x => x.FieldType);
            int mapped = 0;
            int skipped = 0;
            var pending = new List<(string FieldKey, string MemType, int BaseOffset, AxisMotionFieldType ElemKind, int StringChars)>();

            foreach (var row in importRows)
            {
                string? fieldKey = MapExcelNameToFieldKey(row.Name);
                if (fieldKey is null)
                {
                    LogAction?.Invoke($"轴组导入：跳过未识别变量 \"{row.Name}\"。");
                    skipped++;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(row.Address) || !TryParseMemAddress(row.Address, out string memType, out int baseOffset))
                {
                    LogAction?.Invoke($"轴组导入：\"{row.Name}\" 地址无效。");
                    skipped++;
                    continue;
                }

                if (!TryParseExcelArrayElementType(row.DataType, out AxisMotionFieldType elemKind, out int stringChars))
                {
                    LogAction?.Invoke($"轴组导入：\"{row.Name}\" 数据类型无法解析: {row.DataType}");
                    skipped++;
                    continue;
                }

                if (!fieldTypeByKey.TryGetValue(fieldKey, out AxisMotionFieldType appType) || appType != elemKind)
                {
                    LogAction?.Invoke($"轴组导入：\"{row.Name}\" 映射字段 {fieldKey} 与表类型不一致（程序为 {appType}，表为 {elemKind}），已跳过。");
                    skipped++;
                    continue;
                }

                if (elemKind == AxisMotionFieldType.Bool && !memType.Equals("MB", StringComparison.OrdinalIgnoreCase))
                {
                    LogAction?.Invoke($"轴组导入：BOOL 数组 \"{row.Name}\" 当前仅支持起始地址为 MB（单轴一字节），已跳过。");
                    skipped++;
                    continue;
                }

                if (elemKind == AxisMotionFieldType.Real && !memType.Equals("MD", StringComparison.OrdinalIgnoreCase))
                {
                    LogAction?.Invoke($"轴组导入：REAL 数组 \"{row.Name}\" 建议起始地址为 MD（单轴占 1 个 MD），已跳过: {row.Address}");
                    skipped++;
                    continue;
                }

                if (elemKind == AxisMotionFieldType.String)
                {
                    if (stringChars <= 0)
                        stringChars = 6;
                    if (!memType.Equals("MD", StringComparison.OrdinalIgnoreCase) && !memType.Equals("MW", StringComparison.OrdinalIgnoreCase))
                    {
                        LogAction?.Invoke($"轴组导入：STRING \"{row.Name}\" 仅支持 MW 或 MD 起始地址，已跳过。");
                        skipped++;
                        continue;
                    }
                }

                pending.Add((fieldKey, memType.ToUpperInvariant(), baseOffset, elemKind, stringChars));
                mapped++;
            }

            if (pending.Count == 0)
            {
                MessageBox.Show(this, "没有可写入的地址映射，请检查表头（Name/Address/DataType）与内容。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            for (int axisIndex = 0; axisIndex < _grid.Rows.Count; axisIndex++)
            {
                var gridRow = _grid.Rows[axisIndex];
                var map = gridRow.Tag as Dictionary<string, string> ?? new Dictionary<string, string>();

                foreach (var item in pending)
                {
                    int number = gridRow.Cells[0].Tag is int importedNumber ? importedNumber : axisIndex;
                    string addr = BuildPerAxisAddress(item.MemType, item.BaseOffset, number, item.ElemKind, item.StringChars);
                    if (!string.IsNullOrWhiteSpace(addr))
                        map[item.FieldKey] = addr;
                }

                gridRow.Tag = map;
            }

            SaveConfigToFile();
            ResizeAxisIdColumnToFitContent();
            LogAction?.Invoke($"轴组地址表导入完成：文件 {dialog.FileName}，映射 {mapped} 条字段，跳过 {skipped} 行；已按当前 {_grid.Rows.Count} 根轴写入地址。");
            MessageBox.Show(this, $"已根据 Excel 更新 {_grid.Rows.Count} 根轴的地址配置（共 {mapped} 个字段）。", "导入完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导入失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            LogAction?.Invoke($"轴组地址表导入失败: {ex.Message}");
        }
    }

    private static string? MapExcelNameToFieldKey(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
            return null;

        foreach (var (fragments, fieldKey) in ExcelNameFieldMappings)
        {
            foreach (var fragment in fragments)
            {
                if (rawName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    return fieldKey;
            }
        }

        return null;
    }

    private static string NormalizePercentAddress(string? raw)
    {
        string a = raw?.Trim() ?? string.Empty;
        if (a.StartsWith('%'))
            a = a[1..];
        return a.Trim();
    }

    private static bool TryParseMemAddress(string address, out string memType, out int offset)
    {
        memType = string.Empty;
        offset = 0;
        address = address.Trim();
        if (address.Length < 3)
            return false;

        if (address.StartsWith("MB", StringComparison.OrdinalIgnoreCase))
            memType = "MB";
        else if (address.StartsWith("MW", StringComparison.OrdinalIgnoreCase))
            memType = "MW";
        else if (address.StartsWith("MD", StringComparison.OrdinalIgnoreCase))
            memType = "MD";
        else if (address.StartsWith("MX", StringComparison.OrdinalIgnoreCase))
            memType = "MX";
        else
            return false;

        string num = address[2..];
        return int.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out offset);
    }

    private static bool TryParseExcelArrayElementType(string dataTypeText, out AxisMotionFieldType elemKind, out int stringCharLength)
    {
        elemKind = AxisMotionFieldType.Bool;
        stringCharLength = 0;
        if (string.IsNullOrWhiteSpace(dataTypeText))
            return false;

        string compact = dataTypeText.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (Regex.IsMatch(compact, @"ARRAY\s*\[.*\]\s*OFBOOL", RegexOptions.IgnoreCase)
            || string.Equals(compact, "BOOL", StringComparison.OrdinalIgnoreCase))
        {
            elemKind = AxisMotionFieldType.Bool;
            return true;
        }

        if (Regex.IsMatch(compact, @"ARRAY\s*\[.*\]\s*OFREAL", RegexOptions.IgnoreCase)
            || string.Equals(compact, "REAL", StringComparison.OrdinalIgnoreCase))
        {
            elemKind = AxisMotionFieldType.Real;
            return true;
        }

        Match mStr = Regex.Match(compact, @"ARRAY\s*\[.*\]\s*OFSTRING\((\d+)\)", RegexOptions.IgnoreCase);
        if (mStr.Success && int.TryParse(mStr.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
        {
            elemKind = AxisMotionFieldType.String;
            stringCharLength = n;
            return true;
        }

        mStr = Regex.Match(compact, @"OFSTRING\((\d+)\)", RegexOptions.IgnoreCase);
        if (mStr.Success && int.TryParse(mStr.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
        {
            elemKind = AxisMotionFieldType.String;
            stringCharLength = n;
            return true;
        }

        return false;
    }

    /// <summary>
    /// BOOL：MB 基址 + 轴索引（每轴一字节）。REAL：MD 基址 + 轴索引（每轴一个 MD）。
    /// STRING：按 MW 读取；MD 基址会换算为字节后再按轴步进；MW 基址则按寄存器字步进。
    /// </summary>
    private static string BuildPerAxisAddress(string memType, int baseOffset, int axisIndex, AxisMotionFieldType elemKind, int stringChars)
    {
        return elemKind switch
        {
            AxisMotionFieldType.Bool when memType == "MB" => $"MB{baseOffset + axisIndex}",
            AxisMotionFieldType.Real when memType == "MD" => $"MD{baseOffset + axisIndex}",
            AxisMotionFieldType.String => BuildStringAxisAddress(memType, baseOffset, axisIndex, stringChars),
            _ => string.Empty
        };
    }

    private static string BuildStringAxisAddress(string memType, int baseOffset, int axisIndex, int stringChars)
    {
        int charCount = Math.Max(1, stringChars);
        int bytesPerAxis = ((charCount + 1) / 2) * 2;

        if (memType == "MW")
        {
            int wordsPerAxis = Math.Max(1, (charCount + 1) / 2);
            return $"MW{baseOffset + axisIndex * wordsPerAxis}";
        }

        if (memType == "MD")
        {
            int byteBase = baseOffset * 4 + axisIndex * bytesPerAxis;
            int mwStart = byteBase / 2;
            return $"MW{mwStart}";
        }

        return string.Empty;
    }

    private void LoadConfigFromFile()
    {
        try
        {
            if (!File.Exists(_configPath))
                return;

            string json = File.ReadAllText(_configPath);
            var configs = JsonSerializer.Deserialize<List<AxisMotionAddressConfig>>(json);
            if (configs is null)
                return;

            _suspendAutoSave = true;
            _grid.Rows.Clear();
            _syncAxisByAxisId.Clear();
            foreach (var cfg in configs)
            {
                int rowIndex = _grid.Rows.Add(cfg.AxisId);
                _grid.Rows[rowIndex].Cells[0].Tag = cfg.AxisNumber;
                var addr = cfg.Addresses?.ToDictionary(x => x.Key, x => x.Value) ?? new Dictionary<string, string>();
                RemoveObsoleteAddressKeys(addr);
                _grid.Rows[rowIndex].Tag = addr;
                if (_isAbsEncorderColumnIndex >= 0)
                    _grid.Rows[rowIndex].Cells[_isAbsEncorderColumnIndex].Value = cfg.IsAbsEncorder;
                StoreSyncAxisMapping(cfg.AxisId, cfg.SyncAxisId);
            }
            _suspendAutoSave = false;

            RebuildPanelAxisCombo();
            ResizeAxisIdColumnToFitContent();
            RequestApplyInitialGridSelectionAndSync();

            LogAction?.Invoke("轴运动配置已加载。");
        }
        catch (Exception ex)
        {
            _suspendAutoSave = false;
            LogAction?.Invoke($"加载轴运动配置失败: {ex.Message}");
        }
    }

    private async Task OnSaveConfigClickedAsync()
    {
        try
        {
            SaveConfigToFile();
            await SaveSpeedProfileFieldsToPlcAsync();
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"保存配置: {ex.Message}");
        }
    }

    /// <summary>
    /// 「编辑数据」弹框打开前，从 PLC 实时读取指定字段（速度/加减速与软限位）。
    /// 成功的字段会回写表格单元格保持一致；PLC 未连接或某字段读取失败时，
    /// 该字段回退表格缓存值（可能为空），不阻塞弹框打开。
    /// </summary>
    private async Task<Dictionary<string, string>> ReadDialogFieldValuesFromPlcAsync(
        DataGridViewRow row, string axisId, IReadOnlyList<string> fieldKeys)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var addresses = row.Tag as Dictionary<string, string> ?? new Dictionary<string, string>();
        var plc = PlcProvider?.Invoke() ?? PlcInstance;

        foreach (string fieldKey in fieldKeys)
        {
            string fallback = string.Empty;
            if (_valueColumnIndexByField.TryGetValue(fieldKey, out int colIdx) && colIdx >= 0 && colIdx < row.Cells.Count)
                fallback = FormatRealFieldText(fieldKey, row.Cells[colIdx].Value);

            result[fieldKey] = fallback;

            string address = addresses.TryGetValue(fieldKey, out string? addr) ? addr.Trim() : string.Empty;
            if (plc is not { IsConnected: true } || string.IsNullOrWhiteSpace(address))
                continue;

            try
            {
                object read = await ReadFieldValueAsync(plc, AxisMotionFieldType.Real, address);
                string text = FormatRealFieldText(fieldKey, read);
                result[fieldKey] = text;

                // 回写表格单元格，保持界面与 PLC 一致
                if (_valueColumnIndexByField.TryGetValue(fieldKey, out int writeCol) && writeCol >= 0 && writeCol < row.Cells.Count)
                    row.Cells[writeCol].Value = text;
            }
            catch (Exception ex)
            {
                LogAction?.Invoke($"轴[{axisId}] {GetFieldDisplayName(fieldKey)} 读取失败 {address}: {ex.Message}（弹框显示表格缓存值）");
            }
        }

        return result;
    }

    private async Task OnEditSpeedProfileClickedAsync()
    {
        if (!_canModify)
            return;

        DataGridViewRow? row = GetSelectedAxisRow();
        if (row is null)
        {
            MessageBox.Show(this, "请先在右侧「轴编号」选择轴，或在表格中选中一行轴数据。", "编辑数据", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string axisId = Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture)?.Trim() ?? "?";

        // 弹框前先从 PLC 实时读取六项速度/加减速与软正/负限位；
        // PLC 未连接或读取失败时回退表格缓存值（可能为空）。
        Dictionary<string, string> initialValues = await ReadDialogFieldValuesFromPlcAsync(row, axisId, EditDialogFieldKeys);

        using var dlg = new Form
        {
            Text = $"编辑数据 — {axisId}",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoSize = false,
            ClientSize = new Size(420, 390),
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular)
        };

        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = EditDialogFieldKeys.Length,
            Padding = new Padding(12, 12, 12, 8),
            AutoSize = false
        };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        var textBoxes = new TextBox[EditDialogFieldKeys.Length];
        for (int i = 0; i < EditDialogFieldKeys.Length; i++)
        {
            string fieldKey = EditDialogFieldKeys[i];
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

            var lbl = new Label
            {
                Text = $"{GetFieldDisplayName(fieldKey)}:",
                AutoSize = true,
                // 顶对齐：长标签（如「绝对定位软正限位:」）测量期换行也不会垂直居中下坠
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Margin = new Padding(0, 8, 8, 0)
            };
            string initial = initialValues.TryGetValue(fieldKey, out string? value) ? value : string.Empty;

            textBoxes[i] = new TextBox
            {
                Text = initial,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
                Margin = new Padding(0, 4, 0, 0),
                Width = 220
            };

            main.Controls.Add(lbl, 0, i);
            main.Controls.Add(textBoxes[i], 1, i);
        }

        // 弹框一经关闭控件就会被 Dispose（半模态宿主以 Show() 显示），
        // 届时再读 TextBox.Text 会拿到空串，故在点击「保存」时先把内容摘出来。
        var valuesByField = new Dictionary<string, string>(StringComparer.Ordinal);
        void CaptureDialogValues()
        {
            valuesByField.Clear();
            for (int i = 0; i < EditDialogFieldKeys.Length; i++)
                valuesByField[EditDialogFieldKeys[i]] = textBoxes[i].Text.Trim();
        }

        var btnOk = new Button { Text = "保存", Width = 88 };
        var btnCancel = new Button { Text = "取消", Width = 88, DialogResult = DialogResult.Cancel };
        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12, 4, 12, 12),
            AutoSize = true
        };
        buttonPanel.Controls.Add(btnCancel);
        buttonPanel.Controls.Add(btnOk);

        btnOk.Click += (_, _) =>
        {
            for (int i = 0; i < textBoxes.Length; i++)
            {
                string t = textBoxes[i].Text.Trim();
                if (string.IsNullOrEmpty(t))
                    continue;
                if (string.Equals(t, "ERR", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(dlg, $"「{GetFieldDisplayName(EditDialogFieldKeys[i])}」不能为 ERR。", "编辑数据", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    MessageBox.Show(dlg, $"「{GetFieldDisplayName(EditDialogFieldKeys[i])}」不是有效数值。", "编辑数据", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            CaptureDialogValues();
            dlg.DialogResult = DialogResult.OK;
            dlg.Close();
        };

        dlg.Controls.Add(buttonPanel);
        dlg.Controls.Add(main);
        dlg.AcceptButton = btnOk;
        dlg.CancelButton = btnCancel;

        Form? owner = FindForm();
        // 用 ShowDialog 的返回值判断结果：半模态路径下弹框已 Close+Dispose，
        // 再读 dlg.DialogResult 可能拿到 None，导致保存后的写入被跳过。
        DialogResult dialogResult = owner is not null && !owner.IsDisposed
            ? AppDialog.ShowDialog(dlg, owner)
            : AppDialog.ShowDialog(dlg);

        if (dialogResult != DialogResult.OK)
        {
            // 保留一条痕迹：避免出现「点了保存但日志完全没有记录」时无法区分是取消还是异常
            LogAction?.Invoke($"轴[{axisId}] 编辑数据未写入（弹框结果 {dialogResult}）。");
            return;
        }

        // 用「保存」时摘出的数值写入 PLC，不经已 Dispose 的文本框读取
        _suspendAutoSave = true;
        try
        {
            for (int i = 0; i < EditDialogFieldKeys.Length; i++)
            {
                string fieldKey = EditDialogFieldKeys[i];
                if (!_valueColumnIndexByField.TryGetValue(fieldKey, out int colIdx))
                    continue;
                row.Cells[colIdx].Value = valuesByField.TryGetValue(fieldKey, out string? cellText) ? cellText : string.Empty;
            }
        }
        finally
        {
            _suspendAutoSave = false;
        }

        SaveConfigToFile();
        LogAction?.Invoke($"轴[{axisId}] 编辑数据保存：开始写入 PLC。");
        await WriteDialogValuesToPlcAsync(row, valuesByField, axisId);
    }

    /// <summary>
    /// 把「编辑数据」弹框填入的数值直接写入 PLC（不经过表格单元格），
    /// 并逐字段回读验证：写成功/写失败/地址未配置/被 PLC 周期覆盖都会留下日志。
    /// </summary>
    private async Task WriteDialogValuesToPlcAsync(
        DataGridViewRow row, IReadOnlyDictionary<string, string> valuesByField, string axisId)
    {
        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
        {
            LogAction?.Invoke("速度/加减速与软限位参数未写入：PLC 未连接。");
            return;
        }

        var addresses = row.Tag as Dictionary<string, string> ?? new Dictionary<string, string>();

        foreach (string fieldKey in EditDialogFieldKeys)
        {
            if (!valuesByField.TryGetValue(fieldKey, out string? text) || string.IsNullOrWhiteSpace(text))
                continue;

            string addr = addresses.TryGetValue(fieldKey, out string? a) ? a.Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(addr))
            {
                LogAction?.Invoke($"轴[{axisId}] {GetFieldDisplayName(fieldKey)} 未写入：地址绑定中未配置该字段地址。");
                continue;
            }

            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                LogAction?.Invoke($"轴[{axisId}] {GetFieldDisplayName(fieldKey)} 未写入：无法解析「{text}」。");
                continue;
            }

            try
            {
                await plc.WriteDoubleAsync(addr, value);
                double back = await plc.ReadDoubleAsync(addr);
                LogAction?.Invoke(Math.Abs(back - value) <= 0.01
                    ? $"轴[{axisId}] {GetFieldDisplayName(fieldKey)} 已写入 {value} → {addr}（回读一致）。"
                    : $"轴[{axisId}] {GetFieldDisplayName(fieldKey)} 已写入 {value} → {addr}，但回读到 {back}：该地址可能被 PLC 程序周期刷新覆盖。");
            }
            catch (Exception ex)
            {
                LogAction?.Invoke($"轴[{axisId}] {GetFieldDisplayName(fieldKey)} 写入失败 {addr}: {ex.Message}");
            }
        }
    }

    /// <summary>将表格中空载/工作/缓行/回零速度及加、减速度写入各行已配置的 PLC 地址。</summary>
    private async Task SaveSpeedProfileFieldsToPlcAsync()
    {
        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
        {
            LogAction?.Invoke("保存配置：PLC 未连接，速度/加减速参数未写入。");
            return;
        }

        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow)
                continue;

            await WriteSpeedProfileFieldsForRowToPlcAsync(row, plc);
        }
    }

    /// <summary>将当前行的速度/加减速（默认）或指定字段写入 PLC（地址需在地址配置中填写）。</summary>
    /// <param name="verboseLog">true 时逐字段记录写入与回读结果（「编辑数据」保存路径使用）。</param>
    /// <param name="fieldKeys">待写入字段；null 时为六项速度/加减速。</param>
    private async Task WriteSpeedProfileFieldsForRowToPlcAsync(
        DataGridViewRow row,
        global::PlcConectCom.PlcCommunicator? plc = null,
        bool verboseLog = false,
        IReadOnlyList<string>? fieldKeys = null)
    {
        var keys = fieldKeys ?? SpeedProfilePlcWriteFieldKeys;
        plc ??= PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
        {
            if (verboseLog)
                LogAction?.Invoke("速度/加减速参数未写入：PLC 未连接。");
            return;
        }

        if (row.IsNewRow)
            return;

        string axisId = Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture)?.Trim() ?? "?";
        var addresses = row.Tag as Dictionary<string, string> ?? new Dictionary<string, string>();

        foreach (string fieldKey in keys)
        {
            if (!_valueColumnIndexByField.TryGetValue(fieldKey, out int colIndex))
                continue;

            addresses.TryGetValue(fieldKey, out string? addr);
            addr = addr?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(addr))
            {
                if (verboseLog)
                    LogAction?.Invoke($"轴[{axisId}] {GetFieldDisplayName(fieldKey)} 未写入：地址绑定中未配置该字段地址。");
                continue;
            }

            string? cellText = Convert.ToString(row.Cells[colIndex].Value, CultureInfo.InvariantCulture)?.Trim();
            if (string.IsNullOrWhiteSpace(cellText) ||
                string.Equals(cellText, "ERR", StringComparison.OrdinalIgnoreCase))
            {
                LogAction?.Invoke($"轴[{axisId}] {GetFieldDisplayName(fieldKey)} 未写入：单元格无有效数值。");
                continue;
            }

            if (!double.TryParse(cellText, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                LogAction?.Invoke($"轴[{axisId}] {GetFieldDisplayName(fieldKey)} 未写入：无法解析「{cellText}」。");
                continue;
            }

            try
            {
                await plc.WriteDoubleAsync(addr, value);

                if (!verboseLog)
                    continue;

                // 写后立即回读，验证寄存器确实落值；若 PLC 程序周期刷新该地址，回读能暴露覆盖现象
                double back = await plc.ReadDoubleAsync(addr);
                if (Math.Abs(back - value) <= 0.01)
                    LogAction?.Invoke($"轴[{axisId}] {GetFieldDisplayName(fieldKey)} 已写入 {value} → {addr}（回读一致）。");
                else
                    LogAction?.Invoke($"轴[{axisId}] {GetFieldDisplayName(fieldKey)} 已写入 {value} → {addr}，但回读到 {back}：该地址可能被 PLC 程序周期刷新覆盖。");
            }
            catch (Exception ex)
            {
                LogAction?.Invoke($"轴[{axisId}] {GetFieldDisplayName(fieldKey)} 写入失败 {addr}: {ex.Message}");
            }
        }
    }

    private void SaveConfigToFile()
    {
        if (_suspendAutoSave)
            return;

        try
        {
            var configs = GetAxes();
            WriteAxisConfiguration(configs);
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"保存轴运动配置失败: {ex.Message}");
        }
    }

    private async Task<object> ReadFieldValueAsync(global::PlcConectCom.PlcCommunicator plc, AxisMotionFieldType type, string address)
    {
        return type switch
        {
            AxisMotionFieldType.Bool => await plc.ReadBoolAsync(address),
            AxisMotionFieldType.Real => await plc.ReadDoubleAsync(address),
            AxisMotionFieldType.String => await plc.ReadStringAsync(address, StringReadLength),
            _ => string.Empty
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _manualSpeedDebounce.Dispose();
            _autoRefreshTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
