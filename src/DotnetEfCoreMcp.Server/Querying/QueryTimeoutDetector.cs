using System.Data.Common;
using System.Reflection;

namespace DotnetEfCoreMcp.Server.Querying;

/// <summary>Recognizes a database provider's own command timeout without referencing any provider
/// package. The provider's <c>CommandTimeout</c> is set to the connection's
/// <c>CommandTimeoutSeconds</c>, so it fires before the server's own cancellation token (which
/// adds <see cref="QueryExecutionOptions.CancellationMargin"/>) and surfaces as a provider
/// exception rather than an <see cref="OperationCanceledException"/>.</summary>
internal static class QueryTimeoutDetector
{
    /// <summary>SQL Server's <c>SqlException.Number</c> for "Execution Timeout Expired".</summary>
    private const int SqlServerCommandTimeoutNumber = -2;

    public static bool IsCommandTimeout(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AggregateException aggregate && aggregate.InnerExceptions.Any(IsCommandTimeout))
                return true;

            if (current is DbException dbException && IsProviderTimeout(dbException))
                return true;
        }

        return false;
    }

    private static bool IsProviderTimeout(DbException exception)
    {
        if (exception.GetType().GetProperty("Number", BindingFlags.Public | BindingFlags.Instance)?.GetValue(exception) is SqlServerCommandTimeoutNumber)
            return true;

        // Npgsql (and others) report a command timeout as a provider exception caused by a TimeoutException.
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is TimeoutException)
                return true;
        }

        return false;
    }
}
