using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace EdifierTray.Transport;

/// <summary>
/// BLE GATT 傳輸（只有 BLE 的機種，例如 MP330）。做法與 Python 版 ble.py 的 BleakTransport 相同：
/// 開機種的 service，對 read characteristic 訂閱通知，往 write characteristic 寫入；
/// 支援 write-without-response 就用它，否則用 write。直接用 BLE 位址連線，不需要先掃到廣播。
/// </summary>
public sealed class BleGattTransport : ITransport
{
    public const string ReadUuid = "48090001-1a48-11e9-ab14-d663bd873d93";
    public const string WriteUuid = "48090002-1a48-11e9-ab14-d663bd873d93";

    public event Action<byte[]>? DataReceived;
    public event Action? Closed;

    readonly BluetoothLEDevice _device;
    readonly IReadOnlyList<GattDeviceService> _services;
    readonly GattCharacteristic _read, _write;
    readonly GattWriteOption _option;
    volatile bool _closed;

    BleGattTransport(BluetoothLEDevice device, IReadOnlyList<GattDeviceService> services, GattCharacteristic read, GattCharacteristic write)
    {
        _device = device; _services = services; _read = read; _write = write;
        _option = write.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
            ? GattWriteOption.WriteWithoutResponse : GattWriteOption.WriteWithResponse;
    }

    public static async Task<BleGattTransport> ConnectAsync(string bleAddress, string serviceUuid, TimeSpan? timeout = null)
    {
        var to = timeout ?? TimeSpan.FromSeconds(15);
        var open = BluetoothLEDevice.FromBluetoothAddressAsync(RfcommTransport.ParseAddress(bleAddress)).AsTask();
        if (await Task.WhenAny(open, Task.Delay(to)) != open) throw new TimeoutException("BLE 連線逾時");
        var dev = await open ?? throw new IOException($"找不到 BLE 裝置 {bleAddress}");
        try
        {
            var svcs = await dev.GetGattServicesForUuidAsync(Guid.Parse(serviceUuid), BluetoothCacheMode.Uncached);
            if (svcs.Status != GattCommunicationStatus.Success || svcs.Services.Count == 0)
                throw new IOException($"找不到 service {serviceUuid}（{svcs.Status}）");
            var svc = svcs.Services[0];
            var read = await Characteristic(svc, ReadUuid);
            var write = await Characteristic(svc, WriteUuid);
            var t = new BleGattTransport(dev, svcs.Services.ToList(), read, write);
            read.ValueChanged += t.OnValueChanged;
            dev.ConnectionStatusChanged += t.OnConnectionStatusChanged;
            var st = await read.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (st != GattCommunicationStatus.Success) { t.Dispose(); throw new IOException($"訂閱通知失敗（{st}）"); }
            return t;
        }
        catch { dev.Dispose(); throw; }
    }

    /// <summary>取 characteristic。剛斷線重連時，上一個連線的 GATT 工作階段可能還沒完全釋放，會暫時取不到，所以重試幾次。</summary>

    static async Task<GattCharacteristic> Characteristic(GattDeviceService svc, string uuid)
    {
        GattCommunicationStatus status = GattCommunicationStatus.Unreachable;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0) await Task.Delay(600);
            var r = await svc.GetCharacteristicsForUuidAsync(Guid.Parse(uuid), BluetoothCacheMode.Uncached);
            status = r.Status;
            if (r.Status == GattCommunicationStatus.Success && r.Characteristics.Count > 0) return r.Characteristics[0];
        }
        throw new IOException($"找不到 characteristic {uuid}（{status}）");
    }

    void OnValueChanged(GattCharacteristic _, GattValueChangedEventArgs e)
    {
        var bytes = new byte[e.CharacteristicValue.Length];
        DataReader.FromBuffer(e.CharacteristicValue).ReadBytes(bytes);
        DataReceived?.Invoke(bytes);
    }

    void OnConnectionStatusChanged(BluetoothLEDevice d, object _)
    {
        if (d.ConnectionStatus == BluetoothConnectionStatus.Disconnected && !_closed) Closed?.Invoke();
    }

    public async Task WriteAsync(byte[] data)
    {
        if (_closed) throw new IOException("BLE 連線已關閉");
        var w = new DataWriter();
        w.WriteBytes(data);
        var st = await _write.WriteValueAsync(w.DetachBuffer(), _option);
        if (st != GattCommunicationStatus.Success) throw new IOException($"BLE 寫入失敗（{st}）");
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _read.ValueChanged -= OnValueChanged;
        _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        // 取消訂閱並釋放 GATT 服務與裝置：沒釋放的話，Windows 會一直占著這個工作階段，之後同一台再連就取不到 characteristic（顯示未連線）
        _ = Task.Run(async () =>
        {
            try { await _read.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None); }
            catch (Exception) { /* 裝置已斷線時寫不進去，照常釋放 */ }
            foreach (var s in _services) s.Dispose();
            _device.Dispose();
        });
    }
}
