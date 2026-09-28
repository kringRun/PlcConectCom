namespace PlcConectCom.WinFormsTest;

/// <summary>
/// MES / SCADA 模块的宿主侧配置：把 <see cref="MesScada.MesSignal"/> 等逻辑信号映射到本机的 PLC 地址。
/// 落在 <c>AppContext.BaseDirectory\MesScada.config.json</c>；地址留空的信号按 false 处理。
/// </summary>
public sealed class MesScadaConfig
{
    /// <summary>MesScada.MesSignal 名称 → PLC 地址（MX100.0 / MB200 / MW300 / MD400 制式）。</summary>
    public Dictionary<string, string> SignalAddresses { get; set; } = BuildDefaultSignalAddresses();

    /// <summary>直通（过站）模式信号地址；留空表示恒为非直通。</summary>
    public string PassModeAddress { get; set; } = string.Empty;

    /// <summary>焊接工站「运行中」地址。</summary>
    public string StationRunningAddress { get; set; } = string.Empty;

    /// <summary>焊接工站「报警」地址。</summary>
    public string StationAlarmAddress { get; set; } = string.Empty;

    /// <summary>焊接工站「急停」地址。</summary>
    public string StationEstopAddress { get; set; } = string.Empty;

    /// <summary>PLC 轮询周期（毫秒）。MES 状态机只读内存快照，这里决定快照的刷新频率。</summary>
    public int PollIntervalMs { get; set; } = 200;

    /// <summary>Modbus TCP 从站监听端口（SCADA 界面「开启」按钮用）。</summary>
    public int ModbusServerPort { get; set; } = 10001;

    /// <summary>主程序启动时是否自动开启 Modbus TCP 从站。</summary>
    public bool AutoStartModbusServer { get; set; }

    /// <summary>生成一份「键齐全、值为空」的默认映射，便于上线前逐个填地址。</summary>
    public static Dictionary<string, string> BuildDefaultSignalAddresses()
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        foreach (string name in Enum.GetNames<MesScada.MesSignal>())
            map[name] = string.Empty;

        return map;
    }

    /// <summary>取某个信号配置的地址；未配置时返回空串。</summary>
    public string AddressOf(MesScada.MesSignal signal) =>
        SignalAddresses.TryGetValue(signal.ToString(), out string? address)
            ? address ?? string.Empty
            : string.Empty;
}
