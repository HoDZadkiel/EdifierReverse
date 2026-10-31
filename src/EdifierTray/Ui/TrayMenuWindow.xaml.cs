using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace EdifierTray.Ui;

/// <summary>Windows 11 風格的右鍵選單：圓角、Acrylic、圖示、選中指示條。內容由 TrayMenuModel 提供，點選時以事件回報動作代號。</summary>
public partial class TrayMenuWindow : Window
{
    /// <summary>點選項目時觸發，參數是 MenuEntry.Id。</summary>
    public event Action<string>? ItemInvoked;

    DateTime _shownAt = DateTime.MinValue, _hiddenAt = DateTime.MinValue;
    IReadOnlyList<MenuEntry> _entries = Array.Empty<MenuEntry>();

    public TrayMenuWindow()
    {
        InitializeComponent();
        InitSubmenuTimers();
        Closed += (_, _) => CloseSub();
        Deactivated += (_, _) => { if ((DateTime.UtcNow - _shownAt).TotalMilliseconds > 300 && _sub?.IsMouseOver != true) HideMenu(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) HideMenu(); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var dark = Theme.IsDark();
        if (WindowEffects.Apply(this, dark)) Theme.Apply(dark, 0.82);  // 與彈出面板一致：Acrylic 成功時背景略透明
    }

    public IReadOnlyList<MenuEntry> Entries => _entries;

    /// <summary>依項目重建畫面。logo 顯示在標頭左側（可為 null）。</summary>
    public void Populate(IReadOnlyList<MenuEntry> entries, ImageSource? logo)
    {
        _entries = entries;
        CloseSub();
        ItemsHost.Children.Clear();
        foreach (var e in entries)
        {
            var el = BuildEntry(e, logo);
            if (e.Kind != EntryKind.Section) el.MouseEnter += (_, _) => ScheduleClose();   // 移到別的列：子選單收起
            ItemsHost.Children.Add(el);
        }
    }

    /// <summary>一個項目對應的畫面元素（主選單與子選單共用）。</summary>
    UIElement BuildEntry(MenuEntry e, ImageSource? logo = null)
    {
        switch (e.Kind)
        {
            case EntryKind.Header: return BuildHeader(e, logo);
            case EntryKind.Label:
                var label = new TextBlock { Text = e.Text, FontSize = 12, Margin = new Thickness(12, 6, 0, 2) };
                label.SetResourceReference(TextBlock.ForegroundProperty, "SubBrush");
                return label;
            case EntryKind.Segmented: return BuildSegmented(e);
            case EntryKind.Section: return BuildSection(e);
            case EntryKind.Separator:
                var sep = new Border { Height = 1, Margin = new Thickness(8, 4, 8, 4) };
                sep.SetResourceReference(Border.BackgroundProperty, "BorderBrush2");
                return sep;
            default: return BuildItem(e);
        }
    }

    // ---- 子選單：滑鼠移到大項上，選項從右側彈出（右邊放不下會自動翻到左邊） ----
    Window? _sub;   // 子選單用獨立的無邊框視窗（Close 是同步的；Popup 的視窗銷毀是非同步，收尾時會出問題）
    string _subKey = "";

    /// <summary>子選單視窗的位置與大小（DIP）；沒有子選單時為 null。</summary>
    public Rect? SubmenuBounds => _sub is { IsVisible: true } ? new Rect(_sub.Left, _sub.Top, _sub.ActualWidth, _sub.ActualHeight) : null;

    /// <summary>目前彈出的子選單所屬大項的代號（sec:...）；沒有子選單時為空字串。</summary>
    public string OpenSubmenu => _sub is { IsVisible: true } ? _subKey : "";
    readonly DispatcherTimer _openTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromMilliseconds(280) };
    (MenuEntry Entry, FrameworkElement Row)? _pendingOpen;

    void InitSubmenuTimers()
    {
        _openTimer.Tick += (_, _) => { _openTimer.Stop(); if (_pendingOpen is var (en, row)) OpenSub(en, row); };
        _closeTimer.Tick += (_, _) => { _closeTimer.Stop(); CloseSub(); };
    }

    /// <summary>游標移進大項標題列：稍微延遲後彈出它的子選單（游標斜著掃過別的大項時才不會一直閃）。</summary>
    void EnterSection(MenuEntry e, FrameworkElement row)
    {
        _closeTimer.Stop();
        if (_subKey == e.Id) return;
        _pendingOpen = (e, row);
        _openTimer.Stop(); _openTimer.Start();
    }

    /// <summary>測試與自動化用：等同游標移進指定大項／移進一般列。</summary>
    public void HoverSection(string id) =>
        EnterSection(_entries.Single(x => x.Id == id), (FrameworkElement)ItemsHost.Children.OfType<Button>().Single(b => (string)b.Tag == id));
    /// <summary>測試用：讓還在倒數的開啟／收起計時器立刻到期（不必跑訊息迴圈，那會讓視窗失焦而收起）。</summary>
    public void FlushHoverTimers()
    {
        if (_openTimer.IsEnabled) { _openTimer.Stop(); if (_pendingOpen is var (en, row)) OpenSub(en, row); }
        if (_closeTimer.IsEnabled) { _closeTimer.Stop(); CloseSub(); }
    }
    public void HoverOtherRow() => ScheduleClose();

    void ScheduleClose() { _openTimer.Stop(); _pendingOpen = null; _closeTimer.Stop(); _closeTimer.Start(); }

    void CloseSub()
    {
        _openTimer.Stop(); _closeTimer.Stop(); _pendingOpen = null;
        if (_sub is not null) { var w = _sub; _sub = null; w.Close(); }
        _subKey = "";
    }

    void OpenSub(MenuEntry e, FrameworkElement row)
    {
        _closeTimer.Stop();
        if (_sub is not null && _subKey == e.Id) return;   // 已經開著
        CloseSub();
        var content = BuildSubmenuContent(e);
        content.MouseEnter += (_, _) => _closeTimer.Stop();   // 游標移進子選單：不收
        content.MouseLeave += (_, _) => ScheduleClose();
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = content.DesiredSize;
        var scale = WindowEffects.DpiScale(this);
        var rowTop = row.PointToScreen(new Point(0, 0)).Y / scale;   // PointToScreen 是裝置像素
        var wa = SystemParameters.WorkArea;
        const double shadow = 12;   // 內容右、下留給陰影的邊
        double left = Left + ActualWidth - 2;                         // 預設貼在主選單右側
        if (left + size.Width > wa.Right) left = Left - size.Width + shadow + 2;   // 右邊放不下：翻到左側
        double top = Math.Min(Math.Max(rowTop - 5, wa.Top), wa.Bottom - size.Height);
        _sub = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false,
            ShowActivated = false, Topmost = true, ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight,
            Left = left, Top = top, Content = content, UseLayoutRounding = true, SnapsToDevicePixels = true,
            FontFamily = FontFamily,
        };
        _sub.Show();
        _subKey = e.Id;
    }

    /// <summary>子選單的內容（圓角面板，樣式跟主選單一致）。</summary>
    public FrameworkElement BuildSubmenuContent(MenuEntry section)
    {
        var host = new StackPanel();
        foreach (var child in section.Children ?? Array.Empty<MenuEntry>()) host.Children.Add(BuildEntry(child));
        var box = new Border
        {
            Child = host, Padding = new Thickness(4), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            MinWidth = 220, MaxWidth = 320, Margin = new Thickness(0, 0, 12, 12),   // 留邊給陰影
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.35 },
        };
        box.SetResourceReference(Border.BackgroundProperty, "BgBrush");
        box.SetResourceReference(Border.BorderBrushProperty, "BorderBrush2");
        return box;
    }

    /// <summary>大項標題列：左邊名稱、右邊目前狀態摘要與向右的箭頭。滑過或點一下就彈出子選單，點選時主選單不關。</summary>
    UIElement BuildSection(MenuEntry e)
    {
        var grid = new Grid { Height = 34 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.Children.Add(new TextBlock { Text = e.Text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) });
        if (e.Detail != "")
        {
            var summary = new TextBlock { Text = e.Detail, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, MaxWidth = 130, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(summary, 1);
            grid.Children.Add(summary);
        }
        var chevron = new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = 11,
                                      HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        chevron.SetResourceReference(TextBlock.ForegroundProperty, "SubBrush");
        Grid.SetColumn(chevron, 2);
        grid.Children.Add(chevron);
        var button = new Button { Content = grid, Style = (Style)Application.Current.Resources["MenuRow"], Tag = e.Id };
        button.MouseEnter += (_, _) => EnterSection(e, button);
        button.MouseLeave += (_, _) => { _openTimer.Stop(); _pendingOpen = null; ScheduleClose(); };
        button.Click += (_, _) => OpenSub(e, button);
        return button;
    }

    static UIElement BuildHeader(MenuEntry e, ImageSource? logo)
    {
        var grid = new Grid { Margin = new Thickness(8, 8, 8, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (logo is not null)
            grid.Children.Add(new Image { Source = logo, Width = 36, Height = 36, Margin = new Thickness(0, 0, 10, 0) });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = e.Text, FontSize = 14, FontWeight = FontWeights.SemiBold };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var detail = new TextBlock { Text = e.Detail, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "SubBrush");
        texts.Children.Add(name);
        texts.Children.Add(detail);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        return grid;
    }

    /// <summary>一排並列的小按鈕（外觀、語言），選中的填強調色。</summary>
    UIElement BuildSegmented(MenuEntry e)
    {
        var grid = new Grid { Margin = new Thickness(10, 2, 10, 4) };
        var opts = e.Options ?? Array.Empty<(string Id, string Text, bool Selected)>();
        for (int i = 0; i < opts.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (id, text, selected) = opts[i];
            var label = new TextBlock { Text = text, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, selected ? "AccentTextBrush" : "TextBrush");
            var pill = new Border { Child = label, Height = 28, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
                                    Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0), Tag = id };
            pill.SetResourceReference(Border.BackgroundProperty, selected ? "AccentBrush" : "SurfaceBrush");
            pill.SetResourceReference(Border.BorderBrushProperty, selected ? "AccentBrush" : "BorderBrush2");
            if (!selected)
            {
                pill.MouseEnter += (_, _) => pill.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
                pill.MouseLeave += (_, _) => pill.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            }
            pill.MouseLeftButtonUp += (_, _) => { HideMenu(); ItemInvoked?.Invoke(id); };
            Grid.SetColumn(pill, i);
            grid.Children.Add(pill);
        }
        return grid;
    }

    UIElement BuildItem(MenuEntry e)
    {
        // 欄：選中指示條 | 圖示 | 文字 | 次要文字 | 勾選
        var grid = new Grid { Height = 34 };
        foreach (var w in new[] { 10.0, 28.0 }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });

        bool isMode = e.Id.StartsWith("anc:");
        if (e.Checked && isMode)  // 降噪模式用 Windows 11 導覽列那種小色條，其餘用勾號
        {
            var pill = new Rectangle { Width = 3, Height = 16, RadiusX = 1.5, RadiusY = 1.5, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 0) };
            pill.SetResourceReference(Shape.FillProperty, "AccentBrush");
            grid.Children.Add(pill);
        }
        if (e.Glyph != "")
        {
            var g = new TextBlock { Text = e.Glyph, FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = 15,
                                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(g, 1);
            grid.Children.Add(g);
        }
        var text = new TextBlock { Text = e.Text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);
        if (e.Detail != "")
        {
            var d = new TextBlock { Text = e.Detail, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
            Grid.SetColumn(d, 3);
            grid.Children.Add(d);
        }
        if (e.Checked && !isMode)
        {
            var c = new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = 14,
                                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            c.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            Grid.SetColumn(c, 4);
            grid.Children.Add(c);
        }

        var button = new Button { Content = grid, Style = (Style)Application.Current.Resources["MenuRow"], IsEnabled = e.Enabled, Tag = e.Id };
        button.Click += (_, _) => { HideMenu(); ItemInvoked?.Invoke(e.Id); };
        return button;
    }

    // ---- 顯示 ----
    int _anchorX, _anchorY;

    /// <summary>在游標位置（裝置像素）顯示。</summary>
    public void ShowAt(int cursorX, int cursorY)
    {
        _anchorX = cursorX; _anchorY = cursorY;
        new WindowInteropHelper(this).EnsureHandle();  // 視窗從未顯示過就 Measure 會讓 WPF FailFast
        Measure(new Size(Width, double.PositiveInfinity));
        var wa = SystemParameters.WorkArea;
        var (left, top) = TrayMenuModel.Place(cursorX, cursorY, Width, DesiredSize.Height, WindowEffects.DpiScale(this),
                                              wa.Left, wa.Top, wa.Right, wa.Bottom);
        Left = left;
        Top = top;
        _shownAt = DateTime.UtcNow;
        Show();
        Activate();
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(160))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    public void HideMenu()
    {
        CloseSub();
        _hiddenAt = DateTime.UtcNow;
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Visibility = Visibility.Hidden;
    }

    /// <summary>右鍵點圖示：開著就收起；剛因失焦收起的那一下點擊不重開（點圖示會先讓選單失焦）。</summary>
    public void ToggleAt(int cursorX, int cursorY)
    {
        if (IsVisible) { HideMenu(); return; }
        if ((DateTime.UtcNow - _hiddenAt).TotalMilliseconds < 250) return;
        ShowAt(cursorX, cursorY);
    }
}
