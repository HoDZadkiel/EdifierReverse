using EdifierTray.Protocol;
using EdifierTray.Transport;

namespace EdifierTray.Service;

/// <summary>
/// 裝置目錄：已知裝置、選用的裝置、掃描，以及連到選用裝置的連線器。多台裝置時使用者從選單選一台，
/// 選擇存在設定檔，下次啟動沿用。位址與 Session 來自 BLE 廣播，掃描要數秒，所以掃過的裝置都記住。
/// 一次只連一台（耳機的 SPP 一次只服務一個控制端）。
/// </summary>
public sealed class DeviceDirectory
{
    public delegate Task<List<Discovery.Found>> Scanner(TimeSpan timeout, CancellationToken ct);

    readonly Action<Settings> _save;
    readonly Scanner _scanner;
    public Settings Settings { get; }

    public DeviceDirectory(Settings? settings = null, Action<Settings>? save = null, Scanner? scanner = null)
    {
        Settings = settings ?? Settings.Load();
        _save = save ?? (s => s.Save());
        _scanner = scanner ?? Discovery.ScanAllAsync;
    }

    public static DeviceDirectory Load() => new();

    public IReadOnlyList<KnownDevice> Devices => Settings.Devices.ToList();
    public KnownDevice? Current => Settings.CurrentDevice();

    /// <summary>選單上的名稱：連過的有裝置名稱，沒連過的用機種名稱；一律附後四碼以區分同型號多台。</summary>
    public static string Label(KnownDevice d)
    {
        var name = d.Name.StartsWith("EDIFIER ", StringComparison.OrdinalIgnoreCase) ? d.Name[8..] : d.Name;
        if (name == "") name = d.Model != Catalog.GenericModel ? d.Model : Strings.T("d.unnamed", new { tail = "" }).Trim();
        return $"{name} · {d.Tail}";
    }

    public bool Select(string a2dp)
    {
        var d = Settings.Device(a2dp);
        if (d is null) return false;
        Settings.Selected = d.A2dp;
        _save(Settings);
        return true;
    }

    public void RememberName(string a2dp, string name)
    {
        var d = Settings.Device(a2dp);
        if (d is null || name == "" || d.Name == name) return;
        d.Name = name;
        _save(Settings);
    }

    /// <summary>掃描廣播，把找到的裝置併入已知清單並存檔；回傳這次掃到的裝置。</summary>
    public async Task<List<KnownDevice>> ScanAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var seen = new List<KnownDevice>();
        foreach (var f in await _scanner(timeout, ct))
        {
            Settings.Remember(new KnownDevice
            {
                A2dp = f.A2dpAddress, Ble = f.BleAddress, Version = f.Session.Version, Encryption = f.Session.Encryption,
                Model = f.Model == "" ? Catalog.GenericModel : f.Model,
            });
            seen.Add(Settings.Device(f.A2dpAddress)!);
        }
        if (seen.Count > 0) _save(Settings);
        return seen;
    }

    /// <summary>連到目前選用的裝置（沒選就用第一台；一台都沒有就先掃描）。官方規則：機種有 SPP 就走 SPP，否則走 BLE GATT。</summary>
    public Connector MakeConnector(TimeSpan? scanTimeout = null)
    {
        var scan = scanTimeout ?? TimeSpan.FromSeconds(8);
        return async ct =>
        {
            var dev = Current;
            if (dev is null)
            {
                await ScanAsync(scan, ct);
                dev = Current ?? throw new IOException(Strings.T("e.not_found", new { model = "Edifier" }));
            }
            if (!Catalog.UsesSpp(dev.Model)) throw new IOException(Strings.T("e.ble_only"));  // C# 版尚未實作 BLE GATT
            var t = await RfcommTransport.ConnectAsync(dev.A2dp);
            return ((ITransport)t, new Session(dev.Version, dev.Encryption));
        };
    }
}
