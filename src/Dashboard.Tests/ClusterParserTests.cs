namespace Dashboard.Tests;

public class ClusterParserTests
{
    [Fact]
    public void TheSampleStatusIsRead()
    {
        var status = ClusterFixture.SampleStatus;

        Assert.Equal(ClusterFixture.Generated, status.Generated);
        Assert.Equal(15, status.IntervalSeconds);
        Assert.Equal("v1.33.3", status.KubernetesVersion);

        var node = Assert.Single(status.Nodes);
        Assert.Equal("aks-system-12148983-vmss000000", node.Name);
        Assert.True(node.Ready);
        Assert.Empty(node.Pressures);
        Assert.False(node.Unschedulable);
        Assert.Equal(("system", "Standard_D4as_v6", null, "v1.33.3"), (node.Pool, node.Size, node.Zone, node.KubeletVersion));
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 14, 2, 11, TimeSpan.Zero), node.CreatedAt);
        Assert.Equal(new ResourceUse(812, 2450, 9100, 3860), node.Cpu);
        Assert.Equal(new ResourceUse(9876543210, 6200000000, 11800000000, 13400000000), node.Memory);
        Assert.Equal((63, 110), (node.Pods, node.PodCapacity));

        Assert.Equal(["argocd", "cmdemo3-prod", "cmdemo3-tdd"], status.Namespaces.Select(space => space.Name));
        Assert.Equal(6, status.Pods.Count());
        var prod = status.Namespaces[1];
        Assert.Equal(["ui", "db", "dashboard", "db-backup"], prod.Pods.Select(pod => pod.Workload));
        Assert.Equal(new ClusterVolume("data-db-0", 8589934592, "Bound"), Assert.Single(prod.Volumes));

        var ui = status.Namespaces[2].Pods[0];
        Assert.Equal(("ui-7f6b5c4d3-m8n2p", "ui", "Deployment", "aks-system-12148983-vmss000000"), (ui.Name, ui.Workload, ui.Kind, ui.Node));
        Assert.Equal((ClusterPod.Running, false, 1, 0, 7, "CrashLoopBackOff"), (ui.Phase, ui.Ready, ui.Containers, ui.ContainersReady, ui.Restarts, ui.Reason));
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 20, 1, 12, TimeSpan.Zero), ui.StartedAt);
        Assert.Equal(new ResourceUse(0, 100, 500), ui.Cpu);
    }

    [Fact]
    public void AUsageOrALimitThatIsNullIsNoNumber()
    {
        var status = ClusterFixture.SampleStatus;
        var controller = status.Namespaces[0].Pods[0];
        var backup = status.Namespaces[1].Pods[3];

        // No limit is set.
        Assert.Equal(new ResourceUse(61, 250, null), controller.Cpu);
        Assert.Equal(new ResourceUse(412000000, 268435456, null), controller.Memory);

        // A finished pod has no usage.
        Assert.Equal((ClusterPod.Succeeded, "Completed"), (backup.Phase, backup.Reason));
        Assert.Equal(new ResourceUse(null, 50, 500), backup.Cpu);
        Assert.Equal(new ResourceUse(null, 67108864, 268435456), backup.Memory);
    }

    [Fact]
    public void EveryPartButTheNamesIsOptional()
    {
        var status = ClusterStatus.Parse("""
            { "nodes": [ { "name": "node-1" }, { "ready": true }, "text" ],
              "namespaces": [ { "name": "apps", "pods": [ { "name": "web-1", "futureField": { "a": 1 } }, { "phase": "Running" } ], "volumes": [ { "name": "data" }, {} ] },
                              { "pods": [] } ],
              "futureField": true }
            """).Value!;

        Assert.Null(status.Generated);
        Assert.Null(status.IntervalSeconds);
        Assert.Null(status.KubernetesVersion);

        // A node or a pod without a name is left out; what is missing is no number, not ready, phase Unknown.
        var node = Assert.Single(status.Nodes);
        Assert.Equal(("node-1", false, false), (node.Name, node.Ready, node.Unschedulable));
        Assert.Empty(node.Pressures);
        Assert.Equal(ResourceUse.None, node.Cpu);
        Assert.Null(node.Pods);

        var space = Assert.Single(status.Namespaces);
        var pod = Assert.Single(space.Pods);
        Assert.Equal(("web-1", null, null, ClusterPod.Unknown, false, 0, null), (pod.Name, pod.Workload, pod.Kind, pod.Phase, pod.Ready, pod.Restarts, pod.Reason));
        Assert.Equal("web-1", pod.Label);
        Assert.Null(pod.StartedAt);
        Assert.Equal(ResourceUse.None, pod.Memory);
        Assert.Equal(new ClusterVolume("data", null, null), Assert.Single(space.Volumes));
    }

    [Fact]
    public void ValuesOfAnotherTypeAreNoValuesAndNeverAnException()
    {
        var status = ClusterStatus.Parse("""
            { "generated": 5, "intervalSeconds": "15", "kubernetesVersion": null,
              "nodes": [ { "name": "node-1", "ready": "yes", "pressures": [ "DiskPressure", 3, "" ], "cpu": 4, "pods": [] } ],
              "namespaces": { "name": "apps" } }
            """).Value!;

        var node = Assert.Single(status.Nodes);
        Assert.False(node.Ready);
        Assert.Equal(["DiskPressure"], node.Pressures);
        Assert.Equal(ResourceUse.None, node.Cpu);
        Assert.Null(status.Generated);
        Assert.Null(status.IntervalSeconds);
        Assert.Empty(status.Namespaces);
    }

    [Theory]
    [InlineData(null, "the file is empty")]
    [InlineData("  ", "the file is empty")]
    [InlineData("<!DOCTYPE html><html></html>", "the file is not valid JSON")]
    [InlineData("""{ "nodes": [ """, "the file is not valid JSON")]
    [InlineData("[]", "the file does not contain a JSON object")]
    [InlineData("""{ "generated": "2026-10-06T20:15:30Z" }""", "the file has neither nodes nor namespaces")]
    [InlineData("""{ "nodes": {}, "namespaces": "none" }""", "the file has neither nodes nor namespaces")]
    public void AMalformedStatusIsAProblemInWords(string? json, string problem)
    {
        var parsed = ClusterStatus.Parse(json);

        Assert.Null(parsed.Value);
        Assert.Equal(problem, parsed.Problem);
    }

    [Fact]
    public void TheSampleServiceIsRead()
    {
        var service = ClusterFixture.SampleService;

        Assert.Equal(new DateTimeOffset(2026, 10, 6, 20, 10, 4, TimeSpan.Zero), service.Generated);
        Assert.Equal(("aks-cmdemo3", "rg-cmdemo3-cluster", "southcentralus"), (service.Name, service.ResourceGroup, service.Location));
        Assert.Equal(
            new AksAvailability("Available", "There aren't any known Azure platform problems affecting this managed cluster.", null, new DateTimeOffset(2026, 10, 3, 14, 5, 0, TimeSpan.Zero)),
            service.Availability);
        Assert.Equal(("Running", "Succeeded", "1.33.3", "Free"), (service.PowerState, service.ProvisioningState, service.KubernetesVersion, service.Tier));
        Assert.False(service.IsStopped);
        Assert.Equal(new AksPool("system", "System", 1, "Standard_D4as_v6", 128, "Running", "Succeeded", "1.33.3"), Assert.Single(service.Pools));
        Assert.Equal(new AksMetrics(15, 21.4, 63.0, 18.2, 4.6, 31.5), service.Metrics);
    }

    [Fact]
    public void EachNumberOfTheMetricsMayBeNullOrAbsent()
    {
        // The real cluster: Azure emits no disk metric, and an older file has no API server numbers.
        var real = AksService.Parse("""
            { "powerState": "Running",
              "metrics": { "windowMinutes": 15, "nodeCpuPercent": 21.4, "nodeMemoryPercent": 63, "nodeDiskPercent": null,
                           "apiServerCpuPercent": null, "apiServerMemoryPercent": 31.5 } }
            """).Value!;
        var older = AksService.Parse("""{ "powerState": "Running", "metrics": { "nodeCpuPercent": 5 } }""").Value!;
        var none = AksService.Parse("""{ "powerState": "Running", "metrics": { "windowMinutes": 15, "nodeCpuPercent": null } }""").Value!;
        var absent = AksService.Parse("""{ "powerState": "Running" }""").Value!;

        Assert.Equal(new AksMetrics(15, 21.4, 63, null, null, 31.5), real.Metrics);
        Assert.Equal(new AksMetrics(null, 5, null, null), older.Metrics);
        Assert.True(real.Metrics!.HasAny);
        Assert.False(none.Metrics!.HasAny);
        Assert.Null(absent.Metrics);
    }

    [Fact]
    public void EveryPartOfTheServiceIsOptional()
    {
        var service = AksService.Parse("""
            { "powerState": "Stopped", "availability": { "summary": "no state" }, "pools": [ { "name": "system" }, { "count": 2 } ], "futureField": 1 }
            """).Value!;

        Assert.True(service.IsStopped);
        Assert.Null(service.Availability);
        Assert.Null(service.Generated);
        Assert.Null(service.Metrics);
        Assert.Equal(new AksPool("system", null, null, null, null, null, null, null), Assert.Single(service.Pools));
    }

    [Theory]
    [InlineData("", "the file is empty")]
    [InlineData("404: Not Found", "the file is not valid JSON")]
    [InlineData("\"text\"", "the file does not contain a JSON object")]
    [InlineData("""{ "name": "aks-cmdemo3" }""", "the file says nothing about the service: no availability, no powerState and no provisioningState")]
    public void AMalformedServiceFileIsAProblemInWords(string json, string problem)
    {
        var parsed = AksService.Parse(json);

        Assert.Null(parsed.Value);
        Assert.Equal(problem, parsed.Problem);
    }
}
