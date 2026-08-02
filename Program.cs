using System.Runtime.InteropServices;

namespace ArchClock;

internal static class Program
{
    public const string MutexName = "ArchClock.SingleInstance";

    [STAThread]
    private static void Main(string[] args)
    {
        // 混在DPI環境で各モニターの実ピクセルを正しく扱うために、一番早い段階で宣言する
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }   // PER_MONITOR_AWARE_V2

        // 自己再起動の直後は前のプロセスがまだ落ちきっていないので、少し待って引き継ぐ
        var mutex = new Mutex(false, MutexName);
        bool owned;
        try { owned = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) return;

        // 例外で JIT デバッガのダイアログを出さない。記録して続ける。
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write(e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();

        App? app = null;
        try
        {
            app = new App();
            app.Start();
            Application.Run();
        }
        catch (Exception ex) { Log.Write(ex); }
        finally
        {
            app?.Dispose();
            try { mutex.ReleaseMutex(); } catch { }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}

/// <summary>失敗したことを黙って捨てない。%LOCALAPPDATA%\ArchClock\archclock.log に残す。</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static void Write(Exception? ex)
    {
        if (ex is not null) Write(ex.ToString());
    }

    public static void Write(string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.DataDir);
                var file = Paths.LogFile;

                // 際限なく太らせない
                try
                {
                    var fi = new FileInfo(file);
                    if (fi.Exists && fi.Length > 512 * 1024)
                        File.Move(file, file + ".old", overwrite: true);
                }
                catch { }

                File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }
}

/// <summary>デスクトップの壁紙レイヤー(アイコンの背後)を解決する。</summary>
public static class DesktopLayer
{
    public static IntPtr Progman() => Native.FindWindow("Progman", null);

    /// <summary>
    /// 壁紙レイヤーの HWND を返す。見つからなければ IntPtr.Zero。
    /// WorkerW の生え方が Windows のビルドによって二通りあるので、両方を見る。
    /// </summary>
    public static IntPtr Resolve()
    {
        IntPtr progman = Progman();
        if (progman == IntPtr.Zero) return IntPtr.Zero;

        // Progman に 0x052C を送ると壁紙用の WorkerW が生成される
        Native.SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero,
                                  Native.SMTO_NORMAL, 1000, out _);

        IntPtr parent = IntPtr.Zero;

        // (A) 旧来の構造 : SHELLDLL_DefView を持つウィンドウの「次の兄弟」が WorkerW
        Native.EnumWindows((hwnd, _) =>
        {
            if (Native.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            {
                IntPtr next = Native.FindWindowEx(IntPtr.Zero, hwnd, "WorkerW", null);
                if (next != IntPtr.Zero) parent = next;
            }
            return true;
        }, IntPtr.Zero);

        // (B) Windows 11 の新しい構造 : WorkerW が Progman の「子」として生える
        if (parent == IntPtr.Zero)
            parent = Native.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);

        // (C) どちらも無ければ Progman 直下
        return parent != IntPtr.Zero ? parent : progman;
    }
}

public static class Native
{
    public const uint SMTO_NORMAL    = 0x0000;
    public const uint SWP_NOZORDER   = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter,
                                             string? className, string? windowName);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                           int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam,
                                                   IntPtr lParam, uint fuFlags, uint uTimeout,
                                                   out IntPtr lpdwResult);

    // ---- モニターの実 DPI ----
    // Control.DeviceDpi は、壁紙レイヤーの子になった窓では親(主モニター)の値を
    // 返すことがある。モニターそのものに聞く。

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>その矩形が載っているモニターの実効 DPI。取れなければ 96。</summary>
    public static int MonitorDpi(Rectangle bounds)
    {
        try
        {
            var center = new POINT { X = bounds.X + bounds.Width / 2, Y = bounds.Y + bounds.Height / 2 };
            IntPtr mon = MonitorFromPoint(center, 2);          // MONITOR_DEFAULTTONEAREST
            if (mon == IntPtr.Zero) return 96;
            if (GetDpiForMonitor(mon, 0, out uint dx, out _) != 0) return 96;   // MDT_EFFECTIVE_DPI
            return (int)dx;
        }
        catch { return 96; }
    }
}
