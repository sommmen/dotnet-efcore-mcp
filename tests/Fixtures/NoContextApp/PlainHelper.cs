namespace NoContextApp;

/// <summary>An entirely ordinary class with no relation to EF Core, used to prove the assembly
/// loads fine but simply contains zero <see cref="Microsoft.EntityFrameworkCore.DbContext"/>-derived
/// types.</summary>
public class PlainHelper
{
    public string Greet(string name) => $"Hello, {name}!";
}
