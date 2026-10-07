namespace Dashboard.Health;

/// <summary>The trends the page draws, each from the readings a node keeps (<see cref="TargetStatus.Samples"/>).</summary>
public static class Trends
{
    public static Trend? Requests(TargetStatus node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.TrendOf(telemetry => telemetry.Requests, "Requests per minute");
    }

    public static Trend? FromFrontDoor(TargetStatus node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.TrendOf(telemetry => telemetry.FromFrontDoor, "Requests per minute from Front Door");
    }

    public static Trend? Direct(TargetStatus node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.TrendOf(telemetry => telemetry.Direct, "Requests per minute to the web app's own address");
    }

    /// <summary>
    /// The trend of the SQL number that is shown: the commands of the requests when the node tells them from the
    /// background ones now (a reading from before it did is left out), all commands otherwise.
    /// </summary>
    public static Trend? Sql(TargetStatus node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.Telemetry is { SplitsSql: true }
            ? node.TrendOf(telemetry => telemetry.SplitsSql ? telemetry.SqlRequests : null, "SQL commands per minute while handling requests")
            : node.TrendOf(telemetry => telemetry.Sql, "SQL commands per minute");
    }

    public static Trend? Cpu(TargetStatus node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.TrendOf(telemetry => telemetry.Process?.CpuPercent, "CPU of the process", " %");
    }

    /// <summary>
    /// The requests Front Door forwarded to all of its origins together: the readings of the same check added up,
    /// counted back from the last one. A check where no origin reported is left out.
    /// </summary>
    public static Trend? Sum(IEnumerable<TargetStatus> origins)
    {
        ArgumentNullException.ThrowIfNull(origins);
        var samples = origins.Select(origin => origin.Samples).ToList();
        var length = samples.Count == 0 ? 0 : samples.Max(readings => readings.Count);
        var sums = new List<double?>();
        for (var back = length; back >= 1; back--)
        {
            var readings = samples
                .Where(buffer => buffer.Count >= back)
                .Select(buffer => buffer[buffer.Count - back])
                .OfType<TelemetrySnapshot>()
                .ToList();
            sums.Add(readings.Count == 0 ? null : readings.Sum(reading => reading.FromFrontDoor));
        }

        return Trend.Of(sums, "Requests per minute through Front Door");
    }
}
