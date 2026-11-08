using NAudio.CoreAudioApi;

namespace QuietMic.Audio;

/// <param name="Name">用户可见名称，可在 Windows 设置里被重命名。</param>
/// <param name="Description">设备描述（如 "CABLE Output"），重命名时可能一起被改写。</param>
/// <param name="Adapter">声卡/驱动名称（如 "VB-Audio Virtual Cable"），重命名后不变。</param>
public sealed record AudioDevice(string Id, string Name, string Description, string Adapter, bool IsDefault)
{
    public bool Matches(string what)
    {
        return Description.Contains(what, StringComparison.OrdinalIgnoreCase)
            || Name.Contains(what, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsAdapter(string what)
    {
        return Adapter.Contains(what, StringComparison.OrdinalIgnoreCase);
    }
}

public static class DeviceList
{
    // PKEY_Device_DeviceDesc
    private static readonly PropertyKey DeviceDescriptionKey =
        new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);

    // PKEY_DeviceInterface_FriendlyName
    private static readonly PropertyKey DeviceAdapterKey =
        new(new Guid("026e516e-b814-414b-83cd-856d6fef4822"), 2);

    public static IReadOnlyList<AudioDevice> Capture()
    {
        return Enumerate(DataFlow.Capture);
    }

    public static IReadOnlyList<AudioDevice> Render()
    {
        return Enumerate(DataFlow.Render);
    }

    private static List<AudioDevice> Enumerate(DataFlow flow)
    {
        var result = new List<AudioDevice>();

        using var enumerator = new MMDeviceEnumerator();
        var defaultId = TryGetDefaultId(enumerator, flow);

        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
            {
                var description = ReadProperty(device, DeviceDescriptionKey) ?? device.FriendlyName;
                var adapter = ReadProperty(device, DeviceAdapterKey) ?? string.Empty;
                result.Add(new AudioDevice(device.ID, device.FriendlyName, description, adapter, device.ID == defaultId));
            }
        }

        return result;
    }

    private static string? ReadProperty(MMDevice device, PropertyKey key)
    {
        try
        {
            return device.Properties[key]?.Value as string;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetDefaultId(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Communications);
            return device.ID;
        }
        catch
        {
            return null;
        }
    }

    public static MMDevice Open(string id)
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDevice(id);
    }
}
