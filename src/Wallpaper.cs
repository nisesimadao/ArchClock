using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ArchClock;

/// <summary>
/// 壁紙の取得と、モニターごとの正確な切り出し。
///
/// Windows は 8 以降 IDesktopWallpaper でモニターごとに別画像を持てる。スライドショー中も
/// このAPIは「今表示されている画像」を返すので、レジストリを読むより確実。
/// 取れない環境(古いビルド/COM失敗)ではレジストリに落とす。
/// </summary>
public static class Wallpaper
{
    public enum Fit { Center = 0, Tile = 1, Stretch = 2, Fit_ = 3, Fill = 4, Span = 5 }

    public sealed record Desc(string? Path, Fit Position, Color Background);

    /// <summary>指定モニター(物理座標の矩形で識別)の壁紙を得る。</summary>
    public static Desc Describe(Rectangle monitorBounds)
    {
        try
        {
            var dw = (IDesktopWallpaper)new DesktopWallpaperClass();

            Fit pos = (Fit)dw.GetPosition();
            Color bg = ColorFromCOLORREF(dw.GetBackgroundColor());

            // モニターIDは \\?\DISPLAY#... 形式。矩形で突き合わせる方が確実。
            uint count = dw.GetMonitorDevicePathCount();
            for (uint i = 0; i < count; i++)
            {
                string id = dw.GetMonitorDevicePathAt(i);
                if (string.IsNullOrEmpty(id)) continue;

                RECT r;
                try { r = dw.GetMonitorRECT(id); }
                catch { continue; }   // 無効化されたモニターは例外になる

                var rect = Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom);
                if (rect != monitorBounds) continue;

                string? p = null;
                try { p = dw.GetWallpaper(id); } catch { }
                if (string.IsNullOrWhiteSpace(p)) p = null;
                return new Desc(p, pos, bg);
            }

            // 矩形が一致しない(Span など)ときはモニター指定なしで取る
            string? any = null;
            try { any = dw.GetWallpaper(null); } catch { }
            return new Desc(string.IsNullOrWhiteSpace(any) ? null : any, pos, bg);
        }
        catch (Exception ex)
        {
            Log.Write($"IDesktopWallpaper 失敗、レジストリにフォールバック: {ex.Message}");
            return FromRegistry();
        }
    }

    private static Desc FromRegistry()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
        var path  = k?.GetValue("WallPaper") as string;
        var style = (k?.GetValue("WallpaperStyle") as string) ?? "10";
        var tile  = (k?.GetValue("TileWallpaper") as string) ?? "0";

        Fit pos = tile == "1" ? Fit.Tile : style switch
        {
            "0"  => Fit.Center,
            "2"  => Fit.Stretch,
            "6"  => Fit.Fit_,
            "22" => Fit.Span,
            _    => Fit.Fill,
        };

        Color bg = Color.Black;
        try
        {
            using var c = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors");
            if (c?.GetValue("Background") is string s)
            {
                var v = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (v.Length == 3) bg = Color.FromArgb(int.Parse(v[0]), int.Parse(v[1]), int.Parse(v[2]));
            }
        }
        catch { }

        return new Desc(string.IsNullOrWhiteSpace(path) ? null : path, pos, bg);
    }

    /// <summary>
    /// 対象モニターの物理ピクセルちょうどの下地画像を作る。
    /// Span のときは仮想デスクトップ全体に対して敷いた上で、このモニターの領域を切り出す。
    /// </summary>
    public static Bitmap Compose(Desc d, Rectangle monitor, Rectangle virtualScreen)
    {
        var dst = new Bitmap(monitor.Width, monitor.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode   = PixelOffsetMode.HighQuality;
        g.Clear(d.Background);

        if (d.Path is null || !File.Exists(d.Path)) return dst;   // 単色壁紙

        Image src;
        try { src = Image.FromFile(d.Path); }
        catch (Exception ex) { Log.Write(ex); return dst; }

        using (src)
        {
            switch (d.Position)
            {
                case Fit.Tile:
                    using (var brush = new TextureBrush(src, WrapMode.Tile))
                    {
                        // タイルは仮想デスクトップの原点から敷かれる
                        brush.TranslateTransform(-(monitor.X - virtualScreen.X) % src.Width,
                                                 -(monitor.Y - virtualScreen.Y) % src.Height);
                        g.FillRectangle(brush, 0, 0, monitor.Width, monitor.Height);
                    }
                    break;

                case Fit.Center:
                    g.DrawImage(src,
                                (monitor.Width  - src.Width)  / 2,
                                (monitor.Height - src.Height) / 2,
                                src.Width, src.Height);
                    break;

                case Fit.Stretch:
                    g.DrawImage(src, 0, 0, monitor.Width, monitor.Height);
                    break;

                case Fit.Span:
                    DrawSpan(g, src, monitor, virtualScreen);
                    break;

                case Fit.Fit_:
                    DrawScaled(g, src, monitor.Width, monitor.Height, contain: true);
                    break;

                default: // Fill
                    DrawScaled(g, src, monitor.Width, monitor.Height, contain: false);
                    break;
            }
        }

        return dst;
    }

    /// <summary>仮想デスクトップ全体を覆うように敷き、その中からこのモニターの範囲を写す。</summary>
    private static void DrawSpan(Graphics g, Image src, Rectangle monitor, Rectangle virt)
    {
        double scale = Math.Max((double)virt.Width / src.Width, (double)virt.Height / src.Height);
        double sw = src.Width * scale, sh = src.Height * scale;
        double ox = virt.X + (virt.Width  - sw) / 2;   // 仮想デスクトップ座標での画像左上
        double oy = virt.Y + (virt.Height - sh) / 2;

        g.DrawImage(src,
                    new RectangleF((float)(ox - monitor.X), (float)(oy - monitor.Y),
                                   (float)sw, (float)sh),
                    new RectangleF(0, 0, src.Width, src.Height),
                    GraphicsUnit.Pixel);
    }

    private static void DrawScaled(Graphics g, Image src, int w, int h, bool contain)
    {
        double sx = (double)w / src.Width, sy = (double)h / src.Height;
        double scale = contain ? Math.Min(sx, sy) : Math.Max(sx, sy);
        int dw = (int)Math.Round(src.Width * scale);
        int dh = (int)Math.Round(src.Height * scale);
        g.DrawImage(src, (w - dw) / 2, (h - dh) / 2, dw, dh);
    }

    /// <summary>この壁紙が今どういう状態かを表す指紋。変わったときだけ描き直すために使う。</summary>
    public static string Fingerprint(Desc d, Rectangle monitor)
    {
        long stamp = 0, size = 0;
        if (d.Path is not null)
        {
            try { var fi = new FileInfo(d.Path); stamp = fi.LastWriteTimeUtc.Ticks; size = fi.Length; }
            catch { }
        }
        return $"{d.Path}|{stamp}|{size}|{d.Position}|{d.Background.ToArgb()}|{monitor.Width}x{monitor.Height}";
    }

    private static Color ColorFromCOLORREF(uint c)
        => Color.FromArgb((int)(c & 0xFF), (int)((c >> 8) & 0xFF), (int)((c >> 16) & 0xFF));

    // ---------------- COM ----------------

    [ComImport, Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")]
    private class DesktopWallpaperClass { }

    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorID,
                          [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorID);
        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetMonitorDevicePathAt(uint monitorIndex);
        uint GetMonitorDevicePathCount();
        RECT GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID);
        void SetBackgroundColor(uint color);
        uint GetBackgroundColor();
        void SetPosition(int position);
        int GetPosition();
        // 以降(SetSlideshow など)は使わないので宣言しない
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }
}
