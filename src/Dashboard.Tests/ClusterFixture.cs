namespace Dashboard.Tests;

/// <summary>The cluster view's two sample files, and the parts of a status the tests build their own from.</summary>
internal static class ClusterFixture
{
    /// <summary>The moment the sample <c>cluster.json</c> was written.</summary>
    public static readonly DateTimeOffset Generated = new(2026, 10, 6, 20, 15, 30, TimeSpan.Zero);

    public static string Sample(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "cluster.sample", file));

    public static ClusterStatus SampleStatus => ClusterStatus.Parse(Sample("cluster.json")).Value!;

    public static AksService SampleService => AksService.Parse(Sample("aks.json")).Value!;

    public static ClusterNode Node(string name = "aks-system-12148983-vmss000000", bool ready = true, params string[] pressures) =>
        new(name, ready, pressures, false, "system", "Standard_D4as_v6", null, "v1.33.3", Generated.AddDays(-3), new ResourceUse(812, 2450, 9100, 3860), new ResourceUse(9876543210, 6200000000, 11800000000, 13400000000), 63, 110);

    /// <param name="age">How long before <see cref="Generated"/> the pod started; null for a pod that does not say.</param>
    public static ClusterPod Pod(
        string workload,
        string phase = ClusterPod.Running,
        bool ready = true,
        int restarts = 0,
        string? reason = null,
        TimeSpan? age = null,
        string? name = null,
        bool started = true) =>
        new(
            name ?? $"{workload}-0",
            workload,
            "Deployment",
            "aks-system-12148983-vmss000000",
            phase,
            ready,
            1,
            ready ? 1 : 0,
            restarts,
            reason,
            started ? Generated - (age ?? TimeSpan.FromHours(1)) : null,
            new ResourceUse(10, 100, 500),
            new ResourceUse(100 * 1024 * 1024, 268435456, 536870912));

    public static ClusterNamespace Space(string name, params ClusterPod[] pods) => new(name, pods, []);

    public static ClusterStatus Status(IReadOnlyList<ClusterNode>? nodes = null, params ClusterNamespace[] spaces) =>
        new(Generated, 15, "v1.33.3", nodes ?? [Node()], spaces);

    public static SourceReading<ClusterStatus> Read(ClusterStatus status) => new(SourceState.Read, status);

    public static SourceReading<AksService> Read(AksService service) => new(SourceState.Read, service);

    public static EnvironmentInfo Environment(string name, string? space, string? tier = "nonprod") =>
        new(name, tier, [], Namespace: space);
}
