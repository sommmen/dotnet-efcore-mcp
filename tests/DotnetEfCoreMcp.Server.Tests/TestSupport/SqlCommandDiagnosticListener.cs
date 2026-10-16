using System.Diagnostics;

namespace DotnetEfCoreMcp.Server.Tests.TestSupport;

/// <summary>Captures the SQL EF Core actually issues, by observing its
/// <c>CommandExecuting</c> diagnostic events. Lets a test assert on the real command text rather
/// than on how the server built the expression tree.</summary>
public sealed class SqlCommandDiagnosticListener : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private readonly List<string> _commands;
    private readonly IDisposable _allListenersSubscription;
    private readonly List<IDisposable> _efCoreSubscriptions = [];
    private readonly Lock _efCoreSubscriptionsLock = new();
    private bool _disposed;

    public SqlCommandDiagnosticListener(List<string> commands)
    {
        _commands = commands;
        _allListenersSubscription = DiagnosticListener.AllListeners.Subscribe(this);
    }

    public void OnNext(DiagnosticListener listener)
    {
        // EF Core can create more than one internal "Microsoft.EntityFrameworkCore"
        // DiagnosticListener instance (e.g. a distinct internal service provider per
        // unique set of context options, or - under parallel test execution - one per
        // concurrently running test's DbContext). Subscribing to every instance we see
        // (rather than switching to only the latest) avoids losing command-executing
        // events emitted on an earlier listener before a later one is observed.
        if (listener.Name == "Microsoft.EntityFrameworkCore")
        {
            // Disposing AllListeners does not guarantee this callback stops firing
            // synchronously, so a new listener can still be observed concurrently with (or
            // just after) Dispose(). Subscribe first, then check _disposed under the same
            // lock Dispose uses; if disposal has already started, immediately dispose the
            // just-created subscription instead of leaking it in _efCoreSubscriptions.
            var subscription = listener.Subscribe(this);
            lock (_efCoreSubscriptionsLock)
            {
                if (_disposed)
                {
                    subscription.Dispose();
                    return;
                }

                _efCoreSubscriptions.Add(subscription);
            }
        }
    }

    public void OnNext(KeyValuePair<string, object?> value)
    {
        if (value.Key.EndsWith("CommandExecuting", StringComparison.Ordinal)
            && value.Value is Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData command)
        {
            lock (_commands)
            {
                _commands.Add(command.Command.CommandText);
            }
        }
    }

    public void OnCompleted() { }

    public void OnError(Exception error) { }

    public void Dispose()
    {
        // Dispose the AllListeners subscription first so OnNext(DiagnosticListener) can no
        // longer fire and add a new entry to _efCoreSubscriptions once we start disposing
        // them below; otherwise a listener observed during that window would never be
        // disposed. The _disposed flag (checked under the same lock in OnNext) closes the
        // remaining window where AllListeners.Dispose() does not synchronously guarantee
        // OnNext has stopped firing.
        _allListenersSubscription.Dispose();

        lock (_efCoreSubscriptionsLock)
        {
            _disposed = true;
            foreach (var subscription in _efCoreSubscriptions)
            {
                subscription.Dispose();
            }
        }
    }
}
