namespace Dashboard.Health;

/// <summary>The system the dashboard shows: the content of <c>topology.json</c>.</summary>
/// <param name="Clusters">
/// The Kubernetes clusters the system runs in, for the cluster view: the one of <c>cluster</c>, or those of
/// <c>clusters</c> in the order of the file. Empty for a system without one, which is then shown without that view.
/// </param>
public sealed record Topology(SystemInfo System, DateTimeOffset? Generated, IReadOnlyList<EnvironmentInfo> Environments, IReadOnlyList<ClusterInfo> Clusters)
{
    /// <summary>
    /// True when a deployable of any environment has a Front Door endpoint: the page's help then speaks of Front Door.
    /// </summary>
    public bool HasFrontDoor => Environments.Any(environment => environment.Deployables.Any(deployable => deployable.FrontDoor is not null));

    /// <summary>
    /// True when a deployable of any environment reports its own counts (<c>telemetryPath</c>): the runtime view's help
    /// then speaks of calls per minute.
    /// </summary>
    public bool HasTelemetry => Environments.Any(environment => environment.Deployables.Any(deployable => deployable.TelemetryPath is not null));

    /// <summary>
    /// True when a cluster names the environments it hosts and where Azure's facts about it are read: such an
    /// environment can read as asleep, and the runtime view's help and legend then say what that looks like.
    /// </summary>
    public bool CanSleep => Clusters.Any(cluster => cluster is { ServiceUrl: not null, Environments.Count: > 0 });

    /// <summary>
    /// The cluster that hosts an environment: the one whose <c>environments</c> names it. Null for an environment no
    /// cluster names, and a cluster without <c>environments</c> claims none: the health view then knows no cluster
    /// behind the environment's nodes, and an unreachable node is a failure.
    /// </summary>
    public ClusterInfo? ClusterOf(string environment) =>
        Clusters.FirstOrDefault(cluster => cluster.Hosts(environment));
}

/// <param name="Slug">The system's short name.</param>
/// <param name="Name">The name the header shows.</param>
/// <param name="Repository">The system repository on GitHub, where the deployments pin the versions.</param>
/// <param name="DeliveryUrl">Where the browser reads the system's delivery facts (<c>delivery.json</c>); null without them.</param>
public sealed record SystemInfo(string Slug, string Name, Uri? Repository = null, Uri? DeliveryUrl = null);

/// <param name="Name">The environment's name.</param>
/// <param name="Tier">The tier, for example <c>nonprod</c>.</param>
/// <param name="Deployables">The deployables the dashboard checks in this environment.</param>
/// <param name="VersionsUrl">
/// Where the browser reads the environment's <c>versions.json</c>: the versions the deployments pinned in Git.
/// </param>
/// <param name="VersionsHistoryUrl">The page with the history of that file.</param>
/// <param name="Links">Where the environment's resources are (<see cref="LinkSet"/>); null without links.</param>
/// <param name="Namespace">
/// The namespace of the cluster that holds the environment's pods: the cluster view lists its pods under the
/// environment. Null where the system runs in no cluster.
/// </param>
public sealed record EnvironmentInfo(
    string Name,
    string? Tier,
    IReadOnlyList<DeployableInfo> Deployables,
    Uri? VersionsUrl = null,
    Uri? VersionsHistoryUrl = null,
    LinkSet? Links = null,
    string? Namespace = null);

/// <summary>
/// A Kubernetes cluster of the system (<c>cluster</c>, or an entry of <c>clusters</c>, of <c>topology.json</c>): the
/// two public files the cluster view reads, where the cluster is in the Azure portal and which environments it hosts.
/// Every part is optional.
/// </summary>
/// <param name="Name">The cluster's name; null when the topology does not say.</param>
/// <param name="StatusUrl">
/// Where the browser reads the live status a collector inside the cluster writes (<c>cluster.json</c>): nodes,
/// namespaces, pods and volumes.
/// </param>
/// <param name="ServiceUrl">
/// Where the browser reads Azure's own facts about the AKS service (<c>aks.json</c>), which a scheduled workflow
/// publishes.
/// </param>
/// <param name="Links">Where the cluster is in the Azure portal (<see cref="LinkSet"/>); null without links.</param>
/// <param name="Environments">
/// The names of the environments the cluster hosts (<c>environments</c>); null when the topology does not say. With
/// it, the cluster view lists the pods of these environments only, and the health view reads an environment as asleep
/// while Azure reports its cluster stopped. Without it the cluster view groups the pods by every environment of the
/// topology, and the health view knows no cluster behind an environment.
/// </param>
public sealed record ClusterInfo(string? Name, Uri? StatusUrl = null, Uri? ServiceUrl = null, LinkSet? Links = null, IReadOnlyList<string>? Environments = null)
{
    /// <summary>True when the cluster's <c>environments</c> names the environment, by its exact name.</summary>
    public bool Hosts(string environment) => Environments is { } hosted && hosted.Contains(environment, StringComparer.Ordinal);

    /// <summary>
    /// A record compares a list by reference; two clusters are the same when they name the same environments in the
    /// same order.
    /// </summary>
    public bool Equals(ClusterInfo? other) =>
        other is not null
        && Name == other.Name
        && StatusUrl == other.StatusUrl
        && ServiceUrl == other.ServiceUrl
        && EqualityComparer<LinkSet?>.Default.Equals(Links, other.Links)
        && (Environments is null ? other.Environments is null : other.Environments is not null && Environments.SequenceEqual(other.Environments, StringComparer.Ordinal));

    public override int GetHashCode() => HashCode.Combine(Name, StatusUrl, ServiceUrl, Links, Environments?.Count);
}

/// <summary>
/// One deployable of an environment: its public address (Front Door), the nodes behind it and the page of the project
/// that deploys it (<c>ProjectUrl</c>, in Octopus Deploy).
/// </summary>
/// <param name="TelemetryPath">
/// Where a node reports its own calls of the last minute (<c>/_telemetry</c>); null when the app has no such endpoint.
/// </param>
/// <param name="TrafficPaths">The paths the traffic button calls: representative requests, the start page first.</param>
/// <param name="BuildPath">
/// Where the primary node reports the build it runs (<c>/_build</c>, see <see cref="BuildInfo"/>); null when the app
/// has no such endpoint.
/// </param>
/// <param name="Links">Where the deployable's resources are (<see cref="LinkSet"/>); null without links.</param>
/// <param name="PinUrl">
/// Where the browser reads the deployable's own pin: a Kustomize file whose first <c>newTag</c> is the version pinned
/// in Git. Null when the environment's <c>versions.json</c> holds the pin.
/// </param>
/// <param name="PinHistoryUrl">The page with the history of that file; null for the history of <c>versions.json</c>.</param>
public sealed record DeployableInfo(
    string Name,
    Uri? FrontDoor,
    string HealthPath,
    string AlivePath,
    string VersionPath,
    IReadOnlyList<NodeInfo> Nodes,
    Uri? ProjectUrl = null,
    string? TelemetryPath = null,
    IReadOnlyList<string>? TrafficPaths = null,
    string? BuildPath = null,
    LinkSet? Links = null,
    Uri? PinUrl = null,
    Uri? PinHistoryUrl = null)
{
    public const string DefaultHealthPath = "/_healthcheck";
    public const string DefaultAlivePath = "/alive";
    public const string DefaultVersionPath = "/_version";

    /// <summary>The name of the file that holds the deployable's pin: its kustomization's, or <c>versions.json</c>.</summary>
    public string PinFile => PinUrl is null ? PinnedVersions.FileName : PinnedVersions.FileOf(PinUrl);

    /// <summary>The path the chosen probe calls on every node of this deployable.</summary>
    public string PathFor(ProbeKind probe) => probe == ProbeKind.Liveness ? AlivePath : HealthPath;
}

/// <summary>One regional node (web app) of a deployable.</summary>
/// <param name="Links">Where the node's numbers lead (<see cref="LinkSet"/>); null without links.</param>
public sealed record NodeInfo(string Name, string? Region, string Role, Uri Url, LinkSet? Links = null)
{
    public const string PrimaryRole = "primary";
    public const string StandbyRole = "standby";

    public bool IsPrimary => string.Equals(Role, PrimaryRole, StringComparison.OrdinalIgnoreCase);
}
