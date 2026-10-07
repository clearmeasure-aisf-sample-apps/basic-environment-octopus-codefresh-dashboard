using System.Net;

namespace Dashboard.Tests;

public class BuildInfoTests
{
    private readonly SignallingTimeProvider _time = new();

    [Fact]
    public void TheBuildEndpointsAnswerIsRead()
    {
        var build = BuildInfo.Parse(Optics.Build)!;

        Assert.Equal("2.4.15", build.Version);
        Assert.Equal("0a1b2c3d4e5f60718293a4b5c6d7e8f901234567", build.Commit);
        Assert.Equal("https://github.example.net/o/r/commit/0a1b2c3d4e5f60718293a4b5c6d7e8f901234567", build.CommitUrl?.AbsoluteUri);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 20, 0, 0, TimeSpan.Zero), build.BuiltAt);
        Assert.Equal("https://github.example.net/o/r/actions/runs/1", build.BuildUrl?.AbsoluteUri);
        Assert.Equal((84210L, 1203), (build.Code!.LinesOfCode, build.Code.Files));
        Assert.Equal(["C#", "Razor", "SQL", "JavaScript", "CSS", "PowerShell", "YAML"], build.Code.Languages.Select(language => language.Name));
        Assert.Equal(new TestCounts(1009, 240, 168), build.Tests);
        Assert.Equal(new CoverageFacts(81.2, 70.1), build.Coverage);
        Assert.Equal(new ComplexityFacts(1.9, 34, 5210), build.Complexity);
        Assert.Equal(new CrapFacts(28.5, 30, 0), build.Crap);
        Assert.Equal(0, build.QodanaProblems);
    }

    [Fact]
    public void AnySectionMayBeNullOrMissing()
    {
        var build = BuildInfo.Parse("""{ "version": "2.4.15", "commit": "0a1b2c3", "commitUrl": "javascript:alert(1)", "code": null, "tests": { "unit": 12 }, "coverage": "none", "crap": { } }""")!;

        Assert.Equal("2.4.15", build.Version);
        Assert.Null(build.CommitUrl);
        Assert.Null(build.Code);
        Assert.Equal(new TestCounts(12, null, null), build.Tests);
        Assert.Null(build.Coverage);
        Assert.Null(build.Complexity);
        Assert.Null(build.Crap);
        Assert.Null(build.QodanaProblems);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html>Not found</html>")]
    [InlineData("[]")]
    [InlineData("{ }")]
    [InlineData("""{ "status": "Healthy" }""")]
    public void AnythingButBuildFactsIsNoBuild(string? body) => Assert.Null(BuildInfo.Parse(body));

    [Fact]
    public void TheCardsWordsAreDecidedHere()
    {
        var build = BuildInfo.Parse(Optics.Build)!;

        Assert.Equal("0a1b2c3", BuildText.ShortCommit(build.Commit!));
        Assert.Equal("84,210 lines in 1,203 files", BuildText.Size(build.Code!));
        Assert.Equal("1,417: 1,009 unit, 240 integration, 168 acceptance", BuildText.Tests(build.Tests!));
        Assert.Equal("81.2 % of lines, 70.1 % of branches", BuildText.Coverage(build.Coverage!));
        Assert.Equal("average 1.9, worst 34 (5,210 methods)", BuildText.Complexity(build.Complexity!));
        Assert.Equal("worst 28.5, none over 30", BuildText.Crap(build.Crap!));
        Assert.False(BuildText.CrapIsOver(build.Crap!));
        Assert.Equal("no problems", BuildText.Problems(build.QodanaProblems!.Value));
    }

    [Fact]
    public void PartialFactsAreSaidWithWhatIsThere()
    {
        Assert.Equal("12 unit", BuildText.Tests(new TestCounts(12, null, null)));
        Assert.Equal("70 % of branches", BuildText.Coverage(new CoverageFacts(null, 70)));
        Assert.Equal("worst 34", BuildText.Complexity(new ComplexityFacts(null, 34, null)));
        Assert.Equal("worst 41, 3 methods over 30", BuildText.Crap(new CrapFacts(41, 30, 3)));
        Assert.Equal("worst 41, 1 method over the threshold", BuildText.Crap(new CrapFacts(41, null, 1)));
        Assert.Equal("worst 12, threshold 30", BuildText.Crap(new CrapFacts(12, 30, null)));
        Assert.True(BuildText.CrapIsOver(new CrapFacts(41, 30, 3)));
        Assert.True(BuildText.CrapIsOver(new CrapFacts(41, 30, null)));
        Assert.Equal("1 problem", BuildText.Problems(1));
        Assert.Equal("1,204 problems", BuildText.Problems(1204));
        Assert.Equal("1,203 files", BuildText.Size(new CodeSize(null, 1203, [])));
    }

    [Fact]
    public void TheLanguageBarNamesTheFiveLargestAndSumsTheRest()
    {
        var shares = BuildText.Shares(BuildInfo.Parse(Optics.Build)!.Code!);

        Assert.Equal(["C#", "Razor", "SQL", "JavaScript", "CSS", "Other"], shares.Select(share => share.Name));
        Assert.Equal([1, 2, 3, 4, 5, 0], shares.Select(share => share.Slot));
        Assert.Equal(1976, shares[^1].Lines);
        Assert.Equal(100, shares.Sum(share => share.Percent), 6);
        Assert.Equal("C# 73 %, Razor 11 %, SQL 6 %, JavaScript 5 %, CSS 4 %, Other 2 %", BuildText.Describe(shares));
    }

    [Fact]
    public void ASmallShareIsNotRoundedToNothingAndNoLanguagesIsNoBar()
    {
        var shares = BuildText.Shares(new CodeSize(1000, null, [new LanguageSize("C#", 998, null), new LanguageSize("YAML", 2, null)]));

        Assert.Equal("C# 100 %, YAML <1 %", BuildText.Describe(shares));
        Assert.Empty(BuildText.Shares(new CodeSize(1000, 3, [])));
    }

    [Fact]
    public void WithoutATotalTheLinesOfTheLanguagesAreAdded()
    {
        var build = BuildInfo.Parse("""{ "code": { "languages": [ { "name": "C#", "lines": 700 }, { "name": "SQL", "lines": 300 }, { "name": "", "lines": 5 }, { "lines": 9 } ] } }""")!;

        Assert.Equal(1000, build.Code!.LinesOfCode);
        Assert.Equal(2, build.Code.Languages.Count);
    }

    [Fact]
    public void TheTopologyCarriesTheBuildPath()
    {
        var topology = TopologyParser.Parse(Optics.Topology).Topology!;

        Assert.Equal("/_build", topology.Environments[0].Deployables[0].BuildPath);
        Assert.Equal("/_build", topology.Environments[1].Deployables[0].BuildPath);
        Assert.Null(TopologyParser.Parse("""{ "environments": [ { "name": "tdd", "deployables": [ { "nodes": [] } ] } ] }""").Topology!.Environments[0].Deployables[0].BuildPath);
    }

    [Fact]
    public async Task TheBuildIsReadFromThePrimaryNodeOncePerTopologyAndNotEveryRound()
    {
        var handler = new StubHandler(request => Optics.Answer(request));
        var monitor = Optics.Monitor(handler, _time);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        await monitor.CheckAllAsync(ProbeKind.Liveness, CancellationToken.None);

        Assert.Equal(
            ["https://tdd-west.example.net/_build", "https://uat-west.example.net/_build"],
            handler.RequestedUrls.Where(url => url.EndsWith("/_build", StringComparison.Ordinal)).Order());
        Assert.All(monitor.Environments, environment => Assert.Equal("2.4.15", environment.Deployables[0].Build!.Version));
    }

    [Fact]
    public async Task ANewVersionOfTheNodeIsANewBuildAndEachEnvironmentShowsItsOwn()
    {
        var uat = "2.4.14";
        var handler = new StubHandler(request =>
        {
            var version = request.RequestUri!.Host.StartsWith("tdd", StringComparison.Ordinal) ? "2.4.15" : uat;
            return request.RequestUri.AbsolutePath == "/_build"
                ? StubHandler.Answer(HttpStatusCode.OK, $$"""{ "version": "{{version}}", "commit": "c-{{version}}" }""")
                : Optics.Answer(request, version);
        });
        var monitor = Optics.Monitor(handler, _time);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        Assert.Equal(["c-2.4.15", "c-2.4.14"], monitor.Environments.Select(environment => environment.Deployables[0].Build!.Commit));

        uat = "2.4.15";
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Equal(["c-2.4.15", "c-2.4.15"], monitor.Environments.Select(environment => environment.Deployables[0].Build!.Commit));
        Assert.Equal(1, handler.RequestedUrls.Count(url => url == "https://tdd-west.example.net/_build"));
        Assert.Equal(2, handler.RequestedUrls.Count(url => url == "https://uat-west.example.net/_build"));
    }

    [Fact]
    public async Task ANodeWithoutTheEndpointHasNoBuildAndIsNotAskedAgain()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath == "/_build" ? StubHandler.Answer(HttpStatusCode.NotFound, "<html>") : Optics.Answer(request));
        var monitor = Optics.Monitor(handler, _time);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.All(monitor.Environments, environment => Assert.Null(environment.Deployables[0].Build));
        Assert.Equal(2, Optics.Count(handler, "/_build"));
        Assert.All(monitor.Targets, target => Assert.Equal(HealthState.Healthy, target.State));
    }

    [Fact]
    public async Task AnUnreachablePrimaryIsAskedForItsBuildOnceItAnswers()
    {
        var down = true;
        var handler = new StubHandler(request => down && request.RequestUri!.Host == "uat-west.example.net" ? throw new HttpRequestException("offline") : Optics.Answer(request));
        var monitor = Optics.Monitor(handler, _time);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        Assert.Null(monitor.Environments[1].Deployables[0].Build);
        Assert.DoesNotContain("https://uat-west.example.net/_build", handler.RequestedUrls);

        down = false;
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Equal("2.4.15", monitor.Environments[1].Deployables[0].Build!.Version);
    }

    [Fact]
    public async Task WithoutABuildPathNothingIsAsked()
    {
        var handler = new StubHandler(request => Optics.Answer(request));
        var monitor = Optics.Monitor(handler, _time, topology: Optics.Topology.Replace("buildPath", "ignored", StringComparison.Ordinal));

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Equal(0, Optics.Count(handler, "/_build"));
        Assert.All(monitor.Environments, environment => Assert.Null(environment.Deployables[0].BuildSource));
    }
}
