using System.Text.Json;
using Dashboard.Health;

namespace Dashboard.Cluster;

/// <summary>
/// Azure's own facts about the AKS service: the content of the file at <c>cluster.serviceUrl</c> of the topology
/// (<c>aks.json</c>), which a scheduled workflow publishes about every ten minutes. Slow by design: what Azure says,
/// next to what the cluster says about itself (<see cref="ClusterStatus"/>). Every part is optional.
/// </summary>
/// <param name="Generated">When the workflow read the facts from Azure: they are as of then.</param>
/// <param name="Location">The Azure region.</param>
/// <param name="Availability">The verdict of Azure Resource Health.</param>
/// <param name="PowerState">Running or Stopped.</param>
/// <param name="ProvisioningState">Succeeded, or what Azure is doing to the service (Updating, Starting, Stopping, Failed).</param>
/// <param name="Tier">The pricing tier of the control plane.</param>
public sealed record AksService(
    DateTimeOffset? Generated,
    string? Name,
    string? ResourceGroup,
    string? Location,
    AksAvailability? Availability,
    string? PowerState,
    string? ProvisioningState,
    string? KubernetesVersion,
    string? Tier,
    IReadOnlyList<AksPool> Pools,
    AksMetrics? Metrics)
{
    public const string Running = "Running";
    public const string Stopped = "Stopped";
    public const string Succeeded = "Succeeded";

    /// <summary>The facts are old from this age on: the workflow that publishes them may not be running.</summary>
    public static readonly TimeSpan OldAfter = TimeSpan.FromMinutes(30);

    public bool IsStopped => Is(PowerState, Stopped);

    /// <summary>True when the workflow read the facts longer ago than <see cref="OldAfter"/>.</summary>
    public bool IsOld(DateTimeOffset now) => Generated is { } generated && now - generated > OldAfter;

    /// <summary>
    /// Reads the file. It must be a JSON object that says something about the service: its availability, its power
    /// state or its provisioning state. Unknown fields are ignored.
    /// </summary>
    public static Parsed<AksService> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Parsed<AksService>(null, "the file is empty");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new Parsed<AksService>(null, "the file does not contain a JSON object");
            }

            var service = new AksService(
                JsonRead.Time(root, "generated"),
                JsonRead.Text(root, "name"),
                JsonRead.Text(root, "resourceGroup"),
                JsonRead.Text(root, "location"),
                AksAvailability.Read(JsonRead.Section(root, "availability")),
                JsonRead.Text(root, "powerState"),
                JsonRead.Text(root, "provisioningState"),
                JsonRead.Text(root, "kubernetesVersion"),
                JsonRead.Text(root, "tier"),
                [.. JsonRead.Items(root, "pools").Select(AksPool.Read).OfType<AksPool>()],
                AksMetrics.Read(JsonRead.Section(root, "metrics")));
            return service is { Availability: null, PowerState: null, ProvisioningState: null }
                ? new Parsed<AksService>(null, "the file says nothing about the service: no availability, no powerState and no provisioningState")
                : new Parsed<AksService>(service);
        }
        catch (JsonException)
        {
            return new Parsed<AksService>(null, "the file is not valid JSON");
        }
    }

    internal static bool Is(string? value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The verdict of Azure Resource Health on the service.</summary>
/// <param name="State">Available, Unavailable, Degraded or Unknown.</param>
/// <param name="Summary">Azure's sentence about it.</param>
/// <param name="Reason">Why, where Azure says so.</param>
/// <param name="OccurredAt">Since when.</param>
public sealed record AksAvailability(string State, string? Summary, string? Reason, DateTimeOffset? OccurredAt)
{
    public const string Available = "Available";
    public const string Unavailable = "Unavailable";
    public const string Degraded = "Degraded";
    public const string Unknown = "Unknown";

    internal static AksAvailability? Read(JsonElement? section) =>
        JsonRead.Text(section, "state") is { } state
            ? new AksAvailability(state, JsonRead.Text(section, "summary"), JsonRead.Text(section, "reason"), JsonRead.Time(section, "occurredAt"))
            : null;
}

/// <summary>One node pool, as Azure describes it.</summary>
/// <param name="Mode">System or User.</param>
/// <param name="Count">How many nodes.</param>
/// <param name="Size">The size of their virtual machines.</param>
public sealed record AksPool(
    string Name,
    string? Mode,
    int? Count,
    string? Size,
    int? OsDiskGb,
    string? PowerState,
    string? ProvisioningState,
    string? KubernetesVersion)
{
    internal static AksPool? Read(JsonElement element) =>
        JsonRead.Text(element, "name") is { } name
            ? new AksPool(
                name,
                JsonRead.Text(element, "mode"),
                JsonRead.Count(element, "count"),
                JsonRead.Text(element, "size"),
                JsonRead.Count(element, "osDiskGb"),
                JsonRead.Text(element, "powerState"),
                JsonRead.Text(element, "provisioningState"),
                JsonRead.Text(element, "kubernetesVersion"))
            : null;
}

/// <summary>
/// Azure Monitor's numbers, each the average of the last <see cref="WindowMinutes"/> in percent: the nodes, and the
/// API server of the control plane. Each may be absent: Azure does not emit every metric for every cluster.
/// </summary>
public sealed record AksMetrics(
    int? WindowMinutes,
    double? NodeCpuPercent,
    double? NodeMemoryPercent,
    double? NodeDiskPercent,
    double? ApiServerCpuPercent = null,
    double? ApiServerMemoryPercent = null)
{
    /// <summary>True when Azure reports one number at least: without any, the card has no block of them.</summary>
    public bool HasAny => (NodeCpuPercent ?? NodeMemoryPercent ?? NodeDiskPercent ?? ApiServerCpuPercent ?? ApiServerMemoryPercent) is not null;

    internal static AksMetrics? Read(JsonElement? section) =>
        section is null
            ? null
            : new AksMetrics(
                JsonRead.Count(section, "windowMinutes"),
                JsonRead.Number(section, "nodeCpuPercent"),
                JsonRead.Number(section, "nodeMemoryPercent"),
                JsonRead.Number(section, "nodeDiskPercent"),
                JsonRead.Number(section, "apiServerCpuPercent"),
                JsonRead.Number(section, "apiServerMemoryPercent"));
}
