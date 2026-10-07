namespace Dashboard.Health;

/// <summary>The state of one regional node, as the serving decision needs it.</summary>
public sealed record NodeHealth(string Name, string? Region, bool IsPrimary, HealthState State)
{
    /// <summary>The region, or the node's name when the topology gives no region.</summary>
    public string Label => Region ?? Name;
}

public enum ServingState
{
    /// <summary>The deployable has no nodes.</summary>
    NoNodes,

    /// <summary>A node that could be the serving one has not been checked yet.</summary>
    Pending,

    /// <summary>A primary node is healthy and serves.</summary>
    Primary,

    /// <summary>No primary node is healthy; a standby is, and serves.</summary>
    FailedOver,

    /// <summary>No node is healthy.</summary>
    Down,
}

public enum FrontDoorAgreement
{
    /// <summary>The deployable has no Front Door endpoint.</summary>
    NotPresent,

    /// <summary>The Front Door endpoint or the nodes have not been checked yet.</summary>
    Pending,

    /// <summary>Front Door is healthy exactly when a node is.</summary>
    Agrees,

    /// <summary>Front Door is healthy and no node is, or the other way around.</summary>
    Disagrees,
}

/// <summary>
/// Which node is expected to serve a deployable's traffic: the first healthy node in priority order (primary nodes,
/// then the others, each in the order of the topology), and whether the Front Door endpoint agrees.
/// </summary>
/// <param name="OnlyNode">
/// The only node of a deployable without a Front Door endpoint (one app at one public address, as in a cluster); null
/// for every other deployable. Nothing can take over from such a node, so its words name no primary, no standby, no
/// region and no failover: it serves, or it does not.
/// </param>
public sealed record ServingAssessment(
    ServingState State,
    NodeHealth? Expected,
    NodeHealth? FailedPrimary,
    FrontDoorAgreement FrontDoor,
    HealthState? FrontDoorState,
    NodeHealth? OnlyNode = null)
{
    public bool IsFailedOver => State == ServingState.FailedOver;

    public string Headline => OnlyNode is { } only
        ? State switch
        {
            ServingState.Pending => $"Checking {only.Label}",
            ServingState.Primary or ServingState.FailedOver => $"Serves traffic: {only.Label}",
            _ => $"Not serving: {only.Label}",
        }
        : State switch
        {
            ServingState.NoNodes => "No nodes in the topology",
            ServingState.Pending => "Checking which region serves traffic",
            ServingState.Primary => $"Expected to serve traffic: {Expected!.Label} (primary)",
            ServingState.FailedOver => $"Failed over to {Expected!.Label}",
            _ => "No healthy node: nothing can serve traffic",
        };

    /// <summary>Why the deployable failed over; null in every other state, and for a single node.</summary>
    public string? FailoverDetail => State != ServingState.FailedOver || OnlyNode is not null
        ? null
        : FailedPrimary is null
            ? $"No primary node is healthy; {Expected!.Label} is expected to serve traffic."
            : $"Primary {FailedPrimary.Label} is {Lower(FailedPrimary.State)}; {Expected!.Label} is expected to serve traffic.";

    /// <summary>Whether the Front Door endpoint shows what the nodes predict; null without a Front Door endpoint.</summary>
    public string? FrontDoorText => FrontDoor switch
    {
        FrontDoorAgreement.NotPresent => null,
        FrontDoorAgreement.Pending => "Front Door is being checked.",
        FrontDoorAgreement.Agrees when FrontDoorState == HealthState.Healthy => "Front Door agrees: it is healthy.",
        FrontDoorAgreement.Agrees => $"Front Door agrees: it is {Lower(FrontDoorState)} too.",
        _ when FrontDoorState == HealthState.Healthy => "Front Door disagrees: it is healthy although no node is.",
        _ => $"Front Door disagrees: it is {Lower(FrontDoorState)} although {Expected?.Label ?? "a node"} is healthy.",
    };

    public static ServingAssessment Assess(IEnumerable<NodeHealth> nodes, HealthState? frontDoor)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        // OrderBy is stable: within primaries and within standbys, the order of the topology is the priority.
        var byPriority = nodes.OrderBy(node => node.IsPrimary ? 0 : 1).ToList();
        var (state, expected) = Decide(byPriority);
        var failedPrimary = state == ServingState.FailedOver ? byPriority.FirstOrDefault(node => node.IsPrimary) : null;
        var single = frontDoor is null && byPriority.Count == 1 ? byPriority[0] : null;
        return new ServingAssessment(state, expected, failedPrimary, Compare(state, frontDoor), frontDoor, single);
    }

    private static (ServingState State, NodeHealth? Expected) Decide(List<NodeHealth> byPriority)
    {
        if (byPriority.Count == 0)
        {
            return (ServingState.NoNodes, null);
        }

        foreach (var node in byPriority)
        {
            if (node.State == HealthState.Healthy)
            {
                return (node.IsPrimary ? ServingState.Primary : ServingState.FailedOver, node);
            }

            // A node with a higher priority than every healthy one is still unchecked: no decision yet.
            if (node.State == HealthState.Pending)
            {
                return (ServingState.Pending, null);
            }
        }

        return (ServingState.Down, null);
    }

    private static FrontDoorAgreement Compare(ServingState state, HealthState? frontDoor)
    {
        if (frontDoor is null)
        {
            return FrontDoorAgreement.NotPresent;
        }

        if (frontDoor == HealthState.Pending || state is ServingState.Pending or ServingState.NoNodes)
        {
            return FrontDoorAgreement.Pending;
        }

        var nodeServes = state is ServingState.Primary or ServingState.FailedOver;
        return nodeServes == (frontDoor == HealthState.Healthy) ? FrontDoorAgreement.Agrees : FrontDoorAgreement.Disagrees;
    }

    private static string Lower(HealthState? state) => state switch
    {
        HealthState.Healthy => "healthy",
        HealthState.Unhealthy => "unhealthy",
        HealthState.Unreachable => "unreachable",
        _ => "not checked",
    };
}
