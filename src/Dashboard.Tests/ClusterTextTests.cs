namespace Dashboard.Tests;

public class ClusterTextTests
{
    private static readonly DateTimeOffset Now = ClusterFixture.Generated;

    [Theory]
    [InlineData(812, "0.81")]
    [InlineData(3860, "3.86")]
    [InlineData(1000, "1")]
    [InlineData(12500, "12.5")]
    [InlineData(0, "0")]
    public void MillicoresAreShownAsCores(double millicores, string cores)
    {
        Assert.Equal(cores, ClusterText.Cores(millicores));
    }

    [Fact]
    public void TheCpuOfANodeIsInCoresAndThatOfAPodInMillicoresBelowOneCore()
    {
        Assert.Equal("0.81 of 3.86 cores", ClusterText.CoresOf(812, 3860));
        Assert.Equal("0.5 of 1 core", ClusterText.CoresOf(500, 1000));
        Assert.Equal("14 m", ClusterText.Cpu(14));
        Assert.Equal("0 m", ClusterText.Cpu(0));
        Assert.Equal("1.25 cores", ClusterText.Cpu(1250));
        Assert.Equal("1 core", ClusterText.Cpu(1000));
        Assert.Equal("14 of 500 m", ClusterText.CpuOf(14, 500));
        Assert.Equal("38 m of 1 core", ClusterText.CpuOf(38, 1000));
        Assert.Equal("1.2 of 2 cores", ClusterText.CpuOf(1200, 2000));
    }

    [Theory]
    [InlineData(524288, "512 KiB")]
    [InlineData(9000000, "9 MiB")]
    [InlineData(271000000, "258 MiB")]
    [InlineData(1073741824, "1 GiB")]
    [InlineData(1310000000, "1.2 GiB")]
    [InlineData(9876543210, "9.2 GiB")]
    [InlineData(13400000000, "12.5 GiB")]
    [InlineData(8589934592, "8 GiB")]
    public void BytesAreShownInBinaryUnits(double bytes, string text)
    {
        Assert.Equal(text, ClusterText.Memory(bytes));
    }

    [Fact]
    public void MemoryAgainstALimitNamesTheUnitOnceWhereBothShareIt()
    {
        Assert.Equal("258 of 512 MiB", ClusterText.MemoryOf(271000000, 536870912));
        Assert.Equal("9.2 of 12.5 GiB", ClusterText.MemoryOf(9876543210, 13400000000));
        Assert.Equal("258 MiB of 2 GiB", ClusterText.MemoryOf(271000000, 2147483648));
    }

    [Fact]
    public void APodsUseIsAgainstItsLimitWhereItHasOneAndNullWithoutAUsage()
    {
        Assert.Equal("14 of 500 m", ClusterText.CpuUse(new ResourceUse(14, 100, 500)));
        Assert.Equal("61 m", ClusterText.CpuUse(new ResourceUse(61, 250, null)));
        Assert.Null(ClusterText.CpuUse(new ResourceUse(null, 50, 500)));
        Assert.Equal("258 of 512 MiB", ClusterText.MemoryUse(new ResourceUse(271000000, 268435456, 536870912)));
        Assert.Equal("393 MiB", ClusterText.MemoryUse(new ResourceUse(412000000, 268435456, null)));
        Assert.Null(ClusterText.MemoryUse(ResourceUse.None));
    }

    [Fact]
    public void ANodesUseIsAgainstWhatItOffers()
    {
        var node = ClusterFixture.Node();

        Assert.Equal("0.81 of 3.86 cores", ClusterText.NodeCpu(node.Cpu));
        Assert.Equal("9.2 of 12.5 GiB", ClusterText.NodeMemory(node.Memory));
        Assert.Equal("63 % requested", ClusterText.Requested(node.Cpu));
        Assert.Equal("46 % requested", ClusterText.Requested(node.Memory));
        Assert.Equal("0.81 cores", ClusterText.NodeCpu(new ResourceUse(812, null, null)));
        Assert.Null(ClusterText.NodeCpu(ResourceUse.None));
        Assert.Null(ClusterText.NodeMemory(ResourceUse.None));
        Assert.Null(ClusterText.Requested(new ResourceUse(1, 2, 3)));
    }

    [Fact]
    public void AShareIsAPercentageAndHighFromNineTenthsOn()
    {
        Assert.Equal(21, Math.Round(ClusterText.Share(812, 3860)!.Value));
        Assert.Null(ClusterText.Share(null, 3860));
        Assert.Null(ClusterText.Share(812, null));
        Assert.Null(ClusterText.Share(812, 0));
        Assert.Equal("21 %", ClusterText.Percent(21.4));
        Assert.Equal("63 %", ClusterText.Percent(63.0));
        Assert.Equal("0 %", ClusterText.Percent(0.2));
        Assert.Equal("140 %", ClusterText.Percent(140));
        Assert.False(ClusterText.IsHigh(89.9));
        Assert.True(ClusterText.IsHigh(90));
        Assert.False(ClusterText.IsHigh(null));
    }

    [Fact]
    public void AgesAreCountedToTheMomentTheFactsAreOf()
    {
        Assert.Equal("14 min", ClusterText.Age(new DateTimeOffset(2026, 10, 6, 20, 1, 12, TimeSpan.Zero), Now));
        Assert.Equal("3 d", ClusterText.Age(new DateTimeOffset(2026, 10, 3, 14, 2, 11, TimeSpan.Zero), Now));
        Assert.Equal("12 h", ClusterText.Age(new DateTimeOffset(2026, 10, 6, 8, 0, 3, TimeSpan.Zero), Now));
        Assert.Equal("40 s", ClusterText.Age(Now.AddSeconds(-40), Now));
        Assert.Equal("0 s", ClusterText.Age(Now.AddSeconds(5), Now));
        Assert.Null(ClusterText.Age(null, Now));
    }

    [Fact]
    public void CountsAndNamesAreInThePagesWords()
    {
        Assert.Equal("1 restart", ClusterText.Restarts(1));
        Assert.Equal("7 restarts", ClusterText.Restarts(7));
        Assert.Equal("0 restarts", ClusterText.Restarts(0));
        Assert.Equal("3 of 3", ClusterText.Of(3, 3));
        Assert.Equal("aks-…000000", ClusterText.ShortNode("aks-system-12148983-vmss000000"));
        Assert.Equal("node-1", ClusterText.ShortNode("node-1"));
        Assert.Equal("worker", ClusterText.ShortNode("worker"));
        Assert.Equal("memory pressure", ClusterText.Pressure("MemoryPressure"));
        Assert.Equal("disk pressure", ClusterText.Pressure("DiskPressure"));
        Assert.Equal("PID pressure", ClusterText.Pressure("PIDPressure"));
        Assert.Equal("network unavailable", ClusterText.Pressure("NetworkUnavailable"));
        Assert.Equal("SomethingNew", ClusterText.Pressure("SomethingNew"));
        Assert.Equal("data-db-0 8 GiB, Bound", ClusterText.Volume(new ClusterVolume("data-db-0", 8589934592, "Bound")));
        Assert.Equal("data", ClusterText.Volume(new ClusterVolume("data", null, null)));
    }

    [Fact]
    public void AzuresFactsSayWhenTheyAreFromAndWhenTheyAreOld()
    {
        var service = ClusterFixture.SampleService;
        var read = service.Generated!.Value;

        Assert.Equal("as of 20:10:04 (5 min ago)", ClusterText.AsOf(read, read.AddMinutes(5).AddSeconds(26), TimeZoneInfo.Utc));
        Assert.Equal("as of 20:10:04 (just now)", ClusterText.AsOf(read, read.AddSeconds(20), TimeZoneInfo.Utc));
        Assert.Null(ClusterText.OldFacts(service, read.AddMinutes(30)));
        Assert.Equal(
            "Azure's facts are 45 min old: the workflow that publishes them may not be running.",
            ClusterText.OldFacts(service, read.AddMinutes(45)));
        Assert.Equal(
            "Azure's facts are 3 h old: the workflow that publishes them may not be running.",
            ClusterText.OldFacts(service, read.AddHours(3).AddMinutes(10)));
        Assert.Null(ClusterText.OldFacts(service with { Generated = null }, read.AddDays(1)));
    }

    [Fact]
    public void ANodePoolAndTheWindowOfTheMetricsAreInWords()
    {
        Assert.Equal("1 × Standard_D4as_v6", ClusterText.PoolSize(ClusterFixture.SampleService.Pools[0]));
        Assert.Equal("3 nodes", ClusterText.PoolSize(new AksPool("user", null, 3, null, null, null, null, null)));
        Assert.Equal("Standard_D2s_v5", ClusterText.PoolSize(new AksPool("user", null, null, "Standard_D2s_v5", null, null, null, null)));
        Assert.Null(ClusterText.PoolSize(new AksPool("user", null, null, null, null, null, null, null)));
        Assert.Equal("average of the last 15 minutes", ClusterText.MetricsWindow(new AksMetrics(15, 1, 2, 3)));
        Assert.Equal("average of the last minute", ClusterText.MetricsWindow(new AksMetrics(1, 1, 2, 3)));
        Assert.Equal("average, as Azure Monitor reports it", ClusterText.MetricsWindow(new AksMetrics(null, 1, 2, 3)));
    }

    [Fact]
    public void TheLinksSayWhereTheyGoAndThatThePortalAsksForASignIn()
    {
        Assert.Equal(
            "The AKS cluster aks-cmdemo3 in the Azure portal (opens in a new tab; the Azure portal asks you to sign in)",
            ClusterText.PortalTitle("aks-cmdemo3"));
        Assert.Equal("The AKS cluster in the Azure portal" + LinkText.PortalSuffix, ClusterText.PortalTitle(null));
        Assert.Equal(
            "The workloads of aks-cmdemo3 in the Azure portal: deployments, pods and their logs" + LinkText.PortalSuffix,
            ClusterText.WorkloadsTitle("aks-cmdemo3"));
        Assert.Equal("The cluster's workloads in the Azure portal" + LinkText.PortalSuffix, ClusterText.WorkloadsTitle(null));
    }
}
