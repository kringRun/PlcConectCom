using System.Text.Encodings.Web;
using System.Text.Json;
using System.Globalization;

namespace PlcConectCom.WinFormsTest;

// 位置参数项
public sealed class PositionParameterItem
{
    public string PositionName { get; set; } = string.Empty;

    public List<PositionAxisBinding> AxisBindings { get; set; } = [];

    public List<string> AxisNames { get; set; } = [];
}

// 位置中单轴绑定（轴名 + 地址）
public sealed class PositionAxisBinding
{
    public string AxisName { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;
}

// 位置参数表控件：位置维护、轴绑定与位置数据编辑
public sealed class PositionParameterTableControl : UserControl
{
    private const string ConfigFileName = "PositionParameterTable.config.json";
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
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
    };

    private readonly Button _btnAdd = new() { Text = "新增位置", Width = 82 };
    private readonly Button _btnEditAuth = new() { Text = "编辑解锁", Width = 84 };
    private readonly Button _btnEdit = new() { Text = "编辑位置", Width = 82 };
    private readonly Button _btnRemove = new() { Text = "删除位置", Width = 82 };
    private readonly Button _btnEditData = new() { Text = "编辑数据", Width = 82 };
    private readonly Button _btnRefresh = new() { Text = "刷新数据", Width = 82 };
    private readonly Button _btnSave = new() { Text = "保存", Width = 72 };
    private readonly Button _btnLoad = new() { Text = "加载", Width = 72 };
    private readonly ContextMenuStrip _rowMenu = new();
    private readonly ToolStripMenuItem _menuTeach = new("试教位置");
    private readonly ToolStripMenuItem _menuLocate = new("定位运动");
    private readonly ToolStripMenuItem _menuSavePosition = new("保存位置");

    private readonly string _configPath;
    private readonly Dictionary<PositionParameterItem, Dictionary<string, double>> _teachedValues = [];
    private bool _suspendAutoSave;
    private bool _canAddDelete = true;
    private bool _canModify = true;
    private bool _editUnlocked;

    public PositionParameterTableControl()
    {
        _configPath = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
        BuildUi();
        BindEvents();
        LoadConfig();
    }

    public Action<string>? LogAction { get; set; }

    public Func<IReadOnlyList<string>>? AxisNameProvider { get; set; }

    public Func<global::PlcConectCom.PlcCommunicator?>? PlcProvider { get; set; }

    public global::PlcConectCom.PlcCommunicator? PlcInstance { get; set; }

    public Func<bool>? EditPermissionRequest { get; set; }

    public Func<string, string, string?>? AxisFieldAddressResolver { get; set; }

    public void SetPermission(bool canAddDelete, bool canModify)
    {
        _canAddDelete = canAddDelete;
        _canModify = canModify;
        if (!canModify)
            _editUnlocked = false;

        _btnAdd.Enabled = _canAddDelete && _editUnlocked;
        _btnRemove.Enabled = _canAddDelete && _editUnlocked;
        _btnEdit.Enabled = canModify && _editUnlocked;
        _btnEditData.Enabled = canModify && _editUnlocked;
        _btnRefresh.Enabled = canModify;
        _btnSave.Enabled = canModify && _editUnlocked;
        _btnLoad.Enabled = canModify && _editUnlocked;
        _btnEditAuth.Enabled = canModify;
        _btnEditAuth.Text = _editUnlocked ? "编辑锁定" : "编辑解锁";
        _grid.ReadOnly = !_editUnlocked;
        UpdateRowMenuState();
    }

    public IReadOnlyList<PositionParameterItem> GetItems()
    {
        var list = new List<PositionParameterItem>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is not PositionParameterItem item)
                continue;

            string positionName = Convert.ToString(row.Cells[0].Value)?.Trim() ?? string.Empty;
            var clone = new PositionParameterItem
            {
                PositionName = positionName,
                AxisBindings = item.AxisBindings.Select(x => new PositionAxisBinding
                {
                    AxisName = x.AxisName,
                    Address = x.Address
                }).ToList()
            };
            clone.AxisNames = clone.AxisBindings.Select(x => x.AxisName).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();

            if (string.IsNullOrWhiteSpace(clone.PositionName) && clone.AxisBindings.Count == 0)
                continue;

            list.Add(clone);
        }

        return list;
    }

    public void SetItems(IEnumerable<PositionParameterItem> items)
    {
        _suspendAutoSave = true;
        _grid.Rows.Clear();

        foreach (var item in items)
            AddItemRow(item);

        _suspendAutoSave = false;
        SaveConfig();
    }

    private void BuildUi()
    {
        _grid.DefaultCellStyle.SelectionBackColor = Color.Gainsboro;
        _grid.DefaultCellStyle.SelectionForeColor = Color.Black;
        _grid.RowTemplate.Height = 30;

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "位置名称", FillWeight = 20 });
        for (int i = 1; i <= 5; i++)
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = $"轴{i}数据", FillWeight = 16, ReadOnly = true });

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 0, 0, 4)
        };

        actions.Controls.AddRange([_btnEditAuth, _btnAdd, _btnEdit, _btnRemove, _btnEditData, _btnRefresh, _btnSave, _btnLoad]);

        Controls.Add(_grid);
        Controls.Add(actions);
    }

    private void BindEvents()
    {
        _menuTeach.Click += async (_, _) => await TeachSelectedRowAsync();
        _menuLocate.Click += async (_, _) => await LocateSelectedRowAsync();
        _menuSavePosition.Click += async (_, _) => await SaveSelectedRowPositionAsync();
        _rowMenu.Items.AddRange([_menuTeach, _menuLocate, _menuSavePosition]);

        _btnEditAuth.Click += (_, _) => ToggleEditAuth();
        _btnAdd.Click += (_, _) => AddPositionByDialog();
        _btnEdit.Click += (_, _) => EditSelectedPosition();
        _btnRemove.Click += (_, _) =>
        {
            if (_grid.SelectedRows.Count == 0)
                return;

            _grid.Rows.RemoveAt(_grid.SelectedRows[0].Index);
            SaveConfig();
        };
        _btnEditData.Click += async (_, _) => await EditSelectedRowDataAsync();
        _btnRefresh.Click += async (_, _) => await RefreshDataAsync();
        _btnSave.Click += (_, _) => SaveConfig();
        _btnLoad.Click += (_, _) => LoadConfig();
        _grid.CellValueChanged += (_, e) =>
        {
            if (_suspendAutoSave || !_editUnlocked)
                return;

            if (e.RowIndex >= 0)
            {
                if (_grid.Rows[e.RowIndex].Tag is PositionParameterItem item)
                    item.PositionName = Convert.ToString(_grid.Rows[e.RowIndex].Cells[0].Value)?.Trim() ?? string.Empty;

                SaveConfig();
            }
        };
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && _editUnlocked)
                EditSelectedPosition();
        };
        _grid.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0)
                return;

            _grid.ClearSelection();
            _grid.Rows[e.RowIndex].Selected = true;
            _rowMenu.Show(_grid, e.Location);
        };
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
            SetPermission(_canAddDelete, _canModify);
            return;
        }

        bool ok;
        try
        {
            ok = EditPermissionRequest?.Invoke() ?? false;
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
        SetPermission(_canAddDelete, _canModify);
    }

    private void UpdateRowMenuState()
    {
        bool enabled = _canModify && _editUnlocked;
        _menuTeach.Enabled = enabled;
        _menuLocate.Enabled = enabled;
        _menuSavePosition.Enabled = enabled;
    }

    private async Task TeachSelectedRowAsync()
    {
        var row = GetSelectedRow();
        if (row?.Tag is not PositionParameterItem item)
            return;

        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
        {
            LogAction?.Invoke("请先连接 PLC。");
            return;
        }

        var values = await ReadAxisCurrentPositionsAsync(plc, item);
        if (values.Count == 0)
            return;

        _teachedValues[item] = values;
        FillRowDisplay(row, item, values.ToDictionary(x => x.Key, x => x.Value.ToString("0.###", CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase));
        LogAction?.Invoke($"试教完成: {item.PositionName}");
    }

    private async Task LocateSelectedRowAsync()
    {
        var row = GetSelectedRow();
        if (row?.Tag is not PositionParameterItem item)
            return;

        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
        {
            LogAction?.Invoke("请先连接 PLC。");
            return;
        }

        foreach (var binding in item.AxisBindings)
        {
            if (string.IsNullOrWhiteSpace(binding.AxisName) || string.IsNullOrWhiteSpace(binding.Address))
                continue;

            string axisName = binding.AxisName.Trim();
            string? setCurrentPosAddress = AxisFieldAddressResolver?.Invoke(axisName, "SetCurrentPos")?.Trim();
            string? absMoveAddress = AxisFieldAddressResolver?.Invoke(axisName, "AbsMove")?.Trim();
            if (string.IsNullOrWhiteSpace(setCurrentPosAddress) || string.IsNullOrWhiteSpace(absMoveAddress))
            {
                LogAction?.Invoke($"轴[{axisName}]未配置 SetCurrentPos 或 AbsMove 地址。");
                continue;
            }

            try
            {
                double target = await plc.ReadDoubleAsync(binding.Address.Trim());
                await plc.WriteDoubleAsync(setCurrentPosAddress, target);
                await plc.WriteBoolAsync(absMoveAddress, true);
                await Task.Delay(50);
                await plc.WriteBoolAsync(absMoveAddress, false);
            }
            catch (Exception ex)
            {
                LogAction?.Invoke($"定位失败 轴[{axisName}]: {ex.Message}");
            }
        }

        LogAction?.Invoke($"定位运动完成: {item.PositionName}");
    }

    private async Task SaveSelectedRowPositionAsync()
    {
        var row = GetSelectedRow();
        if (row?.Tag is not PositionParameterItem item)
            return;

        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
        {
            LogAction?.Invoke("请先连接 PLC。");
            return;
        }

        var values = _teachedValues.TryGetValue(item, out var cached)
            ? cached
            : await ReadAxisCurrentPositionsAsync(plc, item);

        if (values.Count == 0)
            return;

        foreach (var binding in item.AxisBindings)
        {
            if (string.IsNullOrWhiteSpace(binding.AxisName) || string.IsNullOrWhiteSpace(binding.Address))
                continue;

            if (!values.TryGetValue(binding.AxisName.Trim(), out double value))
                continue;

            try
            {
                await plc.WriteDoubleAsync(binding.Address.Trim(), value);
            }
            catch (Exception ex)
            {
                LogAction?.Invoke($"保存位置失败 轴[{binding.AxisName}]: {ex.Message}");
            }
        }

        await RefreshDataAsync();
        LogAction?.Invoke($"位置保存完成: {item.PositionName}");
    }

    private async Task<Dictionary<string, double>> ReadAxisCurrentPositionsAsync(global::PlcConectCom.PlcCommunicator plc, PositionParameterItem item)
    {
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in item.AxisBindings)
        {
            if (string.IsNullOrWhiteSpace(binding.AxisName))
                continue;

            string axisName = binding.AxisName.Trim();
            string? currentPosAddress = AxisFieldAddressResolver?.Invoke(axisName, "CurrentPos")?.Trim();
            if (string.IsNullOrWhiteSpace(currentPosAddress))
            {
                LogAction?.Invoke($"轴[{axisName}]未配置 CurrentPos 地址。");
                continue;
            }

            try
            {
                values[axisName] = await plc.ReadDoubleAsync(currentPosAddress);
            }
            catch (Exception ex)
            {
                LogAction?.Invoke($"试教读取失败 轴[{axisName}]: {ex.Message}");
            }
        }

        return values;
    }

    private DataGridViewRow? GetSelectedRow()
    {
        return _grid.SelectedRows.Count == 0 ? null : _grid.SelectedRows[0];
    }

    private async Task EditSelectedRowDataAsync()
    {
        if (!_canModify)
            return;

        if (_grid.SelectedRows.Count == 0)
            return;

        if (_grid.SelectedRows[0].Tag is not PositionParameterItem item || item.AxisBindings.Count == 0)
            return;

        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
        {
            LogAction?.Invoke("请先连接 PLC。");
            return;
        }

        using var dialog = new Form
        {
            Text = $"编辑位置数据 - {item.PositionName}",
            Width = 420,
            Height = 320,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = item.AxisBindings.Count,
            Padding = new Padding(10)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var inputs = new List<(string axisName, string address, TextBox input)>();
        foreach (var binding in item.AxisBindings)
        {
            if (string.IsNullOrWhiteSpace(binding.AxisName) || string.IsNullOrWhiteSpace(binding.Address))
                continue;

            layout.Controls.Add(new Label { Text = binding.AxisName, AutoSize = true, Anchor = AnchorStyles.Left }, 0, inputs.Count);
            var txt = new TextBox { Dock = DockStyle.Fill };
            try
            {
                double v = await plc.ReadDoubleAsync(binding.Address);
                txt.Text = v.ToString("0.###");
            }
            catch
            {
                txt.Text = string.Empty;
            }

            layout.Controls.Add(txt, 1, inputs.Count);
            inputs.Add((binding.AxisName, binding.Address, txt));
        }

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

        dialog.Controls.Add(layout);
        dialog.Controls.Add(btnPanel);
        dialog.AcceptButton = btnOk;
        dialog.CancelButton = btnCancel;

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        foreach (var x in inputs)
        {
            if (!double.TryParse(x.input.Text, out double value) &&
                !double.TryParse(x.input.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                LogAction?.Invoke($"{x.axisName} 数值格式错误。");
                return;
            }

            await plc.WriteDoubleAsync(x.address, value);
        }

        await RefreshDataAsync();
    }

    public async Task RefreshDataAsync()
    {
        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is not { IsConnected: true })
        {
            foreach (DataGridViewRow row in _grid.Rows)
                FillRowDisplay(row, row.Tag as PositionParameterItem, null);
            return;
        }

        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is not PositionParameterItem item)
                continue;

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var binding in item.AxisBindings)
            {
                if (string.IsNullOrWhiteSpace(binding.AxisName) || string.IsNullOrWhiteSpace(binding.Address))
                    continue;

                try
                {
                    double value = await plc.ReadDoubleAsync(binding.Address);
                    values[binding.AxisName] = value.ToString("0.###");
                }
                catch
                {
                    values[binding.AxisName] = "ERR";
                }
            }

            FillRowDisplay(row, item, values);
        }
    }

    private void AddPositionByDialog()
    {
        if (!_canModify)
            return;

        var created = ShowPositionDialog();
        if (created is null)
            return;

        AddItemRow(created);
        SaveConfig();
        _ = RefreshDataAsync();
    }

    private void EditSelectedPosition()
    {
        if (!_canModify)
            return;

        if (_grid.SelectedRows.Count == 0)
            return;

        var row = _grid.SelectedRows[0];
        if (row.Tag is not PositionParameterItem existing)
            return;

        var edited = ShowPositionDialog(existing);
        if (edited is null)
            return;

        row.Tag = edited;
        FillRowDisplay(row, edited, null);
        SaveConfig();
        _ = RefreshDataAsync();
    }

    private PositionParameterItem? ShowPositionDialog(PositionParameterItem? existing = null)
    {
        var axisNames = AxisNameProvider?.Invoke() ?? [];
        if (axisNames.Count == 0)
        {
            LogAction?.Invoke("请先在轴配置表中新增轴。");
            return null;
        }

        using var dialog = new Form
        {
            Text = existing is null ? "新增位置参数" : "编辑位置参数",
            Width = 420,
            Height = 360,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 7,
            Padding = new Padding(10)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        layout.Controls.Add(new Label { Text = "位置名称", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        var txtPositionName = new TextBox { Dock = DockStyle.Fill };
        layout.SetColumnSpan(txtPositionName, 2);
        layout.Controls.Add(txtPositionName, 1, 0);

        var axisCombos = new List<ComboBox>();
        var axisAddressInputs = new List<TextBox>();
        layout.Controls.Add(new Label { Text = "轴名称", AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font(Font, FontStyle.Bold) }, 1, 1);
        layout.Controls.Add(new Label { Text = "地址", AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font(Font, FontStyle.Bold) }, 2, 1);
        for (int i = 0; i < 5; i++)
        {
            int rowIndex = i + 2;
            layout.Controls.Add(new Label { Text = $"轴{i + 1}", AutoSize = true, Anchor = AnchorStyles.Left }, 0, rowIndex);
            var cmb = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            cmb.Items.Add(string.Empty);
            cmb.Items.AddRange(axisNames.Distinct(StringComparer.OrdinalIgnoreCase).Cast<object>().ToArray());
            cmb.SelectedIndex = 0;
            axisCombos.Add(cmb);

            var txtAddress = new TextBox { Dock = DockStyle.Fill };
            axisAddressInputs.Add(txtAddress);

            layout.Controls.Add(cmb, 1, rowIndex);
            layout.Controls.Add(txtAddress, 2, rowIndex);
        }

        var panelButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8)
        };

        var btnOk = new Button { Text = "确定", Width = 72, DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "取消", Width = 72, DialogResult = DialogResult.Cancel };
        panelButtons.Controls.Add(btnOk);
        panelButtons.Controls.Add(btnCancel);

        dialog.Controls.Add(layout);
        dialog.Controls.Add(panelButtons);
        dialog.AcceptButton = btnOk;
        dialog.CancelButton = btnCancel;

        if (existing is not null)
        {
            txtPositionName.Text = existing.PositionName;
            for (int i = 0; i < Math.Min(5, existing.AxisBindings.Count); i++)
            {
                var b = existing.AxisBindings[i];
                string axisName = b.AxisName ?? string.Empty;
                int idx = axisCombos[i].Items.IndexOf(axisName);
                axisCombos[i].SelectedIndex = idx >= 0 ? idx : 0;
                axisAddressInputs[i].Text = b.Address ?? string.Empty;
            }
        }

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return null;

        string positionName = txtPositionName.Text.Trim();
        if (string.IsNullOrWhiteSpace(positionName))
        {
            LogAction?.Invoke("位置名称不能为空。");
            return null;
        }

        var selectedBindings = new List<PositionAxisBinding>();
        var usedAxisNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 5; i++)
        {
            string axisName = axisCombos[i].SelectedItem?.ToString() ?? string.Empty;
            string address = axisAddressInputs[i].Text.Trim();
            if (string.IsNullOrWhiteSpace(axisName) && string.IsNullOrWhiteSpace(address))
                continue;

            if (string.IsNullOrWhiteSpace(axisName))
                continue;

            if (!usedAxisNames.Add(axisName))
                continue;

            selectedBindings.Add(new PositionAxisBinding
            {
                AxisName = axisName,
                Address = address
            });
        }

        return new PositionParameterItem
        {
            PositionName = positionName,
            AxisBindings = selectedBindings,
            AxisNames = selectedBindings.Select(x => x.AxisName).ToList()
        };
    }

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(_configPath))
                return;

            string json = File.ReadAllText(_configPath);
            var items = JsonSerializer.Deserialize<List<PositionParameterItem>>(json);
            if (items is null)
                return;

            _suspendAutoSave = true;
            _grid.Rows.Clear();
            foreach (var item in items)
                AddItemRow(item);
            _suspendAutoSave = false;
            _ = RefreshDataAsync();
        }
        catch (Exception ex)
        {
            _suspendAutoSave = false;
            LogAction?.Invoke($"加载位置参数配置失败: {ex.Message}");
        }
    }

    private void SaveConfig()
    {
        if (_suspendAutoSave)
            return;

        try
        {
            var items = GetItems();
            string json = JsonSerializer.Serialize(items, JsonFileOptions);
            File.WriteAllText(_configPath, json);
            LogAction?.Invoke("位置参数配置已保存。");
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"保存位置参数配置失败: {ex.Message}");
        }
    }

    private void AddItemRow(PositionParameterItem item)
    {
        var bindings = item.AxisBindings;
        if (bindings.Count == 0 && item.AxisNames.Count > 0)
            bindings = item.AxisNames.Select(x => new PositionAxisBinding { AxisName = x }).ToList();

        int rowIndex = _grid.Rows.Add();
        var row = _grid.Rows[rowIndex];

        var normalized = new PositionParameterItem
        {
            PositionName = item.PositionName,
            AxisBindings = bindings.Take(5).Select(x => new PositionAxisBinding
            {
                AxisName = x.AxisName,
                Address = x.Address
            }).ToList()
        };
        normalized.AxisNames = normalized.AxisBindings.Select(x => x.AxisName).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();

        row.Tag = normalized;
        FillRowDisplay(row, normalized, null);
    }

    private static void FillRowDisplay(DataGridViewRow row, PositionParameterItem? item, Dictionary<string, string>? axisValues)
    {
        if (item is null)
            return;

        row.Cells[0].Value = item.PositionName;
        for (int i = 0; i < 5; i++)
        {
            if (i >= item.AxisBindings.Count)
            {
                row.Cells[i + 1].Value = string.Empty;
                continue;
            }

            var binding = item.AxisBindings[i];
            string axisName = binding.AxisName;
            string value = "--";
            if (axisValues is not null && axisValues.TryGetValue(axisName, out string? current))
                value = current;

            row.Cells[i + 1].Value = string.IsNullOrWhiteSpace(axisName) ? string.Empty : $"{axisName}: {value}";
        }
    }
}
