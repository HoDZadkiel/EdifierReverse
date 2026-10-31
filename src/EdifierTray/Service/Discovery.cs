using EdifierTray.Protocol;
using EdifierTray.Transport;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace EdifierTray.Service;

/// <summary>
/// 從 BLE 廣播找出 Edifier 裝置，取得經典藍牙位址與 Session（版本、加密）。廣播格式（實機驗證）：
/// 廠商 ID 0x07E0，資料前 6 bytes 是 A2DP 位址，倒數第 2 byte 是 bleVersion，最後 1 byte 是 encryption。
/// 哪些廣播算 Edifier 裝置由 <see cref="Catalog.Identify"/> 決定（官方 App 的判定方式）。
/// </summary>
public static class Discovery
{
    public const ushort CompanyId = Catalog.CompanyId;

    public sealed record Found(string A2dpAddress, Session Session, string Model = "", string BleAddress = "", int Rssi = 0);

    public static Found? ParseManufacturerData(byte[] data)
    {
        if (data.Length < 8) return null;  // 6 bytes 位址 + 版本 + 加密
        var addr = string.Join(':', data.Take(6).Select(b => b.ToString("X2")));
        return new Found(addr, new Session(data[^2], data[^1]));
    }

    /// <summary>辨識一則廣播：不是 Edifier 回傳 null；是就帶上機種、位址、Session。</summary>
    public static Found? IdentifyAdvert(IEnumerable<string> serviceUuids, IReadOnlyDictionary<ushort, byte[]> manufacturerData,
                                        string bleAddress = "", int rssi = 0)
    {
        var model = Catalog.Identify(serviceUuids, manufacturerData);
        if (model is null || !manufacturerData.TryGetValue(CompanyId, out var data)) return null;  // 沒有廠商資料就取不到位址
        var f = ParseManufacturerData(data);
        return f is null ? null : f with { Model = model.Model, BleAddress = bleAddress, Rssi = rssi };
    }

    static string FormatAddress(ulong a) => string.Join(':', Enumerable.Range(0, 6).Select(i => ((a >> (8 * (5 - i))) & 0xFF).ToString("X2")));

    /// <summary>掃描指定時間，回傳所有 Edifier 裝置（任何機種，同機種多台也各一筆），依訊號強弱排序。</summary>
    public static async Task<List<Found>> ScanAllAsync(TimeSpan timeout, CancellationToken ct)
    {
        var found = new Dictionary<string, Found>();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, e) =>
        {
            var mfr = new Dictionary<ushort, byte[]>();
            foreach (var m in e.Advertisement.ManufacturerData)
            {
                var bytes = new byte[m.Data.Length];
                DataReader.FromBuffer(m.Data).ReadBytes(bytes);
                mfr[m.CompanyId] = bytes;
            }
            var f = IdentifyAdvert(e.Advertisement.ServiceUuids.Select(g => g.ToString()), mfr,
                                   FormatAddress(e.BluetoothAddress), e.RawSignalStrengthInDBm);
            if (f is null) return;
            lock (found)
                if (!found.TryGetValue(f.A2dpAddress, out var old) || f.Rssi > old.Rssi) found[f.A2dpAddress] = f;
        };
        watcher.Start();
        try { await Task.Delay(timeout, ct); }
        catch (OperationCanceledException) { }
        finally { watcher.Stop(); }
        lock (found) return found.Values.OrderByDescending(f => f.Rssi).ToList();
    }

    /// <summary>這台電腦是否已配對該裝置（經典藍牙位址）。查不到時回傳 true：寧可多顯示，也不要因為 API 失敗把裝置藏起來。</summary>
    public static async Task<bool> IsPairedAsync(string a2dpAddress)
    {
        try
        {
            using var dev = await BluetoothDevice.FromBluetoothAddressAsync(RfcommTransport.ParseAddress(a2dpAddress));
            return dev?.DeviceInformation.Pairing.IsPaired ?? false;
        }
        catch (Exception) { return true; }
    }

    /// <summary>這台電腦目前是否已用經典藍牙連著該裝置（耳機／喇叭開機並連上）。查不到回傳 false。</summary>
    public static async Task<bool> IsConnectedAsync(string a2dpAddress)
    {
        try
        {
            using var dev = await BluetoothDevice.FromBluetoothAddressAsync(RfcommTransport.ParseAddress(a2dpAddress));
            return dev?.ConnectionStatus == BluetoothConnectionStatus.Connected;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Windows 已配對、名稱以 EDIFIER 開頭的裝置（經典藍牙位址, 名稱）。查不到回傳空清單。已連著電腦的裝置不再廣播，這是備援來源。</summary>
    public static async Task<List<(string A2dp, string Name)>> PairedEdifierDevicesAsync()
    {
        try
        {
            var infos = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true));
            return infos.Where(i => i.Name.StartsWith("EDIFIER ", StringComparison.OrdinalIgnoreCase) && i.Id.Contains('-'))
                        .Select(i => (i.Id[(i.Id.LastIndexOf('-') + 1)..].ToUpperInvariant(), i.Name)).ToList();
        }
        catch (Exception) { return new(); }
    }

    /// <summary>命令列與自動化用：依設定檔的選用裝置連線。</summary>
    public static Connector MakeConnector() => DeviceDirectory.Load().MakeConnector();
}
