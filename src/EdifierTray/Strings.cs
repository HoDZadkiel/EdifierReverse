using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace EdifierTray;

/// <summary>
/// 介面語言：繁體中文、簡體中文、英文、日文。字串表是 Python 版共用的 strings.json（嵌入在這個程式裡），
/// 兩邊不會各自漂移。找不到的鍵退回繁體中文，再找不到就回傳鍵名。
/// XAML 以 {DynamicResource T_xxx} 引用（鍵名的「.」換成「_」），語言切換時重寫資源就會立刻更新。
/// </summary>
public static class Strings
{
    public const string Default = "zh-TW";

    /// <summary>(代碼, 原生語言名稱)。語言名稱永遠用該語言自己的寫法，不翻譯，使用者才找得到自己的語言。</summary>
    public static readonly (string Code, string Native)[] Languages =
        { ("zh-TW", "繁體中文"), ("zh-CN", "简体中文"), ("en", "English"), ("ja", "日本語") };

    /// <summary>選單分段按鈕用的短名稱。</summary>
    public static readonly IReadOnlyDictionary<string, string> Short = new Dictionary<string, string>
        { ["zh-TW"] = "繁體", ["zh-CN"] = "简体", ["en"] = "EN", ["ja"] = "日本語" };

    /// <summary>各語言的 Windows 11 介面字體；英文與數字另用 Segoe UI Variable，放在最前面。</summary>
    static readonly IReadOnlyDictionary<string, string> CjkFonts = new Dictionary<string, string>
    {
        ["zh-TW"] = "Microsoft JhengHei UI", ["zh-CN"] = "Microsoft YaHei UI",
        ["ja"] = "Yu Gothic UI", ["en"] = "Segoe UI",
    };

    static readonly Lazy<Dictionary<string, Dictionary<string, string>>> Table = new(Load);
    public static string Language { get; private set; } = Default;

    static Dictionary<string, Dictionary<string, string>> Load()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("strings.json")
                      ?? throw new InvalidOperationException("找不到內嵌的 strings.json");
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(s)!;
    }

    public static IReadOnlyCollection<string> Codes => Languages.Select(l => l.Code).ToArray();
    public static IReadOnlyCollection<string> Keys => Table.Value[Default].Keys;
    public static IReadOnlyDictionary<string, string> TableFor(string code) => Table.Value[code];

    public static string SetLanguage(string code)
    {
        Language = Table.Value.ContainsKey(code) ? code : Default;
        return Language;
    }

    /// <summary>取字串並代入 {名稱} 參數（args 是匿名物件，例如 new { n = 10 }）。缺參數時保留原文，不丟例外。</summary>
    public static string T(string key, object? args = null)
    {
        var t = Table.Value;
        var text = (t.TryGetValue(Language, out var cur) && cur.TryGetValue(key, out var v) ? v : null)
                   ?? (t[Default].TryGetValue(key, out var d) ? d : key);
        if (args is null) return text;
        var props = args.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(args));
        return Regex.Replace(text, @"\{(\w+)\}", m => props.TryGetValue(m.Groups[1].Value, out var val) ? val?.ToString() ?? "" : m.Value);
    }

    /// <summary>第一次執行時依 Windows 介面語言挑：zh-TW/HK/MO -> 繁中、zh-* -> 簡中、ja -> 日文，其餘英文。</summary>
    public static string DetectSystemLanguage()
    {
        var name = System.Globalization.CultureInfo.InstalledUICulture.Name.ToLowerInvariant();
        if (name.StartsWith("zh"))
            return name.Contains("tw") || name.Contains("hk") || name.Contains("mo") || name.Contains("hant") ? "zh-TW" : "zh-CN";
        return name.StartsWith("ja") ? "ja" : "en";
    }

    public static string ResourceKey(string key) => "T_" + key.Replace('.', '_');

    public static string FontFamilyFor(string code) =>
        $"Segoe UI Variable Text, {(CjkFonts.TryGetValue(code, out var f) ? f : "Segoe UI")}, Segoe UI";

    /// <summary>把目前語言的字串與字體寫進 Application 資源，XAML 的 DynamicResource 會立刻更新。</summary>
    public static void ApplyToResources(ResourceDictionary r)
    {
        foreach (var key in Keys) r[ResourceKey(key)] = T(key);
        r["UiFont"] = new FontFamily(FontFamilyFor(Language));
    }
}
