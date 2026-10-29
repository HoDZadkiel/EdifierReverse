using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace EdifierTray.Ui;

/// <summary>圖示：深色圓盤、耳機輪廓，外圈是電量環。造型與配色對應 Python 版 logo.py。</summary>
public static class LogoRenderer
{
    public static Color RingColor(int? battery, bool connected)
    {
        if (!connected || battery is null) return Color.FromArgb(120, 120, 120);
        if (battery >= 50) return Color.FromArgb(108, 203, 95);
        if (battery >= 20) return Color.FromArgb(255, 170, 40);
        return Color.FromArgb(255, 99, 97);
    }

    /// <param name="number">true 且已連線有電量時，圓盤中央畫電量數字取代耳機輪廓（托盤小圖示用）。</param>
    public static Bitmap Render(int size, int? battery, bool connected, bool number = false)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        float u = size / 100f;
        RectangleF R(float x1, float y1, float x2, float y2) => new(x1 * u, y1 * u, (x2 - x1) * u, (y2 - y1) * u);

        using (var bg = new SolidBrush(Color.FromArgb(32, 32, 36))) g.FillEllipse(bg, R(2, 2, 98, 98));
        float w = Math.Max(1f, 7f * u);
        using (var track = new Pen(Color.FromArgb(70, 70, 76), w)) g.DrawArc(track, R(5, 5, 95, 95), 0, 360);
        if (connected && battery is int b && b > 0)
        {
            using var ring = new Pen(RingColor(battery, true), w);
            g.DrawArc(ring, R(5, 5, 95, 95), -90, 360f * Math.Clamp(b, 0, 100) / 100f);
        }

        var fg = connected ? Color.White : Color.FromArgb(170, 170, 170);
        if (number && connected && battery is int level)
        {
            var text = Math.Clamp(level, 0, 100).ToString();
            using var font = new Font("Segoe UI", size * (text.Length == 3 ? 0.4f : 0.55f), FontStyle.Bold, GraphicsUnit.Pixel);
            using var digits = new SolidBrush(fg);
            using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.DrawString(text, font, digits, new RectangleF(0, 0, size, size), center);
            return bmp;
        }
        using var band = new Pen(fg, Math.Max(1f, 6f * u));
        g.DrawArc(band, R(25, 24, 75, 74), 180, 180);       // 頭帶
        using var brush = new SolidBrush(fg);
        FillRounded(g, brush, R(21, 48, 35, 72), 5f * u);   // 左耳罩
        FillRounded(g, brush, R(65, 48, 79, 72), 5f * u);   // 右耳罩
        return bmp;
    }

    static void FillRounded(Graphics g, Brush b, RectangleF r, float radius)
    {
        float d = radius * 2;
        using var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(b, path);
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(nint handle);

    /// <summary>轉成托盤用的 Icon（複製一份再釋放 GDI handle，避免洩漏）。</summary>
    public static Icon ToIcon(Bitmap bmp)
    {
        var h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    public static BitmapSource ToImageSource(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = ms;
        img.EndInit();
        img.Freeze();
        return img;
    }
}
