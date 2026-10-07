namespace Dashboard.Tests;

public class ProbeUrlTests
{
    [Theory]
    [InlineData("https://app.example.net", "/_healthcheck", "https://app.example.net/_healthcheck")]
    [InlineData("https://app.example.net/", "/_healthcheck", "https://app.example.net/_healthcheck")]
    [InlineData("https://app.example.net/", "alive", "https://app.example.net/alive")]
    [InlineData("https://app.example.net/base/", "/_version", "https://app.example.net/base/_version")]
    [InlineData("http://localhost:5000", "/health/ready", "http://localhost:5000/health/ready")]
    public void BaseAddressAndPathAreJoinedWithOneSlash(string baseAddress, string path, string expected) =>
        Assert.Equal(expected, ProbeUrl.Combine(new Uri(baseAddress), path).ToString());
}
