using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AmpUp.Services;

public sealed record SignalRgbEffectInfo(string Name, string Id);

public static class SignalRgbEffectCatalog
{
    private const string EffectsRegistryPath = @"Software\WhirlwindFX\SignalRgb\effects";

    public static List<SignalRgbEffectInfo> GetInstalledEffects()
    {
        var effects = new Dictionary<string, SignalRgbEffectInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (string id in GetInstalledEffectIds())
        {
            string? name = ResolveEffectName(id);
            if (string.IsNullOrWhiteSpace(name)) continue;

            name = name.Trim();
            effects[name] = new SignalRgbEffectInfo(name, id);
        }

        return effects.Values
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static void ApplyEffect(string effectName)
    {
        if (string.IsNullOrWhiteSpace(effectName)) return;

        string encoded = Uri.EscapeDataString(effectName.Trim());
        string url = $"signalrgb://effect/apply/{encoded}?-silentlaunch-";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Logger.Log($"SignalRGB effect apply requested: {effectName}");
        }
        catch (Exception ex)
        {
            Logger.Log($"SignalRGB effect apply error ({effectName}): {ex.Message}");
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
}
