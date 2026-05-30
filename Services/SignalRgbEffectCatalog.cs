using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using AmpUp.Core;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace AmpUp.Services;

public sealed record SignalRgbEffectInfo(string Name, string Id, string ImageUrl = "");
public sealed record SignalRgbLayoutInfo(string Name, string Path);

public static class SignalRgbEffectCatalog
{
    private const string EffectsRegistryPath = @"Software\WhirlwindFX\SignalRgb\effects";
    private const string SolidColorEffectName = "Solid Color";
    private static readonly HttpClient ImageHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
    };
    private static Dictionary<string, string>? _catalogImageUrlsById;

    public static string LastAppliedEffectName { get; private set; } = "";

    public static List<SignalRgbEffectInfo> GetInstalledEffects()
    {
        var effects = new Dictionary<string, SignalRgbEffectInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (string id in GetInstalledEffectIds())
        {
            string? name = ResolveEffectName(id);
            if (string.IsNullOrWhiteSpace(name)) continue;

            name = name.Trim();
            if (IsUnresolvedHashName(name, id)) continue;

            effects[name] = new SignalRgbEffectInfo(name, id, ResolveEffectImageUrl(id, name));
        }

        return effects.Values
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static void ApplyEffect(string effectName)
    {
        if (string.IsNullOrWhiteSpace(effectName)) return;

        ApplySignalRgbUrl("effect", effectName);
        LastAppliedEffectName = effectName.Trim();
    }

    public static void ApplyBlackout()
    {
        string encoded = Uri.EscapeDataString(SolidColorEffectName);
        string url = $"signalrgb://effect/apply/{encoded}?color=%23000000&background=%23000000&-silentlaunch-";
        LaunchSignalRgbUrl(url, "SignalRGB blackout requested", "SignalRGB blackout error");
    }

    public static void RestoreLastEffect(string fallbackEffectName)
    {
        string effectName = !string.IsNullOrWhiteSpace(LastAppliedEffectName)
            ? LastAppliedEffectName
            : fallbackEffectName;

        if (!string.IsNullOrWhiteSpace(effectName))
            ApplyEffect(effectName);
    }

    public static List<SignalRgbLayoutInfo> GetInstalledLayouts()
    {
        var layouts = new Dictionary<string, SignalRgbLayoutInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (string dir in GetLikelyLayoutDirectories())
        {
            if (!Directory.Exists(dir)) continue;

            try
            {
                foreach (string path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    string extension = Path.GetExtension(path);
                    if (!IsLikelyLayoutFile(extension)) continue;

                    layouts[name] = new SignalRgbLayoutInfo(name, path);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"SignalRGB layout cache read error ({dir}): {ex.Message}");
            }
        }

        return layouts.Values
            .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static void ApplyLayout(string layoutName)
    {
        if (string.IsNullOrWhiteSpace(layoutName)) return;

        ApplySignalRgbUrl("layout", layoutName);
    }

    public static string GetOrCacheEffectImagePath(SignalRgbEffectInfo effect)
    {
        if (string.IsNullOrWhiteSpace(effect.ImageUrl))
            return "";

        try
        {
            string cacheDir = Path.Combine(ConfigManager.AppDataDir, "SignalRgbIcons");
            Directory.CreateDirectory(cacheDir);

            string extension = Path.GetExtension(new Uri(effect.ImageUrl).AbsolutePath);
            if (string.IsNullOrWhiteSpace(extension) || extension.Length > 5)
                extension = ".png";

            string fileName = $"{SanitizeFileName(effect.Id)}{extension}";
            string path = Path.Combine(cacheDir, fileName);
            if (File.Exists(path) && new FileInfo(path).Length > 0)
                return path;

            byte[] bytes = ImageHttpClient.GetByteArrayAsync(effect.ImageUrl).GetAwaiter().GetResult();
            if (bytes.Length == 0)
                return "";

            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch (Exception ex)
        {
            Logger.Log($"SignalRGB effect image cache error ({effect.Name}): {ex.Message}");
            return "";
        }
    }

    private static void ApplySignalRgbUrl(string type, string name)
    {
        string trimmed = name.Trim();
        string encoded = Uri.EscapeDataString(trimmed);
        string url = $"signalrgb://{type}/apply/{encoded}?-silentlaunch-";
        LaunchSignalRgbUrl(url, $"SignalRGB {type} apply requested: {trimmed}", $"SignalRGB {type} apply error ({trimmed})");
    }

    private static void LaunchSignalRgbUrl(string url, string successMessage, string errorPrefix)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Logger.Log(successMessage);
        }
        catch (Exception ex)
        {
            Logger.Log($"{errorPrefix}: {ex.Message}");
        }
    }

    private static IEnumerable<string> GetInstalledEffectIds()
    {
        using var key = Registry.CurrentUser.OpenSubKey(EffectsRegistryPath);
        if (key == null) yield break;

        foreach (string subKeyName in key.GetSubKeyNames())
        {
            if (string.Equals(subKeyName, "selected", StringComparison.OrdinalIgnoreCase))
                continue;

            yield return subKeyName;
        }
    }

    private static string? ResolveEffectName(string id)
    {
        if (id.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            return Path.GetFileNameWithoutExtension(id);

        string? cached = ResolveCachedEffectTitle(id);
        if (!string.IsNullOrWhiteSpace(cached))
            return cached;

        using var key = Registry.CurrentUser.OpenSubKey($@"{EffectsRegistryPath}\{id}");
        string? name = key?.GetValue("name") as string;
        return string.IsNullOrWhiteSpace(name) ? id : name;
    }

    private static string ResolveEffectImageUrl(string id, string name)
    {
        var catalog = GetCatalogImageUrlsById();
        if (catalog.TryGetValue(id, out var byId))
            return byId;

        return catalog.TryGetValue(name, out var byName) ? byName : "";
    }

    private static Dictionary<string, string> GetCatalogImageUrlsById()
    {
        if (_catalogImageUrlsById != null)
            return _catalogImageUrlsById;

        var urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhirlwindFX",
            "SignalRgb",
            "cache",
            "catalog_en.json");

        if (!File.Exists(path))
            return _catalogImageUrlsById = urls;

        try
        {
            var root = JObject.Parse(File.ReadAllText(path));
            foreach (var prop in root.Properties())
            {
                if (prop.Value is not JObject entry)
                    continue;

                string image = entry.Value<string>("image") ?? "";
                if (string.IsNullOrWhiteSpace(image))
                    continue;

                urls[prop.Name] = image;

                string name = entry.Value<string>("name") ?? "";
                if (!string.IsNullOrWhiteSpace(name))
                    urls[name] = image;
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"SignalRGB catalog image read error: {ex.Message}");
        }

        return _catalogImageUrlsById = urls;
    }

    private static string SanitizeFileName(string value)
    {
        string clean = string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return string.IsNullOrWhiteSpace(clean) ? Guid.NewGuid().ToString("N") : clean;
    }

    private static bool IsUnresolvedHashName(string name, string id)
    {
        if (!string.Equals(name, id, StringComparison.OrdinalIgnoreCase))
            return false;

        return name.Contains('-');
    }

    private static string? ResolveCachedEffectTitle(string id)
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhirlwindFX",
            "SignalRgb",
            "cache",
            "effects",
            id);

        if (!Directory.Exists(dir)) return null;

        string? htmlPath = Directory.GetFiles(dir, "*.html").FirstOrDefault()
            ?? Directory.GetFiles(dir).FirstOrDefault(f => f.EndsWith(".html", StringComparison.OrdinalIgnoreCase));
        if (htmlPath == null) return null;

        try
        {
            foreach (string line in File.ReadLines(htmlPath).Take(40))
            {
                var match = Regex.Match(line, @"<title>\s*(.*?)\s*</title>", RegexOptions.IgnoreCase);
                if (match.Success)
                    return System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"SignalRGB effect cache read error ({id}): {ex.Message}");
        }

        return null;
    }

    private static IEnumerable<string> GetLikelyLayoutDirectories()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        yield return Path.Combine(local, "WhirlwindFX", "SignalRgb", "layouts");
        yield return Path.Combine(local, "WhirlwindFX", "SignalRgb", "cache", "layouts");
        yield return Path.Combine(local, "VortxEngine", "SignalRgb", "layouts");
        yield return Path.Combine(docs, "WhirlwindFX", "Layouts");
        yield return Path.Combine(docs, "WhirlwindFX", "SignalRgb", "Layouts");
    }

    private static bool IsLikelyLayoutFile(string extension)
    {
        return extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".layout", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".signalrgb", StringComparison.OrdinalIgnoreCase);
    }
}
