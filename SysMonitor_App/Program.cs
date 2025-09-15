using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using LibreHardwareMonitor.Hardware;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;

// Minimal API host
var builder = WebApplication.CreateBuilder(args);
// Allow Seelen WebView (custom scheme/file origin) to fetch metrics
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowSeelen", policy =>
        policy
            .AllowAnyOrigin()
            .WithMethods("GET")
            .AllowAnyHeader());
});
builder.WebHost.UseUrls("http://127.0.0.1:58090");
var app = builder.Build();

// Initialize the shared sensor computer once
SensorsHost.Initialize();

// Metrics endpoint
app.MapGet("/metrics", (HttpRequest request) =>
{
    SensorsHost.Refresh();

    var cpu = SensorsHost.ReadCpuLoad();
    float? gpu;

    var useGpuZ = false;
    if (request.Query.TryGetValue("gpuz", out var gpuzVal))
    {
        var raw = gpuzVal.ToString();
        useGpuZ = string.Equals(raw, "1") || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    // Read from both sources and combine to avoid zeros when one source fails
    float? gpuFromGpuZ = useGpuZ ? SensorsHost.ReadGpuLoadGpuZ() : null;
    float? gpuFromStd = SensorsHost.ReadGpuLoad();
    float? combined = null;
    if (gpuFromGpuZ.HasValue && gpuFromStd.HasValue)
        combined = Math.Max(gpuFromGpuZ.Value, gpuFromStd.Value);
    else
        combined = gpuFromGpuZ ?? gpuFromStd;

    // Clamp and smooth; hold last good value for brief gaps
    if (combined.HasValue)
    {
        var clamped = SensorsHost.Clamp01(combined.Value);
        SensorsHost.UpdateGpuCombinedEma(clamped);
        SensorsHost.RememberLastGpu(SensorsHost.GpuCombinedEma!.Value);
        gpu = SensorsHost.GpuCombinedEma;
    }
    else
    {
        gpu = SensorsHost.GetLastGpuWithin(TimeSpan.FromSeconds(10));
    }

    // Fallback: Windows "GPU Engine" counters if LHM not available or returns 0
    if (!(gpu.HasValue && gpu.Value > 0f))
    {
        var gpuPdh = SensorsHost.ReadGpuLoadPdh();
        if (gpuPdh.HasValue) gpu = gpuPdh.Value;
    }

    var payload = new
    {
        cpuLoad = cpu.GetValueOrDefault(0f),
        gpuLoad = gpu.GetValueOrDefault(0f),
        timestamp = DateTimeOffset.UtcNow
    };

    return Results.Json(payload, new JsonSerializerOptions { WriteIndented = false });
});

// Debug endpoint (sanitized, never throws on NaN/Infinity)
app.MapGet("/debug", () =>
{
    SensorsHost.Refresh();

    var list = new List<object>();
    foreach (var hw in SensorsHost.Computer.Hardware)
    {
        hw.Update();
        var sensors = new List<object>();
        foreach (var s in hw.Sensors)
        {
            sensors.Add(new
            {
                s.Name,
                s.SensorType,
                Value = SensorsHost.CleanFloat(s.Value),
                Min = SensorsHost.CleanFloat(s.Min),
                Max = SensorsHost.CleanFloat(s.Max)
            });
        }
        list.Add(new
        {
            HardwareName = hw.Name,
            HardwareType = hw.HardwareType.ToString(),
            Sensors = sensors
        });
    }

    return Results.Json(list, new JsonSerializerOptions
    {
        WriteIndented = true
        // For debugging only, you could also allow named non-finite numbers:
        // NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    });
});

// Convenience redirect
app.MapGet("/", () => Results.Redirect("/metrics"));

app.Lifetime.ApplicationStopping.Register(SensorsHost.Dispose);
app.UseCors("AllowSeelen");
app.Run();

static class SensorsHost
{
    public static Computer Computer { get; private set; } = null!;

    // Windows PDH "GPU Engine" counters (Utilization Percentage)
    private static List<PerformanceCounter>? s_gpuCounters;
    private static readonly object s_gpuLock = new();
    private static List<PerformanceCounter>? s_gpuTotalCounters;
    private static PerformanceCounter? s_gpu3DCounter;
    private static Dictionary<string, List<PerformanceCounter>>? s_gpuByType;
    private static float? s_gpuEma;
    private static float? s_gpuCombinedEma;
    private static float? s_lastGpu;
    private static DateTimeOffset? s_lastGpuAt;
    private static float? s_gpuzLastValue;
    private static uint s_gpuzLastUpdateTick;
    private static DateTimeOffset? s_gpuzLastValueAt;

    public static void Initialize()
    {
        Computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = false,
            IsMotherboardEnabled = false,
            IsControllerEnabled = false,
            IsNetworkEnabled = false,
            IsStorageEnabled = false
        };
        Computer.Open();
    }

    public static void Dispose()
    {
        try { Computer.Close(); } catch { /* ignore */ }
    }

    public static void Refresh()
    {
        foreach (var hw in Computer.Hardware)
        {
            hw.Update();
            foreach (var sub in hw.SubHardware) sub.Update();
        }
    }

    public static float? CleanFloat(float? v)
    {
        if (!v.HasValue) return null;
        var x = v.Value;
        if (float.IsNaN(x) || float.IsInfinity(x)) return null;
        return x;
    }

    public static float? ReadCpuLoad()
    {
        foreach (var hw in Computer.Hardware)
        {
            if (hw.HardwareType == HardwareType.Cpu)
            {
                hw.Update();

                // Prefer "CPU Total"
                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType == SensorType.Load &&
                        s.Name.Contains("CPU Total", StringComparison.OrdinalIgnoreCase))
                    {
                        return s.Value ?? 0f;
                    }
                }

                // Fallback: average of CPU Core loads
                var coreLoads = hw.Sensors
                    .Where(s => s.SensorType == SensorType.Load &&
                                s.Name.Contains("CPU Core", StringComparison.OrdinalIgnoreCase))
                    .Select(s => s.Value)
                    .Where(v => v.HasValue)
                    .Select(v => v!.Value)
                    .ToList();

                if (coreLoads.Count > 0)
                    return coreLoads.Average();
            }
        }
        return null;
    }

    public static float? ReadGpuLoad()
    {
        // Common labels across vendors/drivers
        string[] preferredNames =
        {
            "GPU Core",
            "GPU Graphics",
            "GPU Total",
            "GPU 3D",
            "GPU Utilization",
            "GPU Render"
        };

        foreach (var hw in Computer.Hardware)
        {
            if (hw.HardwareType == HardwareType.GpuAmd ||
                hw.HardwareType == HardwareType.GpuNvidia ||
                hw.HardwareType == HardwareType.GpuIntel)
            {
                hw.Update();

                // Try preferred names first
                foreach (var name in preferredNames)
                {
                    var match = hw.Sensors
                        .Where(s => s.SensorType == SensorType.Load &&
                                    s.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                        .Select(s => s.Value)
                        .FirstOrDefault(v => v.HasValue);

                    if (match.HasValue)
                        return match.Value;
                }

                // Fallback: maximum of any Load sensor
                var loads = hw.Sensors
                    .Where(s => s.SensorType == SensorType.Load && s.Value.HasValue)
                    .Select(s => s.Value!.Value)
                    .ToList();

                if (loads.Count > 0)
                    return loads.Max();
            }
        }
        return null;
    }

    // GPU-Z shared memory consumer based on documented layout. Returns percentage [0..100] or null
    public static float? ReadGpuLoadGpuZ()
    {
        try
        {
            // GPU-Z publishes a named shared memory mapping. Names vary by version; try common ones.
            string[] mapNames =
            {
                "GPUZShMem",
                "GPUZSHMEM",
                "GPU-Z Shared Memory"
            };

            foreach (var name in mapNames)
            {
                try
                {
                    using var mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
                    using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

                    // Try a few times if GPU-Z is writing (busy flag)
                    for (int attempt = 0; attempt < 10; attempt++)
                    {
                        uint version = accessor.ReadUInt32(0);
                        int busy = accessor.ReadInt32(4);
                        uint lastUpdate = accessor.ReadUInt32(8);
                        if (busy != 0) { Thread.Sleep(5); continue; }

                        // Offsets based on struct with #pragma pack(push,1)
                        const int MAX_RECORDS = 128;
                        long offset = 12; // version(4) + busy(4) + lastUpdate(4)
                        long recordSize = 256 * 2 + 256 * 2; // key + value (UTF-16), in bytes
                        long recordsTotal = recordSize * MAX_RECORDS;
                        long sensorsOffset = offset + recordsTotal;

                        // Sensor record: name[256]*2 + unit[8]*2 + uint32 + double
                        const int SENSOR_NAME_CHARS = 256;
                        const int SENSOR_UNIT_CHARS = 8;
                        long sensorStride = SENSOR_NAME_CHARS * 2 + SENSOR_UNIT_CHARS * 2 + 4 + 8; // 540

                        double best = double.NaN;
                        for (int i = 0; i < MAX_RECORDS; i++)
                        {
                            long sBase = sensorsOffset + i * sensorStride;
                            // read name
                            int nameBytes = SENSOR_NAME_CHARS * 2;
                            byte[] nameBuf = new byte[nameBytes];
                            accessor.ReadArray(sBase, nameBuf, 0, nameBuf.Length);
                            string nameStr = Encoding.Unicode.GetString(nameBuf);
                            int z = nameStr.IndexOf('\0');
                            if (z >= 0) nameStr = nameStr.Substring(0, z);
                            if (string.IsNullOrWhiteSpace(nameStr)) continue;

                            // read unit
                            long unitPos = sBase + SENSOR_NAME_CHARS * 2;
                            byte[] unitBuf = new byte[SENSOR_UNIT_CHARS * 2];
                            accessor.ReadArray(unitPos, unitBuf, 0, unitBuf.Length);
                            string unitStr = Encoding.Unicode.GetString(unitBuf);
                            z = unitStr.IndexOf('\0');
                            if (z >= 0) unitStr = unitStr.Substring(0, z);

                            uint digits = accessor.ReadUInt32(unitPos + SENSOR_UNIT_CHARS * 2);
                            double value = accessor.ReadDouble(unitPos + SENSOR_UNIT_CHARS * 2 + 4);

                            string ln = nameStr.ToLowerInvariant();
                            string u = unitStr.Trim();

                            // Prefer clear GPU load signals with % unit
                            bool isCandidate = u == "%" && (ln.Contains("gpu load") || ln.Contains("gpu core load") || ln.Contains("gpu utilization") || ln.Contains("gpu usage") || ln.Contains("render") || ln.Contains("graphics"));
                            if (!isCandidate) continue;

                            if (!double.IsNaN(value) && !double.IsInfinity(value))
                            {
                                if (double.IsNaN(best) || value > best) best = value;
                            }
                        }

                        if (!double.IsNaN(best))
                        {
                            float ret = (float)best;
                            if (ret < 0) ret = 0; if (ret > 100) ret = 100;
                            // If GPU-Z isn't updating (lastUpdate unchanged), treat as stale
                            if (lastUpdate == s_gpuzLastUpdateTick && s_gpuzLastValue.HasValue)
                            {
                                // keep last for 15s
                                if (s_gpuzLastValueAt.HasValue && DateTimeOffset.UtcNow - s_gpuzLastValueAt.Value <= TimeSpan.FromSeconds(15))
                                    return s_gpuzLastValue.Value;
                            }
                            // Guard against brief zero blips during sensor table reconfiguration
                            if (ret <= 0.1f && s_gpuzLastValue.HasValue && s_gpuzLastValue.Value > 0.1f &&
                                s_gpuzLastValueAt.HasValue && DateTimeOffset.UtcNow - s_gpuzLastValueAt.Value <= TimeSpan.FromSeconds(3))
                            {
                                return s_gpuzLastValue.Value;
                            }
                            s_gpuzLastUpdateTick = lastUpdate;
                            s_gpuzLastValue = ret;
                            s_gpuzLastValueAt = DateTimeOffset.UtcNow;
                            return ret;
                        }
                    }
                }
                catch
                {
                    // ignore and try next mapping name
                }
            }
        }
        catch { }

        // If we couldn't read now, return last GPU-Z value within 15s window
        if (s_gpuzLastValue.HasValue && s_gpuzLastValueAt.HasValue &&
            DateTimeOffset.UtcNow - s_gpuzLastValueAt.Value <= TimeSpan.FromSeconds(15))
        {
            return s_gpuzLastValue.Value;
        }
        return null;
    }

    public static float? ReadGpuLoadPdh()
    {
        try
        {
            InitGpuEngineCounters();

            float? sample = null;

            // Compute sum per engine type across all instances, then take max across types (matches Task Manager behavior)
            if (s_gpuByType != null && s_gpuByType.Count > 0)
            {
                float maxType = 0f;
                foreach (var kv in s_gpuByType)
                {
                    float typeSum = 0f;
                    foreach (var c in kv.Value)
                    {
                        typeSum += c.NextValue();
                    }
                    if (typeSum > maxType) maxType = typeSum;
                }
                if (maxType > 0.1f) sample = maxType;
            }

            // Fallbacks
            if (!sample.HasValue && s_gpuTotalCounters != null && s_gpuTotalCounters.Count > 0)
            {
                float maxVal = 0f;
                foreach (var c in s_gpuTotalCounters)
                {
                    var v = c.NextValue();
                    if (v > maxVal) maxVal = v;
                }
                if (maxVal > 0.1f) sample = maxVal;
            }

            if (!sample.HasValue && s_gpu3DCounter != null)
            {
                var v = s_gpu3DCounter.NextValue();
                if (v > 0.1f) sample = v;
            }

            if (!sample.HasValue) return null;

            var clamped = Clamp01(sample.Value);
            // Smooth a bit to reduce jitter
            s_gpuEma = s_gpuEma.HasValue ? (0.5f * clamped + 0.5f * s_gpuEma.Value) : clamped;
            return s_gpuEma;
        }
        catch
        {
            return null;
        }
    }

    private static void InitGpuEngineCounters()
    {
        if (s_gpuTotalCounters != null || s_gpu3DCounter != null) return;
        lock (s_gpuLock)
        {
            if (s_gpuTotalCounters != null || s_gpu3DCounter != null) return;

            var totals = new List<PerformanceCounter>();
            var byType = new Dictionary<string, List<PerformanceCounter>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var category = new PerformanceCounterCategory("GPU Engine");
                var instances = category.GetInstanceNames();

                foreach (var inst in instances)
                {
                    // Build map of engine types and capture totals
                    var isTotal = inst.IndexOf("_Total", StringComparison.OrdinalIgnoreCase) >= 0;
                    var typeKey = "other";
                    if (inst.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) >= 0) typeKey = "3D";
                    else if (inst.IndexOf("engtype_VideoDecode", StringComparison.OrdinalIgnoreCase) >= 0) typeKey = "VideoDecode";
                    else if (inst.IndexOf("engtype_VideoEncode", StringComparison.OrdinalIgnoreCase) >= 0) typeKey = "VideoEncode";
                    else if (inst.IndexOf("engtype_Compute", StringComparison.OrdinalIgnoreCase) >= 0) typeKey = "Compute";
                    else if (inst.IndexOf("engtype_Copy", StringComparison.OrdinalIgnoreCase) >= 0) typeKey = "Copy";

                    foreach (var c in category.GetCounters(inst))
                    {
                        if (!string.Equals(c.CounterName, "Utilization Percentage", StringComparison.Ordinal)) continue;
                        _ = c.NextValue();

                        if (!byType.TryGetValue(typeKey, out var list))
                        {
                            list = new List<PerformanceCounter>();
                            byType[typeKey] = list;
                        }
                        list.Add(c);

                        if (isTotal) totals.Add(c);
                        if (typeKey == "3D" && isTotal) s_gpu3DCounter = c;
                        break;
                    }
                }
            }
            catch
            {
                // Category may not exist on some systems
            }

            s_gpuTotalCounters = totals;
            s_gpuByType = byType;
        }
    }

    public static float Clamp01(float v)
    {
        if (v < 0) return 0;
        if (v > 100) return 100;
        return v;
    }

    public static float? GpuCombinedEma => s_gpuCombinedEma;

    public static void UpdateGpuCombinedEma(float sample)
    {
        s_gpuCombinedEma = s_gpuCombinedEma.HasValue ? (0.3f * sample + 0.7f * s_gpuCombinedEma.Value) : sample;
    }

    public static void RememberLastGpu(float value)
    {
        s_lastGpu = value;
        s_lastGpuAt = DateTimeOffset.UtcNow;
    }

    public static float? GetLastGpuWithin(TimeSpan ttl)
    {
        if (s_lastGpu.HasValue && s_lastGpuAt.HasValue)
        {
            if (DateTimeOffset.UtcNow - s_lastGpuAt.Value <= ttl) return s_lastGpu.Value;
        }
        return null;
    }
}
