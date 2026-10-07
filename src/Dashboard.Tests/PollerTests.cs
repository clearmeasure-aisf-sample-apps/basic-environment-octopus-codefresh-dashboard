namespace Dashboard.Tests;

public sealed class PollerTests : IAsyncDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(150);

    private readonly SignallingTimeProvider _time = new();
    private readonly SemaphoreSlim _checks = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Poller _poller;
    private Func<Task> _duringCheck = () => Task.CompletedTask;
    private Task _running = Task.CompletedTask;
    private int _count;

    public PollerTests()
    {
        _poller = new Poller(
            async _ =>
            {
                Interlocked.Increment(ref _count);
                await _duringCheck();
                _checks.Release();
            },
            _time,
            Interval);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _running.WaitAsync(Patience));
        _stop.Dispose();
        _checks.Dispose();
    }

    private void Start() => _running = _poller.RunAsync(_stop.Token);

    private async Task CheckRanAsync() =>
        Assert.True(await _checks.WaitAsync(Patience), "The poller did not run a check.");

    /// <summary>The first check ran and the poller waits for the interval.</summary>
    private async Task StartAndWaitForTheFirstIntervalAsync()
    {
        Start();
        await CheckRanAsync();
        await _time.TimerCreatedAsync();
    }

    [Fact]
    public async Task ItChecksAtOnceAndThenAfterEveryInterval()
    {
        await StartAndWaitForTheFirstIntervalAsync();
        Assert.Equal(1, _count);

        _time.Advance(Interval - TimeSpan.FromSeconds(1));
        Assert.Equal(1, _count);

        _time.Advance(TimeSpan.FromSeconds(1));
        await CheckRanAsync();
        await _time.TimerCreatedAsync();
        Assert.Equal(2, _count);

        _time.Advance(Interval);
        await CheckRanAsync();
        Assert.Equal(3, _count);
    }

    [Fact]
    public async Task PausedItDoesNotCheckAndResumingChecksAtOnce()
    {
        await StartAndWaitForTheFirstIntervalAsync();

        _poller.Pause();
        Assert.True(_poller.IsPaused);
        Assert.False(_poller.IsActive);
        _time.Advance(Interval * 10);
        Assert.False(await _time.TimerCreatedWithinAsync(Quiet), "A paused poller must not wait for an interval.");
        Assert.Equal(1, _count);

        _poller.Resume();
        await CheckRanAsync();
        await _time.TimerCreatedAsync();
        Assert.Equal(2, _count);
        Assert.True(_poller.IsActive);
    }

    [Fact]
    public async Task HiddenItDoesNotCheckAndBecomingVisibleChecksAtOnce()
    {
        await StartAndWaitForTheFirstIntervalAsync();

        _poller.SetVisible(false);
        Assert.False(_poller.IsVisible);
        Assert.False(_poller.IsActive);
        _time.Advance(Interval * 10);
        Assert.False(await _time.TimerCreatedWithinAsync(Quiet), "A hidden poller must not wait for an interval.");
        Assert.Equal(1, _count);

        _poller.SetVisible(true);
        await CheckRanAsync();
        Assert.Equal(2, _count);
    }

    [Fact]
    public async Task PausedAndHiddenNeedsBothToEndBeforeItChecks()
    {
        await StartAndWaitForTheFirstIntervalAsync();
        _poller.Pause();
        _poller.SetVisible(false);

        _poller.SetVisible(true);
        Assert.False(_poller.IsActive);
        Assert.False(await _time.TimerCreatedWithinAsync(Quiet));
        Assert.Equal(1, _count);

        _poller.Resume();
        await CheckRanAsync();
        Assert.Equal(2, _count);
    }

    [Fact]
    public async Task ANewIntervalChecksAtOnceAndIsUsedFromThenOn()
    {
        await StartAndWaitForTheFirstIntervalAsync();

        _poller.SetInterval(TimeSpan.FromSeconds(10));
        await CheckRanAsync();
        await _time.TimerCreatedAsync();
        Assert.Equal(TimeSpan.FromSeconds(10), _poller.Interval);
        Assert.Equal(2, _count);

        _time.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(2, _count);
        _time.Advance(TimeSpan.FromSeconds(1));
        await CheckRanAsync();
        Assert.Equal(3, _count);
    }

    [Fact]
    public async Task TheSameIntervalChangesNothing()
    {
        await StartAndWaitForTheFirstIntervalAsync();

        _poller.SetInterval(Interval);

        Assert.False(await _time.TimerCreatedWithinAsync(Quiet));
        Assert.Equal(1, _count);
    }

    [Fact]
    public async Task CheckNowChecksAtOnce()
    {
        await StartAndWaitForTheFirstIntervalAsync();

        _poller.CheckNow();
        await CheckRanAsync();

        Assert.Equal(2, _count);
    }

    [Fact]
    public async Task CheckNowRunsOneCheckWhilePaused()
    {
        await StartAndWaitForTheFirstIntervalAsync();
        _poller.Pause();

        _poller.CheckNow();
        await CheckRanAsync();

        Assert.Equal(2, _count);
        Assert.False(await _time.TimerCreatedWithinAsync(Quiet), "After the requested check, a paused poller waits again.");
        Assert.Equal(2, _count);
    }

    [Fact]
    public async Task PausingDuringACheckLetsItEndAndThenStops()
    {
        var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _duringCheck = () =>
        {
            checking.TrySetResult();
            return release.Task;
        };

        Start();
        await checking.Task.WaitAsync(Patience);
        _poller.Pause();
        release.SetResult();
        await CheckRanAsync();

        Assert.False(await _time.TimerCreatedWithinAsync(Quiet));
        Assert.Equal(1, _count);
    }

    [Fact]
    public async Task StartedHiddenItWaitsForTheTabToShow()
    {
        _poller.SetVisible(false);
        Start();
        Assert.False(await _checks.WaitAsync(Quiet));

        _poller.SetVisible(true);
        await CheckRanAsync();

        Assert.Equal(1, _count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void AnIntervalMustBePositive(int seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => _poller.SetInterval(TimeSpan.FromSeconds(seconds)));
}
