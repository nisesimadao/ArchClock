using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ArchClock;

/// <summary>
/// ショートカットウィジェット用。
///
/// アプリの一覧は AppsFolder を列挙する。ここはスタートの「すべてのアプリ」の実体で、
/// 従来の .exe/.lnk だけでなくストアアプリも含まれる。スタートメニューの .lnk を
/// 漁る方式では、ストアアプリも、ショートカットを作っていないものも拾えない。
/// </summary>
public static class Shortcuts
{
    public sealed record AppEntry(string Name, string Path, string? Icon);

    public static string IconDir => System.IO.Path.Combine(Paths.DataDir, "icons");

    private const string AppsFolderPrefix = @"shell:AppsFolder\";

    // ================= 一覧 =================

    private static List<AppEntry>? _cache;

    /// <summary>インストールされているアプリを名前順で返す。初回だけ少し時間がかかる。</summary>
    public static List<AppEntry> ListApps(bool refresh = false)
    {
        if (!refresh && _cache is not null) return _cache;

        var found = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);

        // 本命: AppsFolder (Win32 + ストアアプリ)
        foreach (var (name, id) in EnumAppsFolder())
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
            if (LooksLikeNoise(name)) continue;
            found[id] = new AppEntry(name, AppsFolderPrefix + id, null);
        }

        // 補い: デスクトップに置いてあるもの (自作ツールなど、AppsFolder に出ないもの)
        foreach (var dir in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                 })
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.lnk"))
                {
                    var n = System.IO.Path.GetFileNameWithoutExtension(f);
                    if (LooksLikeNoise(n)) continue;
                    if (found.Values.Any(v => v.Name.Equals(n, StringComparison.OrdinalIgnoreCase))) continue;
                    found[f] = new AppEntry(n, f, null);
                }
            }
            catch (Exception ex) { Log.Write(ex); }
        }

        _cache = found.Values
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return _cache;
    }

    private static bool LooksLikeNoise(string name)
        => name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase)
        || name.Contains("アンインストール")
        || name.Contains("をアンインストール");

    /// <summary>AppsFolder を Shell の COM 越しに読む。</summary>
    private static List<(string Name, string Id)> EnumAppsFolder()
    {
        var list = new List<(string, string)>();
        try
        {
            var t = Type.GetTypeFromProgID("Shell.Application");
            if (t is null) return list;
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            if (folder is null) return list;

            dynamic items = folder.Items();
            int count = items.Count;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    dynamic it = items.Item(i);
                    list.Add(((string)it.Name, (string)it.Path));
                }
                catch { }
            }
        }
        catch (Exception ex) { Log.Write($"AppsFolder を読めません: {ex.Message}"); }
        return list;
    }

    // ================= アイコン =================

    /// <summary>アイコンを PNG にして、web から参照できる URL を返す。同じものは使い回す。</summary>
    public static string? EnsureIcon(string target)
    {
        try
        {
            Directory.CreateDirectory(IconDir);

            var key  = Hash(target);
            var file = System.IO.Path.Combine(IconDir, key + ".png");
            if (File.Exists(file)) return $"https://assets.local/icons/{key}.png";

            using var bmp = Render(target, 256);
            if (bmp is null) return null;
            bmp.Save(file, ImageFormat.Png);
            return $"https://assets.local/icons/{key}.png";
        }
        catch (Exception ex) { Log.Write(ex); return null; }
    }

    /// <summary>
    /// シェルに絵を描かせる。ストアアプリでも実行ファイルでもフォルダでも同じ扱いで取れる。
    /// </summary>
    private static Bitmap? Render(string parsingName, int size)
    {
        IntPtr hbmp = IntPtr.Zero;
        try
        {
            var guid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref guid, out var factory) != 0
                || factory is null)
                return FallbackIcon(parsingName);

            try
            {
                // ICONONLY: 画像ファイルの中身ではなく必ずアイコンを出させる
                // BIGGERSIZEOK: 小さい絵しか無いときに引き伸ばさせない
                int hr = factory.GetImage(new SIZE { cx = size, cy = size },
                                          SIIGBF.ICONONLY | SIIGBF.BIGGERSIZEOK, out hbmp);
                if (hr != 0 || hbmp == IntPtr.Zero) return FallbackIcon(parsingName);
                return FromHBitmap(hbmp);
            }
            finally { Marshal.ReleaseComObject(factory); }
        }
        catch (Exception ex) { Log.Write(ex); return FallbackIcon(parsingName); }
        finally { if (hbmp != IntPtr.Zero) DeleteObject(hbmp); }
    }

    private static Bitmap? FallbackIcon(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var ico = Icon.ExtractAssociatedIcon(path);
            return ico?.ToBitmap();
        }
        catch { return null; }
    }

    /// <summary>
    /// シェルが返す HBITMAP は 32bit の乗算済みアルファ。Image.FromHbitmap では
    /// 透過が落ちるので、ビットを直接読んで写し取る。
    ///
    /// 行の並びはトップダウンのこともボトムアップのこともある。決め打ちすると
    /// 上下が逆になるので、DIB のヘッダー(biHeight の符号)で判定する。
    /// </summary>
    private static Bitmap? FromHBitmap(IntPtr hbmp)
    {
        var ds = new DIBSECTION();
        int got = GetObject(hbmp, Marshal.SizeOf<DIBSECTION>(), ref ds);

        BITMAP bm;
        bool topDown;
        if (got >= Marshal.SizeOf<DIBSECTION>())
        {
            bm      = ds.dsBm;
            topDown = ds.dsBmih.biHeight < 0;   // 負ならトップダウン
        }
        else
        {
            // DIB section ではなかった。BITMAP だけ取り直す。
            bm = new BITMAP();
            if (GetObjectBitmap(hbmp, Marshal.SizeOf<BITMAP>(), ref bm) == 0) return null;
            topDown = false;
        }

        if (bm.bmBits == IntPtr.Zero || bm.bmBitsPixel != 32 || bm.bmWidth <= 0 || bm.bmHeight <= 0)
            return null;

        int h      = Math.Abs(bm.bmHeight);
        int stride = bm.bmWidthBytes;

        try
        {
            // ボトムアップなら最終行を先頭として、行送りを負で読ませる
            IntPtr scan0 = topDown ? bm.bmBits : IntPtr.Add(bm.bmBits, (h - 1) * stride);
            int    step  = topDown ? stride    : -stride;

            using var src = new Bitmap(bm.bmWidth, h, step, PixelFormat.Format32bppPArgb, scan0);

            // src はシェル側のメモリを指しているので、こちらの持ち物へ写す
            var dst = new Bitmap(bm.bmWidth, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(dst);
            g.Clear(Color.Transparent);
            g.DrawImageUnscaled(src, 0, 0);
            return dst;
        }
        catch (Exception ex)
        {
            Log.Write($"アイコンの読み取りに失敗 ({bm.bmWidth}x{h} stride={stride} topDown={topDown}): {ex.Message}");
            return null;
        }
    }

    // ================= 起動 =================

    public static void Launch(string target, string? args = null)
    {
        try
        {
            if (target.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
            {
                // ストアアプリも Win32 も、この形なら explorer が面倒を見てくれる
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"")
                {
                    UseShellExecute = true,
                });
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName        = target,
                Arguments       = args ?? "",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { Log.Write($"起動できません: {target} / {ex.Message}"); }
    }

    private static string Hash(string s)
    {
        var bytes = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(s.ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    // ================= P/Invoke =================

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [Flags]
    private enum SIIGBF
    {
        RESIZETOFIT   = 0x00,
        BIGGERSIZEOK  = 0x01,
        ICONONLY      = 0x04,
        THUMBNAILONLY = 0x08,
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DIBSECTION
    {
        public BITMAP dsBm;
        public BITMAPINFOHEADER dsBmih;
        public uint dsBitfields0, dsBitfields1, dsBitfields2;
        public IntPtr dshSection;
        public uint dsOffset;
    }

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, ref DIBSECTION lpvObject);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetObjectBitmap(IntPtr hgdiobj, int cbBuffer, ref BITMAP lpvObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
