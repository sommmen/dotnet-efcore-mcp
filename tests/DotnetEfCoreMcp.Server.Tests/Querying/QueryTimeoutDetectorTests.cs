using System.Data.Common;
using System.Reflection;
using DotnetEfCoreMcp.Server.Querying;

namespace DotnetEfCoreMcp.Server.Tests.Querying;

public sealed class QueryTimeoutDetectorTests
{
    [Fact]
    public void IsCommandTimeout_SqlServerExecutionTimeoutNumber_IsATimeout()
    {
        Assert.True(QueryTimeoutDetector.IsCommandTimeout(new SqlException(-2, "Execution Timeout Expired.")));
    }

    [Fact]
    public void IsCommandTimeout_OtherSqlServerError_IsNotATimeout()
    {
        Assert.False(QueryTimeoutDetector.IsCommandTimeout(new SqlException(207, "Invalid column name 'x'.")));
    }

    [Fact]
    public void IsCommandTimeout_ProviderExceptionWithInnerTimeoutException_IsATimeout()
    {
        var exception = new FakeProviderException("Exception while reading from stream", new TimeoutException("Timeout during reading attempt"));

        Assert.True(QueryTimeoutDetector.IsCommandTimeout(exception));
    }

    [Fact]
    public void IsCommandTimeout_TimeoutBuriedBehindWrappers_IsATimeout()
    {
        var exception = new TargetInvocationException(new AggregateException(new InvalidOperationException("wrapped", new SqlException(-2, "Execution Timeout Expired."))));

        Assert.True(QueryTimeoutDetector.IsCommandTimeout(exception));
    }

    [Fact]
    public void IsCommandTimeout_ProviderExceptionWithoutTimeout_IsNotATimeout()
    {
        Assert.False(QueryTimeoutDetector.IsCommandTimeout(new FakeProviderException("syntax error", new InvalidOperationException("boom"))));
    }

    [Fact]
    public void IsCommandTimeout_OperationCanceled_IsNotATimeout()
    {
        Assert.False(QueryTimeoutDetector.IsCommandTimeout(new OperationCanceledException()));
    }

    private sealed class FakeProviderException(string message, Exception? inner = null) : DbException(message, inner);

    // Mirrors the shape of Microsoft.Data.SqlClient.SqlException, which this server must not reference directly.
    private sealed class SqlException(int number, string message) : DbException(message)
    {
        public int Number { get; } = number;
    }
}
