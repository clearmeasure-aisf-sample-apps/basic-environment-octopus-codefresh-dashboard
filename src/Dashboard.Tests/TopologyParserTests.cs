namespace Dashboard.Tests;

public class TopologyParserTests
{
    private static Topology Valid(string json)
    {
        var result = TopologyParser.Parse(json);
        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
        Assert.Empty(result.Errors);
        return result.Topology!;
    }

    private static IReadOnlyList<string> Invalid(string? json)
    {
        var result = TopologyParser.Parse(json);
        Assert.False(result.IsValid);
        Assert.Null(result.Topology);
        Assert.NotEmpty(result.Errors);
        return result.Errors;
    }

    [Fact]
    public void TheSampleShippedWithTheAppIsRead()
    {
        var topology = Valid(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "topology.sample.json")));

        Assert.Equal(
            new SystemInfo(
                "cmdemo2",
                "CM demo 2 multi-region",
                new Uri("https://github.com/example-org/cmdemo2-system"),
                new Uri("https://raw.githubusercontent.com/example-org/cmdemo2-system/status/delivery.json")),
            topology.System);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 22, 0, 0, TimeSpan.Zero), topology.Generated);
        Assert.Equal(["tdd", "uat"], topology.Environments.Select(environment => environment.Name));
        Assert.All(topology.Environments, environment => Assert.Equal("nonprod", environment.Tier));

        var tdd = Assert.Single(topology.Environments[0].Deployables);
        Assert.Equal("ui", tdd.Name);
        Assert.Equal(new Uri("https://cmdemo2-tdd-abc123.z01.azurefd.net"), tdd.FrontDoor);
        Assert.Equal("/_healthcheck", tdd.HealthPath);
        Assert.Equal("/alive", tdd.AlivePath);
        Assert.Equal("/_version", tdd.VersionPath);
        Assert.Equal(
            new NodeInfo("app-cmdemo2-tdd-ui", "westus3", "primary", new Uri("https://app-cmdemo2-tdd-ui.azurewebsites.net")),
            Assert.Single(tdd.Nodes) with { Links = null });

        var uat = Assert.Single(topology.Environments[1].Deployables);
        Assert.Equal(["westus3", "eastus2"], uat.Nodes.Select(node => node.Region));
        Assert.Equal(["primary", "standby"], uat.Nodes.Select(node => node.Role));
        Assert.True(uat.Nodes[0].IsPrimary);
        Assert.False(uat.Nodes[1].IsPrimary);

        // Where the versions pinned in Git are read, and the links into GitHub and Octopus Deploy.
        Assert.Equal(
            [
                "https://raw.githubusercontent.com/example-org/cmdemo2-system/main/environments/tdd/versions.json",
                "https://raw.githubusercontent.com/example-org/cmdemo2-system/main/environments/uat/versions.json",
            ],
            topology.Environments.Select(environment => environment.VersionsUrl?.AbsoluteUri));
        Assert.Equal(
            [
                "https://github.com/example-org/cmdemo2-system/commits/main/environments/tdd/versions.json",
                "https://github.com/example-org/cmdemo2-system/commits/main/environments/uat/versions.json",
            ],
            topology.Environments.Select(environment => environment.VersionsHistoryUrl?.AbsoluteUri));
        Assert.Equal("https://example.octopus.app/app#/Spaces-1/projects/cmdemo2-ui", tdd.ProjectUrl?.AbsoluteUri);
        Assert.Equal(tdd.ProjectUrl, uat.ProjectUrl);
    }

    [Theory]
    [InlineData("""{ "environments": [ { "name": "tdd", "deployables": [ { "name": "ui", "nodes": [] } ] } ] }""")]
    [InlineData("""
        { "system": { "slug": "demo", "repository": null },
          "environments": [ { "name": "tdd", "versionsUrl": null, "versionsHistoryUrl": null,
            "deployables": [ { "name": "ui", "projectUrl": null, "nodes": [] } ] } ] }
        """)]
    public void TheVersionAndLinkAddressesMayBeAbsentOrNull(string json)
    {
        var topology = Valid(json);

        Assert.Null(topology.System.Repository);
        Assert.Null(topology.Environments[0].VersionsUrl);
        Assert.Null(topology.Environments[0].VersionsHistoryUrl);
        Assert.Null(topology.Environments[0].Deployables[0].ProjectUrl);
    }

    [Fact]
    public void TheOctopusProjectAddressKeepsItsFragment()
    {
        var deployable = Valid("""
            { "environments": [ { "name": "tdd", "deployables": [
              { "name": "ui", "projectUrl": " https://octopus.example.net/app#/Spaces-42/projects/demo-ui " } ] } ] }
            """).Environments[0].Deployables[0];

        Assert.Equal("https://octopus.example.net/app#/Spaces-42/projects/demo-ui", deployable.ProjectUrl?.AbsoluteUri);
    }

    [Fact]
    public void AVersionOrLinkAddressThatIsNotAnAddressIsAnErrorWithItsPlace()
    {
        var errors = Invalid("""
            { "system": { "slug": "demo", "repository": "example-org/demo-system" },
              "environments": [
                { "name": "tdd", "versionsUrl": "environments/tdd/versions.json", "versionsHistoryUrl": 7,
                  "deployables": [ { "name": "ui", "projectUrl": "javascript:alert(1)" } ] } ] }
            """);

        Assert.Equal(
            [
                "system.repository: not an absolute http or https address.",
                "environments[0].versionsUrl: not an absolute http or https address.",
                "environments[0].versionsHistoryUrl: not an absolute http or https address.",
                "environments[0].deployables[0].projectUrl: not an absolute http or https address.",
            ],
            errors);
    }

    [Fact]
    public void ADeployableMayNameItsOwnPinAndItsHistory()
    {
        var deployable = Valid("""
            { "environments": [ { "name": "uat", "deployables": [
              { "name": "ui", "frontDoor": null,
                "pinUrl": " https://raw.example.net/org/demo-system/main/gitops/environments/uat/ui/kustomization.yaml ",
                "pinHistoryUrl": "https://github.example.net/org/demo-system/commits/main/gitops/environments/uat/ui/kustomization.yaml",
                "nodes": [ { "url": "https://ui.uat.example.net", "role": "primary" } ] } ] } ] }
            """).Environments[0].Deployables[0];

        Assert.Equal("https://raw.example.net/org/demo-system/main/gitops/environments/uat/ui/kustomization.yaml", deployable.PinUrl?.AbsoluteUri);
        Assert.Equal(
            "https://github.example.net/org/demo-system/commits/main/gitops/environments/uat/ui/kustomization.yaml",
            deployable.PinHistoryUrl?.AbsoluteUri);
        Assert.Equal("kustomization.yaml", deployable.PinFile);
    }

    [Theory]
    [InlineData("""{ "name": "ui", "nodes": [] }""")]
    [InlineData("""{ "name": "ui", "pinUrl": null, "pinHistoryUrl": null, "nodes": [] }""")]
    public void ThePinAddressesOfADeployableMayBeAbsentOrNull(string deployable)
    {
        var read = Valid($$"""{ "environments": [ { "name": "tdd", "deployables": [ {{deployable}} ] } ] }""").Environments[0].Deployables[0];

        Assert.Null(read.PinUrl);
        Assert.Null(read.PinHistoryUrl);
        Assert.Equal("versions.json", read.PinFile);
    }

    [Fact]
    public void APinAddressThatIsNotAnAddressIsAnErrorWithItsPlace()
    {
        var errors = Invalid("""
            { "environments": [ { "name": "tdd", "deployables": [
              { "name": "ui", "pinUrl": "gitops/environments/tdd/ui/kustomization.yaml", "pinHistoryUrl": "ftp://example.net/history" },
              { "name": "api", "pinUrl": 7, "pinHistoryUrl": "" } ] } ] }
            """);

        Assert.Equal(
            [
                "environments[0].deployables[0].pinUrl: not an absolute http or https address.",
                "environments[0].deployables[0].pinHistoryUrl: not an absolute http or https address.",
                "environments[0].deployables[1].pinUrl: not an absolute http or https address.",
                "environments[0].deployables[1].pinHistoryUrl: not an absolute http or https address.",
            ],
            errors);
    }

    [Fact]
    public void ATopologyKnowsWhetherAnyDeployableHasAFrontDoor()
    {
        const string Node = """ "nodes": [ { "url": "https://a.example.net" } ] """;

        Assert.True(Valid(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "topology.sample.json"))).HasFrontDoor);
        Assert.True(Valid($$"""
            { "environments": [ { "name": "tdd", "deployables": [ { {{Node}} } ] },
                                { "name": "uat", "deployables": [ { "frontDoor": "https://fd.example.net", {{Node}} } ] } ] }
            """).HasFrontDoor);
        Assert.False(Valid($$"""{ "environments": [ { "name": "tdd", "deployables": [ { "frontDoor": null, {{Node}} } ] } ] }""").HasFrontDoor);
        Assert.False(Valid("""{ "environments": [] }""").HasFrontDoor);
    }

    [Fact]
    public void TheProbeDecidesThePath()
    {
        var deployable = Valid("""
            { "environments": [ { "name": "tdd", "deployables": [
              { "healthPath": "/health", "alivePath": "/live", "nodes": [ { "url": "https://a.example.net" } ] } ] } ] }
            """).Environments[0].Deployables[0];

        Assert.Equal("/health", deployable.PathFor(ProbeKind.Health));
        Assert.Equal("/live", deployable.PathFor(ProbeKind.Liveness));
    }

    [Theory]
    [InlineData("""{ "name": "ui", "frontDoor": null, "nodes": [] }""")]
    [InlineData("""{ "name": "ui", "nodes": [] }""")]
    public void FrontDoorMayBeNullOrAbsent(string deployable)
    {
        var topology = Valid($$"""{ "environments": [ { "name": "tdd", "deployables": [ {{deployable}} ] } ] }""");

        Assert.Null(topology.Environments[0].Deployables[0].FrontDoor);
    }

    [Fact]
    public void MissingOptionalFieldsGetDefaults()
    {
        var topology = Valid("""
            { "environments": [ { "name": "tdd", "deployables": [ { "nodes": [
              { "url": "https://one.example.net" },
              { "url": "https://two.example.net/" } ] } ] } ] }
            """);

        Assert.Equal(new SystemInfo(string.Empty, "System"), topology.System);
        Assert.Null(topology.Generated);
        Assert.Null(topology.Environments[0].Tier);

        var deployable = topology.Environments[0].Deployables[0];
        Assert.Equal("app", deployable.Name);
        Assert.Equal("/_healthcheck", deployable.HealthPath);
        Assert.Equal("/alive", deployable.AlivePath);
        Assert.Equal("/_version", deployable.VersionPath);

        // Without a name the host names the node; without a role the first node is the primary.
        Assert.Equal(["one.example.net", "two.example.net"], deployable.Nodes.Select(node => node.Name));
        Assert.Equal(["primary", "standby"], deployable.Nodes.Select(node => node.Role));
        Assert.All(deployable.Nodes, node => Assert.Null(node.Region));
    }

    [Fact]
    public void UnknownFieldsAreIgnored()
    {
        var topology = Valid("""
            {
              "schema": 7, "owner": { "team": "platform" },
              "system": { "slug": "demo", "name": "Demo", "colour": "blue" },
              "environments": [ { "name": "prod", "tier": "prod", "order": 3, "deployables": [
                { "name": "ui", "sku": "P1v3", "nodes": [
                  { "url": "https://a.example.net", "role": "primary", "zone": [1, 2, 3] } ] } ] } ]
            }
            """);

        Assert.Equal("Demo", topology.System.Name);
        Assert.Equal("prod", topology.Environments[0].Tier);
    }

    [Fact]
    public void OptionalFieldsOfTheWrongTypeFallBackToTheDefault()
    {
        var topology = Valid("""
            { "system": "demo", "generated": 12, "environments": [ { "name": "tdd", "tier": 1, "deployables": [
              { "name": 5, "healthPath": true, "nodes": [ { "url": "https://a.example.net", "region": 3, "role": [] } ] } ] } ] }
            """);

        var deployable = topology.Environments[0].Deployables[0];
        Assert.Equal("System", topology.System.Name);
        Assert.Null(topology.Generated);
        Assert.Equal("app", deployable.Name);
        Assert.Equal("/_healthcheck", deployable.HealthPath);
        Assert.Equal("primary", deployable.Nodes[0].Role);
    }

    [Fact]
    public void TheSlugNamesASystemWithoutAName() =>
        Assert.Equal("demo", Valid("""{ "system": { "slug": "demo" }, "environments": [] }""").System.Name);

    [Fact]
    public void AnEnvironmentWithoutDeployablesAndADeployableWithoutNodesAreEmpty()
    {
        var topology = Valid("""{ "environments": [ { "name": "tdd" }, { "name": "uat", "deployables": [ { "name": "ui" } ] } ] }""");

        Assert.Empty(topology.Environments[0].Deployables);
        Assert.Empty(topology.Environments[1].Deployables[0].Nodes);
    }

    [Fact]
    public void APathWithoutLeadingSlashGetsOne()
    {
        var deployable = Valid("""
            { "environments": [ { "name": "tdd", "deployables": [ { "healthPath": "health", "nodes": [] } ] } ] }
            """).Environments[0].Deployables[0];

        Assert.Equal("/health", deployable.HealthPath);
    }

    [Fact]
    public void RoleIsReadWithoutRegardToCase()
    {
        var node = Valid("""
            { "environments": [ { "name": "tdd", "deployables": [ { "nodes": [
              { "url": "https://a.example.net", "role": "Standby" }, { "url": "https://b.example.net", "role": "PRIMARY" } ] } ] } ] }
            """).Environments[0].Deployables[0].Nodes;

        Assert.False(node[0].IsPrimary);
        Assert.True(node[1].IsPrimary);
    }

    [Fact]
    public void CommentsAndTrailingCommasAreAllowed() =>
        Assert.Empty(Valid("""
            {
              // written by hand
              "environments": [],
            }
            """).Environments);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyFileIsAnError(string? json) =>
        Assert.Equal("The file is empty.", Assert.Single(Invalid(json)));

    [Theory]
    [InlineData("<!DOCTYPE html><html></html>", "The file is not valid JSON (line 1, position 1).")]
    [InlineData("{ \"environments\": [\n  { \"name\": }\n] }", "The file is not valid JSON (line 2, position 13).")]
    public void MalformedJsonIsAnErrorWithThePlace(string json, string expected) =>
        Assert.Equal(expected, Assert.Single(Invalid(json)));

    [Theory]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"topology\"")]
    public void AnythingButAnObjectIsAnError(string json) =>
        Assert.Equal("The file must contain a JSON object.", Assert.Single(Invalid(json)));

    [Theory]
    [InlineData("""{ "system": { "slug": "demo" } }""")]
    [InlineData("""{ "environments": null }""")]
    [InlineData("""{ "environments": { "tdd": {} } }""")]
    public void EnvironmentsMustBeAnArray(string json) =>
        Assert.Equal("environments: missing or not an array.", Assert.Single(Invalid(json)));

    [Fact]
    public void EveryProblemIsReportedWithItsPlace()
    {
        var errors = Invalid("""
            { "environments": [
              { "tier": "nonprod" },
              "uat",
              { "name": "prod", "deployables": [
                { "frontDoor": "not an address", "nodes": [
                  { "name": "no-url" },
                  { "url": "ftp://files.example.net" },
                  { "url": "/relative" },
                  7 ] },
                { "nodes": "none" } ] },
              { "name": "dr", "deployables": {} } ] }
            """);

        Assert.Equal(
            [
                "environments[0].name: missing or empty.",
                "environments[1]: not an object.",
                "environments[2].deployables[0].frontDoor: not an absolute http or https address.",
                "environments[2].deployables[0].nodes[0].url: missing or not an absolute http or https address.",
                "environments[2].deployables[0].nodes[1].url: missing or not an absolute http or https address.",
                "environments[2].deployables[0].nodes[2].url: missing or not an absolute http or https address.",
                "environments[2].deployables[0].nodes[3]: not an object.",
                "environments[2].deployables[1].nodes: not an array.",
                "environments[3].deployables: not an array.",
            ],
            errors);
    }

    // ----- The cluster view: cluster and environments[].namespace -----

    private const string ClusterTopology = """
        {
          "environments": [
            { "name": "tdd", "namespace": "cmdemo3-tdd", "deployables": [] },
            { "name": "uat", "namespace": " cmdemo3-uat ", "deployables": [] },
            { "name": "prod", "deployables": [] }
          ],
          "cluster": {
            "name": "aks-cmdemo3",
            "statusUrl": "https://cmdemo3-cluster.20-225-155-175.sslip.io/cluster.json",
            "serviceUrl": "https://raw.githubusercontent.com/example-org/cmdemo3-system/cluster-status/aks.json",
            "links": { "portal": "https://portal.azure.com/#@tenant/resource/aks/overview",
                       "workloads": "https://portal.azure.com/#@tenant/resource/aks/workloads",
                       "futureLink": "not an address" },
            "futureField": 1
          }
        }
        """;

    [Fact]
    public void TheClusterAndTheNamespacesOfTheEnvironmentsAreRead()
    {
        var topology = Valid(ClusterTopology);

        var cluster = Assert.Single(topology.Clusters);
        Assert.Equal("aks-cmdemo3", cluster.Name);
        Assert.Equal("https://cmdemo3-cluster.20-225-155-175.sslip.io/cluster.json", cluster.StatusUrl?.AbsoluteUri);
        Assert.Equal("https://raw.githubusercontent.com/example-org/cmdemo3-system/cluster-status/aks.json", cluster.ServiceUrl?.AbsoluteUri);
        Assert.Equal("https://portal.azure.com/#@tenant/resource/aks/overview", cluster.Links![LinkSet.Portal]?.AbsoluteUri);
        Assert.Equal("https://portal.azure.com/#@tenant/resource/aks/workloads", cluster.Links[LinkSet.Workloads]?.AbsoluteUri);

        // A key of links the page does not know is ignored, whatever its value.
        Assert.Equal([LinkSet.Portal, LinkSet.Workloads], cluster.Links.Keys.Order());
        Assert.Equal(["cmdemo3-tdd", "cmdemo3-uat", null], topology.Environments.Select(environment => environment.Namespace));
    }

    [Theory]
    [InlineData("""{ "environments": [ { "name": "tdd" } ] }""")]
    [InlineData("""{ "environments": [ { "name": "tdd", "namespace": null } ], "cluster": null }""")]
    public void ATopologyWithoutAClusterHasNoneAndItsEnvironmentsNoNamespace(string json)
    {
        var topology = Valid(json);

        Assert.Empty(topology.Clusters);
        Assert.Null(Assert.Single(topology.Environments).Namespace);
    }

    [Fact]
    public void TheSampleShippedWithTheAppNamesNoCluster()
    {
        var topology = Valid(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "topology.sample.json")));

        Assert.Empty(topology.Clusters);
        Assert.All(topology.Environments, environment => Assert.Null(environment.Namespace));
    }

    [Theory]
    [InlineData("""{ "environments": [], "cluster": {} }""")]
    [InlineData("""{ "environments": [], "cluster": { "name": 5, "statusUrl": null, "serviceUrl": null, "links": null } }""")]
    [InlineData("""{ "environments": [], "cluster": { "links": "none" } }""")]
    [InlineData("""{ "environments": [], "cluster": { "links": { "portal": null } } }""")]
    public void EveryPartOfTheClusterIsOptional(string json)
    {
        Assert.Equal(new ClusterInfo(null), Assert.Single(Valid(json).Clusters));
    }

    [Theory]
    [InlineData("\"statusUrl\": \"cluster.json\"", "cluster.statusUrl: not an absolute http or https address.")]
    [InlineData("\"statusUrl\": \"/cluster.json\"", "cluster.statusUrl: not an absolute http or https address.")]
    [InlineData("\"serviceUrl\": \"ftp://example.net/aks.json\"", "cluster.serviceUrl: not an absolute http or https address.")]
    [InlineData("\"serviceUrl\": 7", "cluster.serviceUrl: not an absolute http or https address.")]
    [InlineData("\"links\": { \"portal\": \"portal.azure.com\" }", "cluster.links.portal: not an absolute http or https address.")]
    [InlineData("\"links\": { \"workloads\": false }", "cluster.links.workloads: not an absolute http or https address.")]
    public void AnAddressOfTheClusterThatIsNotOneIsAnError(string field, string error)
    {
        var errors = Invalid($$"""{ "environments": [], "cluster": { {{field}} } }""");

        Assert.Equal(error, Assert.Single(errors));
    }

    [Fact]
    public void AClusterThatIsNotAnObjectIsAnError()
    {
        Assert.Equal("cluster: not an object.", Assert.Single(Invalid("""{ "environments": [], "cluster": "aks-cmdemo3" }""")));
    }

    // ----- Several clusters: clusters, and the environments a cluster hosts -----

    private const string TwoClusters = """
        {
          "environments": [ { "name": "tdd" }, { "name": "uat" }, { "name": "prod" } ],
          "clusters": [
            { "name": "aks-platform-nonprod", "environments": [ "tdd", " uat " ],
              "serviceUrl": "https://raw.githubusercontent.com/example-org/dashboard/status/aks-platform-nonprod.json" },
            { "name": "aks-platform-prod", "environments": [ "prod" ],
              "serviceUrl": "https://raw.githubusercontent.com/example-org/dashboard/status/aks-platform-prod.json",
              "statusUrl": "https://prod-cluster.example.net/cluster.json",
              "links": { "portal": "https://portal.azure.com/#@tenant/resource/aks-prod/overview" } }
          ]
        }
        """;

    [Fact]
    public void SeveralClustersAreReadInTheOrderOfTheFileEachWithTheEnvironmentsItHosts()
    {
        var topology = Valid(TwoClusters);

        Assert.Equal(["aks-platform-nonprod", "aks-platform-prod"], topology.Clusters.Select(cluster => cluster.Name));
        Assert.Equal(["tdd", "uat"], topology.Clusters[0].Environments);
        Assert.Equal(["prod"], topology.Clusters[1].Environments);
        Assert.Equal(
            "https://raw.githubusercontent.com/example-org/dashboard/status/aks-platform-nonprod.json",
            topology.Clusters[0].ServiceUrl?.AbsoluteUri);
        Assert.Null(topology.Clusters[0].StatusUrl);
        Assert.Null(topology.Clusters[0].Links);
        Assert.Equal("https://prod-cluster.example.net/cluster.json", topology.Clusters[1].StatusUrl?.AbsoluteUri);
        Assert.Equal("https://portal.azure.com/#@tenant/resource/aks-prod/overview", topology.Clusters[1].Links![LinkSet.Portal]?.AbsoluteUri);
    }

    [Fact]
    public void AnEnvironmentsClusterIsTheOneThatNamesIt()
    {
        var topology = Valid(TwoClusters);

        Assert.Same(topology.Clusters[0], topology.ClusterOf("tdd"));
        Assert.Same(topology.Clusters[0], topology.ClusterOf("uat"));
        Assert.Same(topology.Clusters[1], topology.ClusterOf("prod"));
        Assert.Null(topology.ClusterOf("TDD"));
        Assert.Null(topology.ClusterOf("elsewhere"));
    }

    [Fact]
    public void TheSingleClusterMayNameItsEnvironmentsTooAndWithoutThemItClaimsNone()
    {
        var naming = Valid("""{ "environments": [ { "name": "tdd" }, { "name": "uat" } ], "cluster": { "name": "aks", "environments": [ "uat" ] } }""");
        var silent = Valid(ClusterTopology);

        Assert.Equal(["uat"], Assert.Single(naming.Clusters).Environments);
        Assert.Null(naming.ClusterOf("tdd"));
        Assert.Same(naming.Clusters[0], naming.ClusterOf("uat"));

        // As before the field existed: the cluster view groups by every environment, the health view knows no cluster.
        Assert.Null(Assert.Single(silent.Clusters).Environments);
        Assert.All(silent.Environments, environment => Assert.Null(silent.ClusterOf(environment.Name)));
    }

    [Theory]
    [InlineData("""{ "environments": [], "clusters": [] }""")]
    [InlineData("""{ "environments": [], "clusters": null }""")]
    [InlineData("""{ "environments": [], "cluster": null, "clusters": null }""")]
    public void NoClusterInClustersIsNoCluster(string json) => Assert.Empty(Valid(json).Clusters);

    [Theory]
    [InlineData("""{ "environments": [], "cluster": null, "clusters": [ { "name": "a" } ] }""")]
    [InlineData("""{ "environments": [], "cluster": { "name": "a" }, "clusters": null }""")]
    public void OneOfTheTwoMayBeNullNextToTheOther(string json) =>
        Assert.Equal(new ClusterInfo("a"), Assert.Single(Valid(json).Clusters));

    [Theory]
    [InlineData("""{ "environments": [], "clusters": [ {} ] }""")]
    [InlineData("""{ "environments": [], "clusters": [ { "name": null, "statusUrl": null, "serviceUrl": null, "links": null, "environments": null } ] }""")]
    public void EveryPartOfAClusterInClustersIsOptional(string json) =>
        Assert.Equal(new ClusterInfo(null), Assert.Single(Valid(json).Clusters));

    [Fact]
    public void AClusterThatHostsNoEnvironmentSaysSoWhichIsNotTheSameAsNotSaying()
    {
        var cluster = Assert.Single(Valid("""{ "environments": [ { "name": "tdd" } ], "clusters": [ { "environments": [] } ] }""").Clusters);

        Assert.Empty(cluster.Environments!);
        Assert.False(cluster.Hosts("tdd"));
        Assert.NotEqual(new ClusterInfo(null), cluster);
        Assert.Equal(new ClusterInfo(null, Environments: []), cluster);
    }

    [Fact]
    public void ClusterAndClustersTogetherAreAnError()
    {
        var errors = Invalid("""{ "environments": [], "cluster": { "name": "a" }, "clusters": [ { "name": "b" } ] }""");

        Assert.Equal("cluster and clusters: both are present; name the clusters in one of them.", Assert.Single(errors));
    }

    [Theory]
    [InlineData("\"clusters\": {}", "clusters: not an array.")]
    [InlineData("\"clusters\": \"aks\"", "clusters: not an array.")]
    [InlineData("\"clusters\": [ \"aks\" ]", "clusters[0]: not an object.")]
    [InlineData("\"clusters\": [ {}, 5 ]", "clusters[1]: not an object.")]
    [InlineData("\"clusters\": [ {}, { \"serviceUrl\": \"aks.json\" } ]", "clusters[1].serviceUrl: not an absolute http or https address.")]
    [InlineData("\"clusters\": [ { \"statusUrl\": 7 } ]", "clusters[0].statusUrl: not an absolute http or https address.")]
    [InlineData("\"clusters\": [ { \"links\": { \"portal\": \"portal.azure.com\" } } ]", "clusters[0].links.portal: not an absolute http or https address.")]
    [InlineData("\"clusters\": [ { \"environments\": \"tdd\" } ]", "clusters[0].environments: not an array.")]
    [InlineData("\"clusters\": [ { \"environments\": [ 5 ] } ]", "clusters[0].environments[0]: not the name of an environment.")]
    [InlineData("\"clusters\": [ { \"environments\": [ \"tdd\", \" \" ] } ]", "clusters[0].environments[1]: not the name of an environment.")]
    [InlineData("\"clusters\": [ { \"environments\": [ \"tdd\", \"uta\" ] } ]", "clusters[0].environments[1]: \"uta\" is not an environment of the topology.")]
    [InlineData("\"clusters\": [ { \"environments\": [ \"TDD\" ] } ]", "clusters[0].environments[0]: \"TDD\" is not an environment of the topology.")]
    [InlineData("\"clusters\": [ { \"environments\": [ \"tdd\", \"tdd\" ] } ]", "clusters[0].environments[1]: \"tdd\" is named twice.")]
    [InlineData("\"clusters\": [ { \"environments\": [ \"tdd\" ] }, { \"environments\": [ \"uat\", \"tdd\" ] } ]", "clusters[1].environments[1]: \"tdd\" is already hosted by clusters[0].")]
    [InlineData("\"cluster\": { \"environments\": [ \"prod\" ] }", "cluster.environments[0]: \"prod\" is not an environment of the topology.")]
    public void AClusterOfSeveralThatBreaksARuleIsAnErrorWithItsPlace(string field, string error)
    {
        var errors = Invalid($$"""{ "environments": [ { "name": "tdd" }, { "name": "uat" } ], {{field}} }""");

        Assert.Equal(error, Assert.Single(errors));
    }

    [Fact]
    public void TheTopologyThisRepositoryDeploysNamesItsTwoClustersWithTheirEnvironments()
    {
        var topology = Valid(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "topology.deploy.json")));

        Assert.Equal(["tdd", "uat", "prod"], topology.Environments.Select(environment => environment.Name));
        Assert.Equal(["aks-platform-nonprod", "aks-platform-prod"], topology.Clusters.Select(cluster => cluster.Name));
        Assert.Equal(["aks-platform-nonprod", "aks-platform-nonprod", "aks-platform-prod"], topology.Environments.Select(environment => topology.ClusterOf(environment.Name)?.Name));
        Assert.Equal(
            [
                "https://raw.githubusercontent.com/clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh-dashboard/status/aks-platform-nonprod.json",
                "https://raw.githubusercontent.com/clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh-dashboard/status/aks-platform-prod.json",
            ],
            topology.Clusters.Select(cluster => cluster.ServiceUrl?.AbsoluteUri));
        Assert.All(topology.Clusters, cluster =>
        {
            Assert.Null(cluster.StatusUrl);
            Assert.Null(cluster.Links);
        });
    }
}
