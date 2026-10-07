using System.Net;
using Dashboard.Health;

namespace Dashboard.Cluster;

public enum SourceState
{
    /// <summary>The file has not been read yet.</summary>
    Pending,

    /// <summary>The file was read.</summary>
    Read,

    /// <summary>The address answered HTTP 404: the file is not there (not published yet, or nothing serves it).</summary>
    Missing,

    /// <summary>No answer the browser may read, or another HTTP status.</summary>
    Unavailable,

    /// <summary>An answer that is not the file: not JSON, or JSON without its facts.</summary>
    Malformed,
}

/// <summary>One reading of one of the cluster view's two files.</summary>
/// <param name="Value">The file's content; null unless it was read.</param>
/// <param name="Detail">Why it was not read, as the end of a sentence.</param>
public sealed record SourceReading<T>(SourceState State, T? Value = null, string? Detail = null)
    where T : class;

/// <summary>
/// Reads the cluster view's two files from the addresses the topology gives: the live status the cluster writes
/// (<see cref="ClusterStatus"/>) and Azure's facts about the AKS service (<see cref="AksService"/>). The same request
/// as every check of the page: a GET the browser neither answers from its cache nor stores, with the node's timeout.
/// </summary>
public sealed class ClusterReader(HttpClient http, TimeProvider time)
{
    /// <summary>How long the answer may take: as long as a node's.</summary>
    public TimeSpan Timeout { get; init; } = NodeProber.DefaultTimeout;

    /// <summary>
    /// Never throws for a file that cannot be read: that is the result Missing, Unavailable or Malformed. Only the
    /// caller's cancellation throws.
    /// </summary>
    public Task<SourceReading<ClusterStatus>> ReadStatusAsync(Uri address, CancellationToken cancellationToken) =>
        ReadAsync(address, ClusterStatus.Parse, cancellationToken);

    /// <summary>Azure's facts about the AKS service, by the same rules.</summary>
    public Task<SourceReading<AksService>> ReadServiceAsync(Uri address, CancellationToken cancellationToken) =>
        ReadAsync(address, AksService.Parse, cancellationToken);

    private async Task<SourceReading<T>> ReadAsync<T>(Uri address, Func<string?, Parsed<T>> parse, CancellationToken cancellationToken)
        where T : class
    {
        using var timeout = new CancellationTokenSource(Timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var request = NodeProber.NewRequest(address);
            using var response = await http.SendAsync(request, linked.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new SourceReading<T>(SourceState.Missing, Detail: "the address answered HTTP 404");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new SourceReading<T>(SourceState.Unavailable, Detail: $"the address answered HTTP {(int)response.StatusCode}");
            }

            var parsed = parse(await response.Content.ReadAsStringAsync(linked.Token));
            return parsed.Value is { } value
                ? new SourceReading<T>(SourceState.Read, value)
                : new SourceReading<T>(SourceState.Malformed, Detail: parsed.Problem);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SourceReading<T>(SourceState.Unavailable, Detail: $"no answer within {Timeout.TotalSeconds:0} s");
        }
        catch (HttpRequestException)
        {
            return new SourceReading<T>(SourceState.Unavailable, Detail: "the browser could not read an answer (network or CORS)");
        }
    }
}
