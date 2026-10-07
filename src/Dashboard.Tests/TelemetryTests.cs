using System.Net;

namespace Dashboard.Tests;

public class TelemetryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 22, 0, 0, TimeSpan.Zero);
    private static readonly Uri Page = new("http://localhost:5210/");

    private const string Answer = """
        {
          "windowSeconds": 60,
          "startedAt": "2026-10-05T23:00:00Z",
          "requests": { "perMinute": 12, "frontDoor": 10, "direct": 2, "errors": 0, "p95Ms": 85 },
          "probes": { "perMinute": 4, "frontDoor": 6 },
          "sql": { "perMinute": 30, "p95Ms": 12 },
          "http": { "perMinute": 1 }
        }
        """;

    private const string WithTelemetry = """
        {
          "system": { "slug": "demo", "name": "Demo" },
          "environments": [
            { "name": "uat", "deployables": [ {
                "name": "ui", "frontDoor": "https://fd-uat.example.net", "telemetryPath": "_telemetry",
                "trafficPaths": [ "/", "api/workorders/summary" ],
                "nodes": [
                  { "name": "uat-west", "region": "westus3", "role": "primary", "url": "https://uat-west.example.net" },
                  { "name": "uat-east", "region": "eastus2", "role": "standby", "url": "https://uat-east.example.net" } ] } ] },
            { "name": "dev", "deployables": [ {
                "name": "ui",
                "nodes": [ { "name": "dev-west", "region": "westus3", "role": "primary", "url": "https://dev-west.example.net" } ] } ] },
            { "name": "empty", "deployables": [] }
          ]
        }
        """;

    private static TelemetrySnapshot Snapshot(int frontDoor = 10, int direct = 2, int errors = 0, int probes = 6, int sql = 30) =>
        new(frontDoor + direct, frontDoor, direct, errors, 85, 4, probes, sql, 12, 1, Now);

    [Fact]
    public void TheEndpointsAnswerIsRead()
    {
        var snapshot = TelemetrySnapshot.Parse(Answer, Now);

        Assert.Equal(
            new TelemetrySnapshot(12, 10, 2, 0, 85, 4, 6, 30, 12, 1, Now) { StartedAt = new DateTimeOffset(2026, 10, 5, 23, 0, 0, TimeSpan.Zero) },
            snapshot);
        Assert.Null(snapshot!.Process);
    }

    [Theory]
    [InlineData("<html>Not found</html>")]
    [InlineData("[]")]
    [InlineData("""{ "status": "Healthy" }""")]
    public void AnythingButTheExpectedJsonIsNoTelemetry(string body)
    {
        Assert.Null(TelemetrySnapshot.Parse(body, Now));
    }

    [Fact]
    public void MissingSectionsCountZeroAndMissingLatenciesAreNull()
    {
        var snapshot = TelemetrySnapshot.Parse("""{ "requests": { "perMinute": 3, "frontDoor": 3 } }""", Now);

        Assert.Equal(new TelemetrySnapshot(3, 3, 0, 0, null, 0, 0, 0, null, 0, Now), snapshot);
    }

    [Fact]
    public void TheTopologyCarriesTheTelemetryPathAndTheTrafficPaths()
    {
        var topology = TopologyParser.Parse(WithTelemetry).Topology!;

        var ui = topology.Environments[0].Deployables[0];
        Assert.Equal("/_telemetry", ui.TelemetryPath);
        Assert.Equal(["/", "/api/workorders/summary"], ui.TrafficPaths);
        Assert.Null(topology.Environments[1].Deployables[0].TelemetryPath);
        Assert.Null(topology.Environments[1].Deployables[0].TrafficPaths);
    }

    [Fact]
    public void ATrafficPathThatIsNotTextIsAnError()
    {
        var result = TopologyParser.Parse(WithTelemetry.Replace("\"api/workorders/summary\"", "42", StringComparison.Ordinal));

        Assert.Contains(result.Errors, error => error.Contains("trafficPaths", StringComparison.Ordinal));
    }

    [Fact]
    public void TrafficGoesToTheFrontDoorAddressOrElseThePrimaryWebApp()
    {
        var topology = TopologyParser.Parse(WithTelemetry).Topology!;

        var uat = TrafficPlan.For(topology.Environments[0])!;
        var dev = TrafficPlan.For(topology.Environments[1])!;

        Assert.Equal(["https://fd-uat.example.net/", "https://fd-uat.example.net/api/workorders/summary"], uat.Addresses.Select(address => address.AbsoluteUri));
        Assert.Equal(["ui at fd-uat.example.net"], uat.Targets);
        Assert.Equal(["https://dev-west.example.net/"], dev.Addresses.Select(address => address.AbsoluteUri));
        Assert.Null(TrafficPlan.For(topology.Environments[2]));
    }

    [Fact]
    public async Task ARoundReadsTheTelemetryOfEveryWebAppButNotOfFrontDoor()
    {
        var time = new SignallingTimeProvider();
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/_telemetry" when request.RequestUri.Host == "uat-west.example.net" => StubHandler.Answer(HttpStatusCode.OK, Answer),
            "/_telemetry" => StubHandler.Answer(HttpStatusCode.NotFound),
            _ => StubHandler.Answer(HttpStatusCode.OK, "Healthy"),
        });
        var http = new HttpClient(handler);
        var monitor = new DashboardMonitor(TopologyParser.Parse(WithTelemetry).Topology!, new NodeProber(http, time), new PinnedVersionsReader(http, time), time);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        var ui = monitor.Environments[0].Deployables[0];
        Assert.Equal(12, ui.Nodes[0].Telemetry!.Requests);
        Assert.Null(ui.Nodes[1].Telemetry);
        Assert.Null(ui.FrontDoor!.Telemetry);
        Assert.DoesNotContain("https://fd-uat.example.net/_telemetry", handler.RequestedUrls);
        Assert.DoesNotContain(handler.RequestedUrls, url => url.StartsWith("https://dev-west.example.net/_telemetry", StringComparison.Ordinal));
    }

    private static RuntimeManifest Manifest() =>
        RuntimeManifestParser.ParseManifest(RuntimeManifestParserTests.Sample("uat.json")).Value!;

    private static EnvironmentStatus Uat(TelemetrySnapshot? primary, TelemetrySnapshot? standby)
    {
        var topology = TopologyParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "topology.sample.json"))).Topology!;
        var uat = new EnvironmentStatus(topology.Environments.Single(environment => environment.Name == "uat"));
        var ui = uat.Deployables[0];
        ui.FrontDoor!.Record(new ProbeResult(HealthState.Healthy, 200, 40, Now, "2.4.21"));
        ui.Nodes[0].Record(new ProbeResult(HealthState.Healthy, 200, 40, Now, "2.4.21"));
        ui.Nodes[1].Record(new ProbeResult(HealthState.Healthy, 200, 40, Now, "2.4.21"));
        ui.Nodes[0].RecordTelemetry(primary);
        ui.Nodes[1].RecordTelemetry(standby);
        return uat;
    }

    private static RuntimeEdgeMark Edge(RuntimePayload payload, string id) => payload.Edges.Single(edge => edge.Id == id);

    [Fact]
    public void EveryRelationshipShowsTheCountedCalls()
    {
        var payload = RuntimePayloadBuilder.Build(Manifest(), Uat(Snapshot(), Snapshot(frontDoor: 0, direct: 0, probes: 7, sql: 3)), Page, TimeZoneInfo.Utc);

        Assert.Equal(("10", "calls/min", "first, while healthy · 6 probes"), Triple(Edge(payload, "fd_ui-to-app_ui_primary")));
        Assert.Equal(("0", "calls/min", "when priority 1 is down · 7 probes"), Triple(Edge(payload, "fd_ui-to-app_ui_standby")));
        Assert.Equal("30", Edge(payload, "app_ui_primary-to-sqldb").Number);
        Assert.Equal("3", Edge(payload, "app_ui_standby-to-sqldb").Number);
        Assert.Equal("10", Edge(payload, "browser-to-fd_ui").Number);
        Assert.Contains("30 SQL commands, p95 12 ms", Edge(payload, "app_ui_primary-to-sqldb").Title, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutTelemetryEveryNumberIsADash()
    {
        var payload = RuntimePayloadBuilder.Build(Manifest(), Uat(null, null), Page, TimeZoneInfo.Utc);

        Assert.All(payload.Edges.Where(edge => edge.Number is not null), edge => Assert.Equal(RuntimePayloadBuilder.NoNumber, edge.Number));
        Assert.Equal(RuntimePayloadBuilder.NoNumber, Edge(payload, "browser-to-fd_ui").Number);
        Assert.DoesNotContain(payload.Nodes.Single(tile => tile.Alias == "app_ui_primary").Lines, line => line.Text.Contains("req/min", StringComparison.Ordinal));
        Assert.Null(payload.Nodes.Single(tile => tile.Alias == "sqldb").Facts);
    }

    [Fact]
    public void TheWebAppTileShowsItsTrafficAndTheDatabaseItsQueries()
    {
        var payload = RuntimePayloadBuilder.Build(Manifest(), Uat(Snapshot(errors: 2), Snapshot(frontDoor: 0, direct: 0, sql: 3)), Page, TimeZoneInfo.Utc);

        var primary = payload.Nodes.Single(tile => tile.Alias == "app_ui_primary");
        Assert.Equal(
            [("12 req/min · p95 85 ms", "plain"), ("2 errors", "warn")],
            primary.Lines.Skip(2).Take(2).Select(line => (line.Text, line.Tone)));
        Assert.Equal("33 queries/min", payload.Nodes.Single(tile => tile.Alias == "sqldb").Facts);
    }

    private static TelemetrySnapshot Split(int requests, int background) =>
        Snapshot(sql: requests + background) with { SqlRequests = requests, SqlBackground = background };

    [Fact]
    public void TheSqlCommandsOfRequestsAndOfTheBackgroundAreReadWhereTheAppTellsThemApart()
    {
        var split = TelemetrySnapshot.Parse("""{ "requests": { "perMinute": 12 }, "sql": { "perMinute": 117, "requests": 62, "background": 55, "p95Ms": 12 } }""", Now)!;
        var older = TelemetrySnapshot.Parse(Answer, Now)!;
        var half = TelemetrySnapshot.Parse("""{ "requests": { }, "sql": { "perMinute": 117, "requests": 62 } }""", Now)!;

        Assert.Equal((117, 62, 55, true, 62), (split.Sql, split.SqlRequests, split.SqlBackground, split.SplitsSql, split.SqlOfTraffic));
        Assert.Equal((30, null, null, false, 30), (older.Sql, older.SqlRequests, older.SqlBackground, older.SplitsSql, older.SqlOfTraffic));
        // One of the two is not enough to tell them apart: the total stays the number.
        Assert.Equal((false, 117), (half.SplitsSql, half.SqlOfTraffic));
    }

    [Fact]
    public void TheDatabaseArrowShowsTheQueriesOfTheRequestsAndNamesTheBackgroundOnes()
    {
        var payload = RuntimePayloadBuilder.Build(Manifest(), Uat(Split(62, 55), Split(0, 48)), Page, TimeZoneInfo.Utc);

        var primary = Edge(payload, "app_ui_primary-to-sqldb");
        Assert.Equal(("62", "calls/min", "queries of the app · 55 background"), Triple(primary));
        Assert.Contains("62 SQL commands while handling requests (the number shown: what traffic causes) and 55 in the background (mostly the message bus polling the database), 117 in all, p95 12 ms", primary.Title, StringComparison.Ordinal);
        Assert.Equal(("0", "calls/min", "queries of the app · 48 background"), Triple(Edge(payload, "app_ui_standby-to-sqldb")));

        var database = payload.Nodes.Single(tile => tile.Alias == "sqldb");
        Assert.Equal("62 queries/min", database.Facts);
        Assert.Contains("62 while handling requests, 103 in the background (mostly the message bus polling the database)", database.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOlderAppThatDoesNotTellThemApartKeepsTheTotalOnItsArrow()
    {
        var payload = RuntimePayloadBuilder.Build(Manifest(), Uat(Split(62, 55), Snapshot(sql: 9)), Page, TimeZoneInfo.Utc);

        var standby = Edge(payload, "app_ui_standby-to-sqldb");
        Assert.Equal(("9", "calls/min", "queries of the app"), Triple(standby));
        Assert.Contains("counted by the web app: 9 SQL commands, p95 12 ms;", standby.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("background", standby.Title, StringComparison.Ordinal);

        // The database adds what each app's traffic causes: the requests' queries of the one, all queries of the other.
        var database = payload.Nodes.Single(tile => tile.Alias == "sqldb");
        Assert.Equal("71 queries/min", database.Facts);
        Assert.Contains("71 while handling requests, 55 in the background", database.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutTheSplitTheDatabaseTooltipSaysNothingOfTheBackground()
    {
        var payload = RuntimePayloadBuilder.Build(Manifest(), Uat(Snapshot(), Snapshot(sql: 3)), Page, TimeZoneInfo.Utc);

        Assert.DoesNotContain("background", payload.Nodes.Single(tile => tile.Alias == "sqldb").Title, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSparklineOfTheDatabaseArrowFollowsTheNumberShown()
    {
        var uat = Uat(Snapshot(sql: 100), null);
        var node = uat.Deployables[0].Nodes[0];

        // An older app: the trend of all its commands.
        node.RecordTelemetry(Snapshot(sql: 50));
        Assert.Equal("SQL commands per minute, last 2 checks: 50 to 100, now 50", Trends.Sql(node)!.Title);

        // Once it tells them apart, the trend is of the requests' commands; the readings from before have none.
        node.RecordTelemetry(Split(20, 55));
        Assert.Null(Trends.Sql(node));
        node.RecordTelemetry(Split(40, 60));
        var trend = Trends.Sql(node)!;
        Assert.Equal("SQL commands per minute while handling requests, last 2 checks: 20 to 40, now 40", trend.Title);

        var edge = Edge(RuntimePayloadBuilder.Build(Manifest(), uat, Page, TimeZoneInfo.Utc), "app_ui_primary-to-sqldb");
        Assert.Equal("40", edge.Number);
        Assert.Equal(trend.Points, edge.Trend!.Points);
    }

    private static (string?, string?, string?) Triple(RuntimeEdgeMark mark) => (mark.Number, mark.Unit, mark.Text);
}
