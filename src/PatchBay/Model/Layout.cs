using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PatchBay.Audio;

namespace PatchBay.Model;

public sealed class CableModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string From { get; set; } = "";
    public string To { get; set; } = "";

    // Remembered so an unplugged device can still be drawn as a greyed-out box.
    public string FromLabel { get; set; } = "";
    public string FromGroup { get; set; } = "";
    public string ToLabel { get; set; } = "";
    public string ToGroup { get; set; } = "";

    public float Volume { get; set; } = 1f;
    public bool Muted { get; set; }
    public ChannelMode Channels { get; set; } = ChannelMode.Normal;
    public int Color { get; set; }

    [JsonIgnore] public string? Error { get; set; }
}

public sealed class BoxPosition
{
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class LayoutFile
{
    public Dictionary<string, BoxPosition> Boxes { get; set; } = new();
    public List<CableModel> Cables { get; set; } = new();
    public List<string> Hidden { get; set; } = new();
    public int LatencyMs { get; set; } = 3;
    public bool ShowLoopback { get; set; }
    public bool AudioOn { get; set; } = true;
    public bool GuideOpen { get; set; } = true;
}

public static class LayoutStore
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PatchBay");
    public static string FilePath { get; } = Path.Combine(Folder, "layout.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static LayoutFile Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<LayoutFile>(File.ReadAllText(FilePath), Options) ?? new LayoutFile();
        }
        catch (Exception ex)
        {
            Log("Could not read layout: " + ex.Message);
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch { }
        }
        return new LayoutFile();
    }

    public static void Save(LayoutFile layout)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(layout, Options));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex)
        {
            Log("Could not save layout: " + ex.Message);
        }
    }

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.AppendAllText(Path.Combine(Folder, "log.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch { }
    }
}
