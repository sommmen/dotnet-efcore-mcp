namespace BrokenDependencyApp.Dependency;

/// <summary>Stands in for a shared-framework/transitive-package type (e.g. an ASP.NET Core Identity
/// type living in `Microsoft.AspNetCore.App`) that a target DbContext type might depend on. Tests
/// remove the compiled DLL containing this type from the load folder to force a
/// <see cref="System.Reflection.ReflectionTypeLoadException"/> during discovery.</summary>
public class ExternalIdentityMarker
{
    public string Value { get; set; } = string.Empty;
}
