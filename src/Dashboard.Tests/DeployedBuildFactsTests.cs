namespace Dashboard.Tests;

/// <summary>
/// The "Code" card of this system: <c>deploy/topology.json</c> names where the work orders app answers the build it
/// runs (<c>/_build</c>, app issue 20260923-001#79), and the answer the app gives is one the card can show. The
/// answers below are the app's own: the record its Codefresh release build writes into the image (version, commit,
/// build, lines of code, tests, coverage, complexity, CRAP), with <c>null</c> for what that build did not measure.
/// </summary>
public class DeployedBuildFactsTests
{
    private static readonly string[] Environments = ["tdd", "uat", "prod"];

    /// <summary>
    /// What the app answers after a usual release: the release build ran the unit and integration tests and the CRAP
    /// audit; the acceptance tests and Qodana ran in the pull request's build, so this build has no number for them.
    /// The text is the answer of the app itself, read on a workstation from a build made with the release pipeline's
    /// own scripts (its build URL is therefore not a real Codefresh build).
    /// </summary>
    private const string UsualRelease = """
        {"version":"2.5.774","commit":"27cbc35c4eac87998c8ed2ab26949eb66d1870a0","commitUrl":"https://github.com/clearmeasure-aisf-sample-apps/20260923-001/commit/27cbc35c4eac87998c8ed2ab26949eb66d1870a0","builtAt":"2026-10-07T05:47:40Z","buildUrl":"https://g.codefresh.io/build/local-e2e","code":{"linesOfCode":60824,"files":814,"languages":[{"name":"C#","lines":44772,"files":657},{"name":"PowerShell","lines":3545,"files":21},{"name":"TypeScript","lines":3153,"files":39},{"name":"CSS","lines":3057,"files":13},{"name":"YAML","lines":2104,"files":8},{"name":"Razor","lines":1629,"files":24},{"name":"SQL","lines":1361,"files":31},{"name":"JavaScript","lines":640,"files":9},{"name":"Shell","lines":439,"files":8},{"name":"HTML","lines":52,"files":1},{"name":"Python","lines":41,"files":2},{"name":"Protocol Buffers","lines":31,"files":1}]},"tests":{"unit":1045,"integration":326,"acceptance":null},"coverage":{"linePercent":89.4,"branchPercent":80.1},"complexity":{"average":2.0,"max":44,"methods":1317},"crap":{"max":6.0,"threshold":6,"overThreshold":0},"analysis":null}
        """;

    /// <summary>What the app answers when its image carries no record: a local run, or a release built before the pipeline wrote one.</summary>
    private const string NoRecord = """
        {"version":"2.5.774","commit":null,"commitUrl":null,"builtAt":null,"buildUrl":null,"code":null,"tests":null,"coverage":null,"complexity":null,"crap":null,"analysis":null}
        """;

    [Fact]
    public void EveryEnvironmentAsksItsAppForTheBuildItRuns()
    {
        var topology = TopologyParser.Parse(File.ReadAllText(DeployedTopology())).Topology!;

        Assert.Equal(Environments, topology.Environments.Select(environment => environment.Name));
        Assert.All(topology.Environments, environment =>
        {
            var deployable = Assert.Single(environment.Deployables);
            Assert.Equal("workorders", deployable.Name);
            Assert.Equal("/_build", deployable.BuildPath);
            Assert.NotNull(new EnvironmentStatus(environment).Deployables[0].BuildSource);
        });
    }

    [Fact]
    public void TheAnswerOfAUsualReleaseFillsTheCard()
    {
        var build = BuildInfo.Parse(UsualRelease)!;

        Assert.Equal("2.5.774", build.Version);
        Assert.Equal("27cbc35", BuildText.ShortCommit(build.Commit!));
        Assert.Equal("https://github.com/clearmeasure-aisf-sample-apps/20260923-001/commit/27cbc35c4eac87998c8ed2ab26949eb66d1870a0", build.CommitUrl?.AbsoluteUri);
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 5, 47, 40, TimeSpan.Zero), build.BuiltAt);
        Assert.Equal("https://g.codefresh.io/build/local-e2e", build.BuildUrl?.AbsoluteUri);
        Assert.Equal("60,824 lines in 814 files", BuildText.Size(build.Code!));
        Assert.Equal("C# 74 %, PowerShell 6 %, TypeScript 5 %, CSS 5 %, YAML 3 %, Other 7 %", BuildText.Describe(BuildText.Shares(build.Code!)));
        Assert.Equal("1,371: 1,045 unit, 326 integration", BuildText.Tests(build.Tests!));
        Assert.Equal("89.4 % of lines, 80.1 % of branches", BuildText.Coverage(build.Coverage!));
        Assert.Equal("average 2, worst 44 (1,317 methods)", BuildText.Complexity(build.Complexity!));
        Assert.Equal("worst 6, none over 6", BuildText.Crap(build.Crap!));
        Assert.False(BuildText.CrapIsOver(build.Crap!));
    }

    [Fact]
    public void WhatTheBuildDidNotMeasureIsNotShownAsANumber()
    {
        var build = BuildInfo.Parse(UsualRelease)!;

        Assert.Null(build.Tests!.Acceptance);
        Assert.Null(build.QodanaProblems);
    }

    [Fact]
    public void AnAnswerWithoutARecordIsACardWithTheVersionAlone()
    {
        var build = BuildInfo.Parse(NoRecord)!;

        Assert.Equal(new BuildInfo("2.5.774", null, null, null, null, null, null, null, null, null, null), build);
    }

    /// <summary><c>deploy/topology.json</c> of the repository: the folder of <c>Dashboard.sln</c> above the tests' output.</summary>
    private static string DeployedTopology()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "Dashboard.sln")))
            {
                return Path.Combine(folder.FullName, "deploy", "topology.json");
            }
        }

        throw new InvalidOperationException($"No Dashboard.sln above {AppContext.BaseDirectory}.");
    }
}
