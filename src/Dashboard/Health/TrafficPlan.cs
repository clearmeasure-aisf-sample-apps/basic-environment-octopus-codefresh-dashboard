namespace Dashboard.Health;

/// <summary>
/// The requests the traffic button sends in one environment: every deployable's representative paths
/// (<c>trafficPaths</c> of the topology; the start page without them) at its public address, which is the Front Door
/// endpoint where there is one and the primary web app's own address otherwise. So the calls take the path a user's
/// calls take: browser, Front Door, the origin that serves, the database.
/// </summary>
public sealed record TrafficPlan(string Environment, IReadOnlyList<Uri> Addresses, IReadOnlyList<string> Targets)
{
    public static readonly IReadOnlyList<string> DefaultPaths = ["/"];

    /// <summary>Requests per second while the button runs: enough to move every number, far below any limit.</summary>
    public const int PerSecond = 2;

    /// <summary>How long one press sends: the counters' window is one minute.</summary>
    public const int Seconds = 60;

    /// <summary>The plan of an environment; null when it has nothing to call.</summary>
    public static TrafficPlan? For(EnvironmentInfo environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var addresses = new List<Uri>();
        var targets = new List<string>();
        foreach (var deployable in environment.Deployables)
        {
            var publicAddress = deployable.FrontDoor
                ?? deployable.Nodes.FirstOrDefault(node => node.IsPrimary)?.Url
                ?? (deployable.Nodes.Count > 0 ? deployable.Nodes[0].Url : null);
            if (publicAddress is null)
            {
                continue;
            }

            targets.Add($"{deployable.Name} at {publicAddress.Host}");
            var paths = deployable.TrafficPaths is { Count: > 0 } configured ? configured : DefaultPaths;
            addresses.AddRange(paths.Select(path => ProbeUrl.Combine(publicAddress, path)));
        }

        return addresses.Count == 0 ? null : new TrafficPlan(environment.Name, addresses, targets);
    }
}
