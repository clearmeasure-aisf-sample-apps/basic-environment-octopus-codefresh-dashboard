namespace Dashboard.Tests;

public class ClusterAssessmentTests
{
    private static readonly DateTimeOffset Generated = ClusterFixture.Generated;
    private static readonly DateTimeOffset Now = Generated.AddSeconds(20);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static ClusterAssessment Live(ClusterStatus status, DateTimeOffset? now = null) =>
        ClusterAssessment.Live(ClusterFixture.Read(status), null, now ?? Now, Utc);

    private static SourceReading<ClusterStatus> Silent(SourceState state = SourceState.Unavailable, string detail = "no answer within 10 s") =>
        new(state, Detail: detail);

    private static ClusterAssessment Service(AksService service) => ClusterAssessment.Service(ClusterFixture.Read(service));

    // ----- A pod -----

    [Theory]
    [InlineData(ClusterPod.Running, true, 3600, PodState.Ready)]
    [InlineData(ClusterPod.Running, true, 5, PodState.Ready)]
    [InlineData(ClusterPod.Succeeded, false, 3600, PodState.Finished)]
    [InlineData(ClusterPod.Failed, false, 5, PodState.Unhealthy)]
    [InlineData(ClusterPod.Failed, true, 5, PodState.Unhealthy)]
    [InlineData(ClusterPod.Running, false, 119, PodState.Starting)]
    [InlineData(ClusterPod.Running, false, 120, PodState.Unhealthy)]
    [InlineData(ClusterPod.Running, false, 3600, PodState.Unhealthy)]
    [InlineData(ClusterPod.Pending, false, 30, PodState.Starting)]
    [InlineData(ClusterPod.Pending, false, 300, PodState.Starting)]
    [InlineData(ClusterPod.Pending, false, 301, PodState.Unhealthy)]
    [InlineData(ClusterPod.Unknown, false, 3600, PodState.Unhealthy)]
    [InlineData(ClusterPod.Unknown, true, 3600, PodState.Ready)]
    public void APodsStateFollowsItsPhaseItsReadinessAndItsAge(string phase, bool ready, int ageSeconds, PodState expected)
    {
        var pod = ClusterFixture.Pod("ui", phase, ready, age: TimeSpan.FromSeconds(ageSeconds));

        Assert.Equal(expected, PodRules.StateOf(pod, Generated));
    }

    [Fact]
    public void APodThatDoesNotSayWhenItStartedIsStartingWhilePendingAndUnhealthyWhileRunningWithoutBeingReady()
    {
        Assert.Equal(PodState.Starting, PodRules.StateOf(ClusterFixture.Pod("ui", ClusterPod.Pending, false, started: false), Generated));
        Assert.Equal(PodState.Unhealthy, PodRules.StateOf(ClusterFixture.Pod("ui", ClusterPod.Running, false, started: false), Generated));
    }

    [Fact]
    public void APodsStateIsSaidInWords()
    {
        string Words(ClusterPod pod) => ClusterText.PodWords(pod, PodRules.StateOf(pod, Generated), Generated);

        Assert.Equal("Ready", Words(ClusterFixture.Pod("ui")));
        Assert.Equal("Finished", Words(ClusterFixture.Pod("job", ClusterPod.Succeeded, false, reason: "Completed")));
        Assert.Equal("Starting", Words(ClusterFixture.Pod("ui", ready: false, age: TimeSpan.FromSeconds(30))));
        Assert.Equal("Starting: ContainerCreating", Words(ClusterFixture.Pod("ui", ClusterPod.Pending, false, reason: "ContainerCreating", age: TimeSpan.FromSeconds(30))));
        Assert.Equal("CrashLoopBackOff", Words(ClusterFixture.Pod("ui", ready: false, restarts: 7, reason: "CrashLoopBackOff")));
        Assert.Equal("Not ready", Words(ClusterFixture.Pod("ui", ready: false)));
        Assert.Equal("Pending for 12 min", Words(ClusterFixture.Pod("ui", ClusterPod.Pending, false, age: TimeSpan.FromMinutes(12))));
        Assert.Equal("Pending for 12 min: ImagePullBackOff", Words(ClusterFixture.Pod("ui", ClusterPod.Pending, false, reason: "ImagePullBackOff", age: TimeSpan.FromMinutes(12))));
        Assert.Equal("Evicted", Words(ClusterFixture.Pod("ui", ClusterPod.Failed, false, reason: "Evicted")));
        Assert.Equal("Failed", Words(ClusterFixture.Pod("ui", ClusterPod.Failed, false)));
    }

    // ----- The cluster, live -----

    [Fact]
    public void TheClusterIsHealthyWhenEveryNodeIsReadyUnderNoPressureAndEveryPodThatIsNotFinishedIsReady()
    {
        var status = ClusterFixture.Status(
            null,
            ClusterFixture.Space("apps", ClusterFixture.Pod("ui"), ClusterFixture.Pod("backup", ClusterPod.Succeeded, false, reason: "Completed")));

        Assert.Equal(
            new ClusterAssessment(ClusterState.Healthy, "Healthy", "Every node is ready and under no pressure, and every pod is ready"),
            Live(status));
    }

    [Fact]
    public void ACordonedNodeIsStillAHealthyOne()
    {
        var status = ClusterFixture.Status([ClusterFixture.Node() with { Unschedulable = true }], ClusterFixture.Space("apps", ClusterFixture.Pod("ui")));

        Assert.Equal(ClusterState.Healthy, Live(status).State);
    }

    [Fact]
    public void TheSampleIsUnhealthyAndNamesThePodThatCrashes()
    {
        Assert.Equal(
            new ClusterAssessment(ClusterState.Unhealthy, "Unhealthy", "ui in cmdemo3-tdd: CrashLoopBackOff, 7 restarts"),
            Live(ClusterFixture.SampleStatus));
    }

    [Fact]
    public void TheHeadlineNamesWhatIsWrongMostSevereFirstAndCountsTheRestAfterThree()
    {
        var status = ClusterFixture.Status(
            [
                ClusterFixture.Node("aks-system-12148983-vmss000001", true, "MemoryPressure", "DiskPressure"),
                ClusterFixture.Node("aks-system-12148983-vmss000000", false),
            ],
            ClusterFixture.Space(
                "apps",
                ClusterFixture.Pod("slow", ClusterPod.Pending, false, age: TimeSpan.FromMinutes(12)),
                ClusterFixture.Pod("api", ready: false, restarts: 2, reason: "CrashLoopBackOff"),
                ClusterFixture.Pod("ui", ready: false, restarts: 7, reason: "CrashLoopBackOff"),
                ClusterFixture.Pod("quiet", ready: false),
                ClusterFixture.Pod("gone", ClusterPod.Failed, false, reason: "Evicted"),
                ClusterFixture.Pod("fine")));

        Assert.Equal(
            [
                "Node aks-…000000 is not ready",
                "Node aks-…000001: memory pressure, disk pressure",
                "gone in apps: Evicted",
                "ui in apps: CrashLoopBackOff, 7 restarts",
                "api in apps: CrashLoopBackOff, 2 restarts",
                "quiet in apps: not ready",
                "slow in apps: Pending for 12 min",
            ],
            ClusterAssessment.Problems(status, Generated));
        Assert.Equal(
            new ClusterAssessment(
                ClusterState.Unhealthy,
                "Unhealthy",
                "Node aks-…000000 is not ready; Node aks-…000001: memory pressure, disk pressure; gone in apps: Evicted; and 4 more"),
            Live(status));
    }

    [Fact]
    public void ThreeProblemsAreNamedWithoutACount()
    {
        Assert.Equal("a; b; c", ClusterAssessment.Named(["a", "b", "c"]));
        Assert.Equal("a; b; c; and 1 more", ClusterAssessment.Named(["a", "b", "c", "d"]));
        Assert.Equal("a", ClusterAssessment.Named(["a"]));
    }

    [Fact]
    public void AStatusWithoutANodeIsUnhealthy()
    {
        var status = ClusterFixture.Status([], ClusterFixture.Space("apps", ClusterFixture.Pod("ui")));

        Assert.Equal((ClusterState.Unhealthy, "The status file lists no node"), (Live(status).State, Live(status).Headline));
    }

    [Fact]
    public void PodsThatAreOnlyStartingMakeTheClusterStartNotUnhealthy()
    {
        var one = ClusterFixture.Status(null, ClusterFixture.Space("apps", ClusterFixture.Pod("ui", ready: false, age: TimeSpan.FromSeconds(40)), ClusterFixture.Pod("db")));
        var two = ClusterFixture.Status(
            null,
            ClusterFixture.Space("apps", ClusterFixture.Pod("ui", ready: false, age: TimeSpan.FromSeconds(40)), ClusterFixture.Pod("db", ClusterPod.Pending, false, started: false)));

        Assert.Equal(new ClusterAssessment(ClusterState.Checking, "Starting", "1 pod is starting: ui in apps"), Live(one));
        Assert.Equal(new ClusterAssessment(ClusterState.Checking, "Starting", "2 pods are starting: ui in apps; db in apps"), Live(two));
    }

    [Theory]
    [InlineData(15, 60, false)]
    [InlineData(15, 61, true)]
    [InlineData(30, 120, false)]
    [InlineData(30, 121, true)]
    [InlineData(5, 61, true)]
    [InlineData(null, 60, false)]
    [InlineData(null, 61, true)]
    public void TheStatusIsStaleAfterFourIntervalsAndAMinuteAtLeast(int? interval, int ageSeconds, bool stale)
    {
        var status = ClusterFixture.Status() with { IntervalSeconds = interval };

        Assert.Equal(stale, status.IsStale(Generated.AddSeconds(ageSeconds)));
        Assert.Equal(stale ? ClusterLiveness.Stale : ClusterLiveness.Live, ClusterAssessment.LivenessOf(ClusterFixture.Read(status), Generated.AddSeconds(ageSeconds)));
    }

    [Fact]
    public void AStatusThatDoesNotSayWhenItWasWrittenIsNotStaleAndAClockBehindTheClustersIsNoProblem()
    {
        Assert.False((ClusterFixture.Status() with { Generated = null }).IsStale(Generated.AddDays(1)));
        Assert.False(ClusterFixture.Status().IsStale(Generated.AddMinutes(-10)));
    }

    [Fact]
    public void AStaleStatusSaysWhenTheCollectorLastWroteAndWhatAzureReports()
    {
        var reading = ClusterFixture.Read(ClusterFixture.SampleStatus);
        var now = Generated.AddMinutes(12).AddSeconds(4);

        var alone = ClusterAssessment.Live(reading, null, now, Utc);
        var withAzure = ClusterAssessment.Live(reading, ClusterFixture.Read(ClusterFixture.SampleService), now, Utc);

        Assert.Equal(
            new ClusterAssessment(
                ClusterState.Unreachable,
                "Stale",
                "The collector in the cluster last wrote 20:15:30 (12 min ago)",
                "What is shown below is as of then."),
            alone);
        Assert.Equal(
            "What is shown below is as of then. Azure reports the AKS service Available and Running, as of 20:10:04 (17 min ago).",
            withAzure.Detail);
    }

    [Fact]
    public void AStatusFileThatDoesNotAnswerIsUnreachableNextToWhatAzureReports()
    {
        var azure = ClusterFixture.Read(ClusterFixture.SampleService);
        var now = ClusterFixture.SampleService.Generated!.Value.AddMinutes(5).AddSeconds(30);

        Assert.Equal(
            new ClusterAssessment(
                ClusterState.Unreachable,
                "Unreachable",
                "The cluster's status file does not answer",
                "No answer within 10 s. Azure reports the AKS service Available and Running, as of 20:10:04 (5 min ago)."),
            ClusterAssessment.Live(Silent(), azure, now, Utc));
        Assert.Equal(
            "The address answered HTTP 404. Azure reports the AKS service Available and Running, as of 20:10:04 (5 min ago).",
            ClusterAssessment.Live(Silent(SourceState.Missing, "the address answered HTTP 404"), azure, now, Utc).Detail);
    }

    [Fact]
    public void WithoutAzuresFactsAnUnreachableClusterSaysSo()
    {
        Assert.Equal("No answer within 10 s.", ClusterAssessment.Live(Silent(), null, Now, Utc).Detail);
        Assert.Equal(
            "No answer within 10 s.",
            ClusterAssessment.Live(Silent(), new SourceReading<AksService>(SourceState.Pending), Now, Utc).Detail);
        Assert.Equal(
            "No answer within 10 s. Azure's facts about the AKS service are not published yet.",
            ClusterAssessment.Live(Silent(), new SourceReading<AksService>(SourceState.Missing), Now, Utc).Detail);
        Assert.Equal(
            "No answer within 10 s. Azure's facts about the AKS service could not be read either.",
            ClusterAssessment.Live(Silent(), new SourceReading<AksService>(SourceState.Unavailable, Detail: "no answer within 10 s"), Now, Utc).Detail);
        Assert.Equal(
            "No answer within 10 s. Azure reports the AKS service Running.",
            ClusterAssessment.Live(Silent(), ClusterFixture.Read(ClusterFixture.SampleService with { Availability = null, Generated = null }), Now, Utc).Detail);
    }

    [Theory]
    [InlineData(SourceState.Missing)]
    [InlineData(SourceState.Unavailable)]
    public void AStatusFileThatDoesNotAnswerWhileAzureSaysStoppedIsTheClusterStopped(SourceState state)
    {
        var stopped = ClusterFixture.SampleService with { PowerState = "Stopped" };
        var now = stopped.Generated!.Value.AddMinutes(5).AddSeconds(30);

        Assert.Equal(
            new ClusterAssessment(
                ClusterState.Neutral,
                "Stopped",
                "The cluster is stopped",
                "The pods and the pages served from inside the cluster do not run while it is stopped. Azure reports the power state Stopped, as of 20:10:04 (5 min ago)."),
            ClusterAssessment.Live(Silent(state), ClusterFixture.Read(stopped), now, Utc));
    }

    [Fact]
    public void AStatusFileThatIsNotTheFileIsAProblemInWords()
    {
        var assessment = ClusterAssessment.Live(Silent(SourceState.Malformed, "the file is not valid JSON"), null, Now, Utc);

        Assert.Equal(
            new ClusterAssessment(ClusterState.Unhealthy, "Unreadable", "The cluster's status file could not be read", "The file is not valid JSON."),
            assessment);
        Assert.Equal(ClusterLiveness.Unreadable, ClusterAssessment.LivenessOf(Silent(SourceState.Malformed), Now));
    }

    [Fact]
    public void BeforeTheFirstReadingTheClusterIsBeingChecked()
    {
        var pending = new SourceReading<ClusterStatus>(SourceState.Pending);

        Assert.Equal(new ClusterAssessment(ClusterState.Checking, "Checking", "Reading the cluster's status file"), ClusterAssessment.Live(pending, null, Now, Utc));
        Assert.Equal(ClusterLiveness.Pending, ClusterAssessment.LivenessOf(pending, Now));
        Assert.Equal(ClusterLiveness.Silent, ClusterAssessment.LivenessOf(Silent(), Now));
        Assert.Equal(ClusterLiveness.Silent, ClusterAssessment.LivenessOf(Silent(SourceState.Missing), Now));
    }

    // ----- The AKS service, as Azure reports it -----

    [Fact]
    public void TheServiceIsHealthyWhenItIsAvailableRunningAndSucceeded()
    {
        Assert.Equal(
            new ClusterAssessment(
                ClusterState.Healthy,
                "Available",
                "Azure reports the AKS service available and running",
                "There aren't any known Azure platform problems affecting this managed cluster."),
            Service(ClusterFixture.SampleService));
    }

    [Fact]
    public void AStoppedServiceIsNeutralWhateverElseAzureSays()
    {
        var stopped = ClusterFixture.SampleService with
        {
            PowerState = "Stopped",
            Availability = new AksAvailability("Unavailable", "The cluster is stopped.", null, null),
        };

        Assert.Equal(new ClusterAssessment(ClusterState.Neutral, "Stopped", "The AKS service is stopped", "The cluster is stopped."), Service(stopped));
    }

    [Fact]
    public void AnUnavailableServiceIsUnhealthyAndADegradedOneAWarning()
    {
        var unavailable = ClusterFixture.SampleService with { Availability = new AksAvailability("Unavailable", "The API server does not answer.", null, null) };
        var degraded = ClusterFixture.SampleService with { Availability = new AksAvailability("Degraded", null, null, null) };

        Assert.Equal(
            new ClusterAssessment(ClusterState.Unhealthy, "Unavailable", "Azure reports the AKS service unavailable", "The API server does not answer."),
            Service(unavailable));
        Assert.Equal(new ClusterAssessment(ClusterState.Warning, "Degraded", "Azure reports the AKS service degraded"), Service(degraded));
    }

    [Theory]
    [InlineData("Updating")]
    [InlineData("Failed")]
    [InlineData("Starting")]
    public void AProvisioningStateOtherThanSucceededIsAWarningWithTheWords(string provisioning)
    {
        var assessment = Service(ClusterFixture.SampleService with { ProvisioningState = provisioning });

        Assert.Equal(ClusterState.Warning, assessment.State);
        Assert.Equal(provisioning, assessment.Label);
        Assert.Equal($"The provisioning state of the AKS service is {provisioning}, not Succeeded", assessment.Headline);
    }

    [Fact]
    public void AVerdictThatIsUnknownOrAbsentIsNeutralAndCalm()
    {
        var unknown = Service(ClusterFixture.SampleService with { Availability = new AksAvailability("Unknown", null, null, null) });
        var absent = Service(ClusterFixture.SampleService with { Availability = null });
        var other = Service(ClusterFixture.SampleService with { Availability = new AksAvailability("Recovering", null, null, null) });

        Assert.Equal(new ClusterAssessment(ClusterState.Neutral, "Unknown", "Azure Resource Health has no verdict on the AKS service at the moment"), unknown);
        Assert.Equal(unknown, absent);
        Assert.Equal(new ClusterAssessment(ClusterState.Neutral, "Recovering", "Azure reports the AKS service Recovering"), other);
    }

    [Fact]
    public void AnAvailableServiceThatSaysNothingElseIsHealthy()
    {
        var service = ClusterFixture.SampleService with { PowerState = null, ProvisioningState = null };

        Assert.Equal(ClusterState.Healthy, Service(service).State);
    }

    [Fact]
    public void AServiceFileThatIsNotPublishedYetIsSaidCalmly()
    {
        var assessment = ClusterAssessment.Service(new SourceReading<AksService>(SourceState.Missing, Detail: "the address answered HTTP 404"));

        Assert.Equal(
            new ClusterAssessment(
                ClusterState.Neutral,
                "Not published",
                "Azure's facts about the AKS service are not published yet",
                "A scheduled workflow publishes them about every ten minutes; until its first run the address answers HTTP 404. The page reads them again with every check."),
            assessment);
    }

    [Fact]
    public void AServiceFileThatCannotBeReadIsNeutralAndAMalformedOneAWarning()
    {
        var silent = ClusterAssessment.Service(new SourceReading<AksService>(SourceState.Unavailable, Detail: "the address answered HTTP 500"));
        var broken = ClusterAssessment.Service(new SourceReading<AksService>(SourceState.Malformed, Detail: "the file is not valid JSON"));
        var pending = ClusterAssessment.Service(new SourceReading<AksService>(SourceState.Pending));

        Assert.Equal(
            new ClusterAssessment(
                ClusterState.Neutral,
                "Not known",
                "Azure's facts about the AKS service could not be read",
                "The address answered HTTP 500. The page reads them again with every check."),
            silent);
        Assert.Equal(
            new ClusterAssessment(ClusterState.Warning, "Unreadable", "Azure's facts about the AKS service could not be read", "The file is not valid JSON."),
            broken);
        Assert.Equal(new ClusterAssessment(ClusterState.Checking, "Checking", "Reading Azure's facts about the AKS service"), pending);
    }
}
