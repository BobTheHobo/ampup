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
    bool IsAvailable);

internal sealed class HardwareMonitorService : IDisposable
{
    private const int RefreshMs = 1000;

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

                return source switch
                {
                    "cpu_temp" => ReadCpuTemperature(),
                    "gpu_temp" => ReadSensor(source, "GPU Temp", IsGpu, LhmSensorType.Temperature, PreferGpuCore),
                    "cpu_load" => ReadSensor(source, "CPU Load", IsCpu, LhmSensorType.Load, PreferTotalOrCore, "%"),
                    "gpu_load" => ReadSensor(source, "GPU Load", IsGpu, LhmSensorType.Load, PreferTotalOrCore, "%"),
                    "cpu_clock" => ReadSensor(source, "CPU Clock", IsCpu, LhmSensorType.Clock, PreferTotalOrCore, "MHz"),
                    "gpu_clock" => ReadSensor(source, "GPU Clock", IsGpu, LhmSensorType.Clock, PreferGpuCore, "MHz"),
                    "cpu_power" => ReadSensor(source, "CPU Power", IsCpu, LhmSensorType.Power, PreferPackageOrTotal, "W"),
                    "gpu_power" => ReadSensor(source, "GPU Power", IsGpu, LhmSensorType.Power, PreferPackageOrTotal, "W"),
                    "memory_used" => ReadMemoryUsed(),
                    "memory_load" => ReadSensor(source, "Memory", h => h.HardwareType == HardwareType.Memory, LhmSensorType.Load, PreferUsedMemory, "%"),
                    "vram_used" => ReadSensor(source, "VRAM", IsGpu, LhmSensorType.Data, PreferMemoryData, "GB"),
                    "vram_load" => ReadSensor(source, "VRAM", IsGpu, LhmSensorType.Load, PreferMemoryData, "%"),
                    "fan_speed" => ReadSensor(source, "Fan", h => true, LhmSensorType.Fan, PreferAny, "RPM"),
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

        string value = FormatValue(sensor.Value!.Value, unit);
        var reading = new HardwareMetricReading(source, label, value, true);
        _lastFallback = reading;
        return reading;
    }

    private HardwareMetricReading ReadCpuTemperature()
    {
        var lhmReading = ReadSensor("cpu_temp", "CPU Temp", IsCpu, LhmSensorType.Temperature, PreferCpuPackage);
        if (lhmReading.IsAvailable)
            return lhmReading;

        var hwinfoReading = ReadHwinfoCpuTemperature();
        return hwinfoReading ?? lhmReading;
    }

    private HardwareMetricReading? ReadHwinfoCpuTemperature()
    {
        HwinfoSensorReading? sensor = ReadHwinfoSensors()
            .Where(s => s.Type == HwinfoSensorType.SensorTypeTemp
                && IsUsefulTemperature(s.Value)
                && IsHwinfoCpuTemperature(s))
            .OrderBy(PreferHwinfoCpuTemperature)
            .ThenBy(s => s.Index)
            .Select(s => (HwinfoSensorReading?)s)
            .FirstOrDefault();

        if (sensor == null)
            return null;

        var reading = new HardwareMetricReading("cpu_temp", "CPU Temp", FormatValue((float)sensor.Value.Value, "°"), true);
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
                Logger.Log($"HWiNFO shared memory CPU temp fallback unavailable: {ex.Message}. Start HWiNFO and enable Shared Memory Support if CPU temperature stays unavailable.");
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
