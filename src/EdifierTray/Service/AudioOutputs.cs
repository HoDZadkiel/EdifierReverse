using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace EdifierTray.Service;

/// <summary>一個 Windows 播放端點（輸出裝置）。</summary>
public sealed record AudioOutput(string Id, string Name, bool IsDefault);

/// <summary>
/// 輸出裝置切換：列出作用中的播放端點，並把其中一個設為系統預設輸出（等同在 Windows 音效設定按「設為預設」）。
/// 設定預設裝置沒有公開 API，用的是系統內建但未文件化的 IPolicyConfig（SoundSwitch 等工具也是這樣做）。
/// 只改「主控台」與「多媒體」兩個角色；「通訊」角色不動，免得通話被切到不適合的立體聲端點。
/// </summary>
public static class AudioOutputs
{
    /// <summary>藍牙耳機同時有立體聲（A2DP）與通話（Hands-Free）兩個端點，後者音質低，不放進選單。</summary>
    public static bool IsListable(string friendlyName) => !friendlyName.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase);

    /// <summary>選單放得下的名稱長度。</summary>
    public static string Shorten(string name, int max = 34) => name.Length <= max ? name : name[..(max - 1)] + "…";

    public static List<AudioOutput> List()
    {
        using var enumerator = new MMDeviceEnumerator();
        string defaultId;
        try { defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; }
        catch (Exception) { defaultId = ""; }   // 完全沒有輸出裝置時沒有預設
        var list = new List<AudioOutput>();
        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active))
            if (IsListable(d.FriendlyName)) list.Add(new AudioOutput(d.ID, d.FriendlyName, d.ID == defaultId));
        return list;
    }

    public static void SetDefault(string deviceId)
    {
        var policy = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            foreach (var role in new[] { Role.Console, Role.Multimedia })
            {
                int hr = policy.SetDefaultEndpoint(deviceId, role);
                if (hr != 0) throw new COMException("SetDefaultEndpoint", hr);
            }
        }
        finally { Marshal.ReleaseComObject(policy); }
    }

    /// <summary>
    /// 輸出裝置清單的快取：右鍵選單直接讀 <see cref="Current"/>，不在 UI 執行緒列舉
    /// （列舉並讀名稱實測每次約 0.55 秒，會讓選單明顯延遲才出現）。
    /// 啟動時，以及 Windows 通知播放端點增減、狀態或預設輸出改變時，在背景重抓；重抓期間又有通知就再抓一次。
    /// </summary>
    public sealed class Cache : IDisposable
    {
        readonly Func<List<AudioOutput>> _list;
        IReadOnlyList<AudioOutput> _current = Array.Empty<AudioOutput>();
        int _running, _dirty;
        MMDeviceEnumerator? _enumerator;
        Notifier? _notifier;

        /// <param name="list">列出端點，測試可注入；預設 <see cref="AudioOutputs.List"/>。</param>
        public Cache(Func<List<AudioOutput>>? list = null) => _list = list ?? AudioOutputs.List;

        public IReadOnlyList<AudioOutput> Current => Volatile.Read(ref _current);

        /// <summary>訂閱 Windows 的端點變動通知並先抓一次。訂閱失敗只是不會自動更新，不影響其他功能。</summary>
        public void Start()
        {
            try
            {
                _enumerator = new MMDeviceEnumerator();
                _notifier = new Notifier(this);
                _enumerator.RegisterEndpointNotificationCallback(_notifier);
            }
            catch (Exception) { }
            Refresh();
        }

        /// <summary>在背景重抓。回傳的工作在這一輪（含期間累積的通知）抓完時完成，測試用。</summary>
        public Task Refresh()
        {
            Volatile.Write(ref _dirty, 1);
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return Task.CompletedTask;   // 抓取中：由那一輪再抓一次
            return Task.Run(() =>
            {
                try
                {
                    while (Interlocked.Exchange(ref _dirty, 0) == 1)
                    {
                        try { Volatile.Write(ref _current, (IReadOnlyList<AudioOutput>)_list()); }
                        catch (Exception) { }   // 列不出來就保留上一份
                    }
                }
                finally { Volatile.Write(ref _running, 0); }
                if (Volatile.Read(ref _dirty) == 1) Refresh();   // 剛結束時才進來的通知
            });
        }

        public void Dispose()
        {
            try { if (_enumerator is not null && _notifier is not null) _enumerator.UnregisterEndpointNotificationCallback(_notifier); }
            catch (Exception) { }
            _enumerator?.Dispose();
        }

        sealed class Notifier : NAudio.CoreAudioApi.Interfaces.IMMNotificationClient
        {
            readonly Cache _cache;
            public Notifier(Cache cache) => _cache = cache;
            public void OnDeviceStateChanged(string deviceId, NAudio.CoreAudioApi.DeviceState newState) => _cache.Refresh();
            public void OnDeviceAdded(string pwstrDeviceId) => _cache.Refresh();
            public void OnDeviceRemoved(string deviceId) => _cache.Refresh();
            public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { if (flow == DataFlow.Render) _cache.Refresh(); }
            public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }   // 屬性變動非常頻繁（音量等），不跟著重抓
        }
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class PolicyConfigClient { }

    // 只用到 SetDefaultEndpoint，其他方法照 vtable 順序佔位（順序錯了會呼叫到別的方法）
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string name, nint format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Bool)] bool isDefault, nint format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string name, nint endpointFormat, nint mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Bool)] bool isDefault, nint defaultPeriod, nint minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string name, nint period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string name, nint mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string name, nint mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Bool)] bool fx, nint key, nint value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Bool)] bool fx, nint key, nint value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string name, Role role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Bool)] bool visible);
    }
}
