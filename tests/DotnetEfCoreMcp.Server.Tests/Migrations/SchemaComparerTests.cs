using DotnetEfCoreMcp.Server.Migrations;
using EfSchemaCompare;

namespace DotnetEfCoreMcp.Server.Tests.Migrations;

public sealed class SchemaComparerTests
{
    private static SchemaComparer.FlatEntry Different(string column, string attribute, string? expected, string? found) =>
        new(CompareState.Different, new SchemaDriftEntry
        {
            Kind = "Column",
            Table = "OrderLines",
            Column = column,
            Attribute = attribute,
            Expected = expected,
            Found = found,
        });

    [Fact]
    public void WithoutComparisonNoise_DropsUndeclaredDefaultAndItsValueGeneratedMismatch()
    {
        var entries = new[]
        {
            Different("IsPurchaseInvoiced", "DefaultValueSql", null, "CONVERT([bit],(0))"),
            Different("IsPurchaseInvoiced", "ValueGenerated", "Never", "OnAdd"),
        };

        Assert.Empty(SchemaComparer.WithoutComparisonNoise(entries));
    }

    [Fact]
    public void WithoutComparisonNoise_DropsColumnTypesDifferingOnlyInCase()
    {
        var entries = new[] { Different("Content", "ColumnType", "NVARCHAR(MAX)", "nvarchar(max)") };

        Assert.Empty(SchemaComparer.WithoutComparisonNoise(entries));
    }

    [Fact]
    public void WithoutComparisonNoise_KeepsRealDrift()
    {
        var entries = new[]
        {
            Different("Status", "DefaultValueSql", "1", "0"),
            Different("Status", "ValueGenerated", "Never", "OnAdd"),
            Different("Price", "ColumnType", "decimal(18,2)", "decimal(18,4)"),
            Different("Code", "ValueGenerated", "Never", "OnAdd"),
        };

        var kept = SchemaComparer.WithoutComparisonNoise(entries).Select(e => (e.Entry.Column, e.Entry.Attribute)).ToList();

        Assert.Equal(
            [("Status", "DefaultValueSql"), ("Status", "ValueGenerated"), ("Price", "ColumnType"), ("Code", "ValueGenerated")],
            kept);
    }
}
