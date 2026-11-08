using System.Text.Json;

namespace QuietMic.Settings;

public sealed class AppSettings
{
    public string? MicrophoneId { get; set; }

    public string? ModelId { get; set; } = "dpdfnet8_48khz_hr";

    public double Strength { get; set; } = 1.0;

    public bool MonitorEnabled { get; set; }

    public string? MonitorId { get; set; }

    private static string FilePath => Path.Combine(App.DataDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
        }
        catch
        {
            // 损坏的设置文件直接忽略
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(App.DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 忽略
        }
    }
}
