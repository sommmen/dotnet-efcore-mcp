using DotnetEfCoreMcp.Server.Querying;

namespace DotnetEfCoreMcp.Server.Tests.Querying;

/// <summary>Pins the default <c>run_query</c> engine per the migration/rollout plan in
/// <c>docs/development/roslyn-user-query.md</c> - Roslyn is now the default, with
/// <see cref="QueryEngine.DynamicLinq"/> retained only as an explicit, temporary compatibility
/// escape hatch.</summary>
public sealed class QueryExecutionOptionsTests
{
    [Fact]
    public void Engine_DefaultsToRoslyn()
    {
        var options = new QueryExecutionOptions();

        Assert.Equal(QueryEngine.Roslyn, options.Engine);
    }
}
