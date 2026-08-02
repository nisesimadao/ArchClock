using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace ArchClock;

/// <summary>
/// CPU・メモリ・プロセス・ディスク・ネットワークを一定間隔で採取する。
/// PerformanceCounter は初期化が重く値も遅れるので、カーネルの生の時間から自前で計算する。
/// </summary>
public sealed class Metrics : IDisposable
{
    public sealed record Snapshot(
        double CpuPercent,
        MemInfo Memory,
        IReadOnlyList<ProcInfo> TopByCpu,
        IReadOnlyList<ProcInfo> TopByMemory,
        IReadOnlyList<DiskInfo> Disks,
        NetInfo Network,
        int ProcessCount,
        TimeSpan Uptime);

    public sealed record MemInfo(double UsedGb, double TotalGb, double Percent);
    public sealed record ProcInfo(int Pid, string Name, double Cpu, double MemMb);
    public sealed record DiskInfo(string Name, double UsedGb, double TotalGb, double Percent);
    public sealed record NetInfo(double DownKbps, double UpKbps);

    private ulong _prevIdle, _prevKernel, _prevUser;
    private readonly Dictionary<int, (TimeSpan cpu, DateTime at)> _procCpu = new();
    private long _prevRx, _prevTx;
    private DateTime _prevNetAt = DateTime.MinValue;
    private readonly int _cores = Environment.ProcessorCount;

    /// <summary>1回分を採取する。前回との差分を使うので、2回目以降が正しい値になる。</summary>
    public Snapshot Sample(int topCount = 15)
    {
        // プロセスの採取は1回だけ。2回呼ぶと2回目の CPU 差分が必ず 0 になる。
        var procs = SampleProcesses();

        return new Snapshot(
            SampleCpu(),
            SampleMemory(),
            procs.OrderByDescending(x => x.Cpu).ThenByDescending(x => x.MemMb).Take(topCount).ToList(),
            procs.OrderByDescending(x => x.MemMb).Take(topCount).ToList(),
            SampleDisks(),
            SampleNetwork(),
            _lastProcessCount,
            TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    // ---------------- CPU ----------------

    private double SampleCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;

        ulong i = ToU(idle), k = ToU(kernel), u = ToU(user);
        double result = 0;

        if (_prevKernel != 0)
        {
            ulong dIdle   = i - _prevIdle;
            ulong dKernel = k - _prevKernel;
            ulong dUser   = u - _prevUser;
            ulong total   = dKernel + dUser;              // kernel には idle が含まれる
            if (total > 0) result = (total - dIdle) * 100.0 / total;
        }

        _prevIdle = i; _prevKernel = k; _prevUser = u;
        return Math.Clamp(result, 0, 100);
    }

    private static ulong ToU(FILETIME f) => ((ulong)(uint)f.dwHighDateTime << 32) | (uint)f.dwLowDateTime;

    // ---------------- メモリ ----------------

    private static MemInfo SampleMemory()
    {
        var s = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref s)) return new MemInfo(0, 0, 0);

        double total = s.ullTotalPhys / 1073741824.0;
        double used  = (s.ullTotalPhys - s.ullAvailPhys) / 1073741824.0;
        return new MemInfo(Math.Round(used, 1), Math.Round(total, 1), (double)s.dwMemoryLoad);
    }

    // ---------------- プロセス ----------------

    private int _lastProcessCount;

    private List<ProcInfo> SampleProcesses()
    {
        var now  = DateTime.UtcNow;
        var list = new List<ProcInfo>();
        var seen = new HashSet<int>();

        Process[] procs;
        try { procs = Process.GetProcesses(); } catch { return list; }
        _lastProcessCount = procs.Length;

        foreach (var p in procs)
        {
            try
            {
                seen.Add(p.Id);
                double cpu = 0;
                var cur = p.TotalProcessorTime;

                if (_procCpu.TryGetValue(p.Id, out var prev))
                {
                    double elapsedMs = (now - prev.at).TotalMilliseconds;
                    if (elapsedMs > 0)
                        cpu = (cur - prev.cpu).TotalMilliseconds / (elapsedMs * _cores) * 100.0;
                }
                _procCpu[p.Id] = (cur, now);

                list.Add(new ProcInfo(p.Id, p.ProcessName,
                                      Math.Round(Math.Clamp(cpu, 0, 100), 1),
                                      Math.Round(p.WorkingSet64 / 1048576.0, 0)));
            }
            catch { /* 権限のないプロセスや、採取中に終了したものは飛ばす */ }
            finally { p.Dispose(); }
        }

        // 終了したプロセスの記録を捨てる
        foreach (var id in _procCpu.Keys.Where(k => !seen.Contains(k)).ToList())
            _procCpu.Remove(id);

        // 同名プロセス(chrome など)はまとめた方が読みやすい
        return list
            .GroupBy(x => x.Name)
            .Select(g => new ProcInfo(
                g.OrderByDescending(x => x.MemMb).First().Pid,
                g.Key,
                Math.Round(Math.Min(100, g.Sum(x => x.Cpu)), 1),
                Math.Round(g.Sum(x => x.MemMb), 0)))
            .ToList();
    }

    // ---------------- ディスク ----------------

    private static List<DiskInfo> SampleDisks()
    {
        var list = new List<DiskInfo>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!d.IsReady || d.DriveType != DriveType.Fixed) continue;
                double total = d.TotalSize / 1073741824.0;
                if (total <= 0) continue;
                double used = (d.TotalSize - d.TotalFreeSpace) / 1073741824.0;
                list.Add(new DiskInfo(d.Name.TrimEnd('\\'), Math.Round(used, 1),
                                      Math.Round(total, 1), Math.Round(used / total * 100, 0)));
            }
        }
        catch (Exception ex) { Log.Write(ex); }
        return list;
    }

    // ---------------- ネットワーク ----------------

    private NetInfo SampleNetwork()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var s = ni.GetIPStatistics();
                rx += s.BytesReceived;
                tx += s.BytesSent;
            }
        }
        catch { return new NetInfo(0, 0); }

        var now = DateTime.UtcNow;
        double down = 0, up = 0;
        if (_prevNetAt != DateTime.MinValue)
        {
            double sec = (now - _prevNetAt).TotalSeconds;
            if (sec > 0)
            {
                down = Math.Max(0, (rx - _prevRx)) / sec / 1024.0;
                up   = Math.Max(0, (tx - _prevTx)) / sec / 1024.0;
            }
        }
        _prevRx = rx; _prevTx = tx; _prevNetAt = now;
        return new NetInfo(Math.Round(down, 1), Math.Round(up, 1));
    }

    public void Dispose() => _procCpu.Clear();

    // ---------------- P/Invoke ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public int dwLowDateTime; public int dwHighDateTime; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys;
        public ulong ullTotalPageFile, ullAvailPageFile;
        public ulong ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
