using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ArchClock;

/// <summary>
/// モニター1枚ぶんのウィジェット面。デスクトップの壁紙レイヤーにぶら下がる。
///
/// 生涯に一度だけ SetParent する。二度目を呼ぶと WinForms がハンドルを作り直し、
/// 中の WebView2 が破棄済みになって落ちるため、explorer が再起動した場合は
/// プロセスごと入れ替える(App 側で面倒を見る)。
/// </summary>
public sealed class DesktopWindow : Form
{
    public string Device { get; }
    public Rectangle MonitorBounds { get; }
    public IntPtr BornUnderProgman { get; private set; } = IntPtr.Zero;

    /// <summary>
    /// 物理ピクセル ÷ CSS ピクセル。ページが実際に何 CSS px で描かれているかを
    /// 測った値を使う。壁紙レイヤーの子になった窓の DeviceDpi は親(主モニター)の
    /// 値を返すことがあり、当てにできない。
    /// </summary>
    public double CssScale { get; private set; } = 1.0;

    public void ReportViewport(double cssW, double cssH, double dpr)
    {
        if (cssW <= 0) return;
        CssScale = MonitorBounds.Width / cssW;
        Log.Write($"{Device}: 物理 {MonitorBounds.Width}x{MonitorBounds.Height} / " +
                  $"CSS {cssW:F0}x{cssH:F0} / dpr={dpr} => scale={CssScale:F3} " +
                  $"(Control.DeviceDpi={DeviceDpi} → {DeviceDpi / 96.0:F3})");
    }

    private readonly App _app;
    private readonly int _index;
    private WebView2? _web;
    private string _wallpaperKey = "";
    private int _wallpaperVersion;
    private bool _pageReady;
    private readonly Queue<string> _pending = new();

    public DesktopWindow(App app, Screen screen, int index)
    {
        _app          = app;
        _index        = index;
        Device        = screen.DeviceName;
        MonitorBounds = screen.Bounds;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar   = false;
        StartPosition   = FormStartPosition.Manual;
        BackColor       = Color.Black;
        Bounds          = screen.Bounds;
        TopMost         = false;
    }

    // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW : フォーカスを奪わず Alt+Tab にも出さない。
    // 親はここでは渡さない。FormBorderStyle.None の Form には WS_POPUP が付いており
    // WS_CHILD と排他なので、cp.Parent を指定しても無視される。
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x00000080;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    public async Task InitAsync()
    {
        var page = Path.Combine(Paths.WebDir, "index.html");
        if (!File.Exists(page)) { Log.Write($"index.html がありません: {page}"); return; }

        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
        Controls.Add(_web);

        try
        {
            var env = await CoreWebView2Environment.CreateAsync(
                null, Path.Combine(Paths.DataDir, "WebView2"));
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex) { Log.Write(ex); return; }

        var s = _web.CoreWebView2.Settings;
        s.AreDefaultContextMenusEnabled    = false;
        s.AreDevToolsEnabled               = false;
        s.IsStatusBarEnabled               = false;
        s.IsZoomControlEnabled             = false;
        s.AreBrowserAcceleratorKeysEnabled = false;

        // 生成した壁紙とアイコンを web から参照できるようにする
        _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "assets.local", Paths.DataDir, CoreWebView2HostResourceAccessKind.Allow);

        _web.CoreWebView2.WebMessageReceived += (_, e) =>
        {
            try { _app.HandleMessage(this, e.WebMessageAsJson); }
            catch (Exception ex) { Log.Write(ex); }
        };

        _web.CoreWebView2.Navigate(new Uri(page).AbsoluteUri);

        AttachToDesktop();
    }

    /// <summary>デスクトップの壁紙レイヤーにぶら下げる。生涯に一度だけ。</summary>
    private void AttachToDesktop()
    {
        IntPtr layer = DesktopLayer.Resolve();
        if (layer == IntPtr.Zero) { Log.Write("壁紙レイヤーが見つかりません"); return; }

        Native.SetParent(Handle, layer);
        BornUnderProgman = DesktopLayer.Progman();

        // 親の左上を原点として、担当モニターの位置・大きさに合わせる
        int x = MonitorBounds.X, y = MonitorBounds.Y;
        if (Native.GetWindowRect(layer, out var pr))
        {
            x = MonitorBounds.X - pr.Left;
            y = MonitorBounds.Y - pr.Top;
        }
        // z順は触らない。HWND_BOTTOM にすると壁紙の裏に回って見えなくなる。
        Native.SetWindowPos(Handle, IntPtr.Zero, x, y, MonitorBounds.Width, MonitorBounds.Height,
                            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | Native.SWP_NOZORDER);
    }

    // ---------------- ページとのやり取り ----------------

    public void MarkReady()
    {
        _pageReady = true;
        while (_pending.Count > 0) Send(_pending.Dequeue());
    }

    public void Send(string json)
    {
        if (_web?.CoreWebView2 is null || !_pageReady)
        {
            if (_pending.Count < 64) _pending.Enqueue(json);
            return;
        }
        try { _web.CoreWebView2.PostWebMessageAsJson(json); }
        catch (Exception ex) { Log.Write(ex); }
    }

    public void SendInit(AppConfig cfg)
    {
        // 壁紙レイヤーの子になった窓は親(主モニター)の DPI で描かれるため、
        // 拡大率の違うモニターではウィジェットが意図した大きさにならない。
        // そのモニター本来の拡大率と、実際に描かれている倍率の比を渡して、
        // ページ側で打ち消してもらう。
        double renderScale = CssScale;                                  // 実際 (例 1.5)
        double dpiScale    = Native.MonitorDpi(MonitorBounds) / 96.0;   // 本来 (例 1.25)
        double uiScale     = renderScale > 0 ? dpiScale / renderScale : 1.0;

        var payload = new
        {
            t = "init",
            device = Device,
            monitor = new
            {
                w = MonitorBounds.Width,
                h = MonitorBounds.Height,
                cssW = MonitorBounds.Width  / renderScale,
                cssH = MonitorBounds.Height / renderScale,
                dpi = Native.MonitorDpi(MonitorBounds),
                scale = renderScale,
                dpiScale,
                uiScale,
                primary = Screen.PrimaryScreen?.DeviceName == Device,
            },
            theme   = cfg.Theme,
            widgets = cfg.ForMonitor(Device),
        };
        Send(JsonSerializer.Serialize(payload, JsonOpts.Web));
    }

    /// <summary>壁紙を作り直して差し込む。変化がなければ何もしない。</summary>
    public void RefreshWallpaper(bool show)
    {
        if (!show)
        {
            _wallpaperKey = "off";
            Send("""{"t":"wallpaper","url":null}""");
            return;
        }

        try
        {
            var desc = Wallpaper.Describe(MonitorBounds);
            var key  = Wallpaper.Fingerprint(desc, MonitorBounds);
            if (key == _wallpaperKey) return;
            _wallpaperKey = key;

            var virt = SystemInformation.VirtualScreen;
            using var bmp = Wallpaper.Compose(desc, MonitorBounds, virt);

            var file = Path.Combine(Paths.DataDir, $"bg{_index}.jpg");
            SaveJpeg(bmp, file, 90L);

            _wallpaperVersion++;
            var url = $"https://assets.local/bg{_index}.jpg?v={_wallpaperVersion}";
            Send(JsonSerializer.Serialize(new { t = "wallpaper", url }, JsonOpts.Web));
        }
        catch (Exception ex) { Log.Write(ex); }
    }

    private static void SaveJpeg(Bitmap bmp, string file, long quality)
    {
        var enc = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
            .First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
        using var p = new System.Drawing.Imaging.EncoderParameters(1);
        p.Param[0] = new System.Drawing.Imaging.EncoderParameter(
            System.Drawing.Imaging.Encoder.Quality, quality);
        bmp.Save(file, enc, p);
    }

    public void Reload()
    {
        _pageReady    = false;
        _wallpaperKey = "";
        try { _web?.CoreWebView2?.Reload(); } catch (Exception ex) { Log.Write(ex); }
    }

    /// <summary>実際に描かれている内容をそのまま PNG に落とす。検証用。</summary>
    public async Task<bool> CaptureAsync(string path)
    {
        if (_web?.CoreWebView2 is null) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // 途中で失敗しても前回の画像を壊さないよう、メモリに撮ってから書き出す
            using var mem = new MemoryStream();
            await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, mem);
            if (mem.Length == 0) return false;

            await File.WriteAllBytesAsync(path, mem.ToArray());
            return true;
        }
        catch (Exception ex) { Log.Write(ex); return false; }
    }

    /// <summary>この点が担当モニターの中か。仮想デスクトップの物理座標で受ける。</summary>
    public bool Contains(int x, int y) => MonitorBounds.Contains(x, y);

    /// <summary>物理座標をこの面の CSS 座標へ直す。</summary>
    public (double x, double y) ToCss(int x, int y)
        => ((x - MonitorBounds.X) / CssScale, (y - MonitorBounds.Y) / CssScale);

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _web?.Dispose();
        base.OnFormClosed(e);
    }
}

public static class JsonOpts
{
    public static readonly JsonSerializerOptions Web = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
