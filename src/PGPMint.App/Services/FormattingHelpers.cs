namespace PGPMint.App.Services;

public static class FormattingHelpers
{
    public static string FormatHashRateValue(double hashesPerSecond)
    {
        if (hashesPerSecond <= 0.0 || double.IsNaN(hashesPerSecond) || double.IsInfinity(hashesPerSecond))
        {
            return "0.00";
        }

        double ghs = hashesPerSecond / 1_000_000_000.0;
        if (ghs >= 100.0) return ghs.ToString("F1");
        if (ghs >= 1.0) return ghs.ToString("F2");
        if (ghs >= 0.01) return ghs.ToString("F3");
        return (hashesPerSecond / 1_000_000.0).ToString("F1");
    }

    public static string FormatHashRateUnit(double hashesPerSecond)
    {
        if (hashesPerSecond > 0.0 && hashesPerSecond < 10_000_000.0)
        {
            return "MH/s";
        }

        return "GH/s";
    }

    public static string FormatHashRateCompact(double hashesPerSecond)
    {
        if (hashesPerSecond <= 0.0)
        {
            return "0.00 GH/s";
        }

        if (hashesPerSecond < 10_000_000.0)
        {
            return $"{hashesPerSecond / 1_000_000.0:F1} MH/s";
        }

        double ghs = hashesPerSecond / 1_000_000_000.0;
        return ghs >= 100.0 ? $"{ghs:F1} GH/s" : $"{ghs:F2} GH/s";
    }

    public static string FormatCheckedCount(ulong count)
    {
        if (count >= 1_000_000_000_000_000UL) return $"{count / 1e15:F2} PH";
        if (count >= 1_000_000_000_000UL) return $"{count / 1e12:F2} TH";
        if (count >= 1_000_000_000UL) return $"{count / 1e9:F2} GH";
        if (count >= 1_000_000UL) return $"{count / 1e6:F2} MH";
        if (count >= 1_000UL) return $"{count / 1e3:F1} KH";
        return $"{count} H";
    }

    public static string FormatProbability(double probability)
    {
        if (probability <= 0.0) return "0.000%";
        if (probability >= 0.99999) return "99.999%";
        if (probability < 0.00001) return $"{probability * 100.0:F5}%";
        return $"{probability * 100.0:F3}%";
    }

    public static string FormatEta(TimeSpan? eta, bool isChinese)
    {
        if (eta is null)
        {
            return "\u2014";
        }

        double seconds = eta.Value.TotalSeconds;
        if (seconds < 0.1) return isChinese ? "<0.1 秒" : "<0.1s";
        if (seconds < 60.0) return isChinese ? $"~{seconds:F1} 秒" : $"~{seconds:F1}s";

        double minutes = seconds / 60.0;
        if (minutes < 60.0) return isChinese ? $"~{minutes:F1} 分" : $"~{minutes:F1}m";

        double hours = minutes / 60.0;
        if (hours < 24.0) return isChinese ? $"~{hours:F1} 小時" : $"~{hours:F1}h";

        double days = hours / 24.0;
        if (days < 365.25) return isChinese ? $"~{days:F1} 天" : $"~{days:F1}d";

        double years = days / 365.25;
        if (years < 1_000.0) return isChinese ? $"~{years:F1} 年" : $"~{years:F1}y";
        if (years < 1_000_000.0) return isChinese ? $"~{years / 1_000.0:F1} 千年" : $"~{years / 1_000.0:F1}ky";
        return isChinese ? ">100 萬年" : ">1My";
    }

    public static string FormatKeyIdSpaced(string keyId16)
    {
        if (string.IsNullOrEmpty(keyId16) || keyId16.Length != 16)
        {
            return keyId16 ?? string.Empty;
        }

        return $"{keyId16[..4]} {keyId16[4..8]} {keyId16[8..12]} {keyId16[12..16]}";
    }
}
