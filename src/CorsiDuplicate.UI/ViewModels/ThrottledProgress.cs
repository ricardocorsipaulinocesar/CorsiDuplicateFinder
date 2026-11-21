using System.Windows;
using System.Windows.Threading;

namespace CorsiDuplicate.UI.ViewModels;

/// <summary>
/// Progress reporter that posts to the UI thread at most a few times per second. A scan reports
/// once per file (thousands per minute); handling each one on the UI thread made the window
/// stutter. Reports that arrive too soon are dropped, except those the caller marks as final.
/// </summary>
public sealed class ThrottledProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;
    private readonly Func<T, bool>? _alwaysDeliver;
    private readonly long _intervalMs;
    private long _lastDelivery;

    public ThrottledProgress(Action<T> handler, Func<T, bool>? alwaysDeliver = null, int intervalMs = 120)
    {
        _handler = handler;
        _alwaysDeliver = alwaysDeliver;
        _intervalMs = intervalMs;
    }

    public void Report(T value)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastDelivery);
        if (now - last < _intervalMs && _alwaysDeliver?.Invoke(value) != true)
        {
            return;
        }

        Interlocked.Exchange(ref _lastDelivery, now);
        // Normal priority, like Progress<T>: reports then keep their order relative to whatever
        // the scan sets on the UI thread afterwards (e.g. the "Scan complete" message).
        Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => _handler(value));
    }
}
