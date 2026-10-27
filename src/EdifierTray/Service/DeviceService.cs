using System.Text;
using EdifierTray.Protocol;
using EdifierTray.Transport;

namespace EdifierTray.Service;

public sealed record AncState(int? Group, int Mode, int? Db = null)
{
    /// <summary>v2 回應／推送：[group, mode, db?]。</summary>
    public static AncState Parse(byte[] payload)
    {
        if (payload.Length < 2) throw new PacketException("ANC 回應過短");
        return new AncState(payload[0], payload[1], payload.Length > 2 ? payload[2] : null);
    }
}

public sealed record DeviceState(
    bool Connected = false, string Name = "", string Version = "", int? Battery = null,
    AncState? Anc = null, string Error = "", bool Cooling = false)
{
    public bool AncSupported => Anc?.Group is int g && DeviceService.AncModes.ContainsKey(g);

    public string AncLabel
    {
        get
        {
            if (Anc is null) return Strings.T("anc.unknown");
            if (Anc.Group is int g && DeviceService.AncModes.TryGetValue(g, out var modes))
                return modes.Contains(Anc.Mode) ? Strings.T($"anc.n.{Anc.Mode}") : Strings.T("anc.mode_n", new { m = Anc.Mode });
            return Strings.T("anc.unverified", new { g = Anc.Group, m = Anc.Mode });
        }
    }
}

public class ServiceException : Exception
{
    public string Key { get; }

    /// <summary>訊息以字串表的鍵產生（依建立當下的介面語言），介面直接顯示 Message。</summary>
    public ServiceException(string key, object? args = null) : base(Strings.T(key, args)) { Key = key; }
}

public delegate Task<(ITransport Transport, Session Session)> Connector(CancellationToken ct);

/// <summary>
/// 裝置服務：連線、狀態、ANC 設定的節流與重送、斷線重連。規則來自 ES850NB 實機驗證：
/// 兩次 ANC 設定間隔 ≤ 1.0 秒時第二次會被裝置靜默忽略，所以設定一律經單一鎖與節流（預設 2.0 秒）；
/// 設定成功後裝置會主動推送 0xCC。只送查詢與 0xC1（ANC）。
/// </summary>
public sealed class DeviceService
{
    /// <summary>只有實機驗證過的 group 才開放設定。</summary>
    /// <summary>模式名稱在 strings.json（anc.n.&lt;mode&gt;），依介面語言顯示。</summary>
    public static readonly IReadOnlyDictionary<int, IReadOnlyList<int>> AncModes =
        new Dictionary<int, IReadOnlyList<int>> { [16] = new[] { 1, 2, 3, 4, 5 } };
    public static readonly IReadOnlyDictionary<int, int> AmbientMode = new Dictionary<int, int> { [16] = 4 };
    public const int DbMin = 1, DbMax = 12;  // 實機驗證過 1、3、9、12

    readonly Connector _connector;
    readonly TimeSpan _minSetInterval, _pollInterval, _setTimeout, _queryTimeout;
    readonly TimeSpan[] _backoff;
    readonly SemaphoreSlim _setLock = new(1, 1);
    readonly object _stateLock = new();
    CancellationTokenSource? _cts;
    Task? _loop;
    Client? _client;
    long _lastSetMs = long.MinValue / 2;  // Environment.TickCount64（毫秒）
    int _pendingSets;
    CancellationTokenSource? _cooldownCts;
    TaskCompletionSource _interrupt = NewSignal();

    public DeviceState State { get; private set; } = new();
    public event Action<DeviceState>? StateChanged;

    public DeviceService(Connector connector, TimeSpan? minSetInterval = null, TimeSpan? pollInterval = null,
                         TimeSpan? setTimeout = null, TimeSpan? queryTimeout = null, TimeSpan[]? backoff = null)
    {
        _connector = connector;
        _minSetInterval = minSetInterval ?? TimeSpan.FromSeconds(2.0);
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(60);
        _setTimeout = setTimeout ?? TimeSpan.FromSeconds(3);
        _queryTimeout = queryTimeout ?? TimeSpan.FromSeconds(3);
        _backoff = backoff ?? new[] { 1.0, 2.0, 5.0, 10.0, 30.0 }.Select(TimeSpan.FromSeconds).ToArray();
    }

    static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ---- 生命週期 ----
    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        _cooldownCts?.Cancel();
        if (_loop is null) return;
        _cts!.Cancel();
        try { await _loop; } catch (OperationCanceledException) { }
        _loop = null;
    }

    public async Task<DeviceState> WaitConnectedAsync(TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (!State.Connected)
        {
            if (DateTime.UtcNow >= end)
                throw new ServiceException("e.wait_timeout", new { err = State.Error == "" ? Strings.T("e.not_connected") : State.Error });
            await Task.Delay(50);
        }
        return State;
    }

    /// <summary>要求立刻重連（略過退避等待）。</summary>
    public void Reconnect() => _interrupt.TrySetResult();

    // ---- 狀態 ----
    void Update(Func<DeviceState, DeviceState> change)
    {
        DeviceState s;
        lock (_stateLock) { s = State = change(State); }
        StateChanged?.Invoke(s);
    }

    void OnEvent(Packet pkt)
    {
        if (pkt.Cmd != Cmd.AncQuery) return;  // 裝置主動推送（含別處切換，如耳機按鍵）
        try { var anc = AncState.Parse(pkt.Payload); Update(s => s with { Anc = anc }); }
        catch (PacketException) { }
    }

    async Task<byte[]> Ask(byte cmd) =>
        (await _client!.RequestAsync(cmd, null, _queryTimeout)).Payload;

    /// <summary>查詢並解析；機種不支援（沒有回應或格式不符）時回傳 null，不當成連線失敗。
    /// 目錄內的其他機種不一定有 ANC 或同樣的電量指令，只讀查詢逐項容錯才能連上它們。</summary>
    async Task<T?> Optional<T>(byte cmd, Func<byte[], T> parse) where T : class
    {
        try { return parse(await Ask(cmd)); }
        catch (Exception e) when (e is ResponseTimeoutException or PacketException) { return null; }
    }

    async Task RefreshStatic()
    {
        var name = await Optional(Cmd.NameQuery, p => Encoding.UTF8.GetString(p));
        var ver = await Optional(Cmd.VersionQuery, p => string.Join('.', p.Select(b => ((sbyte)b).ToString())));
        if (name is null && ver is null) throw new ResponseTimeoutException(Cmd.NameQuery);
        Update(s => s with { Name = name ?? "", Version = ver ?? "" });
    }

    async Task Refresh()
    {
        int? battery = null;
        try { battery = (sbyte)(await Ask(Cmd.BatteryQuery)).FirstOrDefault(); }
        catch (ResponseTimeoutException) { }
        var anc = await Optional(Cmd.AncQuery, AncState.Parse);
        if (battery is null && anc is null) throw new ResponseTimeoutException(Cmd.BatteryQuery);  // 兩個都沒回應才視為連線已壞
        Update(s => s with { Battery = battery, Anc = anc });
    }

    // ---- 主迴圈 ----
    async Task RunAsync(CancellationToken ct)
    {
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            ITransport? transport = null;
            var interrupt = _interrupt = NewSignal();
            try
            {
                var (t, session) = await _connector(ct);
                transport = t;
                t.Closed += () => interrupt.TrySetResult();
                var client = _client = new Client(t, session);
                client.EventReceived += OnEvent;
                await RefreshStatic();
                await Refresh();
                Update(s => s with { Connected = true, Error = "" });
                failures = 0;
                while (true)
                {
                    var done = await Task.WhenAny(interrupt.Task, Task.Delay(_pollInterval, ct));
                    ct.ThrowIfCancellationRequested();  // WhenAny 不會拋出取消；沒有這行，停止後會無限輪詢
                    if (done == interrupt.Task) throw new IOException("連線中斷");
                    await Refresh();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                Update(s => s with { Connected = false, Error = $"{e.GetType().Name}: {e.Message}" });
            }
            finally
            {
                _client = null;
                transport?.Dispose();
            }
            var delay = _backoff[Math.Min(failures++, _backoff.Length - 1)];
            await Task.WhenAny(interrupt.Task, Task.Delay(delay, ct));
            if (ct.IsCancellationRequested) break;
        }
    }

    // ---- 設定 ----
    /// <summary>切換 ANC 模式。所有設定經同一把鎖與節流；逾時（被忽略或封包遺失）重送一次。</summary>
    public async Task<AncState> SetAncAsync(int mode, int? db = null)
    {
        var anc = State.Anc;
        if (!State.Connected || _client is null) throw new ServiceException("e.not_connected");
        if (anc?.Group is not int group || !AncModes.TryGetValue(group, out var modes))
            throw new ServiceException("e.unverified_group", new { g = anc?.Group.ToString() ?? "None" });
        if (!modes.Contains(mode)) throw new ServiceException("e.no_mode", new { g = group, m = mode });
        if (db is int d)
        {
            if (!AmbientMode.TryGetValue(group, out var amb) || mode != amb) throw new ServiceException("e.ambient_only");
            if (d < DbMin || d > DbMax) throw new ServiceException("e.strength_range", new { lo = DbMin, hi = DbMax });
        }
        // 官方 App 只在 db > 0 時才附帶
        var payload = db is int dv && dv > 0 ? new[] { (byte)group, (byte)mode, (byte)dv } : new[] { (byte)group, (byte)mode };

        // 冷卻期：從第一個設定進來，到最後一個設定完成後再過 minSetInterval 為止（介面據此停用切換）
        Interlocked.Increment(ref _pendingSets);
        _cooldownCts?.Cancel();
        if (!State.Cooling) Update(st => st with { Cooling = true });
        try { return await SendAncAsync(payload); }
        finally
        {
            if (Interlocked.Decrement(ref _pendingSets) == 0) StartCooldownEnd();
        }
    }

    void StartCooldownEnd()
    {
        var delayMs = Math.Max(0, _lastSetMs + (long)_minSetInterval.TotalMilliseconds - Environment.TickCount64);
        var cts = _cooldownCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay((int)delayMs, cts.Token);
                if (Volatile.Read(ref _pendingSets) == 0) Update(st => st with { Cooling = false });
            }
            catch (OperationCanceledException) { }
        });
    }

    async Task<AncState> SendAncAsync(byte[] payload)
    {
        await _setLock.WaitAsync();
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var waitMs = _lastSetMs + (long)_minSetInterval.TotalMilliseconds - Environment.TickCount64;
                if (waitMs > 0) await Task.Delay((int)waitMs);
                var client = _client ?? throw new ServiceException("e.conn_lost_plain");
                try
                {
                    var resp = await client.RequestAsync(Cmd.AncSet, payload, _setTimeout);
                    _lastSetMs = Environment.TickCount64;
                    var now = AncState.Parse(resp.Payload);
                    Update(s => s with { Anc = now });
                    return now;
                }
                catch (ResponseTimeoutException)
                {
                    _lastSetMs = Environment.TickCount64;
                    if (attempt == 1) throw new ServiceException("e.no_response");
                }
                catch (IOException e)
                {
                    Reconnect();
                    throw new ServiceException("e.conn_lost", new { err = e.Message });
                }
            }
            throw new ServiceException("e.set_failed");  // 不會到這裡，保險
        }
        finally { _setLock.Release(); }
    }
}
