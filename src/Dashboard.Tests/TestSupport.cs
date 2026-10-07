using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;

namespace Dashboard.Tests;

/// <summary>An HTTP handler whose answers the test decides; it records the requests it saw.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    : HttpMessageHandler
{
    private readonly List<HttpRequestMessage> _requests = [];

    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : this((request, _) => Task.FromResult(respond(request)))
    {
    }

    public IReadOnlyList<HttpRequestMessage> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public IEnumerable<string> RequestedUrls => Requests.Select(request => request.RequestUri!.ToString());

    public static HttpResponseMessage Answer(HttpStatusCode status, string body = "") =>
        new(status) { Content = new StringContent(body) };

    /// <summary>An answer that never comes: the request ends when it is cancelled.</summary>
    public static async Task<HttpResponseMessage> NeverAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("An infinite delay ended without cancellation.");
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_requests)
        {
            _requests.Add(request);
        }

        return respond(request, cancellationToken);
    }
}

/// <summary>Fake time that tells the test when code under test started to wait on a timer.</summary>
internal sealed class SignallingTimeProvider : FakeTimeProvider
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private readonly Channel<TimeSpan> _timers = Channel.CreateUnbounded<TimeSpan>();

    public SignallingTimeProvider()
        : base(new DateTimeOffset(2026, 10, 4, 22, 0, 0, TimeSpan.Zero))
    {
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        _timers.Writer.TryWrite(dueTime);
        return timer;
    }

    /// <summary>Waits until a timer was created that no earlier call of this method consumed.</summary>
    public async Task TimerCreatedAsync() =>
        Assert.True(await TimerCreatedWithinAsync(Patience), "No timer was created: the code under test is not waiting.");

    /// <summary>True when a timer is created within <paramref name="window"/> (real time).</summary>
    public async Task<bool> TimerCreatedWithinAsync(TimeSpan window)
    {
        using var patience = new CancellationTokenSource(window);
        try
        {
            await _timers.Reader.ReadAsync(patience.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
