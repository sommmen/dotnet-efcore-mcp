using DotnetEfCoreMcp.Server.Tools;

namespace DotnetEfCoreMcp.Server.Tests.Tools;

public sealed class ToonToolResultFormatterTests
{
    private readonly ToonToolResultFormatter _formatter = new();

    private sealed class Node
    {
        public string? Name { get; set; }
        public Node? Next { get; set; }
    }

    [Fact]
    public void Format_OmitsNullProperties()
    {
        var value = new { Name = "Alice", Role = (string?)null };

        var toon = _formatter.Format(value);

        Assert.Contains("Name", toon);
        Assert.DoesNotContain("Role", toon);
    }

    [Fact]
    public void Format_IgnoresReferenceCycles()
    {
        var node = new Node { Name = "root" };
        node.Next = node;

        var toon = _formatter.Format(node);

        Assert.Contains("root", toon);
    }

    [Fact]
    public void Format_EncodesTabularArrayOfObjects()
    {
        var value = new
        {
            users = new[]
            {
                new { id = 1, name = "Alice", role = "admin" },
                new { id = 2, name = "Bob", role = "user" },
            },
        };

        var toon = _formatter.Format(value);

        Assert.Contains("users[2]{id,name,role}:", toon);
        Assert.Contains("1,Alice,admin", toon);
        Assert.Contains("2,Bob,user", toon);
    }

    [Fact]
    public void Format_EncodesPrimitiveValue()
    {
        var toon = _formatter.Format(42);

        Assert.Equal("42", toon.Trim());
    }

    [Fact]
    public void Format_EncodesNestedObject()
    {
        var value = new { outer = new { inner = new { value = "deep" } } };

        var toon = _formatter.Format(value);

        Assert.Contains("outer", toon);
        Assert.Contains("inner", toon);
        Assert.Contains("deep", toon);
    }
}
