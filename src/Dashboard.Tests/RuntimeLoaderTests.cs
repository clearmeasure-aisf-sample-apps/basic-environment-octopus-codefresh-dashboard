using System.Net;

namespace Dashboard.Tests;

public class RuntimeLoaderTests
{
    private static RuntimeLoader Loader(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://dashboard.example.net/") });

    private static HttpResponseMessage Serve(HttpRequestMessage request) => request.RequestUri!.AbsolutePath switch
    {
        "/runtime/index.json" => StubHandler.Answer(HttpStatusCode.OK, RuntimeManifestParserTests.Sample("index.json")),
        "/runtime/uat.json" => StubHandler.Answer(HttpStatusCode.OK, RuntimeManifestParserTests.Sample("uat.json")),
        "/runtime/uat.svg" => StubHandler.Answer(HttpStatusCode.OK, RuntimeManifestParserTests.Sample("uat.svg")),
        _ => StubHandler.Answer(HttpStatusCode.NotFound),
    };

    [Fact]
    public async Task TheIndexOfADeploymentWithDiagramsIsAvailable()
    {
        var handler = new StubHandler(Serve);

        var result = await Loader(handler).LoadIndexAsync(CancellationToken.None);

        Assert.Equal(RuntimeAvailability.Available, result.Availability);
        Assert.Equal(["tdd", "uat"], result.Index!.Environments.Select(entry => entry.Name));
        Assert.Equal(["https://dashboard.example.net/runtime/index.json"], handler.RequestedUrls);
    }

    [Fact]
    public async Task ADeploymentWithoutRuntimeIsNotAnError()
    {
        var result = await Loader(new StubHandler(_ => StubHandler.Answer(HttpStatusCode.NotFound))).LoadIndexAsync(CancellationToken.None);

        Assert.Equal(RuntimeAvailability.NotDeployed, result.Availability);
        Assert.Null(result.Index);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task AHostThatServesThePageForEveryAddressHasNoRuntimeEither()
    {
        var result = await Loader(new StubHandler(_ => StubHandler.Answer(HttpStatusCode.OK, "<!DOCTYPE html><html></html>")))
            .LoadIndexAsync(CancellationToken.None);

        Assert.Equal(RuntimeAvailability.NotDeployed, result.Availability);
    }

    [Fact]
    public async Task AnIndexThatCannotBeReadIsReportedWithItsReason()
    {
        var failed = await Loader(new StubHandler(_ => StubHandler.Answer(HttpStatusCode.InternalServerError))).LoadIndexAsync(CancellationToken.None);
        var broken = await Loader(new StubHandler(_ => StubHandler.Answer(HttpStatusCode.OK, "{ }"))).LoadIndexAsync(CancellationToken.None);
        var offline = await Loader(new StubHandler(_ => throw new HttpRequestException("offline"))).LoadIndexAsync(CancellationToken.None);

        Assert.Equal(RuntimeAvailability.Failed, failed.Availability);
        Assert.Equal(["The server answered HTTP 500 for runtime/index.json."], failed.Errors);
        Assert.Equal(["runtime/index.json: environments: missing or not an array."], broken.Errors);
        Assert.Equal(["runtime/index.json could not be loaded: offline"], offline.Errors);
    }

    [Fact]
    public async Task ADiagramIsItsManifestAndItsSvg()
    {
        var diagram = await Loader(new StubHandler(Serve)).LoadDiagramAsync(new RuntimeIndexEntry("uat", "uat.json", "uat.svg"), CancellationToken.None);

        Assert.True(diagram.IsValid);
        Assert.Equal("uat", diagram.Manifest!.Environment);
        Assert.StartsWith("<svg", diagram.Svg, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADiagramWithoutItsSvgOrManifestIsNotShown()
    {
        var diagram = await Loader(new StubHandler(Serve)).LoadDiagramAsync(new RuntimeIndexEntry("prod", "prod.json", "prod.svg"), CancellationToken.None);

        Assert.False(diagram.IsValid);
        Assert.Null(diagram.Manifest);
        Assert.Equal(
            ["The server answered HTTP 404 for runtime/prod.json.", "The server answered HTTP 404 for runtime/prod.svg."],
            diagram.Errors);
    }

    [Fact]
    public async Task AnSvgThatIsNoSvgIsReported()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith(".svg", StringComparison.Ordinal)
            ? StubHandler.Answer(HttpStatusCode.OK, "not a picture")
            : Serve(request));

        var diagram = await Loader(handler).LoadDiagramAsync(new RuntimeIndexEntry("uat", "uat.json", "uat.svg"), CancellationToken.None);

        Assert.Equal(["runtime/uat.svg: not an SVG."], diagram.Errors);
    }
}
