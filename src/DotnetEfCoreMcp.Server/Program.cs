using DotnetEfCoreMcp.Server.Hooks;
using DotnetEfCoreMcp.Server.Hosting;
using DotnetEfCoreMcp.Server.Onboarding;
using DotnetEfCoreMcp.Server.Updates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// CLI verbs are handled before the host is built: `hooks` and `onboard` run as one-shot commands
// (including the short-lived processes the installed agent hooks invoke on every tool call) rather
// than starting the stdio MCP server. Launching with no arguments, or with only configuration
// arguments, still starts the server exactly as before.
if (args.Length > 0 && args[0].Equals(HooksCommand.VerbName, StringComparison.OrdinalIgnoreCase))
{
    return HooksCommand.Run(args, Console.Out, Console.Error, Console.In);
}

if (args.Length > 0 && args[0].Equals(OnboardCommand.VerbName, StringComparison.OrdinalIgnoreCase))
{
    return OnboardCommand.Run(args, Console.Out, Console.Error);
}

if (args.Length > 0 && args[0].Equals(VersionCommand.VerbName, StringComparison.OrdinalIgnoreCase))
{
    return await VersionCommand.RunAsync(args, Console.Out, Console.Error);
}

var builder = Host.CreateApplicationBuilder(args);

ServerComposition.AddEfCoreServices(builder);
ServerComposition.AddEfCoreMcpServer(builder);

var host = builder.Build();

ServerComposition.PreloadTargets(
    host.Services,
    builder.Configuration,
    host.Services.GetRequiredService<ILogger<Program>>());

await host.RunAsync();

return 0;