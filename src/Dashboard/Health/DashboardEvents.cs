using System.Globalization;

namespace Dashboard.Health;

public enum EventKind
{
    /// <summary>An endpoint's health state changed.</summary>
    Health,

    /// <summary>A web app's process started again.</summary>
    Restart,

    /// <summary>A web app reports another version: a deployment.</summary>
    Version,

    /// <summary>The node expected to serve a deployable's traffic changed: a failover or a failback.</summary>
    Serving,

    /// <summary>The version pinned in Git changed.</summary>
    Pinned,

    /// <summary>The traffic button was started or stopped.</summary>
    Traffic,

    /// <summary>Something changed in the cluster the system runs in: its status file, a node, a pod, the AKS service.</summary>
    Cluster,
}

public enum EventLevel
{
    Info,
    Good,
    Warning,
    Problem,
}

/// <summary>Something this page observed: when, where and in words.</summary>
/// <param name="Environment">The environment; null for an event of the page itself.</param>
/// <param name="Node">
/// The node (its region, or its name), "Front Door", or the deployable for an event of all its nodes; "cluster" or
/// "AKS" for an event of the cluster view.
/// </param>
public sealed record DashboardEvent(DateTimeOffset At, EventKind Kind, EventLevel Level, string? Environment, string? Node, string Text);

/// <summary>The last events this page observed since it was opened, newest first.</summary>
public sealed class EventLog
{
    public const int DefaultCapacity = 50;

    private readonly Lock _gate = new();
    private readonly HistoryBuffer<DashboardEvent> _events;

    public EventLog(int capacity = DefaultCapacity)
    {
        _events = new HistoryBuffer<DashboardEvent>(capacity);
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _events.Count;
            }
        }
    }

    /// <summary>Newest first; events of the same moment in the order they were observed.</summary>
    public IReadOnlyList<DashboardEvent> Newest
    {
        get
        {
            lock (_gate)
            {
                return [.. _events.Select((entry, index) => (entry, index)).OrderByDescending(item => item.entry.At).ThenBy(item => item.index).Select(item => item.entry)];
            }
        }
    }

    public void Add(DashboardEvent entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            _events.Add(entry);
        }
    }

    public void AddRange(IEnumerable<DashboardEvent> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var entry in entries)
        {
            Add(entry);
        }
    }
}

/// <summary>What the page knew about one endpoint at one check: the input of <see cref="EventDetector"/>.</summary>
public sealed record NodeObservation(HealthState State, string? Version, TelemetrySnapshot? Telemetry, ProbeResult? Last = null)
{
    public static NodeObservation Of(TargetStatus target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new NodeObservation(target.State, target.Version, target.Telemetry, target.Last);
    }
}

/// <summary>
/// Finds the events between two observations: what changed from one check to the next. It decides nothing about the
/// system; it names what the page saw, in words.
/// </summary>
public static class EventDetector
{
    /// <summary>
    /// The events of one endpoint between the check before and this one: its health state, and for a web app a
    /// restart and another version. The first check of the page is an event only when the endpoint is not healthy.
    /// </summary>
    /// <param name="node">The node's label: its region or name, or "Front Door".</param>
    /// <param name="isNode">False for a Front Door endpoint, which answers with the version of whichever node served.</param>
    public static IReadOnlyList<DashboardEvent> Node(
        NodeObservation before,
        NodeObservation after,
        string environment,
        string deployable,
        string node,
        bool isNode,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var events = new List<DashboardEvent>();
        if (after.State != before.State && after.State != HealthState.Pending && !(before.State == HealthState.Pending && after.State == HealthState.Healthy))
        {
            events.Add(new DashboardEvent(at, EventKind.Health, LevelOf(after.State), environment, node, HealthWords(before, after, deployable)));
        }

        if (!isNode)
        {
            return events;
        }

        if (Restarted(before.Telemetry, after.Telemetry))
        {
            var uptime = after.Telemetry?.Process?.UptimeSeconds is { } seconds
                ? $", up {TimeText.Span(TimeSpan.FromSeconds(seconds))}"
                : string.Empty;
            events.Add(new DashboardEvent(at, EventKind.Restart, EventLevel.Warning, environment, node, $"{deployable} restarted{uptime}"));
        }

        if (before.Version is { } old && after.Version is { } current && !string.Equals(old, current, StringComparison.OrdinalIgnoreCase))
        {
            events.Add(new DashboardEvent(at, EventKind.Version, EventLevel.Info, environment, node, $"{deployable}: {old} → {current}, deployed"));
        }

        return events;
    }

    /// <summary>
    /// A restart: the process's uptime went down, or it reports a later start. Neither is known for an app that
    /// reports no telemetry, and a check without telemetry (the app was down) says nothing.
    /// </summary>
    public static bool Restarted(TelemetrySnapshot? before, TelemetrySnapshot? after)
    {
        if (before is null || after is null)
        {
            return false;
        }

        if (before.Process?.UptimeSeconds is { } earlier && after.Process?.UptimeSeconds is { } now)
        {
            return now < earlier;
        }

        return before.StartedAt is { } started && after.StartedAt is { } restarted && restarted > started;
    }

    /// <summary>
    /// The serving decision of a deployable changed: a failover, a failback, or nothing serves. Null when the node
    /// expected to serve is the same, and while the page has not decided yet.
    /// </summary>
    public static DashboardEvent? Serving(ServingAssessment? before, ServingAssessment after, string environment, string deployable, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(after);
        if (after.State is ServingState.Pending or ServingState.NoNodes)
        {
            return null;
        }

        var known = before is not null && before.State is not (ServingState.Pending or ServingState.NoNodes);
        var from = known ? before!.Expected?.Label : null;
        var to = after.Expected?.Label;
        if (known && before!.State == after.State && string.Equals(from, to, StringComparison.Ordinal))
        {
            return null;
        }

        DashboardEvent Event(EventLevel level, string text) => new(at, EventKind.Serving, level, environment, deployable, text);
        return after.State switch
        {
            // A single node whose role is not primary: nothing failed over, it serves again or still.
            ServingState.FailedOver when after.OnlyNode is not null => known && before!.State == ServingState.Down ? Event(EventLevel.Good, $"{to} serves traffic again.") : null,
            ServingState.FailedOver when from is not null => Event(EventLevel.Warning, $"Failover: {from} → {to}. {after.FailoverDetail}"),
            ServingState.FailedOver => Event(EventLevel.Warning, $"Failed over to {to}. {after.FailoverDetail}"),
            ServingState.Primary when known && before!.State == ServingState.FailedOver => Event(EventLevel.Good, $"Failback: {from} → {to}. The primary is healthy again."),
            ServingState.Primary when known && before!.State == ServingState.Down => Event(EventLevel.Good, $"{to} serves traffic again."),
            ServingState.Primary when known => Event(EventLevel.Info, $"Serving region: {from} → {to}."),
            ServingState.Down => Event(EventLevel.Problem, from is null ? "No healthy node: nothing can serve traffic." : $"No healthy node: {from} no longer serves, and nothing else can."),
            _ => null,
        };
    }

    /// <summary>
    /// The version pinned in Git for a deployable changed between two readings of the file that holds its pin: the
    /// environment's <c>versions.json</c>, or the deployable's own Kustomize file.
    /// </summary>
    public static DashboardEvent? Pinned(PinnedVersions before, PinnedVersions after, string environment, string deployable, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.State != PinnedVersionsState.Read || after.State != PinnedVersionsState.Read)
        {
            return null;
        }

        var (old, current) = (before.Of(deployable), after.Of(deployable));
        if (current is null || string.Equals(old, current, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var text = old is null ? $"{deployable}: pinned {current} in Git (its first pin)" : $"{deployable}: pinned {old} → {current} in Git";
        return new DashboardEvent(at, EventKind.Pinned, EventLevel.Info, environment, deployable, text);
    }

    /// <summary>The traffic button, started or stopped, in the words of the panel.</summary>
    public static DashboardEvent Traffic(string environment, string text, DateTimeOffset at) =>
        new(at, EventKind.Traffic, EventLevel.Info, environment, null, text);

    private static EventLevel LevelOf(HealthState state) => state switch
    {
        HealthState.Healthy => EventLevel.Good,
        HealthState.Unhealthy => EventLevel.Warning,
        _ => EventLevel.Problem,
    };

    private static string HealthWords(NodeObservation before, NodeObservation after, string deployable)
    {
        var label = HealthClassifier.Label(after.State);
        var facts = after.Last switch
        {
            { StatusCode: { } status } => string.Create(CultureInfo.InvariantCulture, $" (HTTP {status})"),
            { Detail: { } detail } => $": {detail.TrimEnd('.')}",
            _ => string.Empty,
        };
        return before.State == HealthState.Pending
            ? $"{deployable}: {label} at the first check{facts}"
            : $"{deployable}: {HealthClassifier.Label(before.State)} → {label}{facts}";
    }
}
