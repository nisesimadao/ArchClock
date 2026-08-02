using System.Drawing.Drawing2D;

namespace ArchClock;

/// <summary>システムトレイの常駐アイコンとメニュー。</summary>
public sealed class Tray : IDisposable
{
    private readonly App _app;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _editItem;
    private readonly ToolStripMenuItem _wallpaperItem;

    public Tray(App app)
    {
        _app = app;

        _editItem = new ToolStripMenuItem("ウィジェットを配置", null, (_, __) =>
        {
            _app.EditMode = !_app.EditMode;
            Sync();
        }) { CheckOnClick = false };

        _wallpaperItem = new ToolStripMenuItem("壁紙を下地にする", null, (_, __) =>
        {
            _app.Config.ShowWallpaper = !_app.Config.ShowWallpaper;
            _app.ApplyConfig(_app.Config);
            Sync();
        });

        var menu = new ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add(new ToolStripMenuItem("設定を開く…", null, (_, __) => _app.OpenSettings())
        {
            Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold)
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_editItem);
        menu.Items.Add(_wallpaperItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("表示を再読み込み", null, (_, __) => _app.ReloadAll()));
        menu.Items.Add(new ToolStripMenuItem("スクリーンショットを保存", null, async (_, __) => await Capture()));
        menu.Items.Add(new ToolStripMenuItem("設定フォルダを開く", null, (_, __) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Paths.DataDir) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write(ex); }
        }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("終了", null, (_, __) =>
        {
            _icon.Visible = false;
            Application.Exit();
        }));

        menu.Opening += (_, __) => Sync();

        _icon = new NotifyIcon
        {
            Icon             = BuildIcon(),
            Text             = "ArchClock",
            Visible          = false,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, __) => _app.OpenSettings();
    }

    private void Sync()
    {
        _editItem.Checked      = _app.EditMode;
        _editItem.Text         = _app.EditMode ? "配置を終える" : "ウィジェットを配置";
        _wallpaperItem.Checked = _app.Config.ShowWallpaper;
    }

    private async Task Capture()
    {
        var folder = Path.Combine(Paths.DataDir, "captures");
        Directory.CreateDirectory(folder);
        var made = await _app.CaptureAllAsync(folder);
        if (made.Count > 0)
        {
            _icon.BalloonTipTitle = "スクリーンショットを保存しました";
            _icon.BalloonTipText  = string.Join("\n", made.Select(Path.GetFileName));
            _icon.ShowBalloonTip(4000);
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch (Exception ex) { Log.Write(ex); }
        }
    }

    /// <summary>
    /// トレイのアイコン。exe に埋めたものを使う。
    /// 取り出せなければその場で描いて、アイコンが無い状態にはしない。
    /// </summary>
    private static Icon BuildIcon()
    {
        try
        {
            var exe = Environment.ProcessPath ?? Application.ExecutablePath;
            var ico = Icon.ExtractAssociatedIcon(exe);
            if (ico is not null) return ico;
        }
        catch (Exception ex) { Log.Write(ex); }

        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var ring = new Pen(Color.White, 2.4f);
            g.DrawEllipse(ring, 4, 4, 23, 23);
            using var hand = new Pen(Color.White, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(hand, 15.5f, 15.5f, 15.5f, 9f);    // 長針
            g.DrawLine(hand, 15.5f, 15.5f, 20f, 18f);     // 短針
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Show() => _icon.Visible = true;

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
