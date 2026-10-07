namespace Dashboard.Health;

public static class ProbeUrl
{
    /// <summary>The address of an endpoint: a node's base address and a path, joined with exactly one slash.</summary>
    public static Uri Combine(Uri baseAddress, string path)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(path);
        var root = baseAddress.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return new Uri($"{root}/{path.TrimStart('/')}", UriKind.Absolute);
    }
}
