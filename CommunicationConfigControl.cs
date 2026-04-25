using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PlcConectCom.WinFormsTest;

// 通讯实例类型
public enum CommunicationInstanceType
{
    Socket客户端,
    Socket服务端,
    串口
}

/// <summary>Socket 客户端回包按分隔符拆分后，用「标记名 → 分片序号」供任务步骤取值。</summary>
public sealed class SocketClientResultField
{
    public string Name { get; set; } = string.Empty;

    /// <summary>从 0 开始，对应 Split 后的数组下标。</summary>
    public int Index { get; set; }
}

// 通讯实例配置
public sealed class CommunicationInstanceConfig
{
    public string Name { get; set; } = string.Empty;

    public CommunicationInstanceType Type { get; set; } = CommunicationInstanceType.Socket客户端;

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 9000;

    public string ComPort { get; set; } = "COM1";

    public int BaudRate { get; set; } = 9600;

    // —— Socket 客户端扩展（任务线程与测试均读此配置）——

    /// <summary>发送报文后是否在同一条连接上等待服务端回包。</summary>
    public bool WaitForResponse { get; set; }

    /// <summary>等待回包超时（毫秒），发送后等待与「仅等待数据」步骤共用。</summary>
    public int ReceiveTimeoutMs { get; set; } = 3000;

    /// <summary>将回包拆成数组的分隔符，默认逗号（如 123,456,789）。</summary>
    public string ResponseValueDelimiter { get; set; } = ",";

    public List<SocketClientResultField> SocketResultFields { get; set; } = [];

    /// <summary>任务线程复用同一条 TCP，不在每次发送后断开。</summary>
    public bool PersistentConnection { get; set; }
}

// 通讯配置控件：用于维护可供任务引用的通讯实例
public sealed class CommunicationConfigControl : UserControl
{
    private const string ConfigFileName = "CommunicationInstances.config.json";
    private static readonly JsonSerializerOptions JsonFileOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true
    };

    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        RowHeadersVisible = false,
        MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
    };

    private readonly Button _btnEditAuth = new() { Text = "编辑解锁", Width = 84 };
    private readonly Button _btnAdd = new() { Text = "新增实例", Width = 84 };
    private readonly Button _btnEdit = new() { Text = "编辑实例", Width = 84 };
    private readonly Button _btnDelete = new() { Text = "删除实例", Width = 84 };
    private readonly Button _btnStartServer = new() { Text = "启动监听", Width = 84 };
    private readonly Button _btnStopServer = new() { Text = "停止监听", Width = 84, Enabled = false };
    private readonly Button _btnRefreshClients = new() { Text = "刷新客户端", Width = 92 };
    private readonly Label _lblServerStatus = new() { Text = "服务端状态: 未启动", AutoSize = true, Padding = new Padding(8, 8, 0, 0) };
    private readonly ListBox _lstClients = new() { Dock = DockStyle.Fill };
    private readonly TextBox _txtServerReceiveLog = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, ReadOnly = true };

    private readonly string _configPath;
    private readonly List<CommunicationInstanceConfig> _instances = [];
    private SocketServerCommunicationService? _activeSocketServer;
    private readonly Dictionary<string, SocketClientCommunicationService> _testSocketClients = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>任务用 Socket 长连接（与测试窗「连接」字典分离）。Key = BuildInstanceKey。</summary>
    private readonly ConcurrentDictionary<string, SocketClientCommunicationService> _taskPersistentSockets = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, (string Host, int Port)> _taskPersistentEndpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _taskPersistentGates = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeServerName;
    private bool _canModify = true;
    private bool _editUnlocked;

    public CommunicationConfigControl()
    {
        _configPath = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
        BuildUi();
        BindEvents();
        LoadConfig();
    }

    public Func<bool>? EditPermissionRequest { get; set; }

    public Action<string>? LogAction { get; set; }

    public Func<global::PlcConectCom.PlcCommunicator?>? PlcProvider { get; set; }

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
        _btnStartServer.Enabled = canModify && _activeSocketServer is null;
        _btnStopServer.Enabled = canModify && _activeSocketServer is not null;
        _btnRefreshClients.Enabled = canModify;
    }

    public IReadOnlyList<TaskSourceAddressOption> GetSocketInstanceOptions()
    {
        return _instances
            .Where(x => x.Type is CommunicationInstanceType.Socket客户端 or CommunicationInstanceType.Socket服务端)
            .Select(x => new TaskSourceAddressOption
            {
                Name = x.Name,
                Address = BuildInstanceKey(x)
            })
            .ToList();
    }

    public IReadOnlyList<string> GetSocketResultFieldNames(string? instanceKeyOrName)
    {
        CommunicationInstanceConfig? cfg = FindByInstanceKey(instanceKeyOrName);
        if (cfg?.SocketResultFields is not { Count: > 0 })
            return [];

        return cfg.SocketResultFields
            .Select(f => f.Name?.Trim() ?? string.Empty)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int ClampReceiveTimeoutMs(int ms) => Math.Clamp(ms, 100, 120_000);

    private static IReadOnlyDictionary<string, string>? BuildSocketSliceValueMap(
        CommunicationInstanceConfig cfg,
        string trimmed)
    {
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SocketResponseParser.FullPayloadMapKey] = trimmed
        };

        string[] parts = SocketResponseParser.SplitValues(trimmed, cfg.ResponseValueDelimiter);
        if (cfg.SocketResultFields is not { Count: > 0 })
            return dict;

        foreach (SocketClientResultField field in cfg.SocketResultFields)
        {
            string name = field.Name?.Trim() ?? string.Empty;
            if (name.Length == 0)
                continue;

            if (SocketResponseParser.TryGetValueByMarkerName(cfg.SocketResultFields, parts, name, out string v))
                dict[name] = v;
        }

        return dict;
    }

    private TaskStepExecuteResult BuildSocketClientStepResult(
        CommunicationInstanceConfig cfg,
        string rawReceive,
        string actionLabel,
        TaskExecutionConfig execution)
    {
        string slice = execution.SocketResultSliceName?.Trim() ?? string.Empty;
        string trimmed = (rawReceive ?? string.Empty).TrimEnd('\r', '\n');

        if (string.IsNullOrWhiteSpace(trimmed))
            return new TaskStepExecuteResult($"执行完成：Socket客户端[{cfg.Name}] {actionLabel}，无回包", string.Empty, null);

        IReadOnlyDictionary<string, string>? map = BuildSocketSliceValueMap(cfg, trimmed);
        string[] parts = SocketResponseParser.SplitValues(trimmed, cfg.ResponseValueDelimiter);
        string store = SocketResponseParser.ResolveStoreValue(
            trimmed,
            cfg.ResponseValueDelimiter,
            cfg.SocketResultFields,
            slice);

        string partsLog = SocketResponseParser.FormatPartsForLog(parts);
        if (string.IsNullOrWhiteSpace(slice))
            return new TaskStepExecuteResult($"执行完成：Socket客户端[{cfg.Name}] {actionLabel}，{partsLog}", store, map);

        if (string.IsNullOrEmpty(store))
            return new TaskStepExecuteResult($"执行完成：Socket客户端[{cfg.Name}] {actionLabel}，{partsLog}，未匹配标记[{slice}]", string.Empty, map);

        return new TaskStepExecuteResult($"执行完成：Socket客户端[{cfg.Name}] {actionLabel}，{partsLog}，标记[{slice}]={store}", store, map);
    }

    // 执行任务动作（通讯相关）
    public async Task<TaskStepExecuteResult> ExecuteTaskActionAsync(TaskExecutionConfig execution)
    {
        CommunicationInstanceConfig? cfg = FindByInstanceKey(execution.Target);
        switch (execution.ExecutionType)
        {
            case TaskExecutionType.Socket通讯发送数据:
                {
                    if (cfg?.Type != CommunicationInstanceType.Socket客户端)
                        return new TaskStepExecuteResult($"执行失败：未找到Socket客户端实例[{execution.Target}]", string.Empty);

                    string mode = cfg.PersistentConnection ? "长连接" : "短连接";
                    return await RunWithTaskSocketClientAsync(cfg, async client =>
                    {
                        string message = execution.Payload ?? string.Empty;
                        if (!message.EndsWith("\n", StringComparison.Ordinal))
                            message += "\r\n";
                        await client.SendAsync(message).ConfigureAwait(false);
                        string receive = string.Empty;
                        if (cfg.WaitForResponse)
                        {
                            try
                            {
                                receive = await client.ReceiveAsync(timeoutMs: ClampReceiveTimeoutMs(cfg.ReceiveTimeoutMs))
                                    .ConfigureAwait(false);
                            }
                            catch
                            {
                                // 超时视为无回包
                            }
                        }

                        if (!cfg.WaitForResponse)
                            return new TaskStepExecuteResult(
                                $"执行完成：Socket客户端[{cfg.Name}] [{mode}] 已发送报文（未等回包）",
                                string.Empty,
                                null);

                        return BuildSocketClientStepResult(cfg, receive, $"[{mode}]已发送并等待回包", execution);
                    }).ConfigureAwait(false);
                }
            case TaskExecutionType.Socket通讯等待数据:
                {
                    if (cfg?.Type != CommunicationInstanceType.Socket客户端)
                        return new TaskStepExecuteResult($"执行失败：未找到Socket客户端实例[{execution.Target}]", string.Empty);

                    string mode = cfg.PersistentConnection ? "长连接" : "短连接";
                    return await RunWithTaskSocketClientAsync(cfg, async client =>
                    {
                        string receive;
                        try
                        {
                            receive = await client.ReceiveAsync(timeoutMs: ClampReceiveTimeoutMs(cfg.ReceiveTimeoutMs))
                                .ConfigureAwait(false);
                        }
                        catch
                        {
                            receive = string.Empty;
                        }

                        return BuildSocketClientStepResult(cfg, receive, $"[{mode}]仅接收", execution);
                    }).ConfigureAwait(false);
                }
            case TaskExecutionType.串口通讯发送数据:
                {
                    if (cfg?.Type != CommunicationInstanceType.串口)
                        return new TaskStepExecuteResult($"执行失败：未找到串口实例[{execution.Target}]", string.Empty);

                    using var serial = new SerialCommunicationService();
                    serial.Open(cfg.ComPort, cfg.BaudRate);
                    serial.Send(execution.Payload ?? string.Empty);
                    serial.Close();
                    return new TaskStepExecuteResult($"执行完成：串口[{cfg.Name}] 已发送报文", string.Empty);
                }
            case TaskExecutionType.串口通讯等待数据:
                {
                    if (cfg?.Type != CommunicationInstanceType.串口)
                        return new TaskStepExecuteResult($"执行失败：未找到串口实例[{execution.Target}]", string.Empty);

                    using var serial = new SerialCommunicationService();
                    serial.Open(cfg.ComPort, cfg.BaudRate);
                    string receive = serial.ReadLine(3000);
                    serial.Close();
                    string store = (receive ?? string.Empty).TrimEnd('\r', '\n');
                    return string.IsNullOrWhiteSpace(receive)
                        ? new TaskStepExecuteResult($"执行完成：串口[{cfg.Name}] 等待结束，无数据", string.Empty)
                        : new TaskStepExecuteResult($"执行完成：串口[{cfg.Name}] 收到[{receive}]", store);
                }
            case TaskExecutionType.DO地址赋值:
            case TaskExecutionType.报警地址赋值:
                {
                    var plc = PlcProvider?.Invoke();
                    if (plc is not { IsConnected: true })
                        return new TaskStepExecuteResult($"执行失败：PLC 未连接，无法执行{execution.ExecutionType}", string.Empty);

                    string addr = execution.Target?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(addr))
                        return new TaskStepExecuteResult("执行失败：目标地址为空", string.Empty);

                    try
                    {
                        if (execution.ExecutionType == TaskExecutionType.DO地址赋值)
                        {
                            bool v = TryParseBoolPayload(execution.Payload);
                            await plc.WriteBoolAsync(addr, v);
                            return new TaskStepExecuteResult($"执行完成：DO [{addr}] = {v}", v ? "true" : "false");
                        }

                        string payload = execution.Payload ?? string.Empty;
                        await plc.WriteStringAsync(addr, payload);
                        return new TaskStepExecuteResult($"执行完成：报警地址 [{addr}] 已写入文本", payload);
                    }
                    catch (Exception ex)
                    {
                        return new TaskStepExecuteResult($"执行失败：[{addr}] {ex.Message}", string.Empty);
                    }
                }
            default:
                return new TaskStepExecuteResult("执行完成", string.Empty);
        }
    }

    private static bool TryParseBoolPayload(string? payload)
    {
        string p = payload?.Trim() ?? string.Empty;
        if (bool.TryParse(p, out bool b))
            return b;

        if (string.Equals(p, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(p, "高", StringComparison.OrdinalIgnoreCase)
            || string.Equals(p, "on", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(p, "0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(p, "低", StringComparison.OrdinalIgnoreCase)
            || string.Equals(p, "off", StringComparison.OrdinalIgnoreCase))
            return false;

        if (short.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out short n))
            return n != 0;

        return false;
    }

    public IReadOnlyList<TaskSourceAddressOption> GetSerialInstanceOptions()
    {
        return _instances
            .Where(x => x.Type == CommunicationInstanceType.串口)
            .Select(x => new TaskSourceAddressOption
            {
                Name = x.Name,
                Address = BuildInstanceKey(x)
            })
            .ToList();
    }

    private static string BuildInstanceKey(CommunicationInstanceConfig item)
    {
        return item.Type switch
        {
            CommunicationInstanceType.Socket客户端 => $"socket-client:{item.Name}",
            CommunicationInstanceType.Socket服务端 => $"socket-server:{item.Name}",
            CommunicationInstanceType.串口 => $"serial:{item.Name}",
            _ => item.Name
        };
    }

    private CommunicationInstanceConfig? FindByInstanceKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        string normalized = key.Trim();
        return _instances.FirstOrDefault(x =>
            string.Equals(BuildInstanceKey(x), normalized, StringComparison.OrdinalIgnoreCase)
            || string.Equals(x.Name, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private void DisconnectPersistentTaskSocket(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        if (_taskPersistentSockets.TryRemove(key, out SocketClientCommunicationService? c))
        {
            try
            {
                c.Disconnect();
                c.Dispose();
            }
            catch
            {
                // ignore
            }
        }

        _taskPersistentEndpoints.TryRemove(key, out _);
        if (_taskPersistentGates.TryRemove(key, out SemaphoreSlim? gate))
        {
            try
            {
                gate.Dispose();
            }
            catch
            {
                // ignore
            }
        }
    }

    private void DisconnectAllPersistentTaskSockets()
    {
        foreach (string k in _taskPersistentSockets.Keys.ToArray())
            DisconnectPersistentTaskSocket(k);
    }

    private void PruneStalePersistentTaskSockets()
    {
        foreach (string key in _taskPersistentSockets.Keys.ToArray())
        {
            CommunicationInstanceConfig? cfg = FindByInstanceKey(key);
            if (cfg is null || cfg.Type != CommunicationInstanceType.Socket客户端 || !cfg.PersistentConnection)
            {
                DisconnectPersistentTaskSocket(key);
                continue;
            }

            if (!_taskPersistentEndpoints.TryGetValue(key, out (string Host, int Port) ep)
                || !string.Equals(ep.Host, cfg.Host, StringComparison.OrdinalIgnoreCase)
                || ep.Port != cfg.Port)
            {
                DisconnectPersistentTaskSocket(key);
            }
        }
    }

    private async Task<T> RunWithTaskSocketClientAsync<T>(
        CommunicationInstanceConfig cfg,
        Func<SocketClientCommunicationService, Task<T>> action)
    {
        if (!cfg.PersistentConnection)
        {
            var ephemeral = new SocketClientCommunicationService();
            try
            {
                await ephemeral.ConnectAsync(cfg.Host, cfg.Port, 2500).ConfigureAwait(false);
                return await action(ephemeral).ConfigureAwait(false);
            }
            finally
            {
                ephemeral.Disconnect();
                ephemeral.Dispose();
            }
        }

        string key = BuildInstanceKey(cfg);
        SemaphoreSlim gate = _taskPersistentGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_taskPersistentSockets.TryGetValue(key, out SocketClientCommunicationService? client))
            {
                bool endpointOk = _taskPersistentEndpoints.TryGetValue(key, out (string Host, int Port) ep)
                    && string.Equals(ep.Host, cfg.Host, StringComparison.OrdinalIgnoreCase)
                    && ep.Port == cfg.Port;
                if (client.IsConnected && endpointOk)
                    return await action(client).ConfigureAwait(false);

                try
                {
                    client.Disconnect();
                    client.Dispose();
                }
                catch
                {
                    // ignore
                }

                _taskPersistentSockets.TryRemove(key, out _);
                _taskPersistentEndpoints.TryRemove(key, out _);
            }

            var created = new SocketClientCommunicationService();
            await created.ConnectAsync(cfg.Host, cfg.Port, 2500).ConfigureAwait(false);
            _taskPersistentSockets[key] = created;
            _taskPersistentEndpoints[key] = (cfg.Host, cfg.Port);
            return await action(created).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private void BuildUi()
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "实例名称", FillWeight = 18 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "类型", FillWeight = 14 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Host", FillWeight = 16 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "端口", FillWeight = 10 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "串口", FillWeight = 12 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "波特率", FillWeight = 10 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Socket回包/分隔", FillWeight = 20 });

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 0, 0, 4)
        };
        toolbar.Controls.AddRange([_btnEditAuth, _btnAdd, _btnEdit, _btnDelete, _btnStartServer, _btnStopServer, _btnRefreshClients, _lblServerStatus]);

        var monitorSplit = new SplitContainer
        {
            Dock = DockStyle.Bottom,
            Height = 220,
            SplitterDistance = 320
        };

        monitorSplit.Panel1.Controls.Add(_lstClients);
        monitorSplit.Panel2.Controls.Add(_txtServerReceiveLog);

        var lblClients = new Label { Text = "已连接客户端", Dock = DockStyle.Top, Height = 24, TextAlign = ContentAlignment.MiddleLeft };
        var lblReceive = new Label { Text = "接收数据", Dock = DockStyle.Top, Height = 24, TextAlign = ContentAlignment.MiddleLeft };
        monitorSplit.Panel1.Controls.Add(lblClients);
        monitorSplit.Panel2.Controls.Add(lblReceive);

        Controls.Add(_grid);
        Controls.Add(monitorSplit);
        Controls.Add(toolbar);
    }

    private void BindEvents()
    {
        _btnEditAuth.Click += (_, _) => ToggleEditAuth();
        _btnAdd.Click += (_, _) => AddInstance();
        _btnEdit.Click += (_, _) => EditSelectedInstance();
        _btnDelete.Click += (_, _) => DeleteSelectedInstance();
        _btnStartServer.Click += async (_, _) => await StartSelectedServerAsync();
        _btnStopServer.Click += (_, _) => StopActiveServer();
        _btnRefreshClients.Click += (_, _) => RefreshClientList();
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && _editUnlocked)
                EditSelectedInstance();
        };
    }

    private CommunicationInstanceConfig? GetSelectedInstance()
    {
        int index = GetSelectedIndex();
        if (index < 0 || index >= _instances.Count)
            return null;

        return _instances[index];
    }

    private async Task StartSelectedServerAsync()
    {
        var selected = GetSelectedInstance();
        if (selected is null)
        {
            MessageBox.Show(this, "请先选择一个通讯实例。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (selected.Type != CommunicationInstanceType.Socket服务端)
        {
            MessageBox.Show(this, "仅Socket服务端实例支持启动监听。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_activeSocketServer is not null)
            StopActiveServer();

        try
        {
            _activeSocketServer = new SocketServerCommunicationService();
            _activeSocketServer.ClientConnected += HandleServerClientConnected;
            _activeSocketServer.ClientDisconnected += HandleServerClientDisconnected;
            _activeSocketServer.DataReceived += HandleServerDataReceived;
            await _activeSocketServer.StartAsync(selected.Port);

            _activeServerName = selected.Name;
            _lblServerStatus.Text = $"服务端状态: 监听中 [{selected.Name}] 端口:{selected.Port}";
            SetPermission(_canModify);
            AppendReceiveLog($"服务端实例[{selected.Name}] 已启动监听，端口:{selected.Port}");
        }
        catch (Exception ex)
        {
            StopActiveServer();
            MessageBox.Show(this, $"启动监听失败: {ex.Message}", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void StopActiveServer()
    {
        if (_activeSocketServer is null)
            return;

        _activeSocketServer.ClientConnected -= HandleServerClientConnected;
        _activeSocketServer.ClientDisconnected -= HandleServerClientDisconnected;
        _activeSocketServer.DataReceived -= HandleServerDataReceived;
        _activeSocketServer.Stop();
        _activeSocketServer.Dispose();
        _activeSocketServer = null;

        _lblServerStatus.Text = "服务端状态: 未启动";
        SetPermission(_canModify);
        _activeServerName = null;
        _lstClients.Items.Clear();
    }

    private void HandleServerClientConnected(string endpoint)
    {
        if (IsDisposed)
            return;

        BeginInvoke(() =>
        {
            RefreshClientList();
            AppendReceiveLog($"客户端已连接: {endpoint}");
        });
    }

    private void HandleServerClientDisconnected(string endpoint)
    {
        if (IsDisposed)
            return;

        BeginInvoke(() =>
        {
            RefreshClientList();
            AppendReceiveLog($"客户端已断开: {endpoint}");
        });
    }

    private void HandleServerDataReceived(string endpoint, string data)
    {
        if (IsDisposed)
            return;

        BeginInvoke(() =>
        {
            AppendReceiveLog($"[{endpoint}] -> {data}");
        });
    }

    private void RefreshClientList()
    {
        _lstClients.Items.Clear();
        if (_activeSocketServer is null)
            return;

        foreach (var endpoint in _activeSocketServer.GetClientEndpoints())
            _lstClients.Items.Add(endpoint);
    }

    private void AppendReceiveLog(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        _txtServerReceiveLog.AppendText(line);
        LogAction?.Invoke(message);
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

    private void AddInstance()
    {
        var created = ShowInstanceDialog();
        if (created is null)
            return;

        _instances.Add(created);
        SaveConfig();
        BindGrid();
    }

    private void EditSelectedInstance()
    {
        int index = GetSelectedIndex();
        if (index < 0)
            return;

        CommunicationInstanceConfig? edited = ShowInstanceDialog(_instances[index]);
        if (edited is null)
            return;

        if (!ReferenceEquals(edited, _instances[index]))
            _instances[index] = edited;

        SaveConfig();
        BindGrid();
    }

    private void DeleteSelectedInstance()
    {
        int index = GetSelectedIndex();
        if (index < 0)
            return;

        CommunicationInstanceConfig removed = _instances[index];
        if (removed.Type == CommunicationInstanceType.Socket客户端)
            DisconnectPersistentTaskSocket(BuildInstanceKey(removed));

        _instances.RemoveAt(index);
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
        foreach (var item in _instances)
        {
            string socketCol = item.Type == CommunicationInstanceType.Socket客户端
                ? $"{(item.PersistentConnection ? "长连 " : string.Empty)}{(item.WaitForResponse ? "等回包" : "不等")} {item.ReceiveTimeoutMs}ms 「{item.ResponseValueDelimiter}」"
                : string.Empty;
            _grid.Rows.Add(
                item.Name,
                item.Type,
                item.Type is CommunicationInstanceType.Socket客户端 or CommunicationInstanceType.Socket服务端 ? item.Host : string.Empty,
                item.Type is CommunicationInstanceType.Socket客户端 or CommunicationInstanceType.Socket服务端 ? item.Port : 0,
                item.Type == CommunicationInstanceType.串口 ? item.ComPort : string.Empty,
                item.Type == CommunicationInstanceType.串口 ? item.BaudRate : 0,
                socketCol);
        }
    }

    private static void CopyCommunicationConfig(CommunicationInstanceConfig src, CommunicationInstanceConfig dst)
    {
        dst.Name = src.Name;
        dst.Type = src.Type;
        dst.Host = src.Host;
        dst.Port = src.Port;
        dst.ComPort = src.ComPort;
        dst.BaudRate = src.BaudRate;
        dst.WaitForResponse = src.WaitForResponse;
        dst.ReceiveTimeoutMs = src.ReceiveTimeoutMs;
        dst.ResponseValueDelimiter = src.ResponseValueDelimiter;
        dst.SocketResultFields = (src.SocketResultFields ?? [])
            .Select(f => new SocketClientResultField { Name = f.Name, Index = f.Index })
            .ToList();
        dst.PersistentConnection = src.PersistentConnection;
    }

    private CommunicationInstanceConfig SnapshotCommunicationConfig(CommunicationInstanceConfig src)
    {
        var c = new CommunicationInstanceConfig();
        CopyCommunicationConfig(src, c);
        return c;
    }

    private CommunicationInstanceConfig? ShowInstanceDialog(CommunicationInstanceConfig? existing = null)
    {
        CommunicationInstanceConfig? cancelSnapshot = existing is not null ? SnapshotCommunicationConfig(existing) : null;

        using var dialog = new Form
        {
            Text = existing is null ? "新增通讯实例" : "编辑通讯实例",
            Width = 960,
            Height = 720,
            AutoScroll = true,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 10,
            Padding = new Padding(10)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int ri = 0; ri < 9; ri++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        // 最后一行占满窗体剩余高度，避免标记表格被压成 0 高只剩灰底
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var txtName = new TextBox { Dock = DockStyle.Fill };
        var cmbType = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        cmbType.Items.AddRange(Enum.GetValues<CommunicationInstanceType>().Cast<object>().ToArray());
        cmbType.SelectedIndex = 0;

        var txtHost = new TextBox { Dock = DockStyle.Fill, Text = "127.0.0.1" };
        var numPort = new NumericUpDown { Dock = DockStyle.Left, Width = 120, Minimum = 1, Maximum = 65535, Value = 9000 };
        var txtComPort = new TextBox { Dock = DockStyle.Fill, Text = "COM1" };
        var numBaud = new NumericUpDown { Dock = DockStyle.Left, Width = 120, Minimum = 110, Maximum = 921600, Value = 9600, Increment = 10 };
        var txtTestPayload = new TextBox { Dock = DockStyle.Fill, Text = "PING" };
        var chkWaitResponse = new CheckBox { Text = "等待返回数据(同连接)", AutoSize = true };
        var chkPersistent = new CheckBox { Text = "长连接(任务复用TCP，发送后不关闭)", AutoSize = true };
        var numTimeoutMs = new NumericUpDown { Width = 88, Minimum = 100, Maximum = 120000, Value = 3000, Increment = 100 };
        var lblTimeout = new Label { Text = "超时(ms)", AutoSize = true, Padding = new Padding(6, 6, 0, 0) };
        var btnConnectSocket = new Button { Text = "连接", Width = 72 };
        var btnTest = new Button { Text = "测试", Width = 72 };
        var txtDelimiter = new TextBox { Dock = DockStyle.Fill, Text = "," };
        var gridMarkers = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            RowHeadersVisible = false,
            ColumnHeadersVisible = true,
            EnableHeadersVisualStyles = true,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.Fixed3D,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ScrollBars = ScrollBars.Vertical,
            MinimumSize = new Size(100, 120)
        };
        gridMarkers.Columns.Add(new DataGridViewTextBoxColumn { Name = "MkName", HeaderText = "结果标记名", MinimumWidth = 140 });
        gridMarkers.Columns.Add(new DataGridViewTextBoxColumn { Name = "MkIdx", HeaderText = "分片序号(0起)", FillWeight = 40 });

        // Rows.Clear / Rows.Add 会触发 RowsAdded、CellValueChanged，若在重填表格前执行 AutoPersist 会把标记保存成空列表。
        var suppressMarkerAutoPersist = false;
        // 点「确定」关窗瞬间从表格读取（避免 ShowDialog 返回后网格已清空）；若未捕获则回退 ReadMarkersFromGrid。
        List<SocketClientResultField>? markersOkSnapshot = null;

        List<SocketClientResultField> ReadMarkersFromGrid()
        {
            gridMarkers.EndEdit();
            // 底部「新行」在提交前 IsNewRow 仍为 true，但单元格已有值；不能跳过，否则点「确定」读不到刚填的标记。
            var list = new List<SocketClientResultField>();
            foreach (DataGridViewRow row in gridMarkers.Rows)
            {
                string n = row.Cells["MkName"].Value?.ToString()?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(n))
                    continue;

                int idx = 0;
                _ = int.TryParse(row.Cells["MkIdx"].Value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out idx);
                list.Add(new SocketClientResultField { Name = n, Index = idx });
            }

            return list;
        }

        void BindMarkersToGrid(IReadOnlyList<SocketClientResultField>? fields)
        {
            suppressMarkerAutoPersist = true;
            try
            {
                gridMarkers.Rows.Clear();
                foreach (SocketClientResultField f in fields ?? [])
                {
                    if (string.IsNullOrWhiteSpace(f.Name))
                        continue;
                    int r = gridMarkers.Rows.Add();
                    gridMarkers.Rows[r].Cells["MkName"].Value = f.Name;
                    gridMarkers.Rows[r].Cells["MkIdx"].Value = f.Index.ToString(CultureInfo.InvariantCulture);
                }
            }
            finally
            {
                suppressMarkerAutoPersist = false;
            }
        }

        void AutoPersistSocketClientFields()
        {
            if (suppressMarkerAutoPersist)
                return;

            if (existing is null || cmbType.SelectedItem is not CommunicationInstanceType.Socket客户端)
                return;

            if (existing.PersistentConnection && !chkPersistent.Checked)
                DisconnectPersistentTaskSocket(BuildInstanceKey(existing));

            existing.WaitForResponse = chkWaitResponse.Checked;
            existing.PersistentConnection = chkPersistent.Checked;
            existing.ReceiveTimeoutMs = (int)numTimeoutMs.Value;
            existing.ResponseValueDelimiter = string.IsNullOrWhiteSpace(txtDelimiter.Text) ? "," : txtDelimiter.Text.Trim();
            existing.SocketResultFields = ReadMarkersFromGrid();
            SaveConfig();
        }

        void ApplyAllFieldsFromUi(CommunicationInstanceConfig c)
        {
            gridMarkers.EndEdit();
            c.Name = txtName.Text.Trim();
            c.Type = cmbType.SelectedItem is CommunicationInstanceType ct ? ct : CommunicationInstanceType.Socket客户端;
            c.Host = txtHost.Text.Trim();
            c.Port = (int)numPort.Value;
            c.ComPort = txtComPort.Text.Trim();
            c.BaudRate = (int)numBaud.Value;
            if (c.Type == CommunicationInstanceType.Socket客户端)
            {
                c.WaitForResponse = chkWaitResponse.Checked;
                c.PersistentConnection = chkPersistent.Checked;
                c.ReceiveTimeoutMs = (int)numTimeoutMs.Value;
                c.ResponseValueDelimiter = string.IsNullOrWhiteSpace(txtDelimiter.Text) ? "," : txtDelimiter.Text.Trim();
                // 关窗后 DataGridView 可能已不可用；优先使用 FormClosing 里抓的快照
                c.SocketResultFields = markersOkSnapshot is not null
                    ? markersOkSnapshot.Select(x => new SocketClientResultField { Name = x.Name, Index = x.Index }).ToList()
                    : ReadMarkersFromGrid();
                markersOkSnapshot = null;
            }
        }

        layout.Controls.Add(new Label { Text = "实例名称", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(txtName, 1, 0);
        layout.Controls.Add(new Label { Text = "类型", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(cmbType, 1, 1);
        layout.Controls.Add(new Label { Text = "Host", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        layout.Controls.Add(txtHost, 1, 2);
        layout.Controls.Add(new Label { Text = "端口", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        layout.Controls.Add(numPort, 1, 3);
        layout.Controls.Add(new Label { Text = "串口", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 4);
        layout.Controls.Add(txtComPort, 1, 4);
        layout.Controls.Add(new Label { Text = "波特率", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 5);
        layout.Controls.Add(numBaud, 1, 5);
        layout.Controls.Add(new Label { Text = "测试报文", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 6);
        layout.Controls.Add(txtTestPayload, 1, 6);
        layout.Controls.Add(new Label { Text = "", AutoSize = true }, 0, 7);
        var testPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 2, 0, 2)
        };
        testPanel.Controls.Add(chkWaitResponse);
        testPanel.Controls.Add(chkPersistent);
        testPanel.Controls.Add(lblTimeout);
        testPanel.Controls.Add(numTimeoutMs);
        testPanel.Controls.Add(btnConnectSocket);
        testPanel.Controls.Add(btnTest);
        layout.Controls.Add(testPanel, 1, 7);
        layout.Controls.Add(new Label { Text = "回包数值分隔符", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 8);
        layout.Controls.Add(txtDelimiter, 1, 8);
        var lblMarkers = new Label
        {
            Text = "结果字段标记（任务里按标记名取分片；如 123,456 且分隔符为逗号，序号0→123）",
            AutoSize = true,
            MaximumSize = new Size(900, 0),
            Padding = new Padding(0, 0, 0, 4)
        };
        var markerLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = Padding.Empty,
            Margin = Padding.Empty
        };
        markerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        markerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        markerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        markerLayout.Controls.Add(lblMarkers, 0, 0);
        markerLayout.Controls.Add(gridMarkers, 0, 1);
        layout.Controls.Add(markerLayout, 0, 9);
        layout.SetColumnSpan(markerLayout, 2);

        void UpdateFieldsState()
        {
            bool isSocket = cmbType.SelectedItem is CommunicationInstanceType.Socket客户端 or CommunicationInstanceType.Socket服务端;
            bool isSocketClient = cmbType.SelectedItem is CommunicationInstanceType.Socket客户端;
            txtHost.Enabled = isSocket;
            numPort.Enabled = isSocket;
            txtComPort.Enabled = !isSocket;
            numBaud.Enabled = !isSocket;
            chkWaitResponse.Enabled = isSocketClient;
            chkPersistent.Enabled = isSocketClient;
            numTimeoutMs.Enabled = isSocketClient;
            btnConnectSocket.Enabled = isSocketClient;
            txtDelimiter.Enabled = isSocketClient;
            gridMarkers.Enabled = isSocketClient;
            lblMarkers.Enabled = isSocketClient;
            markerLayout.Enabled = isSocketClient;
        }

        string GetConnectionKey() => txtName.Text.Trim();

        async Task ConnectOrDisconnectSocketAsync()
        {
            if (cmbType.SelectedItem is not CommunicationInstanceType.Socket客户端)
                return;

            string key = GetConnectionKey();
            if (string.IsNullOrWhiteSpace(key))
            {
                MessageBox.Show(dialog, "请先填写实例名称。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (_testSocketClients.TryGetValue(key, out SocketClientCommunicationService? connectedClient))
            {
                connectedClient.Disconnect();
                connectedClient.Dispose();
                _testSocketClients.Remove(key);
                btnConnectSocket.Text = "连接";
                MessageBox.Show(dialog, "Socket客户端已断开。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(txtHost.Text.Trim()))
                throw new ArgumentException("Host不能为空。");

            var client = new SocketClientCommunicationService();
            await client.ConnectAsync(txtHost.Text.Trim(), (int)numPort.Value, 2500);
            _testSocketClients[key] = client;
            btnConnectSocket.Text = "断开";
            MessageBox.Show(dialog, "Socket客户端连接成功。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        cmbType.SelectedIndexChanged += (_, _) => UpdateFieldsState();

        async Task TestCommunicationAsync()
        {
            string payload = txtTestPayload.Text;
            if (string.IsNullOrWhiteSpace(payload))
                payload = "PING";

            var cfg = new CommunicationInstanceConfig
            {
                Name = txtName.Text.Trim(),
                Type = cmbType.SelectedItem is CommunicationInstanceType type ? type : CommunicationInstanceType.Socket客户端,
                Host = txtHost.Text.Trim(),
                Port = (int)numPort.Value,
                ComPort = txtComPort.Text.Trim(),
                BaudRate = (int)numBaud.Value
            };

            try
            {
                btnTest.Enabled = false;
                switch (cfg.Type)
                {
                    case CommunicationInstanceType.Socket客户端:
                        {
                            string key = GetConnectionKey();
                            if (string.IsNullOrWhiteSpace(key))
                                throw new ArgumentException("实例名称不能为空。请先填写并连接。");

                            if (!_testSocketClients.TryGetValue(key, out SocketClientCommunicationService? client))
                                throw new InvalidOperationException("请先点击【连接】建立Socket连接后再测试发送。");

                            string message = payload;
                            if (!message.EndsWith("\n", StringComparison.Ordinal))
                                message += "\r\n";

                            await client.SendAsync(message);

                            string response = string.Empty;
                            if (chkWaitResponse.Checked)
                            {
                                try
                                {
                                    response = await client.ReceiveAsync(timeoutMs: (int)numTimeoutMs.Value);
                                }
                                catch
                                {
                                    // 超时允许继续
                                }
                            }

                            string extra = string.Empty;
                            if (!string.IsNullOrWhiteSpace(response))
                            {
                                string d = string.IsNullOrWhiteSpace(txtDelimiter.Text) ? "," : txtDelimiter.Text.Trim();
                                string[] parts = SocketResponseParser.SplitValues(response.TrimEnd('\r', '\n'), d);
                                extra = "\n" + SocketResponseParser.FormatPartsForLog(parts);
                            }

                            MessageBox.Show(dialog,
                                string.IsNullOrWhiteSpace(response)
                                    ? (chkWaitResponse.Checked
                                        ? "Socket客户端测试成功：已连接并发送报文，等待回包超时或无数据。"
                                        : "Socket客户端测试成功：已连接并发送报文。")
                                    : $"Socket客户端测试成功：已连接并发送报文。\n回包：{response}{extra}",
                                "测试结果", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            break;
                        }
                    case CommunicationInstanceType.Socket服务端:
                        {
                            using var server = new SocketServerCommunicationService();
                            using var cts = new CancellationTokenSource();
                            await server.StartAsync(cfg.Port, cts.Token);
                            server.Stop();
                            MessageBox.Show(dialog, "Socket服务端测试成功：端口可正常监听。", "测试结果", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            break;
                        }
                    case CommunicationInstanceType.串口:
                        {
                            using var serial = new SerialCommunicationService();
                            serial.Open(cfg.ComPort, cfg.BaudRate);
                            serial.Send(payload);
                            serial.Close();
                            MessageBox.Show(dialog, "串口测试成功：串口已打开并发送报文。", "测试结果", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            break;
                        }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(dialog, $"通讯测试失败：{ex.Message}", "测试结果", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                btnTest.Enabled = true;
            }
        }

        btnConnectSocket.Click += async (_, _) =>
        {
            try
            {
                btnConnectSocket.Enabled = false;
                await ConnectOrDisconnectSocketAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(dialog, $"Socket连接操作失败：{ex.Message}", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                btnConnectSocket.Enabled = cmbType.SelectedItem is CommunicationInstanceType.Socket客户端;
            }
        };

        btnTest.Click += async (_, _) => await TestCommunicationAsync();

        // 必须先填好标记表格再订阅自动保存：否则加载 existing 时改「等待回包」等会先触发 AutoPersist，在空表格上读标记并 SaveConfig 清空文件。
        suppressMarkerAutoPersist = true;
        try
        {
            if (existing is not null)
            {
                txtName.Text = existing.Name;
                cmbType.SelectedItem = existing.Type;
                txtHost.Text = existing.Host;
                numPort.Value = Math.Clamp(existing.Port, numPort.Minimum, numPort.Maximum);
                txtComPort.Text = existing.ComPort;
                numBaud.Value = Math.Clamp(existing.BaudRate, numBaud.Minimum, numBaud.Maximum);
                chkWaitResponse.Checked = existing.WaitForResponse;
                chkPersistent.Checked = existing.PersistentConnection;
                numTimeoutMs.Value = Math.Clamp(existing.ReceiveTimeoutMs, numTimeoutMs.Minimum, numTimeoutMs.Maximum);
                txtDelimiter.Text = string.IsNullOrWhiteSpace(existing.ResponseValueDelimiter) ? "," : existing.ResponseValueDelimiter;
                existing.SocketResultFields ??= [];
                BindMarkersToGrid(existing.SocketResultFields);
                if (existing.Type == CommunicationInstanceType.Socket客户端
                    && _testSocketClients.TryGetValue(existing.Name, out _))
                {
                    btnConnectSocket.Text = "断开";
                }
            }
        }
        finally
        {
            suppressMarkerAutoPersist = false;
        }

        chkWaitResponse.CheckedChanged += (_, _) => AutoPersistSocketClientFields();
        chkPersistent.CheckedChanged += (_, _) => AutoPersistSocketClientFields();
        numTimeoutMs.ValueChanged += (_, _) => AutoPersistSocketClientFields();
        txtDelimiter.TextChanged += (_, _) => AutoPersistSocketClientFields();
        gridMarkers.CellValueChanged += (_, _) => AutoPersistSocketClientFields();
        gridMarkers.RowsAdded += (_, _) => AutoPersistSocketClientFields();
        gridMarkers.UserDeletedRow += (_, _) => AutoPersistSocketClientFields();

        UpdateFieldsState();

        var panelBtn = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var btnOk = new Button { Text = "确定", Width = 72, DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "取消", Width = 72, DialogResult = DialogResult.Cancel };
        panelBtn.Controls.Add(btnOk);
        panelBtn.Controls.Add(btnCancel);

        dialog.Controls.Add(panelBtn);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = btnOk;
        dialog.CancelButton = btnCancel;

        dialog.FormClosing += (_, e) =>
        {
            if (dialog.DialogResult != DialogResult.OK)
                return;

            if (string.IsNullOrWhiteSpace(txtName.Text.Trim()))
            {
                MessageBox.Show(dialog, "实例名称不能为空。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                e.Cancel = true;
                return;
            }

            if (cmbType.SelectedItem is CommunicationInstanceType.Socket客户端)
            {
                gridMarkers.EndEdit();
                markersOkSnapshot = ReadMarkersFromGrid();
            }
        };

        DialogResult dr = dialog.ShowDialog(this);
        if (dr != DialogResult.OK)
        {
            if (cancelSnapshot is not null && existing is not null)
            {
                CopyCommunicationConfig(cancelSnapshot, existing);
                SaveConfig();
            }

            return null;
        }

        if (existing is not null)
        {
            ApplyAllFieldsFromUi(existing);
            SaveConfig();
            return existing;
        }

        var created = new CommunicationInstanceConfig();
        ApplyAllFieldsFromUi(created);
        return created;
    }

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(_configPath))
                return;

            var json = File.ReadAllText(_configPath);
            var list = JsonSerializer.Deserialize<List<CommunicationInstanceConfig>>(json, JsonFileOptions);
            if (list is null)
                return;

            DisconnectAllPersistentTaskSockets();
            _instances.Clear();
            _instances.AddRange(list);
            BindGrid();
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"加载通讯配置失败: {ex.Message}");
        }
    }

    private void SaveConfig()
    {
        try
        {
            PruneStalePersistentTaskSockets();
            var json = JsonSerializer.Serialize(_instances, JsonFileOptions);
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"保存通讯配置失败: {ex.Message}");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopActiveServer();
            foreach (var kv in _testSocketClients)
            {
                kv.Value.Disconnect();
                kv.Value.Dispose();
            }

            _testSocketClients.Clear();
            DisconnectAllPersistentTaskSockets();
        }

        base.Dispose(disposing);
    }
}
