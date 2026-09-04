using Microsoft.EntityFrameworkCore;

namespace DotnetEfCoreMcp.Server.Connections;

/// <summary>Outcome of a bounded provider connection-health probe. Never carries provider
/// exception text, stack traces, or connection details - only a stable classification safe to
/// return to an MCP client and to log.</summary>
public enum ConnectionHealthStatus
{
    /// <summary>The provider accepted a connection within the configured timeout.</summary>
    Healthy,

    /// <summary>The provider rejected the connection, or the attempt raised an exception, within
    /// the configured timeout (e.g. authentication failure, unreachable host, invalid database).</summary>
    Failed,

    /// <summary>The probe did not complete before the connection's configured command timeout
    /// (plus the server's cancellation margin) elapsed.</summary>
    TimedOut,
}

/// <summary>Runs a single bounded, read-only provider connection-health probe
/// (<see cref="DbContext.Database"/>'s <c>CanConnectAsync</c>) against an already-constructed
/// <see cref="DbContext"/>. Never executes user SQL, never mutates state, and never exposes
/// provider exception details - callers get back only a <see cref="ConnectionHealthStatus"/>.
/// Genuine caller cancellation (the supplied <see cref="CancellationToken"/> requesting
/// cancellation, as opposed to the internal timeout firing) propagates as a thrown
/// <see cref="OperationCanceledException"/> rather than being reported as a status value.</summary>
public static class ConnectionHealthChecker
{
    /// <summary>Attempts to connect to the database backing <paramref name="context"/>, bounded by
    /// <paramref name="commandTimeoutSeconds"/> plus <paramref name="cancellationMargin"/> - the same
    /// defense-in-depth timeout shape used by query execution (see
    /// <see cref="Querying.QueryExecutor"/>).</summary>
    public static Task<ConnectionHealthStatus> CheckAsync(
        DbContext context,
        int commandTimeoutSeconds,
        TimeSpan cancellationMargin,
        CancellationToken cancellationToken)
        => CheckAsync(context.Database.CanConnectAsync, commandTimeoutSeconds, cancellationMargin, cancellationToken);

    /// <summary>Core implementation, decoupled from <see cref="DbContext"/> so the timeout/cancellation
    /// classification logic can be exercised deterministically in tests via a fake <paramref name="probe"/>
    /// without a real (and inherently timing-sensitive) slow database provider.</summary>
    internal static async Task<ConnectionHealthStatus> CheckAsync(
        Func<CancellationToken, Task<bool>> probe,
        int commandTimeoutSeconds,
        TimeSpan cancellationMargin,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(commandTimeoutSeconds) + cancellationMargin);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            var canConnect = await probe(linkedCts.Token);
            return canConnect ? ConnectionHealthStatus.Healthy : ConnectionHealthStatus.Failed;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return ConnectionHealthStatus.TimedOut;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Any provider-level failure (DbException, socket/auth errors, etc.) is reported as a
            // generic Failed classification; the exception itself (and any connection details it
            // may carry) is never propagated to the caller.
            return ConnectionHealthStatus.Failed;
        }
    }
}
