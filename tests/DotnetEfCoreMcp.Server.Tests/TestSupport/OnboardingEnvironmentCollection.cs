namespace DotnetEfCoreMcp.Server.Tests.TestSupport;

/// <summary>Serializes every test that mutates the process-global
/// <c>DOTNETEFCOREMCP_HOMEDIR</c> (and <c>DOTNETEFCOREMCP_HOOKDATADIR</c>) variables, which
/// redirect user-scoped onboarding paths away from the developer's real profile.
/// <para>xUnit runs separate test collections in parallel, and an environment variable is per
/// process, not per class. Without this, one class can overwrite the redirect another class is
/// mid-way through relying on - so a user-scoped install lands under the wrong temp home and the
/// assertion fails intermittently, depending purely on scheduling. Running the classes one at a
/// time hides it, which is what makes this kind of failure so easy to dismiss as a fluke.</para>
/// <para>Apply <c>[Collection(OnboardingEnvironmentCollection.Name)]</c> to any test class that
/// reads or writes those variables.</para></summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OnboardingEnvironmentCollection
{
    public const string Name = "OnboardingEnvironment";
}
