using BrokenDependencyApp.Dependency;

namespace BrokenDependencyApp;

/// <summary>Deliberately inherits from a type in BrokenDependencyApp.Dependency. The scanner test
/// deletes that dependency DLL from a scratch copy of the output folder before loading this
/// assembly, making this type fail to load while leaving GoodDbContext usable. Base-type
/// resolution happens eagerly during <see cref="System.Reflection.Assembly.GetTypes"/>, unlike a
/// property of the missing type (which resolves lazily and would not trigger the failure here).</summary>
public class TypeWithMissingDependency : ExternalIdentityMarker
{
}
