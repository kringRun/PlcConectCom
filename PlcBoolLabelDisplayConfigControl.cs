using System.Text.Encodings.Web;
using System.Text.Json;

namespace PlcConectCom.WinFormsTest;

public sealed class PlcBoolLabelDisplayConfigItem
{
    public string DisplayName { get; set; } = string.Empty;
    public string PlcAddress { get; set; } = string.Empty;
    public string TrueText { get; set; } = "True";
    public string FalseText { get; set; } = "False";
    public int ForeColorArgb { get; set; } = Color.Black.ToArgb();
    public int TrueBackColorArgb { get; set; } = Color.LimeGreen.ToArgb();
    public int FalseBackColorArgb { get; set; } = Color.Silver.ToArgb();
}

/// <summary>主页面 plcBoolLabe 显示配置。</summary>
public sealed class PlcBoolLabelDisplayConfigControl : UserControl
{
    private const string ConfigFileName = "PlcBoolLabelDisplay.config.json";
    private static readonly JsonSerializerOptions JsonFileOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private sealed class RowColorBinding
    {
        public int ForeColorArgb { get; set; } = Color.Black.ToArgb();
        public int TrueBackColorArgb { get; set; } = Color.LimeGreen.ToArgb();
        public int FalseBackColorArgb { get; set; } = Color.Silver.ToArgb();
    }

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

    private readonly Button _btnEditAuth = new() { Text = "编辑解锁", Width = 84 };
    private readonly Button _btnAdd = new() { Text = "新增", Width = 72 };
    private readonly Button _btnDelete = new() { Text = "删除", Width = 72 };
    private readonly Button _btnSave = new() { Text = "保存", Width = 72 };
    private readonly Button _btnReload = new() { Text = "重载", Width = 72 };

    private readonly string _configPath;
    private bool _suspendAutoSave;
    private bool _canAddDelete = true;
    private bool _canModify = true;
    private bool _editUnlocked;

    public PlcBoolLabelDisplayConfigControl()
    {
        _configPath = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
        BuildUi();
        BindEvents();
        LoadConfig();
    }

    public Action<string>? LogAction { get; set; }
    public Func<bool>? EditPermissionRequest { get; set; }
    public event Action<IReadOnlyList<PlcBoolLabelDisplayConfigItem>>? ConfigurationsChanged;

    public IReadOnlyList<PlcBoolLabelDisplayConfigItem> GetConfigurations()
    {
        var list = new List<PlcBoolLabelDisplayConfigItem>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            string address = GetCellString(row, 1);
            if (string.IsNullOrWhiteSpace(address))
                continue;

            string name = GetCellString(row, 0);
            if (string.IsNullOrWhiteSpace(name))
                name = address;

            RowColorBinding bind = EnsureColorBinding(row);
            list.Add(new PlcBoolLabelDisplayConfigItem
            {
                DisplayName = name,
                PlcAddress = address,
                TrueText = GetCellString(row, 2),
                FalseText = GetCellString(row, 3),
                ForeColorArgb = bind.ForeColorArgb,
                TrueBackColorArgb = bind.TrueBackColorArgb,
                FalseBackColorArgb = bind.FalseBackColorArgb
            });
        }

        return list;
    }

    public void SetConfigurations(IEnumerable<PlcBoolLabelDisplayConfigItem> items)
    {
        _suspendAutoSave = true;
        _grid.Rows.Clear();
        foreach (PlcBoolLabelDisplayConfigItem x in items)
        {
            int rowIndex = _grid.Rows.Add(
                x.DisplayName,
                x.PlcAddress,
                string.IsNullOrWhiteSpace(x.TrueText) ? "True" : x.TrueText,
                string.IsNullOrWhiteSpace(x.FalseText) ? "False" : x.FalseText,
                "字体色",
                "True背景",
                "False背景");
            DataGridViewRow row = _grid.Rows[rowIndex];
            row.Tag = new RowColorBinding
            {
                ForeColorArgb = x.ForeColorArgb,
                TrueBackColorArgb = x.TrueBackColorArgb,
                FalseBackColorArgb = x.FalseBackColorArgb
            };
            ApplyColorPreview(row);
        }
        _suspendAutoSave = false;
        RaiseConfigurationsChanged();
    }

    public void SetPermission(bool canAddDelete, bool canModify)
    {
        _canAddDelete = canAddDelete;
        _canModify = canModify;
        if (!canModify)
            _editUnlocked = false;

        _grid.ReadOnly = !_editUnlocked;
        _btnAdd.Enabled = _canAddDelete && _editUnlocked;
        _btnDelete.Enabled = _canAddDelete && _editUnlocked;
        _btnSave.Enabled = _canModify && _editUnlocked;
        _btnReload.Enabled = _canModify;
        _btnEditAuth.Enabled = _canModify;
        _btnEditAuth.Text = _editUnlocked ? "编辑锁定" : "编辑解锁";
    }

    private void BuildUi()
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "显示名称", FillWeight = 18 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "PLC地址", FillWeight = 18 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "True文本", FillWeight = 15 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "False文本", FillWeight = 15 });
        _grid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "字体颜色", FillWeight = 11, Text = "取色", UseColumnTextForButtonValue = true });
        _grid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "True背景", FillWeight = 11, Text = "取色", UseColumnTextForButtonValue = true });
        _grid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "False背景", FillWeight = 12, Text = "取色", UseColumnTextForButtonValue = true });

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 0, 0, 4)
        };
        toolbar.Controls.AddRange([_btnEditAuth, _btnAdd, _btnDelete, _btnSave, _btnReload]);

        Controls.Add(_grid);
        Controls.Add(toolbar);
    }

    private void BindEvents()
    {
        _btnEditAuth.Click += (_, _) => ToggleEditAuth();
        _btnAdd.Click += (_, _) =>
        {
            int rowIndex = _grid.Rows.Add("新状态", "MX0.0", "True", "False", "取色", "取色", "取色");
            _grid.Rows[rowIndex].Tag = new RowColorBinding();
            ApplyColorPreview(_grid.Rows[rowIndex]);
            SaveConfig();
            RaiseConfigurationsChanged();
        };
        _btnDelete.Click += (_, _) =>
        {
            if (_grid.SelectedRows.Count == 0)
                return;
            _grid.Rows.RemoveAt(_grid.SelectedRows[0].Index);
            SaveConfig();
            RaiseConfigurationsChanged();
        };
        _btnSave.Click += (_, _) =>
        {
            SaveConfig();
            RaiseConfigurationsChanged();
            LogAction?.Invoke("plcBoolLabel显示配置已保存。");
        };
        _btnReload.Click += (_, _) => LoadConfig();
        _grid.CellValueChanged += (_, _) =>
        {
            if (_suspendAutoSave || !_editUnlocked)
                return;
            SaveConfig();
            RaiseConfigurationsChanged();
        };
        _grid.CellContentClick += (_, e) =>
        {
            if (e.RowIndex < 0 || !_editUnlocked || !_canModify)
                return;

            if (e.ColumnIndex is not (4 or 5 or 6))
                return;

            DataGridViewRow row = _grid.Rows[e.RowIndex];
            RowColorBinding bind = EnsureColorBinding(row);
            Color current = e.ColumnIndex switch
            {
                4 => Color.FromArgb(bind.ForeColorArgb),
                5 => Color.FromArgb(bind.TrueBackColorArgb),
                _ => Color.FromArgb(bind.FalseBackColorArgb)
            };

            using var dlg = new ColorDialog { Color = current };
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK)
                return;

            switch (e.ColumnIndex)
            {
                case 4:
                    bind.ForeColorArgb = dlg.Color.ToArgb();
                    break;
                case 5:
                    bind.TrueBackColorArgb = dlg.Color.ToArgb();
                    break;
                case 6:
                    bind.FalseBackColorArgb = dlg.Color.ToArgb();
                    break;
            }

            ApplyColorPreview(row);
            SaveConfig();
            RaiseConfigurationsChanged();
        };

        SetPermission(_canAddDelete, _canModify);
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

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                SaveConfig();
                return;
            }

            string json = File.ReadAllText(_configPath);
            var list = JsonSerializer.Deserialize<List<PlcBoolLabelDisplayConfigItem>>(json) ?? [];
            SetConfigurations(list);
            LogAction?.Invoke($"plcBoolLabel显示配置已加载，共 {list.Count} 条。");
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"加载plcBoolLabel显示配置失败: {ex.Message}");
        }
    }

    private void SaveConfig()
    {
        if (_suspendAutoSave)
            return;

        try
        {
            string json = JsonSerializer.Serialize(GetConfigurations(), JsonFileOptions);
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"保存plcBoolLabel显示配置失败: {ex.Message}");
        }
    }

    private void RaiseConfigurationsChanged()
    {
        ConfigurationsChanged?.Invoke(GetConfigurations());
    }

    private static string GetCellString(DataGridViewRow row, int index)
    {
        return (Convert.ToString(row.Cells[index].Value) ?? string.Empty).Trim();
    }

    private static RowColorBinding EnsureColorBinding(DataGridViewRow row)
    {
        if (row.Tag is RowColorBinding binding)
            return binding;

        binding = new RowColorBinding();
        row.Tag = binding;
        return binding;
    }

    private static void ApplyColorPreview(DataGridViewRow row)
    {
        RowColorBinding bind = EnsureColorBinding(row);
        row.Cells[2].Style.ForeColor = Color.FromArgb(bind.ForeColorArgb);
        row.Cells[3].Style.ForeColor = Color.FromArgb(bind.ForeColorArgb);
        row.Cells[2].Style.BackColor = Color.FromArgb(bind.TrueBackColorArgb);
        row.Cells[3].Style.BackColor = Color.FromArgb(bind.FalseBackColorArgb);
    }
}
