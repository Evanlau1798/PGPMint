using System.Globalization;

namespace PGPMint.Core.Search;

public static class CreationTimestampRange
{
    public const uint DefaultMinTimestamp = 946_684_800u; // 2000-01-01T00:00:00Z
    public const uint AbsoluteMinTimestamp = 86_400u;     // 1970-01-02T00:00:00Z
    public const uint MinRangeWidthSeconds = 7u * 86_400u; // >= 7 days to preserve sufficient timestamp entropy

    public static bool TryParseInput(string? raw, out uint? unixTimestamp)
    {
        unixTimestamp = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        string trimmed = raw.Trim();
        if (ulong.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out ulong rawUnix))
        {
            if (rawUnix < AbsoluteMinTimestamp || rawUnix > uint.MaxValue)
            {
                return false;
            }

            unixTimestamp = (uint)rawUnix;
            return true;
        }

        if (DateOnly.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
        {
            long unix = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            if (unix < AbsoluteMinTimestamp || unix > uint.MaxValue)
            {
                return false;
            }

            unixTimestamp = (uint)unix;
            return true;
        }

        return false;
    }

    public static string FormatUtcDate(uint? unixTimestamp)
    {
        if (unixTimestamp is not { } value || value < AbsoluteMinTimestamp)
        {
            return string.Empty;
        }

        return DateTimeOffset.FromUnixTimeSeconds(value).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    public static void Validate(uint? minInclusive, uint? maxInclusive, long nowUnixSeconds)
    {
        if (!TryValidate(minInclusive, maxInclusive, nowUnixSeconds, out string? error))
        {
            throw new ArgumentException(error);
        }
    }

    public static bool TryValidate(uint? minInclusive, uint? maxInclusive, long nowUnixSeconds, out string? error)
    {
        long upperLimit = Math.Clamp(nowUnixSeconds + 1L, 2L, uint.MaxValue);
        if (minInclusive is { } minVal && minVal < AbsoluteMinTimestamp)
        {
            error = "Start timestamp cannot be earlier than 1970-01-02 UTC.";
            return false;
        }

        if (maxInclusive is { } maxVal)
        {
            if (maxVal < AbsoluteMinTimestamp)
            {
                error = "End timestamp cannot be earlier than 1970-01-02 UTC.";
                return false;
            }

            if (maxVal > nowUnixSeconds)
            {
                error = "End timestamp cannot be in the future.";
                return false;
            }
        }

        uint effectiveEndExclusive = maxInclusive is { } maxTs
            ? (uint)Math.Min((ulong)maxTs + 1UL, (ulong)upperLimit)
            : (uint)upperLimit;
        uint effectiveStart = minInclusive ?? (effectiveEndExclusive > DefaultMinTimestamp ? DefaultMinTimestamp : 1u);

        if (effectiveEndExclusive <= effectiveStart ||
            (ulong)effectiveEndExclusive - effectiveStart < MinRangeWidthSeconds)
        {
            error = "Custom timestamp range must span at least 7 days.";
            return false;
        }

        error = null;
        return true;
    }

    public static void ResolveBounds(
        uint? minInclusive,
        uint? maxInclusive,
        long nowUnixSeconds,
        out uint startInclusive,
        out uint endExclusive)
    {
        Validate(minInclusive, maxInclusive, nowUnixSeconds);
        long upperLimit = Math.Clamp(nowUnixSeconds + 1L, 2L, uint.MaxValue);
        endExclusive = maxInclusive is { } maxTs
            ? (uint)Math.Min((ulong)maxTs + 1UL, (ulong)upperLimit)
            : (uint)upperLimit;
        startInclusive = minInclusive ?? (endExclusive > DefaultMinTimestamp ? DefaultMinTimestamp : 1u);
    }
}
