namespace Dashboard.Health;

public enum SummaryLevel
{
    /// <summary>The topology has no endpoint.</summary>
    Empty,

    /// <summary>Nothing is known to be wrong, and not every endpoint has been checked.</summary>
    Pending,

    AllHealthy,

    Problems,

    /// <summary>Every node is asleep: its cluster is stopped, and nothing is known to be wrong.</summary>
    Asleep,
}

/// <summary>
/// The counts behind the header: every tile counts as one node (Front Door endpoints and web apps). A node that is
/// asleep (it does not answer, and its cluster is known to be stopped) is counted on its own: it is neither healthy
/// nor not healthy, and the other numbers are of the nodes that are awake.
/// </summary>
/// <param name="Asleep">The nodes that are asleep; none for a topology that names no cluster behind its environments.</param>
public sealed record HealthSummary(int Total, int Healthy, int Pending, int Asleep = 0)
{
    /// <summary>The nodes that are expected to answer.</summary>
    public int Awake => Total - Asleep;

    public int NotHealthy => Awake - Healthy - Pending;

    public SummaryLevel Level =>
        Total == 0 ? SummaryLevel.Empty
        : NotHealthy > 0 ? SummaryLevel.Problems
        : Pending > 0 ? SummaryLevel.Pending
        : Awake == 0 ? SummaryLevel.Asleep
        : SummaryLevel.AllHealthy;

    public string Text => Level switch
    {
        SummaryLevel.Empty => "No nodes in the topology",
        SummaryLevel.Asleep => Total == 1 ? "The only node is asleep" : $"All {Total} nodes asleep",
        SummaryLevel.Problems => $"{NotHealthy} of {Awake} {Noun} not healthy{AndAsleep}",
        SummaryLevel.Pending when Healthy == 0 => $"Checking {Awake} {(Awake == 1 ? "node" : "nodes")}{AndAsleep}",
        SummaryLevel.Pending => $"{Healthy} of {Awake} {Noun} healthy, {Pending} being checked{AndAsleep}",
        _ => Awake == 1 ? $"The only {Noun} is healthy{AndAsleep}" : $"All {Awake} {Noun} healthy{AndAsleep}",
    };

    /// <summary>"nodes" as before while none is asleep; "awake nodes" next to the count of those that are.</summary>
    private string Noun => (Asleep > 0 ? "awake " : string.Empty) + (Awake == 1 ? "node" : "nodes");

    private string AndAsleep => Asleep > 0 ? $", {Asleep} asleep" : string.Empty;

    public static HealthSummary Of(IEnumerable<HealthState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        return OfNodes(states.Select(state => (state, false)));
    }

    /// <summary>The summary of nodes of which some may be asleep: each with its state and whether it is asleep.</summary>
    public static HealthSummary OfNodes(IEnumerable<(HealthState State, bool Asleep)> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        int total = 0, healthy = 0, pending = 0, asleep = 0;
        foreach (var (state, sleeps) in nodes)
        {
            total++;
            if (sleeps)
            {
                asleep++;
                continue;
            }

            healthy += state == HealthState.Healthy ? 1 : 0;
            pending += state == HealthState.Pending ? 1 : 0;
        }

        return new HealthSummary(total, healthy, pending, asleep);
    }
}
