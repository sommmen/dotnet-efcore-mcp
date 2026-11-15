using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.Hosting;
using DotnetEfCoreMcp.Server.Querying;
using DotnetEfCoreMcp.Server.Telemetry;
using DotnetEfCoreMcp.Server.Tools;
using DotnetEfCoreMcp.Server.Updates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DotnetEfCoreMcp.Server.Tests.Hosting;

public sealed class ServerCompositionTests
{
    [Fact]
    public async Task AddEfCoreServices_WithoutHostedServices_RegistersSingletonsButNoHostedService()
    {
        var builder = Host.CreateApplicationBuilder([]);

        ServerComposition.AddEfCoreServices(builder, includeHostedServices: false);

        Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(IHostedService));

        await using var provider = builder.Services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<AssemblyLoaderService>());
        Assert.NotNull(provider.GetRequiredService<ConnectionRegistry>());
        Assert.NotNull(provider.GetRequiredService<QueryHostPool>());
        Assert.NotNull(provider.GetRequiredService<UpdateNotificationService>());
        Assert.NotNull(provider.GetRequiredService<IToolResultFormatter>());
        Assert.NotNull(provider.GetRequiredService<IMcpMetrics>());
    }

    [Fact]
    public void AddEfCoreServices_ByDefault_RegistersTheHostedServices()
    {
        var builder = Host.CreateApplicationBuilder([]);

        ServerComposition.AddEfCoreServices(builder);

        var hostedServiceCount = builder.Services.Count(descriptor => descriptor.ServiceType == typeof(IHostedService));
        Assert.Equal(3, hostedServiceCount);
    }

    [Fact]
    public void AddEfCoreServices_WithUnsupportedToolOutputFormat_ThrowsBeforeBuild()
    {
        var builder = Host.CreateApplicationBuilder([]);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ToolOutput:Format"] = "xml" });

        var exception = Assert.Throws<InvalidOperationException>(() => ServerComposition.AddEfCoreServices(builder));

        Assert.Contains("ToolOutput:Format", exception.Message, StringComparison.Ordinal);
    }
}
