using System.Globalization;

namespace Dashboard.Health;

/// <summary>Times as the dashboard shows them, in the time zone of the viewer.</summary>
public static class TimeText
{
    /// <summary>The time of day: <c>15:04:05</c>.</summary>
    public static string Clock(DateTimeOffset time, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(time, zone).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Date and time to the minute with the offset from UTC: <c>2026-10-04 17:00 -05:00</c>.</summary>
    public static string DateAndTime(DateTimeOffset time, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(time, zone).ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);

    /// <summary>The machine-readable form for the <c>datetime</c> attribute of a <c>time</c> element.</summary>
    public static string Iso(DateTimeOffset time) =>
        time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// A length of time in whole units: <c>45 s</c>, <c>3 min</c>, <c>2 h</c>, <c>4 d</c>. A unit is used until two of
    /// the next: minutes up to two hours, hours up to two days.
    /// </summary>
    public static string Span(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalSeconds < 60 ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalSeconds} s")
            : span.TotalMinutes < 120 ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes} min")
            : span.TotalHours < 48 ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours} h")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalDays} d");
    }

    /// <summary>How long ago a moment was: <c>just now</c> within a minute (and for a moment ahead of the clock), then <c>3 min ago</c>.</summary>
    public static string Ago(DateTimeOffset then, DateTimeOffset now) =>
        now - then < TimeSpan.FromMinutes(1) ? "just now" : $"{Span(now - then)} ago";
}
