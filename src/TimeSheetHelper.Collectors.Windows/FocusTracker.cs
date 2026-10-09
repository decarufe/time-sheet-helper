using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using TimeSheetHelper.Core.Activities;
using ActivityEvent = TimeSheetHelper.Core.Activities.ActivityEvent;

namespace TimeSheetHelper.Collectors.Windows;

/// <summary>
/// Records which window has the focus (and which file it shows), plus idle and lock periods.
/// A WinEvent hook gives instant notification of focus/title changes; a poll catches anything missed and idle.
/// </summary>
public sealed class FocusTracker(
    IActivitySink sink,
    FileContextResolver resolver,
    IOptionsMonitor<FocusTrackerOptions> options,
    TimeProvider time,
    ILogger<FocusTracker> logger) : BackgroundService
{
    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private SegmentAggregator? _aggregator;
    private DateTime? _lockedSinceUtc;
    private (IntPtr Hwnd, string Title, FocusSnapshot Snapshot)? _lastResolved;

    public bool IsPaused { get; private set; }

    public FocusSnapshot? Current => _aggregator?.Current;
    public DateTime? CurrentSinceUtc => _aggregator?.Current is null ? null : _aggregator.CurrentStartUtc;

    public void SetPaused(bool paused)
    {
        IsPaused = paused;
        _signals.Writer.TryWrite(true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.CurrentValue;
        _aggregator = new SegmentAggregator(opts.MinSegmentDuration, opts.MergeGap);

        using var hookThread = new WinEventHookThread(() => _signals.Writer.TryWrite(true));
        SystemEvents.SessionSwitch += OnSessionSwitch;
        var lastCheckpoint = time.GetUtcNow().UtcDateTime;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await WaitForSignalAsync(options.CurrentValue.PollInterval, stoppingToken);

                var now = time.GetUtcNow().UtcDateTime;
                var (snapshot, at) = await CaptureAsync(now);
                await WriteAsync(_aggregator.Observe(snapshot, at), stoppingToken);

                if (now - lastCheckpoint >= options.CurrentValue.CheckpointInterval)
                {
                    await WriteAsync(_aggregator.Checkpoint(now), stoppingToken);
                    lastCheckpoint = now;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            await WriteAsync(_aggregator.Flush(time.GetUtcNow().UtcDateTime), CancellationToken.None);
        }
    }

    private async Task WaitForSignalAsync(TimeSpan timeout, CancellationToken stoppingToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cts.CancelAfter(timeout);
        try
        {
            await _signals.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // Poll interval elapsed.
        }
    }

    private async Task<(FocusSnapshot Snapshot, DateTime AtUtc)> CaptureAsync(DateTime nowUtc)
    {
        if (IsPaused)
            return (FocusSnapshot.Paused, nowUtc);

        if (_lockedSinceUtc is { } lockedSince)
            return (FocusSnapshot.Locked, lockedSince);

        var idle = NativeMethods.GetIdleTime();
        if (idle >= options.CurrentValue.IdleThreshold)
            return (FocusSnapshot.Idle, nowUtc - idle);

        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return (_aggregator!.Current ?? FocusSnapshot.Idle, nowUtc);

        var title = NativeMethods.GetWindowTitle(hwnd);
        if (_lastResolved is { } last && last.Hwnd == hwnd && last.Title == title)
            return (last.Snapshot, nowUtc);

        var snapshot = await BuildSnapshotAsync(hwnd, title);
        _lastResolved = (hwnd, title, snapshot);
        return (snapshot, nowUtc);
    }

    private async Task<FocusSnapshot> BuildSnapshotAsync(IntPtr hwnd, string title)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        string processName;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            processName = process.ProcessName;
        }
        catch (ArgumentException)
        {
            processName = "unknown";
        }

        var opts = options.CurrentValue;
        if (opts.ExcludedProcesses.Contains(processName, StringComparer.OrdinalIgnoreCase)
            || opts.ExcludedTitleKeywords.Any(k => title.Contains(k, StringComparison.OrdinalIgnoreCase)))
        {
            return new FocusSnapshot(FocusState.Active, processName, "(privé)");
        }

        var context = await resolver.ResolveAsync(processName, hwnd, title);
        return new FocusSnapshot(FocusState.Active, processName, title, context.FilePath, context.Folder, context.Url);
    }

    private async Task WriteAsync(IReadOnlyList<ActivityEvent> events, CancellationToken cancellationToken)
    {
        foreach (var activity in events)
        {
            try
            {
                await sink.WriteAsync(activity, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to persist focus segment");
            }
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                _lockedSinceUtc = time.GetUtcNow().UtcDateTime;
                break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
                _lockedSinceUtc = null;
                _lastResolved = null;
                break;
        }
        _signals.Writer.TryWrite(true);
    }

    /// <summary>Hosts the WinEvent hooks on a dedicated thread with its own message loop (required for out-of-context hooks).</summary>
    private sealed class WinEventHookThread : IDisposable
    {
        private readonly Thread _thread;
        private readonly Action _onEvent;
        private readonly ManualResetEventSlim _started = new();
        private NativeMethods.WinEventDelegate? _callback; // kept in a field so the GC never collects the native callback
        private uint _threadId;

        public WinEventHookThread(Action onEvent)
        {
            _onEvent = onEvent;
            _thread = new Thread(Run) { IsBackground = true, Name = "FocusTracker.WinEventHook" };
            _thread.Start();
            _started.Wait();
        }

        private void Run()
        {
            _threadId = NativeMethods.GetCurrentThreadId();
            _callback = (_, eventType, hwnd, idObject, _, _, _) =>
            {
                if (eventType == NativeMethods.EVENT_SYSTEM_FOREGROUND
                    || (idObject == NativeMethods.OBJID_WINDOW && hwnd == NativeMethods.GetForegroundWindow()))
                {
                    _onEvent();
                }
            };

            var foregroundHook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _callback, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
            var nameHook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_OBJECT_NAMECHANGE, NativeMethods.EVENT_OBJECT_NAMECHANGE,
                IntPtr.Zero, _callback, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
            _started.Set();

            while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }

            NativeMethods.UnhookWinEvent(foregroundHook);
            NativeMethods.UnhookWinEvent(nameHook);
        }

        public void Dispose()
        {
            NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(2));
            _started.Dispose();
        }
    }
}
