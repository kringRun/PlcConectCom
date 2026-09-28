using System.Net;
using System.Net.Sockets;
using NModbus;
using NModbus.Data;

namespace PlcConectCom.WinFormsTest;

/// <summary>Modbus TCP 服务端的数据来源模式。</summary>
public enum ModbusServerDataMode
{
    /// <summary>HMI 自己维护一块内存地址区，外部可读写，不联动 PLC。</summary>
    本地地址区,

    /// <summary>外部读写经 HMI 转发到当前已连接 PLC（读=读 PLC，写=写 PLC）。</summary>
    桥接PLC
}

/// <summary>
/// Modbus TCP 服务端实例：对外提供标准 Modbus TCP（FC01/02/03/04/05/06/15/16），
/// 内部地址区统一按「保持寄存器 + 线圈位」存储，可在本地地址区 / 桥接 PLC 两种模式下工作。
/// 地址换算见 <see cref="ModbusServerAddressMap"/>（MW 与寄存器 1:1，MX 线性位序）。
/// </summary>
public sealed class ModbusTcpServerService : IDisposable
{
    private readonly ModbusServerDataMode _mode;
    private readonly byte _unitId;
    private readonly Func<global::PlcConectCom.PlcCommunicator?>? _plcProvider;
    private readonly Action<string>? _log;
    private readonly object _sync = new();
    private readonly ushort[] _holding;
    private readonly bool[] _coils;

    private TcpListener? _listener;
    private IModbusSlaveNetwork? _network;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public ModbusTcpServerService(
        ModbusServerDataMode mode,
        byte unitId,
        int registerCount,
        Func<global::PlcConectCom.PlcCommunicator?>? plcProvider,
        Action<string>? log)
    {
        _mode = mode;
        _unitId = unitId;
        _plcProvider = plcProvider;
        _log = log;
        int registers = Math.Clamp(registerCount, 1, 65536);
        _holding = new ushort[registers];
        _coils = new bool[registers * 16];
    }

    public ModbusServerDataMode Mode => _mode;

    public int RegisterCount => _holding.Length;

    public bool IsRunning => _network is not null;

    public int ConnectedClientCount => (_network as IModbusTcpSlaveNetwork)?.Masters.Count ?? 0;

    /// <summary>启动监听（NModbus 的 CreateSlaveNetwork 会自行 Start 监听器）。</summary>
    public void Start(int port)
    {
        if (_disposed || _network is not null)
            return;

        var holding = _mode == ModbusServerDataMode.桥接PLC
            ? (IPointSource<ushort>)new PlcBridgeHoldingSource(this)
            : new LocalHoldingSource(this);
        var coils = _mode == ModbusServerDataMode.桥接PLC
            ? (IPointSource<bool>)new PlcBridgeCoilSource(this)
            : new LocalCoilSource(this);

        var store = new ServerDataStore(holding, coils);
        var factory = new ModbusFactory();
        _listener = new TcpListener(IPAddress.Any, port);
        _network = factory.CreateSlaveNetwork(_listener);
        _network.AddSlave(factory.CreateSlave(_unitId, store));
        _cts = new CancellationTokenSource();
        _ = _network.ListenAsync(_cts.Token);
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignore
        }

        try
        {
            _network?.Dispose();
        }
        catch
        {
            // ignore
        }

        try
        {
            _listener?.Stop();
        }
        catch
        {
            // ignore
        }

        _cts?.Dispose();
        _cts = null;
        _network = null;
        _listener = null;
    }

    // —— 地址区查看/写入（界面线程调用）——

    public async Task<ushort[]> ReadRegistersAsync(int start, int count)
    {
        int from = Math.Clamp(start, 0, _holding.Length - 1);
        int len = Math.Clamp(count, 1, _holding.Length - from);
        if (_mode != ModbusServerDataMode.桥接PLC)
            return PeekRegisters(from, len);

        var plc = _plcProvider?.Invoke();
        if (plc is not { IsConnected: true })
            return PeekRegisters(from, len);

        try
        {
            // 单次 Modbus 读上限 125 个寄存器，超出时分批
            var result = new ushort[len];
            for (int offset = 0; offset < len; offset += 125)
            {
                int batch = Math.Min(125, len - offset);
                short[] values = await plc.ReadIntArrayAsync(
                    ModbusServerAddressMap.RegisterToMw(from + offset), batch).ConfigureAwait(true);
                for (int i = 0; i < batch && i < values.Length; i++)
                    result[offset + i] = (ushort)values[i];
            }

            CacheRegisters(from, result);
            return result;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Modbus服务端读取 PLC 寄存器失败 MW{from}: {ex.Message}");
            return PeekRegisters(from, len);
        }
    }

    public async Task<bool[]> ReadCoilsAsync(int start, int count)
    {
        int from = Math.Clamp(start, 0, _coils.Length - 1);
        int len = Math.Clamp(count, 1, _coils.Length - from);
        if (_mode != ModbusServerDataMode.桥接PLC)
            return PeekCoils(from, len);

        var plc = _plcProvider?.Invoke();
        if (plc is not { IsConnected: true })
            return PeekCoils(from, len);

        try
        {
            var result = new bool[len];
            for (int offset = 0; offset < len; offset += 1000)
            {
                int batch = Math.Min(1000, len - offset);
                bool[] values = await plc.ReadBoolArrayAsync(
                    ModbusServerAddressMap.BitToMxStart(from + offset), batch).ConfigureAwait(true);
                for (int i = 0; i < batch && i < values.Length; i++)
                    result[offset + i] = values[i];
            }

            CacheCoils(from, result);
            return result;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Modbus服务端读取 PLC 位失败 {ModbusServerAddressMap.BitToMx(from)}: {ex.Message}");
            return PeekCoils(from, len);
        }
    }

    public async Task<bool> WriteRegistersAsync(int start, ushort[] values)
    {
        if (values is null || values.Length == 0)
            return false;

        int from = Math.Clamp(start, 0, _holding.Length - 1);
        int len = Math.Min(values.Length, _holding.Length - from);
        CacheRegisters(from, values.Take(len).ToArray());
        if (_mode != ModbusServerDataMode.桥接PLC)
            return true;

        var plc = _plcProvider?.Invoke();
        if (plc is not { IsConnected: true })
            return false;

        try
        {
            for (int offset = 0; offset < len; offset += 125)
            {
                int batch = Math.Min(125, len - offset);
                short[] batchValues = values.Skip(offset).Take(batch).Select(v => (short)v).ToArray();
                await plc.WriteIntArrayAsync(
                    ModbusServerAddressMap.RegisterToMw(from + offset), batchValues).ConfigureAwait(true);
            }

            return true;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Modbus服务端写入 PLC 寄存器失败 MW{from}: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> WriteCoilsAsync(int start, bool[] values)
    {
        if (values is null || values.Length == 0)
            return false;

        int from = Math.Clamp(start, 0, _coils.Length - 1);
        int len = Math.Min(values.Length, _coils.Length - from);
        CacheCoils(from, values.Take(len).ToArray());
        if (_mode != ModbusServerDataMode.桥接PLC)
            return true;

        var plc = _plcProvider?.Invoke();
        if (plc is not { IsConnected: true })
            return false;

        try
        {
            for (int offset = 0; offset < len; offset += 1000)
            {
                int batch = Math.Min(1000, len - offset);
                await plc.WriteBoolArrayAsync(
                    ModbusServerAddressMap.BitToMxStart(from + offset),
                    values.Skip(offset).Take(batch).ToArray()).ConfigureAwait(true);
            }

            return true;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Modbus服务端写入 PLC 位失败 {ModbusServerAddressMap.BitToMx(from)}: {ex.Message}");
            return false;
        }
    }

    public ushort[] PeekRegisters(int start, int count)
    {
        int from = Math.Clamp(start, 0, _holding.Length - 1);
        int len = Math.Clamp(count, 1, _holding.Length - from);
        var result = new ushort[len];
        lock (_sync)
            Array.Copy(_holding, from, result, 0, len);
        return result;
    }

    public bool[] PeekCoils(int start, int count)
    {
        int from = Math.Clamp(start, 0, _coils.Length - 1);
        int len = Math.Clamp(count, 1, _coils.Length - from);
        var result = new bool[len];
        lock (_sync)
            Array.Copy(_coils, from, result, 0, len);
        return result;
    }

    private void CacheRegisters(int start, ushort[] values)
    {
        lock (_sync)
        {
            for (int i = 0; i < values.Length && start + i < _holding.Length; i++)
                _holding[start + i] = values[i];
        }
    }

    private void CacheCoils(int start, bool[] values)
    {
        lock (_sync)
        {
            for (int i = 0; i < values.Length && start + i < _coils.Length; i++)
                _coils[start + i] = values[i];
        }
    }

    // —— NModbus 数据源（服务端线程调用，同步接口）——

    private sealed class LocalHoldingSource : IPointSource<ushort>
    {
        private readonly ModbusTcpServerService _owner;
        public LocalHoldingSource(ModbusTcpServerService owner) => _owner = owner;

        public ushort[] ReadPoints(ushort startAddress, ushort numberOfPoints)
            => _owner.PeekRegisters(startAddress, numberOfPoints);

        public void WritePoints(ushort startAddress, ushort[] points)
            => _owner.CacheRegisters(startAddress, points);
    }

    private sealed class LocalCoilSource : IPointSource<bool>
    {
        private readonly ModbusTcpServerService _owner;
        public LocalCoilSource(ModbusTcpServerService owner) => _owner = owner;

        public bool[] ReadPoints(ushort startAddress, ushort numberOfPoints)
            => _owner.PeekCoils(startAddress, numberOfPoints);

        public void WritePoints(ushort startAddress, bool[] points)
            => _owner.CacheCoils(startAddress, points);
    }

    private sealed class PlcBridgeHoldingSource : IPointSource<ushort>
    {
        private readonly ModbusTcpServerService _owner;
        public PlcBridgeHoldingSource(ModbusTcpServerService owner) => _owner = owner;

        public ushort[] ReadPoints(ushort startAddress, ushort numberOfPoints)
        {
            var plc = _owner._plcProvider?.Invoke();
            if (plc is not { IsConnected: true })
                return _owner.PeekRegisters(startAddress, numberOfPoints);

            try
            {
                int count = Math.Clamp((int)numberOfPoints, 1, 125);
                short[] values = plc.ReadIntArrayAsync(
                    ModbusServerAddressMap.RegisterToMw(startAddress), count).GetAwaiter().GetResult();
                var result = new ushort[count];
                for (int i = 0; i < count && i < values.Length; i++)
                    result[i] = (ushort)values[i];

                _owner.CacheRegisters(startAddress, result);
                return result;
            }
            catch (Exception ex)
            {
                _owner._log?.Invoke($"Modbus服务端(桥接)读 PLC MW{startAddress} 失败: {ex.Message}");
                return _owner.PeekRegisters(startAddress, numberOfPoints);
            }
        }

        public void WritePoints(ushort startAddress, ushort[] points)
        {
            _owner.CacheRegisters(startAddress, points);

            var plc = _owner._plcProvider?.Invoke();
            if (plc is not { IsConnected: true })
                return;

            try
            {
                for (int offset = 0; offset < points.Length; offset += 125)
                {
                    int batch = Math.Min(125, points.Length - offset);
                    awaitableWrite(plc, (ushort)(startAddress + offset), points.Skip(offset).Take(batch).ToArray());
                }
            }
            catch (Exception ex)
            {
                _owner._log?.Invoke($"Modbus服务端(桥接)写 PLC MW{startAddress} 失败: {ex.Message}");
            }

            static void awaitableWrite(global::PlcConectCom.PlcCommunicator plc, ushort address, ushort[] values)
                => plc.WriteIntArrayAsync(
                    ModbusServerAddressMap.RegisterToMw(address),
                    values.Select(v => (short)v).ToArray()).GetAwaiter().GetResult();
        }
    }

    private sealed class PlcBridgeCoilSource : IPointSource<bool>
    {
        private readonly ModbusTcpServerService _owner;
        public PlcBridgeCoilSource(ModbusTcpServerService owner) => _owner = owner;

        public bool[] ReadPoints(ushort startAddress, ushort numberOfPoints)
        {
            var plc = _owner._plcProvider?.Invoke();
            if (plc is not { IsConnected: true })
                return _owner.PeekCoils(startAddress, numberOfPoints);

            try
            {
                int count = Math.Clamp((int)numberOfPoints, 1, 2000);
                bool[] values = plc.ReadBoolArrayAsync(
                    ModbusServerAddressMap.BitToMxStart(startAddress), count).GetAwaiter().GetResult();
                _owner.CacheCoils(startAddress, values);
                return values;
            }
            catch (Exception ex)
            {
                _owner._log?.Invoke($"Modbus服务端(桥接)读 PLC {ModbusServerAddressMap.BitToMx(startAddress)} 失败: {ex.Message}");
                return _owner.PeekCoils(startAddress, numberOfPoints);
            }
        }

        public void WritePoints(ushort startAddress, bool[] points)
        {
            _owner.CacheCoils(startAddress, points);

            var plc = _owner._plcProvider?.Invoke();
            if (plc is not { IsConnected: true })
                return;

            try
            {
                for (int offset = 0; offset < points.Length; offset += 1000)
                {
                    int batch = Math.Min(1000, points.Length - offset);
                    plc.WriteBoolArrayAsync(
                        ModbusServerAddressMap.BitToMxStart(startAddress + offset),
                        points.Skip(offset).Take(batch).ToArray()).GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                _owner._log?.Invoke($"Modbus服务端(桥接)写 PLC {ModbusServerAddressMap.BitToMx(startAddress)} 失败: {ex.Message}");
            }
        }
    }

    private sealed class ServerDataStore : ISlaveDataStore
    {
        public IPointSource<bool> CoilDiscretes { get; }
        public IPointSource<bool> CoilInputs { get; }
        public IPointSource<ushort> HoldingRegisters { get; }
        public IPointSource<ushort> InputRegisters { get; }

        public ServerDataStore(IPointSource<ushort> holding, IPointSource<bool> coils)
        {
            HoldingRegisters = holding;
            InputRegisters = holding;
            CoilDiscretes = coils;
            CoilInputs = coils;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();
    }
}
