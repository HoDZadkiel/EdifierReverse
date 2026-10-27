using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using EdifierTray.Service;
using EdifierTray.Ui;

namespace EdifierTray;

/// <summary>
/// 命令列模式（驗證與自動化用）：
///   --cli status                    連線並印出名稱、韌體、電量、ANC（只讀）
///   --cli anc N [db]                切換 ANC（會改變耳機狀態）
///   --demo-shot 路徑 [dark|light]   用範例資料顯示面板並截圖
///   --live-ui 目錄                  連真的耳機，程式化操作面板元件並截圖，最後還原
/// </summary>
public static class Cli
{
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    public static bool IsCliMode(string[] args) =>
        args.Length > 0 && args[0] is "--cli" or "--demo-shot" or "--live-ui" or "--cold-toggle" or "--menu-shot" or "--live-menu" or "--live-rapid";

    static void Out(string s) => Console.WriteLine(s);

    public static async Task<int> RunAsync(string[] args)
    {
        AttachConsole(-1);
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            return args[0] switch
            {
                "--cli" => await RunCli(args),
                "--demo-shot" => await DemoShot(args[1], args.Length > 2 && args[2] == "light", args.Length > 3 && args[3] == "cooling", args.Length > 4 ? args[4] : Strings.Default),
                "--live-ui" => await LiveUi(args[1]),
                "--cold-toggle" => await ColdToggle(args[1]),
                "--live-menu" => await LiveMenu(),
                "--live-rapid" => await LiveRapid(),
                "--menu-shot" => await MenuShot(args[1], args.Length > 2 && args[2] == "light", args.Length > 3 ? args[3] : "ok", args.Length > 4 ? args[4] : Strings.Default),
                _ => 2,
            };
        }
        catch (Exception e) { Out($"錯誤：{e.Message}"); return 1; }
    }

    static void PrintStatus(DeviceState s)
    {
        Out($"裝置：{s.Name}  韌體 {s.Version}");
        Out($"電量：{s.Battery}%");
        Out($"ANC：{s.AncLabel}" + (s.Anc?.Db is int d && d > 0 ? $"（強度 {d}）" : ""));
    }

    static async Task<int> RunCli(string[] args)
    {
        if (args.Length > 1 && args[1] == "devices")
        {
            // 掃描並列出附近所有被辨識為 Edifier 的裝置（官方 App 的判定方式，見 Catalog.Identify）
            foreach (var f in await Discovery.ScanAllAsync(TimeSpan.FromSeconds(8), CancellationToken.None))
                Out($"{f.Model,-14} A2DP={f.A2dpAddress}  BLE={f.BleAddress}  rssi={f.Rssi}  version={f.Session.Version} encryption={f.Session.Encryption}  傳輸={(Catalog.UsesSpp(f.Model) ? "SPP" : "BLE")}");
            return 0;
        }
        var svc = new DeviceService(Discovery.MakeConnector());
        svc.Start();
        try
        {
            PrintStatus(await svc.WaitConnectedAsync(TimeSpan.FromSeconds(40)));
            if (args.Length > 1 && args[1] == "anc")
            {
                int mode = int.Parse(args[2]);
                int? db = args.Length > 3 ? int.Parse(args[3]) : null;
                var now = await svc.SetAncAsync(mode, db);
                Out($"已切換：{svc.State.AncLabel}" + (db is not null ? $"（強度 {now.Db}）" : ""));
            }
            return 0;
        }
        catch (ServiceException e) { Out($"錯誤：{e.Message}"); return 1; }
        finally { await svc.StopAsync(); }
    }

    // ---- 截圖 ----
    static void Screenshot(Window w, string path)
    {
        var src = PresentationSource.FromVisual(w);
        var m = src.CompositionTarget.TransformToDevice;
        int x = (int)(w.Left * m.M11) - 8, y = (int)(w.Top * m.M22) - 8;
        int cw = (int)(w.ActualWidth * m.M11) + 16, ch = (int)(w.ActualHeight * m.M22) + 16;
        using var bmp = new Bitmap(cw, ch);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(cw, ch));
        bmp.Save(path);
    }

    static Bitmap Grab(Window w)
    {
        var m = PresentationSource.FromVisual(w).CompositionTarget.TransformToDevice;
        int x = (int)(w.Left * m.M11), y = (int)(w.Top * m.M22), cw = (int)(w.ActualWidth * m.M11), ch = (int)(w.ActualHeight * m.M22);
        var bmp = new Bitmap(cw, ch);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(cw, ch));
        return bmp;
    }

    /// <summary>把元素本身渲染成 PNG（不含螢幕上其他內容），底色以不透明背景填滿。驗證版面用。</summary>
    static void RenderElement(FrameworkElement el, string path, System.Windows.Media.Color bg)
    {
        var m = PresentationSource.FromVisual(el).CompositionTarget.TransformToDevice;
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(el.ActualWidth * m.M11), (int)(el.ActualHeight * m.M22), 96 * m.M11, 96 * m.M22, System.Windows.Media.PixelFormats.Pbgra32);
        var back = new System.Windows.Media.DrawingVisual();
        using (var dc = back.RenderOpen()) dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(bg), null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
        rtb.Render(back);
        rtb.Render(el);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    static async Task<int> DemoShot(string path, bool light, bool cooling = false, string lang = "zh-TW")
    {
        Strings.SetLanguage(lang);
        Theme.DarkOverride = !light;
        Theme.Apply(!light); Strings.ApplyToResources(Application.Current.Resources);
        var host = new TrayHost(_ => throw new InvalidOperationException(), new VolumeControl("X", _ => new DemoEndpoint()), withTray: false);
        var demo = new DeviceState(true, "EDIFIER ES850NB", "1.0.1", 16, new AncState(16, 4, 8), Cooling: cooling);
        host.Flyout.ApplyState(demo, 50, false);
        host.Flyout.Show(); host.Flyout.HideFlyout();   // 先建立視窗控制代碼（套用背景材質）
        host.Flyout.ApplyState(demo, 50, false);
        host.Flyout.ShowFlyout();
        await Task.Delay(900);
        // 直接渲染元素（不含螢幕上其他內容），避免截圖抓到使用者畫面上的其他東西
        RenderElement((FrameworkElement)host.Flyout.Content, path, light ? System.Windows.Media.Color.FromRgb(243, 243, 243) : System.Windows.Media.Color.FromRgb(32, 32, 32));
        Out($"已儲存 {path}");
        host.Flyout.HideFlyout();
        return 0;
    }

    /// <summary>重現「第一次點托盤圖示」：視窗從未 Show 過就直接 Toggle（正常執行時的路徑）。</summary>
    static async Task<int> ColdToggle(string path)
    {
        Theme.Apply(Theme.IsDark()); Strings.ApplyToResources(Application.Current.Resources);
        var host = new TrayHost(_ => throw new InvalidOperationException(), new VolumeControl("X", _ => new DemoEndpoint()), withTray: false);
        host.Flyout.ApplyState(new DeviceState(true, "EDIFIER ES850NB", "1.0.1", 16, new AncState(16, 1)), 50, false);
        host.Flyout.Toggle();
        await Task.Delay(900);
        Screenshot(host.Flyout, path);
        Out($"第一次 Toggle 成功，視窗可見={host.Flyout.IsVisible}");
        host.Flyout.Toggle();
        Out($"再 Toggle 後可見={host.Flyout.IsVisible}");
        return 0;
    }

    /// <summary>用範例狀態顯示右鍵選單並截圖。第三個參數：ok（已連線）、off（未連線）、muted（靜音）。</summary>
    static async Task<int> MenuShot(string path, bool light, string scenario, string lang = "zh-TW")
    {
        Strings.SetLanguage(lang);
        Theme.DarkOverride = !light;
        Theme.Apply(!light); Strings.ApplyToResources(Application.Current.Resources);
        var host = new TrayHost(_ => throw new InvalidOperationException(), new VolumeControl("X", _ => new DemoEndpoint()), withTray: false);
        var state = scenario == "off" ? new DeviceState() : new DeviceState(true, "EDIFIER ES850NB", "1.0.1", 16, new AncState(16, 2), Cooling: scenario == "cooling");
        var devs = new List<(string, string, bool)> { ("AA:BB:CC:11:22:33", "ES850NB · 22:33", true), ("AA:BB:CC:11:44:55", "ES850NB · 44:55", false) };
        var entries = TrayMenuModel.Build(state, scenario == "off" ? null : 50, scenario == "muted", scenario == "ok", devs, light ? "light" : "dark", lang);
        using (var bmp = LogoRenderer.Render(72, state.Battery, state.Connected))
            host.Menu.Populate(entries, LogoRenderer.ToImageSource(bmp));
        var wa = System.Windows.SystemParameters.WorkArea;
        host.Menu.ShowAt((int)(wa.Right * 0.6), (int)wa.Bottom);
        await Task.Delay(900);
        // 版面：直接渲染元素（不含螢幕內容）；是否真的畫在螢幕上：比較顯示前後同一區域的像素
        RenderElement((FrameworkElement)host.Menu.Content, path, light ? System.Windows.Media.Color.FromRgb(243, 243, 243) : System.Windows.Media.Color.FromRgb(32, 32, 32));
        using (var after = Grab(host.Menu))
        {
            host.Menu.HideMenu();
            await Task.Delay(400);
            using var before = Grab(host.Menu);
            int diff = 0, total = after.Width * after.Height;
            for (int yy = 0; yy < after.Height; yy += 4)
                for (int xx = 0; xx < after.Width; xx += 4)
                    if (after.GetPixel(xx, yy) != before.GetPixel(xx, yy)) diff++;
            Out($"螢幕上選單區域有變化的取樣點：{diff} / {total / 16}（接近 0 代表選單沒畫出來）");
        }
        host.Menu.ShowAt((int)(wa.Right * 0.6), (int)wa.Bottom);
        await Task.Delay(300);
        Out($"選單：可見={host.Menu.IsVisible} 位置=({host.Menu.Left:F0},{host.Menu.Top:F0}) 大小={host.Menu.ActualWidth:F0}x{host.Menu.ActualHeight:F0} 工作區={wa.Left:F0},{wa.Top:F0},{wa.Right:F0},{wa.Bottom:F0} 透明度={host.Menu.Opacity}");
        Out($"已儲存 {path}");
        host.Menu.HideMenu();
        return 0;
    }

    static IEnumerable<System.Windows.Controls.Button> Buttons(object? root)
    {
        if (root is System.Windows.Controls.Button b) { yield return b; yield break; }
        if (root is System.Windows.Controls.Decorator d) foreach (var x in Buttons(d.Child)) yield return x;
        if (root is System.Windows.Controls.Panel p) foreach (var c in p.Children) foreach (var x in Buttons(c)) yield return x;
    }

    /// <summary>連真的耳機，從右鍵選單「點」各項目（和使用者相同的路徑：開選單、按項目），驗證動作後還原。</summary>
    static async Task<int> LiveMenu()
    {
        Theme.Apply(Theme.IsDark()); Strings.ApplyToResources(Application.Current.Resources);
        var host = new TrayHost(Discovery.MakeConnector(), withTray: false);
        host.Start();
        int? v0 = null; bool? m0 = null;
        async Task Click(string id)
        {
            host.ShowMenuAt(1200, 1380);
            var btn = Buttons(host.Menu.Content).First(b => (string)b.Tag == id);
            if (!btn.IsEnabled) { Out($"  {id} 被停用"); return; }
            btn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Task.Delay(id.StartsWith("anc:") ? 3000 : 600);
        }
        try
        {
            await host.Service.WaitConnectedAsync(TimeSpan.FromSeconds(40));
            await Task.Delay(1500);
            v0 = host.CurrentVolume; m0 = host.Muted;
            Out($"初始：{host.Service.State.AncLabel}  音量 {v0}  靜音 {m0}");
            await Click("anc:2"); Out($"選單點「舒適降噪」：{host.Service.State.AncLabel}");
            await Click("anc:3"); Out($"選單點「防風噪」：{host.Service.State.AncLabel}");
            await Click("vol+"); Out($"選單點「音量 +10%」：Windows 讀回 {host.Volume.Get()}（預期 {v0 + 10}）");
            await Click("vol-"); Out($"選單點「音量 -10%」：Windows 讀回 {host.Volume.Get()}（預期 {v0}）");
            await Click("mute"); Out($"選單點「靜音」：Windows 讀回靜音={host.Volume.Muted()}");
            await Click("mute"); Out($"再點一次：靜音={host.Volume.Muted()}");
            return 0;
        }
        finally
        {
            try { if (v0 is int v) host.Volume.Set(v); if (m0 is bool m) host.Volume.SetMute(m); } catch { }
            await Click("anc:1");
            Out($"還原：{host.Service.State.AncLabel}  音量 {host.Volume.Get()}  靜音 {host.Volume.Muted()}");
            await host.Service.StopAsync();
        }
    }

    /// <summary>快速連按降噪磚塊（約每 150 毫秒一次，像使用者狂點）：只有「可點」的磚塊才會送出，驗證冷卻期停用且不卡住。</summary>
    static async Task<int> LiveRapid()
    {
        Theme.Apply(Theme.IsDark()); Strings.ApplyToResources(Application.Current.Resources);
        var host = new TrayHost(Discovery.MakeConnector(), withTray: false);
        var coolingLog = new List<(long Ms, bool Cooling)>();
        host.Service.StateChanged += s => { lock (coolingLog) { if (coolingLog.Count == 0 || coolingLog[^1].Cooling != s.Cooling) coolingLog.Add((Environment.TickCount64, s.Cooling)); } };
        long origin = Environment.TickCount64;
        var timeline = new List<string>();
        host.Service.StateChanged += s => { lock (timeline) timeline.Add($"{(Environment.TickCount64 - origin) / 1000.0,6:F2}s  冷卻={s.Cooling,-5}  模式={s.Anc?.Mode}  錯誤={(s.Error == "" ? "-" : s.Error)}"); };
        host.Start();
        var f = host.Flyout;
        try
        {
            await host.Service.WaitConnectedAsync(TimeSpan.FromSeconds(40));
            await Task.Delay(1500);
            int sent = 0, skipped = 0;
            var modes = new[] { 2, 3, 5, 2, 3, 5 };
            long t0 = Environment.TickCount64;
            for (int i = 0; i < 24; i++)
            {
                await Task.Delay(150);
                int mode = modes[i % modes.Length];
                if (f.TileState(mode).Enabled) { lock (timeline) timeline.Add($"{(Environment.TickCount64 - origin) / 1000.0,6:F2}s  >>> 點擊 mode {mode}"); f.SimulateTile(mode); sent++; } else skipped++;
            }
            await Task.Delay(3000);
            Out($"連按 24 次（約 {(Environment.TickCount64 - t0) / 1000.0:F1} 秒）：送出 {sent} 次，因冷卻停用而略過 {skipped} 次");
            lock (coolingLog)
                for (int i = 0; i + 1 < coolingLog.Count; i++)
                    if (coolingLog[i].Cooling && !coolingLog[i + 1].Cooling) Out($"  冷卻 {(coolingLog[i + 1].Ms - coolingLog[i].Ms) / 1000.0:F2} 秒後解除");
            Out($"最終：{host.Service.State.AncLabel}  冷卻中={host.Service.State.Cooling}  訊息=[{f.CurrentMessage}]");
            lock (timeline) foreach (var l in timeline.Where(l => l.Contains(">>>") || l.Contains("冷卻=True") || l.Contains("冷卻=False")).Take(40)) Out("  " + l);
            return sent > 0 && !host.Service.State.Cooling ? 0 : 1;
        }
        finally
        {
            await Task.Delay(1800);
            f.SimulateTile(1); await Task.Delay(3000);
            Out($"還原：{host.Service.State.AncLabel}");
            await host.Service.StopAsync();
        }
    }

    sealed class DemoEndpoint : IEndpoint { public float Scalar { get; set; } = 0.5f; public bool Mute { get; set; } }

    static async Task<int> LiveUi(string dir)
    {
        Directory.CreateDirectory(dir);
        Theme.Apply(Theme.IsDark()); Strings.ApplyToResources(Application.Current.Resources);
        var host = new TrayHost(Discovery.MakeConnector(), withTray: false);
        host.Start();
        var f = host.Flyout;
        int? v0 = null;
        try
        {
            await host.Service.WaitConnectedAsync(TimeSpan.FromSeconds(40));
            await Task.Delay(1500);
            f.Show(); f.HideFlyout();
            f.ShowFlyout(); await Task.Delay(900);
            Screenshot(f, Path.Combine(dir, "cs_1_open.png"));
            Out($"初始：{host.Service.State.AncLabel}  電量 {host.Service.State.Battery}%  音量 {host.CurrentVolume}");
            v0 = host.CurrentVolume;

            f.SimulateTile(2); await Task.Delay(3000);
            Out($"點舒適降噪：{host.Service.State.AncLabel}");
            f.SimulateTile(4); await Task.Delay(3000);
            f.SimulateAmbient(9); await Task.Delay(3500);
            Out($"環境音強度拖到 9：{host.Service.State.AncLabel} 強度 {host.Service.State.Anc?.Db}");
            f.SimulateVolume(60); await Task.Delay(500);
            Out($"音量拖到 60：Windows 讀回 {host.Volume.Get()}");
            Screenshot(f, Path.Combine(dir, "cs_2_changed.png"));
            return 0;
        }
        finally
        {
            try { if (v0 is int v) f.SimulateVolume(v); } catch { }
            await Task.Delay(500);
            f.SimulateTile(1); await Task.Delay(3000);
            Out($"還原：{host.Service.State.AncLabel}  音量 {host.Volume.Get()}");
            await host.Service.StopAsync();
        }
    }
}

