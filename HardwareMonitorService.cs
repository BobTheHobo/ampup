using LibreHardwareMonitor.Hardware;

namespace AmpUp;

internal sealed record HardwareMetricReading(
    string Source,
    string Label,
    string ValueText,
    bool IsAvailable);

internal sealed class HardwareMonitorService : IDisposable
{
    private const int RefreshMs = 2000;

    private readonly object _lock = new();
    private Computer? _computer;
    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private bool _opened;
    private bool _failed;
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

                return source switch
                {
                    "cpu_temp" => ReadSensor(source, "CPU Temp", IsCpu, SensorType.Temperature, PreferCpuPackage),
                    "gpu_temp" => ReadSensor(source, "GPU Temp", IsGpu, SensorType.Temperature, PreferGpuCore),
                    "cpu_load" => ReadSensor(source, "CPU Load", IsCpu, SensorType.Load, PreferTotalOrCore, "%"),
                    "gpu_load" => ReadSensor(source, "GPU Load", IsGpu, SensorType.Load, PreferTotalOrCore, "%"),
                    "cpu_clock" => ReadSensor(source, "CPU Clock", IsCpu, SensorType.Clock, PreferTotalOrCore, "MHz"),
                    "gpu_clock" => ReadSensor(source, "GPU Clock", IsGpu, SensorType.Clock, PreferGpuCore, "MHz"),
                    "cpu_power" => ReadSensor(source, "CPU Power", IsCpu, SensorType.Power, PreferPackageOrTotal, "W"),
                    "gpu_power" => ReadSensor(source, "GPU Power", IsGpu, SensorType.Power, PreferPackageOrTotal, "W"),
                    "memory_used" => ReadMemoryUsed(),
                    "memory_load" => ReadSensor(source, "Memory", h => h.HardwareType == HardwareType.Memory, SensorType.Load, PreferUsedMemory, "%"),
                    "vram_used" => ReadSensor(source, "VRAM", IsGpu, SensorType.Data, PreferMemoryData, "GB"),
                    "vram_load" => ReadSensor(source, "VRAM", IsGpu, SensorType.Load, PreferMemoryData, "%"),
                    "fan_speed" => ReadSensor(source, "Fan", h => true, SensorType.Fan, PreferAny, "RPM"),
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
        foreach (var (s, label) in Sources)
            if (s == source) return label;
        return "Hardware";
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
        SensorType sensorType,
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
            return new HardwareMetricReading(source, label, "--", false);

        string value = FormatValue(sensor.Value!.Value, unit);
        var reading = new HardwareMetricReading(source, label, value, true);
        _lastFallback = reading;
        return reading;
    }

    private HardwareMetricReading ReadMemoryUsed()
    {
        var sensor = EnumerateSensors()
            .Where(s => s.Value.HasValue
                && s.Hardware.HardwareType == HardwareType.Memory
                && s.SensorType == SensorType.Data
                && IsUsefulSensorValue(s))
            .OrderBy(PreferUsedMemory)
            .ThenBy(s => s.Index)
            .FirstOrDefault();

        if (sensor == null)
            return new HardwareMetricReading("memory_used", "Memory", "--", false);

        var reading = new HardwareMetricReading("memory_used", "Memory", FormatValue(sensor.Value!.Value, "GB"), true);
        _lastFallback = reading;
        return reading;
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

    private static int PreferMemoryData(ISensor sensor)
    {
        string name = sensor.Name ?? "";
        if (name.Contains("memory", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Contains("vram", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Contains("dedicated", StringComparison.OrdinalIgnoreCase)) return 1;
        return 2;
    }

    private static int PreferAny(ISensor sensor) => sensor.Index;

    private static bool IsUsefulSensorValue(ISensor sensor)
    {
        float value = sensor.Value ?? float.NaN;
        if (float.IsNaN(value) || float.IsInfinity(value)) return false;

        return sensor.SensorType switch
        {
            SensorType.Temperature => value > 0.5f && value < 130f,
            SensorType.Load => value >= 0f && value <= 100f,
            SensorType.Clock => value > 0f,
            SensorType.Power => value > 0f,
            SensorType.Data => value > 0f,
            SensorType.Fan => value >= 0f,
            _ => true,
        };
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
        }
    }
}
