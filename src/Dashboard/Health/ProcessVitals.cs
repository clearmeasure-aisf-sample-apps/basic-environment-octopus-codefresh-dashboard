using System.Globalization;
using System.Text.Json;

namespace Dashboard.Health;

/// <summary>
/// What a web app says about its own process in the <c>process</c> object of its telemetry endpoint. Every value is
/// optional: an app reports what it measures.
/// </summary>
/// <param name="CpuPercent">The process's share of the machine's processors over the last interval.</param>
/// <param name="WorkingSetMb">The memory the process holds, in megabytes.</param>
/// <param name="GcHeapMb">Of that, the managed heap.</param>
/// <param name="Threads">Threads of the process.</param>
/// <param name="InFlight">Requests being answered at this moment.</param>
/// <param name="ExceptionsPerMinute">Exceptions thrown in the last minute, handled or not.</param>
/// <param name="UptimeSeconds">How long the process has run.</param>
public sealed record ProcessVitals(
    double? CpuPercent,
    double? WorkingSetMb,
    double? GcHeapMb,
    int? Threads,
    int? InFlight,
    int? ExceptionsPerMinute,
    long? UptimeSeconds)
{
    /// <summary>An uptime below this is shown as a restart.</summary>
    public static readonly TimeSpan RecentRestart = TimeSpan.FromMinutes(5);

    public bool RestartedRecently => UptimeSeconds is { } seconds && seconds < RecentRestart.TotalSeconds;

    /// <summary>The <c>process</c> object; null when the answer has none (an older app) or it holds no value.</summary>
    internal static ProcessVitals? Read(JsonElement root)
    {
        var process = JsonRead.Section(root, "process");
        if (process is null)
        {
            return null;
        }

        var vitals = new ProcessVitals(
            JsonRead.Number(process, "cpuPercent"),
            JsonRead.Number(process, "workingSetMb"),
            JsonRead.Number(process, "gcHeapMb"),
            JsonRead.Count(process, "threads"),
            JsonRead.Count(process, "inFlight"),
            JsonRead.Count(process, "exceptionsPerMinute"),
            JsonRead.Long(process, "uptimeSeconds"));
        return vitals == new ProcessVitals(null, null, null, null, null, null, null) ? null : vitals;
    }
}

/// <summary>A process's vitals in the words of the tiles.</summary>
public static class VitalsText
{
    /// <summary><c>3 %</c>; one decimal below ten.</summary>
    public static string Cpu(double percent) =>
        percent < 10 && Math.Round(percent, 1) != Math.Round(percent)
            ? string.Create(CultureInfo.InvariantCulture, $"{percent:0.0} %")
            : string.Create(CultureInfo.InvariantCulture, $"{percent:0} %");

    /// <summary><c>412 MB</c>, and from 1024 MB on <c>1.4 GB</c>.</summary>
    public static string Memory(double megabytes) =>
        megabytes >= 1024
            ? string.Create(CultureInfo.InvariantCulture, $"{megabytes / 1024:0.0} GB")
            : string.Create(CultureInfo.InvariantCulture, $"{megabytes:0} MB");

    /// <summary><c>up 2 h</c>; below five minutes <c>restarted 3 min ago</c>.</summary>
    public static string Uptime(long seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span < ProcessVitals.RecentRestart ? $"restarted {TimeText.Span(span)} ago" : $"up {TimeText.Span(span)}";
    }

    /// <summary>The memory in full, for the tooltip: <c>Working set 412 MB, of which managed heap 96 MB; 41 threads</c>.</summary>
    public static string? MemoryDetail(ProcessVitals vitals)
    {
        ArgumentNullException.ThrowIfNull(vitals);
        var parts = new List<string>();
        if (vitals.WorkingSetMb is { } workingSet)
        {
            parts.Add(vitals.GcHeapMb is { } heap ? $"Working set {Memory(workingSet)}, of which managed heap {Memory(heap)}" : $"Working set {Memory(workingSet)}");
        }
        else if (vitals.GcHeapMb is { } heap)
        {
            parts.Add($"Managed heap {Memory(heap)}");
        }

        if (vitals.Threads is { } threads)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{threads} threads"));
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }
}
