using Dashboard.Cluster;

namespace Dashboard.Health;

/// <summary>The state of every endpoint of a topology, and the action that checks them all.</summary>
public sealed class DashboardMonitor
{
    /// <summary>How often the delivery facts are read: they change with a deployment, and GitHub caches the file for minutes.</summary>
    public static readonly TimeSpan DeliveryInterval = TimeSpan.FromMinutes(5);

    private readonly NodeProber _prober;
    private readonly PinnedVersionsReader _versions;
    private readonly TimeProvider _time;
    private readonly List<(EnvironmentStatus Environment, DeployableStatus Deployable, TargetStatus Target)> _targets;
    private DateTimeOffset? _deliveryReadAt;

    /// <param name="events">
    /// Where the monitor writes what it observes; the page keeps one log across reloads of the topology. A log of its
    /// own without one.
    /// </param>
    /// <param name="cluster">
    /// What reads the cluster's files, for a topology with <c>cluster</c>; without it, or without a cluster in the
    /// topology, the monitor has no cluster and reads nothing for one.
    /// </param>
    public DashboardMonitor(
        Topology topology,
        NodeProber prober,
        PinnedVersionsReader versions,
        TimeProvider time,
        EventLog? events = null,
        ClusterReader? cluster = null)
    {
        ArgumentNullException.ThrowIfNull(topology);
        Topology = topology;
        _prober = prober;
        _versions = versions;
        _time = time;
        Events = events ?? new EventLog();
        Environments = [.. topology.Environments.Select(environment => new EnvironmentStatus(environment))];
        _targets =
        [
            .. from environment in Environments
               from deployable in environment.Deployables
               from target in deployable.Targets
               select (environment, deployable, target),
        ];
        if (topology.Cluster is { } info && cluster is not null)
        {
            Cluster = new ClusterMonitor(info, topology.Environments, cluster, time, Events);
            Cluster.Changed += () => Changed?.Invoke();
        }
    }

    /// <summary>Raised whenever an endpoint's state changed and when a round of checks ended.</summary>
    public event Action? Changed;

    public Topology Topology { get; }

    public IReadOnlyList<EnvironmentStatus> Environments { get; }

    public IEnumerable<TargetStatus> Targets => _targets.Select(entry => entry.Target);

    public HealthSummary Summary => HealthSummary.Of(Targets.Select(target => target.State));

    /// <summary>In how many environments a node runs another version than the one pinned in Git.</summary>
    public VersionSummary VersionSummary => new(Environments.Count(environment => environment.VersionsDiffer));

    /// <summary>When the last round of checks ended; null before the first one.</summary>
    public DateTimeOffset? LastRefresh { get; private set; }

    /// <summary>What the page observed: state changes, restarts, deployments, failovers, pins.</summary>
    public EventLog Events { get; }

    /// <summary>
    /// The system's delivery facts, as last read; null when the topology names no <c>deliveryUrl</c> or the file was
    /// never read. A reading that fails keeps the last good one: the file says when it was generated.
    /// </summary>
    public DeliveryReport? Delivery { get; private set; }

    /// <summary>
    /// The cluster the system runs in, read with every round of checks; null when the topology names none, and the
    /// page then has no cluster view.
    /// </summary>
    public ClusterMonitor? Cluster { get; }

    /// <summary>The environment the others are compared with: the first of the topology.</summary>
    public string? FirstEnvironment => Topology.Environments.Count > 0 ? Topology.Environments[0].Name : null;

    /// <summary>
    /// Checks every endpoint at the same time. Each result is recorded as it arrives, so a node that hangs until its
    /// timeout delays neither the others nor their display. The pinned versions are read at the same time, once per
    /// environment, and once per deployable that has a pin of its own (<c>pinUrl</c>): a file that cannot be read is a
    /// result like any other and fails no check. So are the delivery facts, every <see cref="DeliveryInterval"/>, and
    /// the two files of the cluster view, every round, where the topology names a cluster.
    /// </summary>
    public async Task CheckAllAsync(ProbeKind probe, CancellationToken cancellationToken)
    {
        var checks = _targets.Select(entry => CheckAsync(entry.Environment, entry.Deployable, entry.Target, probe, cancellationToken));
        var readings = Environments.Select(environment => ReadPinnedVersionsAsync(environment, cancellationToken));
        var pins =
            from environment in Environments
            from deployable in environment.Deployables
            where deployable.Info.PinUrl is not null
            select ReadPinAsync(environment, deployable, cancellationToken);
        await Task.WhenAll(checks.Concat(readings).Concat(pins)
            .Append(ReadDeliveryAsync(cancellationToken))
            .Append(Cluster?.CheckAsync(cancellationToken) ?? Task.CompletedTask));
        var now = _time.GetUtcNow();
        foreach (var environment in Environments)
        {
            foreach (var deployable in environment.Deployables)
            {
                // The serving decision is compared once per round, when every node has its result: in the middle of a
                // round it would mix this round's answers with the last one's.
                var assessment = deployable.Assess();
                if (EventDetector.Serving(deployable.LastServing, assessment, environment.Name, deployable.Info.Name, now) is { } change)
                {
                    Events.Add(change);
                }

                deployable.RecordServing(assessment);
            }
        }

        LastRefresh = now;
        Changed?.Invoke();
    }

    private async Task ReadPinnedVersionsAsync(EnvironmentStatus environment, CancellationToken cancellationToken)
    {
        if (environment.Info.VersionsUrl is not { } address)
        {
            return;
        }

        var before = environment.Pinned;
        var after = await _versions.ReadAsync(address, cancellationToken);
        environment.Record(after);
        if (after.State == PinnedVersionsState.Read)
        {
            // Compared with the last good reading: a reading that failed in between hides no change.
            var known = environment.LastReadPinned ?? before;
            var now = _time.GetUtcNow();
            Events.AddRange(environment.Deployables
                // A deployable with a pin of its own is not pinned in this file.
                .Where(deployable => deployable.Info.PinUrl is null)
                .Select(deployable => EventDetector.Pinned(known, after, environment.Name, deployable.Info.Name, now))
                .OfType<DashboardEvent>());
            environment.RememberPinned(after);
        }

        Changed?.Invoke();
    }

    /// <summary>Reads the pin of a deployable that has one of its own: its Kustomize file, by the rules of <c>versions.json</c>.</summary>
    private async Task ReadPinAsync(EnvironmentStatus environment, DeployableStatus deployable, CancellationToken cancellationToken)
    {
        if (deployable.Info.PinUrl is not { } address)
        {
            return;
        }

        var before = deployable.Pinned;
        var after = await _versions.ReadKustomizationAsync(address, deployable.Info.Name, cancellationToken);
        deployable.RecordPin(after);
        if (after.State == PinnedVersionsState.Read)
        {
            // Compared with the last good reading: a reading that failed in between hides no change.
            if ((deployable.LastReadPinned ?? before) is { } known
                && EventDetector.Pinned(known, after, environment.Name, deployable.Info.Name, _time.GetUtcNow()) is { } change)
            {
                Events.Add(change);
            }

            deployable.RememberPin(after);
        }

        Changed?.Invoke();
    }

    private async Task ReadDeliveryAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (Topology.System.DeliveryUrl is not { } address || (_deliveryReadAt is { } last && now - last < DeliveryInterval))
        {
            return;
        }

        _deliveryReadAt = now;
        if (await _prober.ReadDeliveryAsync(address, cancellationToken) is { } report)
        {
            Delivery = report;
            Changed?.Invoke();
        }
    }

    private async Task CheckAsync(EnvironmentStatus environment, DeployableStatus status, TargetStatus target, ProbeKind probe, CancellationToken cancellationToken)
    {
        var deployable = status.Info;

        // A regional node also reports its calls; a Front Door address would only answer for the node behind it.
        var telemetry = target.Kind == TargetKind.Node && deployable.TelemetryPath is { } path
            ? _prober.ReadTelemetryAsync(target.Url, path, cancellationToken)
            : Task.FromResult<TelemetrySnapshot?>(null);
        var result = await _prober.ProbeAsync(target.Url, deployable.PathFor(probe), deployable.VersionPath, cancellationToken);
        var before = NodeObservation.Of(target);
        target.Record(result with { Probe = probe });
        target.RecordTelemetry(await telemetry);
        Events.AddRange(EventDetector.Node(
            // A check without telemetry (the app was down) is compared with the last reading that had some.
            before with { Telemetry = before.Telemetry ?? target.Samples.Reverse().Skip(1).FirstOrDefault(sample => sample is not null) },
            NodeObservation.Of(target),
            environment.Name,
            deployable.Name,
            target.Kind == TargetKind.FrontDoor ? "Front Door" : target.Region ?? target.Name,
            target.Kind == TargetKind.Node,
            _time.GetUtcNow()));
        Changed?.Invoke();

        // The build facts change only with a deployment: asked once, and again when the node reports another version.
        if (ReferenceEquals(target, status.BuildSource) && deployable.BuildPath is { } buildPath
            && result.State != HealthState.Unreachable && status.BuildIsDue(target.Version))
        {
            var version = target.Version;
            status.RecordBuild(await _prober.ReadBuildAsync(target.Url, buildPath, cancellationToken), version);
            Changed?.Invoke();
        }
    }
}
