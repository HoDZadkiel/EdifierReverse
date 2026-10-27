using EdifierTray.Protocol;
using EdifierTray.Transport;

namespace EdifierTray.Service;

public sealed class ResponseTimeoutException : TimeoutException
{
    public ResponseTimeoutException(byte cmd) : base($"指令 0x{cmd:X2} 逾時") { }
}

/// <summary>請求／回應客戶端：以指令碼比對回應；沒有對應請求的封包當作裝置主動推送。不做重送（重送在 DeviceService）。</summary>
public sealed class Client
{
    readonly ITransport _transport;
    readonly Reassembler _reassembler;
    readonly Dictionary<byte, TaskCompletionSource<Packet>> _waiting = new();
    readonly object _lock = new();

    public Session Session { get; }
    /// <summary>沒有對應請求的封包（例如耳機按鍵切換 ANC 後的推送）。</summary>
    public event Action<Packet>? EventReceived;

    public Client(ITransport transport, Session session)
    {
        _transport = transport;
        Session = session;
        _reassembler = new Reassembler(session);
        transport.DataReceived += OnData;
    }

    void OnData(byte[] chunk)
    {
        foreach (var pkt in _reassembler.Feed(chunk))
        {
            TaskCompletionSource<Packet>? tcs;
            lock (_lock) { _waiting.Remove(pkt.Cmd, out tcs); }
            if (tcs is not null) tcs.TrySetResult(pkt);
            else EventReceived?.Invoke(pkt);
        }
    }

    public async Task<Packet> RequestAsync(byte cmd, byte[]? payload = null, TimeSpan? timeout = null)
    {
        var tcs = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            if (!_waiting.TryAdd(cmd, tcs)) throw new InvalidOperationException($"指令 0x{cmd:X2} 已有請求在等待回應");
        }
        try
        {
            await _transport.WriteAsync(PacketCodec.Build(Session, cmd, payload ?? Array.Empty<byte>()));
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout ?? TimeSpan.FromSeconds(3)));
            if (done != tcs.Task) throw new ResponseTimeoutException(cmd);
            return await tcs.Task;
        }
        finally
        {
            lock (_lock) { _waiting.Remove(cmd); }
        }
    }
}
