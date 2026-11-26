namespace PGPMint.Core.Search;

public static class LocaleResolver
{
    public const string DefaultFallbackCode = "en-US";

    public static readonly IReadOnlyList<string> BuiltInLocaleOrder =
    [
        "en-US",
        "zh-Hant",
        "zh-Hans",
        "ja-JP",
        "ko-KR",
        "de-DE",
        "fr-FR",
        "es-ES",
        "pt-BR",
        "ru-RU"
    ];

    public static int GetSortRank(string code)
    {
        for (int i = 0; i < BuiltInLocaleOrder.Count; i++)
        {
            if (string.Equals(BuiltInLocaleOrder[i], code, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    public static string ResolveSystemLocale(
        string? cultureName,
        IReadOnlyCollection<string> availableCodes,
        string fallback = DefaultFallbackCode)
    {
        if (availableCodes.Count == 0)
        {
            return fallback;
        }

        var canonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string code in availableCodes)
        {
            if (!string.IsNullOrWhiteSpace(code))
            {
                canonical.TryAdd(code, code);
            }
        }

        if (canonical.Count == 0)
        {
            return fallback;
        }

        string normalized = string.IsNullOrWhiteSpace(cultureName)
            ? string.Empty
            : cultureName.Trim().Replace('_', '-');

        if (!string.IsNullOrEmpty(normalized))
        {
            if (canonical.TryGetValue(normalized, out string? exact))
            {
                return exact;
            }

            if (normalized.Equals("zh", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("zh-", StringComparison.OrdinalIgnoreCase))
            {
                bool preferTraditional =
                    normalized.Equals("zh-TW", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Equals("zh-HK", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Equals("zh-MO", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Equals("zh-Hant", StringComparison.OrdinalIgnoreCase) ||
                    normalized.StartsWith("zh-Hant-", StringComparison.OrdinalIgnoreCase) ||
                    normalized.StartsWith("zh-TW-", StringComparison.OrdinalIgnoreCase) ||
                    normalized.StartsWith("zh-HK-", StringComparison.OrdinalIgnoreCase) ||
                    normalized.StartsWith("zh-MO-", StringComparison.OrdinalIgnoreCase);

                if (preferTraditional)
                {
                    if (canonical.TryGetValue("zh-Hant", out string? zhHant)) return zhHant;
                    if (canonical.TryGetValue("zh-Hans", out string? zhHansFallback)) return zhHansFallback;
                }
                else
                {
                    if (canonical.TryGetValue("zh-Hans", out string? zhHans)) return zhHans;
                    if (canonical.TryGetValue("zh-Hant", out string? zhHantFallback)) return zhHantFallback;
                }
            }

            string subtag = normalized.Split('-')[0];
            if (!string.IsNullOrEmpty(subtag))
            {
                foreach (string builtIn in BuiltInLocaleOrder)
                {
                    if (canonical.TryGetValue(builtIn, out string? match) &&
                        (match.Equals(subtag, StringComparison.OrdinalIgnoreCase) ||
                         match.StartsWith(subtag + "-", StringComparison.OrdinalIgnoreCase)))
                    {
                        return match;
                    }
                }

                foreach (string code in canonical.Values.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
                {
                    if (code.Equals(subtag, StringComparison.OrdinalIgnoreCase) ||
                        code.StartsWith(subtag + "-", StringComparison.OrdinalIgnoreCase))
                    {
                        return code;
                    }
                }
            }
        }

        if (canonical.TryGetValue(fallback, out string? fb))
        {
            return fb;
        }

        return canonical.Values
            .OrderBy(GetSortRank)
            .ThenBy(c => c, StringComparer.OrdinalIgnoreCase)
            .First();
    }
}
