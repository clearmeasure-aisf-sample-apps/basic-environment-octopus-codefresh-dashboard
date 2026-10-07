using Dashboard.Health;

namespace Dashboard.Cluster;

/// <summary>
/// The cluster that hosts an environment is known to be stopped: Azure's facts about it (<see cref="AksService"/>)
/// were read, say <c>powerState</c> <c>Stopped</c> and are not old. A cluster that is stopped on purpose outside
/// working hours is asleep, and what does not answer from inside it is no failure: the health view says so instead
/// of "Unreachable" and "Not serving".
/// </summary>
/// <param name="Cluster">The cluster's name; null when the topology does not say.</param>
/// <param name="AsOf">When the workflow read the facts from Azure: the cluster was stopped then.</param>
public sealed record ClusterSleep(string? Cluster, DateTimeOffset AsOf)
{
    /// <summary>The word of an asleep node's badge, and of the environment's chip.</summary>
    public const string Label = "Asleep";

    /// <summary>Why: <c>cluster aks-platform-nonprod is stopped</c>, or <c>the cluster is stopped</c> for one without a name.</summary>
    public string Reason => Cluster is { } name ? $"cluster {name} is stopped" : "the cluster is stopped";

    /// <summary><c>Asleep: cluster aks-platform-nonprod is stopped</c>: the banner of a deployable whose nodes are all asleep.</summary>
    public string Headline => $"{Label}: {Reason}";

    /// <summary>What a node's tile says in place of the reason it did not answer.</summary>
    public string NodeDetail => $"No answer, as expected: {Reason}.";

    /// <summary>
    /// The sleep of a cluster at the page's clock, or null: the cluster is asleep only when the last reading of
    /// Azure's facts succeeded, the facts say <c>Stopped</c>, and they say when they were read and that is no longer
    /// ago than <see cref="AksService.OldAfter"/>. Facts that say <c>Running</c>, that are missing, not read yet,
    /// unreadable, old or without a time prove nothing: null, and an unreachable node is a failure as before.
    /// </summary>
    /// <param name="service">The last reading of the cluster's facts; null when the topology names no <c>serviceUrl</c>.</param>
    public static ClusterSleep? Of(ClusterInfo cluster, SourceReading<AksService>? service, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        return service is { State: SourceState.Read, Value: { IsStopped: true, Generated: { } generated } facts } && !facts.IsOld(now)
            ? new ClusterSleep(cluster.Name, generated)
            : null;
    }

    /// <summary>
    /// True for an endpoint that is asleep: its cluster is, and it did not answer. An endpoint that answers shows
    /// what it answers (the cluster may have woken since the facts were read), and one that was not checked yet is
    /// being checked.
    /// </summary>
    public static bool Covers(ClusterSleep? sleep, HealthState state) => sleep is not null && state == HealthState.Unreachable;

    /// <summary>
    /// True for a deployable, or an environment, that is asleep as a whole: its cluster is, it has nodes, and none of
    /// them answered. One that answers, or is still being checked, leaves the decision to the serving assessment.
    /// </summary>
    public static bool CoversAll(ClusterSleep? sleep, IEnumerable<HealthState> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (sleep is null)
        {
            return false;
        }

        var any = false;
        foreach (var state in nodes)
        {
            if (state != HealthState.Unreachable)
            {
                return false;
            }

            any = true;
        }

        return any;
    }

    /// <summary>
    /// Under the banner, in whole sentences: <c>Azure reports the power state Stopped, as of 20:40:04 (5 min ago).
    /// Nothing answers from inside a stopped cluster: that is expected, not a failure.</c>
    /// </summary>
    public string Detail(DateTimeOffset now, TimeZoneInfo zone) =>
        $"Azure reports the power state {AksService.Stopped}, {ClusterText.AsOf(AsOf, now, zone)}. "
        + "Nothing answers from inside a stopped cluster: that is expected, not a failure.";
}
