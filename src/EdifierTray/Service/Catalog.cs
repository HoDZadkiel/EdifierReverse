using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdifierTray.Service;

public sealed record CatalogModel(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("search")] string Search,
    [property: JsonPropertyName("service")] string Service,
    [property: JsonPropertyName("spp")] bool Spp,
    [property: JsonPropertyName("type")] int? Type = null,
    [property: JsonPropertyName("tws")] bool Tws = false,
    [property: JsonPropertyName("published")] bool Published = true);

/// <summary>
/// Edifier 機種目錄與裝置辨識（資料是 Python 版共用的 catalog.json，由 scripts/gen_catalog.py 從官方 App 的產品表產生）。
/// 官方 App 的判定方式：每個機種有一個 BLE 搜尋 UUID，廣播的 service UUID 含它就是該機種；廠商資料（廠商 ID 0x07E0）
/// 帶經典藍牙位址、bleVersion 與 encryption。所以辨識 Edifier 裝置用兩個條件：
///   1. 廣播含目錄內任一機種的搜尋 UUID（知道是哪個機種）；
///   2. 或廠商資料的廠商 ID 是 0x07E0 且長度足夠（確定是 Edifier，但機種不在目錄，例如新機種）。
/// </summary>
public static class Catalog
{
    public const ushort CompanyId = 0x07E0;
    public const string GenericModel = "Edifier";
    public const string SppUuid = "EDF00000-EDFE-DFED-FEDF-EDFEDFEDFEDF";

    public static readonly IReadOnlyList<CatalogModel> Models = Load();
    static readonly Dictionary<string, CatalogModel> BySearch = Models.ToDictionary(m => m.Search);
    public static readonly IReadOnlyDictionary<string, CatalogModel> ByName = Models.ToDictionary(m => m.Model);

    static List<CatalogModel> Load()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("catalog.json")
                      ?? throw new InvalidOperationException("找不到內嵌的 catalog.json");
        return JsonSerializer.Deserialize<List<CatalogModel>>(s)!;
    }

    /// <summary>判定一則 BLE 廣播是不是 Edifier 裝置。不是回傳 null；是但機種不在目錄回傳通用機種（先試 SPP）。</summary>
    public static CatalogModel? Identify(IEnumerable<string> serviceUuids, IReadOnlyDictionary<ushort, byte[]> manufacturerData)
    {
        foreach (var u in serviceUuids)
            if (BySearch.TryGetValue(u.ToLowerInvariant(), out var m)) return m;
        if (manufacturerData.TryGetValue(CompanyId, out var data) && data.Length >= 8)
            return new CatalogModel(GenericModel, "", "", true);
        return null;
    }

    /// <summary>機種是否有 SPP 通道。官方規則：產品表有 SPP UUID 就走 SPP，否則走 BLE GATT。不在目錄的先當作有 SPP。</summary>
    public static bool UsesSpp(string model) => !ByName.TryGetValue(model, out var m) || m.Spp;
}
