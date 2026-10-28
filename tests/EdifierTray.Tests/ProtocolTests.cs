using EdifierTray.Protocol;
using EdifierTray.Service;
using EdifierTray.Transport;
using EdifierTray.Ui;
using Xunit;

namespace EdifierTray.Tests;

public class ProtocolTests
{
    static readonly Session V2 = new(2, 0), V1 = new(1, 0), V2Xor = new(2, 16);
    static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    // 向量與 Python 版 tests/test_packet.py 相同（皆為手算），兩邊都要過
    [Fact] public void Build_v2_query_matches_hand_computed_bytes() =>
        Assert.Equal(Hex("AA EC CC 00 00 62"), PacketCodec.Build(V2, Cmd.AncQuery, Array.Empty<byte>()));

    [Fact] public void Build_v1_query_adds_8217_to_checksum() =>
        Assert.Equal(Hex("AA 01 CC 21 90"), PacketCodec.Build(V1, Cmd.AncQuery, Array.Empty<byte>()));

    [Fact] public void Parse_v2_battery_response() =>
        Assert.Equal(new byte[] { 0x50 }, PacketCodec.Parse(V2, Hex("BB EC D0 00 01 50 C8")).Payload);

    [Fact] public void Xor_is_symmetric_and_only_for_encryption_16()
    {
        var data = new byte[] { 0x00, 0x50, 0xFF };
        Assert.Equal(data, PacketCodec.Xor(PacketCodec.Xor(data, 16), 16));
        Assert.Equal(new byte[] { 0xA5, 0xF5, 0x5A }, PacketCodec.Xor(data, 16));
        Assert.Equal(data, PacketCodec.Xor(data, 0));
        Assert.Equal(data, PacketCodec.Xor(data, 99));  // 未知加密碼原樣放行（官方 App 同樣）
    }

    [Fact] public void Crc_covers_the_encrypted_payload_not_the_plain_one()
    {
        Assert.Equal(new byte[] { 0x50 }, PacketCodec.Parse(V2Xor, Hex("BB EC D0 00 01 F5 6D")).Payload);
        var ex = Assert.Throws<PacketException>(() => PacketCodec.Parse(V2Xor, Hex("BB EC D0 00 01 F5 C8")));
        Assert.Contains("CRC", ex.Message);
    }

    // 以下是 ES850NB 實機抓到的封包（encryption=16），C# 組出的位元組必須與實機送出的逐位元組相同
    [Fact] public void Anc_set_matches_the_bytes_sent_to_the_real_headphones() =>
        Assert.Equal(Hex("aa ec c1 00 02 b5 a7 b5"), PacketCodec.Build(V2Xor, Cmd.AncSet, new byte[] { 16, 2 }));

    [Fact] public void Anc_set_back_to_mode_1_matches_the_real_capture() =>
        Assert.Equal(Hex("aa ec c1 00 02 b5 a4 b2"), PacketCodec.Build(V2Xor, Cmd.AncSet, new byte[] { 16, 1 }));

    [Theory] [InlineData(Cmd.OtaReady)] [InlineData(Cmd.OtaStartMaster)] [InlineData(Cmd.OtaStartSub)]
    public void Firmware_update_commands_are_blocked(byte cmd) =>
        Assert.Throws<PacketException>(() => PacketCodec.Build(V2, cmd, Array.Empty<byte>()));

    [Fact] public void Parse_rejects_the_send_header_and_wrong_length()
    {
        Assert.Throws<PacketException>(() => PacketCodec.Parse(V2, Hex("AA EC D0 00 01 50 C7")));
        Assert.Throws<PacketException>(() => PacketCodec.Parse(V2, Hex("BB EC D0 00 02 50 C8")));
    }

    [Fact] public void Roundtrip_with_encryption()
    {
        var wire = PacketCodec.Build(V2Xor, Cmd.AncSet, new byte[] { 5, 2 }, header: 0xBB);
        Assert.Equal(new byte[] { 5, 2 }, PacketCodec.Parse(V2Xor, wire).Payload);
    }
}

public class ReassemblerTests
{
    static readonly Session S = new(2, 16);
    static byte[] Resp(byte cmd, params byte[] p) => PacketCodec.Build(S, cmd, p, header: 0xBB);

    [Theory] [InlineData(1)] [InlineData(3)] [InlineData(100)]
    public void Fragmented_notifications_are_reassembled(int chunk)
    {
        // 不用 0xCC 當測試指令：它同時是接收 header，逐位元組分片時會被誤判為新封包（已知限制，見 reassembly.py 說明）
        var wire = Resp(Cmd.BatteryQuery, 16, 3);
        var r = new Reassembler(S);
        var got = new List<Packet>();
        for (int i = 0; i < wire.Length; i += chunk) got.AddRange(r.Feed(wire[i..Math.Min(wire.Length, i + chunk)]));
        Assert.Single(got);
        Assert.Equal(new byte[] { 16, 3 }, got[0].Payload);
    }

    [Fact] public void Two_packets_in_one_notification_are_split()
    {
        var both = Resp(Cmd.BatteryQuery, 80).Concat(Resp(Cmd.AncQuery, 16, 1)).ToArray();
        var got = new Reassembler(S).Feed(both);
        Assert.Equal(new[] { Cmd.BatteryQuery, Cmd.AncQuery }, got.Select(p => p.Cmd));
    }

    [Fact] public void Corrupt_packet_is_dropped_and_counted_but_the_next_one_survives()
    {
        var bad = Resp(Cmd.BatteryQuery, 80); bad[^1] ^= 0xFF;
        var r = new Reassembler(S);
        var got = r.Feed(bad.Concat(Resp(Cmd.AncQuery, 16, 1)).ToArray());
        Assert.Single(got);
        Assert.Equal(1, r.Dropped);
    }

    [Fact] public void Data_not_starting_with_a_header_is_discarded() =>
        Assert.Empty(new Reassembler(S).Feed(new byte[] { 0x01, 0x02, 0x03 }));
}

public class DiscoveryTests
{
    [Fact] public void Real_advertisement_from_the_headphones_is_parsed()
    {
        // 實機掃描：manufacturer 0x07E0: aa bb cc 11 22 33 02 10
        var f = Discovery.ParseManufacturerData(Convert.FromHexString("aabbcc1122330210"))!;
        Assert.Equal("AA:BB:CC:11:22:33", f.A2dpAddress);
        Assert.Equal(new Session(2, 16), f.Session);
    }

    [Fact] public void Too_short_data_is_rejected() => Assert.Null(Discovery.ParseManufacturerData(new byte[7]));

    [Fact] public void Bluetooth_address_parsing()
    {
        Assert.Equal(0xAABBCC112233UL, RfcommTransport.ParseAddress("AA:BB:CC:11:22:33"));
        Assert.Throws<ArgumentException>(() => RfcommTransport.ParseAddress("AABBCC112233"));
        Assert.Throws<ArgumentException>(() => RfcommTransport.ParseAddress("AA:BB:CC:11:22"));
    }

    [Fact] public void Winsock_address_struct_is_30_bytes_like_the_python_version() =>
        Assert.Equal(30, RfcommTransport.AddressStructSize);
}

public class UiHelperTests
{
    [Theory]
    [InlineData(100, 108, 203, 95)] [InlineData(50, 108, 203, 95)] [InlineData(49, 255, 170, 40)]
    [InlineData(20, 255, 170, 40)] [InlineData(19, 255, 99, 97)] [InlineData(0, 255, 99, 97)]
    public void Ring_color_follows_battery_thresholds(int battery, int r, int g, int b)
    {
        var c = LogoRenderer.RingColor(battery, true);
        Assert.Equal((r, g, b), ((int)c.R, (int)c.G, (int)c.B));
    }

    [Fact] public void Disconnected_ring_is_gray_regardless_of_old_battery()
    {
        Assert.Equal(120, LogoRenderer.RingColor(90, false).R);
        Assert.Equal(120, LogoRenderer.RingColor(null, true).R);
    }

    [Fact] public void Logo_corners_are_transparent_and_size_is_respected()
    {
        using var bmp = LogoRenderer.Render(64, 50, true);
        Assert.Equal(64, bmp.Width);
        Assert.Equal(0, bmp.GetPixel(0, 0).A);
    }

    static DeviceState Conn(int mode, int? db = null) =>
        new(true, "EDIFIER ES850NB", "1.0.1", 80, new AncState(16, mode, db));

    [Fact] public void Tooltip_shows_strength_only_in_ambient_mode()
    {
        Assert.Contains("電量 80%", TrayHost.Tooltip(Conn(1)));
        Assert.Contains("強度 8", TrayHost.Tooltip(Conn(4, 8)));
        Assert.DoesNotContain("強度", TrayHost.Tooltip(Conn(2, 6)));
        Assert.StartsWith("Edifier：未連線", TrayHost.Tooltip(new DeviceState()));           // 沒有裝置資訊時不假設是哪個機種
        Assert.StartsWith("ES850NB：未連線", TrayHost.Tooltip(new DeviceState(), "ES850NB"));  // 有選用裝置時用它的機種／名稱
    }
}

