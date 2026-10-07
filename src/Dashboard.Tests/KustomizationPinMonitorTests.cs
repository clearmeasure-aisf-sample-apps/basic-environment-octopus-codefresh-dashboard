using System.Net;

namespace Dashboard.Tests;

/// <summary>
/// The monitor reads the pin of a deployable that names one of its own (<c>pinUrl</c>, a Kustomize file) next to its
/// health checks, and keeps reading <c>versions.json</c> for the deployables that name none.
/// </summary>
public class KustomizationPinMonitorTests
{
    private const string PinsHost = "raw.example.net";
    private const string UiPin = "https://raw.example.net/org/demo-system/main/gitops/environments/uat/ui/kustomization.yaml";
    private const string ApiPin = "https://raw.example.net/org/demo-system/main/gitops/environments/uat/api/kustomization.yaml";
    private const string VersionsFile = "https://raw.example.net/org/demo-system/main/environments/uat/versions.json";

    /// <summary>
    /// A cluster's environment: every app is one node at one public address, pinned in its own kustomization. "job" has
    /// no pin of its own, so the environment's versions.json holds it; prod names no pin at all.
    /// </summary>
    private const string Cluster = """
        {
          "system": { "slug": "demo", "name": "Demo" },
          "environments": [
            { "name": "uat",
              "versionsUrl": "https://raw.example.net/org/demo-system/main/environments/uat/versions.json",
              "versionsHistoryUrl": "https://github.example.net/org/demo-system/commits/main/environments/uat/versions.json",
              "deployables": [
                { "name": "ui", "frontDoor": null,
                  "pinUrl": "https://raw.example.net/org/demo-system/main/gitops/environments/uat/ui/kustomization.yaml",
                  "pinHistoryUrl": "https://github.example.net/org/demo-system/commits/main/gitops/environments/uat/ui/kustomization.yaml",
                  "nodes": [ { "name": "uat-ui", "role": "primary", "url": "https://ui.uat.example.net" } ] },
                { "name": "api", "frontDoor": null,
                  "pinUrl": "https://raw.example.net/org/demo-system/main/gitops/environments/uat/api/kustomization.yaml",
                  "nodes": [ { "name": "uat-api", "role": "primary", "url": "https://api.uat.example.net" } ] },
                { "name": "job",
                  "nodes": [ { "name": "uat-job", "role": "primary", "url": "https://job.uat.example.net" } ] } ] },
            { "name": "prod",
              "deployables": [
                { "name": "ui", "frontDoor": null,
                  "pinUrl": "https://raw.example.net/org/demo-system/main/gitops/environments/prod/ui/kustomization.yaml",
                  "nodes": [ { "name": "prod-ui", "role": "primary", "url": "https://ui.prod.example.net" } ] },
                { "name": "job",
                  "nodes": [ { "name": "prod-job", "role": "primary", "url": "https://job.prod.example.net" } ] } ] }
          ]
        }
        """;

    private readonly SignallingTimeProvider _time = new();

    private DashboardMonitor Monitor(StubHandler handler) => Optics.Monitor(handler, _time, topology: Cluster);

    private static bool IsPin(HttpRequestMessage request) => request.RequestUri!.Host == PinsHost;

    /// <summary>A Kustomize file that pins the image of one deployable.</summary>
    private static string Kustomization(string version) =>
        $"apiVersion: kustomize.config.k8s.io/v1beta1\nkind: Kustomization\nresources:\n  - ../../../base\nimages:\n  - name: demo.azurecr.io/demo/app\n    newTag: \"{version}\" # pinned by the deployment\n";

    /// <summary>
    /// Every node is healthy on 2.4.15; every kustomization pins the version given for ui (api: 1.3.0), and
    /// versions.json pins job, with an entry for ui that nothing may read.
    /// </summary>
    private static HttpResponseMessage Answer(HttpRequestMessage request, string ui = "2.4.15", string job = "2.4.15")
    {
        if (!IsPin(request))
        {
            return Optics.Answer(request);
        }

        var path = request.RequestUri!.AbsolutePath;
        return path.EndsWith("/versions.json", StringComparison.Ordinal) ? StubHandler.Answer(HttpStatusCode.OK, $$"""{ "job": "{{job}}", "ui": "0.0.1" }""")
            : path.Contains("/api/", StringComparison.Ordinal) ? StubHandler.Answer(HttpStatusCode.OK, Kustomization("1.3.0"))
            : StubHandler.Answer(HttpStatusCode.OK, Kustomization(ui));
    }

    private static VersionAssessment? Assessment(DashboardMonitor monitor, string environment, string deployable)
    {
        var status = monitor.Environments.Single(candidate => candidate.Name == environment);
        return status.AssessVersions(status.Deployables.Single(candidate => candidate.Info.Name == deployable));
    }

    [Fact]
    public void BeforeTheFirstRoundAPinOfItsOwnIsPendingAlsoWithoutVersionsJson()
    {
        var monitor = Monitor(new StubHandler(request => Answer(request)));

        Assert.Equal(VersionState.Pending, Assessment(monitor, "uat", "ui")!.State);
        Assert.Equal(VersionState.Pending, Assessment(monitor, "uat", "job")!.State);

        // prod names no versions.json: ui has its own pin, job has none.
        Assert.Equal(PinnedVersionsState.NotTracked, monitor.Environments[1].Pinned.State);
        Assert.Equal(VersionState.Pending, Assessment(monitor, "prod", "ui")!.State);
        Assert.Null(Assessment(monitor, "prod", "job"));
        Assert.Null(monitor.Environments[1].Deployables[1].Pinned);
    }

    [Fact]
    public async Task ARoundReadsEachPinOnceAndVersionsJsonAsBefore()
    {
        var handler = new StubHandler(request => Answer(request));
        var monitor = Monitor(handler);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        string[] once =
        [
            VersionsFile,
            ApiPin,
            "https://raw.example.net/org/demo-system/main/gitops/environments/prod/ui/kustomization.yaml",
            UiPin,
        ];
        Assert.Equal(
            once.SelectMany(address => new[] { address, address }).Order(StringComparer.Ordinal),
            handler.Requests.Where(IsPin).Select(request => request.RequestUri!.AbsoluteUri).Order(StringComparer.Ordinal));
        Assert.All(handler.Requests.Where(IsPin), request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("no-store", NodeProberTests.FetchOption(request, "cache"));
            Assert.Empty(request.Headers);
        });
    }

    [Fact]
    public async Task ThePinnedVersionOfADeployableComesFromItsOwnFile()
    {
        var monitor = Monitor(new StubHandler(request => Answer(request)));

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        // versions.json says ui is 0.0.1: ui is not pinned there.
        Assert.Equal("Pinned 2.4.15. In sync: uat-ui runs 2.4.15.", Assessment(monitor, "uat", "ui")!.Text);
        Assert.Equal("Pinned 1.3.0. Differs: uat-api runs 2.4.15.", Assessment(monitor, "uat", "api")!.Text);
        Assert.Equal("Pinned 2.4.15. In sync: uat-job runs 2.4.15.", Assessment(monitor, "uat", "job")!.Text);
        Assert.Equal("Pinned 2.4.15. In sync: prod-ui runs 2.4.15.", Assessment(monitor, "prod", "ui")!.Text);
        Assert.Null(Assessment(monitor, "prod", "job"));
        Assert.Equal([true, false], monitor.Environments.Select(environment => environment.VersionsDiffer));
        Assert.Equal("Versions differ in 1 environment", monitor.VersionSummary.Text);
        Assert.Equal("All 5 nodes healthy", monitor.Summary.Text);
    }

    [Fact]
    public async Task AKustomizationThatIsNotFoundOrPinsNothingIsSaidInTheWordsOfVersionsJson()
    {
        var monitor = Monitor(new StubHandler(request => request.RequestUri!.AbsoluteUri switch
        {
            UiPin => StubHandler.Answer(HttpStatusCode.NotFound, "404: Not Found"),
            ApiPin => StubHandler.Answer(HttpStatusCode.OK, "kind: Kustomization\nresources:\n  - ../../../base\n"),
            _ => Answer(request),
        }));

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        var ui = Assessment(monitor, "uat", "ui")!;
        var api = Assessment(monitor, "uat", "api")!;
        Assert.Equal(VersionState.NotDeployed, ui.State);
        Assert.Equal("No pinned version. kustomization.yaml was not found: nothing was deployed here yet, or the repository is not public.", ui.Text);
        Assert.Equal(VersionState.PinnedUnknown, api.State);
        Assert.Equal("Pinned version not known. kustomization.yaml could not be read: the file has no newTag entry.", api.Text);

        // A pin that is not known is no difference and fails no check.
        Assert.Null(monitor.VersionSummary.Text);
        Assert.Equal("All 5 nodes healthy", monitor.Summary.Text);
        Assert.Equal(VersionState.InSync, Assessment(monitor, "uat", "job")!.State);
    }

    [Fact]
    public async Task AFailedReadingOfAPinReplacesAGoodOne()
    {
        var available = true;
        var monitor = Monitor(new StubHandler(request => !available && request.RequestUri!.AbsoluteUri == ApiPin
            ? StubHandler.Answer(HttpStatusCode.BadGateway)
            : Answer(request)));

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        Assert.Equal(VersionState.Differs, Assessment(monitor, "uat", "api")!.State);

        available = false;
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Equal("Pinned version not known. kustomization.yaml could not be read: the server answered HTTP 502.", Assessment(monitor, "uat", "api")!.Text);
        Assert.Null(monitor.VersionSummary.Text);
    }

    [Fact]
    public async Task ANewPinIsAnEventFromEitherSourceAndOnlyFromTheDeployablesOwn()
    {
        var (ui, job, offline) = ("2.4.14", "2.4.15", false);
        var monitor = Monitor(new StubHandler(request => offline && IsPin(request)
            ? StubHandler.Answer(HttpStatusCode.BadGateway)
            : Answer(request, ui, job)));

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        offline = true;
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        Assert.DoesNotContain(monitor.Events.Newest, entry => entry.Kind == EventKind.Pinned);

        // Both pins change while the files cannot be read: the failed reading in between hides no change.
        _time.Advance(TimeSpan.FromSeconds(30));
        (ui, job, offline) = ("2.4.15", "2.4.16", false);
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        // ui changed in both of its environments' kustomizations, job in versions.json; the entry of ui in
        // versions.json, which did not change and is not its pin, is no event.
        Assert.Equal(
            [
                new DashboardEvent(_time.GetUtcNow(), EventKind.Pinned, EventLevel.Info, "prod", "ui", "ui: pinned 2.4.14 → 2.4.15 in Git"),
                new DashboardEvent(_time.GetUtcNow(), EventKind.Pinned, EventLevel.Info, "uat", "job", "job: pinned 2.4.15 → 2.4.16 in Git"),
                new DashboardEvent(_time.GetUtcNow(), EventKind.Pinned, EventLevel.Info, "uat", "ui", "ui: pinned 2.4.14 → 2.4.15 in Git"),
            ],
            monitor.Events.Newest.Where(entry => entry.Kind == EventKind.Pinned).OrderBy(entry => entry.Environment + entry.Node, StringComparer.Ordinal));
    }

    [Fact]
    public async Task AnEntryInVersionsJsonIsNoPinOfADeployableWithItsOwn()
    {
        var entry = "0.0.1";
        var monitor = Monitor(new StubHandler(request => request.RequestUri!.AbsoluteUri == VersionsFile
            ? StubHandler.Answer(HttpStatusCode.OK, $$"""{ "job": "2.4.15", "ui": "{{entry}}" }""")
            : Answer(request)));

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        entry = "0.0.2";
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.DoesNotContain(monitor.Events.Newest, change => change.Kind == EventKind.Pinned);
        Assert.Equal(VersionState.InSync, Assessment(monitor, "uat", "ui")!.State);
    }

    [Fact]
    public async Task ChangedIsRaisedForEveryPinToo()
    {
        var monitor = Monitor(new StubHandler(request => Answer(request)));
        var changes = 0;
        monitor.Changed += () => Interlocked.Increment(ref changes);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        // Five endpoints, three kustomizations, one versions.json and the end of the round.
        Assert.Equal(10, changes);
    }

    [Fact]
    public void ThePinHistoryIsTheDeployablesOwnOrElseThatOfVersionsJson()
    {
        var monitor = Monitor(new StubHandler(request => Answer(request)));
        var (uat, prod) = (monitor.Environments[0], monitor.Environments[1]);

        Assert.Equal(
            "https://github.example.net/org/demo-system/commits/main/gitops/environments/uat/ui/kustomization.yaml",
            uat.PinHistoryOf(uat.Deployables[0])?.AbsoluteUri);

        // api has a pin of its own and no history of its own; job has neither.
        Assert.Equal(uat.Info.VersionsHistoryUrl, uat.PinHistoryOf(uat.Deployables[1]));
        Assert.Equal(uat.Info.VersionsHistoryUrl, uat.PinHistoryOf(uat.Deployables[2]));
        Assert.Null(prod.PinHistoryOf(prod.Deployables[0]));
        Assert.Equal(["kustomization.yaml", "kustomization.yaml", "versions.json"], uat.Deployables.Select(deployable => deployable.Info.PinFile));
    }
}
