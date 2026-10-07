using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Dashboard.Tests;

/// <summary>
/// The runtime view's files of this system, <c>deploy/runtime/</c>, as <c>scripts/write-runtime.ps1</c> rendered them
/// and the build publishes them: they follow the contract the page loads, they match the topology the page loads next
/// to them, and they are what the script wrote from the current sources.
/// </summary>
public class DeployedRuntimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 20, 15, 0, TimeSpan.Zero);
    private static readonly string[] Environments = ["tdd", "uat", "prod"];

    /// <summary>The repository's root: the folder of <c>Dashboard.sln</c> above the tests' output.</summary>
    private static readonly string Root = FindRoot();

    private static readonly Topology Deployed = TopologyParser.Parse(Read("deploy/topology.json")).Topology!;

    private static string FindRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "Dashboard.sln")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException($"No Dashboard.sln above {AppContext.BaseDirectory}.");
    }

    private static string PathOf(string file) => Path.Combine(Root, file.Replace('/', Path.DirectorySeparatorChar));

    private static string Read(string file) => File.ReadAllText(PathOf(file));

    private static RuntimeManifest Manifest(string environment) =>
        RuntimeManifestParser.ParseManifest(Read($"deploy/runtime/{environment}.json")).Value!;

    private static XDocument Svg(string environment) => XDocument.Parse(Read($"deploy/runtime/{environment}.svg"));

    private static EnvironmentStatus Status(string environment) =>
        new(Deployed.Environments.Single(info => info.Name == environment));

    /// <summary>
    /// The words PlantUML drew, in its order, one space apart: it writes a text element per line or per word, and the
    /// spaces between words as no-break spaces, some in elements of their own.
    /// </summary>
    private static string Words(XDocument svg) =>
        Regex.Replace(
            string.Join(' ', svg.Descendants().Where(element => element.Name.LocalName == "text").Select(element => element.Value)),
            @"[\s\u00a0]+",
            " ");

    private static XElement? Group(XDocument svg, string kind, string? qualifiedName) =>
        svg.Descendants().SingleOrDefault(element =>
            element.Name.LocalName == "g"
            && (string?)element.Attribute("class") == kind
            && (string?)element.Attribute("data-qualified-name") == qualifiedName);

    [Fact]
    public void TheIndexListsTheEnvironmentsOfTheDeployedTopologyInItsOrder()
    {
        var index = RuntimeManifestParser.ParseIndex(Read("deploy/runtime/index.json")).Value!;

        Assert.Equal(Deployed.Environments.Select(environment => environment.Name), index.Environments.Select(entry => entry.Name));
        Assert.Equal(
            [new RuntimeIndexEntry("tdd", "tdd.json", "tdd.svg"), new RuntimeIndexEntry("uat", "uat.json", "uat.svg"), new RuntimeIndexEntry("prod", "prod.json", "prod.svg")],
            index.Environments);
        Assert.Equal("1.2026.8", index.PlantUml);
        Assert.NotNull(index.Generated);
    }

    [Fact]
    public void EveryAliasOfAManifestIsInItsSvgWithItsSlot()
    {
        foreach (var environment in Environments)
        {
            var manifest = Manifest(environment);
            var svg = Svg(environment);

            Assert.Equal(environment, manifest.Environment);
            Assert.All(manifest.Nodes, node =>
            {
                var group = Group(svg, "entity", node.QualifiedName);
                Assert.True(group is not null, $"{environment}.svg has no node {node.QualifiedName}");
                Assert.Equal(node.Alias, node.QualifiedName!.Split('.')[^1]);

                // The page draws a tile into every node but the person: its slot is the image PlantUML laid out.
                Assert.Equal(node.Kind != RuntimeNodeKind.Person, group.Elements().Any(element => element.Name.LocalName == "image"));
            });
            Assert.All(manifest.Regions, region =>
            {
                var group = Group(svg, "cluster", region.QualifiedName);
                Assert.True(group is not null, $"{environment}.svg has no frame {region.QualifiedName}");
                Assert.Contains(group.Elements(), element => element.Name.LocalName == "image");
            });
        }
    }

    [Fact]
    public void EveryRelationshipOfAManifestIsDrawnBetweenItsTwoNodes()
    {
        foreach (var environment in Environments)
        {
            var manifest = Manifest(environment);
            var svg = Svg(environment);
            string Id(string alias) => (string)Group(svg, "entity", manifest.Nodes.Single(node => node.Alias == alias).QualifiedName)!.Attribute("id")!;

            Assert.All(manifest.Edges, edge =>
            {
                Assert.Equal($"{edge.From}-to-{edge.To}", edge.Id);
                Assert.Contains(svg.Descendants(), element =>
                    (string?)element.Attribute("class") == "link"
                    && (string?)element.Attribute("data-entity-1") == Id(edge.From)
                    && (string?)element.Attribute("data-entity-2") == Id(edge.To));
            });
        }
    }

    [Fact]
    public void AManifestNamesWhatThisSystemRunsAndNoKindThePageDoesNotKnow()
    {
        foreach (var environment in Environments)
        {
            var manifest = Manifest(environment);

            Assert.Equal(
                [("browser", RuntimeNodeKind.Person), ("gateway", RuntimeNodeKind.Gateway), ("web", RuntimeNodeKind.WebApp),
                 ("worker", RuntimeNodeKind.Workload), ("db", RuntimeNodeKind.Sql), ("argocd", RuntimeNodeKind.Workload)],
                manifest.Nodes.Select(node => (node.Alias, node.Kind)));
            Assert.Equal(
                [("browser-to-gateway", RuntimeEdgeKind.Public), ("gateway-to-web", RuntimeEdgeKind.Route), ("web-to-db", RuntimeEdgeKind.Sql)],
                manifest.Edges.Select(edge => (edge.Id, edge.Kind)));
            Assert.Equal(["aks", "ns_app"], manifest.Regions.Select(region => region.Alias));

            // The cluster's frame is the cluster the topology names for the environment, and everything but the
            // browser is drawn inside it: that is what is asleep while the cluster is stopped.
            var cluster = manifest.Regions.Single(region => region.IsCluster);
            Assert.Equal(Deployed.ClusterOf(environment)!.Name, cluster.Name);
            Assert.Equal($"workorders-{environment}", manifest.Regions.Single(region => region.Alias == "ns_app").Name);
            Assert.All(manifest.Nodes.Where(node => node.Kind != RuntimeNodeKind.Person), node => Assert.Same(cluster, manifest.ClusterOf(node.QualifiedName)));
            Assert.Null(manifest.ClusterOf("browser"));
        }
    }

    [Fact]
    public void TheWebAppOfAManifestIsTheNodeTheTopologyChecks()
    {
        foreach (var environment in Environments)
        {
            var status = Status(environment);
            var node = Assert.Single(Assert.Single(status.Deployables).Nodes);
            var web = Manifest(environment).Nodes.Single(candidate => candidate.Kind == RuntimeNodeKind.WebApp);

            Assert.Equal(node.Url, web.Url);
            Assert.Equal("workorders", web.Deployable);
            Assert.Equal("ui-server", web.Name);
            Assert.Equal("ns_app", web.RegionAlias);

            // So the page finds its checks: before the first one the tile is being checked, not "not in topology.json".
            var payload = RuntimePayloadBuilder.Build(Manifest(environment), status, null, TimeZoneInfo.Utc);
            Assert.Equal("Checking", payload.Nodes.Single(tile => tile.Alias == "web").Label);
            Assert.Equal("checking", payload.Regions.Single(region => region.Alias == "ns_app").State);
        }
    }

    [Theory]
    [InlineData("tdd", "aks-platform-nonprod", "rg-platform-nonprod-aks", "rg-platform-nonprod-data", "argocd-nonprod", "workorders-tdd.20-114-71-237.sslip.io", "1 replica: NServiceBus", "8 GiB")]
    [InlineData("uat", "aks-platform-nonprod", "rg-platform-nonprod-aks", "rg-platform-nonprod-data", "argocd-nonprod", "workorders-uat.20-114-71-237.sslip.io", "0 replicas: NServiceBus", "8 GiB")]
    [InlineData("prod", "aks-platform-prod", "rg-platform-prod-aks", "rg-platform-prod-data", "argocd-prod", "workorders-prod.20-225-152-33.sslip.io", "0 replicas: NServiceBus", "32 GiB")]
    public void ADiagramDrawsThisSystemsElementsByTheirNames(
        string environment, string cluster, string group, string dataGroup, string argocd, string host, string worker, string disk)
    {
        var words = Words(Svg(environment));

        Assert.All(
            (string[])["Browser", "Azure subscription", group, cluster, "AKS cluster, southcentralus", "platform-ingress", "platform-gateway", "Envoy Gateway",
                $"workorders-{environment}", "ui-server", "worker", worker, "db", "SQL Server 2022 Express", argocd, dataGroup, $"disk-workorders-{environment}-db", disk,
                host, "HTTPRoute ui-server", "TCP 1433", "Octopus Deploy", "basic-environment-octopus-codefresh", $"envs/{environment}/app/kustomization.yaml"],
            expected => Assert.Contains(expected, words, StringComparison.Ordinal));

        // Nothing of the kit's App Service stack, which this system does not have.
        Assert.All(
            (string[])["Front Door", "App Service", "Static Web App", "Azure SQL", "standby", "cmdemo"],
            absent => Assert.DoesNotContain(absent, words, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheDiagramsWereRenderedByThePinnedPlantUml()
    {
        foreach (var environment in Environments)
        {
            Assert.Contains("<?plantuml 1.2026.8?>", Read($"deploy/runtime/{environment}.svg"), StringComparison.Ordinal);
            Assert.Contains("!pragma layout smetana", Read($"deploy/runtime/{environment}.puml"), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AHealthyNodeServesAndProvesTheGatewayAndTheDatabase()
    {
        var prod = Status("prod");
        prod.Deployables[0].Nodes[0].Record(new ProbeResult(HealthState.Healthy, 200, 87, Now, "2.5.772"));
        prod.Deployables[0].RecordPin(PinnedVersions.ParseKustomization("images:\n  - name: ui-server\n    newTag: \"2.5.772\"\n", "workorders"));

        var payload = RuntimePayloadBuilder.Build(Manifest("prod"), prod, null, TimeZoneInfo.Utc, null, Now);

        var web = payload.Nodes.Single(tile => tile.Alias == "web");
        Assert.Equal(("healthy", "Healthy", "HTTP 200 · 87 ms"), (web.State, web.Label, web.Facts));
        Assert.Equal(["version 2.5.772", "pinned 2.5.772: in sync", "serves traffic"], web.Lines.Select(line => line.Text));
        Assert.Equal(["healthy"], web.History!);
        Assert.Equal(new RuntimeRegionMark("ns_app", "serving", "serving traffic"), payload.Regions.Single(region => region.Alias == "ns_app"));
        Assert.Equal(new RuntimeRegionMark("aks", "neutral", string.Empty), payload.Regions.Single(region => region.Alias == "aks"));
        Assert.Equal(["Reachable", "Healthy", "Not probed", "Reachable", "Not probed"], payload.Nodes.Select(tile => tile.Label));
        Assert.Equal("check of ui-server passed through it", payload.Nodes.Single(tile => tile.Alias == "gateway").Lines.Single().Text);
        Assert.Equal("health check of ui-server passed", payload.Nodes.Single(tile => tile.Alias == "db").Lines.Single().Text);
        Assert.Equal(["active", "active", "active"], payload.Edges.Select(edge => edge.State));
    }

    [Fact]
    public void WhileTheClusterIsStoppedTheWholeDiagramIsAsleep()
    {
        var tdd = Status("tdd");
        tdd.Deployables[0].Nodes[0].Record(new ProbeResult(HealthState.Unreachable, null, null, Now, null, "No answer within 10 s."));
        var sleep = new ClusterSleep("aks-platform-nonprod", Now.AddMinutes(-5));

        var payload = RuntimePayloadBuilder.Build(Manifest("tdd"), tdd, null, TimeZoneInfo.Utc, sleep, Now);

        Assert.All(payload.Nodes, tile => Assert.Equal(("asleep", "Asleep"), (tile.State, tile.Label)));
        Assert.Equal(
            [new RuntimeRegionMark("aks", "asleep", "Asleep: cluster aks-platform-nonprod is stopped"), new RuntimeRegionMark("ns_app", "asleep", "asleep")],
            payload.Regions);
        Assert.All(payload.Edges, edge => Assert.Equal("asleep", edge.State));

        // The same node without the facts from Azure is a failure, as in the health view.
        var awake = RuntimePayloadBuilder.Build(Manifest("tdd"), tdd, null, TimeZoneInfo.Utc, null, Now);
        Assert.Equal("unreachable", awake.Nodes.Single(tile => tile.Alias == "web").State);
        Assert.Equal("down", awake.Regions.Single(region => region.Alias == "ns_app").State);
    }

    /// <summary>
    /// The guard against a stale diagram: <c>deploy/runtime.sha256</c> lists the SHA-256 of the sources and of every
    /// rendered file, as <c>scripts/write-runtime.ps1</c> wrote them. A source that changed without a new render, a
    /// rendered file that was edited, added or removed by hand: each fails here.
    /// </summary>
    [Fact]
    public void TheRenderedFilesAreWhatTheScriptWroteFromTheCurrentSources()
    {
        const string Remedy = "Render again and commit the result: pwsh scripts/write-runtime.ps1";
        var recorded = File.ReadAllLines(PathOf("deploy/runtime.sha256"))
            .Select(line => Regex.Match(line, "^([0-9a-f]{64})  (.+)$"))
            .Where(match => match.Success)
            .ToDictionary(match => match.Groups[2].Value, match => match.Groups[1].Value, StringComparer.Ordinal);
        var rendered = Directory.GetFiles(PathOf("deploy/runtime")).Select(file => $"deploy/runtime/{Path.GetFileName(file)}").ToList();
        string[] sources = ["deploy/topology.json", "deploy/system.json", "scripts/write-runtime.ps1"];

        Assert.Equal(sources.Concat(rendered).Order(StringComparer.Ordinal), recorded.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(
            Environments.SelectMany(environment => new[] { $"deploy/runtime/{environment}.json", $"deploy/runtime/{environment}.puml", $"deploy/runtime/{environment}.svg" })
                .Append("deploy/runtime/index.json").Order(StringComparer.Ordinal),
            rendered.Order(StringComparer.Ordinal));
        Assert.All(recorded, entry =>
        {
            var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(PathOf(entry.Key))));
            Assert.True(actual == entry.Value, $"{entry.Key} changed since deploy/runtime/ was rendered. {Remedy}");
        });
    }

    /// <summary>The build publishes these files as the site's <c>runtime/</c>, in place of the sample's.</summary>
    [Fact]
    public void TheBuildPublishesTheRenderedFilesInPlaceOfTheSample()
    {
        var workflow = Read(".github/workflows/build.yml");

        var remove = workflow.IndexOf("rm -rf publish/wwwroot/runtime", StringComparison.Ordinal);
        var copy = workflow.IndexOf("cp -r deploy/runtime publish/wwwroot/runtime", StringComparison.Ordinal);
        Assert.True(remove >= 0 && copy > remove, "build.yml must remove the sample's runtime/ and then copy deploy/runtime into the site.");
    }
}
