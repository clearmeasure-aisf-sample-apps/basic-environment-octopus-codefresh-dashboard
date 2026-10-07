using System.Net;
using Dashboard.Health;

namespace Dashboard.Runtime;

public enum RuntimeAvailability
{
    /// <summary>The deployment rendered the diagrams.</summary>
    Available,

    /// <summary>
    /// The site has no <c>runtime/</c>: a deployment from before the runtime view, or <c>dotnet run</c> without the
    /// sample. Not an error.
    /// </summary>
    NotDeployed,

    /// <summary>The files are there and cannot be read.</summary>
    Failed,
}

/// <summary>The outcome of loading <c>runtime/index.json</c>.</summary>
public sealed record RuntimeIndexResult(RuntimeAvailability Availability, RuntimeIndex? Index, IReadOnlyList<string> Errors)
{
    public static RuntimeIndexResult NotDeployed { get; } = new(RuntimeAvailability.NotDeployed, null, []);
}

/// <summary>One environment's diagram: its manifest and the SVG as text, or the reasons there is none.</summary>
public sealed record RuntimeDiagram(string Environment, RuntimeManifest? Manifest, string? Svg, IReadOnlyList<string> Errors)
{
    public bool IsValid => Manifest is not null && Svg is not null;
}

/// <summary>Loads the files of <c>runtime/</c> from the dashboard's own address, as it loads <c>topology.json</c>.</summary>
public sealed class RuntimeLoader(HttpClient http)
{
    public const string Folder = "runtime/";
    public const string IndexFile = "index.json";

    public async Task<RuntimeIndexResult> LoadIndexAsync(CancellationToken cancellationToken)
    {
        var (status, text, error) = await GetAsync(IndexFile, cancellationToken);
        // A host that answers every unknown address with the page itself (a fallback route) serves no JSON either.
        if (status == HttpStatusCode.NotFound || (status == HttpStatusCode.OK && text is not null && text.TrimStart().StartsWith('<')))
        {
            return RuntimeIndexResult.NotDeployed;
        }

        if (error is not null)
        {
            return new RuntimeIndexResult(RuntimeAvailability.Failed, null, [error]);
        }

        var parsed = RuntimeManifestParser.ParseIndex(text);
        return parsed.Value is { } index
            ? new RuntimeIndexResult(RuntimeAvailability.Available, index, [])
            : new RuntimeIndexResult(RuntimeAvailability.Failed, null, [.. parsed.Errors.Select(problem => $"{Folder}{IndexFile}: {problem}")]);
    }

    public async Task<RuntimeDiagram> LoadDiagramAsync(RuntimeIndexEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var manifestTask = GetAsync(entry.Manifest, cancellationToken);
        var svgTask = GetAsync(entry.Svg, cancellationToken);
        var (_, manifestText, manifestError) = await manifestTask;
        var (_, svg, svgError) = await svgTask;

        var errors = new List<string>();
        RuntimeManifest? manifest = null;
        if (manifestError is not null)
        {
            errors.Add(manifestError);
        }
        else
        {
            var parsed = RuntimeManifestParser.ParseManifest(manifestText);
            manifest = parsed.Value;
            errors.AddRange(parsed.Errors.Select(problem => $"{Folder}{entry.Manifest}: {problem}"));
        }

        if (svgError is not null)
        {
            errors.Add(svgError);
        }
        else if (svg is null || !svg.Contains("<svg", StringComparison.Ordinal))
        {
            errors.Add($"{Folder}{entry.Svg}: not an SVG.");
            svg = null;
        }

        return errors.Count > 0 ? new RuntimeDiagram(entry.Name, null, null, errors) : new RuntimeDiagram(entry.Name, manifest, svg, []);
    }

    /// <summary>The status and the text of a file of <c>runtime/</c>; an error text when it could not be read.</summary>
    private async Task<(HttpStatusCode? Status, string? Text, string? Error)> GetAsync(string file, CancellationToken cancellationToken)
    {
        try
        {
            using var request = NodeProber.NewRequest(new Uri(Folder + file, UriKind.Relative));
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (response.StatusCode, null, $"The server answered HTTP {(int)response.StatusCode} for {Folder}{file}.");
            }

            return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken), null);
        }
        catch (HttpRequestException exception)
        {
            return (null, null, $"{Folder}{file} could not be loaded: {exception.Message}");
        }
    }
}
