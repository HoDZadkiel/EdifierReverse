using Windows.Devices.Bluetooth;

namespace EdifierTray.Service;

/// <summary>
/// 監看已配對的經典藍牙裝置：連上電腦（耳機開機或進入範圍）時觸發 <see cref="Connected"/>，
/// 讓服務不必等退避計時就重連。裝置沒配對過（取不到 BluetoothDevice）就不監看，仍靠退避輪詢。
/// 一次只監看一台；換選用裝置時再呼叫 <see cref="Watch"/>。
/// </summary>
public sealed class ConnectionWatcher : IDisposable
{
    BluetoothDevice? _device;
    int _generation;

    public event Action? Connected;

    /// <summary>改為監看指定位址（A2DP 位址）；空字串或取不到裝置時只停止監看。</summary>
    public async Task WatchAsync(string a2dpAddress)
    {
        var gen = Interlocked.Increment(ref _generation);
        Stop();
        if (a2dpAddress == "") return;
        try
        {
            var dev = await BluetoothDevice.FromBluetoothAddressAsync(Transport.RfcommTransport.ParseAddress(a2dpAddress));
            if (dev is null) return;
            if (gen != _generation) { dev.Dispose(); return; }  // 等待期間又換了裝置
            dev.ConnectionStatusChanged += OnStatusChanged;
            _device = dev;
            if (dev.ConnectionStatus == BluetoothConnectionStatus.Connected) Connected?.Invoke();  // 監看前就已連上
        }
        catch (Exception) { /* 取不到裝置不影響功能：退避輪詢照常運作 */ }
    }

    void OnStatusChanged(BluetoothDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Connected) Connected?.Invoke();
    }

    void Stop()
    {
        var old = Interlocked.Exchange(ref _device, null);
        if (old is null) return;
        old.ConnectionStatusChanged -= OnStatusChanged;
        old.Dispose();
    }

    public void Dispose() { Interlocked.Increment(ref _generation); Stop(); }
}
