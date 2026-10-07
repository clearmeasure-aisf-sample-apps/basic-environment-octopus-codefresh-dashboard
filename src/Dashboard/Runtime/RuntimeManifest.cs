namespace Dashboard.Runtime;

/// <summary>
/// <c>runtime/index.json</c>: the environments whose runtime diagram the deployment rendered, in the order of
/// <c>system.json</c>.
/// </summary>
/// <param name="Environments">One entry per environment.</param>
/// <param name="PlantUml">The PlantUML version that rendered the diagrams.</param>
public sealed record RuntimeIndex(IReadOnlyList<RuntimeIndexEntry> Environments, string? PlantUml = null);

/// <param name="Name">The environment's name, as in <c>topology.json</c>.</param>
/// <param name="Manifest">The manifest's file, relative to <c>runtime/</c>.</param>
/// <param name="Svg">The diagram's file, relative to <c>runtime/</c>.</param>
public sealed record RuntimeIndexEntry(string Name, string Manifest, string Svg);

public enum RuntimeNodeKind
{
    /// <summary>A kind this dashboard does not know: drawn, not updated.</summary>
    Other,

    /// <summary>The browser: a person, no tile.</summary>
    Person,

    /// <summary>The Front Door endpoint of a deployable: the public address.</summary>
    FrontDoor,

    /// <summary>A web app (a regional node).</summary>
    WebApp,

    /// <summary>The environment's database (Azure SQL, or SQL Server in a cluster): the browser cannot ask it.</summary>
    Sql,

    /// <summary>A Static Web App: the dashboard.</summary>
    StaticSite,
}

public enum RuntimeEdgeKind
{
    Other,

    /// <summary>The browser to a public address (the Front Door endpoint, or a web app without one).</summary>
    Public,

    /// <summary>Front Door to one of its origins, by priority.</summary>
    Origin,

    /// <summary>A web app to the database.</summary>
    Sql,

    /// <summary>The browser to the dashboard.</summary>
    Dashboard,
}

/// <summary>
/// <c>runtime/&lt;env&gt;.json</c>: which drawn element of the environment's diagram is which. The dashboard finds a
/// node by its alias and a relationship by its id, and never reads names out of the SVG.
/// </summary>
public sealed record RuntimeManifest(
    string Environment,
    IReadOnlyList<RuntimeNode> Nodes,
    IReadOnlyList<RuntimeRegion> Regions,
    IReadOnlyList<RuntimeEdge> Edges);

/// <param name="Alias">The element's name in the PlantUML source: the last part of its <c>data-qualified-name</c>.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Name">The Azure resource's name.</param>
/// <param name="Url">
/// The address the dashboard checks (web app, Front Door endpoint) or serves from (static site); null when the
/// browser does not know one, and always for the database.
/// </param>
public sealed record RuntimeNode(
    string Alias,
    RuntimeNodeKind Kind,
    string Name,
    Uri? Url = null,
    string? Deployable = null,
    string? Role = null,
    string? Region = null,
    string? RegionAlias = null);

/// <param name="Alias">The region boundary's alias.</param>
/// <param name="Name">The Azure region.</param>
/// <param name="Roles">primary, standby, data, static.</param>
public sealed record RuntimeRegion(string Alias, string Name, IReadOnlyList<string> Roles);

/// <param name="Id"><c>&lt;from&gt;-to-&lt;to&gt;</c>.</param>
/// <param name="Priority">For an origin: Front Door's priority (1 first).</param>
public sealed record RuntimeEdge(string Id, string From, string To, RuntimeEdgeKind Kind, int? Priority = null);
