namespace MeetingPreps;

/// <summary>The time, and the local time zone, as the Python read them (datetime.now() and astimezone()). Tests and
/// parity checks fix both, so a prompt's "It is now" and every email's time come out the same.</summary>
public sealed class Clock
{
    public Func<DateTimeOffset> UtcNow { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>The zone astimezone() uses: the laptop's own.</summary>
    public TimeZoneInfo Local { get; init; } = TimeZoneInfo.Local;

    public static Clock System { get; } = new();

    /// <summary>A clock stopped at a local wall-clock time.</summary>
    public static Clock Fixed(DateTime local, TimeZoneInfo zone)
    {
        var at = MeetingPreps.Interim.PyNaive.Aware(local, zone);
        return new Clock { UtcNow = () => at.ToUniversalTime(), Local = zone };
    }

    /// <summary>datetime.now(): local wall-clock time, to the microsecond.</summary>
    public DateTime Now()
    {
        var t = TimeZoneInfo.ConvertTime(UtcNow(), Local).DateTime;
        return new DateTime(t.Ticks - t.Ticks % 10, DateTimeKind.Unspecified);
    }

    /// <summary>datetime.now(timezone.utc).</summary>
    public DateTimeOffset NowUtc()
    {
        var t = UtcNow().ToUniversalTime();
        return new DateTimeOffset(t.Ticks - t.Ticks % 10, TimeSpan.Zero);
    }

    /// <summary>when.astimezone(): the local wall-clock time of an instant.</summary>
    public DateTime ToLocal(DateTimeOffset when) => DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(when, Local).DateTime, DateTimeKind.Unspecified);

    /// <summary>A naive local time made aware (.astimezone() on a naive datetime).</summary>
    public DateTimeOffset Aware(DateTime local) => MeetingPreps.Interim.PyNaive.Aware(local, Local);
}
