namespace Dashboard.Tests;

public class TrendTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 22, 0, 0, TimeSpan.Zero);
    private readonly SignallingTimeProvider _time = new();

    private static TelemetrySnapshot Sample(int requests, int sql = 0, double? cpu = null) =>
        new(requests, requests, 0, 0, null, 0, 0, sql, null, 0, Now) { Process = cpu is null ? null : new ProcessVitals(cpu, null, null, null, null, null, 600) };

    private static TargetStatus Node(params TelemetrySnapshot?[] samples)
    {
        var node = new TargetStatus(TargetKind.Node, "uat-west", new Uri("https://uat-west.example.net"), "westus3", "primary");
        foreach (var sample in samples)
        {
            node.RecordTelemetry(sample);
        }

        return node;
    }

    [Fact]
    public void TheReadingsBecomeHeightsOnAScaleFromZeroToTheLargest()
    {
        var trend = Trend.Of([0, 10, 40, 20], "Requests per minute")!;

        Assert.Equal([0, 0.25, 1, 0.5], trend.Points);
        Assert.Equal("Requests per minute, last 4 checks: 0 to 40, now 20", trend.Title);
    }

    [Fact]
    public void TheUnitFollowsTheNumbersAndDecimalsAreKept() =>
        Assert.Equal("CPU of the process, last 3 checks: 2.5 to 14 %, now 3.2 %", Trend.Of([2.5, 14, 3.2], "CPU of the process", " %")!.Title);

    [Fact]
    public void ANumberThatStaysAtZeroIsAFlatLineAtTheBottom() => Assert.Equal([0, 0, 0], Trend.Of([0, 0, 0], "SQL commands per minute")!.Points);

    [Theory]
    [InlineData(new double[0])]
    [InlineData(new[] { 7d })]
    public void FewerThanTwoReadingsAreNoTrend(double[] readings) => Assert.Null(Trend.Of(readings.Select(value => (double?)value), "Requests per minute"));

    [Fact]
    public void ACheckWithoutAReadingIsLeftOut()
    {
        var trend = Trend.Of([4, null, 8, double.NaN], "Requests per minute")!;

        Assert.Equal([0.5, 1], trend.Points);
        Assert.Contains("last 2 checks", trend.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLineIsDrawnInsideItsBoxWithTheLastReadingAtTheRight()
    {
        var trend = Trend.Of([0, 5, 10], "x")!;

        Assert.Equal("2,14 24,8 46,2", trend.Polyline(48, 16, 2));
        Assert.Equal((46, 2), trend.Last(48, 16, 2));
    }

    [Fact]
    public void ANodeKeepsItsLastSixtyReadings()
    {
        var node = Node([.. Enumerable.Range(1, 75).Select(index => Sample(index))]);

        Assert.Equal(Trend.Length, node.Samples.Count);
        Assert.Equal(60, Trend.Length);
        Assert.Equal(16, node.Samples[0]!.Requests);
        Assert.Equal(75, node.Telemetry!.Requests);
        Assert.Equal("Requests per minute, last 60 checks: 16 to 75, now 75", Trends.Requests(node)!.Title);
    }

    [Fact]
    public void ACheckWithoutTelemetryIsAGapOnceTheNodeReportedAndNothingBefore()
    {
        var silent = Node(null, null);
        var gap = Node(Sample(4), null, Sample(8));

        Assert.Empty(silent.Samples);
        Assert.Null(Trends.Requests(silent));
        Assert.Equal(3, gap.Samples.Count);
        Assert.Equal([0.5, 1], Trends.Requests(gap)!.Points);
        Assert.Null(Node(Sample(4), null).Telemetry);
    }

    [Fact]
    public void EachNumberHasItsTrend()
    {
        var node = Node(Sample(10, sql: 30, cpu: 2), Sample(20, sql: 15, cpu: 8));

        Assert.Equal("Requests per minute from Front Door, last 2 checks: 10 to 20, now 20", Trends.FromFrontDoor(node)!.Title);
        Assert.Equal("SQL commands per minute, last 2 checks: 15 to 30, now 15", Trends.Sql(node)!.Title);
        Assert.Equal("CPU of the process, last 2 checks: 2 to 8 %, now 8 %", Trends.Cpu(node)!.Title);
        Assert.Equal([0, 0], Trends.Direct(node)!.Points);
        Assert.Null(Trends.Cpu(Node(Sample(1), Sample(2))));
    }

    [Fact]
    public void TheOriginsOfAFrontDoorAreAddedCheckByCheckCountedFromTheLast()
    {
        var primary = Node(Sample(5), Sample(10), Sample(30));
        var standby = Node(Sample(1), Sample(2));

        var trend = Trends.Sum([primary, standby])!;

        Assert.Equal("Requests per minute through Front Door, last 3 checks: 5 to 32, now 32", trend.Title);
        Assert.Equal([0.156, 0.344, 1], trend.Points);
        Assert.Null(Trends.Sum([]));
    }

    [Fact]
    public async Task ThePayloadCarriesTheTrendsOfTheTileAndOfTheArrows()
    {
        var requests = 10;
        var monitor = Optics.Monitor(new StubHandler(request => Optics.Answer(request, telemetry: Optics.Telemetry(requests: requests, sql: requests * 2, cpu: requests / 10d))), _time);
        var manifest = new RuntimeManifest(
            "uat",
            [
                new RuntimeNode("fd_ui", RuntimeNodeKind.FrontDoor, "demo-uat-ui", new Uri("https://fd-uat.example.net"), "ui"),
                new RuntimeNode("app_ui_primary", RuntimeNodeKind.WebApp, "uat-west", new Uri("https://uat-west.example.net"), "ui", "primary", "westus3", "region_primary"),
                new RuntimeNode("sqldb", RuntimeNodeKind.Sql, "sqldb-demo-uat"),
            ],
            [],
            [
                new RuntimeEdge("browser-to-fd_ui", "browser", "fd_ui", RuntimeEdgeKind.Public),
                new RuntimeEdge("fd_ui-to-app_ui_primary", "fd_ui", "app_ui_primary", RuntimeEdgeKind.Origin, 1),
                new RuntimeEdge("app_ui_primary-to-sqldb", "app_ui_primary", "sqldb", RuntimeEdgeKind.Sql),
            ]);
        RuntimePayload Payload() => RuntimePayloadBuilder.Build(manifest, monitor.Environments[1], null, TimeZoneInfo.Utc);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        var first = Payload();
        requests = 40;
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        var second = Payload();

        // One reading is no line yet.
        Assert.All(first.Edges, edge => Assert.Null(edge.Trend));
        Assert.All(first.Nodes.SelectMany(node => node.Lines), line => Assert.Null(line.Trend));

        var tile = second.Nodes.Single(node => node.Alias == "app_ui_primary");
        Assert.Equal([0.25, 1], tile.Lines.Single(line => line.Text.StartsWith("40 req/min", StringComparison.Ordinal)).Trend!.Points);
        Assert.Equal("CPU of the process, last 2 checks: 1 to 4 %, now 4 %", tile.Lines.Single(line => line.Text.StartsWith("CPU", StringComparison.Ordinal)).Trend!.Title);
        Assert.Equal([0.211, 1], second.Edges.Single(edge => edge.Id == "fd_ui-to-app_ui_primary").Trend!.Points);
        Assert.Equal("SQL commands per minute, last 2 checks: 20 to 80, now 80", second.Edges.Single(edge => edge.Id == "app_ui_primary-to-sqldb").Trend!.Title);
        Assert.Equal("Requests per minute through Front Door, last 2 checks: 8 to 38, now 38", second.Edges.Single(edge => edge.Id == "browser-to-fd_ui").Trend!.Title);
        Assert.Contains("\"trend\":{\"points\":[0.25,1],\"title\":", second.ToJson(), StringComparison.Ordinal);
    }
}
