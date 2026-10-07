using System.Globalization;
using Dashboard.Health;

namespace Dashboard.Cluster;

/// <summary>The states of the cluster view: the page's vocabulary, with a warning and a neutral state next to it.</summary>
public enum ClusterState
{
    /// <summary>Not read yet, or something is starting: the dots.</summary>
    Checking,

    /// <summary>The check mark.</summary>
    Healthy,

    /// <summary>Worth a look, in words: the warning triangle, without the wash of Unhealthy.</summary>
    Warning,

    /// <summary>An answer that says something is wrong: the warning triangle.</summary>
    Unhealthy,

    /// <summary>No answer, or one that is too old to count: the cross.</summary>
    Unreachable,

    /// <summary>Neither good nor bad: stopped on purpose, finished, or not known. The bar.</summary>
    Neutral,
}

/// <summary>What the view says about one pod.</summary>
public enum PodState
{
    /// <summary>Running, and every container is ready.</summary>
    Ready,

    /// <summary>Not ready yet, and young enough for that to be normal.</summary>
    Starting,

    /// <summary>Failed, pending for too long, or running without being ready.</summary>
    Unhealthy,

    /// <summary>A job that ran to its end (phase Succeeded): not a problem, and not counted.</summary>
    Finished,
}

/// <summary>Whether the cluster's own status reaches the page.</summary>
public enum ClusterLiveness
{
    /// <summary>Not read yet.</summary>
    Pending,

    /// <summary>The file answers and the collector wrote it recently.</summary>
    Live,

    /// <summary>The file answers, but the collector wrote it too long ago.</summary>
    Stale,

    /// <summary>The address does not answer, or answers without the file.</summary>
    Silent,

    /// <summary>The address answers something that is not the file.</summary>
    Unreadable,
}

/// <summary>The rules that turn a pod's phase, readiness and age into its state.</summary>
public static class PodRules
{
    /// <summary>A pod may be Pending this long before it counts as unhealthy.</summary>
    public static readonly TimeSpan PendingGrace = TimeSpan.FromMinutes(5);

    /// <summary>A running pod may be not ready this long before it counts as unhealthy.</summary>
    public static readonly TimeSpan StartingGrace = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Finished for the phase Succeeded. Unhealthy for Failed, for Pending longer than <see cref="PendingGrace"/> and
    /// for Running without being ready, unless the pod is younger than <see cref="StartingGrace"/>: then it is
    /// starting, and so is a Pending pod whose start is not known. A pod in a phase the page does not know is ready
    /// when it says so and unhealthy otherwise.
    /// </summary>
    /// <param name="asOf">The moment the facts are of (<see cref="ClusterStatus.AsOf"/>): the pod's age is counted from its start to it.</param>
    public static PodState StateOf(ClusterPod pod, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(pod);
        var age = pod.StartedAt is { } started ? asOf - started : (TimeSpan?)null;
        return pod.Phase switch
        {
            ClusterPod.Succeeded => PodState.Finished,
            ClusterPod.Failed => PodState.Unhealthy,
            ClusterPod.Pending => age > PendingGrace ? PodState.Unhealthy : PodState.Starting,
            _ when pod.Ready => PodState.Ready,
            ClusterPod.Running => age < StartingGrace ? PodState.Starting : PodState.Unhealthy,
            _ => PodState.Unhealthy,
        };
    }
}

/// <summary>A state of the cluster view with its words: the badge's label, the headline and what stands under it.</summary>
/// <param name="Label">The word of the state's badge.</param>
/// <param name="Headline">One sentence, without its full stop.</param>
/// <param name="Detail">What stands under the headline, in whole sentences; null without any.</param>
public sealed record ClusterAssessment(ClusterState State, string Label, string Headline, string? Detail = null)
{
    /// <summary>How many problems a headline names before it counts the rest.</summary>
    public const int NamedProblems = 3;

    /// <summary>Whether the cluster's own status reaches the page, at the page's clock.</summary>
    public static ClusterLiveness LivenessOf(SourceReading<ClusterStatus> reading, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reading);
        return reading.State switch
        {
            SourceState.Pending => ClusterLiveness.Pending,
            SourceState.Read when reading.Value is { } status => status.IsStale(now) ? ClusterLiveness.Stale : ClusterLiveness.Live,
            SourceState.Malformed => ClusterLiveness.Unreadable,
            _ => ClusterLiveness.Silent,
        };
    }

    /// <summary>
    /// The state of the cluster as it reports itself, next to what Azure says where the two can differ:
    /// healthy when every node is ready under no pressure and every pod that is not finished is ready; starting while
    /// the only pods that are not ready are starting; unhealthy otherwise, with what is wrong. A file the collector
    /// wrote too long ago is stale. A file that does not answer is the cluster stopped when Azure says so, and
    /// unreachable otherwise.
    /// </summary>
    /// <param name="service">Azure's facts about the AKS service; null when the topology names no address for them.</param>
    public static ClusterAssessment Live(SourceReading<ClusterStatus> reading, SourceReading<AksService>? service, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(reading);
        switch (LivenessOf(reading, now))
        {
            case ClusterLiveness.Pending:
                return new ClusterAssessment(ClusterState.Checking, "Checking", "Reading the cluster's status file");
            case ClusterLiveness.Unreadable:
                return new ClusterAssessment(
                    ClusterState.Unhealthy,
                    "Unreadable",
                    "The cluster's status file could not be read",
                    Join(Sentence(reading.Detail), AzureReports(service, now, zone)));
            case ClusterLiveness.Silent when service is { State: SourceState.Read, Value: { IsStopped: true } stopped }:
                return new ClusterAssessment(
                    ClusterState.Neutral,
                    "Stopped",
                    "The cluster is stopped",
                    Join(
                        "The pods and the pages served from inside the cluster do not run while it is stopped.",
                        stopped.Generated is { } read ? $"Azure reports the power state Stopped, {ClusterText.AsOf(read, now, zone)}." : null));
            case ClusterLiveness.Silent:
                return new ClusterAssessment(
                    ClusterState.Unreachable,
                    "Unreachable",
                    "The cluster's status file does not answer",
                    Join(Sentence(reading.Detail), AzureReports(service, now, zone)));
        }

        var status = reading.Value!;
        if (status.IsStale(now) && status.Generated is { } generated)
        {
            return new ClusterAssessment(
                ClusterState.Unreachable,
                "Stale",
                $"The collector in the cluster last wrote {TimeText.Clock(generated, zone)} ({TimeText.Ago(generated, now)})",
                Join("What is shown below is as of then.", AzureReports(service, now, zone)));
        }

        var asOf = status.AsOf(now);
        var problems = Problems(status, asOf);
        if (problems.Count > 0)
        {
            return new ClusterAssessment(ClusterState.Unhealthy, "Unhealthy", Named(problems));
        }

        var starting = status.Pods
            .Where(entry => PodRules.StateOf(entry.Pod, asOf) == PodState.Starting)
            .Select(entry => ClusterText.PodIn(entry.Pod, entry.Namespace.Name))
            .ToList();
        return starting.Count switch
        {
            0 => new ClusterAssessment(ClusterState.Healthy, "Healthy", "Every node is ready and under no pressure, and every pod is ready"),
            1 => new ClusterAssessment(ClusterState.Checking, "Starting", $"1 pod is starting: {starting[0]}"),
            var count => new ClusterAssessment(
                ClusterState.Checking,
                "Starting",
                string.Create(CultureInfo.InvariantCulture, $"{count} pods are starting: {Named(starting)}")),
        };
    }

    /// <summary>
    /// The state of the AKS service as Azure reports it: healthy when it is Available, Running and Succeeded; neutral
    /// when it is stopped or Azure has no verdict; a warning when it is Degraded or being changed; unhealthy when it
    /// is Unavailable. A file that is not published yet is said calmly: it is no error of the page.
    /// </summary>
    public static ClusterAssessment Service(SourceReading<AksService> reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        const string NotRead = "Azure's facts about the AKS service could not be read";
        const string Again = "The page reads them again with every check.";
        switch (reading.State)
        {
            case SourceState.Pending:
                return new ClusterAssessment(ClusterState.Checking, "Checking", "Reading Azure's facts about the AKS service");
            case SourceState.Missing:
                return new ClusterAssessment(
                    ClusterState.Neutral,
                    "Not published",
                    "Azure's facts about the AKS service are not published yet",
                    $"A scheduled workflow publishes them about every ten minutes; until its first run the address answers HTTP 404. {Again}");
            case SourceState.Malformed:
                return new ClusterAssessment(ClusterState.Warning, "Unreadable", NotRead, Sentence(reading.Detail));
            case SourceState.Unavailable:
                return new ClusterAssessment(ClusterState.Neutral, "Not known", NotRead, Join(Sentence(reading.Detail), Again));
        }

        var service = reading.Value!;
        var verdict = service.Availability?.State;
        var summary = service.Availability?.Summary;
        if (service.IsStopped)
        {
            return new ClusterAssessment(ClusterState.Neutral, "Stopped", "The AKS service is stopped", summary);
        }

        if (AksService.Is(verdict, AksAvailability.Unavailable))
        {
            return new ClusterAssessment(ClusterState.Unhealthy, "Unavailable", "Azure reports the AKS service unavailable", summary);
        }

        if (AksService.Is(verdict, AksAvailability.Degraded))
        {
            return new ClusterAssessment(ClusterState.Warning, "Degraded", "Azure reports the AKS service degraded", summary);
        }

        if (service.ProvisioningState is { } provisioning && !AksService.Is(provisioning, AksService.Succeeded))
        {
            return new ClusterAssessment(
                ClusterState.Warning,
                provisioning,
                $"The provisioning state of the AKS service is {provisioning}, not {AksService.Succeeded}",
                summary);
        }

        if (verdict is null || AksService.Is(verdict, AksAvailability.Unknown))
        {
            return new ClusterAssessment(ClusterState.Neutral, "Unknown", "Azure Resource Health has no verdict on the AKS service at the moment", summary);
        }

        return AksService.Is(verdict, AksAvailability.Available)
            ? new ClusterAssessment(ClusterState.Healthy, "Available", "Azure reports the AKS service available and running", summary)
            : new ClusterAssessment(ClusterState.Neutral, verdict, $"Azure reports the AKS service {verdict}", summary);
    }

    /// <summary>
    /// What is wrong in the cluster, most severe first: nodes that are not ready, nodes under pressure, failed pods,
    /// running pods that are not ready (the most restarts first), pods pending for too long, then any other.
    /// </summary>
    public static IReadOnlyList<string> Problems(ClusterStatus status, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(status);
        var problems = new List<string>();
        if (status.Nodes.Count == 0)
        {
            problems.Add("The status file lists no node");
        }

        problems.AddRange(status.Nodes.Where(node => !node.Ready).Select(node => $"Node {ClusterText.ShortNode(node.Name)} is not ready"));
        problems.AddRange(status.Nodes
            .Where(node => node.Ready && node.Pressures.Count > 0)
            .Select(node => $"Node {ClusterText.ShortNode(node.Name)}: {string.Join(", ", node.Pressures.Select(ClusterText.Pressure))}"));
        problems.AddRange(status.Pods
            .Where(entry => PodRules.StateOf(entry.Pod, asOf) == PodState.Unhealthy)
            .OrderBy(entry => Severity(entry.Pod))
            .ThenByDescending(entry => entry.Pod.Restarts)
            .Select(entry => PodProblem(entry.Pod, entry.Namespace.Name, asOf)));
        return problems;
    }

    /// <summary><c>ui in cmdemo3-tdd: CrashLoopBackOff, 7 restarts</c>.</summary>
    public static string PodProblem(ClusterPod pod, string space, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(pod);
        var words = ClusterText.PodWords(pod, PodState.Unhealthy, asOf);
        var text = $"{ClusterText.PodIn(pod, space)}: {(pod.Phase == ClusterPod.Running && pod.Reason is null ? "not ready" : words)}";
        return pod.Restarts > 0 ? $"{text}, {ClusterText.Restarts(pod.Restarts)}" : text;
    }

    /// <summary>The first <see cref="NamedProblems"/> of a list, then <c>and N more</c>.</summary>
    public static string Named(IReadOnlyList<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var named = string.Join("; ", items.Take(NamedProblems));
        return items.Count > NamedProblems
            ? string.Create(CultureInfo.InvariantCulture, $"{named}; and {items.Count - NamedProblems} more")
            : named;
    }

    /// <summary>
    /// What Azure says, for the place where the cluster itself says nothing: <c>Azure reports the AKS service Available
    /// and Running, as of 15:10:04 (5 min ago).</c> Null when the topology names no address for Azure's facts, and
    /// before they were read.
    /// </summary>
    public static string? AzureReports(SourceReading<AksService>? service, DateTimeOffset now, TimeZoneInfo zone)
    {
        switch (service?.State)
        {
            case null or SourceState.Pending:
                return null;
            case SourceState.Missing:
                return "Azure's facts about the AKS service are not published yet.";
            case SourceState.Read when service.Value is { } facts:
                var states = new[] { facts.Availability?.State, facts.PowerState }.OfType<string>().ToList();
                var said = states.Count > 0 ? string.Join(" and ", states) : facts.ProvisioningState;
                var asOf = facts.Generated is { } generated ? $", {ClusterText.AsOf(generated, now, zone)}" : string.Empty;
                return said is null ? null : $"Azure reports the AKS service {said}{asOf}.";
            default:
                return "Azure's facts about the AKS service could not be read either.";
        }
    }

    /// <summary>A reading's reason (the end of a sentence) as a sentence of its own.</summary>
    internal static string? Sentence(string? detail) =>
        string.IsNullOrWhiteSpace(detail) ? null : $"{char.ToUpperInvariant(detail[0])}{detail[1..].TrimEnd('.')}.";

    private static string? Join(params string?[] sentences)
    {
        var said = sentences.OfType<string>().ToList();
        return said.Count == 0 ? null : string.Join(' ', said);
    }

    private static int Severity(ClusterPod pod) => pod.Phase switch
    {
        ClusterPod.Failed => 0,
        ClusterPod.Running => 1,
        ClusterPod.Pending => 2,
        _ => 3,
    };
}
