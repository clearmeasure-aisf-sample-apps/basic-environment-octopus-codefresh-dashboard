namespace Dashboard.Health;

/// <summary>The history strip in words, for readers who do not see it.</summary>
public static class HistoryText
{
    /// <summary><c>Last 12 checks: 10 healthy, 2 unreachable</c>.</summary>
    public static string Describe(IReadOnlyCollection<ProbeResult> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history.Count == 0)
        {
            return "No checks yet";
        }

        var parts = new[] { HealthState.Healthy, HealthState.Unhealthy, HealthState.Unreachable }
            .Select(state => (State: state, Count: history.Count(result => result.State == state)))
            .Where(entry => entry.Count > 0)
            .Select(entry => $"{entry.Count} {HealthClassifier.Label(entry.State).ToLowerInvariant()}");
        var checks = history.Count == 1 ? "Last check" : $"Last {history.Count} checks";
        return $"{checks}: {string.Join(", ", parts)}";
    }

    /// <summary>One check in words: <c>15:04:05 Healthy, HTTP 200, 123 ms</c>.</summary>
    public static string Describe(ProbeResult result, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(result);
        var text = $"{TimeText.Clock(result.CheckedAt, zone)} {HealthClassifier.Label(result.State)}";
        if (result.StatusCode is { } status)
        {
            text += $", HTTP {status}";
        }

        if (result.LatencyMs is { } latency)
        {
            text += $", {latency} ms";
        }

        return text;
    }
}
