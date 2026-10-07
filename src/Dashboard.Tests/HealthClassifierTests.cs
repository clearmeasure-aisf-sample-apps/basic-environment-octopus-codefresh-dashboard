namespace Dashboard.Tests;

public class HealthClassifierTests
{
    [Fact]
    public void Status200IsHealthy() =>
        Assert.Equal(HealthState.Healthy, HealthClassifier.FromStatusCode(200));

    [Theory]
    [InlineData(201)]
    [InlineData(204)]
    [InlineData(301)]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(503)]
    public void EveryOtherStatusIsUnhealthy(int status) =>
        Assert.Equal(HealthState.Unhealthy, HealthClassifier.FromStatusCode(status));

    [Theory]
    [InlineData(HealthState.Healthy, "Healthy")]
    [InlineData(HealthState.Unhealthy, "Unhealthy")]
    [InlineData(HealthState.Unreachable, "Unreachable")]
    [InlineData(HealthState.Pending, "Checking")]
    public void EveryStateHasATextLabel(HealthState state, string label) =>
        Assert.Equal(label, HealthClassifier.Label(state));
}
