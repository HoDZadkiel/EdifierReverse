"""托盤與面板的圖示：深色圓盤、耳機輪廓，外圈是電量環。以 4 倍解析度繪製再縮小，邊緣才會平滑。"""
from typing import Optional, Tuple

RGB = Tuple[int, int, int]
GRAY: RGB = (120, 120, 120)


def battery_color(battery: Optional[int], connected: bool) -> RGB:
    """電量環顏色：未連線灰、>=50 綠、>=20 橘、其餘紅（Windows 11 的語意色）。"""
    if not connected or battery is None:
        return GRAY
    if battery >= 50:
        return (108, 203, 95)
    if battery >= 20:
        return (255, 170, 40)
    return (255, 99, 97)


def _digit_font(px: int):
    """電量數字用的粗體字型；找不到系統字型就退回 PIL 內建（較醜但至少看得到數字）。"""
    from PIL import ImageFont

    for name in ("segoeuib.ttf", "arialbd.ttf"):
        try:
            return ImageFont.truetype(name, px)
        except OSError:
            pass
    return ImageFont.load_default(px)


def make_logo(size: int = 64, battery: Optional[int] = None, connected: bool = True, number: bool = False):
    """回傳 RGBA 的 PIL Image。未連線時整體變灰、電量環只畫底軌。
    number=True 且已連線有電量時，圓盤中央畫電量數字取代耳機輪廓（托盤小圖示用）。"""
    from PIL import Image, ImageDraw

    s = 4
    n = size * s
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    u = n / 100.0  # 以 100x100 的座標思考

    def box(x1, y1, x2, y2):
        return [x1 * u, y1 * u, x2 * u, y2 * u]

    d.ellipse(box(2, 2, 98, 98), fill=(32, 32, 36, 255))
    ring = battery_color(battery, connected)
    track = (70, 70, 76, 255)
    w = max(1, round(7 * u))
    d.arc(box(5, 5, 95, 95), 0, 360, fill=track, width=w)
    if connected and battery is not None:
        sweep = 360 * max(0, min(100, battery)) / 100.0
        if sweep > 0:
            d.arc(box(5, 5, 95, 95), -90, -90 + sweep, fill=ring + (255,), width=w)

    fg = (255, 255, 255, 255) if connected else (170, 170, 170, 255)
    if number and connected and battery is not None:
        text = str(max(0, min(100, battery)))
        d.text((n / 2, n / 2), text, font=_digit_font(round(n * (0.4 if len(text) == 3 else 0.55))), fill=fg, anchor="mm")
        return img.resize((size, size), Image.LANCZOS)
    d.arc(box(25, 24, 75, 74), 180, 360, fill=fg, width=max(1, round(6 * u)))   # 頭帶
    d.rounded_rectangle(box(21, 48, 35, 72), radius=5 * u, fill=fg)             # 左耳罩
    d.rounded_rectangle(box(65, 48, 79, 72), radius=5 * u, fill=fg)             # 右耳罩
    return img.resize((size, size), Image.LANCZOS)


def save_ico(path: str, sizes=(16, 24, 32, 48, 64, 128, 256)) -> None:
    """輸出多尺寸 .ico（給 C# 版與打包用）。"""
    base = make_logo(256, None, True)
    base.save(path, format="ICO", sizes=[(s, s) for s in sizes])
