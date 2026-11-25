using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace PGPMint.Desktop;

public sealed class LocalizationService
{
    private readonly Dictionary<string, Dictionary<string, string>> _bundles = new(StringComparer.OrdinalIgnoreCase);
    public string Language { get; private set; } = "auto";
    public string ActiveCode { get; private set; } = "en-US";
    public IReadOnlyList<string> Languages => ["auto", .. _bundles.Keys.Order()];

    public LocalizationService()
    {
        var assembly = Assembly.GetExecutingAssembly();
        foreach (string name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".json", StringComparison.Ordinal)).Order())
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var doc = JsonDocument.Parse(stream);
            string code = doc.RootElement.GetProperty("_meta").GetProperty("code").GetString()!;
            if (!_bundles.TryGetValue(code, out var bundle)) _bundles[code] = bundle = new(StringComparer.Ordinal);
            foreach (var item in doc.RootElement.GetProperty("strings").EnumerateObject()) bundle[item.Name] = item.Value.GetString()!;
        }
        Apply("auto");
    }

    public void Apply(string language)
    {
        Language = language == "auto" || _bundles.ContainsKey(language) ? language : "auto";
        string locale = Language == "auto" ? CultureInfo.CurrentUICulture.Name : Language;
        ActiveCode = _bundles.ContainsKey(locale) ? locale
            : locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-Hant" : "en-US";
    }

    public string this[string key] => _bundles.GetValueOrDefault(ActiveCode)?.GetValueOrDefault(key)
        ?? _bundles.GetValueOrDefault("en-US")?.GetValueOrDefault(key) ?? key;
}
