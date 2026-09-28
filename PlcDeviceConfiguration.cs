using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlcConectCom.WinFormsTest;

public sealed class PlcDeviceProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "PLC 设备";
    public string IpAddress { get; set; } = "192.168.1.88";
    public int Port { get; set; } = 502;
    public int SlaveId { get; set; } = 1;
    public string HeartbeatBoolAddress { get; set; } = string.Empty;

    public PlcDeviceProfile Copy() => new()
    {
        Id = Id,
        Name = Name,
        IpAddress = IpAddress,
        Port = Port,
        SlaveId = SlaveId,
        HeartbeatBoolAddress = HeartbeatBoolAddress
    };
}

public sealed class PlcDeviceConfiguration
{
    public List<PlcDeviceProfile> Devices { get; set; } = [];

    /// <summary>功能区键到 PLC 设备 ID 的绑定。</summary>
    public Dictionary<string, string> Bindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static readonly string[] FunctionAreas =
    [
        "主界面与报警",
        "调试与轴控",
        "工站控制",
        "参数调整",
        "自动任务",
        "相机桥接",
        "客户界面"
    ];

    public string? GetDeviceId(string area) => Bindings.TryGetValue(area, out string? id) ? id : null;

    public void Bind(string area, string deviceId) => Bindings[area] = deviceId;

    public void EnsureDefaultBindings()
    {
        if (Devices.Count == 0) return;
        string defaultId = Devices[0].Id;
        foreach (string area in FunctionAreas)
        {
            string? id = GetDeviceId(area);
            if (id is null || !Devices.Any(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)))
                Bind(area, defaultId);
        }
    }

    public PlcDeviceConfiguration Copy() => new()
    {
        Devices = Devices.Select(x => x.Copy()).ToList(),
        Bindings = new Dictionary<string, string>(Bindings, StringComparer.OrdinalIgnoreCase)
    };

    public void Normalize()
    {
        Devices ??= [];
        Bindings ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Devices = Devices
            .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Id))
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        foreach (PlcDeviceProfile device in Devices)
        {
            device.Name = string.IsNullOrWhiteSpace(device.Name) ? device.Id : device.Name.Trim();
            device.IpAddress = device.IpAddress?.Trim() ?? string.Empty;
            device.Port = Math.Clamp(device.Port, 1, 65535);
            device.SlaveId = Math.Clamp(device.SlaveId, 0, 255);
            device.HeartbeatBoolAddress = device.HeartbeatBoolAddress?.Trim() ?? string.Empty;
        }

        foreach (string area in FunctionAreas)
        {
            string? boundId = GetDeviceId(area);
            if (boundId is null || Devices.All(x => !string.Equals(x.Id, boundId, StringComparison.OrdinalIgnoreCase)))
                Bindings.Remove(area);
        }
    }

    public static PlcDeviceConfiguration Load(string path, MainPlcLegacyConfig legacy)
    {
        PlcDeviceConfiguration? configuration = null;
        if (File.Exists(path))
        {
            try
            {
                configuration = JsonSerializer.Deserialize<PlcDeviceConfiguration>(File.ReadAllText(path), JsonOptions);
            }
            catch
            {
                // Broken or old config files fall back to the legacy primary PLC configuration.
            }
        }

        configuration ??= new PlcDeviceConfiguration
        {
            Devices =
            [
                new PlcDeviceProfile
                {
                    Id = "plc-1",
                    Name = "主 PLC",
                    IpAddress = legacy.IpAddress,
                    Port = legacy.Port,
                    SlaveId = legacy.SlaveId,
                    HeartbeatBoolAddress = legacy.HeartbeatBoolAddress
                }
            ]
        };

        configuration.Normalize();
        if (configuration.Devices.Count == 0)
        {
            configuration.Devices.Add(new PlcDeviceProfile
            {
                Id = "plc-1",
                Name = "主 PLC",
                IpAddress = legacy.IpAddress,
                Port = legacy.Port,
                SlaveId = legacy.SlaveId,
                HeartbeatBoolAddress = legacy.HeartbeatBoolAddress
            });
        }

        string preferredPrimaryId = configuration.GetDeviceId(FunctionAreas[0]) ?? configuration.Devices[0].Id;
        int primaryIndex = configuration.Devices.FindIndex(x => string.Equals(x.Id, preferredPrimaryId, StringComparison.OrdinalIgnoreCase));
        if (primaryIndex > 0)
        {
            PlcDeviceProfile primary = configuration.Devices[primaryIndex];
            configuration.Devices.RemoveAt(primaryIndex);
            configuration.Devices.Insert(0, primary);
        }

        configuration.EnsureDefaultBindings();

        return configuration;
    }

    public void Save(string path)
    {
        Normalize();
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
}

public sealed class MainPlcLegacyConfig
{
    public string IpAddress { get; set; } = "192.168.1.88";
    public int Port { get; set; } = 502;
    public int SlaveId { get; set; } = 1;
    public string HeartbeatBoolAddress { get; set; } = string.Empty;
}
