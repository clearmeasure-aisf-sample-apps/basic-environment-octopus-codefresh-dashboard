using Dashboard.Health;

namespace Dashboard.Cluster;

/// <summary>
/// The cluster view's part of the page's monitor, one per cluster of the topology: the last reading of the cluster's
/// two files, the readings the trends are drawn from and the events between two readings. It has no timer of its own:
/// the page's round of checks reads it.
/// </summary>
public sealed class ClusterMonitor
{
    private readonly ClusterReader _reader;
    private readonly TimeProvider _time;
    private readonly EventLog _events;
    private readonly string _clusterPlace;
    private readonly string _servicePlace;
    private ClusterLiveness _liveness = ClusterLiveness.Pending;
    private ClusterStatus? _lastStatus;
    private AksService? _lastService;

    /// <param name="environments">
    /// The environments of the topology: their namespaces group the pods. Of them the cluster shows those it hosts
    /// (<c>environments</c> of the cluster), or all where the topology does not say.
    /// </param>
    /// <param name="events">Where the monitor writes what it observes: the page's log.</param>
    /// <param name="label">
    /// What the cluster is called in its events where the topology has several clusters; null for the only cluster,
    /// whose events name no cluster.
    /// </param>
    public ClusterMonitor(
        ClusterInfo info,
        IReadOnlyList<EnvironmentInfo> environments,
        ClusterReader reader,
        TimeProvider time,
        EventLog events,
        string? label = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(environments);
        Info = info;
        Environments = info.Environments is null ? environments : [.. environments.Where(environment => info.Hosts(environment.Name))];
        _clusterPlace = ClusterEventDetector.ClusterPlaceOf(label);
        _servicePlace = ClusterEventDetector.ServicePlaceOf(label);
        _reader = reader;
        _time = time;
        _events = events;
        Status = info.StatusUrl is null ? null : new SourceReading<ClusterStatus>(SourceState.Pending);
        Service = info.ServiceUrl is null ? null : new SourceReading<AksService>(SourceState.Pending);
    }

    /// <summary>Raised when a file was read.</summary>
    public event Action? Changed;

    public ClusterInfo Info { get; }

    /// <summary>
    /// The environments whose pods the cluster view lists under their names, in the topology's order: the ones the
    /// cluster hosts, or every environment of the topology for a cluster that does not say which it hosts.
    /// </summary>
    public IReadOnlyList<EnvironmentInfo> Environments { get; }

    /// <summary>
    /// The cluster's sleep at the page's clock: not null while Azure's facts, read and not old, say the cluster is
    /// stopped (<see cref="ClusterSleep.Of"/>).
    /// </summary>
    public ClusterSleep? Sleep => ClusterSleep.Of(Info, Service, _time.GetUtcNow());

    /// <summary>
    /// The last reading of the cluster's own status; a failed reading replaces a good one. Null when the topology
    /// names no <c>statusUrl</c>: nothing is read.
    /// </summary>
    public SourceReading<ClusterStatus>? Status { get; private set; }

    /// <summary>
    /// The last reading of Azure's facts about the AKS service; a failed reading replaces a good one. Null when the
    /// topology names no <c>serviceUrl</c>: nothing is read.
    /// </summary>
    public SourceReading<AksService>? Service { get; private set; }

    /// <summary>
    /// The last <see cref="Trend.Length"/> readings of the cluster's CPU and memory, oldest first, for the trend
    /// lines: one per check, null where the status was not read or was stale. Nothing is stored beyond the page.
    /// </summary>
    public HistoryBuffer<ClusterSample?> Samples { get; } = new(Trend.Length);

    /// <summary>The trend of the CPU the nodes use, in cores; null with fewer than two readings.</summary>
    public Trend? CpuTrend => Trend.Of(Samples.Select(sample => sample?.CpuMillicores / 1000), "CPU used in the cluster", " cores");

    /// <summary>The trend of the memory the nodes use, in GiB; null with fewer than two readings.</summary>
    public Trend? MemoryTrend => Trend.Of(Samples.Select(sample => sample?.MemoryBytes / (1024d * 1024 * 1024)), "Memory used in the cluster", " GiB");

    /// <summary>Reads both files at the same time; each is recorded as it arrives.</summary>
    public Task CheckAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(ReadStatusAsync(cancellationToken), ReadServiceAsync(cancellationToken));

    private async Task ReadStatusAsync(CancellationToken cancellationToken)
    {
        if (Info.StatusUrl is not { } address)
        {
            return;
        }

        var reading = await _reader.ReadStatusAsync(address, cancellationToken);
        var now = _time.GetUtcNow();
        var liveness = ClusterAssessment.LivenessOf(reading, now);
        if (ClusterEventDetector.Liveness(_liveness, liveness, reading.Detail, now, _clusterPlace) is { } change)
        {
            _events.Add(change);
        }

        if (reading.Value is { } status)
        {
            // Compared with the last status that was read: a reading that failed in between hides no change.
            if (_lastStatus is { } known)
            {
                _events.AddRange(ClusterEventDetector.Status(known, status, Environments, now, _clusterPlace));
            }

            _lastStatus = status;
        }

        // A check without a live status is a gap in the trend, once there is a trend.
        var sample = liveness == ClusterLiveness.Live && reading.Value is { } live ? ClusterSample.Of(live) : null;
        if (sample is not null || Samples.Count > 0)
        {
            Samples.Add(sample);
        }

        _liveness = liveness;
        Status = reading;
        Changed?.Invoke();
    }

    private async Task ReadServiceAsync(CancellationToken cancellationToken)
    {
        if (Info.ServiceUrl is not { } address)
        {
            return;
        }

        var reading = await _reader.ReadServiceAsync(address, cancellationToken);
        if (reading.Value is { } service)
        {
            if (_lastService is { } known)
            {
                _events.AddRange(ClusterEventDetector.Service(known, service, _time.GetUtcNow(), _servicePlace));
            }

            _lastService = service;
        }

        Service = reading;
        Changed?.Invoke();
    }
}
