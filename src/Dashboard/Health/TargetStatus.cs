namespace Dashboard.Health;

public enum TargetKind
{
    /// <summary>The public address of a deployable in an environment.</summary>
    FrontDoor,

    /// <summary>One regional web app behind the public address.</summary>
    Node,
}

/// <summary>One tile of the dashboard: an endpoint, its last check and the checks before it.</summary>
public sealed class TargetStatus(TargetKind kind, string name, Uri url, string? region = null, string? role = null, LinkSet? links = null)
{
    public const int HistoryLength = 30;

    /// <summary>Where the tile's numbers lead: a node's own links; for the Front Door endpoint, the deployable's.</summary>
    public LinkSet Links { get; } = links ?? LinkSet.None;

    public TargetKind Kind { get; } = kind;

    public string Name { get; } = name;

    public Uri Url { get; } = url;

    public string? Region { get; } = region;

    public string? Role { get; } = role;

    public bool IsPrimary => string.Equals(Role, NodeInfo.PrimaryRole, StringComparison.OrdinalIgnoreCase);

    /// <summary>The last <see cref="HistoryLength"/> checks, oldest first.</summary>
    public HistoryBuffer<ProbeResult> History { get; } = new(HistoryLength);

    public ProbeResult? Last { get; private set; }

    public HealthState State => Last?.State ?? HealthState.Pending;

    /// <summary>The version the endpoint last reported: a node that stops answering keeps its last known version.</summary>
    public string? Version { get; private set; }

    /// <summary>The node's own count of its calls at the last check; null when it reported none.</summary>
    public TelemetrySnapshot? Telemetry { get; private set; }

    /// <summary>
    /// The last <see cref="Trend.Length"/> readings of the node's telemetry, oldest first, for the sparklines: one per
    /// check, null where the node reported none.
    /// </summary>
    public HistoryBuffer<TelemetrySnapshot?> Samples { get; } = new(Trend.Length);

    /// <summary>Records the telemetry of one check. A node that never reported any keeps no readings.</summary>
    public void RecordTelemetry(TelemetrySnapshot? telemetry)
    {
        Telemetry = telemetry;
        if (telemetry is not null || Samples.Count > 0)
        {
            Samples.Add(telemetry);
        }
    }

    /// <summary>The trend of one number of the telemetry over the readings kept; null with fewer than two.</summary>
    public Trend? TrendOf(Func<TelemetrySnapshot, double?> number, string what, string unit = "")
    {
        ArgumentNullException.ThrowIfNull(number);
        return Trend.Of(Samples.Select(sample => sample is null ? null : number(sample)), what, unit);
    }

    public void Record(ProbeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Last = result;
        Version = result.Version ?? Version;
        History.Add(result);
    }

    public NodeHealth ToNodeHealth() => new(Name, Region, IsPrimary, State);

    public NodeVersion ToNodeVersion() => new(Region ?? Name, State, Version);
}

/// <summary>A deployable of an environment with the state of its endpoints.</summary>
public sealed class DeployableStatus
{
    public DeployableStatus(DeployableInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        Info = info;
        FrontDoor = info.FrontDoor is null ? null : new TargetStatus(TargetKind.FrontDoor, "Front Door", info.FrontDoor, links: info.Links);
        Nodes = [.. info.Nodes.Select(node => new TargetStatus(TargetKind.Node, node.Name, node.Url, node.Region, node.Role, node.Links))];
        BuildSource = info.BuildPath is null ? null : Nodes.FirstOrDefault(node => node.IsPrimary) ?? (Nodes.Count > 0 ? Nodes[0] : null);
        Pinned = info.PinUrl is null ? null : PinnedVersions.Pending;
    }

    /// <summary>
    /// The last reading of the deployable's own pin (<c>pinUrl</c>, a Kustomize file); a failed reading replaces a good
    /// one. Null when the topology names none: the environment's <c>versions.json</c> then holds the pin.
    /// </summary>
    public PinnedVersions? Pinned { get; private set; }

    /// <summary>The last reading of the deployable's own pin that succeeded: what a new reading is compared with to find a new pin.</summary>
    public PinnedVersions? LastReadPinned { get; private set; }

    public void RecordPin(PinnedVersions pinned)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        Pinned = pinned;
    }

    public void RememberPin(PinnedVersions pinned) => LastReadPinned = pinned;

    /// <summary>The node asked for the build facts: the primary; null when the topology names no <c>buildPath</c>.</summary>
    public TargetStatus? BuildSource { get; }

    /// <summary>The build the deployable runs in this environment, as its primary node reports it; null when it reports none.</summary>
    public BuildInfo? Build { get; private set; }

    /// <summary>True once the build endpoint was asked.</summary>
    public bool BuildWasRead { get; private set; }

    /// <summary>The version the node ran when the build endpoint was asked: another version is another build.</summary>
    public string? BuildReadForVersion { get; private set; }

    /// <summary>The serving decision at the end of the last round of checks; null before the first.</summary>
    public ServingAssessment? LastServing { get; private set; }

    /// <summary>
    /// True when the build endpoint should be asked now: it was not asked yet, or the node reports another version
    /// than when it was. Not every round: the answer changes only with a deployment.
    /// </summary>
    public bool BuildIsDue(string? version) =>
        !BuildWasRead || !string.Equals(version, BuildReadForVersion, StringComparison.OrdinalIgnoreCase);

    public void RecordBuild(BuildInfo? build, string? version)
    {
        Build = build;
        BuildWasRead = true;
        BuildReadForVersion = version;
    }

    public void RecordServing(ServingAssessment assessment) => LastServing = assessment;

    public DeployableInfo Info { get; }

    public TargetStatus? FrontDoor { get; }

    /// <summary>True when the deployable has a Front Door endpoint: what a tile says about Front Door is shown only then.</summary>
    public bool HasFrontDoor => FrontDoor is not null;

    public IReadOnlyList<TargetStatus> Nodes { get; }

    /// <summary>The Front Door endpoint first, then the nodes in the order of the topology.</summary>
    public IEnumerable<TargetStatus> Targets => FrontDoor is null ? Nodes : Nodes.Prepend(FrontDoor);

    public ServingAssessment Assess() => Assess(out _);

    /// <summary>The serving decision, and the tile of the node it expects to serve the traffic (if any).</summary>
    public ServingAssessment Assess(out TargetStatus? expected)
    {
        var healths = Nodes.Select(node => node.ToNodeHealth()).ToList();
        var assessment = ServingAssessment.Assess(healths, FrontDoor?.State);
        var index = healths.FindIndex(health => ReferenceEquals(health, assessment.Expected));
        expected = index >= 0 ? Nodes[index] : null;
        return assessment;
    }
}

/// <summary>An environment with the state of its deployables and the versions the deployments pinned for it in Git.</summary>
public sealed class EnvironmentStatus
{
    public EnvironmentStatus(EnvironmentInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        Info = info;
        Deployables = [.. info.Deployables.Select(deployable => new DeployableStatus(deployable))];
        Pinned = info.VersionsUrl is null ? PinnedVersions.NotTracked : PinnedVersions.Pending;
    }

    public EnvironmentInfo Info { get; }

    public string Name => Info.Name;

    public string? Tier => Info.Tier;

    public IReadOnlyList<DeployableStatus> Deployables { get; }

    /// <summary>The last reading of the environment's <c>versions.json</c>; a failed reading replaces a good one.</summary>
    public PinnedVersions Pinned { get; private set; }

    /// <summary>True when a deployable of this environment has a node that runs another version than the pinned one.</summary>
    public bool VersionsDiffer => Deployables.Any(deployable => AssessVersions(deployable) is { Differs: true });

    /// <summary>The last reading that succeeded: what a new reading is compared with to find a new pin.</summary>
    public PinnedVersions? LastReadPinned { get; private set; }

    public void Record(PinnedVersions pinned)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        Pinned = pinned;
    }

    public void RememberPinned(PinnedVersions pinned) => LastReadPinned = pinned;

    /// <summary>
    /// The pinned version of a deployable next to the versions its nodes run: from the deployable's own pin where the
    /// topology names one (<c>pinUrl</c>), from the environment's <c>versions.json</c> otherwise; null when the
    /// topology names neither.
    /// </summary>
    public VersionAssessment? AssessVersions(DeployableStatus deployable)
    {
        ArgumentNullException.ThrowIfNull(deployable);
        var nodes = deployable.Nodes.Select(node => node.ToNodeVersion());
        if (deployable.Pinned is { } own)
        {
            return VersionAssessment.Assess(own, deployable.Info.Name, nodes, deployable.Info.PinFile);
        }

        return Info.VersionsUrl is null ? null : VersionAssessment.Assess(Pinned, deployable.Info.Name, nodes);
    }

    /// <summary>
    /// The page with the history of a deployable's pin: the deployable's own (<c>pinHistoryUrl</c>), or else the
    /// history of the environment's <c>versions.json</c>; null without both.
    /// </summary>
    public Uri? PinHistoryOf(DeployableStatus deployable)
    {
        ArgumentNullException.ThrowIfNull(deployable);
        return deployable.Info.PinHistoryUrl ?? Info.VersionsHistoryUrl;
    }
}
