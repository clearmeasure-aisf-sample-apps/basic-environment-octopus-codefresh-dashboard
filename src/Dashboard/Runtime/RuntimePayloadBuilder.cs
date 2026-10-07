using System.Globalization;
using Dashboard.Health;

namespace Dashboard.Runtime;

/// <summary>
/// The runtime view's update from the monitor's current state: the same checks, serving decision and version
/// comparison the health view shows, mapped onto the diagram's elements by the manifest. A node of the manifest is
/// matched with a target of the monitor by its address (web app, Front Door endpoint), within the environment of the
/// same name; a node the monitor does not check is drawn neutral, with the reason in words. The database takes no call
/// from a browser: it is drawn reachable when the health check of a web app that uses it passes. The numbers on the
/// relationships are the web apps' own counts of the last minute (<see cref="TelemetrySnapshot"/>); a dash where a web
/// app reports none. A web app's tile also shows its process's vitals and, next to the numbers, their trend over the
/// last checks; where the topology has a link for a number or a name, the payload carries it.
/// </summary>
public static class RuntimePayloadBuilder
{
    public const string Healthy = "healthy";
    public const string Unhealthy = "unhealthy";
    public const string Unreachable = "unreachable";
    public const string Checking = "checking";
    public const string Neutral = "neutral";

    /// <summary>The number line's placeholder where no web app reports its calls.</summary>
    public const string NoNumber = "–";
    public const string CallsUnit = "calls/min";

    private sealed record Entry(DeployableStatus Deployable, TargetStatus Target, ServingAssessment Assessment, TargetStatus? Expected);

    /// <param name="manifest">The environment's manifest.</param>
    /// <param name="environment">The monitor's environment of the same name; null when the topology has none.</param>
    /// <param name="page">The dashboard's own address: the static site that serves it is "this page".</param>
    /// <param name="zone">The viewer's time zone, for the tooltips.</param>
    public static RuntimePayload Build(RuntimeManifest manifest, EnvironmentStatus? environment, Uri? page, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(zone);
        var entries = Index(environment);
        var byAlias = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var node in manifest.Nodes)
        {
            if (Find(entries, node) is { } entry)
            {
                byAlias[node.Alias] = entry;
            }
        }

        var tiles = manifest.Nodes
            .Where(node => node.Kind != RuntimeNodeKind.Person)
            .Select(node => node.Kind == RuntimeNodeKind.Sql
                ? DatabaseTile(node, Clients(manifest, node, byAlias), zone, environment?.Info.Links)
                : Tile(node, byAlias.GetValueOrDefault(node.Alias), environment, page, zone))
            .ToList();
        var reachable = manifest.Nodes
            .Where(node => node.Kind == RuntimeNodeKind.Sql && tiles.Any(tile => tile.Alias == node.Alias && tile.State == Healthy))
            .Select(node => node.RegionAlias)
            .ToHashSet(StringComparer.Ordinal);
        var regions = manifest.Regions.Select(region => Region(region, manifest, byAlias, reachable.Contains(region.Alias))).ToList();
        var edges = manifest.Edges.Select(edge => Edge(edge, manifest, byAlias)).ToList();
        return new RuntimePayload(tiles, regions, edges);
    }

    /// <summary>The state word of a health state, as the payload carries it.</summary>
    public static string StateOf(HealthState state) => state switch
    {
        HealthState.Healthy => Healthy,
        HealthState.Unhealthy => Unhealthy,
        HealthState.Unreachable => Unreachable,
        _ => Checking,
    };

    private static List<Entry> Index(EnvironmentStatus? environment)
    {
        var entries = new List<Entry>();
        if (environment is null)
        {
            return entries;
        }

        foreach (var deployable in environment.Deployables)
        {
            var assessment = deployable.Assess(out var expected);
            entries.AddRange(deployable.Targets.Select(target => new Entry(deployable, target, assessment, expected)));
        }

        return entries;
    }

    private static Entry? Find(List<Entry> entries, RuntimeNode node)
    {
        var kind = node.Kind switch
        {
            RuntimeNodeKind.WebApp => TargetKind.Node,
            RuntimeNodeKind.FrontDoor => TargetKind.FrontDoor,
            _ => (TargetKind?)null,
        };
        return kind is null || node.Url is null
            ? null
            : entries.FirstOrDefault(entry => entry.Target.Kind == kind && entry.Target.Url == node.Url);
    }

    private static RuntimeTile Tile(RuntimeNode node, Entry? entry, EnvironmentStatus? environment, Uri? page, TimeZoneInfo zone)
    {
        if (entry is not null)
        {
            return node.Kind == RuntimeNodeKind.FrontDoor ? FrontDoorTile(node, entry, zone) : WebAppTile(node, entry, environment!, zone);
        }

        return node.Kind switch
        {
            RuntimeNodeKind.StaticSite when node.Url is not null && page is not null && SameSite(node.Url, page) => NeutralTile(
                node,
                "This page",
                "serves this dashboard",
                $"{node.Name} ({node.Url.Host}) serves the page you are looking at."),
            RuntimeNodeKind.StaticSite => NeutralTile(
                node,
                "Not probed",
                node.Url is null ? "its address is not in this deployment" : node.Url.Host,
                $"{node.Name}: the dashboard of this environment. It does not check itself."),
            RuntimeNodeKind.FrontDoor when node.Url is null => NeutralTile(
                node,
                "No address",
                "endpoint not deployed yet",
                $"{node.Name}: the deployment found no such endpoint in the Front Door profile. Deploy the dashboard again once the environment has it."),
            RuntimeNodeKind.WebApp or RuntimeNodeKind.FrontDoor => NeutralTile(
                node,
                "Not checked",
                "not in topology.json",
                $"{node.Name}: the topology this page loaded has no such address, so it is not checked. Reload the topology, or deploy the dashboard again."),
            _ => NeutralTile(node, "Not probed", string.Empty, node.Name),
        };
    }

    /// <summary>The checked web apps with a relationship to the database.</summary>
    private static List<Entry> Clients(RuntimeManifest manifest, RuntimeNode database, Dictionary<string, Entry> byAlias) =>
        [.. manifest.Edges
            .Where(edge => edge.Kind == RuntimeEdgeKind.Sql && edge.To == database.Alias)
            .Select(edge => byAlias.GetValueOrDefault(edge.From))
            .OfType<Entry>()];

    /// <summary>
    /// The database from the web apps' health checks, which connect to it: one that passes says the database answered.
    /// One that fails does not say it did not, since the web app itself may be the cause; the liveness probe leaves the
    /// database alone.
    /// </summary>
    private static RuntimeTile DatabaseTile(RuntimeNode node, List<Entry> clients, TimeZoneInfo zone, LinkSet? links) =>
        DatabaseTile(node, clients, zone) with { NameLink = RuntimeLink.To(links?[LinkSet.Database], LinkText.For(LinkSet.Database, node.Name)) };

    private static RuntimeTile DatabaseTile(RuntimeNode node, List<Entry> clients, TimeZoneInfo zone)
    {
        if (clients.Count == 0)
        {
            return NeutralTile(
                node,
                "Not probed",
                "not probed from the browser",
                $"{node.Name}: the database takes no call from a browser, and this page checks no web app that uses it.");
        }

        var passed = clients.Where(entry => entry.Target.Last is { State: HealthState.Healthy, Probe: ProbeKind.Health }).ToList();
        if (passed.Count > 0)
        {
            var names = string.Join(", ", passed.Select(entry => entry.Target.Name));
            var latest = passed.Max(entry => entry.Target.Last!.CheckedAt);
            var line = passed.Count == 1
                ? $"health check of {passed[0].Target.Region ?? passed[0].Target.Name} passed"
                : $"health checks of {passed.Count} web apps passed";
            var queries = clients.Select(entry => entry.Target.Telemetry).OfType<TelemetrySnapshot>().ToList();

            // The queries the traffic causes; what the apps run in the background (the message bus polling) is said in
            // the tooltip, where an app tells the two apart.
            var background = queries.Any(telemetry => telemetry.SplitsSql)
                ? string.Create(CultureInfo.InvariantCulture, $" Queries per minute, counted by the web apps: {queries.Sum(telemetry => telemetry.SqlOfTraffic)} while handling requests, {queries.Sum(telemetry => telemetry.SqlBackground ?? 0)} in the background (mostly the message bus polling the database).")
                : string.Empty;
            return new RuntimeTile(
                node.Alias,
                Healthy,
                "Reachable",
                queries.Count == 0 ? null : string.Create(CultureInfo.InvariantCulture, $"{queries.Sum(telemetry => telemetry.SqlOfTraffic)} queries/min"),
                [new RuntimeTileLine(line, "ok")],
                null,
                $"{node.Name}: reachable. The database takes no call from a browser; the health check of {names} connected to it (last {TimeText.Clock(latest, zone)}).{background}");
        }

        if (clients.All(entry => entry.Target.Last is { Probe: ProbeKind.Liveness }))
        {
            return NeutralTile(
                node,
                "Not probed",
                "probe Liveness leaves it alone",
                $"{node.Name}: the probe is Liveness, which does not connect to the database. Choose Health check to see whether it answers.");
        }

        if (clients.Any(entry => entry.Target.State == HealthState.Pending))
        {
            return new RuntimeTile(node.Alias, Checking, "Checking", null, [new RuntimeTileLine("waiting for the health checks", "muted")], null, $"{node.Name}: waiting for the health checks of the web apps that use it.");
        }

        return NeutralTile(
            node,
            "Not confirmed",
            "no health check of its apps passes",
            $"{node.Name}: no web app that uses it passes its health check, so this page cannot tell whether it answers: the database or the web app may be the cause.");
    }

    private static RuntimeTile NeutralTile(RuntimeNode node, string label, string line, string title) =>
        new(node.Alias, Neutral, label, null, line.Length > 0 ? [new RuntimeTileLine(line, "muted")] : [], null, title);

    private static RuntimeTile WebAppTile(RuntimeNode node, Entry entry, EnvironmentStatus environment, TimeZoneInfo zone)
    {
        var target = entry.Target;
        var lines = new List<RuntimeTileLine> { VersionLine(target, entry.Deployable) };
        if (PinnedLine(environment.AssessVersions(entry.Deployable), target) is { } pinned)
        {
            lines.Add(pinned);
        }

        if (target.Telemetry is { } telemetry)
        {
            lines.AddRange(TelemetryLines(node, target, telemetry));
        }

        lines.Add(entry.Assessment.OnlyNode is null ? RoleLine(target, entry.Expected) : SingleNodeLine(target, entry.Expected));
        return Checked(node, target, lines, zone) with
        {
            Link = Link(target, LinkSet.LiveMetrics, node.Name),
            NameLink = Link(target, LinkSet.Portal, node.Name),
        };
    }

    /// <summary>A node's role and whether the deployable's traffic goes through it.</summary>
    private static RuntimeTileLine RoleLine(TargetStatus target, TargetStatus? expected)
    {
        var role = target.Role ?? (target.IsPrimary ? NodeInfo.PrimaryRole : NodeInfo.StandbyRole);
        return ReferenceEquals(target, expected)
            ? new RuntimeTileLine($"{role}: serves traffic", "serving")
            : target.State switch
            {
                HealthState.Healthy => new RuntimeTileLine($"{role}: ready, no traffic", "muted"),
                HealthState.Pending => new RuntimeTileLine(role, "muted"),
                _ => new RuntimeTileLine($"{role}: not serving", "plain"),
            };
    }

    /// <summary>
    /// The same line for the only node of a deployable without a Front Door endpoint: no role, since nothing stands by.
    /// </summary>
    private static RuntimeTileLine SingleNodeLine(TargetStatus target, TargetStatus? expected) =>
        ReferenceEquals(target, expected) ? new RuntimeTileLine("serves traffic", "serving")
        : target.State == HealthState.Pending ? new RuntimeTileLine("checking", "muted")
        : new RuntimeTileLine("not serving", "plain");

    private static RuntimeLink? Link(TargetStatus target, string key, string subject) =>
        RuntimeLink.To(target.Links[key], LinkText.For(key, subject));

    /// <summary>
    /// A web app's own numbers: its traffic of the last minute with the trend of the requests, what failed, and its
    /// process's vitals (an app that reports none gets the first two lines only).
    /// </summary>
    private static IEnumerable<RuntimeTileLine> TelemetryLines(RuntimeNode node, TargetStatus target, TelemetrySnapshot telemetry)
    {
        var requests = string.Create(CultureInfo.InvariantCulture, $"{telemetry.Requests} req/min");
        var traffic = new List<RuntimeTextPart> { new(requests, Link(target, LinkSet.Performance, node.Name)) };
        if (telemetry.P95Ms is { } p95)
        {
            traffic.Add(new RuntimeTextPart(string.Create(CultureInfo.InvariantCulture, $" · p95 {p95} ms")));
        }

        yield return RuntimeTileLine.Of("plain", traffic, RuntimeTrend.Of(Trends.Requests(target)));

        var vitals = telemetry.Process;
        var failed = new List<RuntimeTextPart> { new(Errors(telemetry.Errors), Link(target, LinkSet.Failures, node.Name)) };
        if (vitals?.ExceptionsPerMinute is { } exceptions)
        {
            failed.Add(new RuntimeTextPart(string.Create(CultureInfo.InvariantCulture, $" · {exceptions} exceptions/min")));
        }

        yield return RuntimeTileLine.Of(telemetry.Errors > 0 || vitals?.ExceptionsPerMinute > 0 ? "warn" : "plain", failed);

        if (vitals is null)
        {
            yield break;
        }

        var process = new List<string>();
        if (vitals.CpuPercent is { } cpu)
        {
            process.Add($"CPU {VitalsText.Cpu(cpu)}");
        }

        if ((vitals.WorkingSetMb ?? vitals.GcHeapMb) is { } memory)
        {
            process.Add(VitalsText.Memory(memory));
        }

        if (vitals.InFlight is { } inFlight)
        {
            process.Add(string.Create(CultureInfo.InvariantCulture, $"{inFlight} in flight"));
        }

        if (process.Count > 0)
        {
            yield return new RuntimeTileLine(string.Join(" · ", process), "plain", null, RuntimeTrend.Of(Trends.Cpu(target)));
        }

        if (vitals.UptimeSeconds is { } uptime)
        {
            // A process that started within the last minutes is worth a second look: a deployment, a crash, a scale-in.
            yield return new RuntimeTileLine(VitalsText.Uptime(uptime), vitals.RestartedRecently ? "warn" : "muted");
        }
    }

    private static string Errors(int errors) => errors == 1 ? "1 error" : string.Create(CultureInfo.InvariantCulture, $"{errors} errors");

    private static RuntimeTile FrontDoorTile(RuntimeNode node, Entry entry, TimeZoneInfo zone)
    {
        var assessment = entry.Assessment;
        var lines = new List<RuntimeTileLine> { VersionLine(entry.Target, null) };
        lines.Add(assessment.State switch
        {
            ServingState.Primary => new RuntimeTileLine($"routes to {assessment.Expected!.Label} (priority 1)", "plain"),
            ServingState.FailedOver => new RuntimeTileLine($"routes to {assessment.Expected!.Label} (failed over)", "serving"),
            ServingState.Down => new RuntimeTileLine("no healthy origin", "plain"),
            ServingState.NoNodes => new RuntimeTileLine("no origins in the topology", "muted"),
            _ => new RuntimeTileLine("origins being checked", "muted"),
        });
        lines.Add(assessment.FrontDoor switch
        {
            FrontDoorAgreement.Agrees => new RuntimeTileLine("agrees with the web apps", "ok"),
            FrontDoorAgreement.Disagrees => new RuntimeTileLine("disagrees with the web apps", "warn"),
            _ => new RuntimeTileLine("being compared with the web apps", "muted"),
        });
        return Checked(node, entry.Target, lines, zone, assessment.FrontDoorText) with
        {
            NameLink = Link(entry.Target, LinkSet.FrontDoor, entry.Deployable.Info.Name),
        };
    }

    private static RuntimeTile Checked(RuntimeNode node, TargetStatus target, List<RuntimeTileLine> lines, TimeZoneInfo zone, string? note = null)
    {
        var last = target.Last;
        var facts = last switch
        {
            null => "not checked yet",
            { StatusCode: { } status, LatencyMs: { } latency } => string.Create(CultureInfo.InvariantCulture, $"HTTP {status} · {latency} ms"),
            { StatusCode: { } status } => string.Create(CultureInfo.InvariantCulture, $"HTTP {status}"),
            _ => "no answer",
        };
        var title = new List<string> { $"{node.Name}: {HealthClassifier.Label(target.State)}", target.Url.AbsoluteUri };
        if (last is not null)
        {
            title.Add($"Last check {TimeText.Clock(last.CheckedAt, zone)}{(last.Detail is { } detail ? $": {detail}" : string.Empty)}");
        }

        title.Add(HistoryText.Describe(target.History));
        if (note is not null)
        {
            title.Add(note);
        }

        return new RuntimeTile(
            node.Alias,
            StateOf(target.State),
            HealthClassifier.Label(target.State),
            facts,
            lines,
            [.. target.History.Select(result => StateOf(result.State))],
            string.Join('\n', title));
    }

    private static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? NoNumber;

    /// <summary>
    /// The version a node runs. A web app's version leads to its release in Octopus Deploy, or else to the commit its
    /// build names for that version; a Front Door endpoint (no deployable given) answers with the version of whichever
    /// node served, so its version is no link.
    /// </summary>
    private static RuntimeTileLine VersionLine(TargetStatus target, DeployableStatus? deployable)
    {
        if (target.Version is not { } version)
        {
            return new RuntimeTileLine("version not known", "muted");
        }

        return RuntimeTileLine.Of("strong", [new RuntimeTextPart("version "), new RuntimeTextPart(version, deployable is null ? null : VersionLink(deployable, version))]);
    }

    /// <summary>Where a version leads: the release in Octopus Deploy, or else the commit of the build with that version.</summary>
    public static RuntimeLink? VersionLink(DeployableStatus deployable, string version)
    {
        ArgumentNullException.ThrowIfNull(deployable);
        if (LinkText.Release(deployable.Info.ProjectUrl, version) is { } release)
        {
            return new RuntimeLink(release.AbsoluteUri, LinkText.ReleaseTitle(deployable.Info.Name, version));
        }

        return deployable.Build is { CommitUrl: { } commit, Commit: { } sha } build && string.Equals(build.Version, version, StringComparison.OrdinalIgnoreCase)
            ? new RuntimeLink(commit.AbsoluteUri, LinkText.CommitTitle(sha))
            : null;
    }

    /// <summary>This node's part of the comparison with the version pinned in Git; null when nothing is pinned for the environment.</summary>
    private static RuntimeTileLine? PinnedLine(VersionAssessment? versions, TargetStatus target)
    {
        if (versions is null)
        {
            return null;
        }

        var label = target.Region ?? target.Name;
        bool Has(IReadOnlyList<NodeVersion> nodes) => nodes.Any(node => node.Label == label);
        return versions.State switch
        {
            VersionState.Pending => new RuntimeTileLine("reading the pinned version", "unknown"),
            VersionState.NotDeployed => new RuntimeTileLine("no pinned version", "unknown"),
            VersionState.PinnedUnknown => new RuntimeTileLine("pinned version not known", "unknown"),
            _ when Has(versions.Differing) => new RuntimeTileLine($"differs from pinned {versions.Pinned}", "differs"),
            _ when Has(versions.Matching) => new RuntimeTileLine($"pinned {versions.Pinned}: in sync", "insync"),
            _ => new RuntimeTileLine($"pinned {versions.Pinned}: not compared", "unknown"),
        };
    }

    private static RuntimeRegionMark Region(RuntimeRegion region, RuntimeManifest manifest, Dictionary<string, Entry> byAlias, bool databaseReachable)
    {
        var apps = manifest.Nodes
            .Where(node => node.Kind == RuntimeNodeKind.WebApp && node.RegionAlias == region.Alias)
            .Select(node => byAlias.GetValueOrDefault(node.Alias))
            .ToList();
        if (apps.Count == 0)
        {
            var others = region.Roles
                .Where(role => !(databaseReachable && role == "data"))
                .Select(role => role switch
                {
                    "data" => "database",
                    "static" => "static sites",
                    _ => role,
                })
                .ToList();
            var parts = new List<string>();
            if (databaseReachable)
            {
                parts.Add("database: reachable");
            }

            if (others.Count > 0)
            {
                parts.Add($"{string.Join(", ", others)}: not probed");
            }

            return new RuntimeRegionMark(region.Alias, Neutral, string.Join("; ", parts));
        }

        // A web app the topology does not have is left out; a region with none the topology has is not checked.
        var known = apps.OfType<Entry>().ToList();
        return known.Count == 0 ? new RuntimeRegionMark(region.Alias, Neutral, "not checked") : Region(region.Alias, known);
    }

    private static RuntimeRegionMark Region(string alias, List<Entry> apps)
    {
        if (apps.Any(entry => ReferenceEquals(entry.Target, entry.Expected)))
        {
            return new RuntimeRegionMark(alias, "serving", "serving traffic");
        }

        if (apps.All(entry => entry.Target.State == HealthState.Healthy))
        {
            return new RuntimeRegionMark(alias, "standby", "standby: ready");
        }

        return apps.Any(entry => entry.Target.State == HealthState.Pending)
            ? new RuntimeRegionMark(alias, Checking, "checking")
            : new RuntimeRegionMark(alias, "down", "not serving");
    }

    private static RuntimeEdgeMark Edge(RuntimeEdge edge, RuntimeManifest manifest, Dictionary<string, Entry> byAlias)
    {
        var from = byAlias.GetValueOrDefault(edge.From);
        var to = byAlias.GetValueOrDefault(edge.To);
        switch (edge.Kind)
        {
            case RuntimeEdgeKind.Origin:
            {
                var role = edge.Priority == 1
                    ? "first, while healthy"
                    : "when priority 1 is down";
                var state = to is null ? Neutral : Carries(to);
                var telemetry = to?.Target.Telemetry;
                var text = telemetry is { FrontDoorProbes: > 0 } ? string.Create(CultureInfo.InvariantCulture, $"{role} · {telemetry.FrontDoorProbes} probes") : role;
                var counted = telemetry is null
                    ? "Calls per minute: the web app reports none."
                    : string.Create(CultureInfo.InvariantCulture, $"Last minute, counted by the web app: {telemetry.FromFrontDoor} requests forwarded by Front Door, {telemetry.FrontDoorProbes} Front Door health probes.");
                return new RuntimeEdgeMark(
                    edge.Id,
                    state,
                    Number(telemetry?.FromFrontDoor),
                    CallsUnit,
                    text,
                    $"Front Door to {edge.To}, origin priority {edge.Priority?.ToString(CultureInfo.InvariantCulture) ?? "not known"}: {role}. {Words(state)} {counted}",
                    to is null || telemetry is null ? null : Link(to.Target, LinkSet.Performance, to.Target.Name),
                    to is null ? null : RuntimeTrend.Of(Trends.FromFrontDoor(to.Target)));
            }

            case RuntimeEdgeKind.Sql:
            {
                // A web app that is down sends no queries: the database is not the reason, so the line is idle.
                var carries = from is null ? Neutral : Carries(from);
                var state = carries == "down" ? "idle" : carries;
                var telemetry = from?.Target.Telemetry;
                var latency = telemetry?.SqlP95Ms is { } p95 ? string.Create(CultureInfo.InvariantCulture, $", p95 {p95} ms") : string.Empty;
                var counted = telemetry switch
                {
                    null => "Queries per minute: the web app reports none.",
                    { SplitsSql: true } => string.Create(CultureInfo.InvariantCulture, $"Last minute, counted by the web app: {telemetry.SqlRequests} SQL commands while handling requests (the number shown: what traffic causes) and {telemetry.SqlBackground} in the background (mostly the message bus polling the database), {telemetry.Sql} in all{latency}; its health checks query the database too."),
                    _ => string.Create(CultureInfo.InvariantCulture, $"Last minute, counted by the web app: {telemetry.Sql} SQL commands{latency}; its health checks query the database too."),
                };
                const string Queries = "queries of the app";
                return new RuntimeEdgeMark(
                    edge.Id,
                    state,
                    Number(telemetry?.SqlOfTraffic),
                    CallsUnit,
                    telemetry is { SplitsSql: true } ? string.Create(CultureInfo.InvariantCulture, $"{Queries} · {telemetry.SqlBackground} background") : Queries,
                    $"{edge.From} to the database. {Words(state)} {counted}",
                    from is null || telemetry is null ? null : Link(from.Target, LinkSet.Dependencies, from.Target.Name),
                    from is null ? null : RuntimeTrend.Of(Trends.Sql(from.Target)));
            }

            case RuntimeEdgeKind.Public:
            {
                var target = to ?? from;
                var state = target is null ? Neutral : target.Target.State switch
                {
                    HealthState.Healthy => "active",
                    HealthState.Pending => Checking,
                    _ => "down",
                };
                var (number, counted, trend) = PublicCalls(edge, manifest, byAlias);
                var requests = number == NoNumber || target is null
                    ? null
                    : RuntimeLink.To(target.Deployable.Info.Links?[LinkSet.Logs], LinkText.For(LinkSet.Logs, target.Deployable.Info.Name));
                return new RuntimeEdgeMark(edge.Id, state, number, CallsUnit, null, $"The browser to {edge.To}: {Words(state)} {counted}", requests, RuntimeTrend.Of(trend));
            }

            default:
                return new RuntimeEdgeMark(edge.Id, Neutral, null, null, null, $"{edge.From} to {edge.To}");
        }
    }

    /// <summary>
    /// The calls to a public address: for a Front Door endpoint, the sum of what its origins counted as forwarded by
    /// Front Door (no caching rule is set, so every call reaches an origin); for a web app's own address, its direct calls.
    /// </summary>
    private static (string Number, string Words, Trend? Trend) PublicCalls(RuntimeEdge edge, RuntimeManifest manifest, Dictionary<string, Entry> byAlias)
    {
        var origins = manifest.Edges
            .Where(other => other.Kind == RuntimeEdgeKind.Origin && other.From == edge.To)
            .Select(other => byAlias.GetValueOrDefault(other.To)?.Target)
            .ToList();
        if (origins.Count > 0)
        {
            var counted = origins.Select(origin => origin?.Telemetry).OfType<TelemetrySnapshot>().ToList();
            return counted.Count == 0
                ? (NoNumber, "Calls per minute: no origin reports them.", null)
                : (Number(counted.Sum(telemetry => telemetry.FromFrontDoor)), "Calls per minute: the sum of what its origins counted from Front Door.", Trends.Sum(origins.OfType<TargetStatus>()));
        }

        var direct = byAlias.GetValueOrDefault(edge.To)?.Target;
        return direct?.Telemetry is not { } own
            ? (NoNumber, "Calls per minute: the web app reports none.", null)
            : (Number(own.Direct), "Calls per minute: counted by the web app.", Trends.Direct(direct));
    }

    /// <summary>Whether the traffic of a node's deployable goes through this node, by the serving decision.</summary>
    private static string Carries(Entry entry) =>
        ReferenceEquals(entry.Target, entry.Expected) ? "active"
        : entry.Target.State == HealthState.Healthy ? "idle"
        : entry.Target.State == HealthState.Pending ? Checking
        : "down";

    private static string Words(string state) => state switch
    {
        "active" => "Carries the traffic.",
        "idle" => "Idle: no traffic expected.",
        "down" => "Down: its end is not healthy.",
        Checking => "Being checked.",
        _ => "Not checked.",
    };

    private static bool SameSite(Uri site, Uri page) =>
        string.Equals(site.Host, page.Host, StringComparison.OrdinalIgnoreCase) && site.Port == page.Port;
}
