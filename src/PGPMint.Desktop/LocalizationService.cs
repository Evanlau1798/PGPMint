using System.Globalization;
using System.Reflection;
using System.Text.Json;
using PGPMint.Core.Search;

namespace PGPMint.Desktop;

public sealed record LanguageOption(string Code, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed class LocalizationService
{
    private readonly Dictionary<string, Dictionary<string, string>> _bundles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _nativeNames = new(StringComparer.OrdinalIgnoreCase);

    public string Language { get; private set; } = "auto";
    public string ActiveCode { get; private set; } = LocaleResolver.DefaultFallbackCode;

    public IReadOnlyList<string> Languages =>
        ["auto", .. _bundles.Keys.OrderBy(LocaleResolver.GetSortRank).ThenBy(k => k, StringComparer.OrdinalIgnoreCase)];

    public IReadOnlyList<LanguageOption> LanguageOptions =>
        [
            new("auto", this["LanguageAuto"]),
            .. _bundles.Keys
                .OrderBy(LocaleResolver.GetSortRank)
                .ThenBy(k => k, StringComparer.OrdinalIgnoreCase)
                .Select(k => new LanguageOption(k, _nativeNames.GetValueOrDefault(k, k)))
        ];

    public EtaFormatStrings EtaFormats => new(
        LessThanTenthSec: this["EtaLessThanTenthSec"],
        SecondsFormat: this["EtaSecondsFormat"],
        MinutesFormat: this["EtaMinutesFormat"],
        HoursFormat: this["EtaHoursFormat"],
        DaysFormat: this["EtaDaysFormat"],
        YearsFormat: this["EtaYearsFormat"],
        KiloYearsFormat: this["EtaKiloYearsFormat"],
        OverMillionYears: this["EtaOverMillionYears"]);

    public LocalizationService()
    {
        var assembly = Assembly.GetExecutingAssembly();
        foreach (string name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".json", StringComparison.Ordinal)).Order())
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var doc = JsonDocument.Parse(stream);
            JsonElement meta = doc.RootElement.GetProperty("_meta");
            string code = meta.GetProperty("code").GetString()!;
            if (meta.TryGetProperty("nativeName", out JsonElement nativeProp) &&
                nativeProp.ValueKind == JsonValueKind.String &&
                nativeProp.GetString() is { Length: > 0 } nativeName)
            {
                _nativeNames[code] = nativeName;
            }
            if (!_bundles.TryGetValue(code, out var bundle)) _bundles[code] = bundle = new(StringComparer.Ordinal);
            foreach (var item in doc.RootElement.GetProperty("strings").EnumerateObject()) bundle[item.Name] = item.Value.GetString()!;
        }
        Apply("auto");
    }

    public void Apply(string language)
    {
        string normalized = string.IsNullOrWhiteSpace(language) ? "auto" : language.Trim();
        string? matchedKey = _bundles.Keys.FirstOrDefault(k => string.Equals(k, normalized, StringComparison.OrdinalIgnoreCase));
        Language = matchedKey ?? "auto";
        ActiveCode = Language == "auto"
            ? LocaleResolver.ResolveSystemLocale(CultureInfo.CurrentUICulture.Name, _bundles.Keys, LocaleResolver.DefaultFallbackCode)
            : Language;
    }

    public string this[string key] => _bundles.GetValueOrDefault(ActiveCode)?.GetValueOrDefault(key)
        ?? _bundles.GetValueOrDefault(LocaleResolver.DefaultFallbackCode)?.GetValueOrDefault(key) ?? key;
}
