namespace Dashboard.Tests;

/// <summary>When an environment's cluster counts as asleep, and what the health view then says.</summary>
public class ClusterSleepTests
{
    private const HealthState Healthy = HealthState.Healthy;
    private const HealthState Unhealthy = HealthState.Unhealthy;
    private const HealthState Unreachable = HealthState.Unreachable;
    private const HealthState Pending = HealthState.Pending;

    /// <summary>When the workflow read the sample facts from Azure.</summary>
    private static readonly DateTimeOffset Read = new(2026, 10, 6, 20, 10, 4, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Read.AddMinutes(5);

    private static readonly ClusterInfo Cluster = new("aks-platform-nonprod", Environments: ["tdd", "uat"]);

    private static AksService Stopped => ClusterFixture.SampleService with { PowerState = "Stopped" };

    private static ClusterSleep? Of(SourceReading<AksService>? reading, DateTimeOffset? now = null) =>
        ClusterSleep.Of(Cluster, reading, now ?? Now);

    [Fact]
    public void StoppedByFreshFactsIsAsleep()
    {
        var sleep = Of(ClusterFixture.Read(Stopped));

        Assert.Equal(new ClusterSleep("aks-platform-nonprod", Read), sleep);
    }

    [Fact]
    public void ThePowerStateIsReadWithoutRegardToCase() =>
        Assert.NotNull(Of(ClusterFixture.Read(Stopped with { PowerState = "stopped" })));

    [Fact]
    public void StoppedByFactsThatAreOldProvesNothing()
    {
        var reading = ClusterFixture.Read(Stopped);

        // The page's notion of old facts (the note of the AKS service card): older than 30 minutes.
        Assert.NotNull(Of(reading, Read + AksService.OldAfter));
        Assert.Null(Of(reading, Read + AksService.OldAfter + TimeSpan.FromSeconds(1)));
        Assert.Null(Of(reading, Read.AddHours(9)));
        Assert.NotNull(ClusterText.OldFacts(reading.Value!, Read.AddHours(9)));
    }

    [Fact]
    public void StoppedByFactsThatDoNotSayWhenTheyWereReadProvesNothing() =>
        Assert.Null(Of(ClusterFixture.Read(Stopped with { Generated = null })));

    [Fact]
    public void FactsFromAClockAheadOfThePagesAreFresh() =>
        Assert.NotNull(Of(ClusterFixture.Read(Stopped), Read.AddMinutes(-2)));

    [Fact]
    public void RunningIsAwake()
    {
        Assert.Null(Of(ClusterFixture.Read(ClusterFixture.SampleService)));
        Assert.Null(Of(ClusterFixture.Read(ClusterFixture.SampleService with { PowerState = null })));
        Assert.Null(Of(ClusterFixture.Read(ClusterFixture.SampleService with { PowerState = "Unknown" })));
    }

    [Theory]
    [InlineData(SourceState.Pending)]
    [InlineData(SourceState.Missing)]
    [InlineData(SourceState.Unavailable)]
    [InlineData(SourceState.Malformed)]
    public void FactsThatWereNotReadProveNothing(SourceState state) =>
        Assert.Null(Of(new SourceReading<AksService>(state, Detail: "the file is not valid JSON")));

    [Fact]
    public void AClusterWithoutAnAddressForAzuresFactsIsNeverAsleep() => Assert.Null(Of(null));

    [Fact]
    public void AFailedReadingAfterAGoodOneProvesNothingEither()
    {
        // The monitor replaces a good reading with a failed one: the value is gone with it.
        Assert.Null(Of(new SourceReading<AksService>(SourceState.Unavailable, Stopped, "no answer within 10 s")));
    }

    [Theory]
    [InlineData(Unreachable, true)]
    [InlineData(Healthy, false)]
    [InlineData(Unhealthy, false)]
    [InlineData(Pending, false)]
    public void OnlyAnEndpointThatDoesNotAnswerIsAsleep(HealthState state, bool asleep)
    {
        var sleep = Of(ClusterFixture.Read(Stopped));

        Assert.Equal(asleep, ClusterSleep.Covers(sleep, state));
        Assert.False(ClusterSleep.Covers(null, state));
    }

    [Fact]
    public void ADeployableIsAsleepWhenNoneOfItsNodesAnswers()
    {
        var sleep = Of(ClusterFixture.Read(Stopped));

        Assert.True(ClusterSleep.CoversAll(sleep, [Unreachable]));
        Assert.True(ClusterSleep.CoversAll(sleep, [Unreachable, Unreachable]));

        // A node that answers, whatever it answers, or is still being checked: the serving decision speaks.
        Assert.False(ClusterSleep.CoversAll(sleep, [Unreachable, Healthy]));
        Assert.False(ClusterSleep.CoversAll(sleep, [Unreachable, Unhealthy]));
        Assert.False(ClusterSleep.CoversAll(sleep, [Unreachable, Pending]));
        Assert.False(ClusterSleep.CoversAll(sleep, []));
        Assert.False(ClusterSleep.CoversAll(null, [Unreachable]));
    }

    [Fact]
    public void TheWordsNameTheClusterAndSinceWhen()
    {
        var sleep = Of(ClusterFixture.Read(Stopped))!;

        Assert.Equal("Asleep", ClusterSleep.Label);
        Assert.Equal("cluster aks-platform-nonprod is stopped", sleep.Reason);
        Assert.Equal("Asleep: cluster aks-platform-nonprod is stopped", sleep.Headline);
        Assert.Equal("No answer, as expected: cluster aks-platform-nonprod is stopped.", sleep.NodeDetail);
        Assert.Equal(
            "Azure reports the power state Stopped, as of 20:10:04 (5 min ago). Nothing answers from inside a stopped cluster: that is expected, not a failure.",
            sleep.Detail(Now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void AClusterWithoutANameIsTheCluster()
    {
        var sleep = ClusterSleep.Of(new ClusterInfo(null), ClusterFixture.Read(Stopped), Now)!;

        Assert.Equal("Asleep: the cluster is stopped", sleep.Headline);
        Assert.Equal("No answer, as expected: the cluster is stopped.", sleep.NodeDetail);
    }

    [Fact]
    public void ANodeThatIsAsleepIsNamedSoNextToThePinnedVersion()
    {
        var pinned = PinnedVersions.Parse("""{ "ui": "2.4.7" }""");

        var asleep = VersionAssessment.Assess(pinned, "ui", [new NodeVersion("southcentralus", Unreachable, "2.4.7", Asleep: true)]);
        var failing = VersionAssessment.Assess(pinned, "ui", [new NodeVersion("southcentralus", Unreachable, "2.4.7")]);

        Assert.Equal("Pinned 2.4.7. Not compared: southcentralus is asleep.", asleep.Text);
        Assert.Equal("Pinned 2.4.7. Not compared: southcentralus is unreachable.", failing.Text);
        Assert.Equal(VersionState.NodesUnknown, asleep.State);
    }
}
