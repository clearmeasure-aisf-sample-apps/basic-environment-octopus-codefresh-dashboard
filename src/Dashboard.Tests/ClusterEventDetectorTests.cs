namespace Dashboard.Tests;

public class ClusterEventDetectorTests
{
    private static readonly DateTimeOffset Generated = ClusterFixture.Generated;
    private static readonly DateTimeOffset Now = Generated.AddSeconds(5);

    private static readonly EnvironmentInfo[] Environments = [ClusterFixture.Environment("tdd", "cmdemo3-tdd")];

    private static IReadOnlyList<DashboardEvent> Between(ClusterStatus before, ClusterStatus after) =>
        ClusterEventDetector.Status(before, after, Environments, Now);

    private static ClusterStatus With(params ClusterPod[] pods) => ClusterFixture.Status(null, ClusterFixture.Space("cmdemo3-tdd", pods));

    private static DashboardEvent Event(EventLevel level, string? environment, string text, string place = "cluster") =>
        new(Now, EventKind.Cluster, level, environment, place, text);

    [Fact]
    public void TheStatusFileThatStopsAnsweringAndAnswersAgainAreEvents()
    {
        var stopped = ClusterEventDetector.Liveness(ClusterLiveness.Live, ClusterLiveness.Silent, "no answer within 10 s", Now);
        var back = ClusterEventDetector.Liveness(ClusterLiveness.Silent, ClusterLiveness.Live, null, Now);
        var broken = ClusterEventDetector.Liveness(ClusterLiveness.Live, ClusterLiveness.Unreadable, "the file is not valid JSON", Now);

        Assert.Equal(Event(EventLevel.Problem, null, "The cluster's status file stopped answering: no answer within 10 s"), stopped);
        Assert.Equal(Event(EventLevel.Good, null, "The cluster's status file answers again"), back);
        Assert.Equal(Event(EventLevel.Problem, null, "The cluster's status file could not be read: the file is not valid JSON"), broken);
    }

    [Fact]
    public void ACollectorThatStopsWritingAndWritesAgainAreEvents()
    {
        var stale = ClusterEventDetector.Liveness(ClusterLiveness.Live, ClusterLiveness.Stale, null, Now);
        var fresh = ClusterEventDetector.Liveness(ClusterLiveness.Stale, ClusterLiveness.Live, null, Now);

        Assert.Equal(Event(EventLevel.Problem, null, "The collector in the cluster stopped writing: the status file is stale"), stale);
        Assert.Equal(Event(EventLevel.Good, null, "The collector in the cluster writes again"), fresh);
    }

    [Theory]
    [InlineData(ClusterLiveness.Pending, ClusterLiveness.Silent)]
    [InlineData(ClusterLiveness.Pending, ClusterLiveness.Live)]
    [InlineData(ClusterLiveness.Pending, ClusterLiveness.Stale)]
    [InlineData(ClusterLiveness.Live, ClusterLiveness.Live)]
    [InlineData(ClusterLiveness.Silent, ClusterLiveness.Silent)]
    public void TheFirstReadingAndAnUnchangedOneAreNoEvent(ClusterLiveness before, ClusterLiveness after)
    {
        Assert.Null(ClusterEventDetector.Liveness(before, after, "no answer within 10 s", Now));
    }

    [Fact]
    public void ANodeThatIsNoLongerReadyAndOneThatIsReadyAgainAreEvents()
    {
        var ready = ClusterFixture.Status([ClusterFixture.Node()]);
        var notReady = ClusterFixture.Status([ClusterFixture.Node(ready: false)]);

        Assert.Equal([Event(EventLevel.Problem, null, "Node aks-…000000 is not ready")], Between(ready, notReady));
        Assert.Equal([Event(EventLevel.Good, null, "Node aks-…000000 is ready again")], Between(notReady, ready));
        Assert.Empty(Between(ready, ready));
    }

    [Fact]
    public void ANodeTheLastStatusDidNotHaveIsNoEvent()
    {
        var one = ClusterFixture.Status([ClusterFixture.Node()]);
        var two = ClusterFixture.Status([ClusterFixture.Node(), ClusterFixture.Node("aks-system-12148983-vmss000001", false)]);

        Assert.Empty(Between(one, two));
        Assert.Empty(Between(two, one));
    }

    [Fact]
    public void ARestartCountThatRoseIsAnEventOfThePodsEnvironment()
    {
        var before = With(ClusterFixture.Pod("ui", ready: false, restarts: 6, reason: "CrashLoopBackOff"));
        var after = With(ClusterFixture.Pod("ui", ready: false, restarts: 7, reason: "CrashLoopBackOff"));

        Assert.Equal([Event(EventLevel.Warning, "tdd", "ui in cmdemo3-tdd restarted (7 restarts): CrashLoopBackOff")], Between(before, after));
    }

    [Fact]
    public void ARestartOfAPodThatIsReadyAgainNamesNoReasonAndAPodOfThePlatformNoEnvironment()
    {
        var before = ClusterFixture.Status(null, ClusterFixture.Space("argocd", ClusterFixture.Pod("server")));
        var after = ClusterFixture.Status(null, ClusterFixture.Space("argocd", ClusterFixture.Pod("server", restarts: 1)));

        Assert.Equal([Event(EventLevel.Warning, null, "server in argocd restarted (1 restart)")], Between(before, after));
    }

    [Fact]
    public void APodThatBecomesUnhealthyAndOneThatIsReadyAgainAreEvents()
    {
        var ready = With(ClusterFixture.Pod("ui"));
        var failing = With(ClusterFixture.Pod("ui", ready: false, reason: "ImagePullBackOff"));
        var quiet = With(ClusterFixture.Pod("ui", ready: false));

        Assert.Equal([Event(EventLevel.Warning, "tdd", "ui in cmdemo3-tdd is unhealthy: ImagePullBackOff")], Between(ready, failing));
        Assert.Equal([Event(EventLevel.Warning, "tdd", "ui in cmdemo3-tdd is unhealthy: not ready")], Between(ready, quiet));
        Assert.Equal([Event(EventLevel.Good, "tdd", "ui in cmdemo3-tdd is ready again")], Between(failing, ready));
        Assert.Empty(Between(failing, failing));
    }

    [Fact]
    public void APodHasOneEventPerRoundAndTheRestartComesFirst()
    {
        var before = With(ClusterFixture.Pod("ui"));
        var after = With(ClusterFixture.Pod("ui", ready: false, restarts: 1, reason: "CrashLoopBackOff"));

        Assert.Equal([Event(EventLevel.Warning, "tdd", "ui in cmdemo3-tdd restarted (1 restart): CrashLoopBackOff")], Between(before, after));
    }

    [Fact]
    public void ANewPodIsAnEventOnlyWhenItIsUnhealthyAndAPodThatStartsIsNone()
    {
        var before = With(ClusterFixture.Pod("ui"));
        var replaced = With(ClusterFixture.Pod("ui"), ClusterFixture.Pod("ui", name: "ui-1", ready: false, age: TimeSpan.FromSeconds(20)));
        var evicted = With(ClusterFixture.Pod("ui"), ClusterFixture.Pod("ui", ClusterPod.Failed, false, restarts: 3, reason: "Evicted", name: "ui-1"));

        Assert.Empty(Between(before, replaced));
        Assert.Equal([Event(EventLevel.Warning, "tdd", "ui in cmdemo3-tdd is unhealthy: Evicted")], Between(before, evicted));
        Assert.Empty(Between(replaced, before));
    }

    [Fact]
    public void APodThatOutgrowsItsGraceBecomesUnhealthy()
    {
        var young = With(ClusterFixture.Pod("ui", ClusterPod.Pending, false, age: TimeSpan.FromMinutes(4)));
        var old = With(ClusterFixture.Pod("ui", ClusterPod.Pending, false, age: TimeSpan.FromMinutes(6)));

        Assert.Equal([Event(EventLevel.Warning, "tdd", "ui in cmdemo3-tdd is unhealthy: Pending for 6 min")], Between(young, old));
    }

    [Fact]
    public void ManyPodsThatChangeAtOnceAreNamedUpToTenAndThenCounted()
    {
        var names = Enumerable.Range(1, 14).Select(index => $"app{index:00}").ToList();
        var before = With([.. names.Select(name => ClusterFixture.Pod(name))]);
        var after = With([.. names.Select(name => ClusterFixture.Pod(name, ready: false))]);

        var events = Between(before, after);

        Assert.Equal(11, events.Count);
        Assert.Equal("app01 in cmdemo3-tdd is unhealthy: not ready", events[0].Text);
        Assert.Equal("app10 in cmdemo3-tdd is unhealthy: not ready", events[9].Text);
        Assert.Equal(Event(EventLevel.Info, null, "4 more pods changed in this check"), events[10]);
    }

    [Fact]
    public void AChangeOfAzuresVerdictOrOfThePowerStateIsAnEvent()
    {
        var available = ClusterFixture.SampleService;
        AksService Verdict(string state) => available with { Availability = new AksAvailability(state, null, null, null) };
        var stopped = available with { PowerState = "Stopped" };

        Assert.Equal(
            [Event(EventLevel.Warning, null, "Azure's verdict on the AKS service: Available → Degraded", "AKS")],
            ClusterEventDetector.Service(available, Verdict("Degraded"), Now));
        Assert.Equal(EventLevel.Problem, Assert.Single(ClusterEventDetector.Service(available, Verdict("Unavailable"), Now)).Level);
        Assert.Equal(EventLevel.Info, Assert.Single(ClusterEventDetector.Service(available, Verdict("Unknown"), Now)).Level);
        Assert.Equal(
            [Event(EventLevel.Good, null, "Azure's verdict on the AKS service: Unavailable → Available", "AKS")],
            ClusterEventDetector.Service(Verdict("Unavailable"), available, Now));
        Assert.Equal(
            [Event(EventLevel.Info, null, "The power state of the AKS service: Running → Stopped", "AKS")],
            ClusterEventDetector.Service(available, stopped, Now));
        Assert.Equal(
            [Event(EventLevel.Good, null, "The power state of the AKS service: Stopped → Running", "AKS")],
            ClusterEventDetector.Service(stopped, available, Now));
    }

    [Fact]
    public void FactsThatSayTheSameOrLackAStateAreNoEvent()
    {
        var available = ClusterFixture.SampleService;

        Assert.Empty(ClusterEventDetector.Service(available, available with { Generated = Now }, Now));
        Assert.Empty(ClusterEventDetector.Service(available, available with { Availability = null, PowerState = null }, Now));
        Assert.Empty(ClusterEventDetector.Service(available with { Availability = null, PowerState = null }, available, Now));
    }
}
