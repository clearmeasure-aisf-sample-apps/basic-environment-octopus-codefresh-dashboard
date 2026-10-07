using System.Net;

namespace Dashboard.Tests;

/// <summary>
/// A topology with several clusters: each is read with every round and keeps its own readings and events, and an
/// environment whose cluster Azure reports stopped is asleep in the health view.
/// </summary>
public class SeveralClustersMonitorTests
{
    private const string NonprodFacts = "https://raw.example.net/org/dashboard/status/aks-nonprod.json";
    private const string ProdFacts = "https://raw.example.net/org/dashboard/status/aks-prod.json";
    private const string ProdStatus = "https://prod-cluster.example.net/cluster.json";

    private const string Environments = """
          "environments": [
            { "name": "tdd", "namespace": "cmdemo3-tdd", "deployables": [ { "name": "workorders", "nodes": [ { "name": "workorders-tdd", "region": "southcentralus", "url": "https://tdd.example.net" } ] } ] },
            { "name": "uat", "namespace": "cmdemo3-uat", "deployables": [ { "name": "workorders", "nodes": [ { "name": "workorders-uat", "region": "southcentralus", "url": "https://uat.example.net" } ] } ] },
            { "name": "prod", "namespace": "cmdemo3-prod", "deployables": [ { "name": "workorders", "nodes": [ { "name": "workorders-prod", "region": "southcentralus", "url": "https://prod.example.net" } ] } ] }
          ]
        """;

    private const string TwoClusters = "{" + Environments + """
          ,
          "clusters": [
            { "name": "aks-nonprod", "environments": [ "tdd", "uat" ], "serviceUrl": "https://raw.example.net/org/dashboard/status/aks-nonprod.json" },
            { "name": "aks-prod", "environments": [ "prod" ], "serviceUrl": "https://raw.example.net/org/dashboard/status/aks-prod.json",
              "statusUrl": "https://prod-cluster.example.net/cluster.json" }
          ]
        }
        """;

    private readonly SignallingTimeProvider _time = new();

    /// <summary>What the stub answers; a test changes it between rounds. Null is HTTP 404.</summary>
    private string? _nonprod;
    private string? _prod;
    private readonly HashSet<string> _silent = new(StringComparer.Ordinal);

    public SeveralClustersMonitorTests()
    {
        _nonprod = Facts("aks-nonprod", "Running");
        _prod = Facts("aks-prod", "Running");
    }

    /// <summary>Azure's facts about a cluster, read by the workflow at the test's clock unless it says when.</summary>
    private string Facts(string name, string power, DateTimeOffset? generated = null) =>
        ClusterFixture.Sample("aks.json")
            .Replace("\"name\": \"aks-cmdemo3\"", $"\"name\": \"{name}\"", StringComparison.Ordinal)
            .Replace("\"powerState\": \"Running\",", $"\"powerState\": \"{power}\",", StringComparison.Ordinal)
            .Replace("2026-10-06T20:10:04Z", TimeText.Iso(generated ?? _time.GetUtcNow()), StringComparison.Ordinal);

    private HttpResponseMessage Answer(HttpRequestMessage request)
    {
        var address = request.RequestUri!;
        if (_silent.Contains(address.Host))
        {
            throw new HttpRequestException("Failed to fetch");
        }

        return address.AbsoluteUri switch
        {
            NonprodFacts => _nonprod is null ? StubHandler.Answer(HttpStatusCode.NotFound) : StubHandler.Answer(HttpStatusCode.OK, _nonprod),
            ProdFacts => _prod is null ? StubHandler.Answer(HttpStatusCode.NotFound) : StubHandler.Answer(HttpStatusCode.OK, _prod),
            ProdStatus => StubHandler.Answer(
                HttpStatusCode.OK,
                ClusterFixture.Sample("cluster.json").Replace("2026-10-06T20:15:30Z", TimeText.Iso(_time.GetUtcNow()), StringComparison.Ordinal)),
            _ => StubHandler.Answer(HttpStatusCode.OK, address.AbsolutePath == "/_version" ? """{"version":"2.5.772"}""" : "Healthy"),
        };
    }

    private (DashboardMonitor Monitor, StubHandler Handler) Monitor(string topology = TwoClusters)
    {
        var handler = new StubHandler(Answer);
        var http = new HttpClient(handler);
        var monitor = new DashboardMonitor(
            TopologyParser.Parse(topology).Topology!,
            new NodeProber(http, _time),
            new PinnedVersionsReader(http, _time),
            _time,
            cluster: new ClusterReader(http, _time));
        return (monitor, handler);
    }

    private static Task RoundAsync(DashboardMonitor monitor) => monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

    private static IEnumerable<(string? Where, string Text)> ClusterEvents(DashboardMonitor monitor) =>
        monitor.Events.Newest.Where(entry => entry.Kind == EventKind.Cluster).Select(entry => (entry.Node, entry.Text));

    // ----- Polling, readings and events per cluster -----

    [Fact]
    public async Task EveryRoundReadsTheFilesOfEveryClusterAndEachKeepsItsOwnReading()
    {
        _nonprod = Facts("aks-nonprod", "Stopped");
        var (monitor, handler) = Monitor();

        Assert.Equal(2, monitor.Clusters.Count);
        Assert.All(monitor.Clusters, cluster => Assert.Equal(SourceState.Pending, cluster.Service!.State));
        await RoundAsync(monitor);
        await RoundAsync(monitor);

        Assert.Equal(2, handler.RequestedUrls.Count(address => address == NonprodFacts));
        Assert.Equal(2, handler.RequestedUrls.Count(address => address == ProdFacts));
        Assert.Equal(2, handler.RequestedUrls.Count(address => address == ProdStatus));
        Assert.Equal(["aks-nonprod", "aks-prod"], monitor.Clusters.Select(cluster => cluster.Service!.Value!.Name));
        Assert.Equal(["Stopped", "Running"], monitor.Clusters.Select(cluster => cluster.Service!.Value!.PowerState));

        // Only the second cluster names an address for its own status.
        Assert.Null(monitor.Clusters[0].Status);
        Assert.Equal(SourceState.Read, monitor.Clusters[1].Status!.State);
    }

    [Fact]
    public async Task OneClusterThatCannotBeReadLeavesTheOtherAlone()
    {
        _nonprod = "<!DOCTYPE html>";
        var (monitor, _) = Monitor();

        await RoundAsync(monitor);

        Assert.Equal(new SourceReading<AksService>(SourceState.Malformed, Detail: "the file is not valid JSON"), monitor.Clusters[0].Service);
        Assert.Equal(SourceState.Read, monitor.Clusters[1].Service!.State);
    }

    [Fact]
    public void EachClusterShowsTheEnvironmentsItHosts()
    {
        var (monitor, _) = Monitor();

        Assert.Equal(["tdd", "uat"], monitor.Clusters[0].Environments.Select(environment => environment.Name));
        Assert.Equal(["prod"], monitor.Clusters[1].Environments.Select(environment => environment.Name));
    }

    [Fact]
    public void AClusterThatDoesNotSayWhichEnvironmentsItHostsShowsThemAll()
    {
        var (monitor, _) = Monitor("{" + Environments + """, "cluster": { "name": "aks-demo", "serviceUrl": "https://raw.example.net/org/dashboard/status/aks-nonprod.json" } }""");

        Assert.Equal(["tdd", "uat", "prod"], Assert.Single(monitor.Clusters).Environments.Select(environment => environment.Name));
    }

    [Fact]
    public async Task AnEventOfACusterSaysWhichCluster()
    {
        var (monitor, _) = Monitor();

        await RoundAsync(monitor);
        Assert.Empty(ClusterEvents(monitor));
        _nonprod = Facts("aks-nonprod", "Stopped");
        await RoundAsync(monitor);
        _prod = Facts("aks-prod", "Stopped");
        _nonprod = Facts("aks-nonprod", "Running");
        await RoundAsync(monitor);

        Assert.Equal(
            [
                ("AKS aks-nonprod", "The power state of the AKS service: Running → Stopped"),
                ("AKS aks-nonprod", "The power state of the AKS service: Stopped → Running"),
                ("AKS aks-prod", "The power state of the AKS service: Running → Stopped"),
            ],
            ClusterEvents(monitor).Order());
    }

    [Fact]
    public async Task AnEventOfTheClustersOwnStatusSaysWhichClusterToo()
    {
        var (monitor, _) = Monitor();

        await RoundAsync(monitor);
        _silent.Add("prod-cluster.example.net");
        await RoundAsync(monitor);

        Assert.Equal(
            [("cluster aks-prod", "The cluster's status file stopped answering: the browser could not read an answer (network or CORS)")],
            ClusterEvents(monitor));
    }

    [Fact]
    public async Task ClustersWithoutANameAreNumberedInTheirEvents()
    {
        var (monitor, _) = Monitor(TwoClusters.Replace("\"name\": \"aks-nonprod\", ", string.Empty, StringComparison.Ordinal).Replace("\"name\": \"aks-prod\", ", string.Empty, StringComparison.Ordinal));

        await RoundAsync(monitor);
        _prod = Facts("aks-prod", "Stopped");
        await RoundAsync(monitor);

        Assert.Equal([("AKS 2", "The power state of the AKS service: Running → Stopped")], ClusterEvents(monitor));
    }

    [Fact]
    public async Task TheNoteAboutOldFactsIsEachClustersOwn()
    {
        _nonprod = Facts("aks-nonprod", "Running", _time.GetUtcNow().AddMinutes(-47));
        var (monitor, _) = Monitor();

        await RoundAsync(monitor);

        Assert.Equal(
            ["Azure's facts are 47 min old: the workflow that publishes them may not be running.", null],
            monitor.Clusters.Select(cluster => ClusterText.OldFacts(cluster.Service!.Value!, _time.GetUtcNow())));
    }

    // ----- Asleep: the environments of a cluster Azure reports stopped -----

    [Fact]
    public async Task TheEnvironmentsOfAStoppedClusterAreAsleepAndTheOthersAreAsTheyWere()
    {
        _nonprod = Facts("aks-nonprod", "Stopped");
        _silent.UnionWith(["tdd.example.net", "uat.example.net"]);
        var (monitor, _) = Monitor();

        Assert.Equal("Checking 3 nodes", monitor.Summary.Text);
        await RoundAsync(monitor);

        var (tdd, uat, prod) = (monitor.Environments[0], monitor.Environments[1], monitor.Environments[2]);
        Assert.Equal(new ClusterSleep("aks-nonprod", _time.GetUtcNow()), monitor.SleepOf(tdd));
        Assert.Equal(monitor.SleepOf(tdd), monitor.SleepOf(uat));
        Assert.Null(monitor.SleepOf(prod));
        Assert.Equal(SummaryLevel.AllHealthy, monitor.Summary.Level);
        Assert.Equal("The only awake node is healthy, 2 asleep", monitor.Summary.Text);

        // The checks themselves are as they were: the node did not answer.
        Assert.All(monitor.Targets.Take(2), target => Assert.Equal(HealthState.Unreachable, target.State));
        Assert.True(ClusterSleep.CoversAll(monitor.SleepOf(tdd), tdd.Deployables[0].Nodes.Select(node => node.State)));
        Assert.Equal("is asleep", tdd.Deployables[0].Nodes[0].ToNodeVersion(monitor.SleepOf(tdd)).UnknownReason);
        Assert.Equal("Not serving: southcentralus", tdd.Deployables[0].Assess().Headline);
        Assert.Equal("Serves traffic: southcentralus", prod.Deployables[0].Assess().Headline);
    }

    [Fact]
    public async Task EveryClusterStoppedIsEveryNodeAsleep()
    {
        (_nonprod, _prod) = (Facts("aks-nonprod", "Stopped"), Facts("aks-prod", "Stopped"));
        _silent.UnionWith(["tdd.example.net", "uat.example.net", "prod.example.net", "prod-cluster.example.net"]);
        var (monitor, _) = Monitor();

        await RoundAsync(monitor);

        Assert.Equal(SummaryLevel.Asleep, monitor.Summary.Level);
        Assert.Equal("All 3 nodes asleep", monitor.Summary.Text);
        Assert.Equal(0, monitor.Summary.NotHealthy);
    }

    [Fact]
    public async Task ANodeThatDoesNotAnswerInARunningClusterIsAFailureAsBefore()
    {
        _nonprod = Facts("aks-nonprod", "Stopped");
        _silent.UnionWith(["tdd.example.net", "uat.example.net", "prod.example.net"]);
        var (monitor, _) = Monitor();

        await RoundAsync(monitor);

        Assert.Null(monitor.SleepOf(monitor.Environments[2]));
        Assert.Equal(SummaryLevel.Problems, monitor.Summary.Level);
        Assert.Equal("1 of 1 awake node not healthy, 2 asleep", monitor.Summary.Text);
    }

    [Fact]
    public async Task ANodeThatAnswersInAClusterSaidToBeStoppedIsAsItAnswers()
    {
        // The cluster woke since the workflow read the facts: tdd answers already, uat not yet.
        _nonprod = Facts("aks-nonprod", "Stopped");
        _silent.Add("uat.example.net");
        var (monitor, _) = Monitor();

        await RoundAsync(monitor);

        Assert.Equal([HealthState.Healthy, HealthState.Unreachable, HealthState.Healthy], monitor.Targets.Select(target => target.State));
        Assert.Equal("All 2 awake nodes healthy, 1 asleep", monitor.Summary.Text);
        Assert.False(ClusterSleep.CoversAll(monitor.SleepOf(monitor.Environments[0]), monitor.Environments[0].Deployables[0].Nodes.Select(node => node.State)));
    }

    [Fact]
    public async Task FactsThatGrowOldEndTheSleepWithoutAnotherRound()
    {
        _nonprod = Facts("aks-nonprod", "Stopped");
        _silent.UnionWith(["tdd.example.net", "uat.example.net"]);
        var (monitor, _) = Monitor();

        await RoundAsync(monitor);
        _time.Advance(AksService.OldAfter);
        Assert.Equal("The only awake node is healthy, 2 asleep", monitor.Summary.Text);
        _time.Advance(TimeSpan.FromSeconds(1));

        // The workflow stopped publishing: nothing says the cluster is still stopped on purpose.
        Assert.Null(monitor.SleepOf(monitor.Environments[0]));
        Assert.Equal("2 of 3 nodes not healthy", monitor.Summary.Text);

        // And a round that reads the same old file changes nothing.
        await RoundAsync(monitor);
        Assert.Equal("2 of 3 nodes not healthy", monitor.Summary.Text);
        Assert.NotNull(ClusterText.OldFacts(monitor.Clusters[0].Service!.Value!, _time.GetUtcNow()));
    }

    [Theory]
    [InlineData("running")]
    [InlineData("missing")]
    [InlineData("unreadable")]
    [InlineData("unavailable")]
    [InlineData("no time")]
    public async Task WithoutFreshFactsThatSayStoppedAnUnreachableNodeIsAFailureAsBefore(string facts)
    {
        _nonprod = facts switch
        {
            "running" => Facts("aks-nonprod", "Running"),
            "missing" => null,
            "unreadable" => "<!DOCTYPE html>",
            "no time" => Facts("aks-nonprod", "Stopped").Replace("\"generated\"", "\"written\"", StringComparison.Ordinal),
            _ => Facts("aks-nonprod", "Stopped"),
        };
        if (facts == "unavailable")
        {
            _silent.Add("raw.example.net");
        }

        _silent.UnionWith(["tdd.example.net", "uat.example.net"]);
        var (monitor, _) = Monitor();

        await RoundAsync(monitor);

        Assert.Null(monitor.SleepOf(monitor.Environments[0]));
        Assert.Null(monitor.SleepOf(monitor.Environments[1]));
        Assert.Equal(SummaryLevel.Problems, monitor.Summary.Level);
        Assert.Equal("2 of 3 nodes not healthy", monitor.Summary.Text);
        Assert.Contains(monitor.Events.Newest, entry => entry is { Kind: EventKind.Serving, Level: EventLevel.Problem, Environment: "tdd" });
    }

    [Fact]
    public async Task AFailedReadingAfterStoppedEndsTheSleep()
    {
        _nonprod = Facts("aks-nonprod", "Stopped");
        _silent.UnionWith(["tdd.example.net", "uat.example.net"]);
        var (monitor, _) = Monitor();

        await RoundAsync(monitor);
        Assert.NotNull(monitor.SleepOf(monitor.Environments[0]));
        _nonprod = null;
        await RoundAsync(monitor);

        // A failed reading replaces a good one: the page no longer knows that the cluster is stopped.
        Assert.Null(monitor.SleepOf(monitor.Environments[0]));
        Assert.Equal("2 of 3 nodes not healthy", monitor.Summary.Text);
    }

    [Fact]
    public async Task NothingServingBecauseTheClusterIsAsleepIsNoProblemInTheLog()
    {
        _nonprod = Facts("aks-nonprod", "Stopped");
        _silent.UnionWith(["tdd.example.net", "uat.example.net"]);
        var (monitor, _) = Monitor();

        await RoundAsync(monitor);

        // The round has read Azure's facts by its end, whatever arrived first: the serving decision is said calmly.
        var serving = monitor.Events.Newest.Where(entry => entry.Kind == EventKind.Serving).ToList();
        Assert.Equal(["tdd", "uat"], serving.Select(entry => entry.Environment).Order());
        Assert.All(serving, entry => Assert.Equal((EventLevel.Info, "Asleep: cluster aks-nonprod is stopped."), (entry.Level, entry.Text)));
    }

    [Fact]
    public async Task ANodeThatStopsAnsweringInAClusterKnownToBeStoppedFallsAsleepInTheLog()
    {
        // The facts say Stopped while the nodes still answer; then they stop answering.
        _nonprod = Facts("aks-nonprod", "Stopped");
        var (monitor, _) = Monitor();

        await RoundAsync(monitor);
        Assert.Empty(monitor.Events.Newest);
        _silent.Add("tdd.example.net");
        await RoundAsync(monitor);

        Assert.Equal(
            [
                (EventKind.Health, EventLevel.Info, "southcentralus", "workorders: Healthy → Asleep: cluster aks-nonprod is stopped"),
                (EventKind.Serving, EventLevel.Info, "workorders", "Asleep: cluster aks-nonprod is stopped; southcentralus no longer serves traffic."),
            ],
            monitor.Events.Newest.Select(entry => (entry.Kind, entry.Level, entry.Node!, entry.Text)).Order());

        // And when the cluster wakes, the node is healthy again whatever the facts still say.
        _silent.Clear();
        await RoundAsync(monitor);
        Assert.Contains(monitor.Events.Newest, entry => entry is { Kind: EventKind.Health, Level: EventLevel.Good, Text: "workorders: Unreachable → Healthy (HTTP 200)" });
        Assert.Contains(monitor.Events.Newest, entry => entry is { Kind: EventKind.Serving, Level: EventLevel.Good, Text: "southcentralus serves traffic again." });
        Assert.Equal("All 3 nodes healthy", monitor.Summary.Text);
    }

    // ----- The single cluster of a topology -----

    [Fact]
    public async Task TheSingleClusterThatNamesItsEnvironmentsPutsThemToSleepTooAndItsEventsNameNoCluster()
    {
        var (monitor, _) = Monitor("{" + Environments + """, "cluster": { "name": "aks-nonprod", "environments": [ "tdd", "uat" ], "serviceUrl": "https://raw.example.net/org/dashboard/status/aks-nonprod.json" } }""");
        _silent.UnionWith(["tdd.example.net", "uat.example.net"]);

        await RoundAsync(monitor);
        Assert.Equal("2 of 3 nodes not healthy", monitor.Summary.Text);
        _nonprod = Facts("aks-nonprod", "Stopped");
        await RoundAsync(monitor);

        Assert.Equal("The only awake node is healthy, 2 asleep", monitor.Summary.Text);
        Assert.Equal([("AKS", "The power state of the AKS service: Running → Stopped")], ClusterEvents(monitor));
    }

    [Fact]
    public async Task TheSingleClusterThatNamesNoEnvironmentsLeavesTheHealthViewAsItWas()
    {
        _nonprod = Facts("aks-nonprod", "Stopped");
        _silent.UnionWith(["tdd.example.net", "uat.example.net", "prod.example.net"]);
        var (monitor, _) = Monitor("{" + Environments + """, "cluster": { "name": "aks-demo", "serviceUrl": "https://raw.example.net/org/dashboard/status/aks-nonprod.json" } }""");

        await RoundAsync(monitor);

        Assert.True(monitor.Clusters[0].Service!.Value!.IsStopped);
        Assert.NotNull(monitor.Clusters[0].Sleep);
        Assert.All(monitor.Environments, environment => Assert.Null(monitor.SleepOf(environment)));
        Assert.Equal("3 of 3 nodes not healthy", monitor.Summary.Text);
        Assert.All(
            monitor.Events.Newest.Where(entry => entry.Kind is EventKind.Health or EventKind.Serving),
            entry => Assert.Equal(EventLevel.Problem, entry.Level));
    }
}
