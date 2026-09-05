using System.Reflection;
using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Compilation;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using Microsoft.EntityFrameworkCore;

namespace DotnetEfCoreMcp.Server.Querying;

public sealed class RoslynQueryExecutor(QueryExecutionOptions executionOptions, QueryCompiler compiler)
{
    public async Task<QueryResult> ExecuteAsync(
        LoadedAssemblyHandle target,
        Type contextType,
        ConnectionRegistryEntry entry,
        DatabaseProvider provider,
        QueryRequest request,
        CancellationToken cancellationToken)
    {
        using var invocation = await CompileAndInvokeAsync(target, contextType, entry, provider, request, cancellationToken).ConfigureAwait(false);
        return await ShapeResultAsync(invocation.Value, entry.CommandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Compiles and evaluates the user's query up to (but not including) materializing any
    /// rows, obtaining the query's final in-memory value (an <see cref="IQueryable"/>, a scalar, or
    /// an already-materialized sequence) without ever opening a database connection, executing a
    /// command, or calling <c>SaveChanges</c>. Used to preview the SQL that <c>run_query</c> would
    /// issue, via the returned <see cref="IQueryable"/>'s <c>ToQueryString()</c>, which - like this
    /// method - never touches the database.
    /// <para>Only <see cref="IQueryable"/> results have SQL to preview; a
    /// <see cref="QueryExecutionException"/> is thrown for scalars, already-materialized sequences,
    /// and plain <see cref="IEnumerable"/> results produced by operators with no SQL translation
    /// (e.g. <c>Zip</c>), matching the same distinction <see cref="ShapeResultAsync"/> draws between
    /// row-shaped and scalar results.</para></summary>
    public async Task<QuerySqlPreviewResult> PreviewSqlAsync(
        LoadedAssemblyHandle target,
        Type contextType,
        ConnectionRegistryEntry entry,
        DatabaseProvider provider,
        QueryRequest request,
        CancellationToken cancellationToken)
    {
        using var invocation = await CompileAndInvokeAsync(target, contextType, entry, provider, request, cancellationToken).ConfigureAwait(false);
        if (invocation.Value is not IQueryable sequence)
        {
            throw new QueryExecutionException(
                "The query's final value is not an IQueryable and has no SQL to preview; it is either a scalar/element result " +
                "(e.g. Count, FirstOrDefault), an already-materialized sequence (e.g. .ToList()), or produced by an operator with " +
                "no SQL translation (e.g. Zip).");
        }

        try
        {
            var sql = sequence.ToQueryString();
            return new QuerySqlPreviewResult("C#", sql);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TargetInvocationException)
        {
            throw new QueryExecutionException("The query could not be translated by the database provider.", ex);
        }
    }

    /// <summary>Compiles the query, constructs the generated query DbContext, and invokes the
    /// generated <c>RunUserAuthoredQuery</c> method, returning its result together with the
    /// <see cref="DbContext"/> and assembly load context that must stay alive while that result -
    /// if it is an <see cref="IQueryable"/> - is still being shaped or inspected. Disposing the
    /// returned <see cref="CompiledQueryInvocation"/> disposes the context and unloads the assembly.</summary>
    private async Task<CompiledQueryInvocation> CompileAndInvokeAsync(
        LoadedAssemblyHandle target,
        Type contextType,
        ConnectionRegistryEntry entry,
        DatabaseProvider provider,
        QueryRequest request,
        CancellationToken cancellationToken)
    {
        var query = request.Query?.Trim();
        if (string.IsNullOrWhiteSpace(query)) throw new QueryExecutionException("`query` must be non-empty C# code.");
        if (query.Length > executionOptions.MaxQueryLength) throw new QueryExecutionException("`query` exceeds the configured maximum length.");

        var shape = DbContextActivator.DetermineConstructorShape(contextType);
        if (shape is DbContextConstructorShape.DesignTimeFactory or DbContextConstructorShape.Unsupported)
            throw new QueryExecutionException("The selected DbContext cannot be used with the Roslyn query engine because it does not expose a public parameterless or DbContextOptions constructor.");

        var source = UserQuerySourceGenerator.Generate(contextType, query, Guid.NewGuid().ToString("N"));
        var compiled = await compiler.CompileAsync(source, target, cancellationToken).ConfigureAwait(false);
        var allowMutations = !entry.IsProduction && entry.AccessMode == ConnectionAccessMode.ReadWrite && executionOptions.AllowMutationsInRunQuery;
        var loadContext = new CompiledQueryLoadContext(target.Context, target.LoadedAssemblyPaths, $"DotnetEfCoreMcp.Query.{Guid.NewGuid():N}");
        DbContext? context = null;
        try
        {
            using var pe = new MemoryStream(compiled.Pe);
            using var pdb = new MemoryStream(compiled.Pdb);
            var assembly = loadContext.LoadCompiledAssembly(pe, pdb);
            var generatedType = assembly.GetType(source.TypeName, throwOnError: true)!;
            context = (DbContext)CreateContext(generatedType, contextType, shape, entry, provider, allowMutations);
            object? value;
            try
            {
                value = generatedType.GetMethod("RunUserAuthoredQuery", BindingFlags.Instance | BindingFlags.Public)!.Invoke(context, null);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is QueryExecutionException queryException)
            {
                throw queryException;
            }
            catch (TargetInvocationException ex)
            {
                throw new QueryExecutionException("The C# query failed while it was being evaluated.", ex.InnerException ?? ex);
            }

            return new CompiledQueryInvocation(context, loadContext, value);
        }
        catch
        {
            context?.Dispose();
            loadContext.Unload();
            throw;
        }
    }

    /// <summary>The generated query DbContext, its assembly load context, and the value returned by
    /// invoking the generated <c>RunUserAuthoredQuery</c> method. Disposing unloads the assembly and
    /// disposes the context; the underlying <see cref="IQueryable"/> (if <see cref="Value"/> is one)
    /// must not be used after disposal.</summary>
    private sealed record CompiledQueryInvocation(DbContext Context, CompiledQueryLoadContext LoadContext, object? Value) : IDisposable
    {
        public void Dispose()
        {
            Context.Dispose();
            LoadContext.Unload();
        }
    }

    private object CreateContext(Type generatedType, Type contextType, DbContextConstructorShape shape, ConnectionRegistryEntry entry, DatabaseProvider provider, bool allowMutations)
    {
        // The generated UserQuery_* subclass always overrides OnConfiguring to apply
        // QueryTrackingBehavior.NoTracking, so every constructor shape (including
        // parameterless contexts that build their own options in OnConfiguring) gets the
        // default consistently without needing to configure it again here.
        object? options = shape switch
        {
            DbContextConstructorShape.GenericOptions => DbContextActivator.CreateGenericOptions(contextType, entry, provider),
            DbContextConstructorShape.NonGenericOptions => DbContextActivator.BuildOptions(contextType, entry, provider),
            DbContextConstructorShape.Parameterless => null,
            _ => throw new InvalidOperationException()
        };
        var args = options is null ? [allowMutations] : new[] { options, (object)allowMutations };
        var instance = (DbContext?)Activator.CreateInstance(generatedType, args)
            ?? throw new QueryExecutionException("The generated query DbContext could not be constructed.");

        // Parameterless-shape contexts build their own provider/connection in OnConfiguring
        // (see the base.OnConfiguring(...) call the generator emits), so - just like
        // DbContextActivator.CreateInstance does for the classic Dynamic LINQ engine - the
        // server must forcibly override whatever connection string that OnConfiguring set up
        // with the registry-resolved one. Options-based shapes already receive the correct
        // connection string via DbContextActivator.CreateGenericOptions/BuildOptions above, so
        // no override is needed for them.
        if (shape == DbContextConstructorShape.Parameterless)
        {
            try
            {
                DbContextActivator.OverrideConnectionString(instance, entry, contextType, provider);
            }
            catch (DbContextActivationException ex)
            {
                instance.Dispose();
                throw new QueryExecutionException(ex.Message, ex);
            }
        }

        return instance;
    }

    /// <summary>Shapes the return value of the user's compiled query into rows or a scalar.
    /// <para>Scope decision: only <see cref="IQueryable"/> results (i.e. an unmaterialized query
    /// against a root <c>DbSet</c>) are capped, executed against the database, and shaped into
    /// rows. Any other return value - including plain <see cref="IEnumerable"/> sequences produced
    /// by operators with no SQL translation (e.g. <c>Zip</c>), already-materialized lists, or
    /// scalars like <c>int</c>/<c>bool</c> - is returned as-is via the <see cref="QueryResult.Scalar"/>
    /// slot. Widening this to also shape plain <see cref="IEnumerable"/> results was considered,
    /// but rejected: unlike <see cref="IQueryable"/> there is no expression tree to inspect for a
    /// user-supplied <c>Take</c>, so the effective-take cap (<see cref="QueryExecutor.GetEffectiveTake"/>)
    /// and timeout-aware materialization could not be applied consistently, and an already-enumerated
    /// sequence could be unbounded. Users who want row-shaped output from a non-translatable
    /// operator can end the query with <c>.ToList()</c> and inspect the resulting scalar.</para></summary>
    private async Task<QueryResult> ShapeResultAsync(object? value, int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        if (value is not IQueryable sequence)
            return new QueryResult("C#", 1, null, false, true, value, []);

        var effectiveTake = QueryExecutor.GetEffectiveTake(sequence.Expression, executionOptions);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(commandTimeoutSeconds) + executionOptions.CancellationMargin);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var (values, hasMoreRows) = await QueryExecutor.MaterializeWithContinuationAsync(sequence, effectiveTake, linked.Token).ConfigureAwait(false);
            return new QueryResult("C#", values.Count, effectiveTake, hasMoreRows, false, null, values.Select(QueryExecutor.ProjectValue).ToList());
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested)
        {
            throw new QueryExecutionException($"Query timed out after {commandTimeoutSeconds}s.", ex);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TargetInvocationException)
        {
            throw new QueryExecutionException("The query could not be translated or executed by the database provider.", ex);
        }
    }
}
