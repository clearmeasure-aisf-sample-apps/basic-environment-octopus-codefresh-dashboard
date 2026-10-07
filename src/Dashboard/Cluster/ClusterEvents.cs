using System.Globalization;
using Dashboard.Health;

namespace Dashboard.Cluster;

/// <summary>
/// Finds the events of the cluster between two readings, as <see cref="EventDetector"/> does for the endpoints: what
/// changed from one check to the next, in words. The first reading is no event.
/// </summary>
public static class ClusterEventDetector
{
    /// <summary>What an event of the cluster as a whole names as its place.</summary>
    public const string ClusterPlace = "cluster";

    /// <summary>What an event of the AKS service names as its place.</summary>
    public const string ServicePlace = "AKS";

    /// <summary>
    /// How many pods a round names one by one: a node that fails takes every pod with it, and the log keeps fifty
    /// events. The others are counted in one more event.
    /// </summary>
    public const int NamedPods = 10;

    /// <summary>
    /// The cluster's status file stopped answering or answers again, or the collector stopped writing or writes
    /// again. Null when nothing changed, and at the first reading.
    /// </summary>
    /// <param name="detail">Why the file was not read, as the end of a sentence.</param>
    public static DashboardEvent? Liveness(ClusterLiveness before, ClusterLiveness after, string? detail, DateTimeOffset at)
    {
        if (before == after || before == ClusterLiveness.Pending || after == ClusterLiveness.Pending)
        {
            return null;
        }

        DashboardEvent Event(EventLevel level, string text) => new(at, EventKind.Cluster, level, null, ClusterPlace, text);
        var reason = string.IsNullOrWhiteSpace(detail) ? string.Empty : $": {detail.TrimEnd('.')}";
        return after switch
        {
            ClusterLiveness.Silent => Event(EventLevel.Problem, $"The cluster's status file stopped answering{reason}"),
            ClusterLiveness.Unreadable => Event(EventLevel.Problem, $"The cluster's status file could not be read{reason}"),
            ClusterLiveness.Stale => Event(EventLevel.Problem, "The collector in the cluster stopped writing: the status file is stale"),
            _ when before == ClusterLiveness.Stale => Event(EventLevel.Good, "The collector in the cluster writes again"),
            _ => Event(EventLevel.Good, "The cluster's status file answers again"),
        };
    }

    /// <summary>
    /// What changed in the cluster between the last status the page read and this one: a node that is no longer ready
    /// or is ready again, and per pod one event at most: its restart count rose, or else it became unhealthy, or else
    /// it is ready again. A node or a pod the last status did not have is no event, unless the pod is unhealthy.
    /// </summary>
    /// <param name="environments">The environments of the topology: a pod of an environment's namespace is an event of that environment.</param>
    public static IReadOnlyList<DashboardEvent> Status(
        ClusterStatus before,
        ClusterStatus after,
        IEnumerable<EnvironmentInfo> environments,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(environments);
        var events = new List<DashboardEvent>();
        var nodes = before.Nodes.GroupBy(node => node.Name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var node in after.Nodes)
        {
            if (nodes.TryGetValue(node.Name, out var known) && known.Ready != node.Ready)
            {
                events.Add(node.Ready
                    ? new DashboardEvent(at, EventKind.Cluster, EventLevel.Good, null, ClusterPlace, $"Node {ClusterText.ShortNode(node.Name)} is ready again")
                    : new DashboardEvent(at, EventKind.Cluster, EventLevel.Problem, null, ClusterPlace, $"Node {ClusterText.ShortNode(node.Name)} is not ready"));
            }
        }

        var names = environments
            .Where(environment => environment.Namespace is not null)
            .GroupBy(environment => environment.Namespace!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.Ordinal);
        var (beforeAt, afterAt) = (before.AsOf(at), after.AsOf(at));
        var pods = before.Pods
            .GroupBy(entry => (entry.Namespace.Name, entry.Pod.Name))
            .ToDictionary(group => group.Key, group => group.First().Pod);
        var changes = new List<DashboardEvent>();
        foreach (var (space, pod) in after.Pods)
        {
            var known = pods.GetValueOrDefault((space.Name, pod.Name));
            if (Pod(known, known is null ? null : PodRules.StateOf(known, beforeAt), pod, PodRules.StateOf(pod, afterAt), space.Name, afterAt) is { } text)
            {
                changes.Add(new DashboardEvent(at, EventKind.Cluster, text.Level, names.GetValueOrDefault(space.Name), ClusterPlace, text.Text));
            }
        }

        events.AddRange(changes.Take(NamedPods));
        if (changes.Count > NamedPods)
        {
            events.Add(new DashboardEvent(
                at,
                EventKind.Cluster,
                EventLevel.Info,
                null,
                ClusterPlace,
                string.Create(CultureInfo.InvariantCulture, $"{changes.Count - NamedPods} more pods changed in this check")));
        }

        return events;
    }

    /// <summary>
    /// Azure's verdict on the AKS service or its power state changed between the last facts the page read and these.
    /// </summary>
    public static IReadOnlyList<DashboardEvent> Service(AksService before, AksService after, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var events = new List<DashboardEvent>();
        if (before.Availability?.State is { } verdict && after.Availability?.State is { } now && !AksService.Is(verdict, now))
        {
            var level = now switch
            {
                _ when AksService.Is(now, AksAvailability.Available) => EventLevel.Good,
                _ when AksService.Is(now, AksAvailability.Degraded) => EventLevel.Warning,
                _ when AksService.Is(now, AksAvailability.Unavailable) => EventLevel.Problem,
                _ => EventLevel.Info,
            };
            events.Add(new DashboardEvent(at, EventKind.Cluster, level, null, ServicePlace, $"Azure's verdict on the AKS service: {verdict} → {now}"));
        }

        if (before.PowerState is { } power && after.PowerState is { } current && !AksService.Is(power, current))
        {
            events.Add(new DashboardEvent(
                at,
                EventKind.Cluster,
                AksService.Is(current, AksService.Running) ? EventLevel.Good : EventLevel.Info,
                null,
                ServicePlace,
                $"The power state of the AKS service: {power} → {current}"));
        }

        return events;
    }

    /// <summary>The one event of a pod in a round, by what matters most: a restart, then unhealthy, then ready again.</summary>
    private static (EventLevel Level, string Text)? Pod(ClusterPod? before, PodState? was, ClusterPod after, PodState now, string space, DateTimeOffset asOf)
    {
        var name = ClusterText.PodIn(after, space);
        if (before is not null && after.Restarts > before.Restarts)
        {
            var reason = after.Reason is { } why && now != PodState.Ready ? $": {why}" : string.Empty;
            return (EventLevel.Warning, $"{name} restarted ({ClusterText.Restarts(after.Restarts)}){reason}");
        }

        if (now == PodState.Unhealthy && was != PodState.Unhealthy)
        {
            var words = ClusterText.PodWords(after, now, asOf);
            return (EventLevel.Warning, $"{name} is unhealthy: {(after.Phase == ClusterPod.Running && after.Reason is null ? "not ready" : words)}");
        }

        return was == PodState.Unhealthy && now == PodState.Ready ? (EventLevel.Good, $"{name} is ready again") : null;
    }
}
