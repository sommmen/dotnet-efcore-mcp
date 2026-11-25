using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SampleApp;

/// <summary>Fixture for issue #171: every command it issues fails the way SQL Server's provider
/// reports a command timeout (a <see cref="DbException"/> exposing <c>Number == -2</c>), without
/// needing a SQL Server instance or a slow query. Shares <see cref="Customer"/>/<see cref="Order"/>
/// with <see cref="SampleAppDbContext"/> so a database created by that context can be queried
/// through this one.</summary>
public class CommandTimeoutDbContext : DbContext
{
    public CommandTimeoutDbContext(DbContextOptions<CommandTimeoutDbContext> options)
        : base(options)
    {
    }

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Order> Orders => Set<Order>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.AddInterceptors(new CommandTimeoutInterceptor());
    }

    /// <summary>Mirrors the shape of <c>Microsoft.Data.SqlClient.SqlException</c>.</summary>
    private sealed class SimulatedSqlException()
        : DbException("Execution Timeout Expired.  The timeout period elapsed prior to completion of the operation or the server is not responding.")
    {
        public int Number => -2;
    }

    private sealed class CommandTimeoutInterceptor : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
            => throw new SimulatedSqlException();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
            => throw new SimulatedSqlException();

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
            => throw new SimulatedSqlException();

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
            => throw new SimulatedSqlException();
    }
}
