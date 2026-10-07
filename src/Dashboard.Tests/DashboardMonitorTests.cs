using System.Net;

namespace Dashboard.Tests;

public class DashboardMonitorTests
{
    private const string TwoEnvironments = """
        {
          "system": { "slug": "demo", "name": "Demo" },
          "environments": [
            { "name": "tdd", "deployables": [ {
                "name": "ui", "frontDoor": "https://fd-tdd.example.net",
                "healthPath": "/_healthcheck", "alivePath": "/alive", "versionPath": "/_version",
                "nodes": [ { "name": "tdd-west", "region": "westus3", "role": "primary", "url": "https://tdd-west.example.net" } ] } ] },
            { "name": "uat", "deployables": [ {
                "name": "ui", "frontDoor": "https://fd-uat.example.net",
                "healthPath": "/_healthcheck", "alivePath": "/alive", "versionPath": "/_version",
                "nodes": [
                  { "name": "uat-west", "region": "westus3", "role": "primary", "url": "https://uat-west.example.net" },
                  { "name": "uat-east", "region": "eastus2", "role": "standby", "url": "https://uat-east.example.net" } ] } ] }
          ]
        }
        """;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private readonly SignallingTimeProvider _time = new();

    private DashboardMonitor Monitor(StubHandler handler)
    {
        var http = new HttpClient(handler);
        return new DashboardMonitor(
            TopologyParser.Parse(TwoEnvironments).Topology!,
            new NodeProber(http, _time),
            new PinnedVersionsReader(http, _time),
            _time);
    }

    private static HttpResponseMessage Healthy(HttpRequestMessage request) =>
        StubHandler.Answer(HttpStatusCode.OK, request.RequestUri!.AbsolutePath == "/_version" ? """{"version":"2.4.21+sha"}""" : "Healthy");

    [Fact]
    public void EveryFrontDoorAndEveryNodeIsATarget()
    {
        var monitor = Monitor(new StubHandler(Healthy));

        Assert.Equal(["Front Door", "tdd-west", "Front Door", "uat-west", "uat-east"], monitor.Targets.Select(target => target.Name));
        Assert.Equal(["tdd", "uat"], monitor.Environments.Select(environment => environment.Name));
        Assert.Equal("Checking 5 nodes", monitor.Summary.Text);
        Assert.Null(monitor.LastRefresh);
    }

    [Fact]
    public async Task ARoundChecksEveryTargetAndStampsTheRefresh()
    {
        var monitor = Monitor(new StubHandler(Healthy));
        _time.Advance(TimeSpan.FromMinutes(3));

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.All(monitor.Targets, target =>
        {
            Assert.Equal(HealthState.Healthy, target.State);
            Assert.Equal("2.4.21", target.Version);
            Assert.Single(target.History);
        });
        Assert.Equal("All 5 nodes healthy", monitor.Summary.Text);
        Assert.Equal(_time.GetUtcNow(), monitor.LastRefresh);
    }

    [Theory]
    [InlineData(ProbeKind.Health, "/_healthcheck")]
    [InlineData(ProbeKind.Liveness, "/alive")]
    public async Task TheProbeDecidesWhichPathIsCalled(ProbeKind probe, string path)
    {
        var handler = new StubHandler(Healthy);

        await Monitor(handler).CheckAllAsync(probe, CancellationToken.None);

        var probed = handler.Requests.Select(request => request.RequestUri!).Where(address => address.AbsolutePath != "/_version").ToList();
        Assert.Equal(5, probed.Count);
        Assert.All(probed, address => Assert.Equal(path, address.AbsolutePath));
        Assert.Equal(5, handler.Requests.Count(request => request.RequestUri!.AbsolutePath == "/_version"));
    }

    [Fact]
    public async Task EachStateIsCountedInTheSummary()
    {
        var handler = new StubHandler(request => request.RequestUri!.Host switch
        {
            "uat-west.example.net" => throw new HttpRequestException("Failed to fetch"),
            "fd-tdd.example.net" => StubHandler.Answer(HttpStatusCode.ServiceUnavailable),
            _ => Healthy(request),
        });
        var monitor = Monitor(handler);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Equal(
            [HealthState.Unhealthy, HealthState.Healthy, HealthState.Healthy, HealthState.Unreachable, HealthState.Healthy],
            monitor.Targets.Select(target => target.State));
        Assert.Equal("2 of 5 nodes not healthy", monitor.Summary.Text);

        var uat = monitor.Environments[1].Deployables[0].Assess(out var expected);
        Assert.Equal("Failed over to eastus2", uat.Headline);
        Assert.Equal(FrontDoorAgreement.Agrees, uat.FrontDoor);
        Assert.Equal("uat-east", expected?.Name);
    }

    [Fact]
    public async Task ANodeThatHangsDoesNotBlockTheOthers()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler(async (request, _) =>
        {
            if (request.RequestUri!.Host == "uat-west.example.net")
            {
                await release.Task;
            }

            return Healthy(request);
        });
        var monitor = Monitor(handler);
        var othersAnswered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.Changed += () =>
        {
            if (monitor.Targets.Count(target => target.State == HealthState.Healthy) == 4)
            {
                othersAnswered.TrySetResult();
            }
        };

        var round = monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        await othersAnswered.Task.WaitAsync(Patience);

        // The four other targets are shown although the round has not ended.
        Assert.False(round.IsCompleted);
        Assert.Equal(HealthState.Pending, monitor.Targets.Single(target => target.Name == "uat-west").State);
        Assert.Equal("4 of 5 nodes healthy, 1 being checked", monitor.Summary.Text);
        Assert.Null(monitor.LastRefresh);

        release.SetResult();
        await round.WaitAsync(Patience);

        Assert.Equal("All 5 nodes healthy", monitor.Summary.Text);
        Assert.NotNull(monitor.LastRefresh);
    }

    [Fact]
    public async Task ANodeThatTimesOutIsUnreachableAndTheOthersKeepTheirResult()
    {
        var handler = new StubHandler((request, cancellationToken) => request.RequestUri!.Host == "uat-east.example.net"
            ? StubHandler.NeverAsync(cancellationToken)
            : Task.FromResult(Healthy(request)));
        var monitor = Monitor(handler);

        var round = monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        _time.Advance(NodeProber.DefaultTimeout);
        await round.WaitAsync(Patience);

        Assert.Equal(HealthState.Unreachable, monitor.Targets.Single(target => target.Name == "uat-east").State);
        Assert.Equal("1 of 5 nodes not healthy", monitor.Summary.Text);
    }

    [Fact]
    public async Task ChangedIsRaisedForEveryTargetAndForTheEndOfTheRound()
    {
        var monitor = Monitor(new StubHandler(Healthy));
        var changes = 0;
        monitor.Changed += () => Interlocked.Increment(ref changes);

        await monitor.CheckAllAsync(ProbeKind.Liveness, CancellationToken.None);

        Assert.Equal(6, changes);
    }

    [Fact]
    public async Task ATopologyWithoutPinnedVersionsAsksOnlyTheNodes()
    {
        var handler = new StubHandler(Healthy);
        var monitor = Monitor(handler);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        // Five endpoints, each with its probe and its version: nothing else is requested.
        Assert.Equal(10, handler.Requests.Count);
        Assert.All(monitor.Environments, environment =>
        {
            Assert.Equal(PinnedVersionsState.NotTracked, environment.Pinned.State);
            Assert.False(environment.VersionsDiffer);
            Assert.All(environment.Deployables, deployable => Assert.Null(environment.AssessVersions(deployable)));
        });
        Assert.Null(monitor.VersionSummary.Text);
    }

    [Fact]
    public async Task EveryRoundAddsToTheHistory()
    {
        var healthy = true;
        var monitor = Monitor(new StubHandler(request => healthy ? Healthy(request) : StubHandler.Answer(HttpStatusCode.InternalServerError)));

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        healthy = false;
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.All(monitor.Targets, target =>
            Assert.Equal([HealthState.Healthy, HealthState.Unhealthy], target.History.Select(result => result.State)));
        Assert.Equal("5 of 5 nodes not healthy", monitor.Summary.Text);
    }
}
