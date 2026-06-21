using System.Runtime.InteropServices;

namespace KeithVision.Services;

/// <summary>
/// Samples system-wide CPU utilization once a second using the Win32
/// <c>GetSystemTimes</c> API (dependency-free). CPU % needs a delta between two
/// snapshots, so a background timer keeps a current value the footer can read
/// cheaply regardless of how many pages poll.
/// </summary>
public sealed class SystemStatsService : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low; public uint High; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    private static ulong ToUInt64(FileTime f) => ((ulong)f.High << 32) | f.Low;

    private readonly object _lock = new();
    private readonly Timer _timer;
    private ulong _prevIdle, _prevTotal;
    private double _cpuPercent;

    public SystemStatsService()
    {
        Sample(); // prime the baseline snapshot
        _timer = new Timer(_ => Sample(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>Latest CPU snapshot (utilization % + logical core count).</summary>
    public object Cpu
    {
        get
        {
            lock (_lock)
                return new { utilizationPct = (int)Math.Round(_cpuPercent), cores = Environment.ProcessorCount };
        }
    }

    /// <summary>Current physical-memory usage (instantaneous; no sampling needed).</summary>
    public object Memory
    {
        get
        {
            var m = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref m)) return new { available = false };
            const double MB = 1024d * 1024;
            return new
            {
                available = true,
                usedMb = (long)((m.ullTotalPhys - m.ullAvailPhys) / MB),
                totalMb = (long)(m.ullTotalPhys / MB),
                loadPct = (int)m.dwMemoryLoad,
            };
        }
    }

    private void Sample()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return;

        var idleT = ToUInt64(idle);
        var total = ToUInt64(kernel) + ToUInt64(user); // kernel time includes idle

        lock (_lock)
        {
            var idleDelta = idleT - _prevIdle;
            var totalDelta = total - _prevTotal;
            if (_prevTotal != 0 && totalDelta > 0)
            {
                var busy = totalDelta - idleDelta;
                _cpuPercent = Math.Clamp(100.0 * busy / totalDelta, 0, 100);
            }
            _prevIdle = idleT;
            _prevTotal = total;
        }
    }

    public void Dispose() => _timer.Dispose();
}
