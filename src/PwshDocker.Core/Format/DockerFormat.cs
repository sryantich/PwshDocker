using System.Globalization;

namespace PwshDocker;

/// <summary>Human-friendly formatting that matches the docker CLI (used by the module's table views).</summary>
public static class DockerFormat
{
    private static readonly string[] DecimalUnits = { "B", "kB", "MB", "GB", "TB", "PB", "EB" };
    private static readonly string[] BinaryUnits = { "B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB" };

    /// <summary>Decimal size with 3 significant digits, like `docker images` (e.g. 72.8MB, 1.23GB).</summary>
    public static string HumanSize(long? bytes) => HumanSize(bytes, 3);

    /// <summary>Decimal size with the given number of significant digits.</summary>
    public static string HumanSize(long? bytes, int precision) =>
        bytes is null or < 0 ? string.Empty : FormatSize(bytes.Value, 1000d, DecimalUnits, precision);

    /// <summary>Binary size with 4 significant digits, like `docker stats` memory (e.g. 12.54MiB).</summary>
    public static string BinarySize(long? bytes) =>
        bytes is null or < 0 ? string.Empty : FormatSize(bytes.Value, 1024d, BinaryUnits, 4);

    private static string FormatSize(double size, double unitBase, string[] units, int precision)
    {
        precision = Math.Clamp(precision, 1, 15);
        var unit = 0;
        while (size >= unitBase && unit < units.Length - 1)
        {
            size /= unitBase;
            unit++;
        }

        // Avoid "1E+03MB" when rounding to the requested precision reaches the next unit.
        var digits = Digits(size);
        if (digits <= precision && Math.Round(size, precision - digits) >= unitBase && unit < units.Length - 1)
        {
            size /= unitBase;
            unit++;
        }

        return size.ToString("G" + precision.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) + units[unit];
    }

    private static int Digits(double value) => value < 1 ? 1 : (int)Math.Floor(Math.Log10(value)) + 1;

    /// <summary>Human duration like the docker CLI (e.g. "Less than a second", "About a minute", "3 hours").</summary>
    public static string HumanDuration(TimeSpan duration)
    {
        var seconds = (long)duration.TotalSeconds;
        if (seconds < 1)
        {
            return "Less than a second";
        }

        if (seconds == 1)
        {
            return "1 second";
        }

        if (seconds < 60)
        {
            return $"{seconds} seconds";
        }

        var minutes = (long)duration.TotalMinutes;
        if (minutes == 1)
        {
            return "About a minute";
        }

        if (minutes < 60)
        {
            return $"{minutes} minutes";
        }

        var hours = (long)Math.Round(duration.TotalHours, MidpointRounding.AwayFromZero);
        if (hours == 1)
        {
            return "About an hour";
        }

        if (hours < 48)
        {
            return $"{hours} hours";
        }

        if (hours < 24 * 7 * 2)
        {
            return $"{hours / 24} days";
        }

        if (hours < 24 * 30 * 2)
        {
            return $"{hours / 24 / 7} weeks";
        }

        if (hours < 24 * 365 * 2)
        {
            return $"{hours / 24 / 30} months";
        }

        return $"{(long)duration.TotalHours / 24 / 365} years";
    }

    /// <summary>"3 hours ago" style relative time for a past timestamp.</summary>
    public static string Ago(DateTime? time)
    {
        if (time is null)
        {
            return string.Empty;
        }

        var elapsed = DateTime.Now - time.Value.ToLocalTime();
        return elapsed < TimeSpan.Zero ? "Less than a second ago" : HumanDuration(elapsed) + " ago";
    }

    /// <summary>First 12 hex characters of an ID (the "sha256:" prefix is removed).</summary>
    public static string ShortId(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return string.Empty;
        }

        var colon = id.IndexOf(':');
        var hex = colon >= 0 ? id[(colon + 1)..] : id;
        return hex.Length > 12 ? hex[..12] : hex;
    }

    /// <summary>Truncates text to a maximum length with an ellipsis.</summary>
    public static string Truncate(string? text, int length)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= length)
        {
            return text ?? string.Empty;
        }

        return length <= 1 ? text[..length] : text[..(length - 1)] + "…";
    }
}
