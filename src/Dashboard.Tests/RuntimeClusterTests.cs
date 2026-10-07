namespace Dashboard.Tests;

/// <summary>
/// The runtime view of a system inside a Kubernetes cluster: the kinds a cluster adds (a gateway that holds the public
/// address, a workload without one), and asleep, by the health view's decision (<see cref="ClusterSleep"/>).
/// </summary>
public class RuntimeClusterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 20, 15, 0, TimeSpan.Zero);
    private static readonly ClusterSleep Sleep = new("aks-nonprod", Now.AddMinutes(-5));
    private static readonly ProbeResult NoAnswer = new(HealthState.Unreachable, null, null, Now, null, "No answer within 10 s.");

    /// <summary>One web app behind the cluster's gateway, with a worker and a database next to it, and Argo CD.</summary>
    private static readonly RuntimeManifest Manifest = RuntimeManifestParser.ParseManifest("""
        {
          "environment": "tdd",
          "nodes": [
            { "alias": "browser", "qualifiedName": "browser", "kind": "person", "name": "Browser" },
            { "alias": "gateway", "qualifiedName": "sub.rg.aks.ns_ingress.gateway", "kind": "gateway", "name": "platform-gateway", "url": null },
            { "alias": "web", "qualifiedName": "sub.rg.aks.ns_app.web", "kind": "webapp", "deployable": "orders", "name": "ui-server",
              "regionAlias": "ns_app", "url": "https://orders-tdd.example.net" },
            { "alias": "worker", "qualifiedName": "sub.rg.aks.ns_app.worker", "kind": "workload", "name": "worker", "regionAlias": "ns_app" },
            { "alias": "db", "qualifiedName": "sub.rg.aks.ns_app.db", "kind": "sql", "name": "db", "regionAlias": "ns_app" },
            { "alias": "disk", "qualifiedName": "sub.rg_data.disk", "kind": "workload", "name": "outside the cluster's frame" }
          ],
          "regions": [
            { "alias": "aks", "qualifiedName": "sub.rg.aks", "name": "aks-nonprod", "roles": [ "cluster" ] },
            { "alias": "ns_app", "qualifiedName": "sub.rg.aks.ns_app", "name": "orders-tdd", "roles": [ "namespace" ] }
          ],
          "edges": [
            { "id": "browser-to-gateway", "from": "browser", "to": "gateway", "kind": "public" },
            { "id": "gateway-to-web", "from": "gateway", "to": "web", "kind": "route" },
            { "id": "web-to-db", "from": "web", "to": "db", "kind": "sql" }
          ]
        }
        """).Value!;

    private static EnvironmentStatus Environment(string alivePath = "/alive") =>
        new(TopologyParser.Parse($$"""
            { "environments": [ { "name": "tdd", "deployables": [ {
                "name": "orders", "frontDoor": null, "alivePath": "{{alivePath}}",
                "nodes": [ { "name": "orders-tdd", "region": "southcentralus", "url": "https://orders-tdd.example.net" } ] } ] } ] }
            """).Topology!.Environments[0]);

    private static EnvironmentStatus Answering(int? status, ProbeKind probe = ProbeKind.Health, string alivePath = "/alive")
    {
        var environment = Environment(alivePath);
        if (status is { } code)
        {
            environment.Deployables[0].Nodes[0].Record(code == 0
                ? NoAnswer with { Probe = probe }
                : new ProbeResult(HealthClassifier.FromStatusCode(code), code, 40, Now, "2.5.1", null, probe));
        }

        return environment;
    }

    private static RuntimePayload Build(EnvironmentStatus environment, ClusterSleep? sleep = null) =>
        RuntimePayloadBuilder.Build(Manifest, environment, null, TimeZoneInfo.Utc, sleep, Now);

    private static RuntimeTile Tile(RuntimePayload payload, string alias) => payload.Nodes.Single(tile => tile.Alias == alias);

    private static RuntimeRegionMark Region(RuntimePayload payload, string alias) => payload.Regions.Single(region => region.Alias == alias);

    private static string Edge(RuntimePayload payload, string id) => payload.Edges.Single(edge => edge.Id == id).State;

    // ---------- The kinds of a cluster ----------

    [Fact]
    public void AGatewayIsReachableWhenTheCheckOfAWebAppPassesThroughIt()
    {
        var payload = Build(Answering(200));

        var gateway = Tile(payload, "gateway");
        Assert.Equal(("healthy", "Reachable"), (gateway.State, gateway.Label));
        Assert.Equal(new RuntimeTileLine("check of ui-server passed through it", "ok"), Assert.Single(gateway.Lines));
        Assert.Null(gateway.History);
        Assert.Equal("platform-gateway: reachable. This page does not check the gateway on its own; the check of ui-server was answered through it (last 20:15:00).", gateway.Title);
        Assert.Equal("active", Edge(payload, "browser-to-gateway"));
        Assert.Equal("active", Edge(payload, "gateway-to-web"));
        Assert.Equal(new RuntimeRegionMark("ns_app", "serving", "serving traffic"), Region(payload, "ns_app"));
    }

    [Theory]
    [InlineData(503)]
    [InlineData(0)]
    public void AFailingCheckLeavesTheGatewayNotConfirmedNotDown(int status)
    {
        var payload = Build(Answering(status));

        var gateway = Tile(payload, "gateway");
        Assert.Equal(("neutral", "Not confirmed"), (gateway.State, gateway.Label));
        Assert.Equal(new RuntimeTileLine("no check through it passes", "muted"), Assert.Single(gateway.Lines));
        Assert.Contains("the gateway or the web app may be the cause", gateway.Title, StringComparison.Ordinal);
        Assert.Equal("down", Edge(payload, "browser-to-gateway"));
        Assert.Equal("down", Edge(payload, "gateway-to-web"));
    }

    [Fact]
    public void BeforeTheFirstCheckTheGatewayAndItsRelationshipsAreBeingChecked()
    {
        var payload = Build(Answering(null));

        Assert.Equal(("checking", "Checking"), (Tile(payload, "gateway").State, Tile(payload, "gateway").Label));
        Assert.Equal("checking", Edge(payload, "browser-to-gateway"));
        Assert.Equal("checking", Edge(payload, "gateway-to-web"));
    }

    [Fact]
    public void AGatewayWithoutACheckedWebAppBehindItIsNotProbed()
    {
        var unrouted = Manifest with { Edges = [.. Manifest.Edges.Where(edge => edge.Kind != RuntimeEdgeKind.Route)] };

        var payload = RuntimePayloadBuilder.Build(unrouted, Answering(200), null, TimeZoneInfo.Utc);
        var withoutTopology = RuntimePayloadBuilder.Build(Manifest, null, null, TimeZoneInfo.Utc);

        Assert.Equal(("neutral", "Not probed"), (Tile(payload, "gateway").State, Tile(payload, "gateway").Label));
        Assert.Equal("neutral", Edge(payload, "browser-to-gateway"));
        Assert.Equal(("neutral", "Not checked"), (Tile(withoutTopology, "gateway").State, Tile(withoutTopology, "gateway").Label));
        Assert.Equal(new RuntimeTileLine("not in topology.json", "muted"), Assert.Single(Tile(withoutTopology, "gateway").Lines));
        Assert.Equal("neutral", Edge(withoutTopology, "gateway-to-web"));
    }

    [Fact]
    public void ARouteHasNoNumberLineAndThePublicRelationshipCountsTheWebAppsCalls()
    {
        var payload = Build(Answering(200));

        var route = payload.Edges.Single(edge => edge.Id == "gateway-to-web");
        Assert.Null(route.Number);
        Assert.Equal("gateway routes to web. Carries the traffic.", route.Title);

        // No telemetry in this topology: a dash, drawn only where the diagram has a slot for it.
        Assert.Equal(RuntimePayloadBuilder.NoNumber, payload.Edges.Single(edge => edge.Id == "browser-to-gateway").Number);
    }

    [Fact]
    public void AWorkloadIsDrawnNotProbed()
    {
        var worker = Tile(Build(Answering(200)), "worker");

        Assert.Equal(("neutral", "Not probed"), (worker.State, worker.Label));
        Assert.Equal(new RuntimeTileLine("no address a browser can call", "muted"), Assert.Single(worker.Lines));
        Assert.Equal("worker: it runs inside the cluster and has no public address, so this page cannot check it.", worker.Title);
        Assert.Null(worker.History);
    }

    [Fact]
    public void TheDatabaseNamesTheOnlyNodeAsTheDiagramDoes()
    {
        var database = Tile(Build(Answering(200)), "db");

        Assert.Equal(("healthy", "Reachable"), (database.State, database.Label));
        Assert.Equal(new RuntimeTileLine("health check of ui-server passed", "ok"), Assert.Single(database.Lines));
    }

    [Fact]
    public void ALivenessProbeOnTheHealthChecksPathSaysTheDatabaseIsReachable()
    {
        // A system whose liveness path is its health check: both probes connect to the database.
        var same = Tile(Build(Answering(200, ProbeKind.Liveness, "/_healthcheck")), "db");
        var apart = Tile(Build(Answering(200, ProbeKind.Liveness)), "db");
        var failing = Tile(Build(Answering(503, ProbeKind.Liveness, "/_healthcheck")), "db");

        Assert.Equal(("healthy", "Reachable"), (same.State, same.Label));
        Assert.Equal(("neutral", "Not probed"), (apart.State, apart.Label));
        Assert.Equal(new RuntimeTileLine("probe Liveness leaves it alone", "muted"), Assert.Single(apart.Lines));
        Assert.Equal(("neutral", "Not confirmed"), (failing.State, failing.Label));
    }

    [Fact]
    public void TheFrameOfARunningClusterSaysNothing() =>
        Assert.Equal(new RuntimeRegionMark("aks", "neutral", string.Empty), Region(Build(Answering(200)), "aks"));

    // ---------- Asleep ----------

    [Fact]
    public void ANodeThatDoesNotAnswerFromAStoppedClusterIsAsleepInTheHealthViewsWords()
    {
        var environment = Answering(0);
        environment.Deployables[0].RecordPin(PinnedVersions.ParseKustomization("images:\n  - name: ui\n    newTag: \"2.5.1\"\n", "orders"));

        var web = Tile(Build(environment, Sleep), "web");

        Assert.Equal(("asleep", ClusterSleep.Label, "no answer, as expected"), (web.State, web.Label, web.Facts));
        Assert.Equal(
            [new RuntimeTileLine("version not known", "muted"), new RuntimeTileLine("pinned 2.5.1: not compared", "unknown"), new RuntimeTileLine("cluster aks-nonprod is stopped", "muted")],
            web.Lines);

        // The checks keep their states: the strip shows what the page saw.
        Assert.Equal(["unreachable"], web.History!);
        Assert.Equal(
            "ui-server: Asleep\nhttps://orders-tdd.example.net/\n"
            + "No answer, as expected: cluster aks-nonprod is stopped.\n"
            + "Azure reports the power state Stopped, as of 20:10:00 (5 min ago). Nothing answers from inside a stopped cluster: that is expected, not a failure.\n"
            + "Last check 20:15:00: No answer within 10 s.\n"
            + HistoryText.Describe(environment.Deployables[0].Nodes[0].History),
            web.Title);
        Assert.Equal(Sleep.NodeDetail, web.Title.Split('\n')[2]);
        Assert.Equal(Sleep.Detail(Now, TimeZoneInfo.Utc), web.Title.Split('\n')[3]);
    }

    [Fact]
    public void WhileNothingAnswersEverythingInsideTheStoppedClustersFrameIsAsleep()
    {
        var payload = Build(Answering(0), Sleep);

        Assert.All((string[])["gateway", "web", "worker", "db"], alias =>
        {
            var tile = Tile(payload, alias);
            Assert.Equal(("asleep", "Asleep"), (tile.State, tile.Label));
            Assert.Equal(new RuntimeTileLine("cluster aks-nonprod is stopped", "muted"), tile.Lines[^1]);
        });
        Assert.Equal(
            "db: Asleep. It runs inside the cluster, and cluster aks-nonprod is stopped.\n" + Sleep.Detail(Now, TimeZoneInfo.Utc),
            Tile(payload, "db").Title);

        // The banner of the health view on the cluster's frame, its chip on the namespace's.
        Assert.Equal(new RuntimeRegionMark("aks", "asleep", Sleep.Headline), Region(payload, "aks"));
        Assert.Equal(new RuntimeRegionMark("ns_app", "asleep", "asleep"), Region(payload, "ns_app"));
        Assert.All(payload.Edges, edge =>
        {
            Assert.Equal("asleep", edge.State);
            Assert.Contains("Asleep: nothing answers from inside a stopped cluster, and that is expected.", edge.Title, StringComparison.Ordinal);
        });

        // What the diagram draws outside the cluster's frame does not stop with it.
        Assert.Equal(("neutral", "Not probed"), (Tile(payload, "disk").State, Tile(payload, "disk").Label));
    }

    [Fact]
    public void NothingReadsAsAFailureWhileAsleep()
    {
        var payload = Build(Answering(0), Sleep);

        string[] failures = ["unreachable", "unhealthy", "down"];
        Assert.DoesNotContain(payload.Nodes, tile => failures.Contains(tile.State));
        Assert.DoesNotContain(payload.Regions, region => failures.Contains(region.State));
        Assert.DoesNotContain(payload.Edges, edge => failures.Contains(edge.State));
        Assert.DoesNotContain(payload.Nodes.SelectMany(tile => tile.Lines), line => line.Text.Contains("not serving", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(200, "healthy", "serving", "active")]
    [InlineData(503, "unhealthy", "down", "down")]
    public void ANodeThatAnswersIsNeverAsleepWhateverAzuresFactsSay(int status, string state, string region, string edge)
    {
        // A cluster that was just woken: the facts still say Stopped, and the node answers.
        var payload = Build(Answering(status), Sleep);

        Assert.Equal(state, Tile(payload, "web").State);
        Assert.Equal(region, Region(payload, "ns_app").State);
        Assert.Equal(new RuntimeRegionMark("aks", "neutral", string.Empty), Region(payload, "aks"));
        Assert.Equal(edge, Edge(payload, "browser-to-gateway"));
        Assert.Equal(edge, Edge(payload, "gateway-to-web"));
        Assert.DoesNotContain(payload.Nodes, tile => tile.State == "asleep");
    }

    [Fact]
    public void ANodeThatWasNotCheckedYetIsBeingCheckedNotAsleep()
    {
        var payload = Build(Answering(null), Sleep);

        Assert.Equal("checking", Tile(payload, "web").State);
        Assert.Equal("checking", Region(payload, "ns_app").State);
        Assert.Equal("neutral", Region(payload, "aks").State);
        Assert.DoesNotContain(payload.Nodes, tile => tile.State == "asleep");
    }

    [Fact]
    public void WithoutTheFactsAnUnreachableNodeIsAFailureAsBefore()
    {
        var payload = Build(Answering(0));

        Assert.Equal(("unreachable", "Unreachable", "no answer"), (Tile(payload, "web").State, Tile(payload, "web").Label, Tile(payload, "web").Facts));
        Assert.Equal(new RuntimeTileLine("not serving", "plain"), Tile(payload, "web").Lines[^1]);
        Assert.Equal(new RuntimeRegionMark("ns_app", "down", "not serving"), Region(payload, "ns_app"));
        Assert.Equal("down", Edge(payload, "browser-to-gateway"));
        Assert.Equal("idle", Edge(payload, "web-to-db"));
        Assert.Equal(("neutral", "Not confirmed"), (Tile(payload, "db").State, Tile(payload, "db").Label));

        // The overload without a sleep is the same update.
        Assert.Equal(payload.ToJson(), RuntimePayloadBuilder.Build(Manifest, Answering(0), null, TimeZoneInfo.Utc).ToJson());
    }

    [Fact]
    public void TheSamplesNodesAreAsleepAndItsDatabaseOutsideAClusterIsNot()
    {
        // The sample's uat (App Service, Front Door, Azure SQL), as if a cluster hosted it: its manifest draws no
        // cluster frame, so only what the page checks is asleep.
        var topology = TopologyParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "topology.sample.json"))).Topology!;
        var uat = new EnvironmentStatus(topology.Environments.Single(environment => environment.Name == "uat"));
        foreach (var target in uat.Deployables[0].Targets)
        {
            target.Record(NoAnswer);
        }

        var manifest = RuntimeManifestParser.ParseManifest(RuntimeManifestParserTests.Sample("uat.json")).Value!;
        var payload = RuntimePayloadBuilder.Build(manifest, uat, null, TimeZoneInfo.Utc, Sleep, Now);

        Assert.All((string[])["fd_ui", "app_ui_primary", "app_ui_standby"], alias => Assert.Equal("asleep", Tile(payload, alias).State));
        Assert.Equal(new RuntimeTileLine("cluster aks-nonprod is stopped", "muted"), Tile(payload, "app_ui_primary").Lines[^1]);
        Assert.Equal("asleep", Region(payload, "region_primary").State);
        Assert.Equal("asleep", Region(payload, "region_standby").State);
        Assert.Equal(("neutral", "Not confirmed"), (Tile(payload, "sqldb").State, Tile(payload, "sqldb").Label));
        Assert.Equal("neutral", Region(payload, "region_data").State);
        Assert.Equal(["asleep", "asleep", "asleep", "asleep", "asleep", "neutral"], payload.Edges.Select(edge => edge.State));
    }

    [Fact]
    public void AsleepIsAStateOfThePayloadsJson()
    {
        var json = Build(Answering(0), Sleep).ToJson();

        Assert.Contains("\"alias\":\"web\",\"state\":\"asleep\",\"label\":\"Asleep\",\"facts\":\"no answer, as expected\"", json, StringComparison.Ordinal);
        Assert.Contains("{\"alias\":\"aks\",\"state\":\"asleep\",\"label\":\"Asleep: cluster aks-nonprod is stopped\"}", json, StringComparison.Ordinal);
    }

    // ---------- The manifest ----------

    [Fact]
    public void TheKindsOfAClusterAreRead()
    {
        Assert.Equal(
            [RuntimeNodeKind.Person, RuntimeNodeKind.Gateway, RuntimeNodeKind.WebApp, RuntimeNodeKind.Workload, RuntimeNodeKind.Sql, RuntimeNodeKind.Workload],
            Manifest.Nodes.Select(node => node.Kind));
        Assert.Equal([RuntimeEdgeKind.Public, RuntimeEdgeKind.Route, RuntimeEdgeKind.Sql], Manifest.Edges.Select(edge => edge.Kind));
        Assert.Equal("sub.rg.aks.ns_app.web", Manifest.Nodes[2].QualifiedName);
        Assert.Equal([true, false], Manifest.Regions.Select(region => region.IsCluster));
    }

    [Theory]
    [InlineData("sub.rg.aks.ns_app.web", "aks")]
    [InlineData("sub.rg.aks.ns_app", "aks")]
    [InlineData("sub.rg.aks", null)]
    [InlineData("sub.rg_data.disk", null)]
    [InlineData("browser", null)]
    [InlineData("sub.rg.aks_other.web", null)]
    [InlineData(null, null)]
    public void WhatIsDrawnInsideAClustersFrameIsInTheCluster(string? qualifiedName, string? cluster) =>
        Assert.Equal(cluster, Manifest.ClusterOf(qualifiedName)?.Alias);

    [Fact]
    public void AManifestWithoutQualifiedNamesPutsNothingInACluster()
    {
        var manifest = RuntimeManifestParser.ParseManifest("""
            { "environment": "tdd", "nodes": [ { "alias": "db", "kind": "sql" } ], "regions": [ { "alias": "aks", "roles": [ "Cluster" ] } ] }
            """).Value!;

        Assert.True(manifest.Regions[0].IsCluster);
        Assert.Null(manifest.Nodes[0].QualifiedName);
        Assert.Null(manifest.ClusterOf(manifest.Nodes[0].QualifiedName));
    }

    [Theory]
    [InlineData("""{ "generated": "2026-10-07T02:46:44Z", "environments": [] }""", "2026-10-07T02:46:44+00:00")]
    [InlineData("""{ "generated": "yesterday", "environments": [] }""", null)]
    [InlineData("""{ "environments": [] }""", null)]
    public void TheIndexSaysWhenTheDiagramsWereRendered(string json, string? generated)
    {
        var index = RuntimeManifestParser.ParseIndex(json).Value!;

        Assert.Equal(generated is null ? null : DateTimeOffset.Parse(generated, System.Globalization.CultureInfo.InvariantCulture), index.Generated);
    }

    [Fact]
    public void ATopologySaysWhetherItCanSleepAndWhetherItCountsCalls()
    {
        var deployed = TopologyParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "topology.deploy.json"))).Topology!;
        var sample = TopologyParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "topology.sample.json"))).Topology!;

        Assert.True(deployed.CanSleep);
        Assert.False(deployed.HasTelemetry);
        Assert.False(sample.CanSleep);
        Assert.True(sample.HasTelemetry);
    }
}
