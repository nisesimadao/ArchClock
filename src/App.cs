using System.Diagnostics;
using System.Text.Json;

namespace ArchClock;

/// <summary>
/// 全体の取りまとめ。モニターごとの面、システムトレイ、設定画面、メトリクス、入力を持つ。
/// </summary>
public sealed class App : IDisposable
{
    public AppConfig Config { get; private set; } = null!;

    private readonly List<DesktopWindow> _windows = new();
    private readonly Metrics _metrics = new();
    private readonly DesktopInput _input = new();
    private Tray? _tray;
    private SettingsForm? _settings;

    private System.Windows.Forms.Timer? _tick;
    private int _tickCount;
    private bool _editMode;
    private bool _restarting;

    /// <summary>ページ側から届いた当たり判定の矩形。物理座標。</summary>
    private readonly Dictionary<string, List<HitRect>> _hits = new();
    private sealed record HitRect(string Id, string Device, int X, int Y, int W, int H,
                                  string? Target, string? Action);

    public void Start()
    {
        Config = AppConfig.Load();
        StartupRegistration.Apply(Config.RunAtStartup);

        BuildWindows();

        _tray = new Tray(this);
        _tray.Show();

        _input.HitTest = p => FindHit(p.X, p.Y) is not null;
        _input.OnClick = p => OnDesktopClick(p.X, p.Y);
        _input.OnDown  = p => OnDragStart(p.X, p.Y);
        _input.OnMove  = p => OnDrag(p.X, p.Y);
        _input.OnUp    = _ => OnDragEnd();
        _input.OnHover = OnHover;
        // %LOCALAPPDATA%\ArchClock\diagnose.on を置くと、押下のたびにログが残る
        _input.Diagnose = File.Exists(Path.Combine(Paths.DataDir, "diagnose.on"));
        _input.Start();

        _tick = new System.Windows.Forms.Timer { Interval = Math.Max(500, Config.MetricsIntervalMs) };
        _tick.Tick += (_, __) => OnTick();
        _tick.Start();

        // ディスプレイ構成が変わったら作り直す
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, __) => RebuildSoon();

        WatchCaptureRequests();
    }

    /// <summary>
    /// データフォルダに capture.request という空ファイルが置かれたら、実描画を PNG に書き出す。
    /// トレイを操作できない場面(自動テストなど)から見た目を確かめるための口。
    /// </summary>
    private FileSystemWatcher? _captureWatcher;
    private volatile bool _capturing;

    private void WatchCaptureRequests()
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            _captureWatcher = new FileSystemWatcher(Paths.DataDir, "*.request")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            // Created と Changed は同じ保存で両方飛んでくる。二重に走らせない。
            FileSystemEventHandler handler = (_, e) =>
            {
                try
                {
                    var name = Path.GetFileName(e.FullPath);

                    // 配置モードの切り替え。トレイを操作できない場面から確かめるための口。
                    if (name is "edit.request" or "editoff.request")
                    {
                        bool on = name == "edit.request";
                        var w0 = _windows.FirstOrDefault();
                        w0?.BeginInvoke(new Action(() =>
                        {
                            EditMode = on;
                            try { File.Delete(Path.Combine(Paths.DataDir, name)); } catch { }
                        }));
                        return;
                    }

                    if (name is not ("capture.request" or "settings.request")) return;
                    if (_capturing) return;
                    _capturing = true;

                    bool wantSettings = name == "settings.request";
                    var main = _windows.FirstOrDefault();
                    if (main is null) { _capturing = false; return; }

                    main.BeginInvoke(new Action(async () =>
                    {
                        try
                        {
                            await Task.Delay(300);          // 書き込み完了を待つ
                            var folder = Path.Combine(Paths.DataDir, "captures");
                            Directory.CreateDirectory(folder);

                            var made = wantSettings
                                ? await CaptureSettingsAsync(folder)
                                : await CaptureAllAsync(folder);

                            try { File.Delete(Path.Combine(Paths.DataDir, name)); } catch { }
                            File.WriteAllText(Path.Combine(Paths.DataDir, "capture.done"),
                                              string.Join(Environment.NewLine, made));
                        }
                        catch (Exception ex) { Log.Write(ex); }
                        finally { _capturing = false; }
                    }));
                }
                catch (Exception ex) { Log.Write(ex); _capturing = false; }
            };
            _captureWatcher.Created += handler;
            _captureWatcher.Changed += handler;
        }
        catch (Exception ex) { Log.Write(ex); }
    }

    // ---------------- モニターごとの面 ----------------

    private void BuildWindows()
    {
        foreach (var w in _windows) { try { w.Close(); w.Dispose(); } catch { } }
        _windows.Clear();
        _hits.Clear();

        var screens = Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var win = new DesktopWindow(this, screens[i], i);
            _windows.Add(win);
            win.Show();
            _ = win.InitAsync();
        }
        Log.Write($"モニター {screens.Length} 枚に配置: " +
                  string.Join(", ", screens.Select(s => $"{s.DeviceName} {s.Bounds.Width}x{s.Bounds.Height}@({s.Bounds.X},{s.Bounds.Y})")));
    }

    private void RebuildSoon()
    {
        var t = new System.Windows.Forms.Timer { Interval = 1200 };
        t.Tick += (_, __) => { t.Stop(); t.Dispose(); BuildWindows(); };
        t.Start();
    }

    // ---------------- 毎回の更新 ----------------

    private void OnTick()
    {
        CheckDesktopAlive();
        if (_restarting) return;

        // 壁紙は毎回作り直すと重いので数回に一度だけ確認する
        if (_tickCount % 4 == 0)
            foreach (var w in _windows) w.RefreshWallpaper(Config.ShowWallpaper);

        if (NeedsMetrics())
        {
            var s = _metrics.Sample();
            var json = JsonSerializer.Serialize(new { t = "metrics", d = s }, JsonOpts.Web);
            foreach (var w in _windows) w.Send(json);
        }

        _tickCount++;
    }

    /// <summary>メトリクスを使うウィジェットが1つも無いなら採取自体をやめる。</summary>
    private bool NeedsMetrics()
    {
        string[] needs = ["cpu", "memory", "processes", "disk", "network"];
        return Config.Monitors.Values.Any(list =>
            list.Any(w => w.Visible && needs.Contains(w.Type)));
    }

    /// <summary>
    /// explorer.exe が再起動すると Progman ごと作り直され、面は孤児になって見えなくなる。
    /// 貼り直しは SetParent を伴い WebView2 を壊すので、プロセスごと入れ替える。
    /// </summary>
    private void CheckDesktopAlive()
    {
        if (_restarting || _windows.Count == 0) return;

        var born = _windows[0].BornUnderProgman;
        if (born == IntPtr.Zero) return;

        IntPtr now = DesktopLayer.Progman();
        if (now == IntPtr.Zero || now == born) return;

        _restarting = true;
        Log.Write($"デスクトップが作り直されたため再起動します ({born} -> {now})");
        Restart();
    }

    public void Restart()
    {
        try
        {
            // 後継は Mutex を最大10秒待つので、先に起動してからこちらを落とす
            Process.Start(new ProcessStartInfo
            {
                FileName        = Environment.ProcessPath ?? Application.ExecutablePath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { Log.Write(ex); }

        // Application.Exit() では終わらないことがある(親を失った WebView2 の COM 例外で
        // メッセージループが片付かない)。確実に落とす。
        Environment.Exit(0);
    }

    // ---------------- ページからのメッセージ ----------------

    public void HandleMessage(DesktopWindow src, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("t", out var tp)) return;

        switch (tp.GetString())
        {
            case "ready":
                if (root.TryGetProperty("viewport", out var vp))
                {
                    try
                    {
                        src.ReportViewport(vp.GetProperty("w").GetDouble(),
                                           vp.GetProperty("h").GetDouble(),
                                           vp.GetProperty("dpr").GetDouble());
                    }
                    catch (Exception ex) { Log.Write(ex); }
                }
                src.MarkReady();
                src.SendInit(Config);
                src.RefreshWallpaper(Config.ShowWallpaper);
                src.Send(JsonSerializer.Serialize(new { t = "editMode", on = _editMode }, JsonOpts.Web));
                break;

            case "hits":
                UpdateHits(src, root);
                break;

            case "moveWidget":
                MoveWidget(src.Device,
                           root.GetProperty("id").GetString()!,
                           root.GetProperty("x").GetDouble(),
                           root.GetProperty("y").GetDouble());
                break;

            case "launch":
                var target = root.GetProperty("target").GetString();
                if (!string.IsNullOrWhiteSpace(target)) Shortcuts.Launch(target);
                break;

            case "needIcons":
                SendIcons(src, root);
                break;

            case "openSettings":
                OpenSettings();
                break;
        }
    }

    /// <summary>
    /// ショートカットのアイコンを用意して返す。
    /// 設定に URL を残しておくとキャッシュを作り直したときに画像切れになるため、
    /// 表示のたびにここで作り直す(ファイルがあれば使い回すので二度目以降は速い)。
    /// </summary>
    private void SendIcons(DesktopWindow src, JsonElement root)
    {
        if (!root.TryGetProperty("paths", out var arr) || arr.ValueKind != JsonValueKind.Array) return;

        var items = new List<object>();
        foreach (var p in arr.EnumerateArray())
        {
            var path = p.GetString();
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (items.Count >= 120) break;             // 念のための上限

            string? icon;
            try { icon = Shortcuts.EnsureIcon(path); }
            catch (Exception ex) { Log.Write(ex); continue; }
            if (icon is not null) items.Add(new { path, icon });
        }

        if (items.Count > 0)
            src.Send(JsonSerializer.Serialize(new { t = "icons", items }, JsonOpts.Web));
    }

    /// <summary>ページから当たり判定を受け取る。CSS座標で来るので物理座標に直して持つ。</summary>
    private void UpdateHits(DesktopWindow src, JsonElement root)
    {
        var list = new List<HitRect>();
        if (root.TryGetProperty("rects", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            double scale = src.CssScale;
            foreach (var r in arr.EnumerateArray())
            {
                try
                {
                    list.Add(new HitRect(
                        r.GetProperty("id").GetString() ?? "",
                        src.Device,
                        src.MonitorBounds.X + (int)(r.GetProperty("x").GetDouble() * scale),
                        src.MonitorBounds.Y + (int)(r.GetProperty("y").GetDouble() * scale),
                        (int)(r.GetProperty("w").GetDouble() * scale),
                        (int)(r.GetProperty("h").GetDouble() * scale),
                        r.TryGetProperty("target", out var t) ? t.GetString() : null,
                        r.TryGetProperty("action", out var a) ? a.GetString() : null));
                }
                catch { }
            }
        }
        _hits[src.Device] = list;
    }

    private HitRect? FindHit(int x, int y)
    {
        foreach (var (device, rects) in _hits)
            foreach (var r in rects)
                if (x >= r.X && x < r.X + r.W && y >= r.Y && y < r.Y + r.H)
                    return r;
        return null;
    }

    private DesktopWindow? WindowAt(int x, int y) => _windows.FirstOrDefault(w => w.Contains(x, y));

    // ---------------- デスクトップ上の操作 ----------------

    private HitRect? _dragging;
    private (int dx, int dy) _dragOffset;

    private void OnDragStart(int x, int y)
    {
        if (!_editMode) return;
        var hit = FindHit(x, y);
        // 掴めるのは本体だけ。ハンドルのボタンや「配置を終える」では動かさない。
        if (hit is null || hit.Action != "move") return;
        _dragging   = hit;
        _dragOffset = (x - hit.X, y - hit.Y);
    }

    private double _lastFx = -1, _lastFy = -1;

    private void OnDrag(int x, int y)
    {
        if (!_editMode || _dragging is null) return;

        // 送り先はカーソルの下の窓ではなく、そのウィジェットが載っている窓。
        // カーソルが隣のモニターへはみ出しても、掴んだものが迷子にならない。
        var win = _windows.FirstOrDefault(w => w.Device == _dragging.Device);
        if (win is null) return;

        int left = x - _dragOffset.dx;
        int top  = y - _dragOffset.dy;
        double fx = Math.Clamp((double)(left - win.MonitorBounds.X) / win.MonitorBounds.Width,  -0.02, 1.0);
        double fy = Math.Clamp((double)(top  - win.MonitorBounds.Y) / win.MonitorBounds.Height, -0.02, 1.0);

        // 同じ位置なら送らない。マウス移動は秒間数百回来る。
        if (Math.Abs(fx - _lastFx) < 0.0002 && Math.Abs(fy - _lastFy) < 0.0002) return;
        _lastFx = fx; _lastFy = fy;

        win.Send(JsonSerializer.Serialize(
            new { t = "previewMove", id = _dragging.Id, x = fx, y = fy }, JsonOpts.Web));
    }

    private void OnDragEnd()
    {
        if (_dragging is null) return;
        _dragging = null;
        _lastFx = _lastFy = -1;
        foreach (var w in _windows) w.Send("""{"t":"commitMove"}""");
    }

    private void OnDesktopClick(int x, int y)
    {
        var hit = FindHit(x, y);

        if (_editMode)
        {
            if (hit is null) return;
            switch (hit.Action)
            {
                case "done":   EditMode = false;                        break;
                case "remove": RemoveWidget(hit.Device, hit.Id);        break;
                case "config": OpenSettings(hit.Device, hit.Id);        break;
            }
            return;
        }

        if (hit?.Target is not { Length: > 0 }) return;

        // 押したことが分かるように、ページ側で光らせてから起動する
        var win = _windows.FirstOrDefault(w => w.Device == hit.Device);
        if (win is not null)
        {
            var (cx, cy) = win.ToCss(x, y);
            win.Send(JsonSerializer.Serialize(
                new { t = "launched", x = cx, y = cy }, JsonOpts.Web));
        }
        Shortcuts.Launch(hit.Target);
    }

    /// <summary>
    /// カーソル位置をページへ流す。フック越しではページが :hover を検知できないため、
    /// これが無いとショートカットが押せるものに見えない。
    /// </summary>
    private string? _hoverDevice;

    private void OnHover(DesktopInput.Point2? p)
    {
        if (_restarting) return;

        if (p is null)
        {
            if (_hoverDevice is not null)
            {
                foreach (var w in _windows) w.Send("""{"t":"hover","x":null,"y":null}""");
                _hoverDevice = null;
            }
            return;
        }

        var win = WindowAt(p.X, p.Y);
        if (win is null) return;

        // 別のモニターへ移ったら、前の面のホバーを消す
        if (_hoverDevice is not null && _hoverDevice != win.Device)
        {
            var prev = _windows.FirstOrDefault(w => w.Device == _hoverDevice);
            prev?.Send("""{"t":"hover","x":null,"y":null}""");
        }
        _hoverDevice = win.Device;

        var (cx, cy) = win.ToCss(p.X, p.Y);
        win.Send(JsonSerializer.Serialize(
            new { t = "hover", x = cx, y = cy, edit = _editMode }, JsonOpts.Web));
    }

    private void MoveWidget(string device, string id, double x, double y)
    {
        var w = Config.ForMonitor(device).FirstOrDefault(w => w.Id == id);
        if (w is null) return;
        w.X = Math.Clamp(x, -0.5, 1.5);
        w.Y = Math.Clamp(y, 0, 1);
        w.Anchored = true;
        Config.Save();
        // 設定画面は位置を表示していないので、動かしただけでは作り直さない。
    }

    private void RemoveWidget(string device, string id)
    {
        var list = Config.ForMonitor(device);
        int n = list.RemoveAll(w => w.Id == id);
        if (n == 0) return;

        Config.Save();
        var win = _windows.FirstOrDefault(w => w.Device == device);
        win?.SendInit(Config);
        NotifySettings();
    }

    /// <summary>設定画面が開いていれば、そちらの表示も合わせる。</summary>
    private void NotifySettings()
    {
        if (_settings is { IsDisposed: false }) _settings.Refresh_();
    }

    // ---------------- トレイからの操作 ----------------

    public bool EditMode
    {
        get => _editMode;
        set
        {
            _editMode = value;
            var json = JsonSerializer.Serialize(new { t = "editMode", on = value }, JsonOpts.Web);
            foreach (var w in _windows) w.Send(json);
        }
    }

    /// <summary>
    /// 設定画面を開く。ウィジェットを指定すると、その項目まで開いて見せる。
    /// (デスクトップのハンドルの歯車から呼ばれる)
    /// </summary>
    public void OpenSettings(string? device = null, string? widgetId = null)
    {
        if (_settings is { IsDisposed: false })
        {
            if (_settings.WindowState == FormWindowState.Minimized)
                _settings.WindowState = FormWindowState.Normal;
            _settings.Activate();
            _settings.FocusWidget(device, widgetId);
            return;
        }

        _settings = new SettingsForm(this);
        _settings.FocusWidget(device, widgetId);   // 準備できてから送られる
        _settings.Show();
    }

    /// <summary>
    /// 設定画面からの保存。
    ///
    /// 位置(X/Y)はデスクトップでのドラッグが持ち主で、設定画面は表示していない。
    /// 設定画面が持っている config は開いた時点のもののため、そのまま受け取ると
    /// あとから動かした位置を古い値で上書きしてしまう。位置だけは今の値を残す。
    /// </summary>
    public void ApplyConfigFromSettings(AppConfig cfg)
    {
        foreach (var (device, incoming) in cfg.Monitors)
        {
            if (!Config.Monitors.TryGetValue(device, out var current)) continue;
            foreach (var w in incoming)
            {
                var live = current.FirstOrDefault(c => c.Id == w.Id);
                if (live is null) continue;          // 設定画面で追加されたもの
                w.X = live.X;
                w.Y = live.Y;
                w.Anchored = live.Anchored;
            }
        }
        ApplyConfig(cfg);
    }

    /// <summary>設定が変わったら全部の面へ反映して保存する。</summary>
    public void ApplyConfig(AppConfig cfg)
    {
        Config = cfg;
        Config.Save();
        StartupRegistration.Apply(Config.RunAtStartup);
        if (_tick is not null) _tick.Interval = Math.Max(500, Config.MetricsIntervalMs);

        foreach (var w in _windows)
        {
            w.SendInit(Config);
            w.RefreshWallpaper(Config.ShowWallpaper);
        }
    }

    public void ReloadAll()
    {
        foreach (var w in _windows) w.Reload();
    }

    /// <summary>実際に描かれている内容を PNG に保存する。</summary>
    public async Task<List<string>> CaptureAllAsync(string folder)
    {
        var made = new List<string>();
        for (int i = 0; i < _windows.Count; i++)
        {
            var name = _windows[i].Device.Replace(@"\\.\", "").Replace(@"\", "_");
            var path = Path.Combine(folder, $"archclock_{name}.png");
            if (await _windows[i].CaptureAsync(path)) made.Add(path);
        }
        return made;
    }

    public IReadOnlyList<DesktopWindow> Windows => _windows;

    /// <summary>設定画面を開き、各タブの実描画を PNG に落とす。検証用。</summary>
    public async Task<List<string>> CaptureSettingsAsync(string folder)
    {
        OpenSettings();
        var made = new List<string>();
        if (_settings is null) return made;

        await Task.Delay(2500);                      // WebView2 の初期化と初回描画を待つ

        foreach (var tab in new[] { "general", "widgets", "theme", "monitors" })
        {
            await _settings.ShowTabAsync(tab);
            await Task.Delay(450);
            var path = Path.Combine(folder, $"settings_{tab}.png");
            if (await _settings.CaptureAsync(path)) made.Add(path);
        }

        // 重ねて出す一覧も撮っておく
        await _settings.ShowTabAsync("widgets");
        await Task.Delay(250);
        await _settings.ClickAsync("#btnExplorer");
        await Task.Delay(500);
        var ex1 = Path.Combine(folder, "settings_explorer.png");
        if (await _settings.CaptureAsync(ex1)) made.Add(ex1);
        await _settings.ClickAsync("[data-close=\"explorer\"]");

        // アプリの選択も。ショートカットのウィジェットがある場合だけ。
        await Task.Delay(200);
        await _settings.ClickAsync(".witem .whead");     // 一つ開く
        await Task.Delay(200);
        await _settings.ClickAsync("[data-addapp]");
        await Task.Delay(2500);                          // 一覧とアイコンが流れてくるのを待つ
        var ex2 = Path.Combine(folder, "settings_picker.png");
        if (await _settings.CaptureAsync(ex2)) made.Add(ex2);
        await _settings.ClickAsync("[data-close=\"picker\"]");

        return made;
    }

    public void Dispose()
    {
        _tick?.Stop(); _tick?.Dispose();
        _captureWatcher?.Dispose();
        _input.Dispose();
        _metrics.Dispose();
        _tray?.Dispose();
        foreach (var w in _windows) { try { w.Dispose(); } catch { } }
    }
}

/// <summary>ログオン時の自動起動。スタートアップフォルダの .lnk で扱う。</summary>
public static class StartupRegistration
{
    private static string LinkPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), "ArchClock.lnk");

    public static void Apply(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                if (File.Exists(LinkPath)) File.Delete(LinkPath);
                return;
            }

            var exe = Environment.ProcessPath ?? Application.ExecutablePath;
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            var lnk = shell.CreateShortcut(LinkPath);
            lnk.TargetPath       = exe;
            lnk.WorkingDirectory = Path.GetDirectoryName(exe);
            lnk.Description      = "ArchClock — デスクトップウィジェット";
            lnk.Save();
        }
        catch (Exception ex) { Log.Write(ex); }
    }
}
