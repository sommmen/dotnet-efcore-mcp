namespace SampleApp;

/// <summary>A simple entity with a one-to-many relationship, used to exercise schema discovery
/// and query execution (including navigation property traversal) in the MCP server's tests.</summary>
public class Customer
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Age { get; set; }

    public List<Order> Orders { get; set; } = new();
}
