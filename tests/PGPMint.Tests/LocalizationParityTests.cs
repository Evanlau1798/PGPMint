using System.Text.Json;
using System.Text.RegularExpressions;
using PGPMint.Core.Search;
using Xunit;

namespace PGPMint.Tests;

public sealed class LocalizationParityTests
{
    private static readonly Regex EmojiOrDingbatRegex = new(
        @"[\uD83C-\uDBFF][\uDC00-\uDFFF]|[\u2600-\u27BF]",
        RegexOptions.Compiled);

    private static readonly Regex PlaceholderRegex = new(@"\{(\d+)\}", RegexOptions.Compiled);

    private static readonly (string Code, string NativeName)[] ExpectedLocales =
    [
        ("en-US", "English"),
        ("zh-Hant", "繁體中文"),
        ("zh-Hans", "简体中文"),
        ("ja-JP", "日本語"),
        ("ko-KR", "한국어"),
        ("de-DE", "Deutsch"),
        ("fr-FR", "Français"),
        ("es-ES", "Español"),
        ("pt-BR", "Português (Brasil)"),
        ("ru-RU", "Русский")
    ];

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PGPMint.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate PGPMint repository root.");
    }

    private static Dictionary<string, string> ReadStrings(string jsonPath, out string? code, out string? nativeName)
    {
        string raw = File.ReadAllText(jsonPath);
        Assert.DoesNotMatch(EmojiOrDingbatRegex, raw);

        using JsonDocument doc = JsonDocument.Parse(raw);
        JsonElement meta = doc.RootElement.GetProperty("_meta");
        code = meta.GetProperty("code").GetString();
        nativeName = meta.TryGetProperty("nativeName", out JsonElement nativeProp)
            ? nativeProp.GetString()
            : null;

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty prop in doc.RootElement.GetProperty("strings").EnumerateObject())
        {
            result[prop.Name] = prop.Value.GetString() ?? string.Empty;
        }

        return result;
    }

    private static string[] ExtractPlaceholders(string text) =>
        PlaceholderRegex.Matches(text).Select(m => m.Value).OrderBy(v => v, StringComparer.Ordinal).ToArray();

    [Fact]
    public void BaseDesktopAndMacOsCatalogsHaveExactParityAndSync()
    {
        string root = FindRepoRoot();
        string appStringsDir = Path.Combine(root, "src", "PGPMint.App", "Strings");
        string desktopStringsDir = Path.Combine(root, "src", "PGPMint.Desktop", "Strings");
        string macResourcesDir = Path.Combine(root, "src", "PGPMint.MacApp", "Sources", "PGPMintMacCore", "Resources");

        Dictionary<string, string> baseRef = ReadStrings(Path.Combine(appStringsDir, "en-US.json"), out _, out _);
        Dictionary<string, string> desktopRef = ReadStrings(Path.Combine(desktopStringsDir, "Desktop.en-US.json"), out _, out _);

        Assert.Equal(81, baseRef.Count);
        Assert.Equal(30, desktopRef.Count);

        foreach ((string code, string expectedNativeName) in ExpectedLocales)
        {
            string basePath = Path.Combine(appStringsDir, $"{code}.json");
            Assert.True(File.Exists(basePath), $"Missing base locale file: {basePath}");
            Dictionary<string, string> baseDict = ReadStrings(basePath, out string? metaCode, out string? nativeName);
            Assert.Equal(code, metaCode);
            Assert.Equal(expectedNativeName, nativeName);
            Assert.Equal(baseRef.Keys.OrderBy(k => k, StringComparer.Ordinal), baseDict.Keys.OrderBy(k => k, StringComparer.Ordinal));

            foreach (string key in baseRef.Keys)
            {
                Assert.False(string.IsNullOrWhiteSpace(baseDict[key]), $"Empty string for {code}.{key}");
                Assert.Equal(ExtractPlaceholders(baseRef[key]), ExtractPlaceholders(baseDict[key]));
            }

            string desktopPath = Path.Combine(desktopStringsDir, $"Desktop.{code}.json");
            Assert.True(File.Exists(desktopPath), $"Missing desktop overlay file: {desktopPath}");
            Dictionary<string, string> desktopDict = ReadStrings(desktopPath, out string? desktopCode, out _);
            Assert.Equal(code, desktopCode);
            Assert.Equal(desktopRef.Keys.OrderBy(k => k, StringComparer.Ordinal), desktopDict.Keys.OrderBy(k => k, StringComparer.Ordinal));

            foreach (string key in desktopRef.Keys)
            {
                Assert.False(string.IsNullOrWhiteSpace(desktopDict[key]), $"Empty string for Desktop.{code}.{key}");
                Assert.Equal(ExtractPlaceholders(desktopRef[key]), ExtractPlaceholders(desktopDict[key]));
            }

            string macBasePath = Path.Combine(macResourcesDir, $"{code}.json");
            string macDesktopPath = Path.Combine(macResourcesDir, $"Desktop.{code}.json");
            Assert.True(File.Exists(macBasePath), $"Missing macOS resource file: {macBasePath}");
            Assert.True(File.Exists(macDesktopPath), $"Missing macOS desktop resource file: {macDesktopPath}");
            Assert.Equal(File.ReadAllBytes(basePath), File.ReadAllBytes(macBasePath));
            Assert.Equal(File.ReadAllBytes(desktopPath), File.ReadAllBytes(macDesktopPath));
        }
    }

    [Theory]
    [InlineData("en-US", "en-US")]
    [InlineData("en-GB", "en-US")]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("zh-HK", "zh-Hant")]
    [InlineData("zh-MO", "zh-Hant")]
    [InlineData("zh-Hant", "zh-Hant")]
    [InlineData("zh-Hant-TW", "zh-Hant")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-SG", "zh-Hans")]
    [InlineData("zh-Hans", "zh-Hans")]
    [InlineData("zh-Hans-CN", "zh-Hans")]
    [InlineData("zh", "zh-Hans")]
    [InlineData("ja-JP", "ja-JP")]
    [InlineData("ja", "ja-JP")]
    [InlineData("ko-KR", "ko-KR")]
    [InlineData("ko", "ko-KR")]
    [InlineData("de-DE", "de-DE")]
    [InlineData("de-AT", "de-DE")]
    [InlineData("fr-FR", "fr-FR")]
    [InlineData("fr-CA", "fr-FR")]
    [InlineData("es-ES", "es-ES")]
    [InlineData("es-419", "es-ES")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("pt-PT", "pt-BR")]
    [InlineData("ru-RU", "ru-RU")]
    [InlineData("ru-BY", "ru-RU")]
    [InlineData("sv-SE", "en-US")]
    [InlineData("", "en-US")]
    public void LocaleResolverSelectsExpectedBuiltInLocale(string cultureName, string expected)
    {
        string resolved = LocaleResolver.ResolveSystemLocale(cultureName, LocaleResolver.BuiltInLocaleOrder);
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void FormatEtaSupportsAllBuiltInLocalesAndThresholds()
    {
        string root = FindRepoRoot();
        string appStringsDir = Path.Combine(root, "src", "PGPMint.App", "Strings");

        foreach ((string code, _) in ExpectedLocales)
        {
            Dictionary<string, string> dict = ReadStrings(Path.Combine(appStringsDir, $"{code}.json"), out _, out _);
            var formats = new EtaFormatStrings(
                dict["EtaLessThanTenthSec"],
                dict["EtaSecondsFormat"],
                dict["EtaMinutesFormat"],
                dict["EtaHoursFormat"],
                dict["EtaDaysFormat"],
                dict["EtaYearsFormat"],
                dict["EtaKiloYearsFormat"],
                dict["EtaOverMillionYears"]);

            Assert.Equal("\u2014", FormattingHelpers.FormatEta(null, formats));
            Assert.Equal(dict["EtaLessThanTenthSec"], FormattingHelpers.FormatEta(TimeSpan.FromSeconds(0.05), formats));
            Assert.Contains("42.3", FormattingHelpers.FormatEta(TimeSpan.FromSeconds(42.3), formats));
            Assert.Contains("15.0", FormattingHelpers.FormatEta(TimeSpan.FromMinutes(15), formats));
            Assert.Contains("5.5", FormattingHelpers.FormatEta(TimeSpan.FromHours(5.5), formats));
            Assert.Contains("12.0", FormattingHelpers.FormatEta(TimeSpan.FromDays(12), formats));
            Assert.Contains("3.2", FormattingHelpers.FormatEta(TimeSpan.FromDays(3.2 * 365.25), formats));
            Assert.Contains("12.5", FormattingHelpers.FormatEta(TimeSpan.FromDays(12_500 * 365.25), formats));
            Assert.False(string.IsNullOrWhiteSpace(dict["EtaOverMillionYears"]));
            Assert.False(string.IsNullOrWhiteSpace(FormattingHelpers.FormatEta(TimeSpan.MaxValue, formats)));
        }

        Assert.Equal("<0.1s", FormattingHelpers.FormatEta(TimeSpan.FromSeconds(0.05), isChinese: false));
        Assert.Equal("<0.1 \u79D2", FormattingHelpers.FormatEta(TimeSpan.FromSeconds(0.05), isChinese: true));
    }
}
