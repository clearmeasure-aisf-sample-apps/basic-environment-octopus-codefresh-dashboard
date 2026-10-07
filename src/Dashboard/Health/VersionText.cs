using System.Reflection;
using System.Text.Json;

namespace Dashboard.Health;

/// <summary>Versions as the dashboard shows them: <c>2.4.21+abc123</c> becomes <c>2.4.21</c>.</summary>
public static class VersionText
{
    /// <summary>The properties of a version answer that carry the version, in the order they are tried.</summary>
    private static readonly string[] VersionProperties = ["version", "informationalVersion"];

    /// <summary>The part of a version before the build metadata (<c>+</c>); null when nothing is left.</summary>
    public static string? Display(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var plus = version.IndexOf('+', StringComparison.Ordinal);
        var text = (plus >= 0 ? version[..plus] : version).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// Reads the body of a version endpoint: JSON <c>{"version":"2.4.21+sha"}</c>, or, for an app that answers with its
    /// assembly's versions, <c>{"informationalVersion":"2.5.772+sha", ...}</c>. <c>version</c> wins when both are there.
    /// </summary>
    public static string? FromVersionResponse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in VersionProperties)
                {
                    foreach (var property in root.EnumerateObject())
                    {
                        if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                            && property.Value.ValueKind == JsonValueKind.String)
                        {
                            return Display(property.Value.GetString());
                        }
                    }
                }
            }

            return root.ValueKind == JsonValueKind.String ? Display(root.GetString()) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The version of the dashboard itself: the assembly's informational version, set by <c>-p:Version</c>.</summary>
    public static string OfAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return Display(informational) ?? "unknown";
    }
}
