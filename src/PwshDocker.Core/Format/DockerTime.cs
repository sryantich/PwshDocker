using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PwshDocker;

/// <summary>Engine timestamp helpers (RFC 3339 with nanoseconds, Unix seconds/nanoseconds).</summary>
public static partial class DockerTime
{
    [GeneratedRegex(@"^(\d{4})-(\d{2})-(\d{2})[Tt ](\d{2}):(\d{2}):(\d{2})(?:\.(\d+))?(Z|z|[+-]\d{2}:?\d{2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex Rfc3339Pattern();

    /// <summary>
    /// Parses an engine timestamp (e.g. 2025-12-12T14:49:51.123456789Z) to local time. Returns null for empty
    /// values and for Go's zero time (0001-01-01T00:00:00Z), which the engine uses for "never".
    /// </summary>
    public static DateTime? ParseRfc3339(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = Rfc3339Pattern().Match(value.Trim());
        if (!match.Success)
        {
            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var fallback)
                ? ToLocal(fallback)
                : null;
        }

        int Part(int group) => int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);
        var year = Part(1);
        if (year <= 1)
        {
            return null;
        }

        var ticks = 0L;
        if (match.Groups[7].Success)
        {
            var fraction = match.Groups[7].Value;
            fraction = fraction.Length > 7 ? fraction[..7] : fraction.PadRight(7, '0');
            ticks = long.Parse(fraction, CultureInfo.InvariantCulture);
        }

        var offset = TimeSpan.Zero;
        if (match.Groups[8].Success && match.Groups[8].Value is not ("Z" or "z"))
        {
            var zone = match.Groups[8].Value.Replace(":", string.Empty, StringComparison.Ordinal);
            var sign = zone[0] == '-' ? -1 : 1;
            offset = new TimeSpan(int.Parse(zone.AsSpan(1, 2), CultureInfo.InvariantCulture), int.Parse(zone.AsSpan(3, 2), CultureInfo.InvariantCulture), 0) * sign;
        }

        var timestamp = new DateTimeOffset(year, Part(2), Part(3), Part(4), Part(5), Part(6), offset).AddTicks(ticks);
        return ToLocal(timestamp);
    }

    /// <summary>Parses a timestamp property of a JSON object (string RFC 3339 or Unix seconds).</summary>
    public static DateTime? FromJson(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => ParseRfc3339(value.GetString()),
            JsonValueKind.Number when value.TryGetInt64(out var seconds) && seconds > 0 => FromUnixSeconds(seconds),
            _ => null,
        };
    }

    public static DateTime FromUnixSeconds(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime;

    public static DateTime FromUnixNanoseconds(long nanoseconds) =>
        DateTimeOffset.UnixEpoch.AddTicks(nanoseconds / 100).LocalDateTime;

    /// <summary>Formats a time as Unix seconds with a nanosecond fraction, as accepted by since/until parameters.</summary>
    public static string ToUnixTimestamp(DateTime value) =>
        ToUnixTimestamp(value.Kind == DateTimeKind.Unspecified ? new DateTimeOffset(value, TimeZoneInfo.Local.GetUtcOffset(value)) : new DateTimeOffset(value));

    public static string ToUnixTimestamp(DateTimeOffset value)
    {
        var ticks = value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        var seconds = Math.DivRem(ticks, TimeSpan.TicksPerSecond, out var remainder);
        if (remainder < 0)
        {
            seconds--;
            remainder += TimeSpan.TicksPerSecond;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{seconds}.{remainder * 100:D9}");
    }

    private static DateTime ToLocal(DateTimeOffset value) => value.LocalDateTime;
}
