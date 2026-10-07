namespace Dashboard.Tests;

public class TargetStatusTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 4, 22, 0, 0, TimeSpan.Zero);

    private static ProbeResult Result(HealthState state, int second = 0, string? version = null) =>
        new(state, state == HealthState.Unreachable ? null : 200, state == HealthState.Unreachable ? null : 12, Start.AddSeconds(second), version);

    private static TargetStatus Node(string region = "westus3", string role = "primary") =>
        new(TargetKind.Node, $"app-{region}", new Uri($"https://app-{region}.example.net"), region, role);

    [Fact]
    public void ANewTargetIsPending()
    {
        var target = Node();

        Assert.Equal(HealthState.Pending, target.State);
        Assert.Null(target.Last);
        Assert.Null(target.Version);
        Assert.Empty(target.History);
    }

    [Fact]
    public void TheLastResultIsTheState()
    {
        var target = Node();

        target.Record(Result(HealthState.Healthy, 0));
        target.Record(Result(HealthState.Unreachable, 30));

        Assert.Equal(HealthState.Unreachable, target.State);
        Assert.Equal(Start.AddSeconds(30), target.Last?.CheckedAt);
        Assert.Equal([HealthState.Healthy, HealthState.Unreachable], target.History.Select(result => result.State));
    }

    [Fact]
    public void TheHistoryKeepsTheLast30Checks()
    {
        var target = Node();
        for (var second = 0; second < 45; second++)
        {
            target.Record(Result(HealthState.Healthy, second));
        }

        Assert.Equal(30, TargetStatus.HistoryLength);
        Assert.Equal(30, target.History.Count);
        Assert.Equal(Start.AddSeconds(15), target.History[0].CheckedAt);
        Assert.Equal(Start.AddSeconds(44), target.History[29].CheckedAt);
    }

    [Fact]
    public void ANodeThatStopsAnsweringKeepsItsLastKnownVersion()
    {
        var target = Node();

        target.Record(Result(HealthState.Healthy, 0, "2.4.21"));
        target.Record(Result(HealthState.Unreachable, 30));
        Assert.Equal("2.4.21", target.Version);

        target.Record(Result(HealthState.Healthy, 60, "2.4.22"));
        Assert.Equal("2.4.22", target.Version);
    }

    [Fact]
    public void ADeployableListsItsFrontDoorFirst()
    {
        var deployable = new DeployableStatus(new DeployableInfo(
            "ui",
            new Uri("https://demo.azurefd.net"),
            "/_healthcheck",
            "/alive",
            "/_version",
            [
                new NodeInfo("app-west", "westus3", "primary", new Uri("https://app-west.example.net")),
                new NodeInfo("app-east", "eastus2", "standby", new Uri("https://app-east.example.net")),
            ]));

        Assert.Equal([TargetKind.FrontDoor, TargetKind.Node, TargetKind.Node], deployable.Targets.Select(target => target.Kind));
        Assert.Equal(["Front Door", "app-west", "app-east"], deployable.Targets.Select(target => target.Name));
        Assert.True(deployable.HasFrontDoor);
        Assert.True(deployable.Nodes[0].IsPrimary);
        Assert.False(deployable.Nodes[1].IsPrimary);
    }

    [Fact]
    public void ADeployableWithoutFrontDoorHasOnlyItsNodes()
    {
        var deployable = new DeployableStatus(new DeployableInfo(
            "ui", null, "/_healthcheck", "/alive", "/_version", [new NodeInfo("app", "westus3", "primary", new Uri("https://app.example.net"))]));

        Assert.Null(deployable.FrontDoor);
        Assert.False(deployable.HasFrontDoor);
        Assert.Equal(TargetKind.Node, Assert.Single(deployable.Targets).Kind);
        Assert.Equal(FrontDoorAgreement.NotPresent, deployable.Assess().FrontDoor);
    }

    [Fact]
    public void TheAssessmentPointsAtTheTileExpectedToServe()
    {
        var deployable = new DeployableStatus(new DeployableInfo(
            "ui",
            new Uri("https://demo.azurefd.net"),
            "/_healthcheck",
            "/alive",
            "/_version",
            [
                new NodeInfo("app", "westus3", "primary", new Uri("https://app-west.example.net")),
                new NodeInfo("app", "westus3", "standby", new Uri("https://app-east.example.net")),
            ]));
        deployable.FrontDoor!.Record(Result(HealthState.Healthy));
        deployable.Nodes[0].Record(Result(HealthState.Unreachable));
        deployable.Nodes[1].Record(Result(HealthState.Healthy));

        var assessment = deployable.Assess(out var expected);

        Assert.Equal(ServingState.FailedOver, assessment.State);
        Assert.Equal(FrontDoorAgreement.Agrees, assessment.FrontDoor);
        Assert.Same(deployable.Nodes[1], expected);
    }

    [Fact]
    public void NoTileIsExpectedToServeWhenNoNodeIsHealthy()
    {
        var deployable = new DeployableStatus(new DeployableInfo(
            "ui", null, "/_healthcheck", "/alive", "/_version", [new NodeInfo("app", "westus3", "primary", new Uri("https://app.example.net"))]));
        deployable.Nodes[0].Record(Result(HealthState.Unreachable));

        Assert.Equal(ServingState.Down, deployable.Assess(out var expected).State);
        Assert.Null(expected);
    }
}
