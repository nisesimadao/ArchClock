using System.Runtime.InteropServices;

namespace ArchClock;

/// <summary>
/// 壁紙レイヤーに置いた窓はデスクトップアイコンの下にいるため、そのままではクリックが届かない。
/// 低レベルマウスフックでデスクトップ上の操作だけを拾い、ウィジェットへ転送する。
///
/// 前面に別のウィンドウがあるときは何もしない。デスクトップが見えている状況でのみ働く。
/// </summary>
public sealed class DesktopInput : IDisposable
{
    public sealed record Point2(int X, int Y);

    /// <summary>ウィジェットの当たり判定を持っている側に問い合わせる。true なら我々が消費する。</summary>
    public Func<Point2, bool>? HitTest { get; set; }

    public Action<Point2>? OnDown  { get; set; }
    public Action<Point2>? OnMove  { get; set; }
    public Action<Point2>? OnUp    { get; set; }
    public Action<Point2>? OnClick { get; set; }

    /// <summary>
    /// ボタンを押していない普通の移動。デスクトップが見えているときだけ流す。
    /// フック越しではページがホバーを知りようがないので、これで補う。
    /// </summary>
    public Action<Point2?>? OnHover { get; set; }

    private IntPtr _hook = IntPtr.Zero;
    private readonly LowLevelMouseProc _proc;   // GC に回収されないよう保持する
    private bool _downInsideWidget;
    private Point2? _downAt;
    private bool _dragged;
    private int _lastHoverAt;
    private bool _hoverWasOn;

    public DesktopInput()
    {
        _proc = Callback;
    }

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, IntPtr.Zero, 0);
        if (_hook == IntPtr.Zero) Log.Write("マウスフックを設定できませんでした");
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);

        try
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var pt   = new Point2(data.pt.x, data.pt.y);
            int msg  = (int)wParam;

            switch (msg)
            {
                case WM_LBUTTONDOWN:
                    // その一点にデスクトップが見えているときだけ反応する
                    bool onDesk = PointIsOnDesktop(pt);
                    bool hit    = onDesk && HitTest?.Invoke(pt) == true;
                    if (Diagnose)
                        Log.Write($"押下 ({pt.X},{pt.Y}) 直下={ClassAt(pt)} " +
                                  $"前面={ForegroundClass()} デスクトップ={onDesk} 当たり={hit}");
                    if (hit)
                    {
                        _downInsideWidget = true;
                        _downAt   = pt;
                        _dragged  = false;
                        OnDown?.Invoke(pt);
                        return (IntPtr)1;      // アイコンの矩形選択を始めさせない
                    }
                    break;

                case WM_MOUSEMOVE:
                    if (_downInsideWidget)
                    {
                        // ここで GetAsyncKeyState を使ってはいけない。押下をフックで
                        // ブロックしている以上システムは押されたことを知らないので、
                        // 常に「離されている」と返り、最初の移動でドラッグが終わってしまう。
                        // 離した合図は WM_LBUTTONUP がこのフックへ必ず届く。

                        if (_downAt is not null &&
                            (Math.Abs(pt.X - _downAt.X) > 3 || Math.Abs(pt.Y - _downAt.Y) > 3))
                            _dragged = true;

                        OnMove?.Invoke(pt);

                        // ここで 1 を返してはいけない。移動メッセージを握りつぶすと
                        // カーソルそのものが画面上で動かなくなり、掴んで運べなくなる。
                    }
                    else
                    {
                        // ホバーの通知。動くたびに送ると重いので少し間引く。
                        int now = Environment.TickCount;
                        if (now - _lastHoverAt >= 30)
                        {
                            _lastHoverAt = now;
                            bool onDesktop = PointIsOnDesktop(pt);
                            if (onDesktop) { OnHover?.Invoke(pt); _hoverWasOn = true; }
                            else if (_hoverWasOn) { OnHover?.Invoke(null); _hoverWasOn = false; }
                        }
                    }
                    break;

                case WM_LBUTTONUP:
                    if (_downInsideWidget)
                    {
                        _downInsideWidget = false;
                        OnUp?.Invoke(pt);
                        if (!_dragged) OnClick?.Invoke(pt);
                        _downAt = null;
                        return (IntPtr)1;
                    }
                    break;
            }
        }
        catch (Exception ex) { Log.Write(ex); }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>不具合を追うときだけ true にする。押下のたびに1行残す。</summary>
    public bool Diagnose { get; set; }

    /// <summary>
    /// その座標にデスクトップが見えているか。
    ///
    /// 「前面のウィンドウがデスクトップか」で判定してはいけない。他のアプリに
    /// フォーカスがある状態でデスクトップを押した瞬間は、まだ前のアプリが前面のため
    /// すべて弾かれてしまう。カーソルの真下にあるものを直接見る。
    /// </summary>
    private static bool PointIsOnDesktop(Point2 p)
    {
        IntPtr h = WindowFromPoint(new POINT { x = p.X, y = p.Y });
        if (h == IntPtr.Zero) return true;

        // 親を辿って、デスクトップの一族かどうかを見る
        for (int i = 0; i < 8 && h != IntPtr.Zero; i++)
        {
            var cls = ClassOf(h);
            if (cls is "Progman" or "WorkerW" or "SHELLDLL_DefView" or "SysListView32")
                return true;
            h = GetAncestor(h, GA_PARENT);
        }
        return false;
    }

    private static string ClassOf(IntPtr h)
    {
        var sb = new System.Text.StringBuilder(128);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string ClassAt(Point2 p)
        => ClassOf(WindowFromPoint(new POINT { x = p.X, y = p.Y }));

    private static string ForegroundClass()
    {
        IntPtr fg = GetForegroundWindow();
        return fg == IntPtr.Zero ? "(なし)" : ClassOf(fg);
    }

    public void Dispose() => Stop();

    // ---------------- P/Invoke ----------------

    private const int WH_MOUSE_LL    = 14;
    private const int WM_MOUSEMOVE   = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP   = 0x0202;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData, flags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    private const uint GA_PARENT = 1;

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT pt);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
}
