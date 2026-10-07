namespace Dashboard.Health;

/// <summary>The outcome of one check of one endpoint.</summary>
/// <param name="State">Healthy, Unhealthy or Unreachable.</param>
/// <param name="StatusCode">The HTTP status, when the endpoint answered.</param>
/// <param name="LatencyMs">Time to the answer in milliseconds, when the endpoint answered.</param>
/// <param name="CheckedAt">When the check started.</param>
/// <param name="Version">The app version the version endpoint reported, without build metadata.</param>
/// <param name="Detail">Why the endpoint is unreachable.</param>
/// <param name="Probe">Which endpoint was called: only a passing health check says the database answered.</param>
public sealed record ProbeResult(
    HealthState State,
    int? StatusCode,
    int? LatencyMs,
    DateTimeOffset CheckedAt,
    string? Version = null,
    string? Detail = null,
    ProbeKind Probe = ProbeKind.Health);
