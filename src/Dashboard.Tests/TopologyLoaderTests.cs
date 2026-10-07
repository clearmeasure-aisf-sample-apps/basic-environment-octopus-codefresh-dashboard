using System.Net;

namespace Dashboard.Tests;

public class TopologyLoaderTests
{
    private static TopologyLoader Loader(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://dashboard.example.net/") });

    [Fact]
    public async Task TheFileNextToTheDashboardIsLoadedWithoutTheBrowserCache()
    {
        var handler = new StubHandler(_ => StubHandler.Answer(HttpStatusCode.OK, """{ "system": { "name": "Demo" }, "environments": [] }"""));

        var result = await Loader(handler).LoadAsync(CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("Demo", result.Topology!.System.Name);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://dashboard.example.net/topology.json", request.RequestUri!.ToString());
        Assert.Equal("no-store", NodeProberTests.FetchOption(request, "cache"));
    }

    [Fact]
    public async Task AMissingFileIsAnErrorWithTheStatus()
    {
        var handler = new StubHandler(_ => StubHandler.Answer(HttpStatusCode.NotFound));

        var result = await Loader(handler).LoadAsync(CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("The server answered HTTP 404 for topology.json.", Assert.Single(result.Errors));
    }

    [Fact]
    public async Task ANetworkFailureIsAnErrorWithTheReason()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));

        var result = await Loader(handler).LoadAsync(CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("The file could not be loaded: connection refused", Assert.Single(result.Errors));
    }

    [Fact]
    public async Task AMalformedFileIsAnErrorWithTheReason()
    {
        // A static host that answers every unknown path with index.html.
        var handler = new StubHandler(_ => StubHandler.Answer(HttpStatusCode.OK, "<!DOCTYPE html><html></html>"));

        var result = await Loader(handler).LoadAsync(CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("The file is not valid JSON (line 1, position 1).", Assert.Single(result.Errors));
    }
}
