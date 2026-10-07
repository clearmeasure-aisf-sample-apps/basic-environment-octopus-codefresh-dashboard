namespace Dashboard.Runtime;

/// <summary>
/// <c>runtime/index.json</c>: the environments whose runtime diagram the deployment rendered, in the order of
/// <c>system.json</c>.
/// </summary>
/// <param name="Environments">One entry per environment.</param>
/// <param name="PlantUml">The PlantUML version that rendered the diagrams.</param>
/// <param name="Generated">When the diagrams were rendered: their names, sizes and relationships are as of then.</param>
public sealed record RuntimeIndex(IReadOnlyList<RuntimeIndexEntry> Environments, string? PlantUml = null, DateTimeOffset? Generated = null);

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

    /// <summary>
    /// A gateway inside a cluster that holds the public address and routes it to the web apps (relationships of kind
    /// <see cref="RuntimeEdgeKind.Route"/>). The page does not check it on its own: it is reachable when the check of
    /// a web app passes through it.
    /// </summary>
    Gateway,

    /// <summary>
    /// A workload inside a cluster without an address a browser can call (a message handler, Argo CD): drawn, not
    /// probed, and asleep while its cluster is.
    /// </summary>
    Workload,
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

    /// <summary>A gateway to a web app it routes the public address to.</summary>
    Route,
}

/// <summary>
/// <c>runtime/&lt;env&gt;.json</c>: which drawn element of the environment's diagram is which. The dashboard finds a
/// node by its alias and a relationship by its id, and never reads names out of the SVG.
/// </summary>
public sealed record RuntimeManifest(
    string Environment,
    IReadOnlyList<RuntimeNode> Nodes,
    IReadOnlyList<RuntimeRegion> Regions,
    IReadOnlyList<RuntimeEdge> Edges)
{
    /// <summary>
    /// The frame of a cluster (a region with the role <see cref="RuntimeRegion.ClusterRole"/>) that an element is
    /// drawn inside: the frame's alias is a part of the element's qualified name before its own. Null for an element
    /// outside every cluster frame, or without a qualified name: what stops with a cluster is what is drawn inside it.
    /// </summary>
    public RuntimeRegion? ClusterOf(string? qualifiedName)
    {
        if (qualifiedName is null)
        {
            return null;
        }

        var parts = qualifiedName.Split('.');
        return Regions.FirstOrDefault(region => region.IsCluster && Array.IndexOf(parts, region.Alias, 0, parts.Length - 1) >= 0);
    }
}

/// <param name="Alias">The element's name in the PlantUML source: the last part of its <c>data-qualified-name</c>.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Name">The Azure resource's name.</param>
/// <param name="Url">
/// The address the dashboard checks (web app, Front Door endpoint) or serves from (static site); null when the
/// browser does not know one, and always for the database.
/// </param>
/// <param name="QualifiedName">The alias with the aliases of the frames around it, as in the SVG; null when the manifest has none.</param>
public sealed record RuntimeNode(
    string Alias,
    RuntimeNodeKind Kind,
    string Name,
    Uri? Url = null,
    string? Deployable = null,
    string? Role = null,
    string? Region = null,
    string? RegionAlias = null,
    string? QualifiedName = null);

/// <param name="Alias">The region boundary's alias.</param>
/// <param name="Name">The Azure region; the namespace or the cluster for a frame inside a cluster.</param>
/// <param name="Roles">primary, standby, data, static; namespace; cluster (the frame of a Kubernetes cluster).</param>
/// <param name="QualifiedName">The alias with the aliases of the frames around it, as in the SVG; null when the manifest has none.</param>
public sealed record RuntimeRegion(string Alias, string Name, IReadOnlyList<string> Roles, string? QualifiedName = null)
{
    public const string ClusterRole = "cluster";

    /// <summary>True for the frame of a Kubernetes cluster: what is drawn inside it stops with the cluster.</summary>
    public bool IsCluster => Roles.Contains(ClusterRole, StringComparer.OrdinalIgnoreCase);
}

/// <param name="Id"><c>&lt;from&gt;-to-&lt;to&gt;</c>.</param>
/// <param name="Priority">For an origin: Front Door's priority (1 first).</param>
public sealed record RuntimeEdge(string Id, string From, string To, RuntimeEdgeKind Kind, int? Priority = null);
