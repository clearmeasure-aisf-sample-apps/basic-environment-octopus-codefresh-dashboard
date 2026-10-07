using System.Net;

namespace Dashboard.Tests;

public class NodeProberTests
{
    private static readonly Uri Node = new("https://app-demo-tdd-ui.azurewebsites.net");
    private readonly SignallingTimeProvider _time = new();

    private NodeProber Prober(StubHandler handler) => new(new HttpClient(handler), _time);

    private static bool IsVersion(HttpRequestMessage request) => request.RequestUri!.AbsolutePath == "/_version";

    [Fact]
    public async Task Status200IsHealthyWithStatusLatencyAndTime()
    {
        var handler = new StubHandler(request =>
        {
            if (!IsVersion(request))
            {
                _time.Advance(TimeSpan.FromMilliseconds(42));
            }

            return StubHandler.Answer(HttpStatusCode.OK, """{"version":"2.4.21+0a1b2c3"}""");
        });
        var started = _time.GetUtcNow();

        var result = await Prober(handler).ProbeAsync(Node, "/_healthcheck", "/_version", CancellationToken.None);

        Assert.Equal(HealthState.Healthy, result.State);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(42, result.LatencyMs);
        Assert.Equal(started, result.CheckedAt);
        Assert.Equal("2.4.21", result.Version);
        Assert.Null(result.Detail);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task AnyOtherStatusIsUnhealthy(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => StubHandler.Answer(status));

        var result = await Prober(handler).ProbeAsync(Node, "/_healthcheck", null, CancellationToken.None);

        Assert.Equal(HealthState.Unhealthy, result.State);
        Assert.Equal((int)status, result.StatusCode);
        Assert.NotNull(result.LatencyMs);
    }

    [Fact]
    public async Task ANetworkOrCorsFailureIsUnreachable()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("TypeError: Failed to fetch"));

        var result = await Prober(handler).ProbeAsync(Node, "/_healthcheck", "/_version", CancellationToken.None);

        Assert.Equal(HealthState.Unreachable, result.State);
        Assert.Null(result.StatusCode);
        Assert.Null(result.LatencyMs);
        Assert.Null(result.Version);
        Assert.Contains("CORS", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoAnswerWithinTheTimeoutIsUnreachable()
    {
        var handler = new StubHandler((_, cancellationToken) => StubHandler.NeverAsync(cancellationToken));

        var probe = Prober(handler).ProbeAsync(Node, "/_healthcheck", "/_version", CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(9));
        Assert.False(probe.IsCompleted);
        _time.Advance(TimeSpan.FromSeconds(1));
        var result = await probe;

        Assert.Equal(TimeSpan.FromSeconds(10), NodeProber.DefaultTimeout);
        Assert.Equal(HealthState.Unreachable, result.State);
        Assert.Null(result.StatusCode);
        Assert.Equal("No answer within 10 s.", result.Detail);
    }

    [Fact]
    public async Task TheTimeoutCanBeSet()
    {
        var handler = new StubHandler((_, cancellationToken) => StubHandler.NeverAsync(cancellationToken));
        var prober = new NodeProber(new HttpClient(handler), _time) { Timeout = TimeSpan.FromSeconds(2) };

        var probe = prober.ProbeAsync(Node, "/_healthcheck", null, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal("No answer within 2 s.", (await probe).Detail);
    }

    [Fact]
    public async Task TheCallersCancellationIsNotAResult()
    {
        var handler = new StubHandler((_, cancellationToken) => StubHandler.NeverAsync(cancellationToken));
        using var cancellation = new CancellationTokenSource();

        var probe = Prober(handler).ProbeAsync(Node, "/_healthcheck", "/_version", cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"version":"9.9.9"}""")]
    [InlineData(HttpStatusCode.OK, "not json")]
    [InlineData(HttpStatusCode.OK, "")]
    public async Task AnUnreadableVersionDoesNotChangeTheHealth(HttpStatusCode versionStatus, string versionBody)
    {
        var handler = new StubHandler(request =>
            IsVersion(request) ? StubHandler.Answer(versionStatus, versionBody) : StubHandler.Answer(HttpStatusCode.OK));

        var result = await Prober(handler).ProbeAsync(Node, "/_healthcheck", "/_version", CancellationToken.None);

        Assert.Equal(HealthState.Healthy, result.State);
        Assert.Null(result.Version);
    }

    [Fact]
    public async Task AFailingVersionEndpointDoesNotChangeTheHealth()
    {
        var handler = new StubHandler(request =>
            IsVersion(request) ? throw new HttpRequestException("blocked") : StubHandler.Answer(HttpStatusCode.OK));

        var result = await Prober(handler).ProbeAsync(Node, "/_healthcheck", "/_version", CancellationToken.None);

        Assert.Equal(HealthState.Healthy, result.State);
        Assert.Null(result.Version);
    }

    [Fact]
    public async Task AnUnhealthyNodeStillReportsItsVersion()
    {
        var handler = new StubHandler(request => IsVersion(request)
            ? StubHandler.Answer(HttpStatusCode.OK, """{"version":"2.4.21+sha"}""")
            : StubHandler.Answer(HttpStatusCode.ServiceUnavailable));

        var result = await Prober(handler).ProbeAsync(Node, "/_healthcheck", "/_version", CancellationToken.None);

        Assert.Equal(HealthState.Unhealthy, result.State);
        Assert.Equal("2.4.21", result.Version);
    }

    [Fact]
    public async Task TheProbePathAndTheVersionPathAreCalledWithoutTheBrowserCache()
    {
        var handler = new StubHandler(_ => StubHandler.Answer(HttpStatusCode.OK));

        await Prober(handler).ProbeAsync(Node, "/alive", "/_version", CancellationToken.None);

        Assert.Equal(
            ["https://app-demo-tdd-ui.azurewebsites.net/_version", "https://app-demo-tdd-ui.azurewebsites.net/alive"],
            handler.RequestedUrls.Order(StringComparer.Ordinal));
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("no-store", FetchOption(request, "cache"));
        });
    }

    [Fact]
    public async Task WithoutVersionPathOnlyTheProbeIsCalled()
    {
        var handler = new StubHandler(_ => StubHandler.Answer(HttpStatusCode.OK));

        var result = await Prober(handler).ProbeAsync(Node, "/_healthcheck", null, CancellationToken.None);

        Assert.Equal("https://app-demo-tdd-ui.azurewebsites.net/_healthcheck", Assert.Single(handler.RequestedUrls));
        Assert.Null(result.Version);
    }

    /// <summary>The option Blazor's browser HTTP handler passes to <c>fetch</c>.</summary>
    internal static object? FetchOption(HttpRequestMessage request, string name) =>
        request.Options.TryGetValue(new HttpRequestOptionsKey<IDictionary<string, object>>("WebAssemblyFetchOptions"), out var options)
        && options.TryGetValue(name, out var value)
            ? value
            : null;
}
