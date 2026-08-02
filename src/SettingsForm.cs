using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ArchClock;

/// <summary>
/// 設定画面。中身は WebView2 で web/settings.html を表示する。
/// ページ側は Fluent の見た目に寄せてあり、テーマ(明/暗)は OS 設定に追従する。
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly App _app;
    private WebView2? _web;
    private bool _ready;

    public SettingsForm(App app)
    {
        _app = app;

        Text          = "ArchClock の設定";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize   = new Size(880, 620);
        Size          = new Size(1080, 760);
        BackColor     = SystemInformation.HighContrast ? Color.Black
                        : (Theme.IsDark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243));

        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath); }
        catch { }

        Load += async (_, __) => await InitAsync();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyToWindow(Handle);          // タイトルバーを暗色に合わせる
    }

    private async Task InitAsync()
    {
        var page = Path.Combine(Paths.WebDir, "settings.html");
        if (!File.Exists(page))
        {
            MessageBox.Show(this, $"settings.html が見つかりません:\n{page}", "ArchClock");
            Close();
            return;
        }

        _web = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Theme.IsDark ? Color.FromArgb(32, 32, 32) : Color.White,
        };
        Controls.Add(_web);

        try
        {
            var env = await CoreWebView2Environment.CreateAsync(
                null, Path.Combine(Paths.DataDir, "WebView2"));
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex) { Log.Write(ex); Close(); return; }

        var s = _web.CoreWebView2.Settings;
        s.AreDefaultContextMenusEnabled    = false;
        s.IsStatusBarEnabled               = false;
        s.IsZoomControlEnabled             = false;
        s.AreBrowserAcceleratorKeysEnabled = false;

        _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "assets.local", Paths.DataDir, CoreWebView2HostResourceAccessKind.Allow);

        _web.CoreWebView2.WebMessageReceived += async (_, e) =>
        {
            try { await HandleAsync(e.WebMessageAsJson); }
            catch (Exception ex) { Log.Write(ex); }
        };

        _web.CoreWebView2.Navigate(new Uri(page).AbsoluteUri);
    }

    private void Send(object payload)
    {
        if (_web?.CoreWebView2 is null) return;
        try { _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOpts.Web)); }
        catch (Exception ex) { Log.Write(ex); }
    }

    private async Task HandleAsync(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("t", out var tp)) return;

        switch (tp.GetString())
        {
            case "ready":
                _ready = true;
                SendState();
                FlushFocus();
                break;

            case "save":
                {
                    var cfg = root.GetProperty("config").Deserialize<AppConfig>(
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (cfg is not null) _app.ApplyConfigFromSettings(cfg);
                    // ここで状態を送り返さない。送り返すとページが一覧を作り直し、
                    // 開いていた項目が閉じてスクロールも戻ってしまう。
                    // 送り主はページなので、ページ側は既に正しい状態を持っている。
                    break;
                }

            case "listApps":
                StartAppListing(root.TryGetProperty("refresh", out var rf) && rf.GetBoolean());
                break;

            case "needIcons":
                QueueIcons(root);
                break;

            case "pickFile":
                {
                    using var dlg = new OpenFileDialog
                    {
                        Title  = "追加するアプリを選ぶ",
                        Filter = "プログラム・ショートカット (*.exe;*.lnk;*.url)|*.exe;*.lnk;*.url|すべてのファイル (*.*)|*.*",
                    };
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                        Send(new
                        {
                            t    = "picked",
                            name = Path.GetFileNameWithoutExtension(dlg.FileName),
                            path = dlg.FileName,
                            icon = Shortcuts.EnsureIcon(dlg.FileName),
                        });
                    break;
                }


            case "editMode":
                _app.EditMode = root.GetProperty("on").GetBoolean();
                break;

            case "capture":
                {
                    var folder = Path.Combine(Paths.DataDir, "captures");
                    var made = await _app.CaptureAllAsync(folder);
                    Send(new { t = "captured", files = made });
                    break;
                }

            case "reload":
                _app.ReloadAll();
                break;

            case "resetConfig":
                _app.ApplyConfig(AppConfig.Default());
                SendState();
                break;
        }
    }

    private Thread? _iconThread;
    private readonly System.Collections.Concurrent.BlockingCollection<string> _iconQueue = new();

    /// <summary>アプリ一覧を名前だけで先に出す。アイコンはページが見えている分を要求してくる。</summary>
    private void StartAppListing(bool refresh)
    {
        List<Shortcuts.AppEntry> list;
        try { list = Shortcuts.ListApps(refresh); }
        catch (Exception ex) { Log.Write(ex); return; }

        Send(new
        {
            t = "apps",
            apps = list.Select(a => new { name = a.Name, path = a.Path, icon = (string?)null }),
        });
        EnsureIconWorker();
    }

    /// <summary>
    /// 見えている分のアイコンだけを取りに行く。
    /// 数百件を頭から全部描くと1分近くかかるので、要求されたものを優先する。
    /// </summary>
    private void QueueIcons(JsonElement root)
    {
        if (!root.TryGetProperty("paths", out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        EnsureIconWorker();
        foreach (var p in arr.EnumerateArray())
        {
            var s = p.GetString();
            if (!string.IsNullOrWhiteSpace(s)) { try { _iconQueue.Add(s); } catch { } }
        }
    }

    /// <summary>シェルのアイコン取得は STA から呼ぶ必要がある。専用スレッドを1本だけ持つ。</summary>
    private void EnsureIconWorker()
    {
        if (_iconThread is { IsAlive: true }) return;

        _iconThread = new Thread(() =>
        {
            var batch = new List<object>(12);
            var done  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Flush()
            {
                if (batch.Count == 0) return;
                var items = batch.ToArray();
                batch.Clear();
                try { BeginInvoke(new Action(() => Send(new { t = "icons", items }))); } catch { }
            }

            foreach (var path in _iconQueue.GetConsumingEnumerable())
            {
                if (IsDisposed) return;
                if (!done.Add(path)) continue;

                string? icon;
                try { icon = Shortcuts.EnsureIcon(path); }
                catch (Exception ex) { Log.Write(ex); continue; }
                if (icon is not null) batch.Add(new { path, icon });

                // 溜まったか、これ以上要求が無ければ送る
                if (batch.Count >= 12 || _iconQueue.Count == 0) Flush();
            }
        })
        { IsBackground = true };
        _iconThread.SetApartmentState(ApartmentState.STA);
        _iconThread.Start();
    }

    private (string? Device, string? Id)? _pendingFocus;

    /// <summary>
    /// 指定のウィジェットの項目まで開いて見せる。
    /// まだページの準備ができていなければ、できてから送る。
    /// </summary>
    public void FocusWidget(string? device, string? id)
    {
        if (device is null || id is null) return;
        _pendingFocus = (device, id);
        if (_ready) FlushFocus();
    }

    private void FlushFocus()
    {
        if (_pendingFocus is not { } f) return;
        _pendingFocus = null;
        Send(new { t = "focusWidget", device = f.Device, id = f.Id });
    }

    /// <summary>デスクトップ側での変更(移動・削除)を設定画面にも反映する。</summary>
    public void Refresh_()
    {
        try { BeginInvoke(new Action(SendState)); } catch { }
    }

    private void SendState()
    {
        if (!_ready) return;
        Send(new
        {
            t        = "state",
            config   = _app.Config,
            monitors = _app.Windows.Select(w => new
            {
                device  = w.Device,
                w       = w.MonitorBounds.Width,
                h       = w.MonitorBounds.Height,
                x       = w.MonitorBounds.X,
                y       = w.MonitorBounds.Y,
                dpi     = Native.MonitorDpi(w.MonitorBounds),
                scale   = Math.Round(Native.MonitorDpi(w.MonitorBounds) / 96.0 * 100),
                primary = Screen.PrimaryScreen?.DeviceName == w.Device,
            }),
            editMode = _app.EditMode,
            dark     = Theme.IsDark,
        });
    }

    /// <summary>実際に描かれている設定画面をそのまま PNG に落とす。検証用。</summary>
    public async Task<bool> CaptureAsync(string path)
    {
        if (_web?.CoreWebView2 is null) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var mem = new MemoryStream();
            await _web.CoreWebView2.CapturePreviewAsync(
                CoreWebView2CapturePreviewImageFormat.Png, mem);
            if (mem.Length == 0) return false;
            await File.WriteAllBytesAsync(path, mem.ToArray());
            return true;
        }
        catch (Exception ex) { Log.Write(ex); return false; }
    }

    /// <summary>検証用に、指定のタブへ切り替える。</summary>
    public async Task ShowTabAsync(string tab)
    {
        if (_web?.CoreWebView2 is null) return;
        try
        {
            await _web.CoreWebView2.ExecuteScriptAsync(
                $"document.querySelector('nav button[data-tab=\"{tab}\"]')?.click()");
        }
        catch (Exception ex) { Log.Write(ex); }
    }

    /// <summary>検証用に、任意のセレクタを押す。</summary>
    public async Task ClickAsync(string selector)
    {
        if (_web?.CoreWebView2 is null) return;
        try
        {
            await _web.CoreWebView2.ExecuteScriptAsync(
                $"document.querySelector('{selector}')?.click()");
        }
        catch (Exception ex) { Log.Write(ex); }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _web?.Dispose();
        base.OnFormClosed(e);
    }
}

/// <summary>OS の明暗テーマを見て、ウィンドウ枠にも反映する。</summary>
public static class Theme
{
    public static bool IsDark
    {
        get
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return (k?.GetValue("AppsUseLightTheme") as int?) == 0;
            }
            catch { return false; }
        }
    }

    public static void ApplyToWindow(IntPtr hwnd)
    {
        try
        {
            int dark = IsDark ? 1 : 0;
            // DWMWA_USE_IMMERSIVE_DARK_MODE = 20
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
