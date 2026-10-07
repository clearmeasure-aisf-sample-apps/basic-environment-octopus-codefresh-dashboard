namespace Dashboard.Runtime;

public enum DashboardView
{
    /// <summary>The tiles of every environment: the default.</summary>
    Health,

    /// <summary>One environment's runtime diagram, updated live.</summary>
    Runtime,

    /// <summary>The cluster the system runs in: the AKS service, its nodes and its pods. Only for a topology with a cluster.</summary>
    Cluster,
}

/// <summary>
/// The view in the page's address (its hash, without <c>#</c>): <c>runtime</c> or <c>runtime/&lt;environment&gt;</c> is the
/// runtime view, <c>cluster</c> the cluster view, anything else the health view. A link with the hash opens that view.
/// </summary>
public sealed record ViewAddress(DashboardView View, string? Environment = null)
{
    private const string RuntimeName = "runtime";
    private const string ClusterName = "cluster";

    public static ViewAddress Health { get; } = new(DashboardView.Health);

    public static ViewAddress Cluster { get; } = new(DashboardView.Cluster);

    public static ViewAddress Parse(string? hash)
    {
        var text = (hash ?? string.Empty).Trim().TrimStart('#').Trim('/');
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        var head = slash < 0 ? text : text[..slash];
        if (string.Equals(head, ClusterName, StringComparison.OrdinalIgnoreCase))
        {
            return Cluster;
        }

        if (!string.Equals(head, RuntimeName, StringComparison.OrdinalIgnoreCase))
        {
            return Health;
        }

        var environment = slash < 0 ? null : text[(slash + 1)..].Trim('/');
        return new ViewAddress(DashboardView.Runtime, string.IsNullOrWhiteSpace(environment) ? null : environment);
    }

    /// <summary>The hash for this view, without <c>#</c>: empty for the health view.</summary>
    public string ToHash() => View switch
    {
        DashboardView.Runtime when Environment is { } environment => $"{RuntimeName}/{environment}",
        DashboardView.Runtime => RuntimeName,
        DashboardView.Cluster => ClusterName,
        _ => string.Empty,
    };
}
