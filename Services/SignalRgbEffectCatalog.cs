using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using AmpUp.Core;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingBrush = System.Drawing.SolidBrush;
using DrawingColor = System.Drawing.Color;
using DrawingFont = System.Drawing.Font;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingRectangleF = System.Drawing.RectangleF;
using DrawingStringAlignment = System.Drawing.StringAlignment;
using DrawingStringFormat = System.Drawing.StringFormat;

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
    private static Dictionary<string, string>? _catalogImageUrlsByNormalizedKey;
    private static readonly Dictionary<string, string> BuiltInEffectCatalogAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Rainbow", "Rainbow Pulse" },
        { "Screen Ambience", "Average Color" },
        { "Side To Side", "All Directions" },
    };

    public static string LastAppliedEffectName { get; private set; } = "";

    public static List<SignalRgbEffectInfo> GetInstalledEffects()
    {
        var effects = new Dictionary<string, SignalRgbEffectInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (string id in GetInstalledEffectIds())
        {
            string? name = ResolveEffectName(id);
            if (string.IsNullOrWhiteSpace(name)) continue;

            name = name.Trim();
            if (string.Equals(name, "rule", StringComparison.Ordinal))
                continue;
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
        string cacheDir = Path.Combine(ConfigManager.AppDataDir, "SignalRgbIcons");

        if (string.IsNullOrWhiteSpace(effect.ImageUrl))
            return GetOrCreateFallbackEffectImagePath(effect, cacheDir);

        try
        {
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
            return GetOrCreateFallbackEffectImagePath(effect, cacheDir);
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

        if (catalog.TryGetValue(name, out var byName))
            return byName;

        if (BuiltInEffectCatalogAliases.TryGetValue(name, out var alias)
            && catalog.TryGetValue(alias, out var byAlias))
            return byAlias;

        var normalizedCatalog = GetCatalogImageUrlsByNormalizedKey();
        string normalizedName = NormalizeCatalogKey(name);
        if (normalizedCatalog.TryGetValue(normalizedName, out var byNormalizedName))
            return byNormalizedName;

        if (BuiltInEffectCatalogAliases.TryGetValue(name, out alias)
            && normalizedCatalog.TryGetValue(NormalizeCatalogKey(alias), out var byNormalizedAlias))
            return byNormalizedAlias;

        return "";
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

                string description = entry.Value<string>("description") ?? "";
                if (!string.IsNullOrWhiteSpace(description))
                    urls[description] = image;
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"SignalRGB catalog image read error: {ex.Message}");
        }

        return _catalogImageUrlsById = urls;
    }

    private static Dictionary<string, string> GetCatalogImageUrlsByNormalizedKey()
    {
        if (_catalogImageUrlsByNormalizedKey != null)
            return _catalogImageUrlsByNormalizedKey;

        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in GetCatalogImageUrlsById())
        {
            string key = NormalizeCatalogKey(pair.Key);
            if (!string.IsNullOrWhiteSpace(key) && !normalized.ContainsKey(key))
                normalized[key] = pair.Value;
        }

        return _catalogImageUrlsByNormalizedKey = normalized;
    }

    private static string NormalizeCatalogKey(string value)
    {
        return string.Concat((value ?? "")
            .Where(char.IsLetterOrDigit))
            .ToLowerInvariant();
    }

    private static string GetOrCreateFallbackEffectImagePath(SignalRgbEffectInfo effect, string cacheDir)
    {
        try
        {
            Directory.CreateDirectory(cacheDir);
            string path = Path.Combine(cacheDir, $"{SanitizeFileName(effect.Id)}_fallback.png");
            if (File.Exists(path) && new FileInfo(path).Length > 0)
                return path;

            using var bitmap = new DrawingBitmap(256, 256);
            using var graphics = DrawingGraphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(DrawingColor.FromArgb(18, 20, 26));

            var (primary, secondary) = GetFallbackColors(effect.Name);
            using (var brush = new LinearGradientBrush(
                new DrawingRectangleF(0, 0, 256, 256),
                primary,
                secondary,
                135f))
            {
                graphics.FillRectangle(brush, 0, 0, 256, 256);
            }

            using (var vignette = new GraphicsPath())
            {
                vignette.AddEllipse(-80, -80, 416, 416);
                using var pathBrush = new PathGradientBrush(vignette)
                {
                    CenterColor = DrawingColor.FromArgb(15, 255, 255, 255),
                    SurroundColors = new[] { DrawingColor.FromArgb(170, 0, 0, 0) },
                };
                graphics.FillRectangle(pathBrush, 0, 0, 256, 256);
            }

            DrawFallbackEffectGlyph(graphics, effect.Name, primary);
            bitmap.Save(path, ImageFormat.Png);
            return path;
        }
        catch (Exception ex)
        {
            Logger.Log($"SignalRGB fallback image error ({effect.Name}): {ex.Message}");
            return "";
        }
    }

    private static (DrawingColor Primary, DrawingColor Secondary) GetFallbackColors(string name)
    {
        string normalized = NormalizeCatalogKey(name);
        if (normalized.Contains("solid")) return (DrawingColor.FromArgb(255, 128, 72), DrawingColor.FromArgb(72, 46, 36));
        if (normalized.Contains("screen")) return (DrawingColor.FromArgb(46, 194, 255), DrawingColor.FromArgb(128, 56, 255));
        if (normalized.Contains("rainbow")) return (DrawingColor.FromArgb(255, 64, 129), DrawingColor.FromArgb(41, 182, 246));
        if (normalized.Contains("side")) return (DrawingColor.FromArgb(0, 230, 118), DrawingColor.FromArgb(41, 182, 246));

        int hash = Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(name ?? ""));
        var primary = DrawingColorFromHsl(hash % 360, 0.78, 0.58);
        var secondary = DrawingColorFromHsl((hash / 7 + 130) % 360, 0.72, 0.28);
        return (primary, secondary);
    }

    private static void DrawFallbackEffectGlyph(DrawingGraphics graphics, string name, DrawingColor color)
    {
        string normalized = NormalizeCatalogKey(name);
        using var pen = new System.Drawing.Pen(DrawingColor.FromArgb(230, 255, 255, 255), 9)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        using var softPen = new System.Drawing.Pen(DrawingColor.FromArgb(100, color), 18)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        if (normalized.Contains("rainbow"))
        {
            for (int i = 0; i < 5; i++)
            {
                using var arcPen = new System.Drawing.Pen(DrawingColorFromHsl(i * 58, 0.9, 0.58), 11)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                };
                graphics.DrawArc(arcPen, 40 + i * 12, 62 + i * 12, 176 - i * 24, 176 - i * 24, 205, 210);
            }
            return;
        }

        if (normalized.Contains("screen"))
        {
            for (int y = 48; y <= 198; y += 24)
                graphics.DrawLine(softPen, 36, y, 220, y);
            graphics.DrawRectangle(pen, 52, 64, 152, 104);
            return;
        }

        if (normalized.Contains("side"))
        {
            graphics.DrawLine(softPen, 46, 128, 210, 128);
            graphics.DrawLine(pen, 52, 128, 204, 128);
            graphics.DrawLine(pen, 80, 92, 52, 128);
            graphics.DrawLine(pen, 80, 164, 52, 128);
            graphics.DrawLine(pen, 176, 92, 204, 128);
            graphics.DrawLine(pen, 176, 164, 204, 128);
            return;
        }

        if (normalized.Contains("solid"))
        {
            using var brush = new DrawingBrush(DrawingColor.FromArgb(230, 255, 255, 255));
            graphics.FillEllipse(brush, 68, 68, 120, 120);
            return;
        }

        using var font = new DrawingFont("Segoe UI", 76, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
        using var textBrush = new DrawingBrush(DrawingColor.FromArgb(235, 255, 255, 255));
        using var format = new DrawingStringFormat
        {
            Alignment = DrawingStringAlignment.Center,
            LineAlignment = DrawingStringAlignment.Center,
        };
        string initial = string.IsNullOrWhiteSpace(name) ? "S" : name.Trim()[0].ToString().ToUpperInvariant();
        graphics.DrawString(initial, font, textBrush, new DrawingRectangleF(0, 0, 256, 256), format);
    }

    private static DrawingColor DrawingColorFromHsl(int hue, double saturation, double lightness)
    {
        double c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double x = c * (1 - Math.Abs((hue / 60.0) % 2 - 1));
        double m = lightness - c / 2;

        (double r, double g, double b) = hue switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };

        return DrawingColor.FromArgb(
            (int)Math.Round((r + m) * 255),
            (int)Math.Round((g + m) * 255),
            (int)Math.Round((b + m) * 255));
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
