using System.Globalization;
using System.Reflection;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using PGPMint.Core.Search;

namespace PGPMint.App.Services;

public sealed partial class LanguageOption : ObservableObject
{
    public string Code { get; }

    [ObservableProperty]
    private string _displayName;

    public LanguageOption(string code, string displayName)
    {
        Code = code;
        _displayName = displayName;
    }
}

public sealed class LocalizationService : ObservableObject
{
    public const string AutoLanguageCode = "auto";
    private const string DefaultFallbackCode = "en-US";

    private sealed class LocaleBundle
    {
        public string Code { get; init; } = string.Empty;
        public string NativeName { get; set; } = string.Empty;
        public Dictionary<string, string> Strings { get; init; } = new(StringComparer.Ordinal);
    }

    private sealed class LocaleJsonSchema
    {
        public LocaleMetaSchema? _meta { get; set; }
        public Dictionary<string, string>? strings { get; set; }
    }

    private sealed class LocaleMetaSchema
    {
        public string? code { get; set; }
        public string? nativeName { get; set; }
    }

    private readonly Dictionary<string, LocaleBundle> _bundles = new(StringComparer.OrdinalIgnoreCase);
    private string _activeCode = DefaultFallbackCode;
    private string _settingValue = AutoLanguageCode;

    public string SettingValue => _settingValue;
    public string ActiveLanguageCode => _activeCode;
    public bool IsChinese => _activeCode.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    public LocalizationService(string? externalStringsDirectory = null)
    {
        LoadEmbeddedBundles();
        LoadExternalBundles(externalStringsDirectory ?? Path.Combine(AppContext.BaseDirectory, "Strings"));
        ApplySetting(AutoLanguageCode);
    }

    public IReadOnlyList<LanguageOption> GetLanguageOptions()
    {
        var list = new List<LanguageOption>
        {
            new(AutoLanguageCode, Get("LanguageAuto"))
        };

        foreach (LocaleBundle bundle in _bundles.Values.OrderBy(b => b.Code, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(new LanguageOption(bundle.Code, bundle.NativeName));
        }

        return list;
    }

    public void ApplySetting(string? setting)
    {
        string normalized = string.IsNullOrWhiteSpace(setting) ? AutoLanguageCode : setting.Trim();
        if (!string.Equals(normalized, AutoLanguageCode, StringComparison.OrdinalIgnoreCase) &&
            !_bundles.ContainsKey(normalized))
        {
            normalized = AutoLanguageCode;
        }

        bool isAuto = string.Equals(normalized, AutoLanguageCode, StringComparison.OrdinalIgnoreCase);
        _settingValue = isAuto ? AutoLanguageCode : _bundles[normalized].Code;
        _activeCode = isAuto ? ResolveSystemLocale() : _bundles[normalized].Code;

        OnPropertyChanged(string.Empty);
    }

    private string ResolveSystemLocale()
    {
        CultureInfo ui = CultureInfo.CurrentUICulture;
        if (_bundles.TryGetValue(ui.Name, out LocaleBundle? exact))
        {
            return exact.Code;
        }

        if (ui.Name.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase) ||
            ui.Name.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase) ||
            ui.Name.StartsWith("zh-MO", StringComparison.OrdinalIgnoreCase) ||
            ui.Name.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase))
        {
            if (_bundles.TryGetValue("zh-Hant", out LocaleBundle? zhHant))
            {
                return zhHant.Code;
            }
        }

        string twoLetter = ui.TwoLetterISOLanguageName;
        LocaleBundle? prefixMatch = _bundles.Values.FirstOrDefault(b =>
            b.Code.StartsWith(twoLetter + "-", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(b.Code, twoLetter, StringComparison.OrdinalIgnoreCase));

        if (prefixMatch is not null)
        {
            return prefixMatch.Code;
        }

        return _bundles.ContainsKey(DefaultFallbackCode)
            ? DefaultFallbackCode
            : _bundles.Keys.FirstOrDefault() ?? DefaultFallbackCode;
    }

    private void LoadEmbeddedBundles()
    {
        Assembly asm = typeof(LocalizationService).Assembly;
        foreach (string resName in asm.GetManifestResourceNames())
        {
            if (!resName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                !resName.Contains(".Strings.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using Stream? stream = asm.GetManifestResourceStream(resName);
                if (stream is null) continue;
                using var reader = new StreamReader(stream);
                RegisterBundleFromJson(reader.ReadToEnd());
            }
            catch
            {
                // Ignore malformed embedded bundle
            }
        }
    }

    private void LoadExternalBundles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            foreach (string file in Directory.GetFiles(directory, "*.json"))
            {
                try
                {
                    RegisterBundleFromJson(File.ReadAllText(file));
                }
                catch
                {
                    // Skip malformed external locale file without crashing app
                }
            }
        }
        catch
        {
            // Ignore filesystem enumeration errors
        }
    }

    private void RegisterBundleFromJson(string json)
    {
        LocaleJsonSchema? parsed = JsonSerializer.Deserialize<LocaleJsonSchema>(json);
        if (parsed?._meta is null ||
            string.IsNullOrWhiteSpace(parsed._meta.code) ||
            parsed.strings is null)
        {
            return;
        }

        string code = parsed._meta.code.Trim();
        if (string.Equals(code, AutoLanguageCode, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string nativeName = string.IsNullOrWhiteSpace(parsed._meta.nativeName)
            ? code
            : parsed._meta.nativeName.Trim();

        if (_bundles.TryGetValue(code, out LocaleBundle? existing))
        {
            existing.NativeName = nativeName;
            foreach (KeyValuePair<string, string> kv in parsed.strings)
            {
                if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                {
                    existing.Strings[kv.Key] = kv.Value;
                }
            }
            return;
        }

        var cleanStrings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> kv in parsed.strings)
        {
            if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value))
            {
                cleanStrings[kv.Key] = kv.Value;
            }
        }

        _bundles[code] = new LocaleBundle
        {
            Code = code,
            NativeName = nativeName,
            Strings = cleanStrings
        };
    }

    public string Get(string key)
    {
        if (_bundles.TryGetValue(_activeCode, out LocaleBundle? active) &&
            active.Strings.TryGetValue(key, out string? value) &&
            !string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (_bundles.TryGetValue(DefaultFallbackCode, out LocaleBundle? fallback) &&
            fallback.Strings.TryGetValue(key, out string? fbValue) &&
            !string.IsNullOrEmpty(fbValue))
        {
            return fbValue;
        }

        return key;
    }

    private string SafeFormat(string key, params object[] args)
    {
        string template = Get(key);
        try
        {
            return string.Format(CultureInfo.InvariantCulture, template, args);
        }
        catch (FormatException)
        {
            if (_bundles.TryGetValue(DefaultFallbackCode, out LocaleBundle? fallback) &&
                fallback.Strings.TryGetValue(key, out string? fbTemplate) &&
                !string.IsNullOrEmpty(fbTemplate))
            {
                try
                {
                    return string.Format(CultureInfo.InvariantCulture, fbTemplate, args);
                }
                catch (FormatException)
                {
                    // Fall through to raw template
                }
            }

            return template;
        }
    }

    public string GetKeyIdValidationMessage(KeyIdValidationError error) => error switch
    {
        KeyIdValidationError.InvalidCharacters => Get("ValidationInvalidChars"),
        KeyIdValidationError.TooLong => Get("ValidationTooLong"),
        _ => string.Empty
    };

    public string FormatMeasuringSpeed(int remainingSeconds) =>
        SafeFormat("MeasuringSpeedFormat", Math.Max(0, remainingSeconds));

    public string FormatHudMeasuring(int remainingSeconds) =>
        SafeFormat("HudMeasuringFormat", Math.Max(0, remainingSeconds));

    public string FormatClearAllConfirm(int count) =>
        SafeFormat("ClearAllConfirmFormat", count);

    public string SuffixRuleHint => Get("SuffixRuleHint");
    public string TargetPlaceholder => Get("TargetPlaceholder");
    public string SetupTitle => Get("SetupTitle");
    public string Finish => Get("Finish");
    public string Apply => Get("Apply");
    public string Applied => Get("Applied");

    public string Settings => Get("Settings");
    public string SectionLanguage => Get("SectionLanguage");
    public string SectionTarget => Get("SectionTarget");
    public string SectionPreferences => Get("SectionPreferences");
    public string SectionGpus => Get("SectionGpus");
    public string StopToEditHint => Get("StopToEditHint");

    public string Start => Get("Start");
    public string Pause => Get("Pause");
    public string Resume => Get("Resume");
    public string Stop => Get("Stop");
    public string FullSpeed => Get("FullSpeed");
    public string Paused => Get("Paused");
    public string Idle => Get("Idle");
    public string Stopping => Get("Stopping");
    public string Error => Get("Error");

    public string Copy => Get("Copy");
    public string Copied => Get("Copied");
    public string Delete => Get("Delete");
    public string DeleteKeyTooltip => Get("DeleteKeyTooltip");
    public string DeleteConfirmTitle => Get("DeleteConfirmTitle");
    public string ClearAll => Get("ClearAll");
    public string ClearAllConfirmTitle => Get("ClearAllConfirmTitle");
    public string Open => Get("Open");
    public string Quit => Get("Quit");

    public string ExportTitle => Get("ExportTitle");
    public string NamePlaceholder => Get("NamePlaceholder");
    public string EmailPlaceholder => Get("EmailPlaceholder");
    public string PassphrasePlaceholder => Get("PassphrasePlaceholder");
    public string Save => Get("Save");
    public string Cancel => Get("Cancel");

    public string SettingLanguage => Get("SettingLanguage");
    public string SettingAutoPauseOnHit => Get("SettingAutoPauseOnHit");
    public string SettingMinimizeToTray => Get("SettingMinimizeToTray");
    public string SettingShowMiniHud => Get("SettingShowMiniHud");
    public string SettingStartWithWindows => Get("SettingStartWithWindows");
    public string TrayMiniHud => Get("TrayMiniHud");

    public string NoGpuError => Get("NoGpuError");
    public string HitNotificationTitle => Get("HitNotificationTitle");
    public string ExportSuccess => Get("ExportSuccess");
}
