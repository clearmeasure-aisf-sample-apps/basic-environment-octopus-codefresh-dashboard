using System.Globalization;
using System.Text.Json;

namespace Dashboard.Health;

/// <summary>
/// Reads the optional parts of an answer the dashboard does not own (a node's telemetry and build facts, the delivery
/// file, the cluster's files): a part that is absent or of another type is null, never an error.
/// </summary>
internal static class JsonRead
{
    /// <summary>The object under <paramref name="name"/>; null when there is none.</summary>
    public static JsonElement? Section(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var section) && section.ValueKind == JsonValueKind.Object
            ? section
            : null;

    /// <summary>The elements of the array under <paramref name="name"/>; none when there is no array.</summary>
    public static IEnumerable<JsonElement> Items(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? [.. array.EnumerateArray()]
            : [];

    /// <summary>A number that is not negative; null for anything else.</summary>
    public static double? Number(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var number) && number.ValueKind == JsonValueKind.Number
            && number.TryGetDouble(out var real) && double.IsFinite(real) && real >= 0
            ? real
            : null;

    /// <summary>True or false; null for anything else.</summary>
    public static bool? Flag(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var flag)
            ? flag.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    /// <summary>A count: a number that is not negative, rounded.</summary>
    public static int? Count(JsonElement? parent, string name) =>
        Number(parent, name) is { } number && number <= int.MaxValue ? (int)Math.Round(number) : null;

    /// <summary>A large count, such as seconds or lines of code.</summary>
    public static long? Long(JsonElement? parent, string name) =>
        Number(parent, name) is { } number && number <= long.MaxValue / 2 ? (long)Math.Round(number) : null;

    /// <summary>Text that is not empty, trimmed.</summary>
    public static string? Text(JsonElement? parent, string name)
    {
        if (parent is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty(name, out var text) || text.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var trimmed = text.GetString()?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    public static DateTimeOffset? Time(JsonElement? parent, string name) =>
        DateTimeOffset.TryParse(Text(parent, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : null;

    /// <summary>An absolute http or https address: the only kind the dashboard links to.</summary>
    public static Uri? Address(JsonElement? parent, string name) => Address(Text(parent, name));

    public static Uri? Address(string? text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var address) && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps)
            ? address
            : null;
}
