using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PlcConectCom.WinFormsTest;

/// <summary>监控点取值类型。</summary>
public enum ModbusWatchValueType
{
    位,
    UINT16,
    INT16,
    DWORD,
    REAL
}

/// <summary>Modbus 地址区监控点（名称 + 地址 + 取值类型）。</summary>
public sealed class ModbusWatchPoint
{
    public string Name { get; set; } = string.Empty;

    /// <summary>支持 MW/MB/MD/MX 私有地址或 40001 / 00001 标准地址。</summary>
    public string Address { get; set; } = string.Empty;

    public ModbusWatchValueType ValueType { get; set; } = ModbusWatchValueType.INT16;
}

/// <summary>
/// Modbus TCP 服务端地址区查看窗口：
/// ① 地址区浏览——按起始地址+长度列出寄存器/线圈，多格式显示（HEX/UINT16/INT16/DWORD/REAL/ASCII），可翻页、可写入；
/// ② 监控点——自定义名称/地址/类型的点列表，自动刷新，可保存到本地配置。
/// </summary>
internal sealed class ModbusServerViewerForm : Form
{
    private static readonly JsonSerializerOptions JsonFileOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly ModbusTcpServerService _service;
    private readonly string _instanceName;
    private readonly int _port;
    private readonly byte _unitId;
    private readonly string _watchConfigPath;
    private readonly List<ModbusWatchPoint> _watchPoints = [];
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly DataGridView _browseGrid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        RowHeadersVisible = false,
        MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
        BackgroundColor = SystemColors.Window
    };
    private readonly DataGridView _watchGrid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        RowHeadersVisible = false,
        MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
    };
    private readonly TextBox _txtStart = new() { Text = "0", Width = 110 };
    private readonly NumericUpDown _numLength = new() { Width = 70, Minimum = 1, Maximum = 125, Value = 32 };
    private readonly ComboBox _cmbArea = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly ComboBox _cmbNotation = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly CheckBox _chkAuto = new() { Text = "自动刷新(1s)", AutoSize = true };
    private readonly ComboBox _cmbWriteType = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly TextBox _txtWriteValue = new() { Width = 120 };
    private readonly Button _btnWrite = new() { Text = "写入选中", Width = 84 };
    private readonly Label _lblStatus = new() { AutoSize = true, Padding = new Padding(8, 6, 0, 0) };
    private readonly Label _lblHint = new() { AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(8, 2, 0, 4) };

    private bool _watchUnlocked;
    private bool _suspendWatchSave;
    private bool _refreshing;

    public ModbusServerViewerForm(ModbusTcpServerService service, string instanceName, int port, byte unitId)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _instanceName = instanceName;
        _port = port;
        _unitId = unitId;
        string safe = new(instanceName.Where(char.IsLetterOrDigit).ToArray());
        _watchConfigPath = Path.Combine(AppContext.BaseDirectory, $"ModbusWatch.config.{safe}.json");

        Text = $"Modbus 地址区查看 - {instanceName}";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(940, 620);
        MinimumSize = new Size(760, 480);
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildUi();
        LoadWatchPoints();
        BindWatchGrid();
        _timer.Tick += async (_, _) => await RefreshAsync();
        // 监控点默认开启 1s 自动刷新，打开窗口即首次取数
        _timer.Start();
        Shown += async (_, _) => await RefreshAsync();
        FormClosing += (_, _) =>
        {
            _timer.Stop();
            _timer.Dispose();
        };
    }

    public bool CanModify { get; set; } = true;

    private void BuildUi()
    {
        _lblStatus.Text = $"实例[{_instanceName}]  模式:{_service.Mode}  端口:{_port}  从站号:{_unitId}  寄存器数:{_service.RegisterCount}";
        _lblHint.Text = "地址支持 MW/MB/MD/MX（私有）或 40001 / 00001（标准）；桥接模式下读写均转发到已连接 PLC。";

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var browseTab = new TabPage("地址区浏览") { Padding = new Padding(4) };
        var watchTab = new TabPage("监控点") { Padding = new Padding(4) };

        // —— 地址区浏览 ——
        _cmbArea.Items.AddRange(["保持寄存器", "线圈(位)"]);
        _cmbArea.SelectedIndex = 0;
        _cmbNotation.Items.AddRange(Enum.GetNames<ModbusAddressNotation>());
        _cmbNotation.SelectedIndex = 0;
        _cmbWriteType.Items.AddRange(["UINT16", "INT16", "DWORD", "REAL", "位"]);
        _cmbWriteType.SelectedIndex = 0;

        var browseBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(4, 4, 4, 6) };
        browseBar.Controls.Add(new Label { Text = "起始地址", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        browseBar.Controls.Add(_txtStart);
        browseBar.Controls.Add(new Label { Text = "长度", AutoSize = true, Padding = new Padding(6, 6, 0, 0) });
        browseBar.Controls.Add(_numLength);
        browseBar.Controls.Add(new Label { Text = "区", AutoSize = true, Padding = new Padding(6, 6, 0, 0) });
        browseBar.Controls.Add(_cmbArea);
        browseBar.Controls.Add(new Label { Text = "制式", AutoSize = true, Padding = new Padding(6, 6, 0, 0) });
        browseBar.Controls.Add(_cmbNotation);

        var btnRefresh = new Button { Text = "刷新", Width = 72 };
        var btnPrev = new Button { Text = "上一页", Width = 72 };
        var btnNext = new Button { Text = "下一页", Width = 72 };
        browseBar.Controls.Add(btnRefresh);
        browseBar.Controls.Add(btnPrev);
        browseBar.Controls.Add(btnNext);
        browseBar.Controls.Add(_chkAuto);
        browseBar.Controls.Add(new Label { Text = "写入", AutoSize = true, Padding = new Padding(10, 6, 0, 0) });
        browseBar.Controls.Add(_cmbWriteType);
        browseBar.Controls.Add(_txtWriteValue);
        browseBar.Controls.Add(_btnWrite);

        browseTab.Controls.Add(_browseGrid);
        browseTab.Controls.Add(browseBar);

        btnRefresh.Click += async (_, _) => await RefreshAsync();
        btnPrev.Click += async (_, _) => { MovePage(-1); await RefreshAsync(); };
        btnNext.Click += async (_, _) => { MovePage(1); await RefreshAsync(); };
        _btnWrite.Click += async (_, _) => await WriteSelectedAsync();
        _chkAuto.CheckedChanged += (_, _) =>
        {
            if (_chkAuto.Checked)
                _timer.Start();
            else
                _timer.Stop();
        };
        _cmbArea.SelectedIndexChanged += async (_, _) => await RefreshAsync();
        _cmbNotation.SelectedIndexChanged += async (_, _) => await RefreshAsync();

        // —— 监控点 ——
        var watchBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(4, 4, 4, 6) };
        var btnAdd = new Button { Text = "新增", Width = 72 };
        var btnDelete = new Button { Text = "删除", Width = 72 };
        var btnUnlock = new Button { Text = "编辑解锁", Width = 84 };
        var btnSave = new Button { Text = "保存本地", Width = 84 };
        var btnLoad = new Button { Text = "加载配置", Width = 84 };
        var chkWatchAuto = new CheckBox { Text = "自动刷新(1s)", AutoSize = true, Checked = true };
        watchBar.Controls.AddRange([btnAdd, btnDelete, btnUnlock, btnSave, btnLoad, chkWatchAuto]);
        watchTab.Controls.Add(_watchGrid);
        watchTab.Controls.Add(watchBar);

        _watchGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "WName", HeaderText = "名称" });
        _watchGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "WAddr", HeaderText = "地址" });
        _watchGrid.Columns.Add(new DataGridViewComboBoxColumn
        {
            Name = "WType",
            HeaderText = "类型",
            DataSource = Enum.GetNames<ModbusWatchValueType>()
        });
        _watchGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "WValue", HeaderText = "当前值", ReadOnly = true });
        _watchGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "WTime", HeaderText = "更新时间", ReadOnly = true });
        _watchGrid.ReadOnly = true;

        btnAdd.Click += (_, _) =>
        {
            if (!_watchUnlocked)
            {
                MessageBox.Show(this, "请先点击「编辑解锁」。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _watchPoints.Add(new ModbusWatchPoint { Name = $"点{_watchPoints.Count + 1}", Address = "0", ValueType = ModbusWatchValueType.INT16 });
            BindWatchGrid();
            SaveWatchPoints();
        };
        btnDelete.Click += (_, _) =>
        {
            if (!_watchUnlocked || _watchGrid.SelectedRows.Count == 0)
                return;

            _watchPoints.RemoveAt(_watchGrid.SelectedRows[0].Index);
            BindWatchGrid();
            SaveWatchPoints();
        };
        btnUnlock.Click += (_, _) =>
        {
            _watchUnlocked = !_watchUnlocked;
            btnUnlock.Text = _watchUnlocked ? "编辑锁定" : "编辑解锁";
            _watchGrid.ReadOnly = !_watchUnlocked;
        };
        btnSave.Click += (_, _) => SaveWatchPoints();
        btnLoad.Click += (_, _) =>
        {
            LoadWatchPoints();
            BindWatchGrid();
        };
        chkWatchAuto.CheckedChanged += (_, _) =>
        {
            if (chkWatchAuto.Checked)
                _timer.Start();
            else if (!_chkAuto.Checked)
                _timer.Stop();
        };

        // 单元格改动后先同步回模型再落盘（避免把「当前值/时间」列写进配置）
        _watchGrid.CellValueChanged += (_, _) => SyncWatchPointsFromGrid();

        tabs.TabPages.Add(browseTab);
        tabs.TabPages.Add(watchTab);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.Controls.Add(_lblStatus, 0, 0);
        root.Controls.Add(_lblHint, 0, 1);
        root.Controls.Add(tabs, 0, 2);
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 4, 6, 0) };
        var btnClose = new Button { Text = "关闭", Width = 80, DialogResult = DialogResult.Cancel };
        bottom.Controls.Add(btnClose);
        root.Controls.Add(bottom, 0, 3);
        Controls.Add(root);
        CancelButton = btnClose;

        ApplyPermissions();
    }

    private void ApplyPermissions()
    {
        _cmbWriteType.Enabled = CanModify;
        _txtWriteValue.Enabled = CanModify;
        _btnWrite.Enabled = CanModify;
    }

    private void MovePage(int direction)
    {
        if (!ModbusServerAddressMap.TryParseStartAddress(_txtStart.Text, out bool isBit, out int index, out _))
            return;

        int length = (int)_numLength.Value;
        index = Math.Max(0, index + direction * length);
        _suspendWatchSave = true;
        _txtStart.Text = isBit
            ? ModbusServerAddressMap.BitToMx(index)
            : ModbusServerAddressMap.RegisterToMw(index);
        _suspendWatchSave = false;
    }

    private async Task RefreshAsync()
    {
        if (_refreshing)
            return;

        _refreshing = true;
        try
        {
            await RefreshBrowseAsync().ConfigureAwait(true);
            await RefreshWatchValuesAsync().ConfigureAwait(true);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task RefreshBrowseAsync()
    {
        if (!ModbusServerAddressMap.TryParseStartAddress(_txtStart.Text, out bool inputIsBit, out int start, out string? error))
        {
            _browseGrid.Rows.Clear();
            _browseGrid.Columns.Clear();
            _lblHint.Text = error ?? string.Empty;
            return;
        }

        bool coilMode = _cmbArea.SelectedIndex == 1;
        int index = coilMode && !inputIsBit ? start * 16 : start;
        var notation = Enum.TryParse<ModbusAddressNotation>(_cmbNotation.SelectedItem?.ToString(), out var n)
            ? n
            : ModbusAddressNotation.私有地址;

        _browseGrid.Columns.Clear();
        _browseGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Idx", HeaderText = coilMode ? "位号" : "寄存器号" });
        _browseGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "AddrPrivate", HeaderText = coilMode ? "MX地址" : "MW地址" });
        _browseGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "AddrStd", HeaderText = coilMode ? "线圈(0xxxx)" : "保持(4xxxx)" });
        _browseGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Hex", HeaderText = "HEX" });
        if (!coilMode)
        {
            _browseGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "U16", HeaderText = "UINT16" });
            _browseGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "I16", HeaderText = "INT16" });
            _browseGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Dword", HeaderText = "DWORD(高字在前)" });
            _browseGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Real", HeaderText = "REAL" });
            _browseGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Ascii", HeaderText = "ASCII" });
        }
        else
        {
            _browseGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Val", HeaderText = "值" });
        }

        int length = (int)_numLength.Value;
        _browseGrid.Rows.Clear();

        if (coilMode)
        {
            bool[] bits = await _service.ReadCoilsAsync(index, length).ConfigureAwait(true);
            for (int i = 0; i < bits.Length; i++)
            {
                int bit = index + i;
                _browseGrid.Rows.Add(
                    bit.ToString(CultureInfo.InvariantCulture),
                    ModbusServerAddressMap.BitToMx(bit),
                    ModbusServerAddressMap.BitToStandard(bit),
                    bits[i] ? "0x0001" : "0x0000",
                    bits[i] ? "True" : "False");
            }

            return;
        }

        ushort[] regs = await _service.ReadRegistersAsync(index, length).ConfigureAwait(true);
        // 多格式显示需要多读一个寄存器（DWORD/REAL 需 2 个）
        ushort[] extended = regs.Length >= length + 1
            ? regs
            : await _service.ReadRegistersAsync(index, Math.Min(length + 1, 125)).ConfigureAwait(true);

        for (int i = 0; i < regs.Length; i++)
        {
            int register = index + i;
            ushort value = regs[i];
            string dword = "-";
            string real = "-";
            if (i + 1 < extended.Length)
            {
                uint combined = ((uint)extended[i] << 16) | extended[i + 1];
                dword = combined.ToString(CultureInfo.InvariantCulture);
                real = BitConverter.Int32BitsToSingle(unchecked((int)combined)).ToString("G7", CultureInfo.InvariantCulture);
            }

            string ascii = $"{(char)(value & 0xFF)}{(char)(value >> 8)}".Replace('\0', '·');
            _browseGrid.Rows.Add(
                register.ToString(CultureInfo.InvariantCulture),
                ModbusServerAddressMap.FormatRegisterAddress(register, notation),
                ModbusServerAddressMap.FormatRegisterAddress(register, ModbusAddressNotation.标准Modbus),
                "0x" + value.ToString("X4", CultureInfo.InvariantCulture),
                value.ToString(CultureInfo.InvariantCulture),
                unchecked((short)value).ToString(CultureInfo.InvariantCulture),
                dword,
                real,
                ascii);
        }
    }

    private async Task WriteSelectedAsync()
    {
        if (!CanModify)
        {
            MessageBox.Show(this, "当前角色无修改权限。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_browseGrid.SelectedRows.Count == 0)
        {
            MessageBox.Show(this, "请先在表格中选中要写入的行。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        int rowIndex = _browseGrid.SelectedRows[0].Index;
        string? indexText = _browseGrid.Rows[rowIndex].Cells["Idx"].Value?.ToString();
        if (!int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
            return;

        bool coilMode = _cmbArea.SelectedIndex == 1;
        string type = _cmbWriteType.SelectedItem?.ToString() ?? "UINT16";
        string raw = _txtWriteValue.Text.Trim();

        try
        {
            if (coilMode || type == "位")
            {
                bool value = raw is "1" or "true" or "True" or "TRUE" or "ON" or "on";
                bool ok = await _service.WriteCoilsAsync(index, [value]).ConfigureAwait(true);
                ShowWriteResult(ok);
                await RefreshAsync().ConfigureAwait(true);
                return;
            }

            ushort[] payload = type switch
            {
                "INT16" => [(ushort)short.Parse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture)],
                "DWORD" => SplitUInt(uint.Parse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture)),
                "REAL" => SplitUInt(unchecked((uint)BitConverter.SingleToInt32Bits(
                    float.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)))),
                _ => [ushort.Parse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture)]
            };

            bool written = await _service.WriteRegistersAsync(index, payload).ConfigureAwait(true);
            ShowWriteResult(written);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"写入值解析失败: {ex.Message}", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowWriteResult(bool ok)
    {
        if (ok)
        {
            _lblHint.Text = "已写入。" + (_service.Mode == ModbusServerDataMode.桥接PLC ? "（桥接模式：已转发写入 PLC）" : "（本地地址区）");
            return;
        }

        MessageBox.Show(this,
            _service.Mode == ModbusServerDataMode.桥接PLC
                ? "桥接模式写入失败：PLC 未连接。已写入本地镜像，连上 PLC 后请重新写入。"
                : "写入失败。",
            "提示",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private static ushort[] SplitUInt(uint value) => [(ushort)(value >> 16), (ushort)(value & 0xFFFF)];

    // —— 监控点 ——

    private void BindWatchGrid()
    {
        _suspendWatchSave = true;
        try
        {
            _watchGrid.Rows.Clear();
            foreach (ModbusWatchPoint p in _watchPoints)
            {
                int r = _watchGrid.Rows.Add();
                _watchGrid.Rows[r].Cells["WName"].Value = p.Name;
                _watchGrid.Rows[r].Cells["WAddr"].Value = p.Address;
                _watchGrid.Rows[r].Cells["WType"].Value = p.ValueType.ToString();
                _watchGrid.Rows[r].Cells["WValue"].Value = "--";
                _watchGrid.Rows[r].Cells["WTime"].Value = string.Empty;
            }
        }
        finally
        {
            _suspendWatchSave = false;
        }
    }

    private void SyncWatchPointsFromGrid()
    {
        if (_suspendWatchSave)
            return;

        var list = new List<ModbusWatchPoint>();
        foreach (DataGridViewRow row in _watchGrid.Rows)
        {
            string name = row.Cells["WName"].Value?.ToString()?.Trim() ?? string.Empty;
            string addr = row.Cells["WAddr"].Value?.ToString()?.Trim() ?? string.Empty;
            if (name.Length == 0 && addr.Length == 0)
                continue;

            var type = Enum.TryParse<ModbusWatchValueType>(row.Cells["WType"].Value?.ToString(), out var t)
                ? t
                : ModbusWatchValueType.INT16;
            list.Add(new ModbusWatchPoint { Name = name, Address = addr, ValueType = type });
        }

        _watchPoints.Clear();
        _watchPoints.AddRange(list);
        SaveWatchPoints();
    }

    private async Task RefreshWatchValuesAsync()
    {
        if (_watchGrid.Rows.Count == 0)
            return;

        string stamp = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        foreach (DataGridViewRow row in _watchGrid.Rows)
        {
            string addr = row.Cells["WAddr"].Value?.ToString()?.Trim() ?? string.Empty;
            if (addr.Length == 0)
                continue;

            var type = Enum.TryParse<ModbusWatchValueType>(row.Cells["WType"].Value?.ToString(), out var t)
                ? t
                : ModbusWatchValueType.INT16;

            try
            {
                row.Cells["WValue"].Value = await ReadWatchValueAsync(addr, type).ConfigureAwait(true);
                row.Cells["WTime"].Value = stamp;
            }
            catch (Exception ex)
            {
                row.Cells["WValue"].Value = "ERR";
                row.Cells["WTime"].Value = stamp;
                _lblHint.Text = $"监控点[{addr}]读取失败: {ex.Message}";
            }
        }
    }

    private async Task<string> ReadWatchValueAsync(string address, ModbusWatchValueType type)
    {
        if (!ModbusServerAddressMap.TryParseStartAddress(address, out bool isBit, out int index, out string? error))
            throw new InvalidOperationException(error ?? "地址无效");

        if (type == ModbusWatchValueType.位)
        {
            bool[] bits = await _service.ReadCoilsAsync(isBit ? index : index * 16, 1).ConfigureAwait(true);
            return bits.Length > 0 ? (bits[0] ? "True" : "False") : "--";
        }

        int registerCount = type is ModbusWatchValueType.DWORD or ModbusWatchValueType.REAL ? 2 : 1;
        int register = isBit ? index / 16 : index;
        ushort[] regs = await _service.ReadRegistersAsync(register, registerCount).ConfigureAwait(true);
        if (regs.Length == 0)
            return "--";

        return type switch
        {
            ModbusWatchValueType.UINT16 => regs[0].ToString(CultureInfo.InvariantCulture),
            ModbusWatchValueType.INT16 => unchecked((short)regs[0]).ToString(CultureInfo.InvariantCulture),
            ModbusWatchValueType.DWORD => regs.Length >= 2
                ? (((uint)regs[0] << 16) | regs[1]).ToString(CultureInfo.InvariantCulture)
                : "--",
            ModbusWatchValueType.REAL => regs.Length >= 2
                ? BitConverter.Int32BitsToSingle(unchecked((int)(((uint)regs[0] << 16) | regs[1]))).ToString("G7", CultureInfo.InvariantCulture)
                : "--",
            _ => regs[0].ToString(CultureInfo.InvariantCulture)
        };
    }

    private void LoadWatchPoints()
    {
        try
        {
            if (!File.Exists(_watchConfigPath))
                return;

            var list = JsonSerializer.Deserialize<List<ModbusWatchPoint>>(File.ReadAllText(_watchConfigPath));
            _watchPoints.Clear();
            if (list is not null)
                _watchPoints.AddRange(list);
        }
        catch (Exception ex)
        {
            _lblHint.Text = $"监控点配置加载失败: {ex.Message}";
        }
    }

    private void SaveWatchPoints()
    {
        if (_suspendWatchSave)
            return;

        try
        {
            File.WriteAllText(_watchConfigPath, JsonSerializer.Serialize(_watchPoints, JsonFileOptions));
        }
        catch (Exception ex)
        {
            _lblHint.Text = $"监控点配置保存失败: {ex.Message}";
        }
    }
}
