namespace Dashboard.Tests;

public class ClusterGroupsTests
{
    private static readonly DateTimeOffset Generated = ClusterFixture.Generated;

    private static readonly EnvironmentInfo[] Environments =
    [
        ClusterFixture.Environment("tdd", "cmdemo3-tdd"),
        ClusterFixture.Environment("uat", "cmdemo3-uat"),
        ClusterFixture.Environment("prod", "cmdemo3-prod", "prod"),
    ];

    [Fact]
    public void TheNamespacesOfTheEnvironmentsComeFirstInTheOrderOfTheTopologyThenTheOthersByName()
    {
        var status = ClusterFixture.Status(
            null,
            ClusterFixture.Space("kube-system", ClusterFixture.Pod("coredns")),
            ClusterFixture.Space("cmdemo3-prod", ClusterFixture.Pod("ui")),
            ClusterFixture.Space("Envoy-gateway", ClusterFixture.Pod("envoy")),
            ClusterFixture.Space("argocd", ClusterFixture.Pod("server")),
            ClusterFixture.Space("cmdemo3-tdd", ClusterFixture.Pod("ui")));

        var groups = ClusterGroups.Of(status, Environments, Generated);

        Assert.Equal(["cmdemo3-tdd", "cmdemo3-uat", "cmdemo3-prod"], groups.Environments.Select(group => group.Name));
        Assert.Equal(["tdd", "uat", "prod"], groups.Environments.Select(group => group.Environment!.Name));
        Assert.Equal(["argocd", "Envoy-gateway", "kube-system"], groups.Platform.Select(group => group.Name));
        Assert.All(groups.Platform, group => Assert.Null(group.Environment));
    }

    [Fact]
    public void AnEnvironmentWhoseNamespaceTheStatusDoesNotListIsShownAsNotFound()
    {
        var groups = ClusterGroups.Of(ClusterFixture.SampleStatus, Environments, Generated);

        Assert.Equal([true, false, true], groups.Environments.Select(group => group.Found));
        Assert.Empty(groups.Environments[1].Pods);
        Assert.Equal("argocd", Assert.Single(groups.Platform).Name);
    }

    [Fact]
    public void AnEnvironmentWithoutANamespaceHasNoGroupAndWithoutAnyEveryNamespaceIsPlatform()
    {
        var groups = ClusterGroups.Of(ClusterFixture.SampleStatus, [ClusterFixture.Environment("tdd", null)], Generated);

        Assert.Empty(groups.Environments);
        Assert.Equal(["argocd", "cmdemo3-prod", "cmdemo3-tdd"], groups.Platform.Select(group => group.Name));
    }

    [Fact]
    public void ThePodsThatAreNotReadyAndNotFinishedComeFirstAndTheFinishedLast()
    {
        var status = ClusterFixture.Status(
            null,
            ClusterFixture.Space(
                "apps",
                ClusterFixture.Pod("web"),
                ClusterFixture.Pod("backup", ClusterPod.Succeeded, false, reason: "Completed"),
                ClusterFixture.Pod("new", ready: false, age: TimeSpan.FromSeconds(30)),
                ClusterFixture.Pod("api", name: "api-b"),
                ClusterFixture.Pod("api", name: "api-a"),
                ClusterFixture.Pod("broken", ready: false, restarts: 4, reason: "CrashLoopBackOff"),
                ClusterFixture.Pod("Cache")));

        var group = Assert.Single(ClusterGroups.Of(status, [], Generated).Platform);

        Assert.Equal(["broken-0", "new-0", "api-a", "api-b", "Cache-0", "web-0", "backup-0"], group.Pods.Select(row => row.Pod.Name));
        Assert.Equal(
            [PodState.Unhealthy, PodState.Starting, PodState.Ready, PodState.Ready, PodState.Ready, PodState.Ready, PodState.Finished],
            group.Pods.Select(row => row.State));
        Assert.Equal((4, 6, 1, 1, 4), (group.Ready, group.Total, group.Finished, group.Unhealthy, group.Restarts));
    }

    [Fact]
    public void TheSummaryOfANamespaceCountsTheFinishedOnTheirOwn()
    {
        var groups = ClusterGroups.Of(ClusterFixture.SampleStatus, Environments, Generated);
        var (tdd, prod) = (groups.Environments[0], groups.Environments[2]);

        Assert.Equal("3 of 3 ready · 1 finished · 1 restart · CPU 53 m · memory 1.5 GiB", ClusterText.Summary(prod));
        Assert.Equal("0 of 1 ready · 7 restarts · CPU 0 m · memory 20 MiB", ClusterText.Summary(tdd));
        Assert.Equal(["db-backup-29330400-7kx2p"], prod.Pods.Where(row => row.State == PodState.Finished).Select(row => row.Pod.Name));
        Assert.Equal("data-db-0 8 GiB, Bound", ClusterText.Volume(Assert.Single(prod.Volumes)));
    }

    [Fact]
    public void ANamespaceWhosePodsReportNoUsageHasNoSumOfIt()
    {
        var space = ClusterFixture.Space("jobs", ClusterFixture.Pod("backup", ClusterPod.Succeeded, false) with { Cpu = ResourceUse.None, Memory = ResourceUse.None });

        var group = Assert.Single(ClusterGroups.Of(ClusterFixture.Status(null, space), [], Generated).Platform);

        Assert.Null(group.CpuUsage);
        Assert.Null(group.MemoryUsage);
        Assert.Equal("0 of 0 ready · 1 finished · 0 restarts", ClusterText.Summary(group));
    }

    [Fact]
    public void ThePlatformIsOneLineAndNeedsALookWhenOneOfItsPodsIsUnhealthy()
    {
        var healthy = ClusterGroups.Of(
            ClusterFixture.Status(
                null,
                ClusterFixture.Space("argocd", ClusterFixture.Pod("server"), ClusterFixture.Pod("repo")),
                ClusterFixture.Space("kube-system", ClusterFixture.Pod("coredns"), ClusterFixture.Pod("job", ClusterPod.Succeeded, false)),
                ClusterFixture.Space("cmdemo3-tdd", ClusterFixture.Pod("ui", ready: false, restarts: 7, reason: "CrashLoopBackOff"))),
            Environments,
            Generated);
        var broken = ClusterGroups.Of(
            ClusterFixture.Status(null, ClusterFixture.Space("argocd", ClusterFixture.Pod("server", ready: false))),
            Environments,
            Generated);
        var starting = ClusterGroups.Of(
            ClusterFixture.Status(null, ClusterFixture.Space("argocd", ClusterFixture.Pod("server", ready: false, age: TimeSpan.FromSeconds(10)))),
            Environments,
            Generated);

        Assert.Equal("Platform: 2 namespaces, 3 of 3 pods ready", ClusterText.PlatformSummary(healthy));
        Assert.False(healthy.PlatformNeedsALook);
        Assert.Equal("Platform: 1 namespace, 0 of 1 pods ready", ClusterText.PlatformSummary(broken));
        Assert.True(broken.PlatformNeedsALook);
        Assert.False(starting.PlatformNeedsALook);
    }

    [Fact]
    public void TheTotalsOfTheSampleAreItsNodesAndItsPods()
    {
        var totals = ClusterTotals.Of(ClusterFixture.SampleStatus, Generated);

        Assert.Equal((1, 1), (totals.NodesReady, totals.Nodes));
        Assert.Equal((4, 5, 1), (totals.PodsReady, totals.Pods, totals.PodsFinished));
        Assert.Equal((63, 110, 8), (totals.PodsOnNodes, totals.PodCapacity, totals.Restarts));
        Assert.Equal("0.81 of 3.86 cores", ClusterText.NodeCpu(totals.Cpu));
        Assert.Equal("9.2 of 12.5 GiB", ClusterText.NodeMemory(totals.Memory));
        Assert.Equal("63 % requested", ClusterText.Requested(totals.Cpu));
        Assert.Equal("46 % requested", ClusterText.Requested(totals.Memory));
    }

    [Fact]
    public void TheTotalsAddUpTheNodesAndLeaveOutWhatANodeDoesNotReport()
    {
        var status = ClusterFixture.Status(
            [
                ClusterFixture.Node("aks-a-000001"),
                ClusterFixture.Node("aks-a-000002", false) with { Cpu = new ResourceUse(null, 1000, null, 3860), Pods = null },
            ],
            ClusterFixture.Space("apps", ClusterFixture.Pod("ui")));

        var totals = ClusterTotals.Of(status, Generated);

        Assert.Equal((1, 2), (totals.NodesReady, totals.Nodes));
        Assert.Equal(new ResourceUse(812, 3450, 9100, 7720), totals.Cpu);
        Assert.Equal((63, 220), (totals.PodsOnNodes, totals.PodCapacity));
        Assert.Equal(new ClusterSample(812, 2 * 9876543210d), ClusterSample.Of(status));
    }

    [Fact]
    public void ACusterWithoutAnyMeasurementHasNoSample()
    {
        var status = ClusterFixture.Status([ClusterFixture.Node() with { Cpu = ResourceUse.None, Memory = ResourceUse.None }]);

        Assert.Equal(new ClusterSample(null, null), ClusterSample.Of(status));
        Assert.Null(ClusterText.NodeCpu(ClusterTotals.Of(status, Generated).Cpu));
    }
}
