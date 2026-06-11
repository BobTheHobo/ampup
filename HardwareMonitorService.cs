using LibreHardwareMonitor.Hardware;
using System.IO;
using HwinfoReader = Hwinfo.SharedMemory.SharedMemoryReader;
using HwinfoSensorReading = Hwinfo.SharedMemory.SensorReading;
using HwinfoSensorType = Hwinfo.SharedMemory.SensorType;
using LhmSensorType = LibreHardwareMonitor.Hardware.SensorType;

namespace AmpUp;

internal sealed record HardwareMetricReading(
    string Source,
    string Label,
    string ValueText,
    bool IsAvailable,
    string Provider = "",
    string Detail = "",
    float GaugeFraction = -1f);

internal sealed class HardwareMonitorService : IDisposable
{
    private const int RefreshMs = 1000;
    private const string ProviderLhm = "LibreHardwareMonitor";
    private const string ProviderHwinfo = "HWiNFO";

    private readonly object _lock = new();
    private Computer? _computer;
    private HwinfoReader? _hwinfoReader;
    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private bool _opened;
    private bool _failed;
    private bool _hwinfoUnavailableLogged;
    private readonly HashSet<string> _loggedUnavailableSources = new();
    private HardwareMetricReading _lastFallback = new("unavailable", "Hardware", "--", false);

    public static readonly (string Source, string Label)[] Sources =
    {
        ("cpu_temp", "CPU Temp"),
        ("gpu_temp", "GPU Temp"),
        ("cpu_load", "CPU Load"),
        ("gpu_load", "GPU Load"),
        ("cpu_clock", "CPU Clock"),
        ("gpu_clock", "GPU Clock"),
        ("cpu_power", "CPU Power"),
        ("gpu_power", "GPU Power"),
        ("memory_used", "Memory Used"),
        ("memory_load", "Memory Load"),
        ("vram_used", "VRAM Used"),
        ("vram_load", "VRAM Load"),
        ("fan_speed", "Fan Speed"),
    };

    public HardwareMetricReading GetReading(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            source = "cpu_temp";

        lock (_lock)
        {
            try
            {
                EnsureOpen();
                RefreshIfDue();

                if (source.StartsWith("fan:", StringComparison.Ordinal))
                    return ReadSpecificFan(source);

                return source switch
                {
                    "cpu_temp" => ReadCpuMetric(source, "CPU Temp", LhmSensorType.Temperature, PreferCpuPackage, "°"),
                    "gpu_temp" => ReadSensor(source, "GPU Temp", IsGpu, LhmSensorType.Temperature, PreferGpuCore),
                    "cpu_load" => ReadCpuMetric(source, "CPU Load", LhmSensorType.Load, PreferTotalOrCore, "%"),
                    "gpu_load" => ReadSensor(source, "GPU Load", IsGpu, LhmSensorType.Load, PreferTotalOrCore, "%"),
                    "cpu_clock" => ReadCpuMetric(source, "CPU Clock", LhmSensorType.Clock, PreferTotalOrCore, "MHz"),
                    "gpu_clock" => ReadSensor(source, "GPU Clock", IsGpu, LhmSensorType.Clock, PreferGpuCore, "MHz"),
                    "cpu_power" => ReadCpuMetric(source, "CPU Power", LhmSensorType.Power, PreferPackageOrTotal, "W"),
                    "gpu_power" => ReadSensor(source, "GPU Power", IsGpu, LhmSensorType.Power, PreferPackageOrTotal, "W"),
                    "memory_used" => ReadMemoryUsed(),
                    "memory_load" => ReadSensor(source, "Memory", h => h.HardwareType == HardwareType.Memory, LhmSensorType.Load, PreferUsedMemory, "%"),
                    "vram_used" => ReadVramUsed(),
                    "vram_load" => ReadVramLoad(),
                    "fan_speed" => ReadFanSpeed(),
                    _ => new HardwareMetricReading(source, GetSourceLabel(source), "--", false),
                };
            }
            catch (Exception ex)
            {
                if (!_failed)
                {
                    _failed = true;
                    Logger.Log($"Hardware monitor unavailable: {ex.Message}");
                }
                return _lastFallback with { Source = source, Label = GetSourceLabel(source) };
            }
        }
    }

    public static string GetSourceLabel(string source)
    {
        if (source.StartsWith("fan:", StringComparison.Ordinal)) return "Fan";
        foreach (var (s, label) in Sources)
            if (s == source) return label;
        return "Hardware";
    }

    /// <summary>
    /// Enumerates every individual fan sensor currently visible (LHM + HWiNFO) so
    /// the user can pick a specific fan instead of the "Fan Speed" auto pick.
    /// Source ids: "fan:lhm:&lt;identifier&gt;" / "fan:hw:&lt;label&gt;".
    /// </summary>
    public (string Source, string Label)[] GetFanSources()
    {
        lock (_lock)
        {
            try
            {
                EnsureOpen();
                RefreshIfDue();

                var list = new List<(string Source, string Label)>();
                foreach (var s in EnumerateSensors())
                {
                    if (s.SensorType != LhmSensorType.Fan || !s.Value.HasValue) continue;
                    list.Add(($"fan:lhm:{s.Identifier}", $"Fan: {s.Name} · {s.Hardware.Name}"));
                }
                foreach (var s in ReadHwinfoSensors())
                {
                    if (s.Type != HwinfoSensorType.SensorTypeFan) continue;
                    string name = string.IsNullOrWhiteSpace(s.LabelUser) ? s.LabelOrig : s.LabelUser;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    list.Add(($"fan:hw:{name}", $"Fan: {name} (HWiNFO)"));
                }
                return list.DistinctBy(f => f.Source).ToArray();
            }
            catch
            {
                return Array.Empty<(string, string)>();
            }
        }
    }

    /// <summary>Reads one specific fan picked by the user from GetFanSources().</summary>
    private HardwareMetricReading ReadSpecificFan(string source)
    {
        if (source.StartsWith("fan:lhm:", StringComparison.Ordinal))
        {
            string id = source["fan:lhm:".Length..];
            var sensor = EnumerateSensors().FirstOrDefault(s =>
                s.SensorType == LhmSensorType.Fan && s.Identifier.ToString() == id);
            if (sensor?.Value is float v && v >= 0f)
                return MakeLhmReading(source, "Fan", sensor, v, "RPM");
            return new HardwareMetricReading(source, "Fan", "--", false);
        }

        if (source.StartsWith("fan:hw:", StringComparison.Ordinal))
        {
            string name = source["fan:hw:".Length..];
            HwinfoSensorReading? sensor = ReadHwinfoSensors()
                .Where(s => s.Type == HwinfoSensorType.SensorTypeFan && s.Value >= 0)
                .Where(s => string.Equals(
                    string.IsNullOrWhiteSpace(s.LabelUser) ? s.LabelOrig : s.LabelUser,
                    name, StringComparison.OrdinalIgnoreCase))
                .Select(s => (HwinfoSensorReading?)s)
                .FirstOrDefault();
            if (sensor != null)
                return MakeHwinfoReading(source, "Fan", sensor.Value, "RPM");
            return new HardwareMetricReading(source, "Fan", "--", false);
        }

        return ReadFanSpeed();
    }

    private void EnsureOpen()
    {
        if (_opened) return;

        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
        };
        _computer.Open();
        _opened = true;
        _failed = false;
    }

    private void RefreshIfDue()
    {
        if (_computer == null) return;
        if ((DateTime.UtcNow - _lastRefreshUtc).TotalMilliseconds < RefreshMs) return;

        foreach (var hardware in _computer.Hardware)
            UpdateHardwareTree(hardware);
        _lastRefreshUtc = DateTime.UtcNow;
    }

    private static void UpdateHardwareTree(IHardware hardware)
    {
        hardware.Update();
        foreach (var sub in hardware.SubHardware)
            UpdateHardwareTree(sub);
    }

    private HardwareMetricReading ReadSensor(
        string source,
        string label,
        Func<IHardware, bool> hardwareFilter,
        LhmSensorType sensorType,
        Func<ISensor, int> preference,
        string unit = "°")
    {
        var sensor = EnumerateSensors()
            .Where(s => s.Value.HasValue
                && s.SensorType == sensorType
                && hardwareFilter(s.Hardware)
                && IsUsefulSensorValue(s))
            .OrderBy(preference)
            .ThenBy(s => s.Index)
            .FirstOrDefault();

        if (sensor == null)
        {
            LogUnavailableSensorOnce(source, label, sensorType, hardwareFilter);
            return new HardwareMetricReading(source, label, "--", false);
        }

        return MakeLhmReading(source, label, sensor, sensor.Value!.Value, unit);
    }

    private HardwareMetricReading MakeLhmReading(string source, string label, ISensor sensor, float value, string unit)
    {
        var reading = new HardwareMetricReading(
            source, label, FormatValue(value, unit), true,
            ProviderLhm, $"{sensor.Hardware.Name} · {sensor.Name}",
            ComputeGaugeFraction(source, value));
        _lastFallback = reading;
        return reading;
    }

    /// <summary>
    /// Maps a reading to a 0-1 fill fraction for the gauge layout. Percent-style
    /// metrics map directly; the rest use sensible full-scale ranges (or a real
    /// used/total ratio for memory). Returns -1 when no fraction makes sense.
    /// </summary>
    private float ComputeGaugeFraction(string source, float value)
    {
        if (source.StartsWith("fan:", StringComparison.Ordinal))
            return Math.Clamp(value / 2200f, 0f, 1f);

        float frac = source switch
        {
            "cpu_temp" or "gpu_temp" => value / 95f,
            "cpu_load" or "gpu_load" or "memory_load" or "vram_load" => value / 100f,
            "cpu_clock" => value / 6000f,
            "gpu_clock" => value / 3500f,
            "cpu_power" => value / 200f,
            "gpu_power" => value / 500f,
            "fan_speed" => value / 2200f,
            "memory_used" => MemoryUsedFraction(value),
            "vram_used" => VramUsedFraction(value),
            _ => -1f,
        };
        return frac < 0f ? -1f : Math.Clamp(frac, 0f, 1f);
    }

    private float MemoryUsedFraction(float usedGb)
    {
        var available = EnumerateSensors().FirstOrDefault(s =>
            s.Value.HasValue
            && s.Hardware.HardwareType == HardwareType.Memory
            && s.SensorType == LhmSensorType.Data
            && (s.Name ?? "").Contains("available", StringComparison.OrdinalIgnoreCase));
        if (available?.Value is float avail && avail > 0f)
            return usedGb / (usedGb + avail);
        return usedGb / 64f;
    }

    private float VramUsedFraction(float usedGb)
    {
        var total = EnumerateSensors().FirstOrDefault(s =>
            s.Value.HasValue
            && IsGpu(s.Hardware)
            && (s.SensorType == LhmSensorType.Data || s.SensorType == LhmSensorType.SmallData)
            && (s.Name ?? "").Contains("memory", StringComparison.OrdinalIgnoreCase)
            && (s.Name ?? "").Contains("total", StringComparison.OrdinalIgnoreCase));
        if (total?.Value is float t && t > 0f)
        {
            float totalGb = total.SensorType == LhmSensorType.SmallData ? t / 1024f : t;
            return usedGb / totalGb;
        }
        return usedGb / 16f;
    }

    /// <summary>
    /// CPU metrics via LibreHardwareMonitor first, falling back to HWiNFO shared
    /// memory. LHM needs admin rights for its kernel driver — without elevation,
    /// Ryzen CPU temp/clock/power are all unavailable, so the HWiNFO path is the
    /// only non-admin source for them.
    /// </summary>
    private HardwareMetricReading ReadCpuMetric(
        string source,
        string label,
        LhmSensorType sensorType,
        Func<ISensor, int> preference,
        string unit)
    {
        var lhmReading = ReadSensor(source, label, IsCpu, sensorType, preference, unit);
        if (lhmReading.IsAvailable)
            return lhmReading;

        return ReadHwinfoCpuMetric(source, label, sensorType, unit) ?? lhmReading;
    }

    private HardwareMetricReading? ReadHwinfoCpuMetric(string source, string label, LhmSensorType sensorType, string unit)
    {
        HwinfoSensorType hwType;
        Func<HwinfoSensorReading, bool> filter;
        Func<HwinfoSensorReading, int> prefer;

        switch (sensorType)
        {
            case LhmSensorType.Temperature:
                hwType = HwinfoSensorType.SensorTypeTemp;
                filter = s => IsUsefulTemperature(s.Value) && IsHwinfoCpuTemperature(s);
                prefer = PreferHwinfoCpuTemperature;
                break;
            case LhmSensorType.Load:
                hwType = HwinfoSensorType.SensorTypeUsage;
                filter = s => s.Value >= 0 && s.Value <= 100 && IsHwinfoCpuSensor(s, "usage", "utility");
                prefer = PreferHwinfoCpuLoad;
                break;
            case LhmSensorType.Clock:
                hwType = HwinfoSensorType.SensorTypeClock;
                filter = s => s.Value > 0 && IsHwinfoCpuSensor(s, "clock");
                prefer = PreferHwinfoCpuClock;
                break;
            case LhmSensorType.Power:
                hwType = HwinfoSensorType.SensorTypePower;
                filter = s => s.Value > 0 && IsHwinfoCpuSensor(s, "power", "ppt");
                prefer = PreferHwinfoCpuPower;
                break;
            default:
                return null;
        }

        HwinfoSensorReading? sensor = ReadHwinfoSensors()
            .Where(s => s.Type == hwType && filter(s))
            .OrderBy(prefer)
            .ThenBy(s => s.Index)
            .Select(s => (HwinfoSensorReading?)s)
            .FirstOrDefault();

        if (sensor == null)
            return null;

        return MakeHwinfoReading(source, label, sensor.Value, unit);
    }

    private HardwareMetricReading MakeHwinfoReading(string source, string label, HwinfoSensorReading sensor, string unit)
    {
        string name = string.IsNullOrWhiteSpace(sensor.LabelUser) ? sensor.LabelOrig : sensor.LabelUser;
        var reading = new HardwareMetricReading(
            source, label, FormatValue((float)sensor.Value, unit), true,
            ProviderHwinfo, name ?? "",
            ComputeGaugeFraction(source, (float)sensor.Value));
        _lastFallback = reading;
        return reading;
    }

    private IEnumerable<HwinfoSensorReading> ReadHwinfoSensors()
    {
        try
        {
            _hwinfoReader ??= new HwinfoReader(50);
            return _hwinfoReader.ReadLocal();
        }
        catch (Exception ex) when (ex is FileNotFoundException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is TimeoutException)
        {
            if (!_hwinfoUnavailableLogged)
            {
                _hwinfoUnavailableLogged = true;
                Logger.Log($"HWiNFO shared memory fallback unavailable: {ex.Message}. Start HWiNFO and enable Shared Memory Support if CPU sensors stay unavailable.");
            }

            _hwinfoReader?.Dispose();
            _hwinfoReader = null;
            return Array.Empty<HwinfoSensorReading>();
        }
    }

    private HardwareMetricReading ReadMemoryUsed()
    {
        var sensor = EnumerateSensors()
            .Where(s => s.Value.HasValue
                && s.Hardware.HardwareType == HardwareType.Memory
                && s.SensorType == LhmSensorType.Data
                && IsUsefulSensorValue(s))
            .OrderBy(PreferUsedMemory)
            .ThenBy(s => s.Index)
            .FirstOrDefault();

        if (sensor == null)
            return new HardwareMetricReading("memory_used", "Memory", "--", false);

        return MakeLhmReading("memory_used", "Memory", sensor, sensor.Value!.Value, "GB");
    }

    /// <summary>
    /// VRAM used in GB. NVIDIA (and D3D) report memory as SmallData sensors in MB,
    /// AMD additionally exposes Data sensors in GB — accept both and normalize.
    /// </summary>
    private HardwareMetricReading ReadVramUsed()
    {
        var sensor = EnumerateSensors()
            .Where(s => s.Value.HasValue
                && IsGpu(s.Hardware)
                && (s.SensorType == LhmSensorType.Data || s.SensorType == LhmSensorType.SmallData)
                && s.Value.Value > 0f
                && IsVramUsedSensor(s.Name))
            .OrderBy(PreferVramUsed)
            .ThenBy(s => s.Index)
            .FirstOrDefault();

        if (sensor == null)
        {
            LogUnavailableSensorOnce("vram_used", "VRAM", LhmSensorType.SmallData, IsGpu);
            return new HardwareMetricReading("vram_used", "VRAM", "--", false);
        }

        float gb = sensor.SensorType == LhmSensorType.SmallData
            ? sensor.Value!.Value / 1024f
            : sensor.Value!.Value;
        return MakeLhmReading("vram_used", "VRAM", sensor, gb, "GB");
    }

    /// <summary>
    /// VRAM load percent. Prefers a real Load sensor; computes used/total from the
    /// MB memory sensors when the GPU doesn't expose one.
    /// </summary>
    private HardwareMetricReading ReadVramLoad()
    {
        var loadSensor = EnumerateSensors()
            .Where(s => s.Value.HasValue
                && IsGpu(s.Hardware)
                && s.SensorType == LhmSensorType.Load
                && IsUsefulSensorValue(s)
                && (s.Name ?? "").Contains("memory", StringComparison.OrdinalIgnoreCase)
                && !(s.Name ?? "").Contains("controller", StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Index)
            .FirstOrDefault();

        if (loadSensor != null)
            return MakeLhmReading("vram_load", "VRAM", loadSensor, loadSensor.Value!.Value, "%");

        var memSensors = EnumerateSensors()
            .Where(s => s.Value.HasValue
                && IsGpu(s.Hardware)
                && (s.SensorType == LhmSensorType.Data || s.SensorType == LhmSensorType.SmallData)
                && s.Value.Value > 0f)
            .ToList();
        var used = memSensors.Where(s => IsVramUsedSensor(s.Name)).OrderBy(PreferVramUsed).FirstOrDefault();
        var total = memSensors.FirstOrDefault(s =>
            (s.Name ?? "").Contains("total", StringComparison.OrdinalIgnoreCase)
            && (s.Name ?? "").Contains("memory", StringComparison.OrdinalIgnoreCase)
            && s.Hardware == used?.Hardware);

        if (used == null || total == null || total.Value!.Value <= 0f)
        {
            LogUnavailableSensorOnce("vram_load", "VRAM", LhmSensorType.Load, IsGpu);
            return new HardwareMetricReading("vram_load", "VRAM", "--", false);
        }

        float pct = Math.Clamp(used.Value!.Value / total.Value!.Value * 100f, 0f, 100f);
        return MakeLhmReading("vram_load", "VRAM", used, pct, "%");
    }

    /// <summary>
    /// Fan RPM. Prefers CPU fan / pump, then motherboard fans, then GPU fans.
    /// Without admin rights LHM only sees GPU fans, so HWiNFO is also consulted
    /// when it has a better (CPU-ish) fan than LHM offered.
    /// </summary>
    private HardwareMetricReading ReadFanSpeed()
    {
        var sensor = EnumerateSensors()
            .Where(s => s.Value.HasValue
                && s.SensorType == LhmSensorType.Fan
                && IsUsefulSensorValue(s))
            .OrderBy(PreferFan)
            .ThenBy(s => s.Index)
            .FirstOrDefault();

        // LHM found only GPU fans (typical when not elevated) — see if HWiNFO has a CPU fan
        bool lhmIsGpuFan = sensor != null && IsGpu(sensor.Hardware);
        if (sensor == null || lhmIsGpuFan)
        {
            var hwinfoFan = ReadHwinfoFan();
            if (hwinfoFan != null)
                return hwinfoFan;
        }

        if (sensor == null)
        {
            LogUnavailableSensorOnce("fan_speed", "Fan", LhmSensorType.Fan, h => true);
            return new HardwareMetricReading("fan_speed", "Fan", "--", false);
        }

        return MakeLhmReading("fan_speed", "Fan", sensor, sensor.Value!.Value, "RPM");
    }

    private HardwareMetricReading? ReadHwinfoFan()
    {
        HwinfoSensorReading? sensor = ReadHwinfoSensors()
            .Where(s => s.Type == HwinfoSensorType.SensorTypeFan
                && s.Value >= 0
                && !HwinfoText(s).Contains("gpu"))
            .OrderBy(PreferHwinfoFan)
            .ThenBy(s => s.Index)
            .Select(s => (HwinfoSensorReading?)s)
            .FirstOrDefault();

        if (sensor == null)
            return null;

        return MakeHwinfoReading("fan_speed", "Fan", sensor.Value, "RPM");
    }

    private IEnumerable<ISensor> EnumerateSensors()
    {
        if (_computer == null) yield break;
        foreach (var hardware in _computer.Hardware)
        {
            foreach (var sensor in EnumerateSensors(hardware))
                yield return sensor;
        }
    }

    private static IEnumerable<ISensor> EnumerateSensors(IHardware hardware)
    {
        foreach (var sensor in hardware.Sensors)
            yield return sensor;
        foreach (var sub in hardware.SubHardware)
        {
            foreach (var sensor in EnumerateSensors(sub))
                yield return sensor;
        }
    }

    private static bool IsCpu(IHardware hardware) => hardware.HardwareType == HardwareType.Cpu;
    private static bool IsGpu(IHardware hardware) =>
        hardware.HardwareType == HardwareType.GpuAmd
        || hardware.HardwareType == HardwareType.GpuIntel
        || hardware.HardwareType == HardwareType.GpuNvidia;

    private static int PreferCpuPackage(ISensor sensor)
    {
        string name = sensor.Name ?? "";
        if (name.Contains("package", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Contains("ccd", StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Contains("core", StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }

    private static int PreferGpuCore(ISensor sensor)
    {
        string name = sensor.Name ?? "";
        if (name.Contains("core", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Contains("hot spot", StringComparison.OrdinalIgnoreCase)) return 1;
        return 2;
    }

    private static int PreferPackageOrTotal(ISensor sensor)
    {
        string name = sensor.Name ?? "";
        if (name.Contains("package", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Contains("total", StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Contains("gpu", StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }

    private static int PreferTotalOrCore(ISensor sensor)
    {
        string name = sensor.Name ?? "";
        if (name.Contains("total", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Contains("core", StringComparison.OrdinalIgnoreCase)) return 1;
        return 2;
    }

    private static int PreferUsedMemory(ISensor sensor)
    {
        string name = sensor.Name ?? "";
        if (name.Contains("used", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Contains("memory", StringComparison.OrdinalIgnoreCase)) return 1;
        return 2;
    }

    private static bool IsVramUsedSensor(string? name)
    {
        string n = (name ?? "").ToLowerInvariant();
        if (!n.Contains("used")) return false;
        if (n.Contains("shared") || n.Contains("free") || n.Contains("total")) return false;
        return n.Contains("memory") || n.Contains("vram") || n.Contains("dedicated");
    }

    private static int PreferVramUsed(ISensor sensor)
    {
        string name = (sensor.Name ?? "").ToLowerInvariant();
        if (name.Contains("gpu memory used")) return 0; // native NVML/ADL sensor
        if (name.Contains("dedicated")) return 1;       // D3D dedicated memory
        return 2;
    }

    private static int PreferFan(ISensor sensor)
    {
        string name = (sensor.Name ?? "").ToLowerInvariant();
        if (name.Contains("cpu")) return 0;
        if (name.Contains("pump")) return 1;
        if (IsGpu(sensor.Hardware)) return 3; // GPU fans last — usually idle/0 RPM
        return 2;
    }

    private static string HwinfoText(HwinfoSensorReading sensor) =>
        $"{sensor.LabelUser} {sensor.LabelOrig} {sensor.GroupLabelUser} {sensor.GroupLabelOrig}".ToLowerInvariant();

    private static bool IsHwinfoCpuSensor(HwinfoSensorReading sensor, params string[] labelKeywords)
    {
        string label = $"{sensor.LabelUser} {sensor.LabelOrig}".ToLowerInvariant();
        string group = $"{sensor.GroupLabelUser} {sensor.GroupLabelOrig}".ToLowerInvariant();
        if (label.Contains("gpu") || group.Contains("gpu")) return false;

        bool cpuGroup = group.Contains("cpu")
            || group.Contains("processor")
            || group.Contains("ryzen")
            || group.Contains("core")
            || group.Contains("amd")
            || group.Contains("intel")
            || label.Contains("cpu");
        if (!cpuGroup) return false;

        foreach (var keyword in labelKeywords)
            if (label.Contains(keyword)) return true;
        return false;
    }

    private static bool IsHwinfoCpuTemperature(HwinfoSensorReading sensor)
    {
        string label = $"{sensor.LabelUser} {sensor.LabelOrig}".ToLowerInvariant();
        string group = $"{sensor.GroupLabelUser} {sensor.GroupLabelOrig}".ToLowerInvariant();
        if (label.Contains("gpu") || group.Contains("gpu")) return false;

        bool cpuGroup = group.Contains("cpu")
            || group.Contains("processor")
            || group.Contains("ryzen")
            || group.Contains("core")
            || group.Contains("amd");
        bool cpuLabel = label.Contains("cpu")
            || label.Contains("tctl")
            || label.Contains("tdie")
            || label.Contains("ccd")
            || label.Contains("package")
            || label.Contains("core max");

        return cpuGroup && cpuLabel;
    }

    private static int PreferHwinfoCpuTemperature(HwinfoSensorReading sensor)
    {
        string label = $"{sensor.LabelUser} {sensor.LabelOrig}".ToLowerInvariant();
        if (label.Contains("tctl") && label.Contains("tdie")) return 0;
        if (label.Contains("tdie")) return 1;
        if (label.Contains("package")) return 2;
        if (label.Contains("core max")) return 3;
        if (label.Contains("cpu")) return 4;
        return 5;
    }

    private static int PreferHwinfoCpuLoad(HwinfoSensorReading sensor)
    {
        string label = $"{sensor.LabelUser} {sensor.LabelOrig}".ToLowerInvariant();
        if (label.Contains("total cpu usage")) return 0;
        if (label.Contains("total")) return 1;
        if (label.Contains("cpu usage")) return 2;
        return 3;
    }

    private static int PreferHwinfoCpuClock(HwinfoSensorReading sensor)
    {
        string label = $"{sensor.LabelUser} {sensor.LabelOrig}".ToLowerInvariant();
        if (label.Contains("average") || label.Contains("avg")) return 0;
        if (label.Contains("core 0")) return 1;
        if (label.Contains("max")) return 2;
        return 3;
    }

    private static int PreferHwinfoCpuPower(HwinfoSensorReading sensor)
    {
        string label = $"{sensor.LabelUser} {sensor.LabelOrig}".ToLowerInvariant();
        if (label.Contains("package")) return 0;
        if (label.Contains("ppt")) return 1;
        if (label.Contains("cpu")) return 2;
        return 3;
    }

    private static int PreferHwinfoFan(HwinfoSensorReading sensor)
    {
        string text = HwinfoText(sensor);
        if (text.Contains("cpu")) return 0;
        if (text.Contains("pump")) return 1;
        if (text.Contains("#1") || text.Contains("fan1") || text.Contains("fan 1")) return 2;
        return 3;
    }

    private static bool IsUsefulSensorValue(ISensor sensor)
    {
        float value = sensor.Value ?? float.NaN;
        if (float.IsNaN(value) || float.IsInfinity(value)) return false;

        return sensor.SensorType switch
        {
            LhmSensorType.Temperature => IsUsefulTemperature(value),
            LhmSensorType.Load => value >= 0f && value <= 100f,
            LhmSensorType.Clock => value > 0f,
            LhmSensorType.Power => value > 0f,
            LhmSensorType.Data => value > 0f,
            LhmSensorType.SmallData => value > 0f,
            LhmSensorType.Fan => value >= 0f,
            _ => true,
        };
    }

    private void LogUnavailableSensorOnce(
        string source,
        string label,
        LhmSensorType sensorType,
        Func<IHardware, bool> hardwareFilter)
    {
        if (!_loggedUnavailableSources.Add(source))
            return;

        var candidates = EnumerateSensors()
            .Where(s => s.Value.HasValue
                && s.SensorType == sensorType
                && hardwareFilter(s.Hardware))
            .Take(6)
            .Select(s => $"{s.Hardware.Name}/{s.Name}={s.Value:0.##}")
            .ToArray();

        string details = candidates.Length == 0 ? "no candidates" : string.Join(", ", candidates);
        Logger.Log($"Hardware monitor {label} unavailable from LibreHardwareMonitor ({details}).");
    }

    private static bool IsUsefulTemperature(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value) && value > 0.5 && value < 130;
    }

    private static string FormatValue(float value, string unit)
    {
        return unit switch
        {
            "%" => $"{Math.Round(value):0}%",
            "MHz" => value >= 1000f ? $"{value / 1000f:0.0} GHz" : $"{Math.Round(value):0} MHz",
            "W" => $"{value:0} W",
            "GB" => $"{value:0.0} GB",
            "RPM" => $"{Math.Round(value):0} RPM",
            _ => $"{Math.Round(value):0}{unit}",
        };
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _computer?.Close();
            _computer = null;
            _opened = false;
            _hwinfoReader?.Dispose();
            _hwinfoReader = null;
        }
    }
}
