using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PlcConectCom.WinFormsTest;

// 多轴点位管理控件：页签展示点位并提供试教/定位/保存/加载动作
public sealed class MultiAxisPositionControl : UserControl
{
    /// <summary>与 AxisMotionGroupTableControl 中「当前位置」列对应的字段键（AxisMotionFieldDefs.CurrentPos）。</summary>
    private const string AxisMotionCurrentPosFieldKey = "CurrentPos";

    private const string ConfigFileName = "MultiAxisPosition.config.json";
    private const string AuthConfigFileName = "RoleAuth.config.json";
    private static readonly JsonSerializerOptions JsonFileOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly TabControl _tabControl = new() { Dock = DockStyle.Fill };
    private readonly FlowLayoutPanel _teachInputPanel = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false };
    private readonly FlowLayoutPanel _locateInputPanel = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false };
    private readonly Button _btnTeach = new() { Text = "试教位置", Width = 90 };
    private readonly Button _btnLocate = new() { Text = "定位位置", Width = 90 };
    private readonly Button _btnSave = new() { Text = "保存位置", Width = 90 };
    private readonly Button _btnLoad = new() { Text = "加载位置", Width = 90 };
    private readonly Button _btnEditAuth = new() { Text = "编辑解锁", Width = 90 };
    private readonly Dictionary<string, TextBox> _teachInputs = [];
    private readonly Dictionary<string, TextBox> _locateInputs = [];
    private readonly List<AxisPageDefinition> _pages = [];
    private readonly string _configPath;
    private bool _suspendAutoSave;
    private bool _canModify = true;
    private bool _editUnlocked;

    public MultiAxisPositionControl()
    {
        _configPath = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
        BuildUi();
        BindEvents();
        SetPermission(canModify: true);
        LoadConfigFromFile();
    }

    public event Func<MultiAxisActionContext, Task>? TeachPositionRequested;

    public event Func<MultiAxisActionContext, Task>? LocatePositionRequested;

    public event Func<MultiAxisActionContext, Task>? SavePositionRequested;

    public event Func<MultiAxisActionContext, Task>? LoadPositionRequested;

    public Action<string>? LogAction { get; set; }

    public Func<bool>? EditPermissionRequest { get; set; }

    public Func<global::PlcConectCom.PlcCommunicator?>? PlcProvider { get; set; }

    public global::PlcConectCom.PlcCommunicator? PlcInstance { get; set; }

    /// <summary>解析轴在 AxisMotionGroupTableControl 中某字段的 PLC 地址，例如 fieldKey=<see cref="AxisMotionCurrentPosFieldKey"/> 表示「当前位置」。</summary>
    public Func<string, string, string?>? AxisFieldAddressResolver { get; set; }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(4)
        };

        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var positionPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            Padding = new Padding(0, 4, 0, 4)
        };

        positionPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        positionPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        positionPanel.Controls.Add(CreateInputRow("试教位置", _teachInputPanel), 0, 0);
        positionPanel.Controls.Add(CreateInputRow("定位位置", _locateInputPanel), 0, 1);

        var actionPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 2, 0, 0)
        };

        actionPanel.Controls.AddRange([_btnEditAuth, _btnTeach, _btnLocate, _btnSave, _btnLoad]);

        root.Controls.Add(_tabControl, 0, 0);
        root.Controls.Add(positionPanel, 0, 1);
        root.Controls.Add(actionPanel, 0, 2);

        Controls.Add(root);
    }

    private void BindEvents()
    {
        _btnEditAuth.Click += (_, _) => ToggleEditAuth();
        _tabControl.SelectedIndexChanged += (_, _) => RebuildPositionInputsForCurrentPage();
        _btnTeach.Click += async (_, _) => await HandleTeachAsync();
        _btnLocate.Click += async (_, _) => await HandleLocateAsync();
        _btnSave.Click += async (_, _) => await HandleSaveAsync();
        _btnLoad.Click += async (_, _) => await HandleLoadAsync();
    }

    public void SetPermission(bool canModify)
    {
        _canModify = canModify;
        if (!canModify)
            _editUnlocked = false;

        _btnEditAuth.Enabled = canModify;
        _btnEditAuth.Text = _editUnlocked ? "编辑锁定" : "编辑解锁";
        _btnTeach.Enabled = canModify && _editUnlocked;
        _btnLocate.Enabled = canModify && _editUnlocked;
        _btnSave.Enabled = canModify && _editUnlocked;
        _btnLoad.Enabled = canModify;
        UpdateInputEditorsState();
    }

    public void SetPages(IEnumerable<AxisPageDefinition> pages)
    {
        SetPages(pages, saveAfterSet: true);
    }

    private void SetPages(IEnumerable<AxisPageDefinition> pages, bool saveAfterSet)
    {
        _pages.Clear();
        _pages.AddRange(pages);

        _tabControl.TabPages.Clear();
        foreach (var page in _pages)
        {
            var grid = CreateGridForPage(page);
            var tabPage = new TabPage(page.PageName) { Tag = page };
            tabPage.Controls.Add(grid);
            _tabControl.TabPages.Add(tabPage);
        }

        RebuildPositionInputsForCurrentPage();

        if (saveAfterSet && !_suspendAutoSave)
            SaveConfigToFile();
    }

    public void LoadDemoPages()
    {
        SetPages([
            new AxisPageDefinition
            {
                PageName = "前后焊头",
                Axes = [
                    new AxisDefinition { AxisName = "X1", DisplayName = "X1" },
                    new AxisDefinition { AxisName = "X2", DisplayName = "X2" },
                    new AxisDefinition { AxisName = "Z", DisplayName = "Z" }
                ],
                Points = [
                    new AxisPointItem { PointName = "点位1", AxisPositions = new Dictionary<string, double> { ["X1"] = 100, ["X2"] = 100, ["Z"] = 20 } },
                    new AxisPointItem { PointName = "点位2", AxisPositions = new Dictionary<string, double> { ["X1"] = 120, ["X2"] = 118, ["Z"] = 30 } }
                ]
            },
            new AxisPageDefinition
            {
                PageName = "中焊头",
                Axes = [
                    new AxisDefinition { AxisName = "X", DisplayName = "X" },
                    new AxisDefinition { AxisName = "Y", DisplayName = "Y" }
                ],
                Points = [
                    new AxisPointItem { PointName = "点位1", AxisPositions = new Dictionary<string, double> { ["X"] = 50, ["Y"] = 10 } }
                ]
            }
        ]);
    }

    private Control CreateInputRow(string title, FlowLayoutPanel inputPanel)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            AutoSize = true
        };

        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 8, 0) }, 0, 0);
        row.Controls.Add(inputPanel, 1, 0);

        return row;
    }

    private DataGridView CreateGridForPage(AxisPageDefinition page)
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            MultiSelect = false,
            ReadOnly = true
        };

        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "PointName",
            HeaderText = "点位名称",
            Width = 120,
            ReadOnly = true
        });

        foreach (var axis in page.Axes)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = axis.AxisName,
                HeaderText = string.IsNullOrWhiteSpace(axis.DisplayName) ? axis.AxisName : axis.DisplayName,
                Width = 80,
                ReadOnly = true
            });
        }

        foreach (var point in page.Points)
        {
            int rowIndex = grid.Rows.Add();
            var row = grid.Rows[rowIndex];
            row.Cells[0].Value = point.PointName;
            for (int i = 0; i < page.Axes.Count; i++)
            {
                string axisName = page.Axes[i].AxisName;
                if (point.AxisPositions.TryGetValue(axisName, out double value))
                    row.Cells[i + 1].Value = value.ToString("0.###", CultureInfo.InvariantCulture);
                else
                    row.Cells[i + 1].Value = "0";
            }
        }

        grid.Tag = page;
        grid.SelectionChanged += (_, _) => SyncInputsFromSelectedRow(grid, page);

        var rowMenu = new ContextMenuStrip();
        var menuTeach = new ToolStripMenuItem("试教位置");
        menuTeach.Click += async (_, _) => await TeachFromAxisMotionCurrentPosAsync(page, grid);
        rowMenu.Items.Add(menuTeach);
        rowMenu.Opening += (_, e) =>
        {
            menuTeach.Enabled = _canModify && _editUnlocked;
            if (grid.SelectedRows.Count == 0)
                e.Cancel = true;
        };
        grid.ContextMenuStrip = rowMenu;
        grid.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right)
                return;

            var hit = grid.HitTest(e.X, e.Y);
            if (hit.RowIndex >= 0 && hit.RowIndex < grid.Rows.Count)
            {
                grid.ClearSelection();
                grid.Rows[hit.RowIndex].Selected = true;
            }
        };

        if (grid.Rows.Count > 0)
            grid.Rows[0].Selected = true;

        return grid;
    }

    private void RebuildPositionInputsForCurrentPage()
    {
        _teachInputs.Clear();
        _locateInputs.Clear();
        _teachInputPanel.Controls.Clear();
        _locateInputPanel.Controls.Clear();

        var page = GetCurrentPageDefinition();
        if (page is null)
            return;

        foreach (var axis in page.Axes)
        {
            string title = string.IsNullOrWhiteSpace(axis.DisplayName) ? axis.AxisName : axis.DisplayName;

            var teachInput = CreateAxisInputControl(title);
            var locateInput = CreateAxisInputControl(title);

            _teachInputPanel.Controls.Add(teachInput.container);
            _locateInputPanel.Controls.Add(locateInput.container);

            _teachInputs[axis.AxisName] = teachInput.textBox;
            _locateInputs[axis.AxisName] = locateInput.textBox;
        }

        if (_tabControl.SelectedTab?.Controls.OfType<DataGridView>().FirstOrDefault() is DataGridView grid)
            SyncInputsFromSelectedRow(grid, page);

        UpdateInputEditorsState();
    }

    private void ToggleEditAuth()
    {
        if (!_canModify)
        {
            MessageBox.Show(this, "当前权限不可编辑。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_editUnlocked)
        {
            _editUnlocked = false;
            SetPermission(_canModify);
            return;
        }

        bool ok;
        try
        {
            ok = RequestEditAuthorization();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"编辑解锁失败：{ex.Message}", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!ok)
        {
            MessageBox.Show(this, "编辑权限验证未通过。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _editUnlocked = true;
        SetPermission(_canModify);
        LogAction?.Invoke("MultiAxisPositionControl 已解锁编辑。");
    }

    private bool RequestEditAuthorization()
    {
        if (EditPermissionRequest is not null)
            return EditPermissionRequest();

        var (adminPassword, _) = LoadRolePasswords();

        using var dialog = new Form
        {
            Text = "编辑权限验证",
            Width = 360,
            Height = 210,
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
        bool pass = string.Equals(password, adminPassword, StringComparison.Ordinal);

        if (!pass)
        {
            MessageBox.Show(this, "管理员密码错误。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        return true;
    }

    private static (string adminPassword, string engineerPassword) LoadRolePasswords()
    {
        const string defaultAdmin = "admin123";
        const string defaultEngineer = "engineer123";

        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, AuthConfigFileName);
            if (!File.Exists(path))
                return (defaultAdmin, defaultEngineer);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            string admin = root.TryGetProperty("AdminPassword", out var adminProp)
                ? (adminProp.GetString() ?? defaultAdmin)
                : defaultAdmin;
            string engineer = root.TryGetProperty("EngineerPassword", out var engProp)
                ? (engProp.GetString() ?? defaultEngineer)
                : defaultEngineer;
            return (admin, engineer);
        }
        catch
        {
            return (defaultAdmin, defaultEngineer);
        }
    }

    private void UpdateInputEditorsState()
    {
        bool readOnly = !_canModify || !_editUnlocked;
        foreach (var textBox in _teachInputs.Values)
            textBox.ReadOnly = readOnly;
        foreach (var textBox in _locateInputs.Values)
            textBox.ReadOnly = readOnly;
    }

    private static (Control container, TextBox textBox) CreateAxisInputControl(string axisTitle)
    {
        var panel = new Panel { Width = 125, Height = 28 };
        var label = new Label { Text = axisTitle, AutoSize = true, Location = new Point(0, 6) };
        var text = new TextBox { Width = 78, Location = new Point(40, 2) };

        panel.Controls.Add(label);
        panel.Controls.Add(text);

        return (panel, text);
    }

    private void SyncInputsFromSelectedRow(DataGridView grid, AxisPageDefinition page)
    {
        if (grid.SelectedRows.Count == 0)
            return;

        var row = grid.SelectedRows[0];
        for (int i = 0; i < page.Axes.Count; i++)
        {
            string axisName = page.Axes[i].AxisName;
            string value = Convert.ToString(row.Cells[i + 1].Value, CultureInfo.InvariantCulture) ?? "0";

            if (_teachInputs.TryGetValue(axisName, out var teachInput))
                teachInput.Text = value;
            if (_locateInputs.TryGetValue(axisName, out var locateInput))
                locateInput.Text = value;
        }
    }

    private async Task HandleTeachAsync()
    {
        if (!_canModify || !_editUnlocked)
            return;

        var page = GetCurrentPageDefinition();
        if (page is null)
            return;

        if (_tabControl.SelectedTab?.Controls.OfType<DataGridView>().FirstOrDefault() is not DataGridView grid)
            return;

        await TeachFromAxisMotionCurrentPosAsync(page, grid);
    }

    /// <summary>从 AxisMotion 表中各轴「当前位置」地址读取实时值写入试教框，并触发 <see cref="TeachPositionRequested"/>。</summary>
    private async Task TeachFromAxisMotionCurrentPosAsync(AxisPageDefinition page, DataGridView grid)
    {
        if (grid.SelectedRows.Count == 0)
        {
            LogAction?.Invoke("未选中点位。");
            return;
        }

        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
        {
            LogAction?.Invoke("请先连接 PLC。");
            return;
        }

        if (AxisFieldAddressResolver is null)
        {
            LogAction?.Invoke("未配置轴字段地址解析（AxisFieldAddressResolver），无法读取当前位置。");
            return;
        }

        foreach (var axis in page.Axes)
        {
            string axisName = axis.AxisName;
            string? addr = AxisFieldAddressResolver.Invoke(axisName, AxisMotionCurrentPosFieldKey)?.Trim();
            if (string.IsNullOrWhiteSpace(addr))
            {
                LogAction?.Invoke($"轴[{axisName}]未配置「当前位置」(CurrentPos) 地址。");
                continue;
            }

            try
            {
                double value = await plc.ReadDoubleAsync(addr);
                if (_teachInputs.TryGetValue(axisName, out var teachBox))
                    teachBox.Text = value.ToString("0.###", CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                LogAction?.Invoke($"试教读取失败 轴[{axisName}]: {ex.Message}");
            }
        }

        var context = BuildActionContext();
        if (context is null)
            return;

        if (TeachPositionRequested is not null)
            await TeachPositionRequested(context);

        LogAction?.Invoke($"试教位置: {context.PageName}/{context.PointName}");
    }

    private async Task HandleLocateAsync()
    {
        if (!_canModify || !_editUnlocked)
            return;

        var context = BuildActionContext();
        if (context is null)
            return;

        foreach (var (axisName, value) in context.TeachPositions)
        {
            if (_locateInputs.TryGetValue(axisName, out var locateInput))
                locateInput.Text = value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        if (LocatePositionRequested is not null)
            await LocatePositionRequested(context);

        LogAction?.Invoke($"定位位置: {context.PageName}/{context.PointName}");
    }

    private async Task HandleSaveAsync()
    {
        if (!_canModify || !_editUnlocked)
            return;

        var context = BuildActionContext();
        if (context is null)
            return;

        if (_tabControl.SelectedTab?.Controls.OfType<DataGridView>().FirstOrDefault() is DataGridView grid
            && grid.SelectedRows.Count > 0
            && GetCurrentPageDefinition() is AxisPageDefinition page)
        {
            var row = grid.SelectedRows[0];
            for (int i = 0; i < page.Axes.Count; i++)
            {
                string axisName = page.Axes[i].AxisName;
                if (context.LocatePositions.TryGetValue(axisName, out double value))
                    row.Cells[i + 1].Value = value.ToString("0.###", CultureInfo.InvariantCulture);
            }

            var point = page.Points.FirstOrDefault(p => p.PointName == context.PointName);
            if (point is not null)
            {
                foreach (var (axisName, value) in context.LocatePositions)
                    point.AxisPositions[axisName] = value;
            }
        }

        if (SavePositionRequested is not null)
            await SavePositionRequested(context);

        SaveConfigToFile();
        LogAction?.Invoke($"保存位置: {context.PageName}/{context.PointName}");
    }

    private async Task HandleLoadAsync()
    {
        var context = BuildActionContext();
        if (context is null)
            return;

        if (_tabControl.SelectedTab?.Controls.OfType<DataGridView>().FirstOrDefault() is DataGridView grid
            && GetCurrentPageDefinition() is AxisPageDefinition page)
        {
            SyncInputsFromSelectedRow(grid, page);
        }

        if (LoadPositionRequested is not null)
            await LoadPositionRequested(context);

        LogAction?.Invoke($"加载位置: {context.PageName}/{context.PointName}");
    }

    private MultiAxisActionContext? BuildActionContext()
    {
        var page = GetCurrentPageDefinition();
        if (page is null)
            return null;

        string pointName = GetSelectedPointName() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(pointName))
        {
            LogAction?.Invoke("未选中点位。");
            return null;
        }

        var teach = ReadValues(_teachInputs);
        var locate = ReadValues(_locateInputs);

        return new MultiAxisActionContext
        {
            PageName = page.PageName,
            PointName = pointName,
            TeachPositions = teach,
            LocatePositions = locate
        };
    }

    private static Dictionary<string, double> ReadValues(Dictionary<string, TextBox> inputs)
    {
        var values = new Dictionary<string, double>();
        foreach (var (axisName, textBox) in inputs)
        {
            if (!double.TryParse(textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                && !double.TryParse(textBox.Text, out value))
                value = 0;

            values[axisName] = value;
        }

        return values;
    }

    private AxisPageDefinition? GetCurrentPageDefinition()
    {
        return _tabControl.SelectedTab?.Tag as AxisPageDefinition;
    }

    private string? GetSelectedPointName()
    {
        if (_tabControl.SelectedTab?.Controls.OfType<DataGridView>().FirstOrDefault() is not DataGridView grid)
            return null;
        if (grid.SelectedRows.Count == 0)
            return null;
        return Convert.ToString(grid.SelectedRows[0].Cells[0].Value, CultureInfo.InvariantCulture);
    }

    private void LoadConfigFromFile()
    {
        try
        {
            if (!File.Exists(_configPath))
                return;

            string json = File.ReadAllText(_configPath);
            var pages = JsonSerializer.Deserialize<List<AxisPageDefinition>>(json);
            if (pages is null || pages.Count == 0)
                return;

            _suspendAutoSave = true;
            SetPages(pages, saveAfterSet: false);
            _suspendAutoSave = false;
        }
        catch (Exception ex)
        {
            _suspendAutoSave = false;
            LogAction?.Invoke($"加载多轴位置配置失败: {ex.Message}");
        }
    }

    private void SaveConfigToFile()
    {
        if (_suspendAutoSave)
            return;

        try
        {
            string json = JsonSerializer.Serialize(_pages, JsonFileOptions);
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"保存多轴位置配置失败: {ex.Message}");
        }
    }
}
