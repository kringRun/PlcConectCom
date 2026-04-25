using System.ComponentModel;
using Sunny.UI;

namespace PlcConectCom.WinFormsTest;

/// <summary>PLC BOOL 开关控件：切换后写入并回读校验，失败自动回滚。</summary>
public sealed class PlcBoolSwitchControl : UserControl
{
    private readonly UILabel _lblTitle = new()
    {
        Dock = DockStyle.Left,
        Width = 96,
        TextAlign = ContentAlignment.MiddleLeft,
        Text = "开关"
    };

    private readonly UISwitch _switch = new()
    {
        Dock = DockStyle.Fill
    };

    private bool _isInternalUpdating;
    private bool _isApplying;
    private bool _lastStableState;

    [Browsable(true)]
    [Category("Appearance")]
    [DefaultValue("开关")]
    public string Title
    {
        get => _lblTitle.Text;
        set => _lblTitle.Text = string.IsNullOrWhiteSpace(value) ? "开关" : value.Trim();
    }

    /// <summary>仅支持 BOOL 地址（如 MB100 / MX0.0）。</summary>
    public string PlcAddress { get; set; } = string.Empty;

    public Func<global::PlcConectCom.PlcCommunicator?>? PlcProvider { get; set; }

    public global::PlcConectCom.PlcCommunicator? PlcInstance { get; set; }

    public Action<string>? LogAction { get; set; }

    /// <summary>启用后按下开关会在延时后自动写回目标值。</summary>
    [Browsable(true)]
    [Category("Behavior")]
    [DefaultValue(false)]
    public bool AutoReset { get; set; }

    /// <summary>自复位延时（毫秒），仅 <see cref="AutoReset"/> 为 true 时生效。</summary>
    [Browsable(true)]
    [Category("Behavior")]
    [DefaultValue(100)]
    public int AutoResetDelayMs { get; set; } = 100;

    /// <summary>自复位目标值，仅 <see cref="AutoReset"/> 为 true 时生效。</summary>
    [Browsable(true)]
    [Category("Behavior")]
    [DefaultValue(false)]
    public bool AutoResetTargetValue { get; set; }

    /// <summary>切换并经 PLC 校验成功后触发。</summary>
    public event Action<bool>? SwitchConfirmed;

    public PlcBoolSwitchControl()
    {
        Height = 34;
        BuildUi();
        BindEvents();
    }

    public async Task RefreshStateAsync()
    {
        if (_isApplying)
            return;

        try
        {
            var plc = GetConnectedPlcOrNull();
            if (plc is null)
            {
                SetSwitchEnabled(false);
                return;
            }

            bool state = await plc.ReadBoolAsync(PlcAddress.Trim());
            _lastStableState = state;
            SetSwitchState(state);
            SetSwitchEnabled(true);
        }
        catch (Exception ex)
        {
            SetSwitchEnabled(false);
            LogAction?.Invoke($"PLC开关读取失败 [{PlcAddress}]: {ex.Message}");
        }
    }

    private void BuildUi()
    {
        var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0) };
        root.Controls.Add(_switch);
        root.Controls.Add(_lblTitle);
        Controls.Add(root);
    }

    private void BindEvents()
    {
        _switch.ValueChanged += async (_, _) => await HandleSwitchChangedAsync();
    }

    private async Task HandleSwitchChangedAsync()
    {
        if (_isInternalUpdating || _isApplying)
            return;

        var plc = GetConnectedPlcOrNull();
        if (plc is null)
        {
            RevertToLastStableState("PLC未连接，已恢复开关状态。");
            return;
        }

        string addr = PlcAddress.Trim();
        if (string.IsNullOrWhiteSpace(addr))
        {
            RevertToLastStableState("开关未绑定PLC地址，已恢复开关状态。");
            return;
        }

        bool target = _switch.Active;
        _isApplying = true;
        try
        {
            if (AutoReset)
            {
                int delayMs = Math.Clamp(AutoResetDelayMs, 10, 60_000);
                await plc.WriteBoolAsync(addr, target);
                await Task.Delay(delayMs);
                await plc.WriteBoolAsync(addr, AutoResetTargetValue);

                bool resetVerify = await plc.ReadBoolAsync(addr);
                if (resetVerify != AutoResetTargetValue)
                {
                    RevertToLastStableState($"PLC开关自复位校验失败 [{addr}]，目标={AutoResetTargetValue}，回读={resetVerify}，已恢复状态。");
                    return;
                }

                _lastStableState = resetVerify;
                SetSwitchState(resetVerify);
                SwitchConfirmed?.Invoke(resetVerify);
                return;
            }

            await plc.WriteBoolAsync(addr, target);
            bool verify = await plc.ReadBoolAsync(addr);
            if (verify != target)
            {
                RevertToLastStableState($"PLC开关写入校验失败 [{addr}]，目标={target}，回读={verify}，已恢复状态。");
                return;
            }

            _lastStableState = verify;
            SetSwitchState(verify);
            SwitchConfirmed?.Invoke(verify);
        }
        catch (Exception ex)
        {
            RevertToLastStableState($"PLC开关写入失败 [{addr}]: {ex.Message}");
        }
        finally
        {
            _isApplying = false;
        }
    }

    private void RevertToLastStableState(string message)
    {
        SetSwitchState(_lastStableState);
        LogAction?.Invoke(message);
    }

    private void SetSwitchState(bool state)
    {
        _isInternalUpdating = true;
        try
        {
            _switch.Active = state;
        }
        finally
        {
            _isInternalUpdating = false;
        }
    }

    private void SetSwitchEnabled(bool enabled)
    {
        _switch.Enabled = enabled;
        _lblTitle.ForeColor = enabled ? SystemColors.ControlText : SystemColors.GrayText;
    }

    private global::PlcConectCom.PlcCommunicator? GetConnectedPlcOrNull()
    {
        var plc = PlcProvider?.Invoke() ?? PlcInstance;
        if (plc is null || !plc.IsConnected)
            return null;
        return plc;
    }
}

