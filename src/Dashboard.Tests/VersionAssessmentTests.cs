namespace Dashboard.Tests;

public class VersionAssessmentTests
{
    private const HealthState Healthy = HealthState.Healthy;
    private const HealthState Unhealthy = HealthState.Unhealthy;
    private const HealthState Unreachable = HealthState.Unreachable;
    private const HealthState Pending = HealthState.Pending;

    private static readonly PinnedVersions Pinned = PinnedVersions.Parse("""{ "dashboard": "1.0.1", "ui": "2.4.7" }""");

    private static NodeVersion West(HealthState state, string? version) => new("westus3", state, version);

    private static NodeVersion East(HealthState state, string? version) => new("eastus2", state, version);

    private static VersionAssessment Assess(params NodeVersion[] nodes) => VersionAssessment.Assess(Pinned, "ui", nodes);

    [Fact]
    public void EveryNodeOnThePinnedVersionIsInSync()
    {
        var assessment = Assess(West(Healthy, "2.4.7"), East(Healthy, "2.4.7"));

        Assert.Equal(VersionState.InSync, assessment.State);
        Assert.False(assessment.Differs);
        Assert.Equal("2.4.7", assessment.Pinned);
        Assert.Equal("Pinned 2.4.7", assessment.Headline);
        Assert.Equal("In sync: all 2 nodes run 2.4.7.", assessment.Detail);
        Assert.Equal("Pinned 2.4.7. In sync: all 2 nodes run 2.4.7.", assessment.Text);
        Assert.Equal(["westus3", "eastus2"], assessment.Matching.Select(node => node.Label));
        Assert.Empty(assessment.Differing);
        Assert.Empty(assessment.Unknown);
    }

    [Fact]
    public void ASingleNodeIsNamed() =>
        Assert.Equal("In sync: westus3 runs 2.4.7.", Assess(West(Healthy, "2.4.7")).Detail);

    [Fact]
    public void ANodeOnAnotherVersionDiffersAndIsNamedWithItsVersion()
    {
        var assessment = Assess(West(Healthy, "2.4.7"), East(Healthy, "2.4.6"));

        Assert.Equal(VersionState.Differs, assessment.State);
        Assert.True(assessment.Differs);
        Assert.Equal("Pinned 2.4.7", assessment.Headline);
        Assert.Equal("Differs: eastus2 runs 2.4.6.", assessment.Detail);
        Assert.Equal("eastus2", Assert.Single(assessment.Differing).Label);
        Assert.Equal("westus3", Assert.Single(assessment.Matching).Label);
    }

    [Fact]
    public void EveryDifferingNodeIsNamed() =>
        Assert.Equal(
            "Differs: westus3 runs 2.4.8, eastus2 runs 2.4.6.",
            Assess(West(Healthy, "2.4.8"), East(Healthy, "2.4.6")).Detail);

    [Fact]
    public void AnUnhealthyNodeThatAnswersIsComparedToo()
    {
        // A node that fails its health check still runs a version: a failed deployment shows as a difference.
        var assessment = Assess(West(Unhealthy, "2.4.8"));

        Assert.Equal(VersionState.Differs, assessment.State);
        Assert.Equal("Differs: westus3 runs 2.4.8.", assessment.Detail);
    }

    [Theory]
    [InlineData("2.4.7+0a1b2c3", "2.4.7")]
    [InlineData("2.4.7", "2.4.7+0a1b2c3")]
    [InlineData("2.4.7+0a1b2c3", "2.4.7+ffffff0")]
    [InlineData("2.4.7-RC1", "2.4.7-rc1")]
    [InlineData(" 2.4.7 ", "2.4.7")]
    public void VersionsAreComparedWithoutBuildMetadataAndCase(string pinned, string running)
    {
        var versions = new PinnedVersions(PinnedVersionsState.Read, new Dictionary<string, string> { ["ui"] = pinned });

        var assessment = VersionAssessment.Assess(versions, "ui", [West(Healthy, running)]);

        Assert.Equal(VersionState.InSync, assessment.State);
        Assert.DoesNotContain("+", assessment.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2.4.7", "2.4.70")]
    [InlineData("2.4.7", "2.4.7-rc1")]
    [InlineData("2.4.7", "12.4.7")]
    public void AVersionThatOnlyLooksAlikeDiffers(string pinned, string running)
    {
        var versions = new PinnedVersions(PinnedVersionsState.Read, new Dictionary<string, string> { ["ui"] = pinned });

        Assert.Equal(VersionState.Differs, VersionAssessment.Assess(versions, "ui", [West(Healthy, running)]).State);
    }

    [Fact]
    public void AnUnreachableNodeDoesNotMakeTheVersionsDiffer()
    {
        // The dashboard remembers the version the node reported before it stopped answering; it is not compared.
        var assessment = Assess(West(Healthy, "2.4.7"), East(Unreachable, "2.4.6"));

        Assert.Equal(VersionState.InSync, assessment.State);
        Assert.Equal("In sync: westus3 runs 2.4.7; eastus2 is unreachable.", assessment.Detail);
        Assert.Equal("eastus2", Assert.Single(assessment.Unknown).Label);
        Assert.Empty(assessment.Differing);
    }

    [Fact]
    public void SomeNodesInSyncAndSomeUnknownAreCounted()
    {
        var assessment = Assess(West(Healthy, "2.4.7"), East(Healthy, "2.4.7"), new NodeVersion("centralus", Pending, null));

        Assert.Equal(VersionState.InSync, assessment.State);
        Assert.Equal("In sync: 2 of 3 nodes run 2.4.7; centralus is being checked.", assessment.Detail);
    }

    [Fact]
    public void ADifferenceIsReportedNextToAnUnknownNode()
    {
        var assessment = Assess(West(Healthy, "2.4.6"), East(Unreachable, null));

        Assert.Equal(VersionState.Differs, assessment.State);
        Assert.Equal("Differs: westus3 runs 2.4.6; eastus2 is unreachable.", assessment.Detail);
    }

    [Fact]
    public void WhenNoNodeVersionIsKnownNothingIsCompared()
    {
        var assessment = Assess(West(Unreachable, "2.4.6"), East(Pending, null), new NodeVersion("centralus", Healthy, null));

        Assert.Equal(VersionState.NodesUnknown, assessment.State);
        Assert.False(assessment.Differs);
        Assert.Equal("Pinned 2.4.7", assessment.Headline);
        Assert.Equal(
            "Not compared: westus3 is unreachable, eastus2 is being checked, centralus reports no version.",
            assessment.Detail);
    }

    [Fact]
    public void ADeployableWithoutNodesIsNotCompared()
    {
        var assessment = Assess();

        Assert.Equal(VersionState.NodesUnknown, assessment.State);
        Assert.Equal("Not compared: the deployable has no nodes.", assessment.Detail);
    }

    [Fact]
    public void ADeployableMissingFromTheFileIsNotDeployed()
    {
        var assessment = VersionAssessment.Assess(Pinned, "api", [West(Healthy, "0.9.0")]);

        Assert.Equal(VersionState.NotDeployed, assessment.State);
        Assert.False(assessment.Differs);
        Assert.Null(assessment.Pinned);
        Assert.Equal("No pinned version", assessment.Headline);
        Assert.Equal("versions.json has no entry for api: it was not deployed here yet.", assessment.Detail);
        Assert.Empty(assessment.Differing);
    }

    [Fact]
    public void TheDeployableNameIsMatchedExactly() =>
        Assert.Equal(VersionState.NotDeployed, VersionAssessment.Assess(Pinned, "UI", [West(Healthy, "2.4.7")]).State);

    [Fact]
    public void AMissingFileIsNotDeployed()
    {
        var assessment = VersionAssessment.Assess(PinnedVersions.Missing, "ui", [West(Healthy, "2.4.7")]);

        Assert.Equal(VersionState.NotDeployed, assessment.State);
        Assert.Equal("No pinned version", assessment.Headline);
        Assert.Equal("versions.json was not found: nothing was deployed here yet, or the repository is not public.", assessment.Detail);
    }

    [Fact]
    public void AFileThatCouldNotBeReadLeavesThePinnedVersionUnknown()
    {
        var assessment = VersionAssessment.Assess(
            PinnedVersions.Unavailable("the server answered HTTP 503"), "ui", [West(Healthy, "2.4.6")]);

        Assert.Equal(VersionState.PinnedUnknown, assessment.State);
        Assert.False(assessment.Differs);
        Assert.Null(assessment.Pinned);
        Assert.Equal("Pinned version not known", assessment.Headline);
        Assert.Equal("versions.json could not be read: the server answered HTTP 503.", assessment.Detail);
    }

    [Fact]
    public void AMalformedFileLeavesThePinnedVersionUnknown()
    {
        var assessment = VersionAssessment.Assess(PinnedVersions.Parse("<!DOCTYPE html>"), "ui", [West(Healthy, "2.4.6")]);

        Assert.Equal(VersionState.PinnedUnknown, assessment.State);
        Assert.Equal("versions.json could not be read: the file is not valid JSON.", assessment.Detail);
    }

    [Fact]
    public void TheWordsNameTheFileThatHoldsThePin()
    {
        // A deployable whose pin is its own Kustomize file: the same states, with that file's name.
        var missing = VersionAssessment.Assess(PinnedVersions.Missing, "ui", [West(Healthy, "2.4.7")], "kustomization.yaml");
        var unknown = VersionAssessment.Assess(PinnedVersions.ParseKustomization("kind: Kustomization\n", "ui"), "ui", [West(Healthy, "2.4.7")], "kustomization.yaml");
        var read = VersionAssessment.Assess(PinnedVersions.ParseKustomization("images:\n  - name: ui\n    newTag: 2.4.7\n", "ui"), "ui", [West(Healthy, "2.4.7")], "kustomization.yaml");

        Assert.Equal("No pinned version. kustomization.yaml was not found: nothing was deployed here yet, or the repository is not public.", missing.Text);
        Assert.Equal(VersionState.PinnedUnknown, unknown.State);
        Assert.Equal("Pinned version not known. kustomization.yaml could not be read: the file has no newTag entry.", unknown.Text);
        Assert.Equal("Pinned 2.4.7. In sync: westus3 runs 2.4.7.", read.Text);
    }

    [Fact]
    public void BeforeTheFileIsReadThePinnedVersionIsPending()
    {
        var assessment = VersionAssessment.Assess(PinnedVersions.Pending, "ui", [West(Healthy, "2.4.7")]);

        Assert.Equal(VersionState.Pending, assessment.State);
        Assert.Equal("Reading the pinned version", assessment.Headline);
        Assert.Null(assessment.Detail);
        Assert.Equal("Reading the pinned version", assessment.Text);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "Versions differ in 1 environment")]
    [InlineData(3, "Versions differ in 3 environments")]
    public void TheSummaryCountsTheEnvironmentsThatDiffer(int environments, string? expected)
    {
        var summary = new VersionSummary(environments);

        Assert.Equal(expected, summary.Text);
        Assert.Equal(environments > 0, summary.Differs);
    }
}
