using System.Net;

namespace Dashboard.Tests;

public class ClusterMonitorTests
{
    private const string StatusUrl = "https://cluster.example.net/cluster.json";
    private const string ServiceUrl = "https://raw.example.net/org/repo/cluster-status/aks.json";

    private const string WithCluster = """
        {
          "system": { "slug": "demo", "name": "Demo" },
          "environments": [
            { "name": "tdd", "namespace": "cmdemo3-tdd", "deployables": [ { "name": "ui", "nodes": [ { "name": "tdd/ui", "url": "https://tdd.example.net" } ] } ] }
          ],
          "cluster": { "name": "aks-demo", "statusUrl": "https://cluster.example.net/cluster.json",
                       "serviceUrl": "https://raw.example.net/org/repo/cluster-status/aks.json" }
        }
        """;

    private readonly SignallingTimeProvider _time = new();

    private DashboardMonitor Monitor(StubHandler handler, string topology = WithCluster, bool withReader = true)
    {
        var http = new HttpClient(handler);
        return new DashboardMonitor(
            TopologyParser.Parse(topology).Topology!,
            new NodeProber(http, _time),
            new PinnedVersionsReader(http, _time),
            _time,
            cluster: withReader ? new ClusterReader(http, _time) : null);
    }

    /// <summary>A status the collector wrote at the test's clock: the sample, or one of the test's own.</summary>
    private string Status(Func<ClusterStatus, string>? write = null) =>
        (write?.Invoke(ClusterFixture.SampleStatus) ?? ClusterFixture.Sample("cluster.json"))
            .Replace("2026-10-06T20:15:30Z", TimeText.Iso(_time.GetUtcNow()), StringComparison.Ordinal);

    private static HttpResponseMessage Answer(HttpRequestMessage request, string? status, string? service) =>
        request.RequestUri!.AbsoluteUri switch
        {
            StatusUrl => status is null ? StubHandler.Answer(HttpStatusCode.NotFound) : StubHandler.Answer(HttpStatusCode.OK, status),
            ServiceUrl => service is null ? StubHandler.Answer(HttpStatusCode.NotFound) : StubHandler.Answer(HttpStatusCode.OK, service),
            _ => StubHandler.Answer(HttpStatusCode.OK, request.RequestUri.AbsolutePath == "/_version" ? """{"version":"2.4.21"}""" : "Healthy"),
        };

    private static IEnumerable<string> ClusterEvents(DashboardMonitor monitor) =>
        monitor.Events.Newest.Where(entry => entry.Kind == EventKind.Cluster).Select(entry => entry.Text);

    [Fact]
    public async Task EveryRoundReadsBothFilesWithoutTheBrowsersCache()
    {
        var handler = new StubHandler(request => Answer(request, Status(), ClusterFixture.Sample("aks.json")));
        var monitor = Monitor(handler);

        Assert.Equal(SourceState.Pending, monitor.Cluster!.Status!.State);
        Assert.Equal(SourceState.Pending, monitor.Cluster.Service!.State);
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Equal(2, handler.RequestedUrls.Count(address => address == StatusUrl));
        Assert.Equal(2, handler.RequestedUrls.Count(address => address == ServiceUrl));
        Assert.All(
            handler.Requests.Where(request => request.RequestUri!.Host != "tdd.example.net"),
            request => Assert.Equal("no-store", NodeProberTests.FetchOption(request, "cache")));
        Assert.Equal(SourceState.Read, monitor.Cluster.Status!.State);
        Assert.Equal(6, monitor.Cluster.Status.Value!.Pods.Count());
        Assert.Equal("Available", monitor.Cluster.Service!.Value!.Availability!.State);
        Assert.Equal("aks-demo", monitor.Cluster.Info.Name);
    }

    [Fact]
    public async Task ATopologyWithoutAClusterHasNoClusterAndReadsNothingForOne()
    {
        var handler = new StubHandler(request => Answer(request, null, null));
        var monitor = Monitor(handler, WithCluster[..WithCluster.IndexOf("\"cluster\"", StringComparison.Ordinal)].TrimEnd().TrimEnd(',') + "}");

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Null(monitor.Cluster);
        Assert.Equal(["https://tdd.example.net/_healthcheck", "https://tdd.example.net/_version"], handler.RequestedUrls.Order());
        Assert.Empty(monitor.Events.Newest);
    }

    [Fact]
    public void AMonitorWithoutAReaderHasNoClusterEither()
    {
        Assert.Null(Monitor(new StubHandler(request => Answer(request, null, null)), withReader: false).Cluster);
    }

    [Fact]
    public async Task AnAddressTheTopologyDoesNotNameIsNotRead()
    {
        var handler = new StubHandler(request => Answer(request, Status(), null));
        var monitor = Monitor(handler, WithCluster.Replace(""" "serviceUrl": "https://raw.example.net/org/repo/cluster-status/aks.json" """.Trim(), "\"serviceUrl\": null", StringComparison.Ordinal));

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Null(monitor.Cluster!.Service);
        Assert.Equal(SourceState.Read, monitor.Cluster.Status!.State);
        Assert.DoesNotContain(ServiceUrl, handler.RequestedUrls);
    }

    [Fact]
    public async Task AFileThatIsNotThereIsMissingAndNoExceptionAndNoEventAtTheFirstRound()
    {
        var monitor = Monitor(new StubHandler(request => Answer(request, null, null)));

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Equal(new SourceReading<ClusterStatus>(SourceState.Missing, Detail: "the address answered HTTP 404"), monitor.Cluster!.Status);
        Assert.Equal(SourceState.Missing, monitor.Cluster.Service!.State);
        Assert.Empty(ClusterEvents(monitor));
        Assert.Empty(monitor.Cluster.Samples);
    }

    [Fact]
    public async Task AnAnswerThatCannotBeReadIsAReadingWithItsReason()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsoluteUri switch
        {
            StatusUrl => throw new HttpRequestException("Failed to fetch"),
            ServiceUrl => StubHandler.Answer(HttpStatusCode.OK, "<!DOCTYPE html>"),
            _ => Answer(request, null, null),
        });
        var monitor = Monitor(handler);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Equal(
            new SourceReading<ClusterStatus>(SourceState.Unavailable, Detail: "the browser could not read an answer (network or CORS)"),
            monitor.Cluster!.Status);
        Assert.Equal(new SourceReading<AksService>(SourceState.Malformed, Detail: "the file is not valid JSON"), monitor.Cluster.Service);
    }

    [Fact]
    public async Task AnotherStatusAndAnAnswerThatNeverComesAreUnavailable()
    {
        var reader = new ClusterReader(
            new HttpClient(new StubHandler((request, cancellation) => request.RequestUri!.AbsoluteUri == StatusUrl
                ? StubHandler.NeverAsync(cancellation)
                : Task.FromResult(StubHandler.Answer(HttpStatusCode.ServiceUnavailable)))),
            _time);

        var status = reader.ReadStatusAsync(new Uri(StatusUrl), CancellationToken.None);
        await _time.TimerCreatedAsync();
        _time.Advance(NodeProber.DefaultTimeout);

        Assert.Equal(new SourceReading<ClusterStatus>(SourceState.Unavailable, Detail: "no answer within 10 s"), await status);
        Assert.Equal(
            new SourceReading<AksService>(SourceState.Unavailable, Detail: "the address answered HTTP 503"),
            await reader.ReadServiceAsync(new Uri(ServiceUrl), CancellationToken.None));
    }

    [Fact]
    public async Task TheStatusFileThatStopsAnsweringAndAnswersAgainIsObserved()
    {
        string? status = Status();
        var monitor = Monitor(new StubHandler(request => Answer(request, status, null)));

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        status = null;
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        status = Status();
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Equal(
            ["The cluster's status file stopped answering: the address answered HTTP 404", "The cluster's status file answers again"],
            ClusterEvents(monitor));
    }

    [Fact]
    public async Task ACollectorThatStopsWritingMakesTheStatusStale()
    {
        var status = Status();
        var monitor = Monitor(new StubHandler(request => Answer(request, status, null)));

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(61));
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Equal(["The collector in the cluster stopped writing: the status file is stale"], ClusterEvents(monitor));
        Assert.Equal(ClusterLiveness.Stale, ClusterAssessment.LivenessOf(monitor.Cluster!.Status!, _time.GetUtcNow()));
    }

    [Fact]
    public async Task ARestartIsFoundAgainstTheLastStatusThatWasReadAlsoAcrossAFailedReading()
    {
        var status = Status();
        var monitor = Monitor(new StubHandler(request => Answer(request, status, null)));

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        Assert.Empty(ClusterEvents(monitor));
        status = null;
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        status = Status().Replace("\"restarts\": 7", "\"restarts\": 9", StringComparison.Ordinal);
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        var restart = monitor.Events.Newest.Single(entry => entry.Text.Contains("restarted", StringComparison.Ordinal));
        Assert.Equal(
            new DashboardEvent(_time.GetUtcNow(), EventKind.Cluster, EventLevel.Warning, "tdd", "cluster", "ui in cmdemo3-tdd restarted (9 restarts): CrashLoopBackOff"),
            restart);
    }

    [Fact]
    public async Task AChangeOfAzuresFactsIsObserved()
    {
        var service = ClusterFixture.Sample("aks.json");
        var monitor = Monitor(new StubHandler(request => Answer(request, Status(), service)));

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        service = service.Replace("\"powerState\": \"Running\",", "\"powerState\": \"Stopped\",", StringComparison.Ordinal);
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Equal(["The power state of the AKS service: Running → Stopped"], ClusterEvents(monitor));
        Assert.True(monitor.Cluster!.Service!.Value!.IsStopped);
    }

    [Fact]
    public async Task TheTrendsAreDrawnFromTheReadingsThisPageHasSeen()
    {
        string? status = Status();
        var monitor = Monitor(new StubHandler(request => Answer(request, status, null)));
        var cluster = monitor.Cluster!;

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        Assert.Null(cluster.CpuTrend);
        status = Status().Replace("\"usage\": 812", "\"usage\": 1624", StringComparison.Ordinal);
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        status = null;
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        // A check without a live status is a gap, not a zero.
        Assert.Equal([new ClusterSample(812, 9876543210), new ClusterSample(1624, 9876543210), null], cluster.Samples);
        Assert.Equal(new Trend([0.5, 1], "CPU used in the cluster, last 2 checks: 0.8 to 1.6 cores, now 1.6 cores"), cluster.CpuTrend, TrendComparer.Instance);
        Assert.Equal("Memory used in the cluster, last 2 checks: 9.2 to 9.2 GiB, now 9.2 GiB", cluster.MemoryTrend!.Title);
    }

    [Fact]
    public async Task AChangedFileRaisesChanged()
    {
        var monitor = Monitor(new StubHandler(request => Answer(request, Status(), ClusterFixture.Sample("aks.json"))));
        var changes = 0;
        monitor.Changed += () => Interlocked.Increment(ref changes);

        await monitor.Cluster!.CheckAsync(CancellationToken.None);

        Assert.Equal(2, changes);
    }

    private sealed class TrendComparer : IEqualityComparer<Trend?>
    {
        public static TrendComparer Instance { get; } = new();

        public bool Equals(Trend? x, Trend? y) =>
            x is not null && y is not null && x.Title == y.Title && x.Points.SequenceEqual(y.Points);

        public int GetHashCode(Trend? obj) => obj?.Title.GetHashCode(StringComparison.Ordinal) ?? 0;
    }
}
