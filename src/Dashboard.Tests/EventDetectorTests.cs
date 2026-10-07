using System.Net;

namespace Dashboard.Tests;

public class EventDetectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 22, 0, 0, TimeSpan.Zero);
    private readonly SignallingTimeProvider _time = new();

    private static NodeObservation Seen(HealthState state, string? version = "2.4.14", long? uptime = null, string? startedAt = null, int? status = null, string? detail = null)
    {
        var telemetry = uptime is null && startedAt is null
            ? null
            : new TelemetrySnapshot(0, 0, 0, 0, null, 0, 0, 0, null, 0, Now)
            {
                Process = uptime is null ? null : new ProcessVitals(null, null, null, null, null, null, uptime),
                StartedAt = startedAt is null ? null : DateTimeOffset.Parse(startedAt, System.Globalization.CultureInfo.InvariantCulture),
            };
        return new NodeObservation(state, version, telemetry, state == HealthState.Pending ? null : new ProbeResult(state, status, status is null ? null : 40, Now, version, detail));
    }

    private static IReadOnlyList<DashboardEvent> Node(NodeObservation before, NodeObservation after, bool isNode = true) =>
        EventDetector.Node(before, after, "uat", "ui", isNode ? "westus3" : "Front Door", isNode, Now);

    private static ServingAssessment Serving(HealthState primary, HealthState standby) =>
        ServingAssessment.Assess([new NodeHealth("west", "westus3", true, primary), new NodeHealth("east", "eastus2", false, standby)], null);

    [Fact]
    public void NothingChangedIsNoEvent()
    {
        Assert.Empty(Node(Seen(HealthState.Healthy, uptime: 600), Seen(HealthState.Healthy, uptime: 630)));
        Assert.Empty(Node(Seen(HealthState.Pending, null), Seen(HealthState.Healthy)));
    }

    [Fact]
    public void AHealthStateChangeIsAnEventWithBothStatesAndTheFacts()
    {
        var down = Assert.Single(Node(Seen(HealthState.Healthy), Seen(HealthState.Unreachable, detail: "No answer within 10 s.")));
        var failing = Assert.Single(Node(Seen(HealthState.Healthy), Seen(HealthState.Unhealthy, status: 503)));
        var back = Assert.Single(Node(Seen(HealthState.Unreachable), Seen(HealthState.Healthy, status: 200)));

        Assert.Equal(new DashboardEvent(Now, EventKind.Health, EventLevel.Problem, "uat", "westus3", "ui: Healthy → Unreachable: No answer within 10 s"), down);
        Assert.Equal((EventLevel.Warning, "ui: Healthy → Unhealthy (HTTP 503)"), (failing.Level, failing.Text));
        Assert.Equal((EventLevel.Good, "ui: Unreachable → Healthy (HTTP 200)"), (back.Level, back.Text));
    }

    [Fact]
    public void AnEndpointThatIsNotHealthyAtTheFirstCheckIsAnEvent()
    {
        var first = Assert.Single(Node(Seen(HealthState.Pending, null), Seen(HealthState.Unhealthy, status: 500), isNode: false));

        Assert.Equal(new DashboardEvent(Now, EventKind.Health, EventLevel.Warning, "uat", "Front Door", "ui: Unhealthy at the first check (HTTP 500)"), first);
    }

    [Fact]
    public void AnUptimeThatWentDownIsARestart()
    {
        var restart = Assert.Single(Node(Seen(HealthState.Healthy, uptime: 86400), Seen(HealthState.Healthy, uptime: 12)));

        Assert.Equal(new DashboardEvent(Now, EventKind.Restart, EventLevel.Warning, "uat", "westus3", "ui restarted, up 12 s"), restart);
    }

    [Fact]
    public void ALaterStartIsARestartOfAnAppThatReportsNoUptime()
    {
        var restart = Assert.Single(Node(
            Seen(HealthState.Healthy, startedAt: "2026-10-04T20:00:00Z"),
            Seen(HealthState.Healthy, startedAt: "2026-10-04T21:59:30Z")));

        Assert.Equal((EventKind.Restart, "ui restarted"), (restart.Kind, restart.Text));
        Assert.Empty(Node(Seen(HealthState.Healthy, startedAt: "2026-10-04T20:00:00Z"), Seen(HealthState.Healthy, startedAt: "2026-10-04T20:00:00Z")));
    }

    [Fact]
    public void WithoutTelemetryOnEitherSideNothingSaysRestart()
    {
        Assert.False(EventDetector.Restarted(null, Seen(HealthState.Healthy, uptime: 5).Telemetry));
        Assert.False(EventDetector.Restarted(Seen(HealthState.Healthy, uptime: 5000).Telemetry, null));
        Assert.Empty(Node(Seen(HealthState.Healthy), Seen(HealthState.Healthy, uptime: 5)));
    }

    [Fact]
    public void AnotherVersionIsADeployment()
    {
        var events = Node(Seen(HealthState.Healthy, "2.4.14", uptime: 9000), Seen(HealthState.Healthy, "2.4.15", uptime: 20));

        Assert.Equal(
            [(EventKind.Restart, "ui restarted, up 20 s"), (EventKind.Version, "ui: 2.4.14 → 2.4.15, deployed")],
            events.Select(entry => (entry.Kind, entry.Text)));
        Assert.Empty(Node(Seen(HealthState.Healthy, null), Seen(HealthState.Healthy, "2.4.15")));
        Assert.Empty(Node(Seen(HealthState.Healthy, "2.4.15"), Seen(HealthState.Healthy, "2.4.15")));
    }

    [Fact]
    public void AFrontDoorEndpointReportsItsStateOnly()
    {
        // It answers with the version of whichever node served, and it has no process.
        Assert.Empty(Node(Seen(HealthState.Healthy, "2.4.14", uptime: 9000), Seen(HealthState.Healthy, "2.4.15", uptime: 20), isNode: false));
    }

    [Fact]
    public void AFailoverAndAFailbackAreEvents()
    {
        var primary = Serving(HealthState.Healthy, HealthState.Healthy);
        var failedOver = Serving(HealthState.Unreachable, HealthState.Healthy);

        var failover = EventDetector.Serving(primary, failedOver, "uat", "ui", Now)!;
        var failback = EventDetector.Serving(failedOver, primary, "uat", "ui", Now)!;

        Assert.Equal(
            new DashboardEvent(Now, EventKind.Serving, EventLevel.Warning, "uat", "ui", "Failover: westus3 → eastus2. Primary westus3 is unreachable; eastus2 is expected to serve traffic."),
            failover);
        Assert.Equal((EventLevel.Good, "Failback: eastus2 → westus3. The primary is healthy again."), (failback.Level, failback.Text));
    }

    [Fact]
    public void NothingServingAndServingAgainAreEvents()
    {
        var primary = Serving(HealthState.Healthy, HealthState.Healthy);
        var down = Serving(HealthState.Unreachable, HealthState.Unhealthy);

        Assert.Equal((EventLevel.Problem, "No healthy node: westus3 no longer serves, and nothing else can."), Text(EventDetector.Serving(primary, down, "uat", "ui", Now)));
        Assert.Equal((EventLevel.Good, "westus3 serves traffic again."), Text(EventDetector.Serving(down, primary, "uat", "ui", Now)));
        Assert.Equal((EventLevel.Warning, "Failed over to eastus2. Primary westus3 is unreachable; eastus2 is expected to serve traffic."), Text(EventDetector.Serving(down, Serving(HealthState.Unreachable, HealthState.Healthy), "uat", "ui", Now)));
    }

    [Fact]
    public void TheFirstDecisionIsAnEventOnlyWhenThePrimaryDoesNotServe()
    {
        var pending = Serving(HealthState.Pending, HealthState.Pending);

        Assert.Null(EventDetector.Serving(null, Serving(HealthState.Healthy, HealthState.Healthy), "uat", "ui", Now));
        Assert.Null(EventDetector.Serving(pending, Serving(HealthState.Healthy, HealthState.Healthy), "uat", "ui", Now));
        Assert.Null(EventDetector.Serving(Serving(HealthState.Healthy, HealthState.Healthy), pending, "uat", "ui", Now));
        Assert.Equal(EventLevel.Warning, EventDetector.Serving(null, Serving(HealthState.Unhealthy, HealthState.Healthy), "uat", "ui", Now)!.Level);
        Assert.Equal((EventLevel.Problem, "No healthy node: nothing can serve traffic."), Text(EventDetector.Serving(pending, Serving(HealthState.Unreachable, HealthState.Unreachable), "uat", "ui", Now)));
    }

    [Fact]
    public void AnOnlyNodeThatStopsAndServesAgainNamesNoFailover()
    {
        static ServingAssessment Only(HealthState state, bool primary = true) =>
            ServingAssessment.Assess([new NodeHealth("uat-ui", null, primary, state)], null);

        Assert.Null(EventDetector.Serving(null, Only(HealthState.Healthy), "uat", "ui", Now));
        Assert.Equal((EventLevel.Problem, "No healthy node: uat-ui no longer serves, and nothing else can."), Text(EventDetector.Serving(Only(HealthState.Healthy), Only(HealthState.Unreachable), "uat", "ui", Now)));
        Assert.Equal((EventLevel.Good, "uat-ui serves traffic again."), Text(EventDetector.Serving(Only(HealthState.Unreachable), Only(HealthState.Healthy), "uat", "ui", Now)));

        // A role other than primary changes nothing: one node cannot fail over.
        Assert.Null(EventDetector.Serving(null, Only(HealthState.Healthy, primary: false), "uat", "ui", Now));
        Assert.Equal((EventLevel.Good, "uat-ui serves traffic again."), Text(EventDetector.Serving(Only(HealthState.Unreachable, primary: false), Only(HealthState.Healthy, primary: false), "uat", "ui", Now)));
    }

    [Fact]
    public void TheSameDecisionIsNoEvent()
    {
        Assert.Null(EventDetector.Serving(Serving(HealthState.Healthy, HealthState.Healthy), Serving(HealthState.Healthy, HealthState.Unreachable), "uat", "ui", Now));
        Assert.Null(EventDetector.Serving(Serving(HealthState.Unreachable, HealthState.Unreachable), Serving(HealthState.Unhealthy, HealthState.Unreachable), "uat", "ui", Now));
    }

    [Fact]
    public void ANewPinIsAnEventAndAnUnreadFileIsNot()
    {
        var before = PinnedVersions.Parse("""{ "ui": "2.4.14" }""");
        var after = PinnedVersions.Parse("""{ "ui": "2.4.15", "api": "1.0.0" }""");

        Assert.Equal(
            new DashboardEvent(Now, EventKind.Pinned, EventLevel.Info, "uat", "ui", "ui: pinned 2.4.14 → 2.4.15 in Git"),
            EventDetector.Pinned(before, after, "uat", "ui", Now));
        Assert.Equal("api: pinned 1.0.0 in Git (its first pin)", EventDetector.Pinned(before, after, "uat", "api", Now)!.Text);
        Assert.Null(EventDetector.Pinned(after, after, "uat", "ui", Now));
        Assert.Null(EventDetector.Pinned(PinnedVersions.Pending, after, "uat", "ui", Now));
        Assert.Null(EventDetector.Pinned(before, PinnedVersions.Unavailable("offline"), "uat", "ui", Now));
        Assert.Null(EventDetector.Pinned(after, before, "uat", "api", Now));
    }

    [Fact]
    public void TheTrafficButtonIsAnEventOfItsEnvironment() =>
        Assert.Equal(
            new DashboardEvent(Now, EventKind.Traffic, EventLevel.Info, "uat", null, "Traffic started"),
            EventDetector.Traffic("uat", "Traffic started", Now));

    [Fact]
    public void TheLogKeepsTheLastFiftyNewestFirst()
    {
        var log = new EventLog();
        for (var index = 1; index <= 60; index++)
        {
            log.Add(EventDetector.Traffic("uat", $"event {index}", Now.AddSeconds(index / 2)));
        }

        var newest = log.Newest;

        Assert.Equal(50, EventLog.DefaultCapacity);
        Assert.Equal(50, log.Count);
        // Two events of the same second stay in the order they were observed.
        Assert.Equal(["event 60", "event 58", "event 59", "event 56"], newest.Take(4).Select(entry => entry.Text));
        Assert.Equal("event 11", newest[^1].Text);
    }

    private static (EventLevel, string) Text(DashboardEvent? entry) => (entry!.Level, entry.Text);

    [Fact]
    public async Task TheMonitorWritesWhatChangesFromRoundToRound()
    {
        var events = new EventLog();
        var (version, westDown, uptime) = ("2.4.14", false, 9000L);
        var handler = new StubHandler(request =>
            westDown && request.RequestUri!.Host == "uat-west.example.net"
                ? throw new HttpRequestException("offline")
                : Optics.Answer(request, version, Optics.Telemetry(uptime: uptime)));
        var monitor = Optics.Monitor(handler, _time, events);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        Assert.Empty(events.Newest);

        // A deployment: every node restarts on the new version.
        _time.Advance(TimeSpan.FromSeconds(30));
        (version, uptime) = ("2.4.15", 15);
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        Assert.Equal(
            ["tdd westus3 ui restarted, up 15 s", "tdd westus3 ui: 2.4.14 → 2.4.15, deployed", "uat eastus2 ui restarted, up 15 s", "uat eastus2 ui: 2.4.14 → 2.4.15, deployed",
             "uat westus3 ui restarted, up 15 s", "uat westus3 ui: 2.4.14 → 2.4.15, deployed"],
            events.Newest.Select(entry => $"{entry.Environment} {entry.Node} {entry.Text}").Order());

        // The primary of uat goes away: its state changes, and the traffic fails over.
        _time.Advance(TimeSpan.FromSeconds(30));
        (westDown, uptime) = (true, 45);
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        var failure = events.Newest.Where(entry => entry.At == _time.GetUtcNow()).ToList();
        Assert.Equal(
            [(EventKind.Health, "westus3"), (EventKind.Serving, "ui")],
            failure.Select(entry => (entry.Kind, entry.Node!)).Order());
        Assert.StartsWith("Failover: westus3 → eastus2.", failure.Single(entry => entry.Kind == EventKind.Serving).Text, StringComparison.Ordinal);

        // It comes back, started anew: healthy again, restarted (compared with its last reading), failed back.
        _time.Advance(TimeSpan.FromSeconds(30));
        (westDown, uptime) = (false, 5);
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        var recovery = events.Newest.Where(entry => entry.At == _time.GetUtcNow()).ToList();
        Assert.Contains(recovery, entry => entry is { Kind: EventKind.Health, Node: "westus3", Level: EventLevel.Good });
        Assert.Contains(recovery, entry => entry is { Kind: EventKind.Restart, Node: "westus3", Environment: "uat" });
        Assert.Contains(recovery, entry => entry is { Kind: EventKind.Serving, Text: "Failback: eastus2 → westus3. The primary is healthy again." });
        Assert.Same(events, monitor.Events);
    }

    [Fact]
    public async Task ANewPinInGitIsWrittenOnceAndAFailedReadingHidesNoChange()
    {
        const string Topology = """
            { "environments": [ { "name": "uat", "versionsUrl": "https://raw.example.net/versions.json",
                "deployables": [ { "name": "ui", "nodes": [ { "name": "uat-west", "region": "westus3", "url": "https://uat-west.example.net" } ] } ] } ] }
            """;
        var (pinned, offline) = ("2.4.14", false);
        var handler = new StubHandler(request => request.RequestUri!.Host != "raw.example.net"
            ? Optics.Answer(request, "2.4.14")
            : offline ? StubHandler.Answer(HttpStatusCode.BadGateway) : StubHandler.Answer(HttpStatusCode.OK, $$"""{ "ui": "{{pinned}}" }"""));
        var monitor = Optics.Monitor(handler, _time, topology: Topology);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        offline = true;
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        Assert.Empty(monitor.Events.Newest);

        (pinned, offline) = ("2.4.15", false);
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Equal("ui: pinned 2.4.14 → 2.4.15 in Git", Assert.Single(monitor.Events.Newest).Text);
    }
}
