namespace Dashboard.Health;

/// <summary>Which endpoint of a node the dashboard calls.</summary>
public enum ProbeKind
{
    /// <summary>The full health check (<c>healthPath</c>): it also connects to the database.</summary>
    Health,

    /// <summary>The liveness endpoint (<c>alivePath</c>): the web app answers without its dependencies.</summary>
    Liveness,
}
