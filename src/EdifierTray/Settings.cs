using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdifierTray;

public sealed class KnownDevice
{
    [JsonPropertyName("a2dp")] public string A2dp { get; set; } = "";   // 經典藍牙位址（SPP 連線目標，也是裝置的唯一識別）
    [JsonPropertyName("ble")] public string Ble { get; set; } = "";
    [JsonPropertyName("version")] public int Version { get; set; } = 2;
    [JsonPropertyName("encryption")] public int Encryption { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";   // 連上後由 0xC9 取得
    [JsonPropertyName("model")] public string Model { get; set; } = "ES850NB";  // 辨識出的機種（目錄名稱）；不在目錄的為 "Edifier"

    /// <summary>位址後四碼，用來區分同型號的多台裝置。</summary>
    [JsonIgnore] public string Tail => A2dp.Length >= 5 ? A2dp[^5..] : A2dp;
}

/// <summary>
/// 使用者設定：外觀、語言、選用的裝置與已知裝置清單。存在 %APPDATA%\EdifierTray\settings.json
/// （內含裝置位址，不放專案目錄）。讀不到或檔案壞掉就用預設值，不讓程式起不來。
/// </summary>
public sealed class Settings
{
    public static readonly string[] Themes = { "system", "light", "dark" };

    [JsonPropertyName("theme")] public string Theme { get; set; } = "system";
    [JsonPropertyName("language")] public string Language { get; set; } = "";   // 空字串＝第一次執行，依系統語言決定
    [JsonPropertyName("selected")] public string Selected { get; set; } = "";   // 選用裝置的 a2dp 位址；空字串＝第一台
    [JsonPropertyName("devices")] public List<KnownDevice> Devices { get; set; } = new();

    public KnownDevice? Device(string a2dp) =>
        Devices.FirstOrDefault(d => string.Equals(d.A2dp, a2dp, StringComparison.OrdinalIgnoreCase));

    /// <summary>新增或更新（保留已知的名稱）。</summary>
    public void Remember(KnownDevice dev)
    {
        var old = Device(dev.A2dp);
        if (old is null) { Devices.Add(dev); return; }
        if (dev.Ble != "") old.Ble = dev.Ble;
        old.Version = dev.Version; old.Encryption = dev.Encryption; old.Model = dev.Model;
        if (dev.Name != "") old.Name = dev.Name;
    }

    public KnownDevice? CurrentDevice() =>
        (Selected != "" ? Device(Selected) : null) ?? Devices.FirstOrDefault();

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EdifierTray", "settings.json");

    public static Settings Load(string? path = null)
    {
        try
        {
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path ?? DefaultPath)) ?? new Settings();
            if (!Themes.Contains(s.Theme)) s.Theme = "system";
            s.Devices = s.Devices.Where(d => d is not null && d.A2dp != "").ToList();
            return s;
        }
        catch { return new Settings(); }
    }

    public void Save(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        catch { /* 寫不進去不影響功能（只是下次不記得設定） */ }
    }
}
