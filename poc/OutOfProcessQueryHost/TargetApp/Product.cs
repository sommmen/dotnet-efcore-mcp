namespace TargetApp;

/// <summary>A simple entity in the "target application"'s own model. QueryHost never sees this
/// class at compile time - it only ever touches <see cref="Product"/> instances through
/// reflection and EF Core's non-generic APIs, after loading TargetApp.dll from disk.</summary>
public class Product
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public decimal Price { get; set; }
}
