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

    public VolumeControl(string namePart = "EDIFIER", Func<string, IEndpoint>? opener = null)
    {
        _namePart = namePart;
        _open = opener ?? OpenEndpoint;
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
            _ep ??= _open(_namePart);
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
