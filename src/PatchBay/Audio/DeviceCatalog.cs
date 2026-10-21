using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace PatchBay.Audio;

/// <summary>
/// Out = sound comes OUT of the device into the patch bay (a mic, a line input, "CABLE Output").
/// In  = sound goes INTO the device (speakers, headphones, "CABLE Input").
/// </summary>
public enum JackKind { Out, In }

public sealed class JackInfo
{
    public required string Id { get; init; }
    public required string EndpointId { get; init; }
    public required string Label { get; init; }
    public required JackKind Kind { get; init; }
    public required string GroupKey { get; init; }
    public required string GroupTitle { get; init; }
    public bool IsLoopback { get; init; }
    /// <summary>Null when the device is unplugged (a "ghost" jack kept for a saved cable).</summary>
    public MMDevice? Device { get; init; }
    public bool IsMissing => Device == null;

    public static string CaptureId(string endpointId) => "cap:" + endpointId;
    public static string RenderId(string endpointId) => "ren:" + endpointId;
    public static string LoopbackId(string endpointId) => "loop:" + endpointId;
}

public sealed class DeviceGroup
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public string? Subtitle { get; set; }
    public bool IsGhost { get; init; }
    public List<JackInfo> Jacks { get; } = new();
}

/// <summary>Lists the Windows audio endpoints and groups them into one box per physical/virtual device.</summary>
public sealed class DeviceCatalog : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();

    /// <summary>Raised on a background thread when a device is plugged, unplugged, enabled or disabled.</summary>
    public event Action? DevicesChanged;

    public DeviceCatalog()
    {
        _enumerator.RegisterEndpointNotificationCallback(this);
    }

    public List<DeviceGroup> Build(bool includeLoopback)
    {
        var groups = new Dictionary<string, DeviceGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var dev in _enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active))
        {
            string name = Safe(() => dev.FriendlyName) ?? dev.ID;
            string groupTitle = Safe(() => dev.DeviceFriendlyName) ?? "";
            if (string.IsNullOrWhiteSpace(groupTitle)) groupTitle = name;
            string label = StripSuffix(name, groupTitle);

            if (!groups.TryGetValue(groupTitle, out var group))
            {
                group = new DeviceGroup { Key = groupTitle, Title = groupTitle, Subtitle = SubtitleFor(groupTitle) };
                groups[groupTitle] = group;
            }

            DataFlow flow;
            try { flow = dev.DataFlow; } catch { continue; }

            if (flow == DataFlow.Capture)
            {
                group.Jacks.Add(new JackInfo
                {
                    Id = JackInfo.CaptureId(dev.ID), EndpointId = dev.ID, Label = label, Kind = JackKind.Out,
                    GroupKey = group.Key, GroupTitle = group.Title, Device = dev,
                });
            }
            else if (flow == DataFlow.Render)
            {
                group.Jacks.Add(new JackInfo
                {
                    Id = JackInfo.RenderId(dev.ID), EndpointId = dev.ID, Label = label, Kind = JackKind.In,
                    GroupKey = group.Key, GroupTitle = group.Title, Device = dev,
                });
                if (includeLoopback)
                {
                    group.Jacks.Add(new JackInfo
                    {
                        Id = JackInfo.LoopbackId(dev.ID), EndpointId = dev.ID, Label = label + " (what's playing)",
                        Kind = JackKind.Out, IsLoopback = true,
                        GroupKey = group.Key, GroupTitle = group.Title, Device = dev,
                    });
                }
            }
        }

        foreach (var g in groups.Values)
        {
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < g.Jacks.Count; i++)
            {
                var j = g.Jacks[i];
                string k = j.Kind + "|" + j.Label;
                seen[k] = seen.GetValueOrDefault(k) + 1;
                if (seen[k] > 1)
                    g.Jacks[i] = new JackInfo { Id = j.Id, EndpointId = j.EndpointId, Label = j.Label + " #" + seen[k], Kind = j.Kind, GroupKey = j.GroupKey, GroupTitle = j.GroupTitle, IsLoopback = j.IsLoopback, Device = j.Device };
            }
        }
        foreach (var g in groups.Values)
            g.Jacks.Sort((a, b) => a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind) : string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));

        return groups.Values.Where(g => g.Jacks.Count > 0)
            .OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? SubtitleFor(string title)
    {
        if (title.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) || title.Contains("Virtual Cable", StringComparison.OrdinalIgnoreCase))
            return "Virtual cable: sound sent IN comes back OUT";
        if (title.Contains("Voicemod", StringComparison.OrdinalIgnoreCase))
            return "Voicemod's changed voice comes OUT here";
        if (title.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
            return "Virtual device";
        return null;
    }

    private static string StripSuffix(string name, string group)
    {
        string suffix = " (" + group + ")";
        if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && name.Length > suffix.Length)
            return name[..^suffix.Length];
        return name;
    }

    private static string? Safe(Func<string> f)
    {
        try { return f(); } catch { return null; }
    }

    // IMMNotificationClient -------------------------------------------------
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => DevicesChanged?.Invoke();
    public void OnDeviceAdded(string pwstrDeviceId) => DevicesChanged?.Invoke();
    public void OnDeviceRemoved(string deviceId) => DevicesChanged?.Invoke();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
        _enumerator.Dispose();
    }
}
