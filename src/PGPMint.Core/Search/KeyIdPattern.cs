using System.Text;

namespace PGPMint.Core.Search;

public enum KeyIdValidationError
{
    None,
    Empty,
    InvalidCharacters,
    TooLong
}

public sealed record KeyIdPattern(
    string Suffix,
    string CanonicalMask,
    string FormattedDisplay,
    int FixedHexLength,
    ulong Mask,
    ulong Target)
{
    public const int KeyIdHexLength = 16;

    public uint MaskLow => (uint)(Mask & 0xFFFFFFFFul);
    public uint TargetLow => (uint)(Target & 0xFFFFFFFFul);
    public uint MaskHigh => (uint)(Mask >> 32);
    public uint TargetHigh => (uint)(Target >> 32);
    public double HitProbabilityPerCandidate => Math.Pow(16.0, -FixedHexLength);

    public bool Matches(ulong keyId) => (keyId & Mask) == Target;

    public bool MatchesKeyIdHex(string? keyIdHex)
    {
        if (string.IsNullOrWhiteSpace(keyIdHex))
        {
            return false;
        }

        ReadOnlySpan<char> trimmed = keyIdHex.AsSpan().Trim();
        if (trimmed.Length != KeyIdHexLength)
        {
            return false;
        }

        return ulong.TryParse(
            trimmed,
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out ulong keyIdValue) && Matches(keyIdValue);
    }

    public double ComputeCumulativeProbability(ulong checkedCandidates)
    {
        if (checkedCandidates == 0)
        {
            return 0.0;
        }

        double q = HitProbabilityPerCandidate;
        double log1mQ = Log1p(-q);
        double exponent = checkedCandidates * log1mQ;
        return Math.Clamp(-Expm1(exponent), 0.0, 1.0);
    }

    public TimeSpan? EstimateMeanWait(double hashesPerSecond)
    {
        if (hashesPerSecond <= 0.0 || double.IsNaN(hashesPerSecond) || double.IsInfinity(hashesPerSecond))
        {
            return null;
        }

        double seconds = Math.Pow(16.0, FixedHexLength) / hashesPerSecond;
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
        {
            return TimeSpan.MaxValue;
        }

        double maxSeconds = TimeSpan.MaxValue.TotalSeconds - 1.0;
        return seconds >= maxSeconds ? TimeSpan.MaxValue : TimeSpan.FromSeconds(seconds);
    }

    public static KeyIdPattern Parse(string input)
    {
        if (!TryParse(input, out KeyIdPattern? pattern, out KeyIdValidationError error))
        {
            throw new ArgumentException($"Invalid Key ID suffix ({error}).", nameof(input));
        }

        return pattern!;
    }

    public static bool TryParse(string? input, out KeyIdPattern? pattern, out KeyIdValidationError error)
    {
        pattern = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = KeyIdValidationError.Empty;
            return false;
        }

        var cleanedBuilder = new StringBuilder(input.Length);
        bool hasInvalidChar = false;
        bool hasSeparatorOnly = false;

        foreach (char ch in input)
        {
            if (char.IsWhiteSpace(ch))
            {
                continue;
            }

            if (ch == '-' || ch == '_')
            {
                hasSeparatorOnly = true;
                continue;
            }

            char upper = char.ToUpperInvariant(ch);
            if (HexValue(upper) < 0)
            {
                hasInvalidChar = true;
            }
            cleanedBuilder.Append(upper);
        }

        string cleaned = cleanedBuilder.ToString();
        if (cleaned.Length == 0)
        {
            error = hasSeparatorOnly ? KeyIdValidationError.InvalidCharacters : KeyIdValidationError.Empty;
            return false;
        }

        if (hasInvalidChar)
        {
            error = KeyIdValidationError.InvalidCharacters;
            return false;
        }

        if (cleaned.Length > KeyIdHexLength)
        {
            error = KeyIdValidationError.TooLong;
            return false;
        }

        string canonical = cleaned.PadLeft(KeyIdHexLength, '*');
        ulong mask = cleaned.Length == KeyIdHexLength
            ? ulong.MaxValue
            : (1UL << (cleaned.Length * 4)) - 1UL;
        ulong target = Convert.ToUInt64(cleaned, 16);

        string formatted = $"{canonical[..4]} {canonical[4..8]} {canonical[8..12]} {canonical[12..16]}";
        pattern = new KeyIdPattern(cleaned, canonical, formatted, cleaned.Length, mask, target);
        error = KeyIdValidationError.None;
        return true;
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1
    };

    private static double Log1p(double x)
    {
        if (Math.Abs(x) < 1e-4)
        {
            return x - 0.5 * x * x + (x * x * x) / 3.0;
        }
        return Math.Log(1.0 + x);
    }

    private static double Expm1(double x)
    {
        if (Math.Abs(x) < 1e-5)
        {
            return x + 0.5 * x * x + (x * x * x) / 6.0;
        }
        return Math.Exp(x) - 1.0;
    }
}
