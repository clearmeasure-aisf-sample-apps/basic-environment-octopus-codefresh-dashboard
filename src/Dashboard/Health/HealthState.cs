namespace Dashboard.Health;

public enum HealthState
{
    /// <summary>Not checked yet.</summary>
    Pending,

    /// <summary>The endpoint answered HTTP 200.</summary>
    Healthy,

    /// <summary>The endpoint answered with any other HTTP status.</summary>
    Unhealthy,

    /// <summary>The browser got no answer: network failure, CORS refusal or timeout.</summary>
    Unreachable,
}

public static class HealthClassifier
{
    private const int Ok = 200;

    /// <summary>Healthy is HTTP 200 and nothing else; every other status is an answer, but not a healthy one.</summary>
    public static HealthState FromStatusCode(int statusCode) =>
        statusCode == Ok ? HealthState.Healthy : HealthState.Unhealthy;

    public static string Label(HealthState state) => state switch
    {
        HealthState.Healthy => "Healthy",
        HealthState.Unhealthy => "Unhealthy",
        HealthState.Unreachable => "Unreachable",
        _ => "Checking",
    };
}
