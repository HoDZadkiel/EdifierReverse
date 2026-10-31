using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EdifierTray.Service;

namespace EdifierTray.Ui;

/// <summary>Windows 11 風格的彈出面板：電量、降噪模式、環境音強度、音量拖曳條。只負責畫面，動作以事件交給 App。</summary>
public partial class FlyoutWindow : Window
{
    public event Action<int>? AncRequested;
    public event Action<int>? AmbientRequested;
    public event Action<int>? VolumeChanged;
    public event Action? MuteToggled, ReconnectRequested, QuitRequested, PowerCheckRequested, PowerSaveToggled;

    readonly RadioButton[] _tiles;
    readonly DispatcherTimer _ambientDebounce = new() { Interval = TimeSpan.FromMilliseconds(350) };
    readonly DispatcherTimer _messageTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    bool _updating;
    bool _dark => Theme.IsDark();
    DateTime _shownAt = DateTime.MinValue, _hiddenAt = DateTime.MinValue;
    DeviceState _state = new();

    /// <summary>程式版本（編譯時由 pyproject.toml 的 version 寫入，去掉 +commit 後綴）。</summary>
    public static string AppVersion { get; } =
        (typeof(FlyoutWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "").Split('+')[0];

    public const string RepoUrl = "https://github.com/HoDZadkiel/EdifierReverse";

    public string VersionLabel => VersionText.Text;

    public FlyoutWindow()
    {
        InitializeComponent();
        VersionText.Text = "v" + AppVersion;
        VersionText.MouseLeftButtonUp += (_, _) =>  // 版本號連到 GitHub 倉庫
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(RepoUrl) { UseShellExecute = true }); }
            catch (Exception) { }
        };
        _tiles = new[] { Mode1, Mode2, Mode3, Mode4, Mode5 };
        foreach (var t in _tiles) t.Click += (_, _) => AncRequested?.Invoke(int.Parse((string)t.Tag));

        AmbientSlider.ValueChanged += (_, _) =>
        {
            AmbientValue.Text = ((int)AmbientSlider.Value).ToString();
            if (_updating) return;
            _ambientDebounce.Stop();
            _ambientDebounce.Start();  // 設定指令有 ≥2 秒節流：停手或放開後才送一次
        };
        _ambientDebounce.Tick += (_, _) => { _ambientDebounce.Stop(); AmbientRequested?.Invoke((int)AmbientSlider.Value); };
        AmbientSlider.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) =>
        {
            if (_ambientDebounce.IsEnabled) { _ambientDebounce.Stop(); AmbientRequested?.Invoke((int)AmbientSlider.Value); }
        }), true);

        VolumeSlider.ValueChanged += (_, _) =>
        {
            VolumeValue.Text = $"{(int)VolumeSlider.Value}%";
            if (!_updating) VolumeChanged?.Invoke((int)VolumeSlider.Value);
        };
        MuteButton.Click += (_, _) => MuteToggled?.Invoke();
        PowerCheckRow.Click += (_, _) => PowerCheckRequested?.Invoke();
        PowerSaveRow.Click += (_, _) => PowerSaveToggled?.Invoke();
        ReconnectLink.MouseLeftButtonUp += (_, _) => ReconnectRequested?.Invoke();
        QuitLink.MouseLeftButtonUp += (_, _) => QuitRequested?.Invoke();
        _messageTimer.Tick += (_, _) => { _messageTimer.Stop(); MessageText.Text = ""; };

        Deactivated += (_, _) => { if ((DateTime.UtcNow - _shownAt).TotalMilliseconds > 300) HideFlyout(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) HideFlyout(); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 成功時背景略透明，讓 Acrylic 透出來（選單與面板共用同一組顏色資源）
        if (WindowEffects.Apply(this, _dark)) Theme.Apply(_dark, 0.82);  // 外觀切換時由 Theme.Refresh 重新套用
    }

    // ---- 顯示 ----
    public void ShowFlyout()
    {
        // 視窗從未顯示過就 Measure 會讓 WPF 直接 FailFast（第一次點托盤圖示閃退的原因）；先建立控制代碼
        new WindowInteropHelper(this).EnsureHandle();
        Measure(new Size(Width, double.PositiveInfinity));
        var h = DesiredSize.Height;
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 12;
        Top = wa.Bottom - h - 12;
        _shownAt = DateTime.UtcNow;
        Show();  // 只設 Visibility 的話，從未顯示過的視窗之後呼叫 Activate 會丟例外
        Activate();
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(200))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    public void HideFlyout()
    {
        _hiddenAt = DateTime.UtcNow;
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Visibility = Visibility.Hidden;
    }

    public void Toggle()
    {
        // 淡入動畫剛開始時 Opacity 還是 0，不能用它判斷是否開著
        if (IsVisible) { HideFlyout(); return; }
        // 面板開著時點托盤圖示：焦點先被工作列搶走，面板已自己收起；這一下點擊不該又把它打開
        if ((DateTime.UtcNow - _hiddenAt).TotalMilliseconds < 250) return;
        ShowFlyout();
    }

    public void ShowMessage(string text)
    {
        MessageText.Text = text;
        _messageTimer.Stop();
        _messageTimer.Start();
    }

    // ---- 狀態 ----
    public void ApplyState(DeviceState s, int? volume, bool muted, string fallbackName = Catalog.GenericModel)
    {
        _state = s;
        _updating = true;
        try
        {
            using (var bmp = LogoRenderer.Render(96, s.Battery, s.Connected))
                LogoImage.Source = LogoRenderer.ToImageSource(bmp);
            NameText.Text = s.Name == "" ? fallbackName : s.Name;
            if (s.Connected)
            {
                StatusText.Text = Strings.T("p.connected_fw", new { v = s.Version });
                BatteryText.Text = $"{s.Battery}%";
                // 淺色底上的淺綠／橘字不易讀，淺色主題用一般文字色（電量色已在 logo 的環上）
                BatteryText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                if (_dark)
                {
                    var c = LogoRenderer.RingColor(s.Battery, true);
                    BatteryText.Foreground = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
                }
            }
            else
            {
                StatusText.Text = s.Error == "" ? Strings.T("p.disconnected") : Strings.T("p.disconnected_err", new { err = s.Error.Length > 28 ? s.Error[..28] : s.Error });
                BatteryText.Text = "–";
                BatteryText.SetResourceReference(TextBlock.ForegroundProperty, "SubBrush");
            }

            AncBox.Visibility = s.HasAnc ? Visibility.Visible : Visibility.Collapsed;   // 沒有降噪的機種整塊隱藏，視窗高度隨內容縮小
            PowerBox.Visibility = s.PowerSave is null ? Visibility.Collapsed : Visibility.Visible;   // 沒有省電模式的機種整塊隱藏
            PowerStateText.Text = Strings.T("m.power_state", new { state = s.PowerSave == true ? Strings.T("m.on") : Strings.T("m.off") });
            PowerSaveCheck.Visibility = s.PowerSave == false ? Visibility.Visible : Visibility.Hidden;   // 勾＝省電模式關＝防自動關機
            PowerCheckRow.IsEnabled = PowerSaveRow.IsEnabled = s.Connected;
            bool canSet = s.Connected && s.AncSupported;
            // 冷卻期：裝置在兩次設定的間隔內會靜默忽略，連按只會卡住，所以暫停切換（目前模式仍標示）
            bool usable = canSet && !s.Cooling;
            foreach (var t in _tiles)
            {
                t.IsEnabled = usable;
                t.IsChecked = canSet && s.Anc?.Mode == int.Parse((string)t.Tag);
            }

            bool ambientOn = canSet && s.Anc?.Group is int g && DeviceService.AmbientMode.TryGetValue(g, out var am) && s.Anc.Mode == am;
            AmbientSlider.IsEnabled = ambientOn && !s.Cooling;
            if (ambientOn && s.Anc?.Db is int db && db >= DeviceService.DbMin && db <= DeviceService.DbMax) AmbientSlider.Value = db;
            AmbientValue.Text = ambientOn ? ((int)AmbientSlider.Value).ToString() : "";
            AmbientTitle.SetResourceReference(TextBlock.ForegroundProperty, ambientOn ? "SubBrush" : "DisabledBrush");

            VolumeSlider.IsEnabled = volume is not null;
            if (volume is int v) VolumeSlider.Value = v;
            VolumeValue.Text = volume is null ? "" : $"{volume}%";
            MuteButton.Content = muted ? "" : "";
        }
        finally { _updating = false; }
    }

    public DeviceState CurrentState => _state;
    // 供自動化驗證使用：模擬使用者操作元件
    /// <summary>測試用：某個降噪磚塊目前是否可點、是否選中。</summary>
    public (bool Enabled, bool Checked) TileState(int mode) => (_tiles[mode - 1].IsEnabled, _tiles[mode - 1].IsChecked == true);
    public string CurrentMessage => MessageText.Text;
    public bool AmbientEnabled => AmbientSlider.IsEnabled;
    public void SimulateTile(int mode) { var t = _tiles[mode - 1]; t.IsChecked = true; AncRequested?.Invoke(mode); }
    public void SimulateAmbient(int value) { AmbientSlider.Value = value; }
    public void SimulateVolume(int value) { VolumeSlider.Value = value; }
}

