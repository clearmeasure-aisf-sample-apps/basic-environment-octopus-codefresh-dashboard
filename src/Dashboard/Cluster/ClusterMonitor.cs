using Dashboard.Health;

namespace Dashboard.Cluster;

/// <summary>
/// The cluster view's part of the page's monitor: the last reading of the cluster's two files, the readings the
/// trends are drawn from and the events between two readings. It has no timer of its own: the page's round of checks
/// reads it.
/// </summary>
public sealed class ClusterMonitor
{
    private readonly IReadOnlyList<EnvironmentInfo> _environments;
    private readonly ClusterReader _reader;
    private readonly TimeProvider _time;
    private readonly EventLog _events;
    private ClusterLiveness _liveness = ClusterLiveness.Pending;
    private ClusterStatus? _lastStatus;
    private AksService? _lastService;

    /// <param name="environments">The environments of the topology: their namespaces group the pods.</param>
    /// <param name="events">Where the monitor writes what it observes: the page's log.</param>
    public ClusterMonitor(ClusterInfo info, IReadOnlyList<EnvironmentInfo> environments, ClusterReader reader, TimeProvider time, EventLog events)
    {
        ArgumentNullException.ThrowIfNull(info);
        Info = info;
        _environments = environments;
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
        if (ClusterEventDetector.Liveness(_liveness, liveness, reading.Detail, now) is { } change)
        {
            _events.Add(change);
        }

        if (reading.Value is { } status)
        {
            // Compared with the last status that was read: a reading that failed in between hides no change.
            if (_lastStatus is { } known)
            {
                _events.AddRange(ClusterEventDetector.Status(known, status, _environments, now));
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
                _events.AddRange(ClusterEventDetector.Service(known, service, _time.GetUtcNow()));
            }

            _lastService = service;
        }

        Service = reading;
        Changed?.Invoke();
    }
}
