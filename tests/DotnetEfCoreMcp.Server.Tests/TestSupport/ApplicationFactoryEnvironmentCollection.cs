namespace DotnetEfCoreMcp.Server.Tests.TestSupport;

/// <summary>Serializes every test that mutates the process-global
/// <c>DOTNET_EFCORE_MCP_APPLICATION_FACTORY_CONNECTION</c> variable, which the SampleApp fixture's
/// <c>IDesignTimeDbContextFactory</c> reads to resolve its connection string.
/// <para>xUnit runs separate test collections in parallel, so without this the classes that set the
/// variable could interleave: one class's child query host could inherit another's database path,
/// and whichever test restored the previous value last would win. That makes seeded-row assertions
/// fail intermittently and in an order-dependent way.</para>
/// <para>Apply <c>[Collection(ApplicationFactoryEnvironmentCollection.Name)]</c> to any test class
/// that reads or writes this variable.</para></summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ApplicationFactoryEnvironmentCollection
{
    public const string Name = "ApplicationFactoryEnvironment";
}
