namespace Dashboard.Tests;

public class VersionTextTests
{
    [Theory]
    [InlineData("2.4.21+0a1b2c3", "2.4.21")]
    [InlineData("2.4.21", "2.4.21")]
    [InlineData(" 1.0.7+sha ", "1.0.7")]
    [InlineData("0.0.0-local+abc", "0.0.0-local")]
    public void DisplayDropsTheBuildMetadata(string version, string expected) =>
        Assert.Equal(expected, VersionText.Display(version));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+sha")]
    public void DisplayIsNullWhenNothingIsLeft(string? version) =>
        Assert.Null(VersionText.Display(version));

    [Theory]
    [InlineData("""{"version":"2.4.21+0a1b2c3"}""", "2.4.21")]
    [InlineData("""{"Version":"3.0.1"}""", "3.0.1")]
    [InlineData("""{"name":"ui","version":"1.2.3+x","extra":{"a":1}}""", "1.2.3")]
    [InlineData("\"4.5.6+sha\"", "4.5.6")]
    [InlineData("""{"assemblyVersion":"2.5.772.0","informationalVersion":"2.5.772+93aefaa","buildConfiguration":"Release"}""", "2.5.772")]
    [InlineData("""{"informationalVersion":"9.9.9+x","version":"1.2.3+y"}""", "1.2.3")]
    public void TheVersionEndpointIsRead(string body, string expected) =>
        Assert.Equal(expected, VersionText.FromVersionResponse(body));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html>not json</html>")]
    [InlineData("""{"version":42}""")]
    [InlineData("""{"other":"1.0.0"}""")]
    [InlineData("[1,2]")]
    public void AnUnreadableVersionAnswerIsNoVersion(string? body) =>
        Assert.Null(VersionText.FromVersionResponse(body));

    [Fact]
    public void TheDashboardVersionComesFromTheAssemblyWithoutBuildMetadata()
    {
        var version = VersionText.OfAssembly(typeof(VersionText).Assembly);

        Assert.NotEqual("unknown", version);
        Assert.DoesNotContain('+', version);
    }
}
