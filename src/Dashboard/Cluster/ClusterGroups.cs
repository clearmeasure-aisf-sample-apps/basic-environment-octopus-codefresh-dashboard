using Dashboard.Health;

namespace Dashboard.Cluster;

/// <summary>One row of a namespace's table: a pod and what the view says about it.</summary>
public sealed record PodRow(ClusterPod Pod, PodState State);

/// <summary>A namespace as the view shows it: its pods in the order of the table, its volumes and its sums.</summary>
/// <param name="Environment">The environment of the topology whose pods the namespace holds; null for a namespace of the platform.</param>
/// <param name="Found">False for an environment's namespace that the status file does not list.</param>
/// <param name="Pods">The pods that are not ready and not finished first, then the ready ones, then the finished; each by name.</param>
public sealed record NamespaceGroup(
    string Name,
    EnvironmentInfo? Environment,
    bool Found,
    IReadOnlyList<PodRow> Pods,
    IReadOnlyList<ClusterVolume> Volumes)
{
    /// <summary>The pods that are ready.</summary>
    public int Ready => Pods.Count(row => row.State == PodState.Ready);

    /// <summary>The pods that count: every pod but the finished ones.</summary>
    public int Total => Pods.Count(row => row.State != PodState.Finished);

    /// <summary>The finished jobs: counted on their own, in neither number above.</summary>
    public int Finished => Pods.Count(row => row.State == PodState.Finished);

    public int Unhealthy => Pods.Count(row => row.State == PodState.Unhealthy);

    public int Restarts => Pods.Sum(row => row.Pod.Restarts);

    /// <summary>The CPU its pods use, in millicores; null when none reports a usage.</summary>
    public double? CpuUsage => Sum(Pods.Select(row => row.Pod.Cpu.Usage));

    /// <summary>The memory its pods use, in bytes; null when none reports a usage.</summary>
    public double? MemoryUsage => Sum(Pods.Select(row => row.Pod.Memory.Usage));

    internal static NamespaceGroup Of(ClusterNamespace space, EnvironmentInfo? environment, DateTimeOffset asOf) =>
        new(
            space.Name,
            environment,
            true,
            [.. space.Pods
                .Select(pod => new PodRow(pod, PodRules.StateOf(pod, asOf)))
                .OrderBy(row => Order(row.State))
                .ThenBy(row => row.Pod.Label, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Pod.Name, StringComparer.OrdinalIgnoreCase)],
            space.Volumes);

    internal static double? Sum(IEnumerable<double?> numbers)
    {
        var known = numbers.OfType<double>().ToList();
        return known.Count == 0 ? null : known.Sum();
    }

    private static int Order(PodState state) => state switch
    {
        PodState.Unhealthy => 0,
        PodState.Starting => 1,
        PodState.Ready => 2,
        _ => 3,
    };
}

/// <summary>
/// The namespaces of the cluster in the order of the view: first those of the topology's environments, in the
/// topology's order, then every other namespace by name, as the platform.
/// </summary>
public sealed record ClusterGroups(IReadOnlyList<NamespaceGroup> Environments, IReadOnlyList<NamespaceGroup> Platform)
{
    /// <summary>True when a pod of the platform is unhealthy: its group is then shown open.</summary>
    public bool PlatformNeedsALook => Platform.Any(space => space.Unhealthy > 0);

    /// <param name="asOf">The moment the facts are of (<see cref="ClusterStatus.AsOf"/>).</param>
    public static ClusterGroups Of(ClusterStatus status, IEnumerable<EnvironmentInfo> environments, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(environments);
        var named = environments.Where(environment => environment.Namespace is not null).ToList();
        var taken = named.Select(environment => environment.Namespace!).ToHashSet(StringComparer.Ordinal);
        return new ClusterGroups(
            [.. named.Select(environment =>
                status.Namespaces.FirstOrDefault(space => string.Equals(space.Name, environment.Namespace, StringComparison.Ordinal)) is { } space
                    ? NamespaceGroup.Of(space, environment, asOf)
                    : new NamespaceGroup(environment.Namespace!, environment, false, [], []))],
            [.. status.Namespaces
                .Where(space => !taken.Contains(space.Name))
                .OrderBy(space => space.Name, StringComparer.OrdinalIgnoreCase)
                .Select(space => NamespaceGroup.Of(space, null, asOf))]);
    }
}

/// <summary>The sums of the cluster card.</summary>
/// <param name="PodsReady">The pods that are ready.</param>
/// <param name="Pods">The pods that count: every pod but the finished ones.</param>
/// <param name="PodsFinished">The finished jobs.</param>
/// <param name="Cpu">Millicores over all nodes: used, requested and what the nodes offer.</param>
/// <param name="Memory">Bytes over all nodes.</param>
/// <param name="PodsOnNodes">The pods the nodes run, as the nodes count them.</param>
/// <param name="PodCapacity">How many the nodes can take.</param>
public sealed record ClusterTotals(
    int NodesReady,
    int Nodes,
    int PodsReady,
    int Pods,
    int PodsFinished,
    ResourceUse Cpu,
    ResourceUse Memory,
    int? PodsOnNodes,
    int? PodCapacity,
    int Restarts)
{
    /// <param name="asOf">The moment the facts are of (<see cref="ClusterStatus.AsOf"/>).</param>
    public static ClusterTotals Of(ClusterStatus status, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(status);
        var states = status.Pods.Select(entry => PodRules.StateOf(entry.Pod, asOf)).ToList();
        return new ClusterTotals(
            status.Nodes.Count(node => node.Ready),
            status.Nodes.Count,
            states.Count(state => state == PodState.Ready),
            states.Count(state => state != PodState.Finished),
            states.Count(state => state == PodState.Finished),
            Sum(status.Nodes.Select(node => node.Cpu)),
            Sum(status.Nodes.Select(node => node.Memory)),
            (int?)NamespaceGroup.Sum(status.Nodes.Select(node => (double?)node.Pods)),
            (int?)NamespaceGroup.Sum(status.Nodes.Select(node => (double?)node.PodCapacity)),
            status.Pods.Sum(entry => entry.Pod.Restarts));
    }

    private static ResourceUse Sum(IEnumerable<ResourceUse> uses)
    {
        var all = uses.ToList();
        return new ResourceUse(
            NamespaceGroup.Sum(all.Select(use => use.Usage)),
            NamespaceGroup.Sum(all.Select(use => use.Requests)),
            NamespaceGroup.Sum(all.Select(use => use.Limits)),
            NamespaceGroup.Sum(all.Select(use => use.Allocatable)));
    }
}

/// <summary>What the page keeps of one reading of the cluster for its trends: the CPU and the memory the nodes use.</summary>
/// <param name="CpuMillicores">Millicores used over all nodes; null when no node reports a usage.</param>
/// <param name="MemoryBytes">Bytes used over all nodes.</param>
public sealed record ClusterSample(double? CpuMillicores, double? MemoryBytes)
{
    public static ClusterSample Of(ClusterStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new ClusterSample(
            NamespaceGroup.Sum(status.Nodes.Select(node => node.Cpu.Usage)),
            NamespaceGroup.Sum(status.Nodes.Select(node => node.Memory.Usage)));
    }
}
