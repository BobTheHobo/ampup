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
        ("memory_used", "Memory Used"),
        ("memory_load", "Memory Load"),
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
                    "memory_used" => ReadMemoryUsed(),
                    "memory_load" => ReadSensor(source, "Memory", h => h.HardwareType == HardwareType.Memory, SensorType.Load, PreferUsedMemory, "%"),
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
                && hardwareFilter(s.Hardware))
            .OrderBy(preference)
            .ThenBy(s => s.Index)
            .FirstOrDefault();

        if (sensor == null)
            return new HardwareMetricReading(source, label, "--", false);

        string value = unit == "%"
            ? $"{Math.Round(sensor.Value!.Value):0}%"
            : $"{Math.Round(sensor.Value!.Value):0}{unit}";
        var reading = new HardwareMetricReading(source, label, value, true);
        _lastFallback = reading;
        return reading;
    }

    private HardwareMetricReading ReadMemoryUsed()
    {
        var sensor = EnumerateSensors()
            .Where(s => s.Value.HasValue
                && s.Hardware.HardwareType == HardwareType.Memory
                && s.SensorType == SensorType.Data)
            .OrderBy(PreferUsedMemory)
            .ThenBy(s => s.Index)
            .FirstOrDefault();

        if (sensor == null)
            return new HardwareMetricReading("memory_used", "Memory", "--", false);

        var reading = new HardwareMetricReading("memory_used", "Memory", $"{sensor.Value!.Value:0.0} GB", true);
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
