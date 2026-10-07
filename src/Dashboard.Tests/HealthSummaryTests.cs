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

    // ----- Asleep: a node that does not answer while its cluster is known to be stopped -----

    private static HealthSummary Of(params (HealthState State, bool Asleep)[] nodes) => HealthSummary.OfNodes(nodes);

    [Fact]
    public void NodesThatAreAllAsleepAreNotAProblem()
    {
        var summary = Of((Unreachable, true), (Unreachable, true), (Unreachable, true));

        Assert.Equal(SummaryLevel.Asleep, summary.Level);
        Assert.Equal("All 3 nodes asleep", summary.Text);
        Assert.Equal((3, 0, 0), (summary.Asleep, summary.Awake, summary.NotHealthy));
    }

    [Fact]
    public void TheOnlyNodeAsleepReadsAsSingular() =>
        Assert.Equal("The only node is asleep", Of((Unreachable, true)).Text);

    [Fact]
    public void AsleepNodesAreCountedNextToTheAwakeOnesAndNotAsNotHealthy()
    {
        var one = Of((Unreachable, true), (Unreachable, true), (Healthy, false));
        var two = Of((Unreachable, true), (Healthy, false), (Healthy, false));

        Assert.Equal(SummaryLevel.AllHealthy, one.Level);
        Assert.Equal("The only awake node is healthy, 2 asleep", one.Text);
        Assert.Equal(0, one.NotHealthy);
        Assert.Equal(SummaryLevel.AllHealthy, two.Level);
        Assert.Equal("All 2 awake nodes healthy, 1 asleep", two.Text);
    }

    [Fact]
    public void AnAwakeNodeThatIsNotHealthyIsStillAProblem()
    {
        var summary = Of((Unreachable, true), (Unreachable, false), (Healthy, false));
        var single = Of((Unreachable, true), (Unhealthy, false));

        Assert.Equal(SummaryLevel.Problems, summary.Level);
        Assert.Equal("1 of 2 awake nodes not healthy, 1 asleep", summary.Text);
        Assert.Equal("1 of 1 awake node not healthy, 1 asleep", single.Text);
    }

    [Fact]
    public void WhileAnswersArriveTheAsleepOnesAreCountedToo()
    {
        Assert.Equal("Checking 2 nodes, 1 asleep", Of((Unreachable, true), (Pending, false), (Pending, false)).Text);
        Assert.Equal("Checking 1 node, 2 asleep", Of((Unreachable, true), (Unreachable, true), (Pending, false)).Text);
        Assert.Equal(
            "1 of 2 awake nodes healthy, 1 being checked, 1 asleep",
            Of((Unreachable, true), (Healthy, false), (Pending, false)).Text);
        Assert.Equal(SummaryLevel.Pending, Of((Unreachable, true), (Pending, false)).Level);
    }

    [Fact]
    public void WithoutAnAsleepNodeTheSummaryIsAsItWas()
    {
        HealthState[] states = [Healthy, Unhealthy, Unreachable, Pending];

        Assert.Equal(HealthSummary.Of(states), HealthSummary.OfNodes(states.Select(state => (state, false))));
        Assert.Equal(new HealthSummary(4, 1, 1), HealthSummary.Of(states));
        Assert.Equal(0, HealthSummary.Of(states).Asleep);
    }
}
