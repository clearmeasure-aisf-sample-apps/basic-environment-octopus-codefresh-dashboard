namespace Dashboard.Tests;

public class LinkTests
{
    private readonly SignallingTimeProvider _time = new();

    private static readonly Topology Parsed = TopologyParser.Parse(Optics.Topology).Topology!;

    private static readonly RuntimeManifest Manifest = new(
        "uat",
        [
            new RuntimeNode("browser", RuntimeNodeKind.Person, "Browser"),
            new RuntimeNode("fd_ui", RuntimeNodeKind.FrontDoor, "demo-uat-ui", new Uri("https://fd-uat.example.net"), "ui"),
            new RuntimeNode("app_ui_primary", RuntimeNodeKind.WebApp, "uat-west", new Uri("https://uat-west.example.net"), "ui", "primary", "westus3", "region_primary"),
            new RuntimeNode("app_ui_standby", RuntimeNodeKind.WebApp, "uat-east", new Uri("https://uat-east.example.net"), "ui", "standby", "eastus2", "region_standby"),
            new RuntimeNode("sqldb", RuntimeNodeKind.Sql, "sqldb-demo-uat", null, null, null, "centralus", "region_data"),
        ],
        [],
        [
            new RuntimeEdge("browser-to-fd_ui", "browser", "fd_ui", RuntimeEdgeKind.Public),
            new RuntimeEdge("fd_ui-to-app_ui_primary", "fd_ui", "app_ui_primary", RuntimeEdgeKind.Origin, 1),
            new RuntimeEdge("fd_ui-to-app_ui_standby", "fd_ui", "app_ui_standby", RuntimeEdgeKind.Origin, 2),
            new RuntimeEdge("app_ui_primary-to-sqldb", "app_ui_primary", "sqldb", RuntimeEdgeKind.Sql),
            new RuntimeEdge("app_ui_standby-to-sqldb", "app_ui_standby", "sqldb", RuntimeEdgeKind.Sql),
        ]);

    private async Task<RuntimePayload> PayloadAsync(Func<HttpRequestMessage, HttpResponseMessage>? respond = null, string topology = Optics.Topology)
    {
        var monitor = Optics.Monitor(new StubHandler(respond ?? (request => Optics.Answer(request))), _time, topology: topology);
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        return RuntimePayloadBuilder.Build(Manifest, monitor.Environments[1], null, TimeZoneInfo.Utc);
    }

    [Fact]
    public void TheTopologyCarriesTheLinksOfEnvironmentsDeployablesAndNodes()
    {
        var uat = Parsed.Environments[1];
        var ui = uat.Deployables[0];

        Assert.Equal("https://portal.example.net/#sqldb-uat", uat.Links![LinkSet.Database]?.AbsoluteUri);
        Assert.Null(uat.Links[LinkSet.ResourceGroup]);
        Assert.Equal("https://portal.example.net/#afd", ui.Links![LinkSet.FrontDoor]?.AbsoluteUri);
        Assert.Equal(
            [LinkSet.Portal, LinkSet.LiveMetrics, LinkSet.Performance, LinkSet.Failures, LinkSet.Dependencies],
            ui.Nodes[0].Links!.Keys);
        Assert.Null(ui.Nodes[1].Links);
        Assert.Equal("https://portal.example.net/#rg", Parsed.Environments[0].Links![LinkSet.ResourceGroup]?.AbsoluteUri);
    }

    [Fact]
    public void ALinkThatIsNoHttpAddressIsLeftOutAndIsNoError()
    {
        var links = Parsed.Environments[1].Deployables[0].Links!;

        Assert.Equal([LinkSet.FrontDoor, LinkSet.Logs], links.Keys);
        Assert.Null(links["broken"]);
        Assert.Null(links["script"]);
    }

    [Theory]
    [InlineData("""{ "environments": [ { "name": "tdd", "links": [], "deployables": [ { "links": "none", "nodes": [ { "url": "https://a.example.net", "links": null } ] } ] } ] }""")]
    [InlineData("""{ "environments": [ { "name": "tdd", "links": { }, "deployables": [ { "links": { "portal": 4 }, "nodes": [ { "url": "https://a.example.net", "links": { "portal": "ftp://a" } } ] } ] } ] }""")]
    public void LinksOfAnotherShapeAreNoLinks(string json)
    {
        var environment = TopologyParser.Parse(json).Topology!.Environments[0];

        Assert.Null(environment.Links);
        Assert.Null(environment.Deployables[0].Links);
        Assert.Null(environment.Deployables[0].Nodes[0].Links);
    }

    [Fact]
    public void AnEncodedQueryInALinkStaysAsItWasWritten() =>
        Assert.Equal(
            "https://portal.example.net/#logs/q/H4sI%2Bx%3D%3D/timespan/PT1H",
            Parsed.Environments[1].Deployables[0].Links![LinkSet.Logs]!.AbsoluteUri);

    [Fact]
    public void TheLinksOfTheSampleTopologyKeepTheirPortalAddresses()
    {
        var topology = TopologyParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "topology.sample.json"))).Topology!;
        var uat = topology.Environments.Single(environment => environment.Name == "uat");
        var node = uat.Deployables[0].Nodes[1];
        const string Group = "https://portal.azure.com/#@00000000-0000-0000-0000-000000000000/resource/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-cmdemo2-nonprod";

        Assert.Equal($"{Group}/providers/Microsoft.Web/sites/app-cmdemo2-uat-ui-eastus2/appServices", node.Links![LinkSet.Portal]!.AbsoluteUri);
        Assert.Equal($"{Group}/providers/microsoft.insights/components/appi-cmdemo2-uat/quickPulse", node.Links[LinkSet.LiveMetrics]!.AbsoluteUri);
        Assert.Equal($"{Group}/providers/microsoft.insights/components/appi-cmdemo2-uat/performance", node.Links[LinkSet.Performance]!.AbsoluteUri);
        Assert.Equal($"{Group}/providers/microsoft.insights/components/appi-cmdemo2-uat/failures", node.Links[LinkSet.Failures]!.AbsoluteUri);
        Assert.Contains("/blade/Microsoft_Azure_Monitoring_Logs/LogsBlade/resourceId/%2Fsubscriptions%2F", node.Links[LinkSet.Dependencies]!.AbsoluteUri, StringComparison.Ordinal);
        Assert.EndsWith("/timespan/PT1H", node.Links[LinkSet.Dependencies]!.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal($"{Group}/providers/Microsoft.Sql/servers/sql-cmdemo2-uat-def45/databases/sqldb-cmdemo2-uat/overview", uat.Links![LinkSet.Database]!.AbsoluteUri);
        Assert.Equal($"{Group}/overview", uat.Links[LinkSet.ResourceGroup]!.AbsoluteUri);
        Assert.EndsWith("/resourceGroups/rg-cmdemo2-edge/providers/Microsoft.Cdn/profiles/afd-cmdemo2/overview", uat.Deployables[0].Links![LinkSet.FrontDoor]!.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("/_build", uat.Deployables[0].BuildPath);
        Assert.Equal("/_telemetry", uat.Deployables[0].TelemetryPath);
    }

    [Fact]
    public void TheReleasePageIsBuiltFromTheProjectAndTheVersion()
    {
        var project = new Uri("https://octopus.example.net/app#/Spaces-1/projects/demo-ui");

        Assert.Equal("https://octopus.example.net/app#/Spaces-1/projects/demo-ui/deployments/releases/2.4.15", LinkText.Release(project, "2.4.15")!.AbsoluteUri);
        Assert.Null(LinkText.Release(null, "2.4.15"));
        Assert.Null(LinkText.Release(project, null));
        Assert.Equal("Release 2.4.15 of ui in Octopus Deploy (opens in a new tab; Octopus Deploy asks you to sign in)", LinkText.ReleaseTitle("ui", "2.4.15"));
    }

    [Fact]
    public void EveryLinksTitleSaysWhereItGoesAndThatThePortalAsksForASignIn()
    {
        string[] keys =
        [
            LinkSet.Portal, LinkSet.LiveMetrics, LinkSet.Performance, LinkSet.Failures, LinkSet.Dependencies, LinkSet.FrontDoor,
            LinkSet.Logs, LinkSet.ApplicationInsights, LinkSet.ApplicationMap, LinkSet.Database, LinkSet.ResourceGroup,
        ];

        Assert.All(keys, key =>
        {
            var title = LinkText.For(key, "uat-west");
            Assert.Contains("uat-west", title, StringComparison.Ordinal);
            Assert.EndsWith("(opens in a new tab; the Azure portal asks you to sign in)", title, StringComparison.Ordinal);
        });
        Assert.Equal(keys.Length, keys.Select(key => LinkText.For(key, "x")).Distinct().Count());
    }

    [Fact]
    public async Task TheWebAppTileLinksItsStateItsNameItsVersionAndItsNumbers()
    {
        var payload = await PayloadAsync();
        var tile = payload.Nodes.Single(node => node.Alias == "app_ui_primary");

        Assert.Equal("https://portal.example.net/#appi-uat/quickPulse", tile.Link?.Href);
        Assert.Equal("https://portal.example.net/#west", tile.NameLink?.Href);
        Assert.Equal(LinkText.For(LinkSet.Portal, "uat-west"), tile.NameLink?.Title);

        var version = tile.Lines[0].Parts!;
        Assert.Equal(["version ", "2.4.15"], version.Select(part => part.Text));
        Assert.Null(version[0].Link);
        Assert.Equal("https://octopus.example.net/app#/Spaces-1/projects/demo-ui/deployments/releases/2.4.15", version[1].Link?.Href);

        var traffic = tile.Lines.Single(line => line.Text.Contains("req/min", StringComparison.Ordinal)).Parts!;
        Assert.Equal(("12 req/min", "https://portal.example.net/#appi-uat/performance"), (traffic[0].Text, traffic[0].Link?.Href));
        Assert.Null(traffic[1].Link);

        var failed = tile.Lines.Single(line => line.Text.Contains("errors", StringComparison.Ordinal)).Parts!;
        Assert.Equal(("0 errors", "https://portal.example.net/#appi-uat/failures"), (failed[0].Text, failed[0].Link?.Href));
    }

    [Fact]
    public async Task ANodeWithoutLinksIsDrawnAsBefore()
    {
        var payload = await PayloadAsync();
        var standby = payload.Nodes.Single(node => node.Alias == "app_ui_standby");

        Assert.Null(standby.Link);
        Assert.Null(standby.NameLink);
        // The project's page is known for the deployable, so the version still leads to its release.
        Assert.NotNull(standby.Lines[0].Parts);
        Assert.All(standby.Lines.Skip(1), line => Assert.Null(line.Parts));
    }

    [Fact]
    public async Task TheDatabaseAndTheFrontDoorEndpointLinkTheirNames()
    {
        var payload = await PayloadAsync();

        Assert.Equal("https://portal.example.net/#sqldb-uat", payload.Nodes.Single(node => node.Alias == "sqldb").NameLink?.Href);
        var frontDoor = payload.Nodes.Single(node => node.Alias == "fd_ui");
        Assert.Equal("https://portal.example.net/#afd", frontDoor.NameLink?.Href);
        Assert.Null(frontDoor.Link);
        // Front Door answers with the version of whichever node served: its version is no link.
        Assert.Null(frontDoor.Lines[0].Parts);
    }

    [Fact]
    public async Task TheNumbersOnTheArrowsLeadToRequestsAndDependencies()
    {
        var payload = await PayloadAsync();
        RuntimeEdgeMark Edge(string id) => payload.Edges.Single(edge => edge.Id == id);

        Assert.Equal("https://portal.example.net/#appi-uat/performance", Edge("fd_ui-to-app_ui_primary").Link?.Href);
        Assert.Equal("https://portal.example.net/#appi-uat/dependencies", Edge("app_ui_primary-to-sqldb").Link?.Href);
        Assert.Equal("https://portal.example.net/#logs/q/H4sI%2Bx%3D%3D/timespan/PT1H", Edge("browser-to-fd_ui").Link?.Href);
        Assert.Null(Edge("fd_ui-to-app_ui_standby").Link);
        Assert.Null(Edge("app_ui_standby-to-sqldb").Link);
    }

    [Fact]
    public async Task WithoutAProjectTheVersionLeadsToTheCommitOfItsBuild()
    {
        var payload = await PayloadAsync(topology: Optics.Topology.Replace("projectUrl", "ignored", StringComparison.Ordinal));
        var version = payload.Nodes.Single(node => node.Alias == "app_ui_primary").Lines[0].Parts!;

        Assert.Equal("https://github.example.net/o/r/commit/0a1b2c3d4e5f60718293a4b5c6d7e8f901234567", version[1].Link?.Href);
        Assert.Equal("Commit 0a1b2c3 on GitHub (opens in a new tab)", version[1].Link?.Title);
    }

    [Fact]
    public async Task ABuildOfAnotherVersionIsNotTheCommitOfTheRunningOne()
    {
        var payload = await PayloadAsync(request => Optics.Answer(request, "2.4.16"), Optics.Topology.Replace("projectUrl", "ignored", StringComparison.Ordinal));

        Assert.Null(payload.Nodes.Single(node => node.Alias == "app_ui_primary").Lines[0].Parts);
    }

    [Fact]
    public void ALineOfPiecesIsItsWordsInARowAndKeepsThePiecesOnlyForALink()
    {
        var plain = RuntimeTileLine.Of("plain", [new RuntimeTextPart("12 req/min"), new RuntimeTextPart(" · p95 85 ms")]);
        var linked = RuntimeTileLine.Of("plain", [new RuntimeTextPart("12 req/min", new RuntimeLink("https://a.example.net/", "a")), new RuntimeTextPart(" · p95 85 ms")]);

        Assert.Equal(new RuntimeTileLine("12 req/min · p95 85 ms"), plain);
        Assert.Equal("12 req/min · p95 85 ms", linked.Text);
        Assert.Equal(2, linked.Parts!.Count);
        Assert.Null(RuntimeLink.To(null, "nowhere"));
    }
}
