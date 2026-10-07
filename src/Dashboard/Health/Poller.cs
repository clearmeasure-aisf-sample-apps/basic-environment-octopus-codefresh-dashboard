namespace Dashboard.Health;

/// <summary>
/// Runs a check, waits for the interval and repeats, while the dashboard is neither paused nor hidden. Resuming,
/// becoming visible and a new interval start a check at once; <see cref="CheckNow"/> does so even while paused.
/// </summary>
public sealed class Poller(Func<CancellationToken, Task> check, TimeProvider time, TimeSpan interval)
{
    private readonly Lock _gate = new();
    private TaskCompletionSource _wake = NewSignal();
    private TimeSpan _interval = interval;
    private bool _paused;
    private bool _visible = true;
    private bool _checkRequested;

    public TimeSpan Interval
    {
        get
        {
            lock (_gate)
            {
                return _interval;
            }
        }
    }

    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _paused;
            }
        }
    }

    public bool IsVisible
    {
        get
        {
            lock (_gate)
            {
                return _visible;
            }
        }
    }

    /// <summary>Checks run only while the dashboard is not paused and its browser tab is visible.</summary>
    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return !_paused && _visible;
            }
        }
    }

    public void Pause() => Change(() => Set(ref _paused, true));

    public void Resume() => Change(() => Set(ref _paused, false));

    public void SetVisible(bool visible) => Change(() => Set(ref _visible, visible));

    public void SetInterval(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        Change(() => Set(ref _interval, interval));
    }

    /// <summary>Ends the current wait and runs one check at once, also while paused or hidden.</summary>
    public void CheckNow() => Change(() => _checkRequested = true);

    /// <summary>Runs until <paramref name="cancellationToken"/> is cancelled, then throws.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The signal is taken before the check: a change made during the check ends the wait that follows it.
            Task wake;
            bool active;
            lock (_gate)
            {
                if (_wake.Task.IsCompleted)
                {
                    _wake = NewSignal();
                }

                wake = _wake.Task;
                active = (!_paused && _visible) || _checkRequested;
                _checkRequested = false;
            }

            if (active)
            {
                await check(cancellationToken);
            }

            TimeSpan wait;
            lock (_gate)
            {
                wait = !_paused && _visible ? _interval : Timeout.InfiniteTimeSpan;
            }

            if (wait == Timeout.InfiniteTimeSpan)
            {
                await wake.WaitAsync(cancellationToken);
                continue;
            }

            using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(wait, time, delayCancellation.Token);
            await Task.WhenAny(wake, delay);
            await delayCancellation.CancelAsync();
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static bool Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        return true;
    }

    private void Change(Func<bool> change)
    {
        lock (_gate)
        {
            if (change())
            {
                _wake.TrySetResult();
            }
        }
    }
}
