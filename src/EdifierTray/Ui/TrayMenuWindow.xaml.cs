using System.Windows;
using System.Windows.Controls;
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
        Deactivated += (_, _) => { if ((DateTime.UtcNow - _shownAt).TotalMilliseconds > 300) HideMenu(); };
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
        ItemsHost.Children.Clear();
        foreach (var e in entries)
        {
            switch (e.Kind)
            {
                case EntryKind.Header: ItemsHost.Children.Add(BuildHeader(e, logo)); break;
                case EntryKind.Label:
                    var label = new TextBlock { Text = e.Text, FontSize = 12, Margin = new Thickness(12, 6, 0, 2) };
                    label.SetResourceReference(TextBlock.ForegroundProperty, "SubBrush");
                    ItemsHost.Children.Add(label);
                    break;
                case EntryKind.Segmented: ItemsHost.Children.Add(BuildSegmented(e)); break;
                case EntryKind.Separator:
                    var sep = new Border { Height = 1, Margin = new Thickness(8, 4, 8, 4) };
                    sep.SetResourceReference(Border.BackgroundProperty, "BorderBrush2");
                    ItemsHost.Children.Add(sep);
                    break;
                default: ItemsHost.Children.Add(BuildItem(e)); break;
            }
        }
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
    /// <summary>在游標位置（裝置像素）顯示。</summary>
    public void ShowAt(int cursorX, int cursorY)
    {
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
