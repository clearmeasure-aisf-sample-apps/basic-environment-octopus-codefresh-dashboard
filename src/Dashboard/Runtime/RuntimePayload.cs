using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dashboard.Runtime;

/// <summary>
/// What <c>js/runtime.js</c> draws into the diagram on one update: one tile per node, one mark per region and one per
/// relationship, keyed by the manifest's aliases and ids. The C# side decides every word and every state; the script
/// only draws. Serialized with <see cref="RuntimePayloadJson"/> (camelCase, nulls left out).
/// </summary>
public sealed record RuntimePayload(
    IReadOnlyList<RuntimeTile> Nodes,
    IReadOnlyList<RuntimeRegionMark> Regions,
    IReadOnlyList<RuntimeEdgeMark> Edges)
{
    public string ToJson() => JsonSerializer.Serialize(this, RuntimePayloadJson.Default.RuntimePayload);
}

/// <summary>The tile drawn into a node's slot, and the look of its box.</summary>
/// <param name="Alias">The node's alias.</param>
/// <param name="State"><c>healthy</c>, <c>unhealthy</c>, <c>unreachable</c>, <c>checking</c> or <c>neutral</c> (not probed).</param>
/// <param name="Label">The badge's word: the state, never colour alone.</param>
/// <param name="Facts">Next to the badge: HTTP status and latency, or why there is none.</param>
/// <param name="Lines">The lines under the badge, top to bottom.</param>
/// <param name="History">The last checks, oldest first, as states; null for a node that is not checked.</param>
/// <param name="Title">The tooltip of the whole node.</param>
/// <param name="Link">Where the badge leads (Live Metrics); null without a link.</param>
/// <param name="NameLink">Where the node's name leads (the resource in the Azure portal); null without a link.</param>
public sealed record RuntimeTile(
    string Alias,
    string State,
    string Label,
    string? Facts,
    IReadOnlyList<RuntimeTileLine> Lines,
    IReadOnlyList<string>? History,
    string Title,
    RuntimeLink? Link = null,
    RuntimeLink? NameLink = null);

/// <summary>A link the script draws as a real <c>a</c> element: it opens a new tab.</summary>
/// <param name="Href">The address.</param>
/// <param name="Title">Where it goes, and that the destination asks for a sign-in.</param>
public sealed record RuntimeLink(string Href, string Title)
{
    public static RuntimeLink? To(Uri? address, string title) => address is null ? null : new RuntimeLink(address.AbsoluteUri, title);
}

/// <summary>One piece of a line: words, and where they lead when they are a link.</summary>
public sealed record RuntimeTextPart(string Text, RuntimeLink? Link = null);

/// <summary>A sparkline next to a number: heights from 0 to 1, oldest first, and the same in words.</summary>
public sealed record RuntimeTrend(IReadOnlyList<double> Points, string Title)
{
    public static RuntimeTrend? Of(Health.Trend? trend) => trend is null ? null : new RuntimeTrend(trend.Points, trend.Title);
}

/// <param name="Text">The words.</param>
/// <param name="Tone">
/// <c>strong</c> (the running version), <c>plain</c>, <c>muted</c>, <c>serving</c>, and for the comparison with the pinned
/// version <c>insync</c>, <c>differs</c> or <c>unknown</c> (drawn with the dashboard's =, ≠ and dots).
/// </param>
/// <param name="Parts">
/// The same words in pieces, where a piece is a link; null for a line without links, which is drawn from
/// <paramref name="Text"/>.
/// </param>
/// <param name="Trend">The sparkline after the words; null without one.</param>
public sealed record RuntimeTileLine(string Text, string Tone = "plain", IReadOnlyList<RuntimeTextPart>? Parts = null, RuntimeTrend? Trend = null)
{
    /// <summary>A line of pieces: its text is the pieces in a row, and the pieces are kept only when one is a link.</summary>
    public static RuntimeTileLine Of(string tone, IReadOnlyList<RuntimeTextPart> parts, RuntimeTrend? trend = null)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return new RuntimeTileLine(string.Concat(parts.Select(part => part.Text)), tone, parts.Any(part => part.Link is not null) ? parts : null, trend);
    }
}

/// <param name="State"><c>serving</c>, <c>standby</c>, <c>down</c>, <c>checking</c> or <c>neutral</c>.</param>
/// <param name="Label">The words of the region's mark.</param>
public sealed record RuntimeRegionMark(string Alias, string State, string Label);

/// <param name="Id">The relationship's id.</param>
/// <param name="State"><c>active</c> (carries the traffic), <c>idle</c>, <c>down</c>, <c>checking</c> or <c>neutral</c>.</param>
/// <param name="Number">The number of the number line: null for a relationship without one (no slot); a dash until a source of calls per minute exists.</param>
/// <param name="Unit">The unit after the number.</param>
/// <param name="Text">The role of the relationship, under the number.</param>
/// <param name="Title">The tooltip.</param>
/// <param name="Link">Where the number leads; null without a link.</param>
/// <param name="Trend">The sparkline next to the number; null without one.</param>
public sealed record RuntimeEdgeMark(
    string Id,
    string State,
    string? Number,
    string? Unit,
    string? Text,
    string Title,
    RuntimeLink? Link = null,
    RuntimeTrend? Trend = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RuntimePayload))]
public sealed partial class RuntimePayloadJson : JsonSerializerContext;
