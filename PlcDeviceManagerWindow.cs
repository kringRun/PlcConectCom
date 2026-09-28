namespace PlcConectCom.WinFormsTest;

/// <summary>编辑主 PLC 和子 PLC 设备清单。</summary>
public sealed class PlcDeviceManagerWindow : Form
{
    private readonly DataGridView _devices = new();
    private readonly Label _message = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly Func<PlcDeviceConfiguration, Task<IReadOnlyList<string>>> _apply;
    private readonly Func<string, bool> _isConnected;
    private PlcDeviceConfiguration _configuration;

    public PlcDeviceManagerWindow(
        PlcDeviceConfiguration configuration,
        Func<PlcDeviceConfiguration, Task<IReadOnlyList<string>>> apply,
        Func<string, bool> isConnected)
    {
        _configuration = configuration.Copy();
        _apply = apply;
        _isConnected = isConnected;
        Text = "\u0050LC \u8bbe\u5907\u4e0e\u5b50\u6a21\u5757\u7ba1\u7406";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(820, 560);
        Size = new Size(980, 680);
        BuildUi();
        LoadRows();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            Text = "每台设备建立独立 Modbus TCP 连接；新增设备在各功能区以子 PLC 分页显示，主 PLC 保留现有功能区。",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        ((Label)root.Controls[0]).Text = "\u6bcf\u53f0\u8bbe\u5907\u5efa\u7acb\u72ec\u7acb Modbus TCP \u8fde\u63a5\uff1b\u529f\u80fd\u9875\u6309 PLC \u5206\u9875\u663e\u793a\uff0c\u6bcf\u53f0\u8bbe\u5907\u7684\u53c2\u6570\u4e92\u76f8\u72ec\u7acb\u3002";

        ConfigureGrid(_devices);
        _devices.Columns.Add(new DataGridViewTextBoxColumn { Name = "Id", HeaderText = "ID", Visible = false });
        _devices.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "设备名称", Width = 150 });
        _devices.Columns.Add(new DataGridViewTextBoxColumn { Name = "IpAddress", HeaderText = "IP 地址", Width = 150 });
        _devices.Columns.Add(new DataGridViewTextBoxColumn { Name = "Port", HeaderText = "端口", Width = 80 });
        _devices.Columns.Add(new DataGridViewTextBoxColumn { Name = "SlaveId", HeaderText = "站号", Width = 70 });
        _devices.Columns.Add(new DataGridViewTextBoxColumn { Name = "Heartbeat", HeaderText = "心跳地址（可空）", Width = 160 });
        _devices.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "连接状态", ReadOnly = true, Width = 90 });
        var deviceHost = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        deviceHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        deviceHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        deviceHost.Controls.Add(_devices, 0, 0);
        var deviceButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        var add = new Button { Text = "添加设备", AutoSize = true };
        var remove = new Button { Text = "删除所选设备", AutoSize = true };
        add.Click += (_, _) => AddDevice();
        remove.Click += (_, _) => RemoveSelectedDevice();
        deviceButtons.Controls.AddRange([add, remove]);
        deviceHost.Controls.Add(deviceButtons, 0, 1);
        root.Controls.Add(deviceHost, 0, 1);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var close = new Button { Text = "关闭", AutoSize = true };
        var save = new Button { Text = "保存并连接", AutoSize = true };
        close.Click += (_, _) => Close();
        save.Click += async (_, _) => await ApplyAsync(save);
        footer.Controls.Add(close);
        footer.Controls.Add(save);
        footer.Controls.Add(_message);
        root.Controls.Add(footer, 0, 2);
    }

    private static void ConfigureGrid(DataGridView grid)
    {
        grid.ColumnAdded += (_, e) => e.Column.SortMode = DataGridViewColumnSortMode.NotSortable;
        grid.Dock = DockStyle.Fill;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AutoGenerateColumns = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.BackgroundColor = Color.White;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
    }

    private void LoadRows()
    {
        _devices.Rows.Clear();
        foreach (PlcDeviceProfile device in _configuration.Devices)
            _devices.Rows.Add(device.Id, device.Name, device.IpAddress, device.Port, device.SlaveId, device.HeartbeatBoolAddress,
                _isConnected(device.Id) ? "已连接" : "未连接");
    }

    private void AddDevice()
    {
        CommitGridEdits();
        _configuration = ReadConfiguration();
        _configuration.Devices.Add(new PlcDeviceProfile { Name = $"PLC {_configuration.Devices.Count + 1}" });
        LoadRows();
        _devices.CurrentCell = _devices.Rows[_devices.Rows.Count - 1].Cells["Name"];
        _devices.BeginEdit(true);
    }

    private void RemoveSelectedDevice()
    {
        if (_devices.CurrentRow is null)
            return;

        if (_devices.CurrentRow.Index == 0)
        {
            MessageBox.Show(this, "主 PLC 是现有功能区的固定设备，不能删除。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        CommitGridEdits();
        _configuration = ReadConfiguration();
        string id = Convert.ToString(_devices.CurrentRow.Cells["Id"].Value) ?? string.Empty;
        if (_configuration.Devices.Count <= 1)
        {
            MessageBox.Show(this, "至少保留一台 PLC 设备。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _configuration.Devices.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        foreach (string key in _configuration.Bindings.Keys.ToList())
            if (string.Equals(_configuration.Bindings[key], id, StringComparison.OrdinalIgnoreCase))
                _configuration.Bind(key, _configuration.Devices[0].Id);
        LoadRows();
    }

    private async Task ApplyAsync(Button button)
    {
        try
        {
            button.Enabled = false;
            CommitGridEdits();
            PlcDeviceConfiguration updated = ReadConfiguration();
            IReadOnlyList<string> errors = await _apply(updated);
            _configuration = updated;
            LoadRows();
            if (errors.Count == 0)
            {
                _message.Text = "设备配置已保存，连接已建立。";
                _message.ForeColor = Color.DarkGreen;
            }
            else
            {
                _message.Text = $"配置已保存；连接异常：{string.Join("；", errors)}";
                _message.ForeColor = Color.Firebrick;
            }
        }
        catch (Exception ex)
        {
            _message.Text = ex.GetBaseException().Message;
            _message.ForeColor = Color.Firebrick;
        }
        finally
        {
            button.Enabled = true;
        }
    }

    private void CommitGridEdits()
    {
        _devices.EndEdit();
    }

    private PlcDeviceConfiguration ReadConfiguration()
    {
        var result = new PlcDeviceConfiguration
        {
            Bindings = new Dictionary<string, string>(_configuration.Bindings, StringComparer.OrdinalIgnoreCase)
        };
        foreach (DataGridViewRow row in _devices.Rows)
        {
            string id = Convert.ToString(row.Cells["Id"].Value) ?? string.Empty;
            string name = Convert.ToString(row.Cells["Name"].Value)?.Trim() ?? string.Empty;
            string ip = Convert.ToString(row.Cells["IpAddress"].Value)?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(ip))
                throw new InvalidOperationException("设备名称和 IP 地址不能为空。");

            if (!int.TryParse(Convert.ToString(row.Cells["Port"].Value), out int port) || port is < 1 or > 65535)
                throw new InvalidOperationException($"设备 {name} 的端口必须在 1 到 65535 之间。");
            if (!int.TryParse(Convert.ToString(row.Cells["SlaveId"].Value), out int slave) || slave is < 0 or > 255)
                throw new InvalidOperationException($"设备 {name} 的站号必须在 0 到 255 之间。");

            result.Devices.Add(new PlcDeviceProfile
            {
                Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id,
                Name = name,
                IpAddress = ip,
                Port = port,
                SlaveId = slave,
                HeartbeatBoolAddress = Convert.ToString(row.Cells["Heartbeat"].Value)?.Trim() ?? string.Empty
            });
        }
        result.Normalize();
        result.EnsureDefaultBindings();
        return result;
    }

}
