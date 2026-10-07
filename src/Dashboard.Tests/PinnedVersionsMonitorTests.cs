using System.Net;

namespace Dashboard.Tests;

/// <summary>The monitor reads the versions pinned in Git next to its health checks.</summary>
public class PinnedVersionsMonitorTests
{
    private const string VersionsHost = "raw.example.net";

    private const string Tracked = """
        {
          "system": { "slug": "demo", "name": "Demo", "repository": "https://github.example.net/org/demo-system" },
          "environments": [
            { "name": "tdd",
              "versionsUrl": "https://raw.example.net/org/demo-system/main/environments/tdd/versions.json",
              "versionsHistoryUrl": "https://github.example.net/org/demo-system/commits/main/environments/tdd/versions.json",
              "deployables": [ {
                "name": "ui", "projectUrl": "https://octopus.example.net/app#/Spaces-1/projects/demo-ui",
                "frontDoor": "https://fd-tdd.example.net",
                "nodes": [ { "name": "tdd-west", "region": "westus3", "role": "primary", "url": "https://tdd-west.example.net" } ] } ] },
            { "name": "uat",
              "versionsUrl": "https://raw.example.net/org/demo-system/main/environments/uat/versions.json",
              "deployables": [
                { "name": "ui", "frontDoor": "https://fd-uat.example.net",
                  "nodes": [
                    { "name": "uat-west", "region": "westus3", "role": "primary", "url": "https://uat-west.example.net" },
                    { "name": "uat-east", "region": "eastus2", "role": "standby", "url": "https://uat-east.example.net" } ] },
                { "name": "api",
                  "nodes": [ { "name": "uat-api", "region": "westus3", "role": "primary", "url": "https://uat-api.example.net" } ] } ] },
            { "name": "prod",
              "deployables": [ {
                "name": "ui",
                "nodes": [ { "name": "prod-west", "region": "westus3", "role": "primary", "url": "https://prod-west.example.net" } ] } ] }
          ]
        }
        """;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private readonly SignallingTimeProvider _time = new();

    private DashboardMonitor Monitor(StubHandler handler)
    {
        var http = new HttpClient(handler);
        return new DashboardMonitor(
            TopologyParser.Parse(Tracked).Topology!,
            new NodeProber(http, _time),
            new PinnedVersionsReader(http, _time),
            _time);
    }

    private static bool IsVersions(HttpRequestMessage request) => request.RequestUri!.Host == VersionsHost;

    /// <summary>Every node is healthy and runs the version the test names for its host (2.4.7 unless named).</summary>
    private static HttpResponseMessage Node(HttpRequestMessage request, params (string Host, string Version)[] running)
    {
        if (request.RequestUri!.AbsolutePath != "/_version")
        {
            return StubHandler.Answer(HttpStatusCode.OK, "Healthy");
        }

        var version = running.Where(entry => entry.Host == request.RequestUri.Host).Select(entry => entry.Version).FirstOrDefault();
        return StubHandler.Answer(HttpStatusCode.OK, $$"""{"version":"{{version ?? "2.4.7"}}+0a1b2c3"}""");
    }

    private static HttpResponseMessage Versions(string json = """{ "api": "1.3.0", "ui": "2.4.7" }""") =>
        StubHandler.Answer(HttpStatusCode.OK, json);

    private static VersionAssessment? Assessment(DashboardMonitor monitor, string environment, string deployable)
    {
        var status = monitor.Environments.Single(candidate => candidate.Name == environment);
        return status.AssessVersions(status.Deployables.Single(candidate => candidate.Info.Name == deployable));
    }

    [Fact]
    public void BeforeTheFirstRoundThePinnedVersionsArePendingOrNotTracked()
    {
        var monitor = Monitor(new StubHandler(_ => Versions()));

        Assert.Equal(
            [PinnedVersionsState.Pending, PinnedVersionsState.Pending, PinnedVersionsState.NotTracked],
            monitor.Environments.Select(environment => environment.Pinned.State));
        Assert.Equal(VersionState.Pending, Assessment(monitor, "tdd", "ui")!.State);
        Assert.Null(Assessment(monitor, "prod", "ui"));
        Assert.Null(monitor.VersionSummary.Text);
    }

    [Fact]
    public async Task ARoundReadsTheFileOncePerEnvironmentNotOncePerNode()
    {
        var handler = new StubHandler(request => IsVersions(request) ? Versions() : Node(request));
        var monitor = Monitor(handler);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        // tdd and uat name a file; prod does not. Seven endpoints in uat and tdd and prod share them.
        Assert.Equal(
            [
                "https://raw.example.net/org/demo-system/main/environments/tdd/versions.json",
                "https://raw.example.net/org/demo-system/main/environments/tdd/versions.json",
                "https://raw.example.net/org/demo-system/main/environments/uat/versions.json",
                "https://raw.example.net/org/demo-system/main/environments/uat/versions.json",
            ],
            handler.Requests.Where(IsVersions).Select(request => request.RequestUri!.AbsoluteUri).Order(StringComparer.Ordinal));
        Assert.All(handler.Requests.Where(IsVersions), request => Assert.Equal("no-store", NodeProberTests.FetchOption(request, "cache")));
    }

    [Fact]
    public async Task EveryNodeOnThePinnedVersionIsInSync()
    {
        var monitor = Monitor(new StubHandler(request => IsVersions(request) ? Versions() : Node(request, ("uat-api.example.net", "1.3.0"))));

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Equal("Pinned 2.4.7. In sync: westus3 runs 2.4.7.", Assessment(monitor, "tdd", "ui")!.Text);
        Assert.Equal("Pinned 2.4.7. In sync: all 2 nodes run 2.4.7.", Assessment(monitor, "uat", "ui")!.Text);
        Assert.Equal("Pinned 1.3.0. In sync: westus3 runs 1.3.0.", Assessment(monitor, "uat", "api")!.Text);
        Assert.Null(Assessment(monitor, "prod", "ui"));
        Assert.False(monitor.VersionSummary.Differs);
        Assert.Null(monitor.VersionSummary.Text);
        Assert.Equal("All 7 nodes healthy", monitor.Summary.Text);
    }

    [Fact]
    public async Task ANodeOnAnOlderVersionDiffersAndIsCountedOncePerEnvironment()
    {
        var monitor = Monitor(new StubHandler(request => IsVersions(request)
            ? Versions()
            : Node(request, ("uat-east.example.net", "2.4.6"), ("uat-api.example.net", "1.2.9"))));

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Equal("Pinned 2.4.7. Differs: eastus2 runs 2.4.6.", Assessment(monitor, "uat", "ui")!.Text);
        Assert.Equal("Pinned 1.3.0. Differs: westus3 runs 1.2.9.", Assessment(monitor, "uat", "api")!.Text);
        Assert.Equal(VersionState.InSync, Assessment(monitor, "tdd", "ui")!.State);
        Assert.Equal([false, true, false], monitor.Environments.Select(environment => environment.VersionsDiffer));
        Assert.Equal("Versions differ in 1 environment", monitor.VersionSummary.Text);

        // The summary of the header stays about health.
        Assert.Equal("All 7 nodes healthy", monitor.Summary.Text);
    }

    [Fact]
    public async Task TheFrontDoorEndpointIsNotCompared()
    {
        // Front Door answers with the version of whichever node served the request: only the nodes are compared.
        var monitor = Monitor(new StubHandler(request => IsVersions(request) ? Versions() : Node(request, ("fd-tdd.example.net", "2.4.6"))));

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Equal(VersionState.InSync, Assessment(monitor, "tdd", "ui")!.State);
    }

    [Fact]
    public async Task AnUnreachableNodeIsLeftOutOfTheComparison()
    {
        var monitor = Monitor(new StubHandler(request => request.RequestUri!.Host switch
        {
            VersionsHost => Versions(),
            "uat-east.example.net" => throw new HttpRequestException("Failed to fetch"),
            _ => Node(request, ("uat-api.example.net", "1.3.0")),
        }));

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Equal("Pinned 2.4.7. In sync: westus3 runs 2.4.7; eastus2 is unreachable.", Assessment(monitor, "uat", "ui")!.Text);
        Assert.Null(monitor.VersionSummary.Text);
        Assert.Equal("1 of 7 nodes not healthy", monitor.Summary.Text);
    }

    [Fact]
    public async Task ADeployableWithoutAnEntryIsNotDeployed()
    {
        var monitor = Monitor(new StubHandler(request => IsVersions(request) ? Versions("""{ "ui": "2.4.7" }""") : Node(request)));

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Equal(VersionState.NotDeployed, Assessment(monitor, "uat", "api")!.State);
        Assert.Equal(VersionState.InSync, Assessment(monitor, "uat", "ui")!.State);
        Assert.Null(monitor.VersionSummary.Text);
    }

    [Fact]
    public async Task AFileThatCannotBeReadFailsNoHealthCheck()
    {
        var monitor = Monitor(new StubHandler(request =>
        {
            if (!IsVersions(request))
            {
                return Node(request, ("uat-east.example.net", "2.4.6"));
            }

            return request.RequestUri!.AbsolutePath.Contains("/tdd/", StringComparison.Ordinal)
                ? throw new HttpRequestException("Failed to fetch")
                : StubHandler.Answer(HttpStatusCode.NotFound, "404: Not Found");
        }));

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.All(monitor.Targets, target =>
        {
            Assert.Equal(HealthState.Healthy, target.State);
            Assert.NotNull(target.Version);
        });
        Assert.Equal("All 7 nodes healthy", monitor.Summary.Text);
        Assert.NotNull(monitor.LastRefresh);
        Assert.Equal(
            [PinnedVersionsState.Unavailable, PinnedVersionsState.Missing, PinnedVersionsState.NotTracked],
            monitor.Environments.Select(environment => environment.Pinned.State));
        Assert.Equal(VersionState.PinnedUnknown, Assessment(monitor, "tdd", "ui")!.State);
        Assert.Equal(VersionState.NotDeployed, Assessment(monitor, "uat", "ui")!.State);

        // A version that is not known is not a difference.
        Assert.Null(monitor.VersionSummary.Text);
    }

    [Fact]
    public async Task AFileThatHangsDelaysNoHealthResultAndEndsAtTheTimeout()
    {
        var handler = new StubHandler((request, cancellationToken) => IsVersions(request)
            ? StubHandler.NeverAsync(cancellationToken)
            : Task.FromResult(Node(request)));
        var monitor = Monitor(handler);
        var nodesAnswered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.Changed += () =>
        {
            if (monitor.Summary.Level == SummaryLevel.AllHealthy)
            {
                nodesAnswered.TrySetResult();
            }
        };

        var round = monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        await nodesAnswered.Task.WaitAsync(Patience);

        // Every tile is shown although neither file has answered.
        Assert.Equal("All 7 nodes healthy", monitor.Summary.Text);
        Assert.Equal(VersionState.Pending, Assessment(monitor, "tdd", "ui")!.State);

        _time.Advance(NodeProber.DefaultTimeout);
        await round.WaitAsync(Patience);

        Assert.NotNull(monitor.LastRefresh);
        Assert.Equal("versions.json could not be read: no answer within 10 s.", Assessment(monitor, "tdd", "ui")!.Detail);
        Assert.Equal("All 7 nodes healthy", monitor.Summary.Text);
    }

    [Fact]
    public async Task AFailedReadingReplacesAGoodOne()
    {
        var available = true;
        var monitor = Monitor(new StubHandler(request => !IsVersions(request)
            ? Node(request, ("uat-east.example.net", "2.4.6"))
            : available ? Versions() : StubHandler.Answer(HttpStatusCode.BadGateway)));

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        Assert.Equal("Versions differ in 1 environment", monitor.VersionSummary.Text);

        available = false;
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        // The dashboard does not compare with a pinned version it can no longer read.
        Assert.Equal(VersionState.PinnedUnknown, Assessment(monitor, "uat", "ui")!.State);
        Assert.Null(monitor.VersionSummary.Text);
    }

    [Fact]
    public async Task ChangedIsRaisedForEveryReadingToo()
    {
        var monitor = Monitor(new StubHandler(request => IsVersions(request) ? Versions() : Node(request)));
        var changes = 0;
        monitor.Changed += () => Interlocked.Increment(ref changes);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        // Seven endpoints, two files and the end of the round.
        Assert.Equal(10, changes);
    }

    [Fact]
    public void TheLinksOfTheTopologyReachTheStatus()
    {
        var monitor = Monitor(new StubHandler(_ => Versions()));
        var tdd = monitor.Environments[0];

        Assert.Equal("https://github.example.net/org/demo-system", monitor.Topology.System.Repository?.AbsoluteUri);
        Assert.Equal(
            "https://github.example.net/org/demo-system/commits/main/environments/tdd/versions.json",
            tdd.Info.VersionsHistoryUrl?.AbsoluteUri);
        Assert.Equal("https://octopus.example.net/app#/Spaces-1/projects/demo-ui", tdd.Deployables[0].Info.ProjectUrl?.AbsoluteUri);
    }
}
