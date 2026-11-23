using System.Globalization;
using System.Text;

namespace PGPMint.Core.Search;

public static class FormattingHelpers
{
    private static readonly char[] SuperscriptDigits =
    [
        '\u2070', // 0
        '\u00B9', // 1
        '\u00B2', // 2
        '\u00B3', // 3
        '\u2074', // 4
        '\u2075', // 5
        '\u2076', // 6
        '\u2077', // 7
        '\u2078', // 8
        '\u2079'  // 9
    ];

    public static string ToSuperscript(int value)
    {
        string raw = value.ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            if (c == '-')
            {
                sb.Append('\u207B');
            }
            else if (c is >= '0' and <= '9')
            {
                sb.Append(SuperscriptDigits[c - '0']);
            }
        }

        return sb.ToString();
    }

    public static string FormatHashRateValue(double hashesPerSecond)
    {
        if (hashesPerSecond <= 0.0 || double.IsNaN(hashesPerSecond) || double.IsInfinity(hashesPerSecond))
        {
            return "0.00";
        }

        double ghs = hashesPerSecond / 1_000_000_000.0;
        if (ghs >= 100.0) return ghs.ToString("F1", CultureInfo.InvariantCulture);
        if (ghs >= 1.0) return ghs.ToString("F2", CultureInfo.InvariantCulture);
        if (ghs >= 0.01) return ghs.ToString("F3", CultureInfo.InvariantCulture);
        return (hashesPerSecond / 1_000_000.0).ToString("F1", CultureInfo.InvariantCulture);
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
        if (hashesPerSecond <= 0.0 || double.IsNaN(hashesPerSecond) || double.IsInfinity(hashesPerSecond))
        {
            return "0.00 GH/s";
        }

        if (hashesPerSecond < 10_000_000.0)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:F1} MH/s", hashesPerSecond / 1_000_000.0);
        }

        double ghs = hashesPerSecond / 1_000_000_000.0;
        return ghs >= 100.0
            ? string.Format(CultureInfo.InvariantCulture, "{0:F1} GH/s", ghs)
            : string.Format(CultureInfo.InvariantCulture, "{0:F2} GH/s", ghs);
    }

    public static string FormatCheckedCount(ulong count)
    {
        if (count < 10_000UL)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:N0} H", count);
        }

        int exp = (int)Math.Floor(Math.Log10(count));
        double mantissa = count / Math.Pow(10.0, exp);
        if (Math.Round(mantissa, 2, MidpointRounding.AwayFromZero) >= 10.0)
        {
            exp++;
            mantissa = 1.0;
        }

        return string.Format(CultureInfo.InvariantCulture, "{0:F2} \u00D7 10{1} H", mantissa, ToSuperscript(exp));
    }

    public static string FormatCheckedCountExact(ulong count) =>
        string.Format(CultureInfo.InvariantCulture, "{0:N0} H", count);

    public static string FormatProbability(double probability)
    {
        if (probability <= 0.0 || double.IsNaN(probability))
        {
            return "0.00%";
        }

        double pct = Math.Clamp(probability, 0.0, 1.0) * 100.0;
        if (pct >= 99.995)
        {
            return "99.99%";
        }

        if (pct >= 10.0)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:F2}%", pct);
        }

        if (pct >= 0.01)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:F3}%", pct);
        }

        if (pct >= 0.0001)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:F4}%", pct);
        }

        int exp = (int)Math.Floor(Math.Log10(pct));
        double mantissa = exp < -300
            ? (pct * 1e20) / Math.Pow(10.0, exp + 20)
            : pct / Math.Pow(10.0, exp);
        if (Math.Round(mantissa, 2, MidpointRounding.AwayFromZero) >= 10.0)
        {
            exp++;
            mantissa = 1.0;
        }

        return string.Format(CultureInfo.InvariantCulture, "{0:F2}\u00D710{1}%", mantissa, ToSuperscript(exp));
    }

    public static string FormatLabeledProbability(double probability, string labelPrefix)
    {
        string value = FormatProbability(probability);
        return string.IsNullOrWhiteSpace(labelPrefix) ? value : $"{labelPrefix} {value}";
    }

    public static string FormatSingleHashProbabilityDetail(int fixedHexLength)
    {
        int k = Math.Clamp(fixedHexLength, 1, KeyIdPattern.KeyIdHexLength);
        double singleProb = Math.Pow(16.0, -k);

        int exp = (int)Math.Floor(Math.Log10(singleProb));
        double mantissa = singleProb / Math.Pow(10.0, exp);
        if (Math.Round(mantissa, 2, MidpointRounding.AwayFromZero) >= 10.0)
        {
            exp++;
            mantissa = 1.0;
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "1 / 16{0} (\u2248 {1:F2} \u00D7 10{2})",
            ToSuperscript(k),
            mantissa,
            ToSuperscript(exp));
    }

    public static string FormatEta(TimeSpan? eta, bool isChinese)
    {
        if (eta is null)
        {
            return "\u2014";
        }

        double seconds = eta.Value.TotalSeconds;
        if (seconds < 0.1) return isChinese ? "<0.1 秒" : "<0.1s";
        if (seconds < 60.0) return string.Format(CultureInfo.InvariantCulture, isChinese ? "~{0:F1} 秒" : "~{0:F1}s", seconds);

        double minutes = seconds / 60.0;
        if (minutes < 60.0) return string.Format(CultureInfo.InvariantCulture, isChinese ? "~{0:F1} 分" : "~{0:F1}m", minutes);

        double hours = minutes / 60.0;
        if (hours < 24.0) return string.Format(CultureInfo.InvariantCulture, isChinese ? "~{0:F1} 小時" : "~{0:F1}h", hours);

        double days = hours / 24.0;
        if (days < 365.25) return string.Format(CultureInfo.InvariantCulture, isChinese ? "~{0:F1} 天" : "~{0:F1}d", days);

        double years = days / 365.25;
        if (years < 1_000.0) return string.Format(CultureInfo.InvariantCulture, isChinese ? "~{0:F1} 年" : "~{0:F1}y", years);
        if (years < 1_000_000.0) return string.Format(CultureInfo.InvariantCulture, isChinese ? "~{0:F1} 千年" : "~{0:F1}ky", years / 1_000.0);
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
