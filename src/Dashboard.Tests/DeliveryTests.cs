using System.Net;

namespace Dashboard.Tests;

public class DeliveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 22, 0, 0, TimeSpan.Zero);
    private readonly SignallingTimeProvider _time = new();

    private static readonly DeliveryReport Report = DeliveryReport.Parse(Optics.Delivery)!;

    [Fact]
    public void TheDeliveryFileIsRead()
    {
        var ui = Report.Find("uat", "ui")!;

        Assert.Equal(new DateTimeOffset(2026, 10, 4, 21, 0, 0, TimeSpan.Zero), Report.Generated);
        Assert.Equal(["tdd", "uat"], Report.Environments.Select(environment => environment.Name));
        Assert.Equal(
            new DeliveryEntry(
                "ui",
                "2.4.14",
                new DateTimeOffset(2026, 10, 4, 16, 37, 0, TimeSpan.Zero),
                "cm-ai-ops",
                "Acceptance tests passed in tdd",
                "9f8e7d6c5b4a",
                new DateTimeOffset(2026, 10, 4, 11, 25, 0, TimeSpan.Zero),
                5.2,
                new BehindFirst(2, 3.4),
                4,
                0,
                new Uri("https://octopus.example.net/r/2.4.14")),
            ui);
        Assert.Equal(new FailoverTest("uat", new DateTimeOffset(2026, 10, 4, 5, 0, 0, TimeSpan.Zero), 44), Report.Failover);
        Assert.Null(Report.Find("prod", "ui"));
        Assert.Null(Report.Find("uat", "api"));
        Assert.Equal("ui", Report.Find("UAT", "UI")!.Name);
    }

    [Fact]
    public void TheFirstEnvironmentHasNoSignOffAndThatIsNothingToShow()
    {
        var first = Report.Find("tdd", "ui")!;

        Assert.Null(first.SignedOffBy);
        Assert.Null(first.Reason);
        Assert.Null(DeliveryText.Behind(first.Behind, "tdd", "tdd"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("404: Not Found")]
    [InlineData("[]")]
    [InlineData("{ }")]
    [InlineData("""{ "generated": "2026-10-04T21:00:00Z", "environments": "none" }""")]
    public void AnythingButDeliveryFactsIsNoReport(string? body) => Assert.Null(DeliveryReport.Parse(body));

    [Fact]
    public void EveryPartOfAnEntryIsOptional()
    {
        var report = DeliveryReport.Parse("""{ "environments": [ { "name": "uat", "deployables": [ { "name": "ui" }, { "version": "1" }, 7 ] }, { "deployables": [] } ], "failover": null }""")!;

        Assert.Equal(new DeliveryEntry("ui", null, null, null, null, null, null, null, null, null, null, null), Assert.Single(Assert.Single(report.Environments).Deployables));
        Assert.Null(report.Failover);
        Assert.Null(report.Generated);
        Assert.Null(DeliveryText.Frequency(report.Find("uat", "ui")!));
    }

    [Fact]
    public void AFileWithTheFailoverTestOnlyIsAReport() =>
        Assert.Equal(new FailoverTest("uat", null, 44), DeliveryReport.Parse("""{ "failover": { "environment": "uat", "seconds": 44 } }""")!.Failover);

    [Theory]
    [InlineData(0.75, "45 min")]
    [InlineData(5.2, "5.2 h")]
    [InlineData(47.96, "48 h")]
    [InlineData(84, "3.5 d")]
    public void LeadTimeIsInItsFittingUnit(double hours, string expected) => Assert.Equal(expected, DeliveryText.LeadTime(hours));

    [Theory]
    [InlineData(0, 0d, "same as tdd")]
    [InlineData(0, null, "same as tdd")]
    [InlineData(2, 3.4, "2 versions, 3 days behind tdd")]
    [InlineData(1, 1.2, "1 version, 1 day behind tdd")]
    [InlineData(3, null, "3 versions behind tdd")]
    [InlineData(1, 0.2, "1 version behind tdd")]
    [InlineData(null, 2.0, "2 days behind tdd")]
    [InlineData(null, 0.0, "same as tdd")]
    [InlineData(null, null, null)]
    public void BehindIsVersionsAndDaysSinceBothRanTheSameRelease(int? versions, double? days, string? expected) =>
        Assert.Equal(expected, DeliveryText.Behind(new BehindFirst(versions, days), "uat", "tdd"));

    [Fact]
    public void BehindIsNotSaidOfTheFirstEnvironmentOrWithoutOne()
    {
        Assert.Null(DeliveryText.Behind(new BehindFirst(2, 3), "TDD", "tdd"));
        Assert.Null(DeliveryText.Behind(new BehindFirst(2, 3), "uat", null));
        Assert.Null(DeliveryText.Behind(null, "uat", "tdd"));
    }

    [Fact]
    public void TheDeploymentsOfTheLastSevenDaysAreCountedWithTheFailedOnes()
    {
        Assert.Equal("4 deployments, none failed", DeliveryText.Frequency(Report.Find("uat", "ui")!));
        Assert.Equal("9 deployments, 1 failed", DeliveryText.Frequency(Report.Find("tdd", "ui")!));
        Assert.Equal("1 deployment", DeliveryText.Frequency(Report.Find("uat", "ui")! with { DeploymentsLast7Days = 1, FailedLast7Days = null }));
        Assert.Equal("0 deployments", DeliveryText.Frequency(Report.Find("uat", "ui")! with { DeploymentsLast7Days = 0, FailedLast7Days = 0 }));
    }

    [Fact]
    public void TheFailoverTestIsSaidWithItsEnvironmentItsAgeAndItsSeconds()
    {
        Assert.Equal("uat, 17 h ago: the standby answered after 44 s", DeliveryText.Failover(Report.Failover!, Now));
        Assert.Equal("the standby answered after 12.5 s", DeliveryText.Failover(new FailoverTest(null, null, 12.5), Now));
        Assert.Equal("uat", DeliveryText.Failover(new FailoverTest("uat", null, null), Now));
    }

    [Fact]
    public void TheSystemProjectAndTheDeployablesThePageDoesNotCheckAreTheOthers()
    {
        Assert.Equal(["system", "dashboard"], Report.Others("uat", ["ui"]).Select(entry => entry.Name));
        Assert.Equal(["system"], Report.Others("tdd", ["UI"]).Select(entry => entry.Name));
        Assert.Empty(Report.Others("prod", ["ui"]));
        Assert.Equal("the system (infrastructure and pipeline) in uat", DeliveryText.Context("system", "uat"));
        Assert.Equal("dashboard in uat", DeliveryText.Context("dashboard", "uat"));
    }

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(59, "just now")]
    [InlineData(-300, "just now")]
    [InlineData(60, "1 min ago")]
    [InlineData(3599, "59 min ago")]
    [InlineData(5400, "90 min ago")]
    [InlineData(7200, "2 h ago")]
    [InlineData(47 * 3600, "47 h ago")]
    [InlineData(3 * 86400, "3 d ago")]
    public void AMomentIsSaidByHowLongAgoItWas(int seconds, string expected) => Assert.Equal(expected, TimeText.Ago(Now.AddSeconds(-seconds), Now));

    [Fact]
    public void TheTopologyCarriesTheDeliveryAddressAndRefusesOneThatIsNoAddress()
    {
        Assert.Equal("https://raw.example.net/org/demo-system/status/delivery.json", TopologyParser.Parse(Optics.Topology).Topology!.System.DeliveryUrl?.AbsoluteUri);
        Assert.Null(TopologyParser.Parse("""{ "system": { "deliveryUrl": null }, "environments": [] }""").Topology!.System.DeliveryUrl);
        Assert.Equal(
            ["system.deliveryUrl: not an absolute http or https address."],
            TopologyParser.Parse("""{ "system": { "deliveryUrl": "delivery.json" }, "environments": [] }""").Errors);
    }

    [Fact]
    public async Task TheFileIsReadWithTheFirstRoundAndThenEveryFiveMinutes()
    {
        var handler = new StubHandler(request => Optics.Answer(request));
        var monitor = Optics.Monitor(handler, _time);
        const string Path = "/org/demo-system/status/delivery.json";

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        Assert.Equal("2.4.14", monitor.Delivery!.Find("uat", "ui")!.Version);
        Assert.Equal("tdd", monitor.FirstEnvironment);

        for (var round = 0; round < 9; round++)
        {
            _time.Advance(TimeSpan.FromSeconds(30));
            await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        }

        Assert.Equal(1, Optics.Count(handler, Path));

        _time.Advance(TimeSpan.FromSeconds(30));
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Equal(2, Optics.Count(handler, Path));
    }

    [Fact]
    public async Task AFileThatCannotBeReadShowsNothingAndFailsNoCheck()
    {
        var handler = new StubHandler(request => request.RequestUri!.Host == "raw.example.net" ? StubHandler.Answer(HttpStatusCode.NotFound, "404: Not Found") : Optics.Answer(request));
        var monitor = Optics.Monitor(handler, _time);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Null(monitor.Delivery);
        Assert.Equal("All 4 nodes healthy", monitor.Summary.Text);
    }

    [Fact]
    public async Task AFailedReadingKeepsTheLastGoodOne()
    {
        var offline = false;
        var handler = new StubHandler(request => offline && request.RequestUri!.Host == "raw.example.net" ? throw new HttpRequestException("offline") : Optics.Answer(request));
        var monitor = Optics.Monitor(handler, _time);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        offline = true;
        _time.Advance(DashboardMonitor.DeliveryInterval);
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Equal(2, Optics.Count(handler, "/org/demo-system/status/delivery.json"));
        Assert.Equal(Report.Generated, monitor.Delivery!.Generated);
    }

    [Fact]
    public async Task WithoutADeliveryAddressNothingIsRead()
    {
        var handler = new StubHandler(request => Optics.Answer(request));
        var monitor = Optics.Monitor(handler, _time, topology: Optics.Topology.Replace("deliveryUrl", "ignored", StringComparison.Ordinal));

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Null(monitor.Delivery);
        Assert.DoesNotContain(handler.Requests, request => request.RequestUri!.Host == "raw.example.net");
    }
}
