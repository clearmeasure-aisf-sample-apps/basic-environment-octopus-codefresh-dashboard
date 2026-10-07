using System.Text.Json;

namespace Dashboard.Runtime;

/// <summary>The outcome of reading a file of <c>runtime/</c>: the value, or the reasons there is none.</summary>
public sealed record RuntimeParseResult<T>(T? Value, IReadOnlyList<string> Errors)
    where T : class
{
    public bool IsValid => Value is not null;
}

/// <summary>
/// Reads <c>runtime/index.json</c> and <c>runtime/&lt;env&gt;.json</c>. The deployment writes both next to the SVG it
/// rendered (<c>deploy-staticwebapp.ps1</c>). Required: the index's environments with their name, manifest and SVG;
/// the manifest's nodes with alias and kind, the regions with alias, the relationships with id. Unknown fields and
/// kinds are kept apart, not refused: a newer diagram still shows.
/// </summary>
public static class RuntimeManifestParser
{
    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static RuntimeParseResult<RuntimeIndex> ParseIndex(string? json)
    {
        if (!TryOpen(json, out var document, out var problem))
        {
            return Failed<RuntimeIndex>(problem);
        }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("environments", out var array) || array.ValueKind != JsonValueKind.Array)
            {
                return Failed<RuntimeIndex>("environments: missing or not an array.");
            }

            var errors = new List<string>();
            var entries = new List<RuntimeIndexEntry>();
            var index = 0;
            foreach (var element in array.EnumerateArray())
            {
                var path = $"environments[{index++}]";
                var name = element.ValueKind == JsonValueKind.Object ? Text(element, "name") : null;
                if (name is null)
                {
                    errors.Add($"{path}.name: missing or empty.");
                    continue;
                }

                var manifest = RelativeFile(element, "manifest", $"{name}.json", path, errors);
                var svg = RelativeFile(element, "svg", $"{name}.svg", path, errors);
                entries.Add(new RuntimeIndexEntry(name, manifest, svg));
            }

            return errors.Count > 0
                ? new RuntimeParseResult<RuntimeIndex>(null, errors)
                : new RuntimeParseResult<RuntimeIndex>(new RuntimeIndex(entries, Text(root, "plantuml")), []);
        }
    }

    public static RuntimeParseResult<RuntimeManifest> ParseManifest(string? json)
    {
        if (!TryOpen(json, out var document, out var problem))
        {
            return Failed<RuntimeManifest>(problem);
        }

        using (document)
        {
            var root = document.RootElement;
            var errors = new List<string>();
            var environment = Text(root, "environment");
            if (environment is null)
            {
                errors.Add("environment: missing or empty.");
            }

            var nodes = ReadArray(root, "nodes", errors, (element, path) =>
            {
                var alias = Text(element, "alias");
                if (alias is null)
                {
                    errors.Add($"{path}.alias: missing or empty.");
                    return null;
                }

                var url = Address(element, "url");
                if (url is null && Text(element, "url") is not null)
                {
                    errors.Add($"{path}.url: not an absolute http or https address.");
                }

                return new RuntimeNode(
                    alias,
                    NodeKind(Text(element, "kind")),
                    Text(element, "name") ?? alias,
                    url,
                    Text(element, "deployable"),
                    Text(element, "role")?.ToLowerInvariant(),
                    Text(element, "region"),
                    Text(element, "regionAlias"));
            });
            var regions = ReadArray(root, "regions", errors, (element, path) =>
            {
                var alias = Text(element, "alias");
                if (alias is null)
                {
                    errors.Add($"{path}.alias: missing or empty.");
                    return null;
                }

                var roles = element.TryGetProperty("roles", out var array) && array.ValueKind == JsonValueKind.Array
                    ? array.EnumerateArray().Where(role => role.ValueKind == JsonValueKind.String).Select(role => role.GetString()!).ToList()
                    : [];
                return new RuntimeRegion(alias, Text(element, "name") ?? alias, roles);
            });
            var edges = ReadArray(root, "edges", errors, (element, path) =>
            {
                var id = Text(element, "id");
                var from = Text(element, "from");
                var to = Text(element, "to");
                if (id is null || from is null || to is null)
                {
                    errors.Add($"{path}: id, from and to are required.");
                    return null;
                }

                int? priority = element.TryGetProperty("priority", out var number) && number.TryGetInt32(out var value) ? value : null;
                return new RuntimeEdge(id, from, to, EdgeKind(Text(element, "kind")), priority);
            });

            return errors.Count > 0 || environment is null
                ? new RuntimeParseResult<RuntimeManifest>(null, errors)
                : new RuntimeParseResult<RuntimeManifest>(new RuntimeManifest(environment, nodes, regions, edges), []);
        }
    }

    private static RuntimeParseResult<T> Failed<T>(params string[] errors)
        where T : class => new(null, errors);

    private static bool TryOpen(string? json, out JsonDocument document, out string problem)
    {
        document = null!;
        problem = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            problem = "The file is empty.";
            return false;
        }

        try
        {
            document = JsonDocument.Parse(json, Options);
        }
        catch (JsonException exception)
        {
            problem = $"The file is not valid JSON (line {exception.LineNumber + 1}, position {exception.BytePositionInLine + 1}).";
            return false;
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            problem = "The file must contain a JSON object.";
            return false;
        }

        return true;
    }

    private static List<T> ReadArray<T>(JsonElement root, string property, List<string> errors, Func<JsonElement, string, T?> read)
        where T : class
    {
        var items = new List<T>();
        if (!root.TryGetProperty(property, out var array) || array.ValueKind == JsonValueKind.Null)
        {
            return items;
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{property}: not an array.");
            return items;
        }

        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            var path = $"{property}[{index++}]";
            if (element.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{path}: not an object.");
                continue;
            }

            if (read(element, path) is { } item)
            {
                items.Add(item);
            }
        }

        return items;
    }

    /// <summary>A file next to the index: a plain name, never an address or a path that leaves <c>runtime/</c>.</summary>
    private static string RelativeFile(JsonElement element, string property, string fallback, string path, List<string> errors)
    {
        var file = Text(element, property) ?? fallback;
        if (file.Contains('/', StringComparison.Ordinal) || file.Contains('\\', StringComparison.Ordinal) || file.Contains(':', StringComparison.Ordinal) || file.StartsWith('.'))
        {
            errors.Add($"{path}.{property}: not a file name in runtime/.");
        }

        return file;
    }

    private static string? Text(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static Uri? Address(JsonElement parent, string property) =>
        Text(parent, property) is { } text
        && Uri.TryCreate(text, UriKind.Absolute, out var address)
        && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps)
            ? address
            : null;

    private static RuntimeNodeKind NodeKind(string? kind) => kind?.ToLowerInvariant() switch
    {
        "person" => RuntimeNodeKind.Person,
        "frontdoor" => RuntimeNodeKind.FrontDoor,
        "webapp" => RuntimeNodeKind.WebApp,
        "sql" => RuntimeNodeKind.Sql,
        "staticsite" => RuntimeNodeKind.StaticSite,
        _ => RuntimeNodeKind.Other,
    };

    private static RuntimeEdgeKind EdgeKind(string? kind) => kind?.ToLowerInvariant() switch
    {
        "public" => RuntimeEdgeKind.Public,
        "origin" => RuntimeEdgeKind.Origin,
        "sql" => RuntimeEdgeKind.Sql,
        "dashboard" => RuntimeEdgeKind.Dashboard,
        _ => RuntimeEdgeKind.Other,
    };
}
