using System.Globalization;
using System.Text.Json;

namespace Dashboard.Health;

/// <summary>
/// How the system is delivered: the content of the file at <c>system.deliveryUrl</c> of the topology
/// (<c>delivery.json</c>, which a workflow of the system repository publishes). Per environment and deployable the
/// last deployment, and for the system the last failover test. Every part is optional.
/// </summary>
/// <param name="Generated">When the file's content last changed: the facts are as of then. It is not the time of a check.</param>
public sealed record DeliveryReport(DateTimeOffset? Generated, IReadOnlyList<DeliveryEnvironment> Environments, FailoverTest? Failover)
{
    /// <summary>The entry of a deployable in an environment; null when the file has none.</summary>
    public DeliveryEntry? Find(string environment, string deployable) =>
        Environments
            .FirstOrDefault(entry => string.Equals(entry.Name, environment, StringComparison.OrdinalIgnoreCase))
            ?.Deployables.FirstOrDefault(entry => string.Equals(entry.Name, deployable, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The entries of an environment that belong to no deployable the page shows: the system project itself (named
    /// <see cref="SystemName"/>: the infrastructure and the pipeline) first, then the others in the file's order, such
    /// as the dashboard.
    /// </summary>
    public IReadOnlyList<DeliveryEntry> Others(string environment, IEnumerable<string> shown)
    {
        ArgumentNullException.ThrowIfNull(shown);
        var names = shown.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = Environments
            .FirstOrDefault(entry => string.Equals(entry.Name, environment, StringComparison.OrdinalIgnoreCase))
            ?.Deployables.Where(entry => !names.Contains(entry.Name)) ?? [];
        return [.. entries.OrderBy(entry => IsSystem(entry.Name) ? 0 : 1)];
    }

    /// <summary>The name of the entry of the system project itself.</summary>
    public const string SystemName = "system";

    public static bool IsSystem(string name) => string.Equals(name, SystemName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The file's content; null when it is not a JSON object with <c>environments</c> or <c>failover</c>.</summary>
    public static DeliveryReport? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var environments = JsonRead.Items(root, "environments")
                .Select(environment => (Name: JsonRead.Text(environment, "name"), Element: environment))
                .Where(environment => environment.Name is not null)
                .Select(environment => new DeliveryEnvironment(
                    environment.Name!,
                    [.. JsonRead.Items(environment.Element, "deployables").Select(DeliveryEntry.Read).OfType<DeliveryEntry>()]))
                .ToList();
            var failover = FailoverTest.Read(JsonRead.Section(root, "failover"));
            return environments.Count == 0 && failover is null ? null : new DeliveryReport(JsonRead.Time(root, "generated"), environments, failover);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record DeliveryEnvironment(string Name, IReadOnlyList<DeliveryEntry> Deployables);

/// <summary>The last deployment of a deployable to an environment.</summary>
/// <param name="Version">The version it deployed.</param>
/// <param name="DeployedAt">When it ended.</param>
/// <param name="SignedOffBy">Who signed it off.</param>
/// <param name="Reason">The reason given with the sign-off.</param>
/// <param name="Commit">The commit of the version.</param>
/// <param name="CommitAt">When that commit was made.</param>
/// <param name="LeadTimeHours">From the commit to this deployment.</param>
/// <param name="Behind">How far the environment is behind the first one.</param>
/// <param name="DeploymentsLast7Days">Deployments of the deployable to the environment in the last seven days.</param>
/// <param name="FailedLast7Days">Of those, the failed ones.</param>
/// <param name="ReleaseUrl">The release's page.</param>
public sealed record DeliveryEntry(
    string Name,
    string? Version,
    DateTimeOffset? DeployedAt,
    string? SignedOffBy,
    string? Reason,
    string? Commit,
    DateTimeOffset? CommitAt,
    double? LeadTimeHours,
    BehindFirst? Behind,
    int? DeploymentsLast7Days,
    int? FailedLast7Days,
    Uri? ReleaseUrl)
{
    internal static DeliveryEntry? Read(JsonElement element)
    {
        if (JsonRead.Text(element, "name") is not { } name)
        {
            return null;
        }

        var behind = JsonRead.Section(element, "behindFirst");
        return new DeliveryEntry(
            name,
            VersionText.Display(JsonRead.Text(element, "version")),
            JsonRead.Time(element, "deployedAt"),
            JsonRead.Text(element, "signedOffBy"),
            JsonRead.Text(element, "reason"),
            JsonRead.Text(element, "commit"),
            JsonRead.Time(element, "commitAt"),
            JsonRead.Number(element, "leadTimeHours"),
            behind is null ? null : new BehindFirst(JsonRead.Count(behind, "versions"), JsonRead.Number(behind, "days")),
            JsonRead.Count(element, "deploymentsLast7Days"),
            JsonRead.Count(element, "failedLast7Days"),
            JsonRead.Address(element, "releaseUrl"));
    }
}

/// <param name="Versions">How many releases the first environment is ahead, in the project's list of releases.</param>
/// <param name="Days">
/// Days since this environment and the first one last ran the same release; null when they never did.
/// </param>
public sealed record BehindFirst(int? Versions, double? Days);

/// <summary>The last test of the failover to the standby region.</summary>
/// <param name="Seconds">How long the public address took to answer from the standby.</param>
public sealed record FailoverTest(string? Environment, DateTimeOffset? At, double? Seconds)
{
    internal static FailoverTest? Read(JsonElement? section)
    {
        var test = new FailoverTest(JsonRead.Text(section, "environment"), JsonRead.Time(section, "at"), JsonRead.Number(section, "seconds"));
        return test == new FailoverTest(null, null, null) ? null : test;
    }
}

/// <summary>The delivery facts in the words of the "Delivery" card.</summary>
public static class DeliveryText
{
    /// <summary><c>5.2 h</c>; under an hour <c>40 min</c>; from two days on <c>3.5 d</c>.</summary>
    public static string LeadTime(double hours) =>
        hours < 1 ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(hours * 60):0} min")
        : hours < 48 ? string.Create(CultureInfo.InvariantCulture, $"{hours:0.#} h")
        : string.Create(CultureInfo.InvariantCulture, $"{hours / 24:0.#} d");

    /// <summary>Whose delivery a card shows: <c>ui in uat</c>; the system project by what it is.</summary>
    public static string Context(string name, string environment) =>
        DeliveryReport.IsSystem(name) ? $"the system (infrastructure and pipeline) in {environment}" : $"{name} in {environment}";

    /// <summary>What "behind" counts, for the tooltip.</summary>
    public static string BehindHelp(string first) =>
        $"Versions: the distance in the project's list of releases. Days: since this environment and {first} last ran the same release.";

    /// <summary>
    /// <c>same as tdd</c>, <c>2 versions, 3 days behind tdd</c>; null for the first environment itself and when the
    /// file does not say.
    /// </summary>
    public static string? Behind(BehindFirst? behind, string environment, string? first)
    {
        if (behind is null || first is null || string.Equals(environment, first, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (behind.Versions == 0)
        {
            return $"same as {first}";
        }

        var parts = new List<string>();
        if (behind.Versions is { } versions && versions > 0)
        {
            parts.Add(versions == 1 ? "1 version" : string.Create(CultureInfo.InvariantCulture, $"{versions} versions"));
        }

        if (behind.Days is { } days && Math.Round(days) >= 1)
        {
            var whole = (int)Math.Round(days);
            parts.Add(whole == 1 ? "1 day" : string.Create(CultureInfo.InvariantCulture, $"{whole} days"));
        }

        return parts.Count > 0 ? $"{string.Join(", ", parts)} behind {first}" : behind.Days is not null ? $"same as {first}" : null;
    }

    /// <summary><c>4 deployments, none failed</c>; null when the file does not count them.</summary>
    public static string? Frequency(DeliveryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.DeploymentsLast7Days is not { } count)
        {
            return null;
        }

        var deployments = count == 1 ? "1 deployment" : string.Create(CultureInfo.InvariantCulture, $"{count} deployments");
        return entry.FailedLast7Days switch
        {
            null => deployments,
            0 => count == 0 ? deployments : $"{deployments}, none failed",
            var failed => string.Create(CultureInfo.InvariantCulture, $"{deployments}, {failed} failed"),
        };
    }

    /// <summary><c>uat, 17 h ago: traffic moved to the standby in 44 s</c>.</summary>
    public static string Failover(FailoverTest test, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(test);
        var parts = new List<string>();
        if (test.Environment is { } environment)
        {
            parts.Add(environment);
        }

        if (test.At is { } at)
        {
            parts.Add(TimeText.Ago(at, now));
        }

        var text = string.Join(", ", parts);
        if (test.Seconds is { } seconds)
        {
            var took = string.Create(CultureInfo.InvariantCulture, $"the standby answered after {seconds:0.#} s");
            text = text.Length > 0 ? $"{text}: {took}" : took;
        }

        return text;
    }
}
