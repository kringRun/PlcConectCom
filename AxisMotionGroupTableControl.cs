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

    public Dictionary<string, string> Addresses { get; set; } = [];
}

// 轴组运动表控件：统一管理轴地址配置、状态显示与控制写入
public sealed class AxisMotionGroupTableControl : UserControl
{
    private const string ConfigFileName = "AxisMotionGroup.config.json";
    private static readonly JsonSerializerOptions JsonFileOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private sealed record FieldDef(string Key, string DisplayName, AxisMotionFieldType FieldType);
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

    /// <summary>写 true，经 <see cref="BoolPulseHoldMs"/> 后写 false（不含 RelMove，见 <see cref="ExecuteRelMoveSignedAsync"/>）。</summary>
    private static readonly HashSet<string> PulseTriggerFieldKeys =
    [
        "AxisHome", "AbsMove", "Stop", "AbsReset"
    ];

    private const int BoolPulseHoldMs = 50;

    private static readonly HashSet<string> BoolStatusFields =
    [
        "DiPot", "DiNot", "DiHome", "Enabling", "HomeDone", "IsAbsEncorder"
    ];

    private static readonly string[] ObsoleteAddressKeys = ["StepPlus", "StepMinus"];

    /// <summary>表格中可编辑并随「保存配置」写入 PLC 的速度/加减速字段（顺序与列含义一致）。</summary>
    private static readonly string[] SpeedProfilePlcWriteFieldKeys =
    [
        "IdleSpeed", "WorkSpeed", "CreepSpeed", "HomeSpeed", "Acceleration", "Deceleration"
    ];

    private static void RemoveObsoleteAddressKeys(Dictionary<string, string> map)
    {
        foreach (string key in ObsoleteAddressKeys)
            map.Remove(key);
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
        new("AbsReset", "ABS_Reset", AxisMotionFieldType.Bool),
        new("DiPot", "DiPot", AxisMotionFieldType.Bool),
        new("DiNot", "DiNot", AxisMotionFieldType.Bool),
        new("DiHome", "DiHome", AxisMotionFieldType.Bool),
        new("Enabling", "Enabling", AxisMotionFieldType.Bool),
        new("HomeDone", "HomeDone", AxisMotionFieldType.Bool),
        new("IsAbsEncorder", "IsAbsEncorder", AxisMotionFieldType.Bool),
        new("ManualSpeed", "手动速度", AxisMotionFieldType.Real),
        new("IdleSpeed", "空载速度", AxisMotionFieldType.Real),
        new("WorkSpeed", "工作速度", AxisMotionFieldType.Real),
        new("CreepSpeed", "缓行速度", AxisMotionFieldType.Real),
        new("HomeSpeed", "回零速度", AxisMotionFieldType.Real),
        new("Acceleration", "加速度", AxisMotionFieldType.Real),
        new("Deceleration", "减速度", AxisMotionFieldType.Real),
        new("SetCurrentPos", "设置当前位置", AxisMotionFieldType.Real),
        new("CurrentPos", "当前位置", AxisMotionFieldType.Real),
        new("CurrentSpeed", "当前速度", AxisMotionFieldType.Real),
        new("CurrentTorque", "当前扭矩", AxisMotionFieldType.Real),
        new("AlarmCode", "报警代码", AxisMotionFieldType.String)
    ];

    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = true,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        ScrollBars = ScrollBars.Both
    };

    private readonly Button _btnAddAxis = new() { Text = "新增轴", Width = 76 };
    private readonly Button _btnRemoveAxis = new() { Text = "删除轴", Width = 76 };
    private readonly Button _btnEditAuth = new() { Text = "编辑解锁", Width = 84 };
    private readonly Button _btnRefreshData = new() { Text = "刷新数据", Width = 76 };
    private readonly Button _btnSaveConfig = new() { Text = "保存配置", Width = 76 };
    private readonly Button _btnLoadConfig = new() { Text = "加载配置", Width = 76 };
    private readonly Button _btnImportTable = new() { Text = "导入表格配置", Width = 108 };

    private readonly TableLayoutPanel _rootTable = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(0) };
    private readonly Panel _motionPanel = new() { Dock = DockStyle.Fill, Padding = new Padding(8, 6, 8, 8), BackColor = Color.WhiteSmoke };
    private readonly Label _lblDisplacement = new() { Text = "位移", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly ComboBox _cmbDisplacement = new() { DropDownStyle = ComboBoxStyle.DropDown, Anchor = AnchorStyles.Left | AnchorStyles.Right };
    private readonly Label _lblManualSpeedSide = new() { Text = "手动速度", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly TrackBar _trkManualSpeed = new() { Minimum = 0, Maximum = 5000, TickFrequency = 500, AutoSize = false, Height = 42, Anchor = AnchorStyles.Left | AnchorStyles.Right };
    private readonly Label _lblManualSpeedValue = new() { Text = "—", AutoSize = true, Anchor = AnchorStyles.Right };
    private readonly Label _lblPanelStatus = new() { Text = "未选择轴", AutoSize = false, Anchor = AnchorStyles.Left | AnchorStyles.Right };
    private readonly Button _btnPanelStepMinus = new() { Text = "Step−", Width = 104, Height = 40 };
    private readonly Button _btnPanelStepPlus = new() { Text = "Step+", Width = 104, Height = 40 };
    private readonly Button _btnPanelJogMinus = new() { Text = "JOG−", Width = 104, Height = 40, BackColor = Color.FromArgb(46, 160, 67), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly Button _btnPanelJogPlus = new() { Text = "JOG+", Width = 104, Height = 40, BackColor = Color.FromArgb(46, 160, 67), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly Button _btnPanelHome = new() { Text = "HOME", Width = 104, Height = 40, BackColor = Color.FromArgb(0, 150, 160), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly Button _btnPanelStop = new() { Text = "STOP", Width = 104, Height = 40, BackColor = Color.FromArgb(220, 120, 40), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly Button _btnPanelAbsReset = new() { Text = "ABS_Reset", Width = 104, Height = 40, BackColor = Color.FromArgb(120, 80, 160), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly Button _btnPanelEnable = new() { Text = "使能", Width = 104, Height = 40, BackColor = Color.FromArgb(140, 40, 40), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly Panel _pnlReadyLed = new() { Width = 44, Height = 44, Margin = new Padding(6, 2, 0, 0) };
    private readonly System.Windows.Forms.Timer _manualSpeedDebounce = new() { Interval = 400 };
    private bool _programmaticTrackBarChange;
    private bool _sidePanelEnablingLit;

    private readonly Dictionary<string, int> _addressColumnIndexByField = [];
    private readonly Dictionary<string, int> _valueColumnIndexByField = [];
    private readonly Dictionary<string, AxisFieldUiMode> _uiModeByField = [];
    private readonly string _configPath;
    private int _addressConfigColumnIndex;
    private bool _suspendAutoSave;
    private bool _canAddDelete = true;
    private bool _canModify = true;
    private bool _editUnlocked;

    public AxisMotionGroupTableControl()
    {
        _configPath = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
        BuildUi();
        BindEvents();
        LoadConfigFromFile();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyInitialGridSelectionAndSync();
    }

    public Func<global::PlcConectCom.PlcCommunicator?>? PlcProvider { get; set; }

    public global::PlcConectCom.PlcCommunicator? PlcInstance { get; set; }

    public Action<string>? LogAction { get; set; }

    public Func<bool>? EditPermissionRequest { get; set; }

    public int StringReadLength { get; set; } = 64;

    public void SetPermission(bool canAddDelete, bool canModify)
    {
        _canAddDelete = canAddDelete;
        _canModify = canModify;
        if (!canModify)
            _editUnlocked = false;

        _btnAddAxis.Enabled = _canAddDelete && _editUnlocked;
        _btnRemoveAxis.Enabled = _canAddDelete && _editUnlocked;
        _btnSaveConfig.Enabled = canModify && _editUnlocked;
        _btnLoadConfig.Enabled = canModify && _editUnlocked;
        _btnImportTable.Enabled = canModify && _editUnlocked;
        _btnRefreshData.Enabled = canModify;
        _btnEditAuth.Enabled = canModify;
        _btnEditAuth.Text = _editUnlocked ? "编辑锁定" : "编辑解锁";
        _grid.ReadOnly = !_editUnlocked;
        ApplyGridColumnReadOnlyForSpeedProfileCells();
        UpdateMotionPanelEnabledState();
    }

    /// <summary>编辑解锁时仅允许改速度/加减速列，避免把布尔状态列当文本改掉。</summary>
    private void ApplyGridColumnReadOnlyForSpeedProfileCells()
    {
        if (_grid.ReadOnly)
            return;

        foreach (DataGridViewColumn col in _grid.Columns)
        {
            if (string.Equals(col.Name, "AxisId", StringComparison.Ordinal))
            {
                col.ReadOnly = false;
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
                col.ReadOnly = !SpeedProfilePlcWriteFieldKeys.Contains(fieldKey, StringComparer.Ordinal);
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

    private void BuildUi()
    {
        _grid.DefaultCellStyle.SelectionBackColor = Color.Gainsboro;
        _grid.DefaultCellStyle.SelectionForeColor = Color.Black;
        _grid.RowTemplate.Height = 32;
        _grid.Dock = DockStyle.Fill;

        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "AxisId", HeaderText = "轴编号", Width = 90 });
        _addressConfigColumnIndex = _grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "AddressConfig",
            HeaderText = "地址配置",
            Width = 90,
            Text = "地址配置",
            UseColumnTextForButtonValue = true
        });

        foreach (var field in FieldDefinitions)
        {
            AxisFieldUiMode uiMode = GetUiMode(field);
            _uiModeByField[field.Key] = uiMode;

            if (!ShowFieldColumnInMainGrid(field))
                continue;

            int valueIndex = _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = $"{field.Key}_Val",
                HeaderText = field.DisplayName,
                Width = 90
            });

            _valueColumnIndexByField[field.Key] = valueIndex;
        }

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 0, 0, 4)
        };

        actions.Controls.AddRange([_btnEditAuth, _btnAddAxis, _btnRemoveAxis, _btnRefreshData, _btnSaveConfig, _btnLoadConfig, _btnImportTable]);

        BuildMotionSidePanel();

        _rootTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _rootTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 244F));
        _rootTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _rootTable.Controls.Add(_grid, 0, 0);
        _rootTable.Controls.Add(_motionPanel, 1, 0);

        Controls.Add(_rootTable);
        Controls.Add(actions);
        AutoScroll = true;
        _grid.ReadOnly = true;
    }

    private void BuildMotionSidePanel()
    {
        _pnlReadyLed.Paint += OnReadyLedPaint;
        _pnlReadyLed.BackColor = Color.Transparent;

        foreach (var b in new[]
                 {
                     _btnPanelJogMinus, _btnPanelJogPlus, _btnPanelHome, _btnPanelStop, _btnPanelAbsReset,
                     _btnPanelEnable
                 })
        {
            b.FlatAppearance.BorderSize = 0;
            b.UseVisualStyleBackColor = false;
        }

        _cmbDisplacement.Dock = DockStyle.Fill;
        _cmbDisplacement.Items.AddRange(["0.1", "0.5", "1", "5", "10", "50", "100"]);
        if (_cmbDisplacement.Items.Count > 0)
            _cmbDisplacement.SelectedIndex = 2;

        var tlp = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 8,
            AutoSize = true,
            Padding = new Padding(0)
        };
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        for (int i = 0; i < 8; i++)
            tlp.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        int r = 0;
        tlp.Controls.Add(_lblDisplacement, 0, r);
        tlp.Controls.Add(_cmbDisplacement, 1, r++);

        var speedRow = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill, AutoSize = true };
        speedRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        speedRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        speedRow.Controls.Add(_trkManualSpeed, 0, 0);
        speedRow.Controls.Add(_lblManualSpeedValue, 1, 0);
        tlp.Controls.Add(_lblManualSpeedSide, 0, r);
        tlp.Controls.Add(speedRow, 1, r++);

        _lblPanelStatus.Margin = new Padding(0, 6, 0, 6);
        tlp.Controls.Add(_lblPanelStatus, 0, r);
        tlp.SetColumnSpan(_lblPanelStatus, 2);
        r++;

        tlp.Controls.Add(_btnPanelStepMinus, 0, r);
        tlp.Controls.Add(_btnPanelStepPlus, 1, r++);
        tlp.Controls.Add(_btnPanelJogMinus, 0, r);
        tlp.Controls.Add(_btnPanelJogPlus, 1, r++);
        tlp.Controls.Add(_btnPanelHome, 0, r);
        tlp.Controls.Add(_btnPanelStop, 1, r++);
        tlp.Controls.Add(_btnPanelAbsReset, 0, r);
        tlp.SetColumnSpan(_btnPanelAbsReset, 2);
        r++;
        var enableRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
        enableRow.Controls.Add(_btnPanelEnable);
        enableRow.Controls.Add(_pnlReadyLed);
        tlp.Controls.Add(enableRow, 0, r);
        tlp.SetColumnSpan(enableRow, 2);

        _motionPanel.Controls.Add(tlp);
    }

    private void OnReadyLedPaint(object? sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = _pnlReadyLed.ClientRectangle;
        rect.Inflate(-4, -4);
        // 接通：亮绿；断开：冷灰，与「使能打开」状态一眼区分
        Color fill = _sidePanelEnablingLit
            ? Color.FromArgb(50, 205, 50)
            : Color.FromArgb(120, 122, 128);
        using var br = new SolidBrush(fill);
        e.Graphics.FillEllipse(br, rect);
        Color ring = _sidePanelEnablingLit ? Color.FromArgb(20, 120, 40) : Color.FromArgb(70, 72, 78);
        using var pen = new Pen(ring, 1.5f);
        e.Graphics.DrawEllipse(pen, rect);
    }

    private void BindEvents()
    {
        _btnEditAuth.Click += (_, _) => ToggleEditAuth();
        _btnAddAxis.Click += (_, _) => AddAxis();
        _btnRemoveAxis.Click += (_, _) => RemoveCurrentAxis();
        _btnRefreshData.Click += async (_, _) => await RefreshAllAxisDataAsync();
        _btnSaveConfig.Click += async (_, _) => await OnSaveConfigClickedAsync();
        _btnLoadConfig.Click += (_, _) => LoadConfigFromFile();
        _btnImportTable.Click += (_, _) => ImportAxisAddressTableFromExcel();
        _grid.CellContentClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex == _addressConfigColumnIndex)
            {
                if (!_canModify || !_editUnlocked)
                    return;
                ShowAddressConfigDialog(_grid.Rows[e.RowIndex]);
            }
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
        _btnPanelStop.Click += async (_, _) => await PulseBoolFieldOnSelectedRowAsync("Stop");
        _btnPanelAbsReset.Click += async (_, _) => await PulseBoolFieldOnSelectedRowAsync("AbsReset");
        _btnPanelEnable.Click += async (_, _) => await InvertAxisEnableOnSelectedRowAsync();

        _trkManualSpeed.ValueChanged += (_, _) =>
        {
            _lblManualSpeedValue.Text = (_trkManualSpeed.Value / 10.0).ToString("F1", CultureInfo.InvariantCulture);
            if (_programmaticTrackBarChange)
                return;
            _manualSpeedDebounce.Stop();
            _manualSpeedDebounce.Start();
        };

        _manualSpeedDebounce.Tick += async (_, _) =>
        {
            _manualSpeedDebounce.Stop();
            await CommitManualSpeedFromTrackBarAsync();
        };
    }

    private async Task OnGridSelectionChangedAsync()
    {
        UpdatePanelStatusLabel();
        await SyncSidePanelFromPlcAsync();
    }

    private void UpdatePanelStatusLabel()
    {
        var ordered = GetOrderedSelectedDataRows();
        if (ordered.Count == 0)
        {
            if (_grid.CurrentRow is { IsNewRow: false, Index: >= 0 } cur)
            {
                string axisId = Convert.ToString(cur.Cells[0].Value, CultureInfo.InvariantCulture)?.Trim() ?? "—";
                _lblPanelStatus.Text = $"当前轴: {axisId}";
            }
            else
                _lblPanelStatus.Text = "未选择轴";

            return;
        }

        if (ordered.Count == 1)
        {
            string axisId = Convert.ToString(ordered[0].Cells[0].Value, CultureInfo.InvariantCulture)?.Trim() ?? "—";
            _lblPanelStatus.Text = $"当前轴: {axisId}";
            return;
        }

        string a = Convert.ToString(ordered[0].Cells[0].Value, CultureInfo.InvariantCulture)?.Trim() ?? "—";
        string b = Convert.ToString(ordered[1].Cells[0].Value, CultureInfo.InvariantCulture)?.Trim() ?? "—";
        _lblPanelStatus.Text = ordered.Count > 2
            ? $"同步两轴: {a} + {b}（共选中{ordered.Count}行，仅前两行参与）"
            : $"同步两轴: {a} + {b}";
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
    /// 运动目标行：未选或一行时仅该行；选中多行时取行号最靠前的两行，用于 JOG± / Step± / HOME 同步。
    /// </summary>
    private IReadOnlyList<DataGridViewRow> GetMotionSyncRows()
    {
        var ordered = GetOrderedSelectedDataRows();
        if (ordered.Count == 0)
        {
            if (_grid.CurrentRow is { IsNewRow: false, Index: >= 0 } cur)
                return new[] { cur };
            return Array.Empty<DataGridViewRow>();
        }

        if (ordered.Count == 1)
            return new[] { ordered[0] };

        return new[] { ordered[0], ordered[1] };
    }

    /// <summary>侧栏与单轴逻辑使用的主行：多选时取行号最小的选中行。</summary>
    private DataGridViewRow? GetSelectedAxisRow()
    {
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

        foreach (var b in new[]
                 {
                     _btnPanelStepMinus, _btnPanelStepPlus, _btnPanelJogMinus, _btnPanelJogPlus, _btnPanelHome, _btnPanelStop,
                     _btnPanelAbsReset, _btnPanelEnable
                 })
            b.Enabled = allow && rowOk;

        _trkManualSpeed.Enabled = allow && rowOk;
        _cmbDisplacement.Enabled = allow && rowOk;

        if (!rowOk || !plcOk)
        {
            _programmaticTrackBarChange = true;
            _trkManualSpeed.Value = _trkManualSpeed.Minimum;
            _programmaticTrackBarChange = false;
            _lblManualSpeedValue.Text = "—";
        }
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
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"{field.DisplayName} 脉冲写入失败 {address}: {ex.Message}");
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
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"{field.DisplayName} 写入失败 {address}: {ex.Message}");
        }
    }

    /// <summary>
    /// Step−/Step+：向 <see cref="RelMoveDistance"/> 写入带符号位移（负/正），再对 <see cref="RelMove"/> 做脉冲触发。
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
        await Task.WhenAll(rows.Select(r => ExecuteRelMoveSignedOnRowAsync(r, plc, signedDistance)));
        await SyncSidePanelFromPlcAsync(GetSelectedAxisRow());
    }

    private async Task ExecuteRelMoveSignedOnRowAsync(DataGridViewRow row, global::PlcConectCom.PlcCommunicator plc, double signedDistance)
    {
        var addresses = row.Tag as Dictionary<string, string> ?? new Dictionary<string, string>();
        string axisId = Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture)?.Trim() ?? "?";

        if (!addresses.TryGetValue("RelMove", out string? relAddr) || string.IsNullOrWhiteSpace(relAddr))
        {
            LogAction?.Invoke($"轴[{axisId}] RelMove 未配置地址，无法执行相对运动。");
            return;
        }

        if (!addresses.TryGetValue("RelMoveDistance", out string? distAddr) || string.IsNullOrWhiteSpace(distAddr))
        {
            LogAction?.Invoke($"轴[{axisId}] 相对位移量(RelMoveDistance) 未配置地址。");
            return;
        }

        relAddr = relAddr.Trim();
        distAddr = distAddr.Trim();

        try
        {
            await plc.WriteDoubleAsync(distAddr, signedDistance);
            await Task.Delay(30);
            await plc.WriteBoolAsync(relAddr, true);
            await Task.Delay(BoolPulseHoldMs);
            await plc.WriteBoolAsync(relAddr, false);
            await RefreshSingleRowDataAsync(row);
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"轴[{axisId}] 相对运动(RelMove)失败: {ex.Message}");
        }
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

        foreach (var field in FieldDefinitions)
        {
            if (!_valueColumnIndexByField.TryGetValue(field.Key, out int colIndex))
                continue;

            addresses.TryGetValue(field.Key, out string? address);
            address = address?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(address))
            {
                SetFieldDisplay(row.Cells[colIndex], field, null, null);
                continue;
            }

            try
            {
                object value = await ReadFieldValueAsync(plc, field.FieldType, address);
                SetFieldDisplay(row.Cells[colIndex], field, value, null);
            }
            catch (Exception ex)
            {
                SetFieldDisplay(row.Cells[colIndex], field, null, "ERR");
                LogAction?.Invoke($"轴[{axisId}] {field.DisplayName} 读取失败 {address}: {ex.Message}");
            }
        }
    }

    private async Task SyncSidePanelFromPlcAsync(DataGridViewRow? row = null)
    {
        row ??= GetSelectedAxisRow();
        if (row is null || row.Tag is not Dictionary<string, string> addresses)
        {
            _sidePanelEnablingLit = false;
            _pnlReadyLed.Invalidate();
            return;
        }

        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
        {
            _sidePanelEnablingLit = false;
            _pnlReadyLed.Invalidate();
            return;
        }

        // 圆形灯：反映「使能是否接通」。优先读反馈位 Enabling；未配置时读 AxisEnable（与侧栏「使能」按钮同一地址）
        _sidePanelEnablingLit = false;
        if (addresses.TryGetValue("Enabling", out string? fbAddr) && !string.IsNullOrWhiteSpace(fbAddr))
        {
            try
            {
                _sidePanelEnablingLit = await plc.ReadBoolAsync(fbAddr.Trim());
            }
            catch
            {
                _sidePanelEnablingLit = false;
            }
        }
        else if (addresses.TryGetValue("AxisEnable", out string? cmdAddr) && !string.IsNullOrWhiteSpace(cmdAddr))
        {
            try
            {
                _sidePanelEnablingLit = await plc.ReadBoolAsync(cmdAddr.Trim());
            }
            catch
            {
                _sidePanelEnablingLit = false;
            }
        }

        _pnlReadyLed.Invalidate();

        if (addresses.TryGetValue("ManualSpeed", out string? spdAddr) && !string.IsNullOrWhiteSpace(spdAddr))
        {
            try
            {
                double v = await plc.ReadDoubleAsync(spdAddr.Trim());
                int tv = (int)Math.Clamp(Math.Round(v * 10.0), _trkManualSpeed.Minimum, _trkManualSpeed.Maximum);
                _programmaticTrackBarChange = true;
                _trkManualSpeed.Value = tv;
                _programmaticTrackBarChange = false;
                _lblManualSpeedValue.Text = (tv / 10.0).ToString("F1", CultureInfo.InvariantCulture);
            }
            catch
            {
                _lblManualSpeedValue.Text = "ERR";
            }
        }
        else
        {
            _programmaticTrackBarChange = true;
            _trkManualSpeed.Value = _trkManualSpeed.Minimum;
            _programmaticTrackBarChange = false;
            _lblManualSpeedValue.Text = "—";
        }
    }

    private async Task CommitManualSpeedFromTrackBarAsync()
    {
        if (!_canModify)
            return;

        var rows = GetMotionSyncRows();
        if (rows.Count == 0)
            return;

        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
            return;

        double value = _trkManualSpeed.Value / 10.0;
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
    }

    public void AddAxis(string? axisId = null)
    {
        if (!_editUnlocked)
            return;

        int next = _grid.Rows.Count + 1;
        int rowIndex = _grid.Rows.Add(axisId ?? $"Axis{next}");
        _grid.Rows[rowIndex].Tag = new Dictionary<string, string>();
        _grid.ClearSelection();
        _grid.Rows[rowIndex].Selected = true;
        _grid.CurrentCell = _grid.Rows[rowIndex].Cells[0];
        UpdatePanelStatusLabel();
        UpdateMotionPanelEnabledState();
        SaveConfigToFile();
    }

    public void RemoveCurrentAxis()
    {
        if (!_editUnlocked)
            return;

        if (_grid.SelectedRows.Count == 0)
            return;

        _grid.Rows.RemoveAt(_grid.SelectedRows[0].Index);
        SaveConfigToFile();
    }

    public void SetAxes(IEnumerable<AxisMotionAddressConfig> configs)
    {
        _suspendAutoSave = true;
        _grid.Rows.Clear();
        foreach (var cfg in configs)
        {
            int rowIndex = _grid.Rows.Add(cfg.AxisId);
            var row = _grid.Rows[rowIndex];
            var addr = cfg.Addresses.ToDictionary(x => x.Key, x => x.Value);
            RemoveObsoleteAddressKeys(addr);
            row.Tag = addr;
        }
        _suspendAutoSave = false;
        SaveConfigToFile();
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
        if (IsDisposed || !_grid.IsHandleCreated)
            return;

        UpdateMotionPanelEnabledState();
        try
        {
            if (_grid.Rows.Count > 0)
            {
                _grid.ClearSelection();
                DataGridViewRow r0 = _grid.Rows[0];
                r0.Selected = true;
                _grid.CurrentCell = r0.Cells[0];
            }
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"轴表初始选行: {ex.Message}");
        }

        UpdatePanelStatusLabel();
        _ = SyncSidePanelFromPlcAsync();
    }

    public IReadOnlyList<AxisMotionAddressConfig> GetAxes()
    {
        var result = new List<AxisMotionAddressConfig>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            var cfg = new AxisMotionAddressConfig
            {
                AxisId = Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty
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

    public async Task RefreshAllAxisDataAsync()
    {
        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
            return;

        foreach (DataGridViewRow row in _grid.Rows)
        {
            string axisId = Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture) ?? string.Empty;
            var addresses = row.Tag as Dictionary<string, string> ?? new Dictionary<string, string>();
            foreach (var field in FieldDefinitions)
            {
                if (!_valueColumnIndexByField.TryGetValue(field.Key, out int colIndex))
                    continue;

                addresses.TryGetValue(field.Key, out string? address);
                address = address?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(address))
                {
                    SetFieldDisplay(row.Cells[colIndex], field, null, null);
                    continue;
                }

                try
                {
                    object value = await ReadFieldValueAsync(plc, field.FieldType, address);
                    SetFieldDisplay(row.Cells[colIndex], field, value, null);
                }
                catch (Exception ex)
                {
                    SetFieldDisplay(row.Cells[colIndex], field, null, "ERR");
                    LogAction?.Invoke($"轴[{axisId}] {field.DisplayName} 读取失败 {address}: {ex.Message}");
                }
            }
        }

        UpdateMotionPanelEnabledState();
        await SyncSidePanelFromPlcAsync();
    }

    private void ToggleEditAuth()
    {
        if (!_canModify)
            return;

        if (_editUnlocked)
        {
            _editUnlocked = false;
            SetPermission(_canAddDelete, _canModify);
            return;
        }

        bool ok = EditPermissionRequest?.Invoke() ?? false;
        if (!ok)
            return;

        _editUnlocked = true;
        SetPermission(_canAddDelete, _canModify);
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
                cell.Style.Font = new Font("Segoe UI Symbol", 14f, FontStyle.Bold);
                cell.Style.ForeColor = state ? Color.LawnGreen : Color.Red;
                cell.Style.SelectionForeColor = cell.Style.ForeColor;
                cell.Style.SelectionBackColor = Color.Gainsboro;
            }

            return;
        }

        cell.Value = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
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
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };

        cfgGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "字段", ReadOnly = true, FillWeight = 38 });
        cfgGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "地址", FillWeight = 62 });

        foreach (var field in FieldDefinitions)
        {
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

        if (dialog.ShowDialog(this) != DialogResult.OK)
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
        (["相对位移量", "相对位移", "单步位移"], "RelMoveDistance"),
        (["GoABS"], "AbsMove"),
        (["GoRel"], "RelMove"),
        (["定位停止"], "Stop"),
        (["绝对值轴清零"], "AbsReset"),
        (["正极限"], "DiPot"),
        (["负极限"], "DiNot"),
        (["原点信号"], "DiHome"),
        (["使能中"], "Enabling"),
        (["回零完成"], "HomeDone"),
        (["当前位置"], "CurrentPos"),
        (["当前速度"], "CurrentSpeed"),
        (["当前扭矩"], "CurrentTorque"),
        (["报警码", "轴报警"], "AlarmCode")
    ];

    private void ImportAxisAddressTableFromExcel()
    {
        if (!_editUnlocked || !_canModify)
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
                    string addr = BuildPerAxisAddress(item.MemType, item.BaseOffset, axisIndex, item.ElemKind, item.StringChars);
                    if (!string.IsNullOrWhiteSpace(addr))
                        map[item.FieldKey] = addr;
                }

                gridRow.Tag = map;
            }

            SaveConfigToFile();
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
            foreach (var cfg in configs)
            {
                int rowIndex = _grid.Rows.Add(cfg.AxisId);
                var addr = cfg.Addresses?.ToDictionary(x => x.Key, x => x.Value) ?? new Dictionary<string, string>();
                RemoveObsoleteAddressKeys(addr);
                _grid.Rows[rowIndex].Tag = addr;
            }
            _suspendAutoSave = false;

            UpdateMotionPanelEnabledState();
            UpdatePanelStatusLabel();
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

            string axisId = Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture)?.Trim() ?? "?";
            var addresses = row.Tag as Dictionary<string, string> ?? new Dictionary<string, string>();

            foreach (string fieldKey in SpeedProfilePlcWriteFieldKeys)
            {
                if (!_valueColumnIndexByField.TryGetValue(fieldKey, out int colIndex))
                    continue;

                addresses.TryGetValue(fieldKey, out string? addr);
                addr = addr?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(addr))
                    continue;

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
                }
                catch (Exception ex)
                {
                    LogAction?.Invoke($"轴[{axisId}] {GetFieldDisplayName(fieldKey)} 写入失败 {addr}: {ex.Message}");
                }
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
            string json = JsonSerializer.Serialize(configs, JsonFileOptions);
            File.WriteAllText(_configPath, json);
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
            _manualSpeedDebounce.Dispose();
        base.Dispose(disposing);
    }
}
