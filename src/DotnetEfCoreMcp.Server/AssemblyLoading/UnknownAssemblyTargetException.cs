namespace DotnetEfCoreMcp.Server.AssemblyLoading;

/// <summary>Thrown when a caller requests a <c>targetName</c> that has no matching entry in the
/// assembly registry. Unlike <c>UnknownConnectionException</c>, the message deliberately does not
/// enumerate other registered target names - assembly targets can reveal proprietary project/build
/// naming that a client should not be able to enumerate by probing with invalid names.</summary>
public sealed class UnknownAssemblyTargetException(string requestedName) : Exception(
    $"No target assembly named '{requestedName}' is registered on the server. Call load_assembly " +
    "with an explicit targetName to register it, or call list_loaded_assemblies to see what is " +
    "currently registered.")
{
    public string RequestedName { get; } = requestedName;
}
