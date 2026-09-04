using DotnetEfCoreMcp.Server.Tools;
using Microsoft.Extensions.Configuration;

namespace DotnetEfCoreMcp.Server.Tests.Tools;

public sealed class ToolDiagnosticsOptionsTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void CreateEffective_EnablesSafeDetailsOnlyForDevelopment(bool isDevelopment, bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ToolDiagnostics:ExposeSafeErrorDetails"] = "true",
            })
            .Build();

        var options = ToolDiagnosticsOptions.CreateEffective(configuration, isDevelopment);

        Assert.Equal(expected, options.ExposeSafeErrorDetails);
    }

    [Fact]
    public void CreateEffective_LeavesSafeDetailsDisabledWhenNotConfigured()
    {
        var options = ToolDiagnosticsOptions.CreateEffective(new ConfigurationBuilder().Build(), isDevelopment: true);

        Assert.False(options.ExposeSafeErrorDetails);
    }
}
