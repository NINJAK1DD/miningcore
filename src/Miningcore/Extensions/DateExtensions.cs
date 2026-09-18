namespace Miningcore.Extensions;

public static class DateExtensions
{
    // ToUnixTimeMilliseconds truncates both conversions below to this interval.
    public const double UnixSecondsResolution = 0.001d;

    public static double ToUnixSeconds(this DateTime dt)
    {
        return ((DateTimeOffset) dt).ToUnixTimeMilliseconds() / 1000d;
    }

    public static double ToUnixSeconds(this DateTimeOffset dto)
    {
        return dto.ToUnixTimeMilliseconds() / 1000d;
    }
}
