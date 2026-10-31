using Windows.Devices.Bluetooth;

namespace EdifierTray.Service;

/// <summary>
/// 監看已配對的經典藍牙裝置：連上電腦（耳機開機或進入範圍）時觸發 <see cref="Connected"/>，
/// 讓服務不必等退避計時就重連。裝置沒配對過（取不到 BluetoothDevice）就不監看，仍靠退避輪詢。
/// 監看所有已知裝置（不只選用的那台），沒明確選過裝置時，新連上的裝置（例如 MP330）才能被偵測到；清單變動時再呼叫 <see cref="WatchAsync"/>。
/// </summary>
public sealed class ConnectionWatcher : IDisposable
{
    readonly List<BluetoothDevice> _devices = new();
    readonly object _lock = new();
    int _generation;

    public event Action? Connected;

    /// <summary>改為監看指定的位址（A2DP 位址）；空清單或取不到裝置時只停止監看。</summary>
    public async Task WatchAsync(IEnumerable<string> a2dpAddresses)
    {
        var gen = Interlocked.Increment(ref _generation);
        Stop();
        foreach (var address in a2dpAddresses.Where(a => a != "").Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var dev = await BluetoothDevice.FromBluetoothAddressAsync(Transport.RfcommTransport.ParseAddress(address));
                if (dev is null) continue;
                if (gen != _generation) { dev.Dispose(); return; }  // 等待期間又換了清單
                dev.ConnectionStatusChanged += OnStatusChanged;
                lock (_lock) _devices.Add(dev);
                if (dev.ConnectionStatus == BluetoothConnectionStatus.Connected) Connected?.Invoke();  // 監看前就已連上
            }
            catch (Exception) { /* 取不到裝置不影響功能：退避輪詢照常運作 */ }
        }
    }

    void OnStatusChanged(BluetoothDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Connected) Connected?.Invoke();
    }

    void Stop()
    {
        BluetoothDevice[] old;
        lock (_lock) { old = _devices.ToArray(); _devices.Clear(); }
        foreach (var d in old)
        {
            d.ConnectionStatusChanged -= OnStatusChanged;
            d.Dispose();
        }
    }

    public void Dispose() { Interlocked.Increment(ref _generation); Stop(); }
}
