using System.ComponentModel.DataAnnotations.Schema;

namespace SampleApp;

/// <summary>A simple entity with a one-to-many relationship, used to exercise schema discovery
/// and query execution (including navigation property traversal) in the MCP server's tests.</summary>
public class Customer
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Age { get; set; }

    public List<Order> Orders { get; set; } = new();

    /// <summary>Not part of the EF model on purpose - exercises that query result projection only
    /// includes EF-mapped scalar properties, not every public CLR property.</summary>
    [NotMapped]
    public string DisplayLabel => $"{Name} ({Age})";
}
