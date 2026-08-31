using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DotnetEfCoreMcp.Server.Querying;

/// <summary>Executes explicitly enabled, parameterized raw SQL commands and bounds their returned rows.</summary>
public sealed class SqlQueryExecutor(RawSqlExecutionOptions options, ILogger<SqlQueryExecutor> logger)
{
    public async Task<SqlQueryResult> ExecuteAsync(
        DbContext context,
        SqlQueryRequest request,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Sql))
        {
            throw new QueryExecutionException("SQL text is required.");
        }

        var maxRows = Math.Max(1, options.MaxRows);
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(commandTimeoutSeconds) + options.CancellationMargin);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            var connection = context.Database.GetDbConnection();
            await context.Database.OpenConnectionAsync(linkedCts.Token);
            await using var command = connection.CreateCommand();
            command.CommandText = request.Sql;
            command.CommandTimeout = commandTimeoutSeconds;
            AddParameters(command, request.Parameters);

            await using var reader = await command.ExecuteReaderAsync(linkedCts.Token);
            if (reader.FieldCount == 0)
            {
                return new SqlQueryResult
                {
                    Rows = [],
                    AffectedRows = reader.RecordsAffected,
                    MaxRows = maxRows,
                };
            }

            var rows = new List<IReadOnlyDictionary<string, object?>>();
            while (rows.Count < maxRows && await reader.ReadAsync(linkedCts.Token))
            {
                var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
                for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                {
                    row[reader.GetName(ordinal)] = await reader.IsDBNullAsync(ordinal, linkedCts.Token)
                        ? null
                        : reader.GetValue(ordinal);
                }

                rows.Add(row);
            }

            var hasMoreRows = rows.Count == maxRows && await reader.ReadAsync(linkedCts.Token);
            return new SqlQueryResult
            {
                Rows = rows,
                HasMoreRows = hasMoreRows,
                MaxRows = maxRows,
            };
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Raw SQL command timed out after {TimeoutSeconds} seconds.", commandTimeoutSeconds);
            throw new QueryExecutionException("The SQL command timed out.", ex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Raw SQL command failed.");
            throw new QueryExecutionException("The SQL command could not be executed.", ex);
        }
    }

    private static void AddParameters(DbCommand command, object?[]? values)
    {
        if (values is null)
        {
            return;
        }

        for (var index = 0; index < values.Length; index++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"@p{index}";
            parameter.Value = values[index] ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }
}
