namespace Dashboard.Health;

/// <summary>What one regional node says about its version, as the comparison with the pinned version needs it.</summary>
/// <param name="Label">The region, or the node's name when the topology gives no region.</param>
/// <param name="State">The node's last check.</param>
/// <param name="Version">The version the node last reported.</param>
public sealed record NodeVersion(string Label, HealthState State, string? Version)
{
    /// <summary>
    /// The version the node runs now, without build metadata; null when that is not known. A node that did not answer
    /// its last check is not known, although the dashboard remembers the version it reported before.
    /// </summary>
    public string? Running => State is HealthState.Healthy or HealthState.Unhealthy ? VersionText.Display(Version) : null;

    /// <summary>Why the running version is not known, as the end of a sentence that starts with the label.</summary>
    public string UnknownReason => State switch
    {
        HealthState.Pending => "is being checked",
        HealthState.Unreachable => "is unreachable",
        _ => "reports no version",
    };
}

public enum VersionState
{
    /// <summary>The file with the pin (the environment's <c>versions.json</c>, or the deployable's own) has not been read yet.</summary>
    Pending,

    /// <summary>The file could not be read: the pinned version is not known.</summary>
    PinnedUnknown,

    /// <summary>The file was not found or has no entry for the deployable: nothing was deployed.</summary>
    NotDeployed,

    /// <summary>The pinned version is known, and no node's running version is.</summary>
    NodesUnknown,

    /// <summary>Every node whose running version is known runs the pinned version.</summary>
    InSync,

    /// <summary>At least one node runs another version than the pinned one.</summary>
    Differs,
}

/// <summary>
/// The version the deployment pinned in Git for one deployable of one environment, next to the versions its nodes
/// run. Versions are compared without build metadata (the part from <c>+</c>) and without regard to case. A node whose
/// version is not known (unreachable, not checked yet, no version reported) is left out of the comparison: it never
/// makes the versions differ.
/// </summary>
public sealed record VersionAssessment(
    VersionState State,
    string? Pinned,
    IReadOnlyList<NodeVersion> Matching,
    IReadOnlyList<NodeVersion> Differing,
    IReadOnlyList<NodeVersion> Unknown,
    string Headline,
    string? Detail)
{
    public bool Differs => State == VersionState.Differs;

    /// <summary>The whole line in words, for a tooltip and for tests.</summary>
    public string Text => Detail is null ? Headline : $"{Headline}. {Detail}";

    /// <param name="pinned">The last reading of the file that holds the deployable's pin.</param>
    /// <param name="deployable">The deployable's name: its key in the reading.</param>
    /// <param name="nodes">What the deployable's nodes run.</param>
    /// <param name="file">The name of that file, for the words; <c>versions.json</c> when not given.</param>
    public static VersionAssessment Assess(PinnedVersions pinned, string deployable, IEnumerable<NodeVersion> nodes, string? file = null)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        ArgumentNullException.ThrowIfNull(deployable);
        ArgumentNullException.ThrowIfNull(nodes);
        file ??= PinnedVersions.FileName;

        switch (pinned.State)
        {
            case PinnedVersionsState.NotTracked:
            case PinnedVersionsState.Pending:
                return Without(VersionState.Pending, "Reading the pinned version", null);
            case PinnedVersionsState.Missing:
                return Without(
                    VersionState.NotDeployed,
                    "No pinned version",
                    $"{file} was not found: nothing was deployed here yet, or the repository is not public.");
            case PinnedVersionsState.Unavailable:
                return Without(
                    VersionState.PinnedUnknown,
                    "Pinned version not known",
                    $"{file} could not be read: {pinned.Detail ?? "no reason given"}.");
        }

        if (VersionText.Display(pinned.Of(deployable)) is not { } version)
        {
            return Without(
                VersionState.NotDeployed,
                "No pinned version",
                $"{file} has no entry for {deployable}: it was not deployed here yet.");
        }

        var all = nodes.ToList();
        var unknown = all.Where(node => node.Running is null).ToList();
        var matching = all.Where(node => Same(node.Running, version)).ToList();
        var differing = all.Where(node => node.Running is not null && !Same(node.Running, version)).ToList();
        var state = differing.Count > 0 ? VersionState.Differs
            : matching.Count > 0 ? VersionState.InSync
            : VersionState.NodesUnknown;
        return new VersionAssessment(
            state,
            version,
            matching,
            differing,
            unknown,
            $"Pinned {version}",
            Describe(state, version, all.Count, matching, differing, unknown));
    }

    private static VersionAssessment Without(VersionState state, string headline, string? detail) =>
        new(state, null, [], [], [], headline, detail);

    private static bool Same(string? running, string pinned) =>
        string.Equals(running, pinned, StringComparison.OrdinalIgnoreCase);

    private static string Describe(
        VersionState state,
        string version,
        int total,
        List<NodeVersion> matching,
        List<NodeVersion> differing,
        List<NodeVersion> unknown)
    {
        var known = state switch
        {
            VersionState.Differs => $"Differs: {string.Join(", ", differing.Select(node => $"{node.Label} runs {node.Running}"))}",
            VersionState.InSync when matching.Count == 1 => $"In sync: {matching[0].Label} runs {version}",
            VersionState.InSync when matching.Count == total => $"In sync: all {total} nodes run {version}",
            VersionState.InSync => $"In sync: {matching.Count} of {total} nodes run {version}",
            _ when total == 0 => "Not compared: the deployable has no nodes",
            _ => "Not compared",
        };
        if (unknown.Count == 0)
        {
            return $"{known}.";
        }

        var separator = state == VersionState.NodesUnknown ? ": " : "; ";
        return $"{known}{separator}{string.Join(", ", unknown.Select(node => $"{node.Label} {node.UnknownReason}"))}.";
    }
}

/// <summary>The second line of the header: shown only when the versions differ somewhere.</summary>
public sealed record VersionSummary(int EnvironmentsThatDiffer)
{
    public bool Differs => EnvironmentsThatDiffer > 0;

    /// <summary><c>Versions differ in 1 environment</c>; null when they differ nowhere.</summary>
    public string? Text => EnvironmentsThatDiffer switch
    {
        <= 0 => null,
        1 => "Versions differ in 1 environment",
        _ => $"Versions differ in {EnvironmentsThatDiffer} environments",
    };
}
