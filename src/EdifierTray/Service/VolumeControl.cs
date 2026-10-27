using NAudio.CoreAudioApi;

namespace EdifierTray.Service;

public interface IEndpoint
{
    float Scalar { get; set; }
    bool Mute { get; set; }
}

/// <summary>
/// 耳機音量：走 Windows 音訊端點（NAudio），不是 Edifier 協定（耳機對協定的音量查詢沒有回應）。
/// 只調整名稱含 ES850NB 的作用中「播放」端點，排除 Hands-Free（通話用）；不動預設輸出裝置。
/// 端點會快取；操作失敗（例如耳機斷線後端點失效）會重新尋找並重試一次。
/// </summary>
public sealed class VolumeControl
{
    string _namePart;
    readonly Func<string, IEndpoint> _open;
    readonly Func<IEndpoint>? _fallback;
    IEndpoint? _ep;

    sealed class NAudioEndpoint : IEndpoint
    {
        readonly MMDevice _device;
        public NAudioEndpoint(MMDevice d) => _device = d;
        public float Scalar { get => _device.AudioEndpointVolume.MasterVolumeLevelScalar; set => _device.AudioEndpointVolume.MasterVolumeLevelScalar = value; }
        public bool Mute { get => _device.AudioEndpointVolume.Mute; set => _device.AudioEndpointVolume.Mute = value; }
    }

    public static IEndpoint OpenEndpoint(string namePart)
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active))
        {
            var name = d.FriendlyName;
            if (name.Contains(namePart) && !name.Contains("Hands-Free")) return new NAudioEndpoint(d);
        }
        throw new ServiceException("e.vol_endpoint", new { name = namePart });
    }

    /// <summary>系統預設輸出裝置的音量端點。耳機沒連上時的備援，讓音量按鈕仍可調整目前使用中的輸出。</summary>
    public static IEndpoint OpenDefaultEndpoint()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return new NAudioEndpoint(enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia));
        }
        catch (Exception) { throw new ServiceException("e.vol_access"); }
    }

    /// <summary>找不到耳機端點時改用 fallback（預設是系統預設輸出裝置；自訂 opener 時預設不備援）。</summary>
    public VolumeControl(string namePart = "EDIFIER", Func<string, IEndpoint>? opener = null,
                         Func<IEndpoint>? fallback = null)
    {
        _namePart = namePart;
        _open = opener ?? OpenEndpoint;
        _fallback = fallback ?? (opener is null ? OpenDefaultEndpoint : null);
    }

    /// <summary>丟掉快取的端點。耳機連上或斷線時呼叫，讓下次操作重新挑耳機端點或備援端點。</summary>
    public void Reset() => _ep = null;

    IEndpoint Find()
    {
        try { return _open(_namePart); }
        catch (ServiceException) when (_fallback is not null) { return _fallback(); }
    }

    /// <summary>換成另一台裝置的播放端點（名稱含 namePart）。多台 Edifier 裝置時，依目前連線裝置的名稱挑對的那一個。</summary>
    public void SetTarget(string namePart)
    {
        if (namePart == "" || namePart == _namePart) return;
        _namePart = namePart;
        _ep = null;
    }

    T Call<T>(Func<IEndpoint, T> fn)
    {
        for (int attempt = 0; ; attempt++)
        {
            _ep ??= Find();
            try { return fn(_ep); }
            catch (ServiceException) { throw; }
            catch (Exception) when (attempt == 0) { _ep = null; }  // 端點失效，重新尋找後重試
            catch (Exception) { _ep = null; throw new ServiceException("e.vol_access"); }
        }
    }

    public int Get() => Call(ep => (int)Math.Round(ep.Scalar * 100));

    /// <summary>設定音量，超出 0–100 時夾在範圍內；回傳實際設定值。</summary>
    public int Set(int percent)
    {
        var p = Math.Clamp(percent, 0, 100);
        Call(ep => { ep.Scalar = p / 100f; return 0; });
        return p;
    }

    public bool Muted() => Call(ep => ep.Mute);
    public void SetMute(bool mute) => Call(ep => { ep.Mute = mute; return 0; });
}
