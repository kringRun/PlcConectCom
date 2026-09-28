using MesScada;

namespace PlcConectCom.WinFormsTest;

/// <summary>
/// <see cref="IMesDeviceContext"/> 的宿主实现：用主程序的 <see cref="PlcCommunicator"/>
/// 按 <see cref="MesScadaConfig"/> 周期性刷新一份内存快照，再交给 MES 状态机读取。
/// <para>
/// 之所以用「后台轮询 + 内存快照」而不是每次直读 PLC：MesController 的主循环是 5ms 一轮，
/// 每轮会读十几个信号，直连 Modbus 会把主站打满。快照刷新后由
/// <see cref="UpdateDiEdges"/> 在每轮末尾推导上升/下降沿，与 XCore 的 VRDI 语义一致。
/// </para>
/// </summary>
public sealed class MesDeviceContext : IMesDeviceContext, IMesMeterSource
{
    private static readonly MesSignal[] AllSignals = Enum.GetValues<MesSignal>();

    private readonly Func<PlcCommunicator?> _plcProvider;
    private readonly Action<string>? _logger;
    private readonly Action<MesScadaConfig>? _persist;
    private readonly object _sync = new();

    private readonly bool[] _raw = new bool[AllSignals.Length];
    private readonly bool[] _sts = new bool[AllSignals.Length];
    private readonly bool[] _pls = new bool[AllSignals.Length];
    private readonly bool[] _plf = new bool[AllSignals.Length];
    private readonly double[] _meter = new double[22];
    private readonly double[] _energy = new double[30];

    private bool _processSignal;
    private bool _passMode;
    private MesStationState _stationState = MesStationState.NONE;

    public MesDeviceContext(
        Func<PlcCommunicator?> plcProvider,
        MesScadaConfig config,
        Action<string>? logger = null,
        Action<MesScadaConfig>? persist = null)
    {
        _plcProvider = plcProvider ?? throw new ArgumentNullException(nameof(plcProvider));
        Config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
        _persist = persist;
    }

    /// <summary>当前生效的配置（可在运行中改地址后继续用同一实例）。</summary>
    public MesScadaConfig Config { get; }

    /// <summary>当前登录用户名，由主程序在角色切换时同步。</summary>
    public string LoginUserName { get; set; } = string.Empty;

    #region IMesDeviceContext

    public bool ReadDi(MesSignal signal)
    {
        lock (_sync)
            return _sts[(int)signal];
    }

    public bool ReadDiPulse(MesSignal signal)
    {
        lock (_sync)
            return _pls[(int)signal];
    }

    /// <summary>读下降沿（XCore 的 VRDI.PLF）。</summary>
    public bool ReadDiFallingEdge(MesSignal signal)
    {
        lock (_sync)
            return _plf[(int)signal];
    }

    public void ClearDiPulse(MesSignal signal)
    {
        lock (_sync)
            _pls[(int)signal] = false;
    }

    /// <summary>
    /// 写虚拟 DI。维护状态按钮（切线/维修/保养/点检/换料/停机/调试）由 MES 界面直接置位，
    /// 属软件信号，只改内存，不回写 PLC。
    /// </summary>
    public void WriteDi(MesSignal signal, bool value)
    {
        lock (_sync)
            _raw[(int)signal] = value;
    }

    /// <summary>每轮末尾由 MesController 调用：从快照推导上升/下降沿。</summary>
    public void UpdateDiEdges()
    {
        lock (_sync)
        {
            for (int i = 0; i < _raw.Length; i++)
            {
                _pls[i] = _raw[i] && !_sts[i];
                _plf[i] = !_raw[i] && _sts[i];
                _sts[i] = _raw[i];
            }

            if (_pls[(int)MesSignal.产品加工动作])
                _processSignal = true;
        }
    }

    public MesStationState GetStationState(string stationKey)
    {
        lock (_sync)
            return _stationState;
    }

    public bool IsPassMode
    {
        get
        {
            lock (_sync)
                return _passMode;
        }
    }

    public bool TakeProcessSignal()
    {
        lock (_sync)
        {
            if (!_processSignal)
                return false;

            _processSignal = false;
            return true;
        }
    }

    public void WriteLog(string message)
    {
        _logger?.Invoke(message);
        MesScadaLog.Append(message);
    }

    #endregion

    #region IMesMeterSource

    public double ReadMeterValue(int index) =>
        index >= 0 && index < _meter.Length ? _meter[index] : 0;

    public double ReadEnergyValue(int index) =>
        index >= 0 && index < _energy.Length ? _energy[index] : 0;

    /// <summary>由主程序喂入电表寄存器原始值（下标 0..21）。</summary>
    public void SetMeterValues(IReadOnlyList<double> values)
    {
        lock (_sync)
        {
            for (int i = 0; i < _meter.Length && i < values.Count; i++)
                _meter[i] = values[i];
        }
    }

    /// <summary>由主程序喂入能耗寄存器原始值（下标 0..29）。</summary>
    public void SetEnergyValues(IReadOnlyList<double> values)
    {
        lock (_sync)
        {
            for (int i = 0; i < _energy.Length && i < values.Count; i++)
                _energy[i] = values[i];
        }
    }

    #endregion

    /// <summary>
    /// 后台轮询 PLC，刷新内存快照。异常时保留上一轮值（fail-safe），不中断 MES 状态机。
    /// </summary>
    public async Task RunPollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync().ConfigureAwait(false);
            }
            catch
            {
                // 读失败时保持上次状态，与主程序其余心跳逻辑一致
            }

            try
            {
                // 每轮重新取周期，配置页改轮询周期能即时生效
                await Task.Delay(Math.Max(20, Config.PollIntervalMs), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollOnceAsync()
    {
        PlcCommunicator? plc = _plcProvider();
        if (plc is null || !plc.IsConnected)
            return;

        foreach (MesSignal signal in AllSignals)
        {
            string address = Config.AddressOf(signal);
            if (string.IsNullOrWhiteSpace(address))
                continue;

            bool value = await plc.ReadBoolAsync(address).ConfigureAwait(false);
            lock (_sync)
                _raw[(int)signal] = value;
        }

        if (!string.IsNullOrWhiteSpace(Config.PassModeAddress))
        {
            bool pass = await plc.ReadBoolAsync(Config.PassModeAddress).ConfigureAwait(false);
            lock (_sync)
                _passMode = pass;
        }

        MesStationState state = await ReadStationStateAsync(plc).ConfigureAwait(false);
        lock (_sync)
            _stationState = state;
    }

    private async Task<MesStationState> ReadStationStateAsync(PlcCommunicator plc)
    {
        if (!string.IsNullOrWhiteSpace(Config.StationEstopAddress)
            && await plc.ReadBoolAsync(Config.StationEstopAddress).ConfigureAwait(false))
            return MesStationState.ESTOP;

        if (!string.IsNullOrWhiteSpace(Config.StationAlarmAddress)
            && await plc.ReadBoolAsync(Config.StationAlarmAddress).ConfigureAwait(false))
            return MesStationState.ALARM;

        if (!string.IsNullOrWhiteSpace(Config.StationRunningAddress)
            && await plc.ReadBoolAsync(Config.StationRunningAddress).ConfigureAwait(false))
            return MesStationState.RUNNING;

        // 未配置工站地址时，用「设备自动运行中」信号兜底，避免维护状态切换一直被判为不可操作
        string autoRun = Config.AddressOf(MesSignal.设备自动运行中);
        if (!string.IsNullOrWhiteSpace(autoRun) && await plc.ReadBoolAsync(autoRun).ConfigureAwait(false))
            return MesStationState.RUNNING;

        return MesStationState.STOP;
    }

    #region IMesAddressConfigStore（MES 配置分页的读写入口）

    /// <summary>取当前配置快照（配置分页的「重载」用）。</summary>
    public MesAddressConfigSnapshot Load()
    {
        return new MesAddressConfigSnapshot
        {
            SignalAddresses = new Dictionary<string, string>(Config.SignalAddresses, StringComparer.Ordinal),
            PassModeAddress = Config.PassModeAddress,
            StationRunningAddress = Config.StationRunningAddress,
            StationAlarmAddress = Config.StationAlarmAddress,
            StationEstopAddress = Config.StationEstopAddress,
            PollIntervalMs = Config.PollIntervalMs,
            ModbusServerPort = Config.ModbusServerPort,
            AutoStartModbusServer = Config.AutoStartModbusServer
        };
    }

    /// <summary>
    /// 校验并保存配置。保存成功后原地更新运行中的 <see cref="Config"/>：
    /// 信号地址 / 工站地址 / 轮询周期立即生效（轮询循环每轮重新读取），
    /// Modbus 端口需在 SCADA 页重新开启从站后生效；持久化通过构造时注入的 <c>persist</c> 回调落盘。
    /// </summary>
    /// <returns>null 表示成功；否则返回错误描述。</returns>
    public string? Save(MesAddressConfigSnapshot snapshot)
    {
        if (snapshot is null)
            return "配置为空，无法保存。";

        // 兜底校验（配置页已校验过，这里防止其它入口绕过）
        foreach (KeyValuePair<string, string> kv in snapshot.SignalAddresses)
        {
            string? error = MesAddressFormat.GetError(kv.Key, kv.Value ?? string.Empty);
            if (error is not null)
                return error;
        }

        foreach ((string name, string address) in new[]
        {
            ("直通(过站)模式", snapshot.PassModeAddress),
            ("工站运行中", snapshot.StationRunningAddress),
            ("工站报警", snapshot.StationAlarmAddress),
            ("工站急停", snapshot.StationEstopAddress)
        })
        {
            string? error = MesAddressFormat.GetError(name, address);
            if (error is not null)
                return error;
        }

        Config.SignalAddresses = new Dictionary<string, string>(snapshot.SignalAddresses, StringComparer.Ordinal);
        Config.PassModeAddress = snapshot.PassModeAddress?.Trim() ?? string.Empty;
        Config.StationRunningAddress = snapshot.StationRunningAddress?.Trim() ?? string.Empty;
        Config.StationAlarmAddress = snapshot.StationAlarmAddress?.Trim() ?? string.Empty;
        Config.StationEstopAddress = snapshot.StationEstopAddress?.Trim() ?? string.Empty;
        Config.PollIntervalMs = Math.Clamp(snapshot.PollIntervalMs, 20, 60000);
        Config.ModbusServerPort = Math.Clamp(snapshot.ModbusServerPort, 1, 65535);
        Config.AutoStartModbusServer = snapshot.AutoStartModbusServer;

        // 端口同步给从站；已在运行的从站不会自动换端口，需重新开启
        MesController.Instacne.ModbusServerPort = Config.ModbusServerPort;

        try
        {
            _persist?.Invoke(Config);
        }
        catch (Exception ex)
        {
            return $"配置已生效，但写入配置文件失败：{ex.Message}";
        }

        return null;
    }

    #endregion
}
