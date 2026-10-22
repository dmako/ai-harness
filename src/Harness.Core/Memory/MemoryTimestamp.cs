using System.Globalization;

namespace Harness.Core.Memory;

/// <summary>RFC3339 UTC time with Go's full nanosecond precision, including imported timestamps.</summary>
public readonly record struct MemoryTimestamp : IComparable<MemoryTimestamp>
{
    private readonly DateTimeOffset _second;
    private readonly int _nanoseconds;

    public MemoryTimestamp(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        _second = utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerSecond));
        _nanoseconds = (int)(utc.Ticks % TimeSpan.TicksPerSecond) * 100;
    }

    private MemoryTimestamp(DateTimeOffset second, int nanoseconds)
    {
        _second = second;
        _nanoseconds = nanoseconds;
    }

    public static MemoryTimestamp Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        var nanoseconds = 0;
        var seconds = text;
        if (dot >= 0)
        {
            var end = dot + 1;
            while (end < text.Length && char.IsAsciiDigit(text[end]))
            {
                end++;
            }

            var fraction = text[(dot + 1)..end];
            if (fraction.Length is < 1 or > 9)
            {
                throw new FormatException("Invalid RFC3339 fraction.");
            }

            nanoseconds = int.Parse(fraction.PadRight(9, '0'), CultureInfo.InvariantCulture);
            seconds = text[..dot] + text[end..];
        }

        var second = DateTimeOffset
            .ParseExact(
                seconds,
                ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:sszzz"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal
            )
            .ToUniversalTime();
        return new MemoryTimestamp(second, nanoseconds);
    }

    public int CompareTo(MemoryTimestamp other)
    {
        var seconds = _second.CompareTo(other._second);
        return seconds is 0 ? _nanoseconds.CompareTo(other._nanoseconds) : seconds;
    }

    public string ToRfc3339() => _second.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public string ToRfc3339Nano()
    {
        if (_nanoseconds is 0)
        {
            return ToRfc3339();
        }

        return _second.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
            + "."
            + _nanoseconds.ToString("D9", CultureInfo.InvariantCulture).TrimEnd('0')
            + "Z";
    }

    public override string ToString() =>
        _second.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
        + "."
        + _nanoseconds.ToString("D9", CultureInfo.InvariantCulture)
        + "Z";

    public static bool operator <(MemoryTimestamp left, MemoryTimestamp right) => left.CompareTo(right) < 0;

    public static bool operator >(MemoryTimestamp left, MemoryTimestamp right) => left.CompareTo(right) > 0;

    public static bool operator <=(MemoryTimestamp left, MemoryTimestamp right) => left.CompareTo(right) <= 0;

    public static bool operator >=(MemoryTimestamp left, MemoryTimestamp right) => left.CompareTo(right) >= 0;
}
