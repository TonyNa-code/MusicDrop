using System.Text.Json;

namespace MFlacDrop;

internal sealed class AppSettings
{
    public string OutputDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Music Drop 3 Output");
    public string FfmpegPath { get; set; } = "";
    public string OutputFormat { get; set; } = "原始格式";
    public string Mp3Quality { get; set; } = "V0（约 245 kbps）";
    public string PlayerProcessDbPath { get; set; } = "";
    public string ImportedEKeyPath { get; set; } = "";
    public bool UseQqFallback { get; set; } = true;
    public bool AutoStartRequiredClients { get; set; } = true;
    public string QqMusicExecutablePath { get; set; } = "";
    public bool StrictBatchPreflight { get; set; } = true;
    public string KugouDatabasePath { get; set; } = "";

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppInfo.SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppInfo.SettingsPath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppInfo.DataDir);
        File.WriteAllText(AppInfo.SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
