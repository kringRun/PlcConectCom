using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PlcConectCom.WinFormsTest;

/// <summary>条件比较（与任务页逻辑一致，数值使用不变文化解析）。</summary>
public static class TaskConditionEvaluation
{
    public static bool Evaluate(TriggerConditionConfig condition, string current)
    {
        string expected = condition.CompareValue?.Trim() ?? string.Empty;
        string comparator = condition.Comparator?.Trim() ?? "==";

        if (double.TryParse(current, NumberStyles.Float, CultureInfo.InvariantCulture, out double currentNum)
            && double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out double expectedNum))
        {
            return comparator switch
            {
                "==" => Math.Abs(currentNum - expectedNum) < 0.000001,
                "!=" => Math.Abs(currentNum - expectedNum) >= 0.000001,
                ">" => currentNum > expectedNum,
                ">=" => currentNum >= expectedNum,
                "<" => currentNum < expectedNum,
                "<=" => currentNum <= expectedNum,
                _ => false
            };
        }

        int compare = string.Compare(current, expected, StringComparison.OrdinalIgnoreCase);
        return comparator switch
        {
            "==" => compare == 0,
            "!=" => compare != 0,
            ">" => compare > 0,
            ">=" => compare >= 0,
            "<" => compare < 0,
            "<=" => compare <= 0,
            _ => false
        };
    }
}

/// <summary>从 PLC 读取条件当前值（字符串形式供比较）。</summary>
public static class TaskConditionPlcReader
{
    private const string SocketConditionAddressPrefix = "socket-marker:";

    public static async Task<string> ReadCurrentValueAsync(
        global::PlcConectCom.PlcCommunicator plc,
        TriggerConditionConfig c,
        DateTime pipelineStartedUtc,
        Func<string, string, string?>? socketMarkerValueResolver = null)
    {
        if (c.SourceType == TriggerConditionSourceType.延时参数)
        {
            double ms = (DateTime.UtcNow - pipelineStartedUtc).TotalMilliseconds;
            return ms.ToString("F0", CultureInfo.InvariantCulture);
        }

        if (c.SourceType == TriggerConditionSourceType.Socket分片标记)
        {
            if (TryParseSocketMarkerAddress(c.SourceAddress, out string instanceKey, out string markerName))
                return socketMarkerValueResolver?.Invoke(instanceKey, markerName) ?? string.Empty;
            return string.Empty;
        }

        string addr = c.SourceAddress?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(addr))
            return string.Empty;

        string readAs = c.ConditionReadValueType?.Trim().ToUpperInvariant() ?? "AUTO";

        try
        {
            if (readAs is "BOOL" or "AUTO" && (addr.StartsWith("MX", StringComparison.OrdinalIgnoreCase)
                                                 || addr.StartsWith("MB", StringComparison.OrdinalIgnoreCase)))
            {
                bool b = await plc.ReadBoolAsync(addr);
                return b ? "true" : "false";
            }

            if (readAs == "AUTO")
                return (await plc.ReadDoubleAsync(addr)).ToString("G9", CultureInfo.InvariantCulture);

            return readAs switch
            {
                "BOOL" => (await plc.ReadBoolAsync(addr)).ToString(CultureInfo.InvariantCulture),
                "INT" => (await plc.ReadIntAsync(addr)).ToString(CultureInfo.InvariantCulture),
                "DWORD" or "UINT" or "UDINT" => (await plc.ReadDWordAsync(addr)).ToString(CultureInfo.InvariantCulture),
                "REAL" or "DOUBLE" => (await plc.ReadDoubleAsync(addr)).ToString("G9", CultureInfo.InvariantCulture),
                "STRING" => await plc.ReadStringAsync(addr, 128),
                _ => (await plc.ReadDoubleAsync(addr)).ToString("G9", CultureInfo.InvariantCulture)
            };
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool TryParseSocketMarkerAddress(string? sourceAddress, out string instanceKey, out string markerName)
    {
        instanceKey = string.Empty;
        markerName = string.Empty;
        string raw = sourceAddress?.Trim() ?? string.Empty;
        if (!raw.StartsWith(SocketConditionAddressPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        string body = raw[SocketConditionAddressPrefix.Length..];
        int sep = body.IndexOf("::", StringComparison.Ordinal);
        if (sep <= 0 || sep >= body.Length - 2)
            return false;

        instanceKey = body[..sep].Trim();
        markerName = body[(sep + 2)..].Trim();
        return !string.IsNullOrWhiteSpace(instanceKey) && !string.IsNullOrWhiteSpace(markerName);
    }
}

/// <summary>单步执行返回值：StatusMessage 供界面/日志；StoreValue 写入单路「结果存储地址」；SocketSliceMap 供多路分片写入（键见 <see cref="SocketResponseParser.FullPayloadMapKey"/>）。</summary>
public sealed record TaskStepExecuteResult(
    string StatusMessage,
    string StoreValue,
    IReadOnlyDictionary<string, string>? SocketSliceMap = null);

/// <summary>将执行结果字符串写入 PLC 全局地址。</summary>
public static class TaskResultStorageWriter
{
    public static async Task WriteAsync(global::PlcConectCom.PlcCommunicator plc, string address, string valueType, string payload)
    {
        string addr = address?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(addr))
            return;

        string t = valueType?.Trim().ToUpperInvariant() ?? "STRING";
        switch (t)
        {
            case "BOOL":
                if (bool.TryParse(payload, out bool b)
                    || (payload is "1" or "0" && bool.TryParse(payload == "1" ? "true" : "false", out b)))
                    await plc.WriteBoolAsync(addr, b);
                break;
            case "INT":
                if (short.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out short si))
                    await plc.WriteIntAsync(addr, si);
                break;
            case "DWORD":
            case "UINT":
            case "UDINT":
                if (uint.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint ui))
                    await plc.WriteDWordAsync(addr, ui);
                break;
            case "REAL":
            case "DOUBLE":
                if (double.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    await plc.WriteDoubleAsync(addr, d);
                break;
            default:
                await plc.WriteStringAsync(addr, payload ?? string.Empty);
                break;
        }
    }
}

/// <summary>后台轮询或报警事件触发的串行任务管线。</summary>
public sealed class TaskPipelineRunner : IDisposable
{
    private readonly ConcurrentDictionary<string, string> _latestSocketMarkerValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<global::PlcConectCom.PlcCommunicator?> _plcProvider;
    private readonly Func<TaskExecutionConfig, Task<TaskStepExecuteResult>> _executeActionAsync;
    private readonly Func<IReadOnlyList<AutomatedTaskConfig>> _getTasks;
    private readonly Action<string>? _log;
    private readonly Action<string, AutomatedTaskRunnerState, string?>? _stateChanged;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _ctsByTask = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _eventArmed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AutomatedTaskRunnerState> _stateByTask = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string?> _detailByTask = new(StringComparer.Ordinal);

    public TaskPipelineRunner(
        Func<global::PlcConectCom.PlcCommunicator?> plcProvider,
        Func<TaskExecutionConfig, Task<TaskStepExecuteResult>> executeActionAsync,
        Func<IReadOnlyList<AutomatedTaskConfig>> getTasks,
        Action<string>? log,
        Action<string, AutomatedTaskRunnerState, string?>? stateChanged)
    {
        _plcProvider = plcProvider;
        _executeActionAsync = executeActionAsync;
        _getTasks = getTasks;
        _log = log;
        _stateChanged = stateChanged;
    }

    public AutomatedTaskRunnerState GetState(string taskId) =>
        _stateByTask.TryGetValue(taskId, out var s) ? s : AutomatedTaskRunnerState.已停止;

    public string? GetStateDetail(string taskId) =>
        _detailByTask.TryGetValue(taskId, out var d) ? d : null;

    public void StartTask(string taskId)
    {
        AutomatedTaskConfig? cfg = _getTasks().FirstOrDefault(t => string.Equals(t.Id, taskId, StringComparison.Ordinal));
        if (cfg is null || !cfg.Enabled)
        {
            _log?.Invoke($"任务启动忽略：未找到或未启用 [{taskId}]");
            return;
        }

        if (cfg.RunMode == AutomatedTaskRunMode.报警事件触发)
        {
            _eventArmed[taskId] = 1;
            SetState(taskId, AutomatedTaskRunnerState.运行中, "事件待命：报警匹配后执行一轮管线");
            _log?.Invoke($"任务[{cfg.TaskName}] 报警事件模式已启动（待命）。");
            return;
        }

        if (_ctsByTask.ContainsKey(taskId))
            return;

        var cts = new CancellationTokenSource();
        if (!_ctsByTask.TryAdd(taskId, cts))
            return;

        SetState(taskId, AutomatedTaskRunnerState.运行中, null);
        _ = Task.Run(() => PollingLoopAsync(taskId, cts.Token), cts.Token);
    }

    public void StopTask(string taskId)
    {
        _eventArmed.TryRemove(taskId, out _);

        if (_ctsByTask.TryRemove(taskId, out CancellationTokenSource? cts))
        {
            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch
            {
                // ignore
            }
        }

        SetState(taskId, AutomatedTaskRunnerState.已停止, null);
    }

    public void StopAll()
    {
        foreach (string id in _ctsByTask.Keys.ToArray())
            StopTask(id);
    }

    /// <summary>报警事件：对处于事件模式且过滤器匹配的任务执行一轮管线。</summary>
    public void OnAlarmTriggered(string alarmCode, string alarmName)
    {
        foreach (AutomatedTaskConfig task in _getTasks())
        {
            if (!task.Enabled || task.RunMode != AutomatedTaskRunMode.报警事件触发)
                continue;

            if (!_eventArmed.ContainsKey(task.Id))
                continue;

            string filter = task.AlarmTriggerFilter?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(filter))
            {
                bool codeHit = alarmCode.Contains(filter, StringComparison.OrdinalIgnoreCase)
                               || string.Equals(alarmCode, filter, StringComparison.OrdinalIgnoreCase);
                bool nameHit = alarmName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(alarmName, filter, StringComparison.OrdinalIgnoreCase);
                if (!codeHit && !nameHit)
                    continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    SetState(task.Id, AutomatedTaskRunnerState.运行中, "事件执行中…");
                    await RunPipelineOnceAsync(task, CancellationToken.None).ConfigureAwait(false);
                    SetState(task.Id, AutomatedTaskRunnerState.运行中, "事件待命：执行完成，等待下次报警");
                }
                catch (Exception ex)
                {
                    SetState(task.Id, AutomatedTaskRunnerState.错误, ex.Message);
                    _log?.Invoke($"任务[{task.TaskName}] 报警触发执行失败: {ex.Message}");
                }
            });
        }
    }

    private async Task PollingLoopAsync(string taskId, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                AutomatedTaskConfig? cfg = _getTasks().FirstOrDefault(t => string.Equals(t.Id, taskId, StringComparison.Ordinal));
                if (cfg is null || !cfg.Enabled || cfg.RunMode != AutomatedTaskRunMode.后台轮询)
                    break;

                var plc = _plcProvider();
                if (plc is not { IsConnected: true })
                {
                    SetState(taskId, AutomatedTaskRunnerState.错误, "PLC 未连接");
                    await Task.Delay(Math.Clamp(cfg.PollingIntervalMs, 200, 60_000), ct).ConfigureAwait(false);
                    continue;
                }

                SetState(taskId, AutomatedTaskRunnerState.运行中, null);
                try
                {
                    await RunPipelineOnceAsync(cfg, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    SetState(taskId, AutomatedTaskRunnerState.错误, ex.Message);
                    _log?.Invoke($"任务[{cfg.TaskName}] 轮询管线异常: {ex.Message}");
                }

                int delay = Math.Clamp(cfg.PollingIntervalMs, 50, 60_000);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (_ctsByTask.TryRemove(taskId, out CancellationTokenSource? cts))
            {
                try
                {
                    cts.Dispose();
                }
                catch
                {
                    // ignore
                }
            }

            SetState(taskId, AutomatedTaskRunnerState.已停止, null);
        }
    }

    private async Task RunPipelineOnceAsync(AutomatedTaskConfig task, CancellationToken ct)
    {
        var plc = _plcProvider();
        if (plc is not { IsConnected: true })
            throw new InvalidOperationException("PLC 未连接");

        if (task.Steps.Count == 0)
            return;

        DateTime pipelineStart = DateTime.UtcNow;
        var sb = new StringBuilder();

        for (int i = 0; i < task.Steps.Count; i++)
        {
            TaskPipelineStepConfig step = task.Steps[i];
            ct.ThrowIfCancellationRequested();

            string current = await TaskConditionPlcReader.ReadCurrentValueAsync(
                    plc,
                    step.Condition,
                    pipelineStart,
                    (instanceKey, markerName) => TryGetLatestSocketMarkerValue(instanceKey, markerName))
                .ConfigureAwait(false);
            if (!TaskConditionEvaluation.Evaluate(step.Condition, current))
            {
                sb.Append("步").Append(i + 1).Append("条件未满足;");
                SetState(task.Id, AutomatedTaskRunnerState.运行中, sb.ToString());
                return;
            }

            TaskStepExecuteResult exec = await _executeActionAsync(step.Execution).ConfigureAwait(false);

            bool isSocketStep = step.Execution.ExecutionType is TaskExecutionType.Socket通讯发送数据
                or TaskExecutionType.Socket通讯等待数据;
            if (isSocketStep && exec.SocketSliceMap is not null)
                UpdateLatestSocketMarkerValues(step.Execution.Target, exec.SocketSliceMap);
            List<TaskSocketResultSink>? sinks = step.SocketResultSinks;
            if (isSocketStep && sinks is { Count: > 0 } && exec.SocketSliceMap is not null)
            {
                foreach (TaskSocketResultSink sink in sinks)
                {
                    string addr = sink.StorageAddress?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(addr))
                        continue;

                    string sliceKey = sink.SliceName?.Trim() ?? string.Empty;
                    string payload = string.IsNullOrWhiteSpace(sliceKey)
                        ? (exec.SocketSliceMap.TryGetValue(SocketResponseParser.FullPayloadMapKey, out string? full)
                            ? full
                            : string.Empty)
                        : (exec.SocketSliceMap.TryGetValue(sliceKey, out string? part) ? part : string.Empty);

                    await TaskResultStorageWriter.WriteAsync(plc, addr, sink.ValueType, payload).ConfigureAwait(false);
                }
            }
            else if (!string.IsNullOrWhiteSpace(step.ResultStorageAddress))
            {
                await TaskResultStorageWriter.WriteAsync(plc, step.ResultStorageAddress, step.ResultStorageValueType, exec.StoreValue)
                    .ConfigureAwait(false);
            }

            sb.Append("步").Append(i + 1).Append("完成;");
            SetState(task.Id, AutomatedTaskRunnerState.运行中, sb.ToString());
        }
    }

    private void SetState(string taskId, AutomatedTaskRunnerState state, string? detail)
    {
        _stateByTask[taskId] = state;
        if (detail is not null)
            _detailByTask[taskId] = detail;
        else if (state == AutomatedTaskRunnerState.已停止)
            _detailByTask.TryRemove(taskId, out _);

        _stateChanged?.Invoke(taskId, state, detail);
    }

    private string? TryGetLatestSocketMarkerValue(string instanceKey, string markerName)
    {
        string key = BuildSocketMarkerCacheKey(instanceKey, markerName);
        return _latestSocketMarkerValues.TryGetValue(key, out string? value) ? value : null;
    }

    private void UpdateLatestSocketMarkerValues(string? instanceKeyOrName, IReadOnlyDictionary<string, string> map)
    {
        string instanceKey = instanceKeyOrName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(instanceKey) || map.Count == 0)
            return;

        foreach (var kv in map)
        {
            string marker = kv.Key?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(marker)
                || string.Equals(marker, SocketResponseParser.FullPayloadMapKey, StringComparison.OrdinalIgnoreCase))
                continue;

            _latestSocketMarkerValues[BuildSocketMarkerCacheKey(instanceKey, marker)] = kv.Value ?? string.Empty;
        }
    }

    /// <summary>外部数据源（如 Socket服务端收包分片）更新指定实例的标记最新值，供「Socket分片标记」触发条件取值。</summary>
    public void UpdateSocketMarkerValues(string? instanceKeyOrName, IReadOnlyDictionary<string, string> map)
        => UpdateLatestSocketMarkerValues(instanceKeyOrName, map);

    private static string BuildSocketMarkerCacheKey(string instanceKey, string markerName) =>
        $"{instanceKey.Trim()}::{markerName.Trim()}";

    public void Dispose()
    {
        StopAll();
    }
}
