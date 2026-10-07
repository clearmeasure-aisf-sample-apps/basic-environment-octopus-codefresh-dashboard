namespace Dashboard.Tests;

public class HealthSummaryTests
{
    private const HealthState Healthy = HealthState.Healthy;
    private const HealthState Unhealthy = HealthState.Unhealthy;
    private const HealthState Unreachable = HealthState.Unreachable;
    private const HealthState Pending = HealthState.Pending;

    [Fact]
    public void AllHealthy()
    {
        var summary = HealthSummary.Of(Enumerable.Repeat(Healthy, 7));

        Assert.Equal(SummaryLevel.AllHealthy, summary.Level);
        Assert.Equal("All 7 nodes healthy", summary.Text);
        Assert.Equal(0, summary.NotHealthy);
    }

    [Fact]
    public void UnhealthyAndUnreachableBothCountAsNotHealthy()
    {
        var summary = HealthSummary.Of([Healthy, Unhealthy, Healthy, Unreachable, Healthy, Healthy, Healthy]);

        Assert.Equal(SummaryLevel.Problems, summary.Level);
        Assert.Equal(2, summary.NotHealthy);
        Assert.Equal("2 of 7 nodes not healthy", summary.Text);
    }

    [Fact]
    public void BeforeTheFirstCheckEverythingIsBeingChecked()
    {
        var summary = HealthSummary.Of([Pending, Pending, Pending]);

        Assert.Equal(SummaryLevel.Pending, summary.Level);
        Assert.Equal("Checking 3 nodes", summary.Text);
    }

    [Fact]
    public void WhileAnswersArriveTheHealthyOnesAreCounted()
    {
        var summary = HealthSummary.Of([Healthy, Pending, Pending]);

        Assert.Equal(SummaryLevel.Pending, summary.Level);
        Assert.Equal("1 of 3 nodes healthy, 2 being checked", summary.Text);
    }

    [Fact]
    public void AProblemIsReportedBeforeEveryAnswerArrived()
    {
        var summary = HealthSummary.Of([Unreachable, Pending, Healthy]);

        Assert.Equal(SummaryLevel.Problems, summary.Level);
        Assert.Equal("1 of 3 nodes not healthy", summary.Text);
    }

    [Fact]
    public void NoNodes()
    {
        var summary = HealthSummary.Of([]);

        Assert.Equal(SummaryLevel.Empty, summary.Level);
        Assert.Equal("No nodes in the topology", summary.Text);
    }

    [Theory]
    [InlineData(Healthy, "The only node is healthy")]
    [InlineData(Unhealthy, "1 of 1 node not healthy")]
    [InlineData(Pending, "Checking 1 node")]
    public void ASingleNodeReadsAsSingular(HealthState state, string expected) =>
        Assert.Equal(expected, HealthSummary.Of([state]).Text);
}
