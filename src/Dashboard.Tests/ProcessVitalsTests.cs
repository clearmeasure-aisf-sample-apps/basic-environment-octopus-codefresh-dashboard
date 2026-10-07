using System.Net;

namespace Dashboard.Tests;

public class ProcessVitalsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 22, 0, 0, TimeSpan.Zero);
    private readonly SignallingTimeProvider _time = new();

    [Fact]
    public void TheProcessObjectOfTheTelemetryIsRead()
    {
        var snapshot = TelemetrySnapshot.Parse(Optics.Telemetry(), Now)!;

        Assert.Equal(new ProcessVitals(3.2, 412, 96, 41, 2, 0, 5321), snapshot.Process);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 20, 31, 19, TimeSpan.Zero), snapshot.StartedAt);
        Assert.False(snapshot.Process!.RestartedRecently);
    }

    [Theory]
    [InlineData("""{ "requests": { "perMinute": 3 } }""")]
    [InlineData("""{ "requests": { "perMinute": 3 }, "process": null }""")]
    [InlineData("""{ "requests": { "perMinute": 3 }, "process": "busy" }""")]
    [InlineData("""{ "requests": { "perMinute": 3 }, "process": { } }""")]
    [InlineData("""{ "requests": { "perMinute": 3 }, "process": { "cpuPercent": "high", "uptimeSeconds": -4 } }""")]
    public void AnAppWithoutUsableVitalsHasNoneAndKeepsItsCounts(string body)
    {
        var snapshot = TelemetrySnapshot.Parse(body, Now)!;

        Assert.Null(snapshot.Process);
        Assert.Equal(3, snapshot.Requests);
    }

    [Fact]
    public void EveryVitalIsOptional()
    {
        var snapshot = TelemetrySnapshot.Parse("""{ "requests": { }, "process": { "inFlight": 7 } }""", Now)!;

        Assert.Equal(new ProcessVitals(null, null, null, null, 7, null, null), snapshot.Process);
        Assert.False(snapshot.Process!.RestartedRecently);
        Assert.Null(VitalsText.MemoryDetail(snapshot.Process));
    }

    [Theory]
    [InlineData(0, "restarted 0 s ago")]
    [InlineData(45, "restarted 45 s ago")]
    [InlineData(185, "restarted 3 min ago")]
    [InlineData(299, "restarted 4 min ago")]
    [InlineData(300, "up 5 min")]
    [InlineData(3599, "up 59 min")]
    [InlineData(7199, "up 119 min")]
    [InlineData(7300, "up 2 h")]
    [InlineData(172799, "up 47 h")]
    [InlineData(4 * 86400 + 5, "up 4 d")]
    public void UptimeIsWordsAndAYoungProcessIsARestart(long seconds, string expected)
    {
        Assert.Equal(expected, VitalsText.Uptime(seconds));
        Assert.Equal(seconds < 300, new ProcessVitals(null, null, null, null, null, null, seconds).RestartedRecently);
    }

    [Theory]
    [InlineData(3.24, "3.2 %")]
    [InlineData(3.0, "3 %")]
    [InlineData(0, "0 %")]
    [InlineData(41.7, "42 %")]
    public void CpuKeepsOneDecimalBelowTen(double percent, string expected) => Assert.Equal(expected, VitalsText.Cpu(percent));

    [Theory]
    [InlineData(412, "412 MB")]
    [InlineData(1433.6, "1.4 GB")]
    public void MemoryIsMegabytesThenGigabytes(double megabytes, string expected) => Assert.Equal(expected, VitalsText.Memory(megabytes));

    [Fact]
    public void TheMemoryTooltipNamesTheHeapAndTheThreads() =>
        Assert.Equal(
            "Working set 412 MB, of which managed heap 96 MB; 41 threads",
            VitalsText.MemoryDetail(new ProcessVitals(3.2, 412, 96, 41, 2, 0, 5321)));

    private async Task<RuntimeTile> PrimaryTileAsync(string telemetry)
    {
        var monitor = Optics.Monitor(new StubHandler(request => Optics.Answer(request, telemetry: telemetry)), _time);
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        var manifest = new RuntimeManifest(
            "uat",
            [new RuntimeNode("app_ui_primary", RuntimeNodeKind.WebApp, "uat-west", new Uri("https://uat-west.example.net"), "ui", "primary", "westus3", "region_primary")],
            [],
            []);
        return RuntimePayloadBuilder.Build(manifest, monitor.Environments[1], null, TimeZoneInfo.Utc).Nodes.Single();
    }

    private static (string Text, string Tone)[] Words(RuntimeTile tile) => [.. tile.Lines.Select(line => (line.Text, line.Tone))];

    [Fact]
    public async Task TheWebAppTileOfTheDiagramShowsTheVitalsInItsLines()
    {
        var tile = await PrimaryTileAsync(Optics.Telemetry());

        Assert.Equal(
            [
                ("version 2.4.15", "strong"),
                ("12 req/min · p95 85 ms", "plain"),
                ("0 errors · 0 exceptions/min", "plain"),
                ("CPU 3.2 % · 412 MB · 2 in flight", "plain"),
                ("up 88 min", "muted"),
                ("primary: serves traffic", "serving"),
            ],
            Words(tile));
    }

    [Fact]
    public async Task ExceptionsAndARecentRestartAreWarnings()
    {
        var tile = await PrimaryTileAsync(Optics.Telemetry(errors: 1, exceptions: 3, uptime: 185));

        Assert.Contains(("1 error · 3 exceptions/min", "warn"), Words(tile));
        Assert.Contains(("restarted 3 min ago", "warn"), Words(tile));
    }

    [Fact]
    public async Task AnOlderAppWithoutVitalsShowsItsCallsOnly()
    {
        var tile = await PrimaryTileAsync(Optics.Telemetry(uptime: null));

        Assert.Equal(
            [("version 2.4.15", "strong"), ("12 req/min · p95 85 ms", "plain"), ("0 errors", "plain"), ("primary: serves traffic", "serving")],
            Words(tile));
    }

    [Fact]
    public async Task AnAppWithoutTelemetryShowsNeither()
    {
        var monitor = Optics.Monitor(
            new StubHandler(request => request.RequestUri!.AbsolutePath == "/_telemetry" ? StubHandler.Answer(HttpStatusCode.NotFound) : Optics.Answer(request)),
            _time);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.All(monitor.Targets, target =>
        {
            Assert.Null(target.Telemetry);
            Assert.Empty(target.Samples);
        });
    }
}
