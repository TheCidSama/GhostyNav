using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace GhostyNav.Services;

public sealed record TopApp(string Name, double Value);

/// <summary>
/// System readings for the Stats tab. Sampled only while that tab is visible, using the same
/// performance counters Task Manager reads (PDH) plus one native process snapshot per tick.
/// </summary>
public sealed class SystemStats : IDisposable
{
    private nint _query;
    private nint _cpuUtility, _cpuPerformance, _processes, _gpuEngine, _gpuMemory;
    private readonly double _baseMhz;
    private long _lastRx, _lastTx;
    private DateTime _lastNet;
    private readonly Dictionary<int, long> _lastCpuTime = [];
    private DateTime _lastProcSample;

    public string CpuName { get; }
    public string GpuName { get; }
    public ulong GpuMemoryTotal { get; }

    public double CpuPercent { get; private set; }
    public double CpuGhz { get; private set; }
    public int ProcessCount { get; private set; }
    public double? GpuPercent { get; private set; }
    public ulong GpuMemoryUsed { get; private set; }
    public ulong RamTotal { get; private set; }
    public ulong RamUsed { get; private set; }
    public string DiskName { get; private set; } = "C:";
    public long DiskTotal { get; private set; }
    public long DiskUsed { get; private set; }
    public double NetDownBps { get; private set; }
    public double NetUpBps { get; private set; }
    public IReadOnlyList<TopApp> TopByRam { get; private set; } = [];
    public IReadOnlyList<TopApp> TopByCpu { get; private set; } = [];

    public SystemStats()
    {
        using (var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
        {
            CpuName = CleanCpuName(cpu?.GetValue("ProcessorNameString") as string ?? "CPU");
            _baseMhz = cpu?.GetValue("~MHz") is int mhz ? mhz : 0;
        }
        (GpuName, GpuMemoryTotal) = FindGpu();

        if (PdhOpenQueryW(null, 0, out _query) == 0)
        {
            _cpuUtility = AddCounter(@"\Processor Information(_Total)\% Processor Utility");
            _cpuPerformance = AddCounter(@"\Processor Information(_Total)\% Processor Performance");
            _processes = AddCounter(@"\System\Processes");
            _gpuEngine = AddCounter(@"\GPU Engine(*)\Utilization Percentage");
            _gpuMemory = AddCounter(@"\GPU Adapter Memory(*)\Dedicated Usage");
            PdhCollectQueryData(_query); // rate counters need a first sample to diff against
        }
    }

    private nint AddCounter(string path) =>
        PdhAddEnglishCounterW(_query, path, 0, out nint counter) == 0 ? counter : 0;

    /// <param name="includeProcesses">The per-process snapshot is the costliest part; callers sample it less often.</param>
    public void Sample(bool includeProcesses)
    {
        if (_query != 0 && PdhCollectQueryData(_query) == 0)
        {
            CpuPercent = Math.Clamp(ReadDouble(_cpuUtility) ?? CpuPercent, 0, 100);
            if (ReadDouble(_cpuPerformance) is double perf && _baseMhz > 0) CpuGhz = _baseMhz * perf / 100 / 1000;
            ProcessCount = (int)(ReadDouble(_processes) ?? ProcessCount);
            GpuPercent = ReadGpuUtilization();
            GpuMemoryUsed = (ulong)ReadArray(_gpuMemory).Select(i => i.Value).DefaultIfEmpty(0).Max();
        }

        var mem = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (Native.GlobalMemoryStatusEx(ref mem))
        {
            RamTotal = mem.ullTotalPhys;
            RamUsed = mem.ullTotalPhys - mem.ullAvailPhys;
        }

        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
            DiskName = drive.Name.TrimEnd('\\');
            DiskTotal = drive.TotalSize;
            DiskUsed = drive.TotalSize - drive.TotalFreeSpace;
        }
        catch (IOException) { }

        SampleNetwork();
        if (includeProcesses) SampleProcesses();
    }

    private void SampleNetwork()
    {
        long rx = 0, tx = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            var stats = nic.GetIPStatistics();
            rx += stats.BytesReceived;
            tx += stats.BytesSent;
        }
        var now = DateTime.UtcNow;
        double seconds = (now - _lastNet).TotalSeconds;
        if (_lastNet != default && seconds > 0)
        {
            NetDownBps = Math.Max(0, (rx - _lastRx) / seconds);
            NetUpBps = Math.Max(0, (tx - _lastTx) / seconds);
        }
        (_lastRx, _lastTx, _lastNet) = (rx, tx, now);
    }

    /// <summary>Task Manager style "busiest engine of the busiest adapter".</summary>
    private double? ReadGpuUtilization()
    {
        if (_gpuEngine == 0) return null;
        var totals = new Dictionary<string, double>();
        foreach (var (name, value) in ReadArray(_gpuEngine))
        {
            // Instance: pid_1234_luid_0x..._0x..._phys_0_eng_3_engtype_3D
            int luid = name.IndexOf("luid_", StringComparison.Ordinal);
            int engType = name.IndexOf("engtype_", StringComparison.Ordinal);
            int phys = name.IndexOf("_phys", StringComparison.Ordinal);
            if (luid < 0 || engType < 0 || phys < luid) continue;
            string key = name[luid..phys] + name[engType..];
            totals[key] = totals.GetValueOrDefault(key) + value;
        }
        return totals.Count == 0 ? 0 : Math.Clamp(totals.Values.Max(), 0, 100);
    }

    // ---------- Processes ----------

    private unsafe void SampleProcesses()
    {
        if (IntPtr.Size != 8) return; // offsets below are for 64-bit SYSTEM_PROCESS_INFORMATION

        int size = 512 * 1024;
        nint buffer = 0;
        try
        {
            while (true)
            {
                buffer = Marshal.AllocHGlobal(size);
                int status = Native.NtQuerySystemInformation(5 /* SystemProcessInformation */, buffer, size, out int needed);
                if (status == 0) break;
                Marshal.FreeHGlobal(buffer);
                buffer = 0;
                if (status != unchecked((int)0xC0000004)) return; // anything but "buffer too small"
                size = Math.Max(needed, size) + 64 * 1024;
            }

            var now = DateTime.UtcNow;
            double elapsedTicks = (now - _lastProcSample).Ticks * (double)Environment.ProcessorCount;
            bool haveDelta = _lastProcSample != default && elapsedTicks > 0;

            var ram = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var cpu = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var seen = new Dictionary<int, long>();

            byte* p = (byte*)buffer;
            while (true)
            {
                int pid = (int)*(long*)(p + 80);
                ushort nameBytes = *(ushort*)(p + 56);
                char* namePtr = *(char**)(p + 64);
                if (pid != 0 && namePtr != null && nameBytes > 0)
                {
                    string name = new string(namePtr, 0, nameBytes / 2);
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                    long privateWs = *(long*)(p + 8);
                    long cpuTime = *(long*)(p + 40) + *(long*)(p + 48);
                    ram[name] = ram.GetValueOrDefault(name) + privateWs;
                    seen[pid] = cpuTime;
                    if (haveDelta && _lastCpuTime.TryGetValue(pid, out long before))
                        cpu[name] = cpu.GetValueOrDefault(name) + Math.Max(0, cpuTime - before) / elapsedTicks * 100;
                }
                int next = *(int*)p;
                if (next == 0) break;
                p += next;
            }

            _lastCpuTime.Clear();
            foreach (var (pid, t) in seen) _lastCpuTime[pid] = t;
            _lastProcSample = now;

            string[] skip = ["System", "Idle", "Registry", "Memory Compression", "Secure System", "GhostyNav"];
            TopByRam = Top(ram, skip);
            if (haveDelta) TopByCpu = Top(cpu, skip);
        }
        finally
        {
            if (buffer != 0) Marshal.FreeHGlobal(buffer);
        }
    }

    private static List<TopApp> Top(Dictionary<string, double> values, string[] skip) =>
        values.Where(kv => !skip.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
              .OrderByDescending(kv => kv.Value).Take(4)
              .Select(kv => new TopApp(FriendlyName(kv.Key), kv.Value)).ToList();

    private static string FriendlyName(string exe) => exe.ToLowerInvariant() switch
    {
        "chrome" => "Google Chrome",
        "msedge" => "Microsoft Edge",
        "firefox" => "Firefox",
        "brave" => "Brave",
        "code" => "Visual Studio Code",
        "devenv" => "Visual Studio",
        "explorer" => "File Explorer",
        "msedgewebview2" => "Edge WebView",
        "discord" => "Discord",
        "spotify" => "Spotify",
        "steam" or "steamwebhelper" => "Steam",
        "windowsterminal" => "Terminal",
        "dwm" => "Desktop Window Manager",
        "svchost" => "Service Host",
        "memcompression" => "Memory Compression",
        _ => char.ToUpperInvariant(exe[0]) + exe[1..]
    };

    // ---------- Hardware names ----------

    private static string CleanCpuName(string raw)
    {
        string s = raw.Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "");
        foreach (string cut in new[] { " w/ ", " with ", " @ " })
        {
            int i = s.IndexOf(cut, StringComparison.OrdinalIgnoreCase);
            if (i > 0) s = s[..i];
        }
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\b\d+-Core Processor\b|\bProcessor\b|\bCPU\b", "");
        return System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
    }

    /// <summary>The display adapter with the most dedicated memory (the discrete GPU on hybrid laptops).</summary>
    private static (string Name, ulong Memory) FindGpu()
    {
        string best = "GPU";
        ulong bestMemory = 0;
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (string sub in cls?.GetSubKeyNames() ?? [])
            {
                if (!sub.All(char.IsDigit)) continue;
                using var key = cls!.OpenSubKey(sub);
                if (key?.GetValue("DriverDesc") is not string desc) continue;
                ulong memory = key.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long q => (ulong)q,
                    byte[] b when b.Length >= 8 => BitConverter.ToUInt64(b),
                    _ => key.GetValue("HardwareInformation.MemorySize") switch
                    {
                        int d => (uint)d,
                        byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b),
                        _ => 0
                    }
                };
                if (memory > bestMemory || best == "GPU")
                {
                    (best, bestMemory) = (desc, memory);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GPU lookup failed: {ex.Message}");
        }
        return (best, bestMemory);
    }

    // ---------- Speed test ----------

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Downloads then uploads test data against Cloudflare's public speed endpoints.</summary>
    public static async Task<SpeedResult> RunSpeedTestAsync(CancellationToken ct = default)
    {
        var result = new SpeedResult { When = DateTime.Now };

        var sw = Stopwatch.StartNew();
        long received = 0;
        using (var response = await Http.GetAsync("https://speed.cloudflare.com/__down?bytes=25000000",
                   HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            if (response.Headers.TryGetValues("cf-meta-city", out var city)) result.City = city.FirstOrDefault();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[64 * 1024];
            int n;
            // Stop after ~8 s on slow links; the rate so far is good enough.
            while ((n = await stream.ReadAsync(buffer, ct)) > 0 && sw.Elapsed.TotalSeconds < 8)
                received += n;
        }
        result.DownMbps = received * 8 / sw.Elapsed.TotalSeconds / 1_000_000;

        var payload = new byte[8_000_000];
        sw.Restart();
        using (var content = new ByteArrayContent(payload))
        using (var response = await Http.PostAsync("https://speed.cloudflare.com/__up", content, ct))
            response.EnsureSuccessStatusCode();
        result.UpMbps = payload.Length * 8 / sw.Elapsed.TotalSeconds / 1_000_000;
        return result;
    }

    public static string FormatBytes(double bytes, int decimals = 1)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int u = 0;
        while (bytes >= 1024 && u < units.Length - 1) { bytes /= 1024; u++; }
        return u == 0 ? $"{bytes:0} B" : bytes.ToString("0." + new string('#', decimals)) + " " + units[u];
    }

    // ---------- PDH ----------

    private double? ReadDouble(nint counter)
    {
        if (counter == 0) return null;
        return PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, out _, out var value) == 0
               && value.CStatus is 0 or 1 ? value.doubleValue : null;
    }

    private static List<(string Name, double Value)> ReadArray(nint counter)
    {
        var list = new List<(string, double)>();
        if (counter == 0) return list;
        uint size = 0;
        int status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out _, 0);
        if (status != PDH_MORE_DATA || size == 0) return list;

        nint buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out uint count, buffer) != 0)
                return list;
            int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buffer + i * itemSize);
                if (item.Value.CStatus is 0 or 1)
                    list.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Value.doubleValue));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return list;
    }

    public void Dispose()
    {
        if (_query != 0) PdhCloseQuery(_query);
        _query = 0;
    }

    private const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000;
    private const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE
    {
        public uint CStatus;
        public double doubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public nint Name;
        public PDH_FMT_COUNTERVALUE Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQueryW(string? dataSource, nint userData, out nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounterW(nint query, string path, nint userData, out nint counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(nint query);

    [DllImport("pdh.dll")]
    private static extern int PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bufferSize, out uint itemCount, nint buffer);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(nint query);
}
