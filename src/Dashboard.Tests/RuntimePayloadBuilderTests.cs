using System.Text.Json;

namespace Dashboard.Tests;

public class RuntimePayloadBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 22, 0, 0, TimeSpan.Zero);
    private static readonly Uri Page = new("http://localhost:5210/");

    private static readonly Topology SampleTopology =
        TopologyParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "topology.sample.json"))).Topology!;

    private static RuntimeManifest Manifest(string environment) =>
        RuntimeManifestParser.ParseManifest(RuntimeManifestParserTests.Sample($"{environment}.json")).Value!;

    private static EnvironmentStatus Environment(string name) =>
        new(SampleTopology.Environments.Single(environment => environment.Name == name));

    private static ProbeResult Answer(int status, string version = "2.4.21") =>
        new(HealthClassifier.FromStatusCode(status), status, 40, Now, version);

    private static readonly string[] CheckedAliases = ["fd_ui", "app_ui_primary", "app_ui_standby"];

    private static readonly ProbeResult NoAnswer = new(HealthState.Unreachable, null, null, Now, null, "No answer within 10 s.");

    /// <summary>uat with every endpoint answering: Front Door, primary westus3, standby eastus2.</summary>
    private static EnvironmentStatus Uat(int frontDoor = 200, int primary = 200, int standby = 200, string pinned = "2.4.21")
    {
        var uat = Environment("uat");
        var ui = uat.Deployables[0];
        ui.FrontDoor!.Record(Answer(frontDoor));
        ui.Nodes[0].Record(Answer(primary));
        ui.Nodes[1].Record(standby == 0 ? NoAnswer : Answer(standby, "2.4.20"));
        uat.Record(PinnedVersions.Parse($$"""{ "ui": "{{pinned}}" }"""));
        return uat;
    }

    private static RuntimePayload Build(EnvironmentStatus? environment, string manifest = "uat") =>
        RuntimePayloadBuilder.Build(Manifest(manifest), environment, Page, TimeZoneInfo.Utc);

    private static RuntimeTile Tile(RuntimePayload payload, string alias) => payload.Nodes.Single(tile => tile.Alias == alias);

    /// <summary>A tile's lines as words and tone: what a reader sees, whatever is a link.</summary>
    private static (string Text, string Tone)[] Words(RuntimeTile tile) => [.. tile.Lines.Select(line => (line.Text, line.Tone))];

    private static string Region(RuntimePayload payload, string alias) => payload.Regions.Single(region => region.Alias == alias).State;

    private static string Edge(RuntimePayload payload, string id) => payload.Edges.Single(edge => edge.Id == id).State;

    [Fact]
    public void EveryNodeButThePersonGetsATileAndEveryRegionAndRelationshipAMark()
    {
        var payload = Build(Uat());

        Assert.Equal(["fd_ui", "app_ui_standby", "app_ui_primary", "sqldb", "swa_dashboard"], payload.Nodes.Select(tile => tile.Alias));
        Assert.Equal(["region_standby", "region_primary", "region_data"], payload.Regions.Select(region => region.Alias));
        Assert.Equal(Manifest("uat").Edges.Select(edge => edge.Id), payload.Edges.Select(edge => edge.Id));
    }

    [Fact]
    public void BeforeTheFirstCheckEveryCheckedNodeIsBeingChecked()
    {
        var payload = Build(Environment("uat"));

        Assert.All(CheckedAliases, alias =>
        {
            var tile = Tile(payload, alias);
            Assert.Equal("checking", tile.State);
            Assert.Equal("Checking", tile.Label);
            Assert.Equal("not checked yet", tile.Facts);
            Assert.Empty(tile.History!);
        });
        Assert.Equal("checking", Region(payload, "region_primary"));
        Assert.Equal("checking", Edge(payload, "fd_ui-to-app_ui_primary"));
        Assert.Equal("neutral", Region(payload, "region_data"));
    }

    [Fact]
    public void WithEveryNodeHealthyThePrimaryRegionServesAndTheStandbyWaits()
    {
        var payload = Build(Uat());

        var primary = Tile(payload, "app_ui_primary");
        Assert.Equal("healthy", primary.State);
        Assert.Equal("Healthy", primary.Label);
        Assert.Equal("HTTP 200 · 40 ms", primary.Facts);
        Assert.Equal([("version 2.4.21", "strong"), ("pinned 2.4.21: in sync", "insync"), ("primary: serves traffic", "serving")], Words(primary));
        Assert.Equal(["healthy"], primary.History!);
        Assert.Contains("https://app-cmdemo2-uat-ui.azurewebsites.net/", primary.Title, StringComparison.Ordinal);

        var standby = Tile(payload, "app_ui_standby");
        Assert.Equal([("version 2.4.20", "strong"), ("differs from pinned 2.4.21", "differs"), ("standby: ready, no traffic", "muted")], Words(standby));

        Assert.Equal(new RuntimeRegionMark("region_primary", "serving", "serving traffic"), payload.Regions.Single(region => region.Alias == "region_primary"));
        Assert.Equal(new RuntimeRegionMark("region_standby", "standby", "standby: ready"), payload.Regions.Single(region => region.Alias == "region_standby"));
        Assert.Equal("active", Edge(payload, "fd_ui-to-app_ui_primary"));
        Assert.Equal("idle", Edge(payload, "fd_ui-to-app_ui_standby"));
        Assert.Equal("active", Edge(payload, "app_ui_primary-to-sqldb"));
        Assert.Equal("idle", Edge(payload, "app_ui_standby-to-sqldb"));
        Assert.Equal("active", Edge(payload, "browser-to-fd_ui"));

        var frontDoor = Tile(payload, "fd_ui");
        Assert.Equal([("version 2.4.21", "strong"), ("routes to westus3 (priority 1)", "plain"), ("agrees with the web apps", "ok")], Words(frontDoor));
    }

    [Fact]
    public void WhenThePrimaryAnswers503TheDiagramShowsTheFailover()
    {
        var payload = Build(Uat(primary: 503));

        var primary = Tile(payload, "app_ui_primary");
        Assert.Equal("unhealthy", primary.State);
        Assert.Equal("Unhealthy", primary.Label);
        Assert.Equal("HTTP 503 · 40 ms", primary.Facts);
        Assert.Equal(new RuntimeTileLine("primary: not serving", "plain"), primary.Lines[^1]);
        Assert.Equal(new RuntimeTileLine("standby: serves traffic", "serving"), Tile(payload, "app_ui_standby").Lines[^1]);

        Assert.Equal("down", Region(payload, "region_primary"));
        Assert.Equal("serving", Region(payload, "region_standby"));
        Assert.Equal("down", Edge(payload, "fd_ui-to-app_ui_primary"));
        Assert.Equal("active", Edge(payload, "fd_ui-to-app_ui_standby"));
        Assert.Equal("idle", Edge(payload, "app_ui_primary-to-sqldb"));
        Assert.Equal("active", Edge(payload, "app_ui_standby-to-sqldb"));
        Assert.Equal(new RuntimeTileLine("routes to eastus2 (failed over)", "serving"), Tile(payload, "fd_ui").Lines[1]);
    }

    [Fact]
    public void WhenNothingIsHealthyNoRegionServes()
    {
        var payload = Build(Uat(frontDoor: 503, primary: 503, standby: 0));

        Assert.Equal("unreachable", Tile(payload, "app_ui_standby").State);
        Assert.Equal("no answer", Tile(payload, "app_ui_standby").Facts);
        Assert.Equal("down", Region(payload, "region_primary"));
        Assert.Equal("down", Region(payload, "region_standby"));
        Assert.Equal("down", Edge(payload, "browser-to-fd_ui"));
        Assert.Equal("down", Edge(payload, "fd_ui-to-app_ui_standby"));
        Assert.Equal(new RuntimeTileLine("no healthy origin", "plain"), Tile(payload, "fd_ui").Lines[1]);
        Assert.Equal(new RuntimeTileLine("agrees with the web apps", "ok"), Tile(payload, "fd_ui").Lines[2]);
    }

    [Fact]
    public void AnEnvironmentWithoutAStandbyHasNoStandbyRegionNorItsRelationships()
    {
        var tdd = Environment("tdd");
        tdd.Deployables[0].Nodes[0].Record(Answer(200));
        tdd.Deployables[0].FrontDoor!.Record(Answer(200));

        var payload = Build(tdd, "tdd");

        Assert.Equal(["fd_ui", "app_ui_primary", "sqldb", "swa_dashboard"], payload.Nodes.Select(tile => tile.Alias));
        Assert.Equal(["region_primary", "region_data"], payload.Regions.Select(region => region.Alias));
        Assert.DoesNotContain(payload.Edges, edge => edge.Id.Contains("standby", StringComparison.Ordinal));
        Assert.Equal("serving", Region(payload, "region_primary"));
        Assert.Equal("active", Edge(payload, "fd_ui-to-app_ui_primary"));
    }

    [Fact]
    public void TheDatabaseWaitsForTheHealthChecksAndTheDashboardKnowsItsOwnSite()
    {
        var tdd = Build(Environment("tdd"), "tdd");
        var uat = Build(Environment("uat"));

        Assert.Equal(("checking", "Checking"), (Tile(tdd, "sqldb").State, Tile(tdd, "sqldb").Label));
        Assert.Equal(new RuntimeTileLine("waiting for the health checks", "muted"), Assert.Single(Tile(tdd, "sqldb").Lines));
        Assert.Null(Tile(tdd, "sqldb").History);
        // The sample's tdd dashboard is http://localhost:5210, the page of dotnet run; uat's address is not known.
        Assert.Equal("This page", Tile(tdd, "swa_dashboard").Label);
        Assert.Equal(new RuntimeTileLine("its address is not in this deployment", "muted"), Assert.Single(Tile(uat, "swa_dashboard").Lines));
        Assert.Equal("neutral", Edge(uat, "browser-to-swa_dashboard"));
        Assert.Equal(new RuntimeRegionMark("region_data", "neutral", "database, static sites: not probed"), uat.Regions.Single(region => region.Alias == "region_data"));
    }

    [Fact]
    public void APassingHealthCheckOfAWebAppSaysTheDatabaseIsReachable()
    {
        var payload = Build(Uat(standby: 0));

        var database = Tile(payload, "sqldb");
        Assert.Equal(("healthy", "Reachable"), (database.State, database.Label));
        Assert.Equal(new RuntimeTileLine("health check of westus3 passed", "ok"), Assert.Single(database.Lines));
        Assert.Null(database.Facts);
        Assert.Contains("app-cmdemo2-uat-ui connected to it (last 22:00", database.Title, StringComparison.Ordinal);
        Assert.Equal(new RuntimeRegionMark("region_data", "neutral", "database: reachable; static sites: not probed"), payload.Regions.Single(region => region.Alias == "region_data"));
        Assert.Equal(new RuntimeTileLine("health checks of 2 web apps passed", "ok"), Assert.Single(Tile(Build(Uat()), "sqldb").Lines));
    }

    [Fact]
    public void FailingHealthChecksLeaveTheDatabaseNotConfirmedNotDown()
    {
        var payload = Build(Uat(frontDoor: 503, primary: 503, standby: 0));

        var database = Tile(payload, "sqldb");
        Assert.Equal(("neutral", "Not confirmed"), (database.State, database.Label));
        Assert.Equal(new RuntimeTileLine("no health check of its apps passes", "muted"), Assert.Single(database.Lines));
        Assert.Equal("database, static sites: not probed", payload.Regions.Single(region => region.Alias == "region_data").Label);
    }

    [Fact]
    public void TheLivenessProbeLeavesTheDatabaseNotProbed()
    {
        var uat = Environment("uat");
        foreach (var node in uat.Deployables[0].Nodes)
        {
            node.Record(Answer(200) with { Probe = ProbeKind.Liveness });
        }

        var database = Tile(Build(uat), "sqldb");

        Assert.Equal(("neutral", "Not probed"), (database.State, database.Label));
        Assert.Equal(new RuntimeTileLine("probe Liveness leaves it alone", "muted"), Assert.Single(database.Lines));
    }

    [Fact]
    public void ADatabaseNoCheckedWebAppUsesIsNotProbed()
    {
        var manifest = Manifest("uat") with { Edges = [.. Manifest("uat").Edges.Where(edge => edge.Kind != RuntimeEdgeKind.Sql)] };

        var database = Tile(RuntimePayloadBuilder.Build(manifest, Uat(), Page, TimeZoneInfo.Utc), "sqldb");

        Assert.Equal(("neutral", "Not probed"), (database.State, database.Label));
        Assert.Equal(new RuntimeTileLine("not probed from the browser", "muted"), Assert.Single(database.Lines));
    }

    [Fact]
    public void ANodeTheTopologyDoesNotHaveIsDrawnNeutralWithTheReason()
    {
        var manifest = Manifest("uat") with
        {
            Nodes =
            [
                .. Manifest("uat").Nodes.Where(node => node.Alias != "app_ui_standby"),
                new RuntimeNode("app_ui_standby", RuntimeNodeKind.WebApp, "app-cmdemo2-uat-ui-westeurope", new Uri("https://app-cmdemo2-uat-ui-westeurope.azurewebsites.net"), "ui", "standby", "westeurope", "region_standby"),
            ],
        };

        var payload = RuntimePayloadBuilder.Build(manifest, Uat(), Page, TimeZoneInfo.Utc);

        var tile = Tile(payload, "app_ui_standby");
        Assert.Equal(("neutral", "Not checked"), (tile.State, tile.Label));
        Assert.Equal(new RuntimeTileLine("not in topology.json", "muted"), Assert.Single(tile.Lines));
        Assert.Equal(new RuntimeRegionMark("region_standby", "neutral", "not checked"), payload.Regions.Single(region => region.Alias == "region_standby"));
        Assert.Equal("neutral", Edge(payload, "fd_ui-to-app_ui_standby"));
        Assert.Equal("healthy", Tile(payload, "app_ui_primary").State);
    }

    [Fact]
    public void AnEnvironmentTheTopologyDoesNotHaveShowsNothingAsChecked()
    {
        var payload = Build(null);

        Assert.All(payload.Nodes, tile => Assert.Equal("neutral", tile.State));
        Assert.All(payload.Regions, region => Assert.Equal("neutral", region.State));
        Assert.All(payload.Edges, edge => Assert.Equal("neutral", edge.State));
    }

    [Fact]
    public void AFrontDoorEndpointWithoutAnAddressIsNotDeployedYet()
    {
        var manifest = Manifest("uat") with
        {
            Nodes = [.. Manifest("uat").Nodes.Select(node => node.Alias == "fd_ui" ? node with { Url = null } : node)],
        };

        var tile = Tile(RuntimePayloadBuilder.Build(manifest, Uat(), Page, TimeZoneInfo.Utc), "fd_ui");

        Assert.Equal(("neutral", "No address"), (tile.State, tile.Label));
        Assert.Equal(new RuntimeTileLine("endpoint not deployed yet", "muted"), Assert.Single(tile.Lines));
    }

    [Fact]
    public void WithoutPinnedVersionsTheTileHasNoComparisonLine()
    {
        var topology = SampleTopology.Environments.Single(environment => environment.Name == "uat") with { VersionsUrl = null };
        var uat = new EnvironmentStatus(topology);
        uat.Deployables[0].Nodes[0].Record(Answer(200));

        var lines = Tile(Build(uat), "app_ui_primary").Lines;

        Assert.Equal(["version 2.4.21", "primary: serves traffic"], lines.Select(line => line.Text));
    }

    [Theory]
    [InlineData(PinnedVersionsState.Pending, "reading the pinned version")]
    [InlineData(PinnedVersionsState.Missing, "no pinned version")]
    [InlineData(PinnedVersionsState.Unavailable, "pinned version not known")]
    public void APinnedVersionThatIsNotKnownSaysWhy(PinnedVersionsState state, string text)
    {
        var uat = Environment("uat");
        uat.Deployables[0].Nodes[0].Record(Answer(200));
        uat.Record(state switch
        {
            PinnedVersionsState.Missing => PinnedVersions.Missing,
            PinnedVersionsState.Unavailable => PinnedVersions.Unavailable("HTTP 500"),
            _ => PinnedVersions.Pending,
        });

        Assert.Equal(new RuntimeTileLine(text, "unknown"), Tile(Build(uat), "app_ui_primary").Lines[1]);
    }

    [Fact]
    public void WithoutTelemetryTheNumberLinesShowADash()
    {
        var payload = Build(Uat());

        var origin = payload.Edges.Single(edge => edge.Id == "fd_ui-to-app_ui_standby");
        Assert.Equal((RuntimePayloadBuilder.NoNumber, "calls/min", "when priority 1 is down"), (origin.Number, origin.Unit, origin.Text));
        Assert.Equal("first, while healthy", payload.Edges.Single(edge => edge.Id == "fd_ui-to-app_ui_primary").Text);
        Assert.Equal("queries of the app", payload.Edges.Single(edge => edge.Id == "app_ui_primary-to-sqldb").Text);
        Assert.Equal((RuntimePayloadBuilder.NoNumber, null), (payload.Edges.Single(edge => edge.Id == "browser-to-fd_ui").Number, payload.Edges.Single(edge => edge.Id == "browser-to-fd_ui").Text));
    }

    /// <summary>
    /// A cluster's environment: the app is one node at one public address, without a Front Door endpoint, and the
    /// database runs next to it. The diagram has the kinds the page already updates.
    /// </summary>
    private static readonly RuntimeManifest ClusterManifest = new(
        "uat",
        [
            new RuntimeNode("browser", RuntimeNodeKind.Person, "Browser"),
            new RuntimeNode("app_ui_primary", RuntimeNodeKind.WebApp, "uat-ui", new Uri("https://ui.uat.example.net"), "ui", "primary", "westus3", "region_primary"),
            new RuntimeNode("sqldb", RuntimeNodeKind.Sql, "sql-uat", null, null, null, "westus3", "region_primary"),
        ],
        [new RuntimeRegion("region_primary", "westus3", ["primary", "data"])],
        [
            new RuntimeEdge("browser-to-app_ui_primary", "browser", "app_ui_primary", RuntimeEdgeKind.Public),
            new RuntimeEdge("app_ui_primary-to-sqldb", "app_ui_primary", "sqldb", RuntimeEdgeKind.Sql),
        ]);

    private static readonly string[] WordsOfSeveralNodes = ["primary", "standby", "region", "failover", "failed over", "Front Door"];

    private static EnvironmentStatus Cluster(string? frontDoor = null)
    {
        var address = frontDoor is null ? "null" : $"\"{frontDoor}\"";
        return new EnvironmentStatus(TopologyParser.Parse($$"""
            { "environments": [ { "name": "uat", "deployables": [ {
                "name": "ui", "frontDoor": {{address}},
                "nodes": [ { "name": "uat-ui", "role": "primary", "url": "https://ui.uat.example.net" } ] } ] } ] }
            """).Topology!.Environments[0]);
    }

    [Theory]
    [InlineData(null, "checking", "checking", "muted")]
    [InlineData(200, "healthy", "serves traffic", "serving")]
    [InlineData(503, "unhealthy", "not serving", "plain")]
    [InlineData(0, "unreachable", "not serving", "plain")]
    public void TheOnlyNodeOfADeployableWithoutAFrontDoorServesOrDoesNotAndHasNoRole(int? status, string state, string line, string tone)
    {
        var uat = Cluster();
        if (status is { } answered)
        {
            uat.Deployables[0].Nodes[0].Record(answered == 0 ? NoAnswer : Answer(answered));
        }

        var payload = RuntimePayloadBuilder.Build(ClusterManifest, uat, Page, TimeZoneInfo.Utc);

        var tile = Tile(payload, "app_ui_primary");
        Assert.Equal(state, tile.State);
        Assert.Equal(new RuntimeTileLine(line, tone), tile.Lines[^1]);
        Assert.All(WordsOfSeveralNodes, word =>
        {
            Assert.All(tile.Lines, text => Assert.DoesNotContain(word, text.Text, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(word, tile.Title, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(word, uat.Deployables[0].Assess().Headline, StringComparison.OrdinalIgnoreCase);
        });

        // The states of the diagram are the ones a Front Door system has.
        var expected = status switch { null => "checking", 200 => "active", _ => "down" };
        Assert.Equal(expected, Edge(payload, "browser-to-app_ui_primary"));
        Assert.Equal(status switch { null => "checking", 200 => "serving", _ => "down" }, Region(payload, "region_primary"));
    }

    [Fact]
    public void AnOnlyNodeBehindAFrontDoorKeepsItsRole()
    {
        var uat = Cluster("https://fd-uat.example.net");
        uat.Deployables[0].FrontDoor!.Record(Answer(200));
        uat.Deployables[0].Nodes[0].Record(Answer(200));
        var tdd = Environment("tdd");
        tdd.Deployables[0].Nodes[0].Record(Answer(503));

        Assert.Equal(new RuntimeTileLine("primary: serves traffic", "serving"), Tile(RuntimePayloadBuilder.Build(ClusterManifest, uat, Page, TimeZoneInfo.Utc), "app_ui_primary").Lines[^1]);

        // The sample's tdd: one web app behind its Front Door endpoint.
        Assert.Equal(new RuntimeTileLine("primary: not serving", "plain"), Tile(Build(tdd, "tdd"), "app_ui_primary").Lines[^1]);
        Assert.Equal(new RuntimeTileLine("primary", "muted"), Tile(Build(Environment("tdd"), "tdd"), "app_ui_primary").Lines[^1]);
    }

    [Fact]
    public void TheDatabasesWordsNameNoProduct()
    {
        var uat = Cluster();
        uat.Deployables[0].Nodes[0].Record(Answer(200));
        var unused = ClusterManifest with { Edges = [.. ClusterManifest.Edges.Where(edge => edge.Kind != RuntimeEdgeKind.Sql)] };

        var reachable = Tile(RuntimePayloadBuilder.Build(ClusterManifest, uat, Page, TimeZoneInfo.Utc), "sqldb");
        var notProbed = Tile(RuntimePayloadBuilder.Build(unused, uat, Page, TimeZoneInfo.Utc), "sqldb");

        Assert.Equal(("healthy", "Reachable"), (reachable.State, reachable.Label));
        Assert.Equal("sql-uat: reachable. The database takes no call from a browser; the health check of uat-ui connected to it (last 22:00:00).", reachable.Title);
        Assert.Equal(new RuntimeTileLine("health check of uat-ui passed", "ok"), Assert.Single(reachable.Lines));
        Assert.Equal("sql-uat: the database takes no call from a browser, and this page checks no web app that uses it.", notProbed.Title);

        // The same words in a Front Door system.
        Assert.StartsWith("sqldb-cmdemo2-uat: reachable. The database takes no call from a browser; the health check of ", Tile(Build(Uat()), "sqldb").Title, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePayloadIsCamelCaseJsonWithoutNulls()
    {
        using var json = JsonDocument.Parse(Build(Environment("uat")).ToJson());

        var root = json.RootElement;
        Assert.Equal(["nodes", "regions", "edges"], root.EnumerateObject().Select(property => property.Name));
        var sql = root.GetProperty("nodes").EnumerateArray().Single(node => node.GetProperty("alias").GetString() == "sqldb");
        // The sample topology has a link to the database: the name of its node leads there.
        Assert.Equal(["alias", "state", "label", "lines", "title", "nameLink"], sql.EnumerateObject().Select(property => property.Name));
        Assert.Equal(["href", "title"], sql.GetProperty("nameLink").EnumerateObject().Select(property => property.Name));
        Assert.Equal("muted", sql.GetProperty("lines")[0].GetProperty("tone").GetString());
        var edge = root.GetProperty("edges")[0];
        Assert.Equal(["id", "state", "number", "unit", "title"], edge.EnumerateObject().Select(property => property.Name));
    }
}
