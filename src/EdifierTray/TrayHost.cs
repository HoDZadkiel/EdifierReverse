using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using EdifierTray.Service;
using EdifierTray.Ui;

namespace EdifierTray;

/// <summary>托盤圖示、彈出面板、右鍵選單、裝置服務與音量控制的組合。UI 動作都在 WPF 的 UI 執行緒執行。</summary>
public sealed class TrayHost : IDisposable
{
    public DeviceService Service { get; }
    public VolumeControl Volume { get; }
    public FlyoutWindow Flyout { get; }
    public TrayMenuWindow Menu { get; }
    public Settings Settings { get; }
    public DeviceDirectory? Directory { get; }
    readonly NotifyIcon? _icon;
    int? _volume;
    bool _muted, _wasConnected, _scanning;
    string _volumeTarget = "";

    /// <param name="withTray">false 時不建立托盤圖示（自動化驗證用）。</param>
    /// <param name="directory">裝置目錄（裝置選擇與掃描）；自動化驗證可不給。</param>
    public TrayHost(Connector connector, VolumeControl? volume = null, bool withTray = true,
                    DeviceDirectory? directory = null, Settings? settings = null)
    {
        Directory = directory;
        Settings = settings ?? directory?.Settings ?? new Settings();
        Volume = volume ?? new VolumeControl();
        Service = new DeviceService(connector);
        Flyout = new FlyoutWindow();
        Flyout.AncRequested += async m => await RunAnc(m, null);
        Flyout.AmbientRequested += async db => await RunAnc(DeviceService.AmbientMode[16], db);
        Flyout.VolumeChanged += OnVolume;
        Flyout.MuteToggled += ToggleMute;
        Flyout.ReconnectRequested += () => Service.Reconnect();
        Flyout.QuitRequested += Quit;
        Menu = new TrayMenuWindow();
        Menu.ItemInvoked += OnMenuItem;
        Service.StateChanged += s => System.Windows.Application.Current.Dispatcher.BeginInvoke(() => Apply(s));

        if (withTray)
        {
            _icon = new NotifyIcon { Visible = true, Text = Strings.T("t.connecting", new { name = "ES850NB" }) };
            _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Flyout.Toggle(); };
            _icon.MouseUp += (_, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                var p = Cursor.Position;  // 裝置像素
                ShowMenuAt(p.X, p.Y);
            };
        }
        Render(Service.State);
    }

    public void Start()
    {
        RefreshVolume();
        Render(Service.State);
        Service.Start();
    }

    public static string Tooltip(DeviceState s)
    {
        var name = s.Name == "" ? "ES850NB" : s.Name;
        if (!s.Connected) return Strings.T("t.disconnected", new { name });
        var anc = s.AncLabel;
        if (s.Anc?.Db is int db && db > 0 && s.Anc.Group is int g
            && DeviceService.AmbientMode.TryGetValue(g, out var am) && s.Anc.Mode == am) anc += Strings.T("t.strength", new { n = db });
        var text = $"{name}\n{Strings.T("t.battery", new { b = s.Battery })}\n{Strings.T("t.anc", new { mode = anc })}";
        return text.Length > 120 ? text[..120] : text;
    }

    void Apply(DeviceState s)
    {
        // 多台 Edifier 裝置時，調的是目前連線那一台的播放端點；連上後順便記住裝置名稱供選單顯示
        if (s.Connected && s.Name != "" && s.Name != _volumeTarget)
        {
            _volumeTarget = s.Name;
            Volume.SetTarget(s.Name);
            _wasConnected = false;
            if (Directory?.Current is { } cur) Directory.RememberName(cur.A2dp, s.Name);
        }
        if (s.Connected != _wasConnected)  // 連上／斷線時重選端點：連上用耳機端點，斷線改用備援的預設輸出
        {
            Volume.Reset();
            RefreshVolume();
        }
        _wasConnected = s.Connected;
        Render(s);
    }

    void Render(DeviceState s)
    {
        Flyout.ApplyState(s, _volume, _muted);
        if (_icon is null) return;
        var old = _icon.Icon;
        using (var bmp = LogoRenderer.Render(32, s.Battery, s.Connected)) _icon.Icon = LogoRenderer.ToIcon(bmp);
        old?.Dispose();
        _icon.Text = Tooltip(s);
    }

    // ---- 外觀、語言、裝置 ----
    public void SetTheme(string theme)
    {
        if (!Settings.Themes.Contains(theme)) return;
        Settings.Theme = theme;
        Settings.Save();
        Theme.Mode = theme;
        Theme.Refresh(Flyout, Menu);
        Render(Service.State);
    }

    public void SetLanguage(string code)
    {
        Settings.Language = Strings.SetLanguage(code);
        Settings.Save();
        Strings.ApplyToResources(System.Windows.Application.Current.Resources);  // XAML 的 DynamicResource 立刻更新
        Render(Service.State);
    }

    public void SelectDevice(string a2dp)
    {
        if (Directory is null || !Directory.Select(a2dp)) return;
        Service.Reconnect();
    }

    async Task RescanAsync()
    {
        if (Directory is null || _scanning) return;
        _scanning = true;
        try
        {
            var found = await Directory.ScanAsync(TimeSpan.FromSeconds(6));
            Flyout.ShowMessage(Strings.T("m.found_n", new { n = found.Count }));
        }
        catch (Exception e) { Flyout.ShowMessage(Strings.T("e.scan_failed", new { err = e.Message })); }
        finally { _scanning = false; }
    }

    // ---- 右鍵選單 ----
    IReadOnlyList<(string, string, bool)> DeviceRows()
    {
        if (Directory is null) return Array.Empty<(string, string, bool)>();
        var cur = Directory.Current?.A2dp;
        return Directory.Devices.Select(d => (d.A2dp, DeviceDirectory.Label(d), string.Equals(d.A2dp, cur, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>依目前狀態重建選單並在指定位置（裝置像素）切換顯示。</summary>
    public void ShowMenuAt(int x, int y)
    {
        Menu.Populate(TrayMenuModel.Build(Service.State, _volume, _muted, Autostart.IsEnabled(), DeviceRows(),
                                          Settings.Theme, Strings.Language, _scanning), MenuLogo());
        Menu.ToggleAt(x, y);
    }

    System.Windows.Media.ImageSource MenuLogo()
    {
        var s = Service.State;
        using var bmp = LogoRenderer.Render(72, s.Battery, s.Connected);
        return LogoRenderer.ToImageSource(bmp);
    }

    void OnMenuItem(string id)
    {
        var colon = id.IndexOf(':');
        var kind = colon < 0 ? id : id[..colon];
        var arg = colon < 0 ? "" : id[(colon + 1)..];
        switch (kind)
        {
            case "anc": _ = RunAnc(int.Parse(arg), null); return;
            case "dev": SelectDevice(arg); return;
            case "theme": SetTheme(arg); return;
            case "lang": SetLanguage(arg); return;
        }
        switch (id)
        {
            case "mute": ToggleMute(); break;
            case "vol+": StepVolume(+TrayMenuModel.VolumeStep); break;
            case "vol-": StepVolume(-TrayMenuModel.VolumeStep); break;
            case "rescan": _ = RescanAsync(); break;
            case "open": Flyout.ShowFlyout(); break;
            case "reconnect": Service.Reconnect(); break;
            case "autostart": ToggleAutostart(); break;
            case "quit": Quit(); break;
        }
    }

    void StepVolume(int delta)
    {
        try
        {
            _volume = Volume.Set((_volume ?? Volume.Get()) + delta);
            Render(Service.State);
        }
        catch (ServiceException e) { Flyout.ShowMessage(e.Message); }
    }

    void ToggleAutostart()
    {
        try { Autostart.Set(!Autostart.IsEnabled()); }
        catch (Exception e) { Flyout.ShowMessage(Strings.T("e.autostart", new { err = e.Message })); }
    }

    void RefreshVolume()
    {
        try { _volume = Volume.Get(); _muted = Volume.Muted(); }
        catch (ServiceException) { _volume = null; }
    }

    async Task RunAnc(int mode, int? db)
    {
        if (Service.State.Cooling) return;  // 冷卻期內不送（面板已停用按鈕；這裡擋住選單等其他入口，避免排隊卡住）
        try { await Service.SetAncAsync(mode, db); }
        catch (ServiceException e) { Flyout.ShowMessage(e.Message); Render(Service.State); }  // 失敗時把面板校正回真實狀態
        catch (Exception e) { Flyout.ShowMessage($"{e.GetType().Name}: {e.Message}"); Render(Service.State); }
    }

    void OnVolume(int percent)
    {
        try { _volume = Volume.Set(percent); }
        catch (ServiceException e) { Flyout.ShowMessage(e.Message); }
    }

    void ToggleMute()
    {
        try { Volume.SetMute(!Volume.Muted()); }
        catch (ServiceException e) { Flyout.ShowMessage(e.Message); }
        RefreshVolume();
        Render(Service.State);
    }

    public void Quit()
    {
        _ = Service.StopAsync();
        Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    public void Dispose()
    {
        if (_icon is not null) { _icon.Visible = false; _icon.Dispose(); }
        Menu.Close();
        Flyout.Close();
    }

    public int? CurrentVolume => _volume;
    public bool Muted => _muted;
}
