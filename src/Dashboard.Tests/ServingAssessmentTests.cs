namespace Dashboard.Tests;

public class ServingAssessmentTests
{
    private const HealthState Healthy = HealthState.Healthy;
    private const HealthState Unhealthy = HealthState.Unhealthy;
    private const HealthState Unreachable = HealthState.Unreachable;
    private const HealthState Pending = HealthState.Pending;

    private static NodeHealth Primary(HealthState state, string region = "westus3") => new($"app-{region}", region, true, state);

    private static NodeHealth Standby(HealthState state, string region = "eastus2") => new($"app-{region}", region, false, state);

    [Fact]
    public void AHealthyPrimaryServes()
    {
        var assessment = ServingAssessment.Assess([Primary(Healthy), Standby(Healthy)], frontDoor: Healthy);

        Assert.Equal(ServingState.Primary, assessment.State);
        Assert.Equal("westus3", assessment.Expected?.Region);
        Assert.False(assessment.IsFailedOver);
        Assert.Null(assessment.FailoverDetail);
        Assert.Equal("Expected to serve traffic: westus3 (primary)", assessment.Headline);
    }

    [Theory]
    [InlineData(Unhealthy, "Primary westus3 is unhealthy; eastus2 is expected to serve traffic.")]
    [InlineData(Unreachable, "Primary westus3 is unreachable; eastus2 is expected to serve traffic.")]
    public void AStandbyServesWhenThePrimaryIsNotHealthy(HealthState primary, string detail)
    {
        var assessment = ServingAssessment.Assess([Primary(primary), Standby(Healthy)], frontDoor: Healthy);

        Assert.Equal(ServingState.FailedOver, assessment.State);
        Assert.True(assessment.IsFailedOver);
        Assert.Equal("eastus2", assessment.Expected?.Region);
        Assert.Equal("westus3", assessment.FailedPrimary?.Region);
        Assert.Equal("Failed over to eastus2", assessment.Headline);
        Assert.Equal(detail, assessment.FailoverDetail);
    }

    [Fact]
    public void TheFirstHealthyStandbyInTopologyOrderServes()
    {
        var nodes = new[] { Primary(Unreachable), Standby(Unhealthy, "eastus2"), Standby(Healthy, "centralus"), Standby(Healthy, "northeurope") };

        var assessment = ServingAssessment.Assess(nodes, frontDoor: null);

        Assert.Equal(ServingState.FailedOver, assessment.State);
        Assert.Equal("centralus", assessment.Expected?.Region);
    }

    [Fact]
    public void APrimaryListedAfterAStandbyStillHasPriority()
    {
        var assessment = ServingAssessment.Assess([Standby(Healthy), Primary(Healthy)], frontDoor: null);

        Assert.Equal(ServingState.Primary, assessment.State);
        Assert.Equal("westus3", assessment.Expected?.Region);
    }

    [Fact]
    public void NoHealthyNodeMeansNothingServes()
    {
        var assessment = ServingAssessment.Assess([Primary(Unreachable), Standby(Unhealthy)], frontDoor: Unreachable);

        Assert.Equal(ServingState.Down, assessment.State);
        Assert.Null(assessment.Expected);
        Assert.False(assessment.IsFailedOver);
        Assert.Equal("No healthy node: nothing can serve traffic", assessment.Headline);
    }

    [Fact]
    public void ASingleHealthyPrimaryServesWithoutAStandby()
    {
        var assessment = ServingAssessment.Assess([Primary(Healthy)], frontDoor: Healthy);

        Assert.Equal(ServingState.Primary, assessment.State);
    }

    [Fact]
    public void AnUncheckedPrimaryIsNotAFailover()
    {
        var assessment = ServingAssessment.Assess([Primary(Pending), Standby(Healthy)], frontDoor: Healthy);

        Assert.Equal(ServingState.Pending, assessment.State);
        Assert.Null(assessment.Expected);
        Assert.Equal(FrontDoorAgreement.Pending, assessment.FrontDoor);
    }

    [Fact]
    public void AHealthyPrimaryServesWhileTheStandbyIsStillUnchecked()
    {
        var assessment = ServingAssessment.Assess([Primary(Healthy), Standby(Pending)], frontDoor: null);

        Assert.Equal(ServingState.Primary, assessment.State);
    }

    [Fact]
    public void WithoutNodesThereIsNoDecision()
    {
        var assessment = ServingAssessment.Assess([], frontDoor: Healthy);

        Assert.Equal(ServingState.NoNodes, assessment.State);
        Assert.Equal(FrontDoorAgreement.Pending, assessment.FrontDoor);
    }

    [Fact]
    public void ANodeWithoutRegionIsNamedByItsName()
    {
        var assessment = ServingAssessment.Assess([new NodeHealth("app-one", null, true, Healthy)], frontDoor: Healthy);

        Assert.Equal("Expected to serve traffic: app-one (primary)", assessment.Headline);
    }

    [Theory]
    [InlineData(Pending, ServingState.Pending, "Checking app-one")]
    [InlineData(Healthy, ServingState.Primary, "Serves traffic: app-one")]
    [InlineData(Unhealthy, ServingState.Down, "Not serving: app-one")]
    [InlineData(Unreachable, ServingState.Down, "Not serving: app-one")]
    public void TheOnlyNodeOfADeployableWithoutAFrontDoorServesOrDoesNot(HealthState node, ServingState state, string headline)
    {
        // One app at one public address, as in a cluster: the states stay, the words name no role and no region.
        var assessment = ServingAssessment.Assess([new NodeHealth("app-one", null, true, node)], frontDoor: null);

        Assert.Equal(state, assessment.State);
        Assert.Equal("app-one", assessment.OnlyNode?.Name);
        Assert.Equal(headline, assessment.Headline);
        Assert.Null(assessment.FailoverDetail);
        Assert.Null(assessment.FrontDoorText);
    }

    [Fact]
    public void TheOnlyNodeIsNamedByItsRegionWhenTheTopologyGivesOne() =>
        Assert.Equal("Serves traffic: westus3", ServingAssessment.Assess([Primary(Healthy)], frontDoor: null).Headline);

    [Fact]
    public void AnOnlyNodeWhoseRoleIsNotPrimaryDidNotFailOver()
    {
        var assessment = ServingAssessment.Assess([Standby(Healthy)], frontDoor: null);

        Assert.Equal("Serves traffic: eastus2", assessment.Headline);
        Assert.Null(assessment.FailoverDetail);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 0)]
    public void ADeployableWithAFrontDoorOrSeveralNodesKeepsItsWords(bool frontDoor, int nodes)
    {
        NodeHealth[] all = [Primary(Healthy), Standby(Healthy)];

        var assessment = ServingAssessment.Assess(all.Take(nodes), frontDoor ? Healthy : null);

        Assert.Null(assessment.OnlyNode);
        Assert.Equal(nodes == 0 ? "No nodes in the topology" : "Expected to serve traffic: westus3 (primary)", assessment.Headline);
    }

    [Fact]
    public void WithoutAFrontDoorThereIsNothingToCompare()
    {
        var assessment = ServingAssessment.Assess([Primary(Healthy)], frontDoor: null);

        Assert.Equal(FrontDoorAgreement.NotPresent, assessment.FrontDoor);
        Assert.Null(assessment.FrontDoorText);
    }

    [Theory]
    [InlineData(Healthy, Healthy, Healthy, FrontDoorAgreement.Agrees, "Front Door agrees: it is healthy.")]
    [InlineData(Unreachable, Healthy, Healthy, FrontDoorAgreement.Agrees, "Front Door agrees: it is healthy.")]
    [InlineData(Unreachable, Unhealthy, Unhealthy, FrontDoorAgreement.Agrees, "Front Door agrees: it is unhealthy too.")]
    [InlineData(Unreachable, Unreachable, Unreachable, FrontDoorAgreement.Agrees, "Front Door agrees: it is unreachable too.")]
    [InlineData(Healthy, Healthy, Unreachable, FrontDoorAgreement.Disagrees, "Front Door disagrees: it is unreachable although westus3 is healthy.")]
    [InlineData(Unhealthy, Healthy, Unhealthy, FrontDoorAgreement.Disagrees, "Front Door disagrees: it is unhealthy although eastus2 is healthy.")]
    [InlineData(Unreachable, Unreachable, Healthy, FrontDoorAgreement.Disagrees, "Front Door disagrees: it is healthy although no node is.")]
    [InlineData(Healthy, Healthy, Pending, FrontDoorAgreement.Pending, "Front Door is being checked.")]
    public void FrontDoorAgreesWhenItIsHealthyExactlyWhenANodeIs(
        HealthState primary,
        HealthState standby,
        HealthState frontDoor,
        FrontDoorAgreement agreement,
        string text)
    {
        var assessment = ServingAssessment.Assess([Primary(primary), Standby(standby)], frontDoor);

        Assert.Equal(agreement, assessment.FrontDoor);
        Assert.Equal(text, assessment.FrontDoorText);
    }
}
