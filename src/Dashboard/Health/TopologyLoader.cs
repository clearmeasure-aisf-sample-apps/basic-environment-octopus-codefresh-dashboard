namespace Dashboard.Health;

/// <summary>Loads <c>topology.json</c> from the dashboard's own address (next to <c>index.html</c>).</summary>
public sealed class TopologyLoader(HttpClient http)
{
    public const string FileName = "topology.json";

    public async Task<TopologyParseResult> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = NodeProber.NewRequest(new Uri(FileName, UriKind.Relative));
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return TopologyParseResult.Failed($"The server answered HTTP {(int)response.StatusCode} for {FileName}.");
            }

            return TopologyParser.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (HttpRequestException exception)
        {
            return TopologyParseResult.Failed($"The file could not be loaded: {exception.Message}");
        }
    }
}
