namespace Dashboard.Health;

public enum SummaryLevel
{
    /// <summary>The topology has no endpoint.</summary>
    Empty,

    /// <summary>Nothing is known to be wrong, and not every endpoint has been checked.</summary>
    Pending,

    AllHealthy,

    Problems,
}

/// <summary>The counts behind the header: every tile counts as one node (Front Door endpoints and web apps).</summary>
public sealed record HealthSummary(int Total, int Healthy, int Pending)
{
    public int NotHealthy => Total - Healthy - Pending;

    public SummaryLevel Level =>
        Total == 0 ? SummaryLevel.Empty
        : NotHealthy > 0 ? SummaryLevel.Problems
        : Pending > 0 ? SummaryLevel.Pending
        : SummaryLevel.AllHealthy;

    public string Text => Level switch
    {
        SummaryLevel.Empty => "No nodes in the topology",
        SummaryLevel.Problems => $"{NotHealthy} of {Total} {Noun} not healthy",
        SummaryLevel.Pending when Healthy == 0 => $"Checking {Total} {Noun}",
        SummaryLevel.Pending => $"{Healthy} of {Total} {Noun} healthy, {Pending} being checked",
        _ => Total == 1 ? "The only node is healthy" : $"All {Total} nodes healthy",
    };

    private string Noun => Total == 1 ? "node" : "nodes";

    public static HealthSummary Of(IEnumerable<HealthState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        int total = 0, healthy = 0, pending = 0;
        foreach (var state in states)
        {
            total++;
            healthy += state == HealthState.Healthy ? 1 : 0;
            pending += state == HealthState.Pending ? 1 : 0;
        }

        return new HealthSummary(total, healthy, pending);
    }
}
