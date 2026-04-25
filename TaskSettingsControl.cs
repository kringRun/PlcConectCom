using System.Globalization;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PlcConectCom.WinFormsTest;

// 触发条件数据源类型
public enum TriggerConditionSourceType
{
    DI地址,
    DO地址,
    按钮类变量地址,
    报警数据地址,
    延时参数,
    参数表地址,
    HMI显示数据地址,
    Socket分片标记
}

// 多条件逻辑（旧版触发器保留序列化兼容）
public enum TriggerConditionLogicType
{
    且,
    或
}

public sealed class TaskSourceAddressOption
{
    public string Name { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;

    public override string ToString()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return Address;

        if (string.Equals(Name, Address, StringComparison.OrdinalIgnoreCase))
            return Name;

        return $"{Name} ({Address})";
    }
}

public enum TaskExecutionType
{
    报警地址赋值,
    DO地址赋值,
    Socket通讯发送数据,
    Socket通讯等待数据,
    串口通讯发送数据,
    串口通讯等待数据
}

public sealed class TriggerConditionConfig
{
    public TriggerConditionSourceType SourceType { get; set; } = TriggerConditionSourceType.DI地址;

    public string SourceAddress { get; set; } = string.Empty;

    public string Comparator { get; set; } = "==";

    public string CompareValue { get; set; } = string.Empty;

    /// <summary>PLC 读取：AUTO、BOOL、INT、REAL、DWORD、STRING。</summary>
    public string ConditionReadValueType { get; set; } = "AUTO";
}

public sealed class TaskExecutionConfig
{
    public TaskExecutionType ExecutionType { get; set; } = TaskExecutionType.DO地址赋值;

    public string Target { get; set; } = string.Empty;

    public string Payload { get; set; } = string.Empty;

    /// <summary>Socket 步骤：按通讯实例里配置的「结果字段标记」取某一维；空则结果存储写入完整回包（单路 legacy 或与多路写入组并存）。</summary>
    public string SocketResultSliceName { get; set; } = string.Empty;
}

/// <summary>Socket 回包：一分片（标记名或整包）对应一路 PLC 结果地址与类型。</summary>
public sealed class TaskSocketResultSink
{
    /// <summary>通讯实例「结果字段标记」名称；空表示整包原文。</summary>
    public string SliceName { get; set; } = string.Empty;

    public string StorageAddress { get; set; } = string.Empty;

    public string ValueType { get; set; } = "STRING";
}

/// <summary>单步：条件 → 执行 → 结果写入全局 PLC 地址。</summary>
public sealed class TaskPipelineStepConfig
{
    public TriggerConditionConfig Condition { get; set; } = new();

    public TaskExecutionConfig Execution { get; set; } = new();

    public string ResultStorageAddress { get; set; } = string.Empty;

    public string ResultStorageValueType { get; set; } = "STRING";

    /// <summary>Socket 步骤可多组：分片 + 存储地址 + 类型；非空时优先于单路 <see cref="ResultStorageAddress"/>。</summary>
    public List<TaskSocketResultSink> SocketResultSinks { get; set; } = [];
}

public enum AutomatedTaskRunMode
{
    后台轮询,
    报警事件触发
}

public enum AutomatedTaskRunnerState
{
    已停止,
    运行中,
    错误
}

public sealed class AutomatedTaskConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string TaskName { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public AutomatedTaskRunMode RunMode { get; set; } = AutomatedTaskRunMode.后台轮询;

    public int PollingIntervalMs { get; set; } = 500;

    /// <summary>报警事件模式下过滤报警代码或名称（包含匹配）；空表示任意报警。</summary>
    public string AlarmTriggerFilter { get; set; } = string.Empty;

    public List<TaskPipelineStepConfig> Steps { get; set; } = [];
}

// 旧版 JSON 反序列化用
public sealed class TaskTriggerConfig
{
    public string TriggerName { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public TriggerConditionLogicType ConditionLogic { get; set; } = TriggerConditionLogicType.且;

    public List<TriggerConditionConfig> Conditions { get; set; } = [];

    public TriggerConditionConfig? Condition { get; set; }

    public TaskExecutionConfig Execution { get; set; } = new();
}

/// <summary>自动化任务配置页：串行管线、轮询/报警事件、启动停止与状态。</summary>
public sealed class TaskSettingsControl : UserControl
{
    private const string SocketConditionAddressPrefix = "socket-marker:";
    private const string ConfigFileName = "TaskAutomation.config.json";
    private const string LegacyConfigFileName = "TaskSettings.config.json";
    private static readonly JsonSerializerOptions JsonFileOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly DataGridView _grid = new()
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

    private readonly Button _btnEditAuth = new() { Text = "编辑解锁", Width = 84 };
    private readonly Button _btnAdd = new() { Text = "新增任务", Width = 92 };
    private readonly Button _btnEdit = new() { Text = "编辑任务", Width = 92 };
    private readonly Button _btnDelete = new() { Text = "删除任务", Width = 92 };
    private readonly System.Windows.Forms.Timer _stateTimer = new() { Interval = 400 };

    private readonly string _configPath;
    private readonly List<AutomatedTaskConfig> _tasks = [];
    private bool _canModify = true;
    private bool _editUnlocked;
    private TaskPipelineRunner? _runner;
    private int _nameCol;
    private int _modeCol;
    private int _intervalCol;
    private int _stepsCol;
    private int _stateCol;
    private int _enabledCol;
    private int _startCol;
    private int _stopCol;

    public TaskSettingsControl()
    {
        _configPath = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
        BuildUi();
        BindEvents();
        LoadConfig();
    }

    public Func<bool>? EditPermissionRequest { get; set; }

    public Action<string>? LogAction { get; set; }

    public Func<IReadOnlyList<TaskSourceAddressOption>>? DiAddressProvider { get; set; }

    public Func<IReadOnlyList<TaskSourceAddressOption>>? DoAddressProvider { get; set; }

    public Func<IReadOnlyList<TaskSourceAddressOption>>? ButtonVarAddressProvider { get; set; }

    public Func<IReadOnlyList<TaskSourceAddressOption>>? AlarmAddressProvider { get; set; }

    /// <summary>主参数表名称/地址。</summary>
    public Func<IReadOnlyList<TaskSourceAddressOption>>? ParameterTableAddressProvider { get; set; }

    /// <summary>HMI 显示数据表名称/地址。</summary>
    public Func<IReadOnlyList<TaskSourceAddressOption>>? HmiDisplayAddressProvider { get; set; }

    public Func<IReadOnlyList<TaskSourceAddressOption>>? SocketInstanceProvider { get; set; }

    public Func<IReadOnlyList<TaskSourceAddressOption>>? SerialInstanceProvider { get; set; }

    /// <summary>参数为 socket-client:名称 等实例键，返回该实例已配置的标记名列表。</summary>
    public Func<string, IReadOnlyList<string>>? SocketResultFieldNamesProvider { get; set; }

    public Func<TaskExecutionConfig, Task<TaskStepExecuteResult>>? ExecuteTaskActionAsync { get; set; }

    public IReadOnlyList<AutomatedTaskConfig> GetAutomatedTasks() => _tasks;

    public void BindRunner(TaskPipelineRunner runner)
    {
        _runner = runner;
    }

    public void SetPermission(bool canModify)
    {
        _canModify = canModify;
        if (!canModify)
            _editUnlocked = false;

        _btnEditAuth.Enabled = canModify;
        _btnEditAuth.Text = _editUnlocked ? "编辑锁定" : "编辑解锁";
        _btnAdd.Enabled = canModify && _editUnlocked;
        _btnEdit.Enabled = canModify && _editUnlocked;
        _btnDelete.Enabled = canModify && _editUnlocked;
    }

    private void BuildUi()
    {
        _nameCol = _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "任务名", FillWeight = 18 });
        _modeCol = _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "运行模式", FillWeight = 12 });
        _intervalCol = _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "轮询ms", FillWeight = 8 });
        _stepsCol = _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "步数", FillWeight = 6 });
        _stateCol = _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "状态", FillWeight = 14 });
        _enabledCol = _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "启用", FillWeight = 6 });
        _startCol = _grid.Columns.Add(new DataGridViewButtonColumn
        {
            HeaderText = "启动",
            Text = "启动",
            UseColumnTextForButtonValue = true,
            FillWeight = 8
        });
        _stopCol = _grid.Columns.Add(new DataGridViewButtonColumn
        {
            HeaderText = "停止",
            Text = "停止",
            UseColumnTextForButtonValue = true,
            FillWeight = 8
        });

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 0, 0, 4)
        };
        toolbar.Controls.AddRange([_btnEditAuth, _btnAdd, _btnEdit, _btnDelete]);

        Controls.Add(_grid);
        Controls.Add(toolbar);
    }

    private void BindEvents()
    {
        _btnEditAuth.Click += (_, _) => ToggleEditAuth();
        _btnAdd.Click += (_, _) => AddTask();
        _btnEdit.Click += (_, _) => EditTask();
        _btnDelete.Click += (_, _) => DeleteTask();
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && _editUnlocked)
                EditTask();
        };
        _grid.CellContentClick += OnGridCellContentClick;
        _stateTimer.Tick += (_, _) => RefreshStates();
        _stateTimer.Start();
    }

    private void OnGridCellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || _runner is null)
            return;

        if (_grid.Rows[e.RowIndex].Tag is not string taskId)
            return;

        if (e.ColumnIndex == _startCol)
            _runner.StartTask(taskId);
        else if (e.ColumnIndex == _stopCol)
            _runner.StopTask(taskId);
    }

    private void RefreshStates()
    {
        if (_runner is null)
            return;

        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is not string taskId)
                continue;

            AutomatedTaskRunnerState st = _runner.GetState(taskId);
            string? detail = _runner.GetStateDetail(taskId);
            row.Cells[_stateCol].Value = detail is { Length: > 0 }
                ? $"{st} — {detail}"
                : st.ToString();
        }
    }

    private void ToggleEditAuth()
    {
        if (!_canModify)
            return;

        if (_editUnlocked)
        {
            _editUnlocked = false;
            SetPermission(_canModify);
            return;
        }

        bool ok = EditPermissionRequest?.Invoke() ?? false;
        if (!ok)
            return;

        _editUnlocked = true;
        SetPermission(_canModify);
    }

    private void AddTask()
    {
        var created = ShowAutomatedTaskDialog();
        if (created is null)
            return;

        _tasks.Add(created);
        SaveConfig();
        BindGrid();
    }

    private void EditTask()
    {
        int index = GetSelectedIndex();
        if (index < 0)
            return;

        var edited = ShowAutomatedTaskDialog(_tasks[index]);
        if (edited is null)
            return;

        _tasks[index] = edited;
        SaveConfig();
        BindGrid();
    }

    private void DeleteTask()
    {
        int index = GetSelectedIndex();
        if (index < 0)
            return;

        _runner?.StopTask(_tasks[index].Id);
        _tasks.RemoveAt(index);
        SaveConfig();
        BindGrid();
    }

    private int GetSelectedIndex()
    {
        if (_grid.SelectedRows.Count == 0)
            return -1;

        return _grid.SelectedRows[0].Index;
    }

    private void BindGrid()
    {
        _grid.Rows.Clear();
        foreach (var t in _tasks)
        {
            int r = _grid.Rows.Add(
                t.TaskName,
                t.RunMode.ToString(),
                t.RunMode == AutomatedTaskRunMode.后台轮询 ? t.PollingIntervalMs.ToString(CultureInfo.InvariantCulture) : "—",
                t.Steps.Count.ToString(CultureInfo.InvariantCulture),
                _runner?.GetState(t.Id).ToString() ?? AutomatedTaskRunnerState.已停止.ToString(),
                t.Enabled ? "是" : "否",
                "启动",
                "停止");
            _grid.Rows[r].Tag = t.Id;
        }
    }

    private AutomatedTaskConfig? ShowAutomatedTaskDialog(AutomatedTaskConfig? existing = null)
    {
        using var dialog = new Form
        {
            Text = existing is null ? "新增自动化任务" : "编辑自动化任务",
            Width = 1120,
            Height = 720,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var txtName = new TextBox { Dock = DockStyle.Fill };
        var chkEnabled = new CheckBox { Text = "启用", AutoSize = true, Checked = true };
        var cmbMode = new ComboBox { Dock = DockStyle.Left, Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
        cmbMode.Items.AddRange(Enum.GetValues<AutomatedTaskRunMode>().Cast<object>().ToArray());
        cmbMode.SelectedIndex = 0;
        var numInterval = new NumericUpDown { Minimum = 50, Maximum = 60000, Value = 500, Width = 100 };
        var txtAlarmFilter = new TextBox { Dock = DockStyle.Fill };

        var stepGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            Height = 280,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            ScrollBars = ScrollBars.Both
        };
        stepGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "StepNo", HeaderText = "步", Width = 36 });
        stepGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "CondSrc", HeaderText = "条件数据源", Width = 100 });
        stepGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "CondAddr", HeaderText = "条件地址", Width = 130 });
        stepGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "CondRead", HeaderText = "PLC读为", Width = 64 });
        stepGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "CondCmp", HeaderText = "比较", Width = 48 });
        stepGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "CondVal", HeaderText = "比较值", Width = 80 });
        stepGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ExecType", HeaderText = "执行类型", Width = 120 });
        stepGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ExecTgt", HeaderText = "执行目标", Width = 160 });
        stepGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ExecData", HeaderText = "执行数据", Width = 140 });
        stepGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ResultWrite", HeaderText = "结果写入", Width = 280 });

        var lblStepHint = new Label
        {
            Text = "下表为各步明细；双击行或点「编辑步骤」可打开「条件 + 执行」完整编辑。",
            AutoSize = true,
            Padding = new Padding(0, 0, 0, 4)
        };

        var btnStepAdd = new Button { Text = "新增步骤", Width = 88 };
        var btnStepEdit = new Button { Text = "编辑步骤", Width = 88 };
        var btnStepDel = new Button { Text = "删除步骤", Width = 88 };
        var stepBtns = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Height = 36 };
        stepBtns.Controls.AddRange([btnStepAdd, btnStepEdit, btnStepDel]);
        var stepPanel = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty };
        var stepHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty };
        lblStepHint.Dock = DockStyle.Top;
        stepGrid.Dock = DockStyle.Fill;
        stepHost.Controls.Add(lblStepHint);
        stepHost.Controls.Add(stepGrid);
        stepPanel.Controls.Add(stepBtns);
        stepPanel.Controls.Add(stepHost);

        var editingSteps = existing?.Steps.Select(CloneStep).ToList() ?? [];

        void BindStepGrid()
        {
            stepGrid.Rows.Clear();
            for (int i = 0; i < editingSteps.Count; i++)
            {
                TaskPipelineStepConfig s = editingSteps[i];
                TriggerConditionConfig c = s.Condition;
                TaskExecutionConfig e = s.Execution;
                stepGrid.Rows.Add(
                    (i + 1).ToString(CultureInfo.InvariantCulture),
                    c.SourceType.ToString(),
                    c.SourceAddress,
                    c.ConditionReadValueType,
                    c.Comparator,
                    c.CompareValue,
                    e.ExecutionType.ToString(),
                    e.Target,
                    e.Payload,
                    FormatResultWriteSummary(s));
            }
        }

        void EditSelectedStep()
        {
            if (stepGrid.SelectedRows.Count == 0)
                return;
            int i = stepGrid.SelectedRows[0].Index;
            if (i < 0 || i >= editingSteps.Count)
                return;
            TaskPipelineStepConfig? step = ShowPipelineStepDialog(editingSteps[i]);
            if (step is null)
                return;
            editingSteps[i] = step;
            BindStepGrid();
        }

        var topTbl = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 0, 0, 8) };
        topTbl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        topTbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        int tr = 0;
        topTbl.Controls.Add(new Label { Text = "任务名称", AutoSize = true, Anchor = AnchorStyles.Left }, 0, tr);
        topTbl.Controls.Add(txtName, 1, tr++);
        topTbl.Controls.Add(new Label { Text = "", AutoSize = true }, 0, tr);
        topTbl.Controls.Add(chkEnabled, 1, tr++);
        topTbl.Controls.Add(new Label { Text = "运行模式", AutoSize = true, Anchor = AnchorStyles.Left }, 0, tr);
        topTbl.Controls.Add(cmbMode, 1, tr++);
        topTbl.Controls.Add(new Label { Text = "轮询间隔(ms)", AutoSize = true, Anchor = AnchorStyles.Left }, 0, tr);
        topTbl.Controls.Add(numInterval, 1, tr++);
        topTbl.Controls.Add(new Label { Text = "报警过滤(子串)", AutoSize = true, Anchor = AnchorStyles.Left }, 0, tr);
        topTbl.Controls.Add(txtAlarmFilter, 1, tr++);
        topTbl.Controls.Add(new Label { Text = "串行步骤", AutoSize = true, Anchor = AnchorStyles.Left }, 0, tr);
        topTbl.Controls.Add(new Label { Text = "（先启动任务后，报警模式才待命）", AutoSize = true }, 1, tr++);

        body.Controls.Add(topTbl, 0, 0);
        body.Controls.Add(stepPanel, 0, 1);
        root.Controls.Add(body);

        btnStepAdd.Click += (_, _) =>
        {
            var step = ShowPipelineStepDialog();
            if (step is null)
                return;
            editingSteps.Add(step);
            BindStepGrid();
        };
        btnStepEdit.Click += (_, _) => EditSelectedStep();
        stepGrid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= editingSteps.Count)
                return;
            stepGrid.ClearSelection();
            stepGrid.Rows[e.RowIndex].Selected = true;
            EditSelectedStep();
        };
        btnStepDel.Click += (_, _) =>
        {
            if (stepGrid.SelectedRows.Count == 0)
                return;
            int i = stepGrid.SelectedRows[0].Index;
            if (i < 0 || i >= editingSteps.Count)
                return;
            editingSteps.RemoveAt(i);
            BindStepGrid();
        };

        if (existing is not null)
        {
            txtName.Text = existing.TaskName;
            chkEnabled.Checked = existing.Enabled;
            cmbMode.SelectedItem = existing.RunMode;
            numInterval.Value = Math.Clamp(existing.PollingIntervalMs, (int)numInterval.Minimum, (int)numInterval.Maximum);
            txtAlarmFilter.Text = existing.AlarmTriggerFilter;
        }

        BindStepGrid();

        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8)
        };
        var btnOk = new Button { Text = "确定", Width = 72, DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "取消", Width = 72, DialogResult = DialogResult.Cancel };
        btnPanel.Controls.Add(btnOk);
        btnPanel.Controls.Add(btnCancel);
        dialog.Controls.Add(btnPanel);
        dialog.Controls.Add(root);
        dialog.AcceptButton = btnOk;
        dialog.CancelButton = btnCancel;

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return null;

        string name = txtName.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "任务名称不能为空。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        if (editingSteps.Count == 0)
        {
            MessageBox.Show(this, "请至少配置一个串行步骤。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        var result = existing ?? new AutomatedTaskConfig();
        result.TaskName = name;
        result.Enabled = chkEnabled.Checked;
        result.RunMode = cmbMode.SelectedItem is AutomatedTaskRunMode m ? m : AutomatedTaskRunMode.后台轮询;
        result.PollingIntervalMs = (int)numInterval.Value;
        result.AlarmTriggerFilter = txtAlarmFilter.Text.Trim();
        result.Steps = editingSteps.Select(CloneStep).ToList();
        return result;
    }

    private TaskPipelineStepConfig? ShowPipelineStepDialog(TaskPipelineStepConfig? existing = null)
    {
        const string sinkSliceFullDisplay = "(整包)";

        using var dialog = new Form
        {
            Text = existing is null ? "新增步骤" : "编辑步骤",
            Width = 720,
            Height = 580,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var tabs = new TabControl { Dock = DockStyle.Fill };

        var tabCond = new TabPage { Text = "触发条件", Padding = new Padding(10) };
        var condRoot = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 5,
            Padding = new Padding(0)
        };
        condRoot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        condRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        var cSrc = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        cSrc.Items.AddRange(Enum.GetValues<TriggerConditionSourceType>().Cast<object>().ToArray());
        cSrc.SelectedIndex = 0;
        var cAddr = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        var cRead = new ComboBox { Dock = DockStyle.Left, Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
        cRead.Items.AddRange(["AUTO", "BOOL", "INT", "REAL", "DWORD", "STRING"]);
        cRead.SelectedIndex = 0;
        var cComp = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        cComp.Items.AddRange(["==", "!=", ">", ">=", "<", "<="]);
        cComp.SelectedIndex = 0;
        var txtCmp = new TextBox { Dock = DockStyle.Fill };

        void RebindCondAddresses(string? currentAddress = null)
        {
            string current = currentAddress ?? (cAddr.SelectedItem as TaskSourceAddressOption)?.Address ?? cAddr.Text;
            cAddr.Items.Clear();
            if (cSrc.SelectedItem is not TriggerConditionSourceType source)
                return;

            var options = source switch
            {
                TriggerConditionSourceType.DI地址 => DiAddressProvider?.Invoke() ?? [],
                TriggerConditionSourceType.DO地址 => DoAddressProvider?.Invoke() ?? [],
                TriggerConditionSourceType.按钮类变量地址 => ButtonVarAddressProvider?.Invoke() ?? [],
                TriggerConditionSourceType.报警数据地址 => AlarmAddressProvider?.Invoke() ?? [],
                TriggerConditionSourceType.参数表地址 => ParameterTableAddressProvider?.Invoke() ?? [],
                TriggerConditionSourceType.HMI显示数据地址 => HmiDisplayAddressProvider?.Invoke() ?? [],
                TriggerConditionSourceType.Socket分片标记 => GetSocketConditionAddressOptions(),
                TriggerConditionSourceType.延时参数 => [],
                _ => []
            };

            bool isDelay = source == TriggerConditionSourceType.延时参数;
            bool isSocketMarker = source == TriggerConditionSourceType.Socket分片标记;
            cAddr.Enabled = !isDelay;
            if (isDelay)
            {
                cAddr.Text = string.Empty;
                cRead.Enabled = true;
                return;
            }

            cAddr.Items.AddRange(options.Cast<object>().ToArray());
            SelectSourceAddress(cAddr, current);

            // 非 PLC 地址来源时不需要「PLC读取为」。
            cRead.Enabled = !isSocketMarker;
            if (isSocketMarker)
                cRead.SelectedItem = "AUTO";
        }

        cSrc.SelectedIndexChanged += (_, _) => RebindCondAddresses();

        int cr = 0;
        condRoot.Controls.Add(new Label { Text = "条件数据源", AutoSize = true, Anchor = AnchorStyles.Left }, 0, cr);
        condRoot.Controls.Add(cSrc, 1, cr++);
        condRoot.Controls.Add(new Label { Text = "条件地址", AutoSize = true, Anchor = AnchorStyles.Left }, 0, cr);
        condRoot.Controls.Add(cAddr, 1, cr++);
        condRoot.Controls.Add(new Label { Text = "PLC 读取为", AutoSize = true, Anchor = AnchorStyles.Left }, 0, cr);
        condRoot.Controls.Add(cRead, 1, cr++);
        condRoot.Controls.Add(new Label { Text = "比较符", AutoSize = true, Anchor = AnchorStyles.Left }, 0, cr);
        condRoot.Controls.Add(cComp, 1, cr++);
        condRoot.Controls.Add(new Label { Text = "比较值", AutoSize = true, Anchor = AnchorStyles.Left }, 0, cr);
        condRoot.Controls.Add(txtCmp, 1, cr++);
        tabCond.Controls.Add(condRoot);

        var tabExec = new TabPage { Text = "执行与结果存储", Padding = new Padding(10) };
        var execRoot = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5, Padding = new Padding(0) };
        execRoot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        execRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        execRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        execRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        execRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        execRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        execRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var cmbExecType = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        cmbExecType.Items.AddRange(Enum.GetValues<TaskExecutionType>().Cast<object>().ToArray());
        cmbExecType.SelectedIndex = 0;
        var cmbTarget = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
        var txtPayload = new TextBox { Dock = DockStyle.Fill };
        var txtStoreAddr = new TextBox { Dock = DockStyle.Fill };
        var cmbStoreType = new ComboBox { Dock = DockStyle.Left, Width = 140, DropDownStyle = ComboBoxStyle.DropDownList };
        cmbStoreType.Items.AddRange(["STRING", "BOOL", "INT", "DWORD", "REAL"]);
        cmbStoreType.SelectedIndex = 0;

        var pnlResultHost = new Panel { Dock = DockStyle.Fill, MinimumSize = new Size(200, 200), Margin = Padding.Empty };
        var tblLegacy = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 2,
            Padding = Padding.Empty
        };
        tblLegacy.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tblLegacy.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        tblLegacy.Controls.Add(new Label { Text = "结果存储地址", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        tblLegacy.Controls.Add(txtStoreAddr, 1, 0);
        tblLegacy.Controls.Add(new Label { Text = "存储类型", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        tblLegacy.Controls.Add(cmbStoreType, 1, 1);

        var pnlSocketBlock = new Panel { Dock = DockStyle.Fill, Visible = false, Padding = Padding.Empty };
        var sinkToolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 0, 0, 4)
        };
        var btnAddSink = new Button { Text = "添加写入组", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
        var btnRemoveSink = new Button { Text = "删除所选行", AutoSize = true };
        sinkToolbar.Controls.Add(btnAddSink);
        sinkToolbar.Controls.Add(btnRemoveSink);
        var gridSinks = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ScrollBars = ScrollBars.Vertical
        };
        var colSlice = new DataGridViewComboBoxColumn
        {
            Name = "Slice",
            HeaderText = "Socket分片(标记)",
            FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton
        };
        gridSinks.Columns.Add(colSlice);
        gridSinks.Columns.Add(new DataGridViewTextBoxColumn { Name = "Addr", HeaderText = "结果存储地址" });
        var colSinkVt = new DataGridViewComboBoxColumn
        {
            Name = "VType",
            HeaderText = "存储类型",
            FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton
        };
        colSinkVt.Items.AddRange("STRING", "BOOL", "INT", "DWORD", "REAL");
        gridSinks.Columns.Add(colSinkVt);
        pnlSocketBlock.Controls.Add(gridSinks);
        pnlSocketBlock.Controls.Add(sinkToolbar);
        pnlResultHost.Controls.Add(pnlSocketBlock);
        pnlResultHost.Controls.Add(tblLegacy);

        bool IsSocketExecSelected() =>
            cmbExecType.SelectedItem is TaskExecutionType.Socket通讯发送数据 or TaskExecutionType.Socket通讯等待数据;

        bool SliceColumnItemsContains(DataGridViewComboBoxColumn col, string s)
        {
            foreach (object o in col.Items)
            {
                if (o?.ToString() is string t && string.Equals(t, s, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        void RefreshSinkSliceComboItems()
        {
            if (gridSinks.Columns["Slice"] is not DataGridViewComboBoxColumn colSlice)
                return;

            var rowSlices = new List<string?>();
            foreach (DataGridViewRow row in gridSinks.Rows)
            {
                if (row.IsNewRow)
                    continue;
                rowSlices.Add(row.Cells["Slice"].Value?.ToString());
            }

            colSlice.Items.Clear();
            colSlice.Items.Add(sinkSliceFullDisplay);

            string key = cmbTarget.SelectedItem is TaskSourceAddressOption opt ? opt.Address : cmbTarget.Text.Trim();
            if (SocketResultFieldNamesProvider is not null)
            {
                foreach (string n in SocketResultFieldNamesProvider(key))
                {
                    if (string.IsNullOrWhiteSpace(n))
                        continue;
                    if (!SliceColumnItemsContains(colSlice, n))
                        colSlice.Items.Add(n);
                }
            }

            int idx = 0;
            foreach (DataGridViewRow row in gridSinks.Rows)
            {
                if (row.IsNewRow)
                    continue;

                string? cur = idx < rowSlices.Count ? rowSlices[idx] : null;
                idx++;

                if (string.IsNullOrWhiteSpace(cur)
                    || string.Equals(cur.Trim(), sinkSliceFullDisplay, StringComparison.Ordinal)
                    || cur.Trim().StartsWith("(整包", StringComparison.Ordinal))
                {
                    row.Cells["Slice"].Value = sinkSliceFullDisplay;
                    continue;
                }

                string t = cur.Trim();
                if (!SliceColumnItemsContains(colSlice, t))
                    colSlice.Items.Add(t);

                row.Cells["Slice"].Value = t;
            }
        }

        void RebindExecTargets(string? preset = null)
        {
            string cur = preset ?? (cmbTarget.SelectedItem as TaskSourceAddressOption)?.Address ?? cmbTarget.Text;
            cmbTarget.Items.Clear();
            if (cmbExecType.SelectedItem is not TaskExecutionType et)
                return;

            var options = et switch
            {
                TaskExecutionType.报警地址赋值 => AlarmAddressProvider?.Invoke() ?? [],
                TaskExecutionType.DO地址赋值 => DoAddressProvider?.Invoke() ?? [],
                TaskExecutionType.Socket通讯发送数据 => SocketInstanceProvider?.Invoke() ?? [],
                TaskExecutionType.Socket通讯等待数据 => SocketInstanceProvider?.Invoke() ?? [],
                TaskExecutionType.串口通讯发送数据 => SerialInstanceProvider?.Invoke() ?? [],
                TaskExecutionType.串口通讯等待数据 => SerialInstanceProvider?.Invoke() ?? [],
                _ => []
            };

            cmbTarget.Items.AddRange(options.Cast<object>().ToArray());
            bool listMode = et is TaskExecutionType.报警地址赋值 or TaskExecutionType.DO地址赋值
                or TaskExecutionType.Socket通讯发送数据 or TaskExecutionType.Socket通讯等待数据
                or TaskExecutionType.串口通讯发送数据 or TaskExecutionType.串口通讯等待数据;
            cmbTarget.DropDownStyle = listMode ? ComboBoxStyle.DropDownList : ComboBoxStyle.DropDown;
            if (listMode)
                SelectSourceAddress(cmbTarget, cur);
            else
                cmbTarget.Text = cur;

            if (IsSocketExecSelected())
                RefreshSinkSliceComboItems();
        }

        void UpdateExecStorageUi()
        {
            bool socket = IsSocketExecSelected();
            tblLegacy.Visible = !socket;
            pnlSocketBlock.Visible = socket;
            if (socket && gridSinks.Rows.Count == 0)
                gridSinks.Rows.Add(sinkSliceFullDisplay, "", "STRING");
        }

        void LoadSinksGridFromExisting()
        {
            gridSinks.Rows.Clear();
            if (existing is null)
                return;

            bool isSocket = existing.Execution.ExecutionType is TaskExecutionType.Socket通讯发送数据
                or TaskExecutionType.Socket通讯等待数据;
            if (!isSocket)
                return;

            if (existing.SocketResultSinks is { Count: > 0 })
            {
                foreach (TaskSocketResultSink x in existing.SocketResultSinks)
                {
                    gridSinks.Rows.Add(
                        x.SliceName ?? string.Empty,
                        x.StorageAddress ?? string.Empty,
                        string.IsNullOrWhiteSpace(x.ValueType) ? "STRING" : x.ValueType);
                }

                RefreshSinkSliceComboItems();
                return;
            }

            if (!string.IsNullOrWhiteSpace(existing.ResultStorageAddress)
                || !string.IsNullOrWhiteSpace(existing.Execution.SocketResultSliceName))
            {
                gridSinks.Rows.Add(
                    existing.Execution.SocketResultSliceName?.Trim() ?? string.Empty,
                    existing.ResultStorageAddress?.Trim() ?? string.Empty,
                    string.IsNullOrWhiteSpace(existing.ResultStorageValueType) ? "STRING" : existing.ResultStorageValueType);
                RefreshSinkSliceComboItems();
                return;
            }

            gridSinks.Rows.Add(sinkSliceFullDisplay, "", "STRING");
            RefreshSinkSliceComboItems();
        }

        cmbExecType.SelectedIndexChanged += (_, _) =>
        {
            RebindExecTargets();
            UpdateExecStorageUi();
            if (IsSocketExecSelected())
                RefreshSinkSliceComboItems();
        };

        cmbTarget.SelectedIndexChanged += (_, _) =>
        {
            if (IsSocketExecSelected())
                RefreshSinkSliceComboItems();
        };
        cmbTarget.TextChanged += (_, _) =>
        {
            if (IsSocketExecSelected())
                RefreshSinkSliceComboItems();
        };

        btnAddSink.Click += (_, _) => gridSinks.Rows.Add(sinkSliceFullDisplay, "", "STRING");
        btnRemoveSink.Click += (_, _) =>
        {
            if (gridSinks.SelectedRows.Count == 0)
                return;
            foreach (DataGridViewRow row in gridSinks.SelectedRows.Cast<DataGridViewRow>().OrderByDescending(r => r.Index))
            {
                if (!row.IsNewRow)
                    gridSinks.Rows.Remove(row);
            }
        };

        int er = 0;
        execRoot.Controls.Add(new Label { Text = "执行类型", AutoSize = true, Anchor = AnchorStyles.Left }, 0, er);
        execRoot.Controls.Add(cmbExecType, 1, er++);
        execRoot.Controls.Add(new Label { Text = "执行目标", AutoSize = true, Anchor = AnchorStyles.Left }, 0, er);
        execRoot.Controls.Add(cmbTarget, 1, er++);
        execRoot.Controls.Add(new Label { Text = "执行数据", AutoSize = true, Anchor = AnchorStyles.Left }, 0, er);
        execRoot.Controls.Add(txtPayload, 1, er++);
        execRoot.Controls.Add(new Label { Text = "结果写入 PLC", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top }, 0, er);
        execRoot.Controls.Add(pnlResultHost, 1, er++);
        var lblExecHint = new Label
        {
            Text = "非 Socket：单路结果地址与类型。Socket 发送/等待：可添加多组「分片(通讯里配置的结果标记名，空=整包原文)+存储地址+类型」；执行后按组分别写入。串口等待仍写单路整行原文。",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Padding = new Padding(0, 6, 0, 0)
        };
        execRoot.SetColumnSpan(lblExecHint, 2);
        execRoot.Controls.Add(lblExecHint, 0, er);
        tabExec.Controls.Add(execRoot);

        tabs.TabPages.Add(tabCond);
        tabs.TabPages.Add(tabExec);

        TriggerConditionConfig? initC = existing?.Condition;
        if (initC is not null)
        {
            cSrc.SelectedItem = initC.SourceType;
            RebindCondAddresses(initC.SourceAddress);
            cComp.SelectedItem = initC.Comparator;
            txtCmp.Text = initC.CompareValue;
            if (!string.IsNullOrWhiteSpace(initC.ConditionReadValueType))
            {
                int ix = cRead.Items.IndexOf(initC.ConditionReadValueType);
                if (ix >= 0)
                    cRead.SelectedIndex = ix;
            }
        }
        else
        {
            RebindCondAddresses();
        }

        if (existing is not null)
        {
            cmbExecType.SelectedItem = existing.Execution.ExecutionType;
            RebindExecTargets(existing.Execution.Target);
            txtPayload.Text = existing.Execution.Payload;
            txtStoreAddr.Text = existing.ResultStorageAddress;
            int storeIx = cmbStoreType.Items.IndexOf(existing.ResultStorageValueType);
            if (storeIx >= 0)
                cmbStoreType.SelectedIndex = storeIx;
            LoadSinksGridFromExisting();
        }
        else
        {
            RebindExecTargets();
        }

        UpdateExecStorageUi();
        if (IsSocketExecSelected())
            RefreshSinkSliceComboItems();

        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8)
        };
        var btnOk = new Button { Text = "确定", Width = 72, DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "取消", Width = 72, DialogResult = DialogResult.Cancel };
        btnPanel.Controls.Add(btnOk);
        btnPanel.Controls.Add(btnCancel);
        dialog.Controls.Add(tabs);
        dialog.Controls.Add(btnPanel);
        dialog.AcceptButton = btnOk;
        dialog.CancelButton = btnCancel;

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return null;

        TriggerConditionSourceType condSourceType = cSrc.SelectedItem is TriggerConditionSourceType stSel
            ? stSel
            : TriggerConditionSourceType.DI地址;
        string condSourceAddr = cAddr.SelectedItem is TaskSourceAddressOption addrOpt
            ? addrOpt.Address.Trim()
            : cAddr.Text.Trim();
        string condComparator = cComp.SelectedItem?.ToString() ?? "==";
        string condCompareVal = txtCmp.Text.Trim();
        string condReadVt = cRead.SelectedItem?.ToString() ?? "AUTO";

        TaskExecutionType execTypeSel = cmbExecType.SelectedItem is TaskExecutionType execSel
            ? execSel
            : TaskExecutionType.DO地址赋值;
        string execTargetAddr = cmbTarget.SelectedItem is TaskSourceAddressOption tgtOpt
            ? tgtOpt.Address.Trim()
            : cmbTarget.Text.Trim();
        string execPayload = txtPayload.Text.Trim();

        bool socketExec = execTypeSel is TaskExecutionType.Socket通讯发送数据
            or TaskExecutionType.Socket通讯等待数据;

        var sinks = new List<TaskSocketResultSink>();
        if (socketExec)
        {
            gridSinks.EndEdit();
            foreach (DataGridViewRow row in gridSinks.Rows)
            {
                if (row.IsNewRow)
                    continue;

                string sl = row.Cells["Slice"].Value?.ToString()?.Trim() ?? string.Empty;
                if (string.Equals(sl, sinkSliceFullDisplay, StringComparison.Ordinal)
                    || sl.StartsWith("(整包", StringComparison.Ordinal))
                    sl = string.Empty;

                string ad = row.Cells["Addr"].Value?.ToString()?.Trim() ?? string.Empty;
                string vt = row.Cells["VType"].Value?.ToString() ?? "STRING";
                if (string.IsNullOrWhiteSpace(ad))
                    continue;

                sinks.Add(new TaskSocketResultSink
                {
                    SliceName = sl,
                    StorageAddress = ad,
                    ValueType = vt
                });
            }

            return new TaskPipelineStepConfig
            {
                Condition = new TriggerConditionConfig
                {
                    SourceType = condSourceType,
                    SourceAddress = condSourceAddr,
                    Comparator = condComparator,
                    CompareValue = condCompareVal,
                    ConditionReadValueType = condReadVt
                },
                Execution = new TaskExecutionConfig
                {
                    ExecutionType = execTypeSel,
                    Target = execTargetAddr,
                    Payload = execPayload,
                    SocketResultSliceName = string.Empty
                },
                SocketResultSinks = sinks,
                ResultStorageAddress = string.Empty,
                ResultStorageValueType = "STRING"
            };
        }

        return new TaskPipelineStepConfig
        {
            Condition = new TriggerConditionConfig
            {
                SourceType = condSourceType,
                SourceAddress = condSourceAddr,
                Comparator = condComparator,
                CompareValue = condCompareVal,
                ConditionReadValueType = condReadVt
            },
            Execution = new TaskExecutionConfig
            {
                ExecutionType = execTypeSel,
                Target = execTargetAddr,
                Payload = execPayload,
                SocketResultSliceName = string.Empty
            },
            SocketResultSinks = [],
            ResultStorageAddress = txtStoreAddr.Text.Trim(),
            ResultStorageValueType = cmbStoreType.SelectedItem?.ToString() ?? "STRING"
        };
    }

    private static TriggerConditionConfig CloneCondition(TriggerConditionConfig source) => new()
    {
        SourceType = source.SourceType,
        SourceAddress = source.SourceAddress,
        Comparator = source.Comparator,
        CompareValue = source.CompareValue,
        ConditionReadValueType = source.ConditionReadValueType
    };

    private static TaskPipelineStepConfig CloneStep(TaskPipelineStepConfig s) => new()
    {
        Condition = CloneCondition(s.Condition),
        Execution = new TaskExecutionConfig
        {
            ExecutionType = s.Execution.ExecutionType,
            Target = s.Execution.Target,
            Payload = s.Execution.Payload,
            SocketResultSliceName = s.Execution.SocketResultSliceName
        },
        ResultStorageAddress = s.ResultStorageAddress,
        ResultStorageValueType = s.ResultStorageValueType,
        SocketResultSinks = s.SocketResultSinks?.Select(x => new TaskSocketResultSink
        {
            SliceName = x.SliceName,
            StorageAddress = x.StorageAddress,
            ValueType = x.ValueType
        }).ToList() ?? []
    };

    private static bool TryMigrateSocketLegacyToSinks(TaskPipelineStepConfig step)
    {
        step.SocketResultSinks ??= [];
        if (step.SocketResultSinks.Count > 0)
            return false;

        bool isSocket = step.Execution.ExecutionType is TaskExecutionType.Socket通讯发送数据
            or TaskExecutionType.Socket通讯等待数据;
        if (!isSocket)
            return false;

        if (string.IsNullOrWhiteSpace(step.ResultStorageAddress)
            && string.IsNullOrWhiteSpace(step.Execution.SocketResultSliceName))
            return false;

        step.SocketResultSinks.Add(new TaskSocketResultSink
        {
            SliceName = step.Execution.SocketResultSliceName?.Trim() ?? string.Empty,
            StorageAddress = step.ResultStorageAddress?.Trim() ?? string.Empty,
            ValueType = string.IsNullOrWhiteSpace(step.ResultStorageValueType) ? "STRING" : step.ResultStorageValueType
        });
        step.ResultStorageAddress = string.Empty;
        step.ResultStorageValueType = "STRING";
        step.Execution.SocketResultSliceName = string.Empty;
        return true;
    }

    private static string FormatResultWriteSummary(TaskPipelineStepConfig s)
    {
        bool socket = s.Execution.ExecutionType is TaskExecutionType.Socket通讯发送数据
            or TaskExecutionType.Socket通讯等待数据;
        if (socket && s.SocketResultSinks is { Count: > 0 })
        {
            return string.Join("; ", s.SocketResultSinks.Select(x =>
                $"{(string.IsNullOrWhiteSpace(x.SliceName) ? "整包" : x.SliceName)}→{x.StorageAddress}({x.ValueType})"));
        }

        return string.IsNullOrWhiteSpace(s.ResultStorageAddress)
            ? "—"
            : $"{s.ResultStorageAddress} ({s.ResultStorageValueType})";
    }

    private static void SelectSourceAddress(ComboBox comboBox, string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            if (comboBox.Items.Count > 0)
                comboBox.SelectedIndex = 0;
            return;
        }

        for (int i = 0; i < comboBox.Items.Count; i++)
        {
            if (comboBox.Items[i] is TaskSourceAddressOption option
                && string.Equals(option.Address, address.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedIndex = i;
                return;
            }
        }

        if (comboBox.Items.Count > 0)
            comboBox.SelectedIndex = 0;
    }

    private IReadOnlyList<TaskSourceAddressOption> GetSocketConditionAddressOptions()
    {
        var list = new List<TaskSourceAddressOption>();
        IReadOnlyList<TaskSourceAddressOption> instances = SocketInstanceProvider?.Invoke() ?? [];
        foreach (TaskSourceAddressOption instance in instances)
        {
            string instanceKey = instance.Address?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(instanceKey))
                continue;

            string instanceName = string.IsNullOrWhiteSpace(instance.Name) ? instanceKey : instance.Name.Trim();
            var markerNames = SocketResultFieldNamesProvider?.Invoke(instanceKey) ?? [];
            foreach (string marker in markerNames)
            {
                string markerName = marker?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(markerName))
                    continue;

                list.Add(new TaskSourceAddressOption
                {
                    Name = $"{instanceName}.{markerName}",
                    Address = BuildSocketConditionAddress(instanceKey, markerName)
                });
            }
        }

        return list
            .GroupBy(x => x.Address, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static string BuildSocketConditionAddress(string instanceKey, string markerName)
    {
        string key = instanceKey?.Trim() ?? string.Empty;
        string marker = markerName?.Trim() ?? string.Empty;
        return $"{SocketConditionAddressPrefix}{key}::{marker}";
    }

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                var list = JsonSerializer.Deserialize<List<AutomatedTaskConfig>>(json);
                if (list is { Count: > 0 })
                {
                    _tasks.Clear();
                    _tasks.AddRange(list);
                    bool migratedAny = false;
                    foreach (AutomatedTaskConfig t in _tasks)
                    {
                        foreach (TaskPipelineStepConfig st in t.Steps)
                        {
                            if (TryMigrateSocketLegacyToSinks(st))
                                migratedAny = true;
                        }
                    }

                    if (migratedAny)
                    {
                        SaveConfig();
                        LogAction?.Invoke("已迁移旧版 Socket 单路结果存储为多路「分片+地址+类型」组合。");
                    }

                    BindGrid();
                    return;
                }
            }

            string legacyPath = Path.Combine(AppContext.BaseDirectory, LegacyConfigFileName);
            if (!File.Exists(legacyPath))
                return;

            var legacyJson = File.ReadAllText(legacyPath);
            var legacy = JsonSerializer.Deserialize<List<TaskTriggerConfig>>(legacyJson);
            if (legacy is null || legacy.Count == 0)
                return;

            _tasks.Clear();
            foreach (TaskTriggerConfig t in legacy)
            {
                if (t.Conditions is null)
                    t.Conditions = [];
                if (t.Conditions.Count == 0 && t.Condition is not null)
                    t.Conditions.Add(CloneCondition(t.Condition));

                var firstCond = t.Conditions.FirstOrDefault();
                if (firstCond is null)
                    continue;

                _tasks.Add(new AutomatedTaskConfig
                {
                    Id = Guid.NewGuid().ToString("N"),
                    TaskName = string.IsNullOrWhiteSpace(t.TriggerName) ? "已迁移任务" : t.TriggerName,
                    Enabled = t.Enabled,
                    RunMode = AutomatedTaskRunMode.后台轮询,
                    PollingIntervalMs = 500,
                    Steps =
                    [
                        new TaskPipelineStepConfig
                        {
                            Condition = CloneCondition(firstCond),
                            Execution = new TaskExecutionConfig
                            {
                                ExecutionType = t.Execution.ExecutionType,
                                Target = t.Execution.Target,
                                Payload = t.Execution.Payload
                            }
                        }
                    ]
                });
            }

            if (_tasks.Count > 0)
            {
                SaveConfig();
                LogAction?.Invoke($"已从 {LegacyConfigFileName} 迁移 {_tasks.Count} 条任务到 {ConfigFileName}。");
            }

            BindGrid();
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"加载任务设置失败: {ex.Message}");
        }
    }

    private void SaveConfig()
    {
        try
        {
            var json = JsonSerializer.Serialize(_tasks, JsonFileOptions);
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"保存任务设置失败: {ex.Message}");
        }
    }
}
