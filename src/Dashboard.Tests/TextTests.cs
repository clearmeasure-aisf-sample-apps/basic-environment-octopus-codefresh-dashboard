namespace Dashboard.Tests;

public class TextTests
{
    private static readonly DateTimeOffset Time = new(2026, 10, 4, 22, 0, 5, TimeSpan.Zero);
    private static readonly TimeZoneInfo MinusFive = TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(-5), "test", "test");

    [Fact]
    public void TheClockIsInTheViewersTimeZone()
    {
        Assert.Equal("22:00:05", TimeText.Clock(Time, TimeZoneInfo.Utc));
        Assert.Equal("17:00:05", TimeText.Clock(Time, MinusFive));
    }

    [Fact]
    public void DateAndTimeNameTheOffset() =>
        Assert.Equal("2026-10-04 17:00 -05:00", TimeText.DateAndTime(Time, MinusFive));

    [Fact]
    public void TheMachineReadableTimeIsUtc() =>
        Assert.Equal("2026-10-04T22:00:05Z", TimeText.Iso(Time.ToOffset(TimeSpan.FromHours(2))));

    [Fact]
    public void AnEmptyHistoryIsSaidSo() =>
        Assert.Equal("No checks yet", HistoryText.Describe([]));

    [Fact]
    public void TheHistoryIsCountedByState()
    {
        ProbeResult[] history =
        [
            new(HealthState.Healthy, 200, 10, Time),
            new(HealthState.Unreachable, null, null, Time),
            new(HealthState.Healthy, 200, 12, Time),
            new(HealthState.Unhealthy, 503, 40, Time),
        ];

        Assert.Equal("Last 4 checks: 2 healthy, 1 unhealthy, 1 unreachable", HistoryText.Describe(history));
        Assert.Equal("Last check: 1 healthy", HistoryText.Describe(history[..1]));
    }

    [Fact]
    public void OneCheckIsDescribedWithWhatIsKnown()
    {
        Assert.Equal("22:00:05 Unhealthy, HTTP 503, 40 ms", HistoryText.Describe(new ProbeResult(HealthState.Unhealthy, 503, 40, Time), TimeZoneInfo.Utc));
        Assert.Equal("22:00:05 Unreachable", HistoryText.Describe(new ProbeResult(HealthState.Unreachable, null, null, Time), TimeZoneInfo.Utc));
    }
}
