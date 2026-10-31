using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EdifierTray.Service;

/// <summary>
/// 防止喇叭／耳機因為一段時間沒有播放音訊而自動關機：對指定裝置的播放端點每隔一段時間送出一小段極小的低頻訊號
/// （約 -50 dBFS、25 Hz，喇叭重現不出來所以聽不到）。不碰裝置本身的設定，也不走 Edifier 協定，所以所有機種通用。
/// 只在脈衝期間開著串流，其餘時間關閉（不持續播放，沒充電時不會多耗電）；間隔要小於裝置的待機時間（官方說明 15 分鐘）。
/// 這是推測：裝置的韌體是否把這種訊號當成「有音訊」沒有保證，要實際放著超過它的待機時間才知道。
/// 每次呼叫 <see cref="Reconcile"/> 讓執行中的脈衝與「想保持開機的裝置」一致：裝置的端點出現才開始，不再想保持開機就停掉；
/// 開始之後裝置斷線，脈衝仍在，只是每次找不到端點就略過，重新連上後自動接續。
/// </summary>
public sealed class KeepAwake : IDisposable
{
    public const float Amplitude = 0.003f;   // 約 -50 dBFS
    public const float Frequency = 25f;      // Hz，低於小喇叭能重現的範圍
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);   // 兩次脈衝的間隔，要小於裝置的待機時間（15 分鐘）
    public static readonly TimeSpan Burst = TimeSpan.FromSeconds(5);      // 每次脈衝長度，要夠藍牙串流起來並被韌體偵測到（未驗證）

    readonly Func<string, IDisposable?> _start;
    readonly Dictionary<string, IDisposable> _running = new(StringComparer.OrdinalIgnoreCase);
    readonly object _lock = new();

    /// <param name="start">依名稱片段（例如 "MP330"）開始送訊號，找不到作用中的端點回傳 null。測試可注入。</param>
    public KeepAwake(Func<string, IDisposable?>? start = null) => _start = start ?? StartStream;

    public IReadOnlyCollection<string> Running { get { lock (_lock) return _running.Keys.ToList(); } }

    /// <summary>裝置名稱 → 端點名稱片段：去掉 "EDIFIER " 前綴（端點叫「喇叭 (EDIFIER MP330)」），沒有名稱就用機種名稱。</summary>
    public static string NamePart(KnownDevice d)
    {
        var name = d.Name.StartsWith("EDIFIER ", StringComparison.OrdinalIgnoreCase) ? d.Name[8..] : d.Name;
        return name != "" ? name : d.Model != Catalog.GenericModel ? d.Model : "";
    }

    public void Reconcile(IEnumerable<string> wanted)
    {
        var want = wanted.Where(n => n != "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            foreach (var name in _running.Keys.Where(n => !want.Contains(n)).ToList()) Stop(name);
            foreach (var name in want)
            {
                if (_running.ContainsKey(name)) continue;
                try
                {
                    if (_start(name) is { } stream) _running[name] = stream;   // 端點還不存在（裝置沒連上）就下次再試
                }
                catch (Exception) { /* 端點被獨占或剛好失效：下次 Reconcile 再試 */ }
            }
        }
    }

    void Stop(string name)
    {
        if (!_running.Remove(name, out var s)) return;
        try { s.Dispose(); } catch (Exception) { }
    }

    public void Dispose()
    {
        lock (_lock) foreach (var name in _running.Keys.ToList()) Stop(name);
    }

    /// <summary>低頻小振幅正弦波，格式跟隨端點的混音格式。</summary>
    sealed class Tone : ISampleProvider
    {
        readonly int _channels, _rate;
        double _phase;
        public Tone(WaveFormat mix) { _channels = mix.Channels; _rate = mix.SampleRate; WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_rate, _channels); }
        public WaveFormat WaveFormat { get; }
        public int Read(float[] buffer, int offset, int count)
        {
            var step = 2 * Math.PI * Frequency / _rate;
            for (int i = 0; i < count; i += _channels)
            {
                var v = (float)(Math.Sin(_phase) * Amplitude);
                _phase += step;
                if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
                for (int c = 0; c < _channels; c++) buffer[offset + i + c] = v;
            }
            return count;
        }
    }

    sealed class Stream : IDisposable
    {
        readonly MMDevice _device;
        readonly WasapiOut _out;
        public Stream(MMDevice device)
        {
            _device = device;
            _out = new WasapiOut(device, AudioClientShareMode.Shared, true, 200);
            _out.Init(new Tone(device.AudioClient.MixFormat));
            _out.Play();
        }
        public void Dispose() { _out.Dispose(); _device.Dispose(); }
    }

    /// <summary>每隔 <c>every</c> 開一次串流、開著 <c>burst</c> 再關掉；第一次立刻開。Dispose 時停止並關掉進行中的串流。</summary>
    public sealed class Pulse : IDisposable
    {
        readonly CancellationTokenSource _cts = new();

        public Pulse(Func<IDisposable?> open, TimeSpan every, TimeSpan burst)
        {
            if (burst <= TimeSpan.Zero || burst >= every) throw new ArgumentOutOfRangeException(nameof(burst), "脈衝長度必須大於 0 且小於間隔");
            _ = Run(open, every, burst, _cts.Token);
        }

        static async Task Run(Func<IDisposable?> open, TimeSpan every, TimeSpan burst, CancellationToken ct)
        {
            try
            {
                while (true)
                {
                    IDisposable? stream = null;
                    try
                    {
                        stream = open();   // 端點暫時不在或被獨占：略過這次，下個間隔再試
                        await Task.Delay(burst, ct);
                    }
                    catch (Exception e) when (e is not OperationCanceledException) { }
                    finally { try { stream?.Dispose(); } catch (Exception) { } }
                    await Task.Delay(every - burst, ct);
                }
            }
            catch (OperationCanceledException) { }
        }

        public void Dispose() => _cts.Cancel();
    }

    static IDisposable? StartStream(string namePart)
    {
        using (var probe = FindEndpoint(namePart))
            if (probe is null) return null;   // 端點不在（裝置沒連上）：讓 Reconcile 下次再試
        return new Pulse(() => FindEndpoint(namePart) is { } d ? OpenStream(d) : null, Interval, Burst);
    }

    static MMDevice? FindEndpoint(string namePart)
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active))
        {
            if (d.FriendlyName.Contains(namePart, StringComparison.OrdinalIgnoreCase) && AudioOutputs.IsListable(d.FriendlyName)) return d;
            d.Dispose();
        }
        return null;
    }

    static Stream OpenStream(MMDevice d)
    {
        try { return new Stream(d); }
        catch { d.Dispose(); throw; }
    }
}
