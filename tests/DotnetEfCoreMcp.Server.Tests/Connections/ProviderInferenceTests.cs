using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.Tests.TestSupport;

namespace DotnetEfCoreMcp.Server.Tests.Connections;

public sealed class ProviderInferenceTests
{
    [Fact]
    public void TryInfer_SingleKnownProviderReference_Succeeds()
    {
        var success = ProviderInference.TryInfer(
            ["Some.Other.Library", "Microsoft.EntityFrameworkCore.Sqlite", "Microsoft.EntityFrameworkCore"],
            out var provider,
            out var error);

        Assert.True(success);
        Assert.Equal(DatabaseProvider.Sqlite, provider);
        Assert.Null(error);
    }

    [Fact]
    public void TryInfer_NoKnownProviderReference_FailsWithActionableError()
    {
        var success = ProviderInference.TryInfer(
            ["Microsoft.EntityFrameworkCore", "System.Private.CoreLib"],
            out _,
            out var error);

        Assert.False(success);
        Assert.NotNull(error);
        Assert.Contains("Connections:<name>:Provider", error);
    }

    [Fact]
    public void TryInfer_MultipleKnownProviderReferences_FailsWithActionableError()
    {
        var success = ProviderInference.TryInfer(
            ["Microsoft.EntityFrameworkCore.Sqlite", "Microsoft.EntityFrameworkCore.SqlServer"],
            out _,
            out var error);

        Assert.False(success);
        Assert.NotNull(error);
        Assert.Contains("Connections:<name>:Provider", error);
    }

    [Fact]
    public void TryInfer_FromRealAssembly_InfersSqliteForSampleApp()
    {
        var service = new AssemblyLoaderService();
        var handle = service.Load(FixturePaths.SampleAppDllPath);

        var success = ProviderInference.TryInfer(handle.Assembly, out var provider, out var error);

        Assert.True(success);
        Assert.Equal(DatabaseProvider.Sqlite, provider);
        Assert.Null(error);
    }
}
