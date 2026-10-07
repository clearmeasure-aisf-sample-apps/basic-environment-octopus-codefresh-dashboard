using System.Globalization;

namespace Dashboard.Health;

/// <summary>
/// A sparkline: the last readings of one number, oldest first, each as its height between 0 (the bottom: zero) and 1
/// (the largest reading), with the same in words for readers who do not see it. The scale starts at zero, so a line
/// that stays low is a number that stays small.
/// </summary>
/// <param name="Points">One height per reading, 0 to 1.</param>
/// <param name="Title">The text alternative: <c>requests per minute, last 12 checks: 0 to 45, now 12</c>.</param>
public sealed record Trend(IReadOnlyList<double> Points, string Title)
{
    /// <summary>How many readings a node keeps: half an hour at the default interval.</summary>
    public const int Length = 60;

    /// <summary>
    /// The trend of the readings that are there (a round without a reading is left out); null with fewer than two,
    /// which make no line.
    /// </summary>
    /// <param name="what">What the number is: <c>requests per minute</c>.</param>
    /// <param name="unit">The unit after the numbers of the title, with its leading space or empty.</param>
    public static Trend? Of(IEnumerable<double?> readings, string what, string unit = "")
    {
        ArgumentNullException.ThrowIfNull(readings);
        var values = readings.OfType<double>().Where(double.IsFinite).ToList();
        if (values.Count < 2)
        {
            return null;
        }

        var max = values.Max();
        var points = values.Select(value => max > 0 ? Math.Round(Math.Clamp(value / max, 0, 1), 3) : 0).ToList();
        var title = string.Create(
            CultureInfo.InvariantCulture,
            $"{what}, last {values.Count} checks: {Number(values.Min())} to {Number(max)}{unit}, now {Number(values[^1])}{unit}");
        return new Trend(points, title);
    }

    /// <summary>The points of an SVG polyline in a box of the given size, with the line kept inside by <paramref name="inset"/>.</summary>
    public string Polyline(double width, double height, double inset = 1)
    {
        var step = Points.Count > 1 ? (width - 2 * inset) / (Points.Count - 1) : 0;
        return string.Join(' ', Points.Select((point, index) => string.Create(
            CultureInfo.InvariantCulture,
            $"{inset + index * step:0.##},{height - inset - point * (height - 2 * inset):0.##}")));
    }

    /// <summary>Where the last reading is in that box: the dot that marks "now".</summary>
    public (double X, double Y) Last(double width, double height, double inset = 1) =>
        (Math.Round(width - inset, 2), Math.Round(height - inset - Points[^1] * (height - 2 * inset), 2));

    private static string Number(double value) => value.ToString(value == Math.Round(value) ? "0" : "0.#", CultureInfo.InvariantCulture);
}
