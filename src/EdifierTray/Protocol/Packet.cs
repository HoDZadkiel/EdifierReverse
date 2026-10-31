namespace EdifierTray.Protocol;

/// <summary>版本與加密設定，來自 BLE 廣播 Manufacturer Data 的最後兩個位元組。</summary>
public readonly record struct Session(int Version = 2, int Encryption = 0);

public sealed record Packet(byte Header, byte Cmd, byte[] Payload);

public class PacketException : Exception
{
    public PacketException(string message) : base(message) { }
}

/// <summary>指令碼。只列工具用得到的；完整表在 Python 版 commands.py。</summary>
public static class Cmd
{
    public const byte OtaReady = 0x10, OtaStartMaster = 0x11, OtaStartSub = 0x12;   // 韌體更新：永遠封鎖
    public const byte SavingModeQuery = 0xB1, SavingModeSet = 0xB2;   // 省電模式（官方 App 反編譯碼；MP330 實機讀取與設定皆已驗證）
    public const byte AncSet = 0xC1, VersionQuery = 0xC6, NameQuery = 0xC9, AncQuery = 0xCC, BatteryQuery = 0xD0;
}

/// <summary>封包組裝與解析，行為與 Python 版 packet.py 一致（同一組向量在兩邊都測）。</summary>
public static class PacketCodec
{
    public const byte HeaderSend = 0xAA, AppCode = 0xEC, XorKey = 0xA5;
    public const int EncryptionXor = 16, V1CrcBase = 8217;
    static readonly byte[] RecvHeaders = { 0xBB, 0xCC };

    public static bool IsRecvHeader(byte b) => b == 0xBB || b == 0xCC;

    public static byte[] Xor(byte[] payload, int encryption)
    {
        var copy = (byte[])payload.Clone();
        if (encryption != EncryptionXor) return copy;
        for (int i = 0; i < copy.Length; i++) copy[i] ^= XorKey;
        return copy;
    }

    static byte[] Checksum(ReadOnlySpan<byte> data, int version)
    {
        int sum = 0;
        foreach (var b in data) sum += b;
        if (version <= 1)
        {
            int v = (sum + V1CrcBase) & 0xFFFF;
            return new[] { (byte)(v >> 8), (byte)(v & 0xFF) };
        }
        return new[] { (byte)(sum & 0xFF) };
    }

    public static byte[] Build(Session s, byte cmd, byte[] payload, byte header = HeaderSend)
    {
        if (cmd == Cmd.OtaReady || cmd == Cmd.OtaStartMaster || cmd == Cmd.OtaStartSub)
            throw new PacketException($"指令 0x{cmd:X2} 為韌體更新，已封鎖");
        byte[] body;
        if (s.Version <= 1)
        {
            int length = payload.Length + 1;
            if (length > 0xFF) throw new PacketException("v1 封包 payload 過長");
            body = new byte[] { header, (byte)length, cmd }.Concat(payload).ToArray();
        }
        else
        {
            if (payload.Length > 0xFFFF) throw new PacketException("v2 封包 payload 過長");
            body = new byte[] { header, AppCode, cmd, (byte)(payload.Length >> 8), (byte)(payload.Length & 0xFF) }
                .Concat(Xor(payload, s.Encryption)).ToArray();
        }
        return body.Concat(Checksum(body, s.Version)).ToArray();
    }

    /// <summary>依封包開頭算出完整長度；資料還不夠判斷時回傳 null。</summary>
    public static int? ExpectedLength(Session s, ReadOnlySpan<byte> data)
    {
        if (s.Version <= 1) return data.Length >= 2 ? data[1] + 4 : null;
        return data.Length >= 5 ? ((data[3] << 8) | data[4]) + 6 : null;
    }

    /// <summary>解析裝置回應。預設只接受 0xBB / 0xCC；測試模擬裝置時可傳入 0xAA。</summary>
    public static Packet Parse(Session s, byte[] data, byte[]? headers = null)
    {
        headers ??= RecvHeaders;
        var total = ExpectedLength(s, data) ?? throw new PacketException("資料不足，無法判斷長度");
        if (Array.IndexOf(headers, data[0]) < 0) throw new PacketException($"header 錯誤：0x{data[0]:X2}");
        if (data.Length != total) throw new PacketException($"長度不符：預期 {total}，實際 {data.Length}");
        int crcLen = s.Version <= 1 ? 2 : 1;
        var body = data.AsSpan(0, data.Length - crcLen);
        if (!Checksum(body, s.Version).AsSpan().SequenceEqual(data.AsSpan(data.Length - crcLen)))
            throw new PacketException("CRC 錯誤");
        if (s.Version <= 1) return new Packet(data[0], data[2], data[3..^2]);
        if (data[1] != AppCode) throw new PacketException($"appCode 錯誤：0x{data[1]:X2}");
        return new Packet(data[0], data[2], Xor(data[5..^1], s.Encryption));
    }
}

/// <summary>串流重組：通知可能分片，也可能一筆夾帶多包。規則同 Python 版 reassembly.py。</summary>
public sealed class Reassembler
{
    readonly Session _session;
    readonly List<byte> _buf = new();
    public int Dropped { get; private set; }

    public Reassembler(Session session) => _session = session;

    public List<Packet> Feed(byte[] chunk)
    {
        if (chunk.Length > 0 && PacketCodec.IsRecvHeader(chunk[0])) _buf.Clear();
        _buf.AddRange(chunk);
        var output = new List<Packet>();
        while (_buf.Count > 0)
        {
            if (!PacketCodec.IsRecvHeader(_buf[0])) { _buf.Clear(); break; }
            var total = PacketCodec.ExpectedLength(_session, _buf.ToArray());
            if (total is null || _buf.Count < total) break;
            var raw = _buf.GetRange(0, total.Value).ToArray();
            _buf.RemoveRange(0, total.Value);
            try { output.Add(PacketCodec.Parse(_session, raw)); }
            catch (PacketException) { Dropped++; }
        }
        return output;
    }
}
