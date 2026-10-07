using System.Net;
using System.Text.Json;

namespace Dashboard.Health;

public enum PinnedVersionsState
{
    /// <summary>The topology names no file with the pin: versions are not compared.</summary>
    NotTracked,

    /// <summary>The file has not been read yet.</summary>
    Pending,

    /// <summary>The file was read.</summary>
    Read,

    /// <summary>
    /// The file was not found (HTTP 404): nothing has been deployed to the environment, or the repository is not public.
    /// </summary>
    Missing,

    /// <summary>
    /// The file could not be read: no answer, another HTTP status, or content that is not what the file holds (a JSON
    /// object; for a Kustomize file, a <c>newTag</c>).
    /// </summary>
    Unavailable,
}

/// <summary>
/// The versions the deployments pinned in Git for one environment: the content of the system repository's
/// <c>environments/&lt;environment&gt;/versions.json</c>, a JSON object <c>{ "&lt;deployable&gt;": "&lt;version&gt;" }</c>.
/// A deployable whose pin is a Kustomize file of its own (<c>pinUrl</c> of the topology) has a reading of its own, with
/// its one version: see <see cref="ParseKustomization"/>.
/// </summary>
/// <param name="State">Whether the file was read.</param>
/// <param name="Versions">The version by deployable name, without build metadata; empty unless the file was read.</param>
/// <param name="Detail">Why the file could not be read, as the end of a sentence.</param>
public sealed record PinnedVersions(PinnedVersionsState State, IReadOnlyDictionary<string, string> Versions, string? Detail = null)
{
    public const string FileName = "versions.json";

    /// <summary>What a Kustomize file is called when its address names no file.</summary>
    public const string KustomizationFileName = "kustomization.yaml";

    private const string NewTag = "newTag";

    private static readonly Dictionary<string, string> None = new(StringComparer.Ordinal);

    public static PinnedVersions NotTracked { get; } = new(PinnedVersionsState.NotTracked, None);

    public static PinnedVersions Pending { get; } = new(PinnedVersionsState.Pending, None);

    public static PinnedVersions Missing { get; } = new(PinnedVersionsState.Missing, None);

    public static PinnedVersions Unavailable(string detail) => new(PinnedVersionsState.Unavailable, None, detail);

    /// <summary>The version pinned for a deployable; null when the file was not read or has no entry for it.</summary>
    public string? Of(string deployable) => Versions.GetValueOrDefault(deployable);

    /// <summary>
    /// Reads the content of <c>versions.json</c>. An entry whose value is not a version (not text, or empty) is no
    /// entry: the deployable then counts as not deployed.
    /// </summary>
    public static PinnedVersions Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Unavailable("the file is empty");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Unavailable("the file does not contain a JSON object");
            }

            var versions = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String
                    && VersionText.Display(property.Value.GetString()) is { } version)
                {
                    versions[property.Name] = version;
                }
            }

            return new PinnedVersions(PinnedVersionsState.Read, versions);
        }
        catch (JsonException)
        {
            return Unavailable("the file is not valid JSON");
        }
    }

    /// <summary>
    /// Reads a Kustomize file that pins one deployable: the pinned version is the value of its first <c>newTag</c>
    /// entry (<c>images: [ { name, newTag } ]</c>, in block style), in double or single quotes or without, a trailing
    /// comment left out. The result holds that one version under the deployable's name, so it is assessed and compared
    /// like a reading of <c>versions.json</c>. A file without such an entry is not a pin: Unavailable, with the reason.
    /// </summary>
    public static PinnedVersions ParseKustomization(string? yaml, string deployable)
    {
        ArgumentNullException.ThrowIfNull(deployable);
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return Unavailable("the file is empty");
        }

        foreach (var line in yaml.Split('\n'))
        {
            if (NewTagValue(line) is { } value)
            {
                return VersionText.Display(value) is { } version
                    ? new PinnedVersions(PinnedVersionsState.Read, new Dictionary<string, string>(StringComparer.Ordinal) { [deployable] = version })
                    : Unavailable($"the first {NewTag} entry of the file has no value");
            }
        }

        return Unavailable($"the file has no {NewTag} entry");
    }

    /// <summary>The name of the file at an address: the last part of its path.</summary>
    public static string FileOf(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var path = Uri.UnescapeDataString(address.AbsolutePath).TrimEnd('/');
        var name = path[(path.LastIndexOf('/') + 1)..];
        return name.Length == 0 ? KustomizationFileName : name;
    }

    /// <summary>
    /// The value of a line that is a <c>newTag</c> entry, empty when it has none; null for any other line (a comment,
    /// another key).
    /// </summary>
    private static string? NewTagValue(string line)
    {
        var text = line.AsSpan().Trim();

        // The first key of a list item: "- newTag: 2.4.15".
        if (text.Length > 1 && text[0] == '-' && char.IsWhiteSpace(text[1]))
        {
            text = text[1..].TrimStart();
        }

        if (!text.StartsWith(NewTag, StringComparison.Ordinal))
        {
            return null;
        }

        text = text[NewTag.Length..].TrimStart();
        if (text.IsEmpty || text[0] != ':' || (text.Length > 1 && !char.IsWhiteSpace(text[1])))
        {
            return null;
        }

        text = text[1..].Trim();
        if (text.IsEmpty || text[0] == '#')
        {
            return string.Empty;
        }

        if (text[0] is '"' or '\'')
        {
            var rest = text[1..];
            var end = rest.IndexOf(text[0]);
            return (end < 0 ? rest : rest[..end]).ToString();
        }

        // Without quotes, a comment starts at a "#" that follows a space.
        for (var index = 1; index < text.Length; index++)
        {
            if (text[index] == '#' && char.IsWhiteSpace(text[index - 1]))
            {
                text = text[..index].TrimEnd();
                break;
            }
        }

        return text.ToString();
    }
}

/// <summary>
/// Reads the versions pinned in Git from the addresses the topology gives: an environment's <c>versions.json</c>, or
/// the Kustomize file of one deployable.
/// </summary>
public sealed class PinnedVersionsReader(HttpClient http, TimeProvider time)
{
    /// <summary>How long the answer may take: as long as a node's.</summary>
    public TimeSpan Timeout { get; init; } = NodeProber.DefaultTimeout;

    /// <summary>
    /// Never throws for a file that cannot be read: that is the result Missing or Unavailable. Only the caller's
    /// cancellation throws.
    /// </summary>
    public Task<PinnedVersions> ReadAsync(Uri address, CancellationToken cancellationToken) =>
        ReadAsync(address, PinnedVersions.Parse, cancellationToken);

    /// <summary>
    /// Reads the pin of one deployable from its Kustomize file (<c>pinUrl</c> of the topology): the same request and
    /// the same outcomes as <see cref="ReadAsync(Uri, CancellationToken)"/>, with a file without <c>newTag</c> as
    /// Unavailable.
    /// </summary>
    public Task<PinnedVersions> ReadKustomizationAsync(Uri address, string deployable, CancellationToken cancellationToken) =>
        ReadAsync(address, body => PinnedVersions.ParseKustomization(body, deployable), cancellationToken);

    private async Task<PinnedVersions> ReadAsync(Uri address, Func<string, PinnedVersions> parse, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(Timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var request = NodeProber.NewRequest(address);
            using var response = await http.SendAsync(request, linked.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return PinnedVersions.Missing;
            }

            return response.IsSuccessStatusCode
                ? parse(await response.Content.ReadAsStringAsync(linked.Token))
                : PinnedVersions.Unavailable($"the server answered HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return PinnedVersions.Unavailable($"no answer within {Timeout.TotalSeconds:0} s");
        }
        catch (HttpRequestException)
        {
            return PinnedVersions.Unavailable("the browser could not read an answer (network or CORS)");
        }
    }
}
