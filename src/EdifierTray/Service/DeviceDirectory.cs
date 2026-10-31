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
    readonly Func<string, Task<bool>> _isPaired;
    readonly Func<Task<List<(string A2dp, string Name)>>> _pairedLister;
    readonly Func<string, string, Task<ITransport>> _bleConnect;
    readonly Func<string, Task<bool>> _isConnected;
    public Settings Settings { get; }

    public DeviceDirectory(Settings? settings = null, Action<Settings>? save = null, Scanner? scanner = null,
                           Func<string, Task<bool>>? isPaired = null,
                           Func<Task<List<(string A2dp, string Name)>>>? pairedLister = null,
                           Func<string, string, Task<ITransport>>? bleConnect = null,
                           Func<string, Task<bool>>? isConnected = null)
    {
        Settings = settings ?? Settings.Load();
        _save = save ?? (s => s.Save());
        _scanner = scanner ?? Discovery.ScanAllAsync;
        _isPaired = isPaired ?? Discovery.IsPairedAsync;
        _pairedLister = pairedLister ?? Discovery.PairedEdifierDevicesAsync;
        _isConnected = isConnected ?? Discovery.IsConnectedAsync;
        _bleConnect = bleConnect ?? (async (addr, svc) => await BleGattTransport.ConnectAsync(addr, svc));
    }

    public static DeviceDirectory Load() => new();

    /// <summary>沒有廣播就取不到 bleVersion／encryption，退回實機驗證過的值：ES850NB 是 (2, 16)，MP330 是 (2, 0)；其他機種沒驗證過，先用 (2, 0)。</summary>
    public static Session FallbackSession(string model) => model == "ES850NB" ? new Session(2, 16) : new Session(2, 0);

    /// <summary>從經典藍牙位址推導 BLE 位址：第三個 byte 的低 4 bit 清零（實機：ES850NB、MP330 皆為 B3 → B0）。連線時會驗證 GATT service，推導錯了就連不上。</summary>
    public static string FallbackBleAddress(string a2dp)
    {
        var parts = a2dp.ToUpperInvariant().Split(':');
        parts[2] = (Convert.ToInt32(parts[2], 16) & 0xF0).ToString("X2");
        return string.Join(':', parts);
    }

    public IReadOnlyList<KnownDevice> Devices => Settings.Devices.ToList();
    public KnownDevice? Current => Settings.CurrentDevice();

    /// <summary>還沒連上、讀不到裝置名稱時顯示的名稱：連過的用裝置名稱，否則用機種名稱，不知道機種就用 Edifier。</summary>
    public static string FallbackName(KnownDevice? d) =>
        d is null ? Catalog.GenericModel : d.Name != "" ? d.Name : d.Model;

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

    /// <summary>把這台電腦沒有配對的裝置從清單移除（廣播會掃到附近所有 Edifier 裝置，沒配對的連不上）。選用的被移除就清掉選擇。</summary>
    public async Task PruneUnpairedAsync()
    {
        bool changed = false;
        foreach (var d in Settings.Devices.ToList())
        {
            if (await _isPaired(d.A2dp)) continue;
            Settings.Devices.Remove(d);
            if (string.Equals(Settings.Selected, d.A2dp, StringComparison.OrdinalIgnoreCase)) Settings.Selected = "";
            changed = true;
        }
        if (changed) _save(Settings);
    }

    /// <summary>已用經典藍牙連著電腦的裝置不再廣播，掃描看不到。改用 Windows 的已配對清單補上：機種由名稱對應目錄，BLE 位址與 Session 用推導／實機驗證的預設值。</summary>
    async Task<List<KnownDevice>> RememberPairedWithoutAdvertAsync(HashSet<string> advertised)
    {
        var added = new List<KnownDevice>();
        foreach (var (a2dp, name) in await _pairedLister())
        {
            if (advertised.Contains(a2dp.ToUpperInvariant()) || Settings.Device(a2dp) is not null) continue;
            var model = name.StartsWith("EDIFIER ", StringComparison.OrdinalIgnoreCase) ? name[8..] : name;
            if (!Catalog.ByName.ContainsKey(model)) model = Catalog.GenericModel;
            var session = FallbackSession(model);
            var dev = new KnownDevice
            {
                A2dp = a2dp.ToUpperInvariant(), Ble = FallbackBleAddress(a2dp), Version = session.Version,
                Encryption = session.Encryption, Name = name, Model = model,
            };
            Settings.Remember(dev);
            added.Add(dev);
        }
        return added;
    }

    /// <summary>把 Windows 已配對、但清單裡還沒有的 Edifier 裝置加進來（不靠廣播）。回傳是否有新增。啟動時與每次連線前都呼叫，新配對的裝置才找得到。</summary>
    public async Task<bool> AddPairedAsync()
    {
        var added = await RememberPairedWithoutAdvertAsync(new HashSet<string>());
        if (added.Count > 0) _save(Settings);
        return added.Count > 0;
    }

    /// <summary>掃描廣播，把找到且已配對的裝置併入已知清單並存檔；回傳這次掃到的（已配對）裝置。</summary>
    public async Task<List<KnownDevice>> ScanAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var scanned = await _scanner(timeout, ct);
        foreach (var f in scanned)
        {
            Settings.Remember(new KnownDevice
            {
                A2dp = f.A2dpAddress, Ble = f.BleAddress, Version = f.Session.Version, Encryption = f.Session.Encryption,
                Model = f.Model == "" ? Catalog.GenericModel : f.Model,
            });
        }
        var fallback = await RememberPairedWithoutAdvertAsync(scanned.Select(f => f.A2dpAddress.ToUpperInvariant()).ToHashSet());
        await PruneUnpairedAsync();
        var seen = scanned.Select(f => Settings.Device(f.A2dpAddress)).OfType<KnownDevice>()
                          .Concat(fallback.Where(Settings.Devices.Contains)).ToList();
        if (seen.Count > 0) _save(Settings);
        return seen;
    }

    /// <summary>連到目前選用的裝置（沒選就用第一台；一台都沒有就先掃描）。官方規則：機種有 SPP 就走 SPP，否則走 BLE GATT。</summary>
    public Connector MakeConnector(TimeSpan? scanTimeout = null)
    {
        var scan = scanTimeout ?? TimeSpan.FromSeconds(8);
        return async ct =>
        {
            await AddPairedAsync();
            if (Current is null) await ScanAsync(scan, ct);
            var candidates = await CandidatesAsync();
            if (candidates.Count == 0) throw new IOException(Strings.T("e.not_found", new { model = "Edifier" }));
            Exception? last = null;
            foreach (var dev in candidates)
            {
                try { return await ConnectOne(dev); }
                catch (Exception e) when (e is IOException or TimeoutException) { last = e; }
            }
            throw last!;
        };
    }

    /// <summary>要嘗試連線的裝置。使用者明確選過就只連那一台；沒選過時，以藍牙連線狀態偵測：
    /// 有已連著電腦的裝置就只連它們（例如 MP330 連上後自動切過去），都沒連著才連第一台。</summary>
    async Task<List<KnownDevice>> CandidatesAsync()
    {
        if (Settings.Selected != "" && Settings.Device(Settings.Selected) is { } chosen) return new() { chosen };
        var connected = new List<KnownDevice>();
        foreach (var d in Settings.Devices)
            if (await _isConnected(d.A2dp)) connected.Add(d);
        if (connected.Count > 0) return connected;
        return Current is { } first ? new() { first } : new();
    }

    async Task<(ITransport Transport, Session Session)> ConnectOne(KnownDevice dev)
    {
        {
            var session = new Session(dev.Version, dev.Encryption);
            if (!Catalog.UsesSpp(dev.Model))
            {
                if (dev.Ble == "") throw new IOException(Strings.T("e.not_found", new { model = dev.Model }));
                return (await _bleConnect(dev.Ble, Catalog.ByName[dev.Model].Service), session);
            }
            var t = await RfcommTransport.ConnectAsync(dev.A2dp);
            return ((ITransport)t, session);
        }
    }
}
