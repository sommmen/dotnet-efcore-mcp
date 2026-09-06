using DotnetEfCoreMcp.Server.Telemetry;
using Microsoft.Extensions.Configuration;

namespace DotnetEfCoreMcp.Server.Tests.Telemetry;

public sealed class TelemetryOptionsTests
{
    [Fact]
    public void CreateEffective_DisabledByDefaultWhenNotConfigured()
    {
        var options = TelemetryOptions.CreateEffective(new ConfigurationBuilder().Build(), isDevelopment: true);

        Assert.False(options.Enabled);
        Assert.False(options.EnableDevelopmentFullData);
        Assert.Equal(0.05, options.SamplingRatio);
        Assert.Equal(60_000, options.ExportIntervalMilliseconds);
        Assert.Equal(10_000, options.ExportTimeoutMilliseconds);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void CreateEffective_EnablesFullDataOnlyForDevelopment(bool isDevelopment, bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telemetry:EnableDevelopmentFullData"] = "true",
            })
            .Build();

        var options = TelemetryOptions.CreateEffective(configuration, isDevelopment);

        Assert.Equal(expected, options.EnableDevelopmentFullData);
    }

    [Fact]
    public void CreateEffective_ReadsAllConfiguredValues()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telemetry:Enabled"] = "true",
                ["Telemetry:OtlpEndpoint"] = "http://localhost:4318",
                ["Telemetry:SamplingRatio"] = "0.5",
                ["Telemetry:EnableDevelopmentFullData"] = "true",
                ["Telemetry:ExportIntervalMilliseconds"] = "1000",
                ["Telemetry:ExportTimeoutMilliseconds"] = "500",
            })
            .Build();

        var options = TelemetryOptions.CreateEffective(configuration, isDevelopment: true);

        Assert.True(options.Enabled);
        Assert.Equal("http://localhost:4318", options.OtlpEndpoint);
        Assert.Equal(0.5, options.SamplingRatio);
        Assert.True(options.EnableDevelopmentFullData);
        Assert.Equal(1000, options.ExportIntervalMilliseconds);
        Assert.Equal(500, options.ExportTimeoutMilliseconds);
    }

    [Fact]
    public void CreateEffective_SupportsEnvironmentVariableStyleOverride()
    {
        // Mirrors the DOTNETEFCOREMCP_Telemetry__Enabled=true environment-variable override
        // documented for Program.cs: AddEnvironmentVariables maps double-underscore to ':'
        // before this reaches configuration, so an in-memory ':' key is an equivalent stand-in.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telemetry:Enabled"] = "true",
            })
            .Build();

        var options = TelemetryOptions.CreateEffective(configuration, isDevelopment: false);

        Assert.True(options.Enabled);
    }
}
