using System.Runtime.InteropServices;

namespace EdifierTray.Transport;

public interface ITransport : IDisposable
{
    event Action<byte[]>? DataReceived;
    /// <summary>對端主動斷線時觸發（自己 Dispose 不會）。</summary>
    event Action? Closed;
    Task WriteAsync(byte[] data);
}

/// <summary>
/// SPP（經典藍牙 RFCOMM）傳輸。做法與 Python 版 spp.py 相同：直接呼叫 Winsock 的 SOCKADDR_BTH，
/// serviceClassId 填 SPP UUID、port 填 0 讓系統做 SDP 查詢；失敗再直連 channel 1（官方 App 的順序）。
/// </summary>
public sealed class RfcommTransport : ITransport
{
    public const string SppUuid = "EDF00000-EDFE-DFED-FEDF-EDFEDFEDFEDF";
    const int AfBth = 32, SockStream = 1, BthProtoRfcomm = 3, ReadSize = 1024;

    public event Action<byte[]>? DataReceived;
    public event Action? Closed;
    /// <summary>這條連線是怎麼建立的（UUID 查詢或 channel 1），紀錄用。</summary>
    public string How { get; private set; } = "";

    nint _sock;
    volatile bool _closed;

    RfcommTransport(nint sock, string how) { _sock = sock; How = how; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct SockaddrBth
    {
        public ushort AddressFamily;
        public ulong BtAddr;
        public Guid ServiceClassId;
        public uint Port;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WsaData
    {
        public ushort Version, HighVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Description;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] public string SystemStatus;
        public ushort MaxSockets, MaxUdpDg;
        public nint VendorInfo;
    }

    [DllImport("ws2_32.dll")] static extern int WSAStartup(ushort version, out WsaData data);
    [DllImport("ws2_32.dll")] static extern nint socket(int af, int type, int protocol);
    [DllImport("ws2_32.dll")] static extern int connect(nint s, ref SockaddrBth name, int namelen);
    [DllImport("ws2_32.dll")] static extern int send(nint s, byte[] buf, int len, int flags);
    [DllImport("ws2_32.dll")] static extern int recv(nint s, byte[] buf, int len, int flags);
    [DllImport("ws2_32.dll")] static extern int shutdown(nint s, int how);
    [DllImport("ws2_32.dll")] static extern int closesocket(nint s);
    [DllImport("ws2_32.dll")] static extern int WSAGetLastError();

    /// <summary>Winsock 位址結構大小（應為 30，與 Python 版一致），測試用。</summary>
    public static int AddressStructSize => Marshal.SizeOf<SockaddrBth>();

    static readonly object InitLock = new();
    static bool _wsaReady;

    static void EnsureWinsock()
    {
        lock (InitLock)
        {
            if (_wsaReady) return;
            WSAStartup(0x0202, out _);
            _wsaReady = true;
        }
    }

    /// <summary>"AA:BB:CC:11:22:33" 轉成 48 位元位址。</summary>
    public static ulong ParseAddress(string address)
    {
        var parts = address.Split(':');
        if (parts.Length != 6 || parts.Any(p => p.Length != 2)) throw new ArgumentException($"藍牙位址格式錯誤：{address}");
        return Convert.ToUInt64(string.Concat(parts), 16);
    }

    static SockaddrBth MakeAddr(ulong addr, Guid? service, uint channel) =>
        new() { AddressFamily = AfBth, BtAddr = addr, ServiceClassId = service ?? Guid.Empty, Port = channel };

    /// <summary>連線，逾時秒數內沒連上就中止。阻塞的 connect 放在背景工作，逾時時關閉 socket 讓它返回。</summary>
    static async Task<nint> TryConnect(ulong addr, Guid? service, uint channel, TimeSpan timeout)
    {
        EnsureWinsock();
        var s = socket(AfBth, SockStream, BthProtoRfcomm);
        if (s == -1 || s == 0) throw new IOException($"建立 RFCOMM socket 失敗（WinSock {WSAGetLastError()}）");
        var sa = MakeAddr(addr, service, channel);
        var task = Task.Run(() =>
        {
            var local = sa;
            return connect(s, ref local, Marshal.SizeOf<SockaddrBth>()) == 0 ? 0 : WSAGetLastError();
        });
        if (await Task.WhenAny(task, Task.Delay(timeout)) != task)
        {
            closesocket(s);  // 讓卡住的 connect 返回
            throw new TimeoutException("RFCOMM 連線逾時");
        }
        var err = await task;
        if (err != 0) { closesocket(s); throw new IOException($"WinSock 錯誤碼 {err}"); }
        return s;
    }

    public static async Task<RfcommTransport> ConnectAsync(string a2dpAddress, int? channel = null,
                                                          TimeSpan? timeout = null)
    {
        var addr = ParseAddress(a2dpAddress);
        var to = timeout ?? TimeSpan.FromSeconds(10);
        var errors = new List<string>();
        if (channel is null)
        {
            try { return new RfcommTransport(await TryConnect(addr, Guid.Parse(SppUuid), 0, to), $"UUID 查詢 {SppUuid}").Start(); }
            catch (Exception e) when (e is IOException or TimeoutException) { errors.Add($"UUID 查詢：{e.Message}"); }
        }
        var ch = (uint)(channel ?? 1);
        try { return new RfcommTransport(await TryConnect(addr, null, ch, to), $"直連 channel {ch}").Start(); }
        catch (Exception e) when (e is IOException or TimeoutException) { errors.Add($"channel {ch}：{e.Message}"); }
        throw new IOException("RFCOMM 連線失敗：" + string.Join("；", errors));
    }

    RfcommTransport Start()
    {
        new Thread(ReadLoop) { IsBackground = true, Name = "rfcomm-reader" }.Start();
        return this;
    }

    void ReadLoop()
    {
        var buf = new byte[ReadSize];
        try
        {
            while (!_closed)
            {
                int n = recv(_sock, buf, buf.Length, 0);
                if (n <= 0) break;  // 0：對端關閉；負值：錯誤
                DataReceived?.Invoke(buf.AsSpan(0, n).ToArray());
            }
        }
        finally
        {
            if (!_closed) Closed?.Invoke();
        }
    }

    public Task WriteAsync(byte[] data) => Task.Run(() =>
    {
        if (_closed) throw new IOException("SPP 連線已關閉");
        int sent = 0;
        while (sent < data.Length)
        {
            var chunk = sent == 0 ? data : data[sent..];
            int n = send(_sock, chunk, chunk.Length, 0);
            if (n <= 0) throw new IOException($"傳送失敗（WinSock {WSAGetLastError()}）");
            sent += n;
        }
    });

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        shutdown(_sock, 2);
        closesocket(_sock);
    }
}
