using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Migrations;
using DotnetEfCoreMcp.Server.Tests.TestSupport;
using EfSchemaCompare;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetEfCoreMcp.Server.Tests.Migrations;

public sealed class SchemaComparerTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();

    public void Dispose() => _db.Dispose();

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

    [Theory]
    [InlineData("1.0E0", "1.0000000000000000e+000")]
    [InlineData("COALESCE(CAST([OrdersMatch] as float) / NULLIF(CAST([ProcessedLines] as float),0), 0)", "coalesce(CONVERT([float],[OrdersMatch])/nullif(CONVERT([float],[ProcessedLines]),(0)),(0))")]
    [InlineData("[OrderNumberSnapshot] + '.' + CONVERT(varchar(11), [Index])", "[OrderNumberSnapshot]+'.')+CONVERT([varchar](11),[Index]")]
    [InlineData("MONTH(SWITCHOFFSET(OrderDateTime, '+02:00'))", "datepart(month,switchoffset([OrderDateTime],'+02:00'))")]
    [InlineData("CONVERT(bit, CASE WHEN [IsCompany] = 1 OR NULLIF(LTRIM(RTRIM([CompanyName])), N'') IS NOT NULL THEN 1 ELSE 0 END)", "CONVERT([bit],case when [IsCompany]=(1) OR nullif(ltrim(rtrim([CompanyName])),N'') IS NOT NULL then (1) else (0) end)")]
    [InlineData("\n\t\tCASE WHEN a <> '' THEN a ELSE '' END\n\t\t+\n\t\tCASE WHEN b <> '' THEN ' '+b ELSE '' END\n\t\t+\n\t\tCASE WHEN c <> '' THEN ' '+c ELSE '' END", "(case when [a]<>'' then [a] else '' end+case when [b]<>'' then ' '+[b] else '' end)+case when [c]<>'' then ' '+[c] else '' end")]
    [InlineData("[A] + [B] - [C] + [D]", "(([A]+[B])-[C])+[D]")]
    public void SqlExpressionsMatch_TreatsDatabaseReformattingAsEquivalent(string expected, string found) =>
        Assert.True(SchemaComparer.SqlExpressionsMatch(expected, found));

    [Theory]
    [InlineData("1", "0")]
    [InlineData("YEAR(OrderDateTime)", "datepart(month,[OrderDateTime])")]
    [InlineData("[A] + [B]", "[A] - [B]")]
    [InlineData("[A] * ([B] + [C])", "[A] * [B] + [C]")]
    [InlineData("[A] - ([B] + [C])", "[A] - [B] + [C]")]
    [InlineData("([A] + [B]) * [C]", "[A] + [B] * [C]")]
    public void SqlExpressionsMatch_KeepsGenuinelyDifferentExpressions(string expected, string found) =>
        Assert.False(SchemaComparer.SqlExpressionsMatch(expected, found));

    [Fact]
    public void WithoutComparisonNoise_DropsInferredTypeAndNullabilityOfComputedColumnsOnly()
    {
        var entries = new[]
        {
            Different("FullName", "ColumnType", "nvarchar(max)", "nvarchar(4000)"),
            Different("FullName", "Nullability", "NOT NULL", "NULL"),
            Different("DisplayName", "ColumnType", "nvarchar(max)", "nvarchar(100)"),
        };

        var kept = SchemaComparer.WithoutComparisonNoise(entries, new HashSet<(string, string)> { ("OrderLines", "FullName") })
            .Select(e => e.Entry.Column).ToList();

        Assert.Equal(["DisplayName"], kept);
    }

    [Fact]
    public void WithoutComparisonNoise_DropsReformattedSqlButKeepsChangedSql()
    {
        var entries = new[]
        {
            Different("Month", "ComputedColumnSql", "MONTH(OrderDateTime)", "datepart(month,[OrderDateTime])"),
            Different("Year", "ComputedColumnSql", "YEAR(OrderDateTime)", "datepart(month,[OrderDateTime])"),
        };

        var kept = SchemaComparer.WithoutComparisonNoise(entries).Select(e => e.Entry.Column).ToList();

        Assert.Equal(["Year"], kept);
    }

    [Fact]
    public void WithoutComparisonNoise_DropsClientOnlyDeleteBehaviorsAgainstNoAction()
    {
        var entries = new[]
        {
            Different("OrderId", "DeleteBehavior", "ClientCascade", "NoAction"),
            Different("CustomerId", "DeleteBehavior", "Cascade", "NoAction"),
        };

        var kept = SchemaComparer.WithoutComparisonNoise(entries).Select(e => e.Entry.Column).ToList();

        Assert.Equal(["CustomerId"], kept);
    }

    [Fact]
    public void WithoutComparisonNoise_DropsNullabilityOfComputedColumnsOnly()
    {
        var entries = new[]
        {
            Different("CestMonth", "Nullability", "NOT NULL", "NULL"),
            Different("Quantity", "Nullability", "NOT NULL", "NULL"),
        };
        IReadOnlySet<(string, string)> computed = new HashSet<(string, string)> { ("OrderLines", "CestMonth") };

        var kept = SchemaComparer.WithoutComparisonNoise(entries, computed).Select(e => e.Entry.Column).ToList();

        Assert.Equal(["Quantity"], kept);
    }

    [Fact]
    public async Task CompareAsync_ColumnMissingFromDatabaseOnEntityAfterAKeylessEntity_IsReported()
    {
        var result = await CompareModelShapesAsync("ALTER TABLE \"Posts\" DROP COLUMN \"Subtitle\"");

        Assert.True(result.HasDifferences);
        var missing = Assert.Single(result.NotInDatabase);
        Assert.Equal("Column", missing.Kind);
        Assert.Equal("Posts", missing.Table);
        Assert.Equal("Subtitle", missing.Column);
        Assert.Equal("Post", missing.Entity);
    }

    [Fact]
    public async Task CompareAsync_MissingColumnOnSharedTypeJoinEntity_IsReportedUnderItsEfEntityName()
    {
        var result = await CompareModelShapesAsync("DROP TABLE \"PostTag\"; CREATE TABLE \"PostTag\" (\"PostsId\" INTEGER NOT NULL, CONSTRAINT \"PK_PostTag\" PRIMARY KEY (\"PostsId\"))");

        var missing = Assert.Single(result.NotInDatabase, e => e.Kind == "Column");
        Assert.Equal("PostTag", missing.Table);
        Assert.Equal("TagsId", missing.Column);
        Assert.Equal("PostTag", missing.Entity);
    }

    [Fact]
    public async Task CompareAsync_MatchingDatabase_ReportsNoDifferences()
    {
        var result = await CompareModelShapesAsync(sql: null);

        Assert.False(result.HasDifferences, string.Join("; ", result.NotInDatabase.Concat(result.Different).Select(e => $"{e.Kind} {e.Table}.{e.Column} {e.Attribute} {e.Expected} {e.Found}")));
    }

    private async Task<SchemaComparisonResult> CompareModelShapesAsync(string? sql)
    {
        var handle = new AssemblyLoaderService().Load(FixturePaths.ModelShapesAppDllPath);
        var contextType = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors.Single().ClrType;
        var entry = _db.ToRegistryEntry();
        using (var setup = DbContextActivator.CreateInstance(contextType, entry, DatabaseProvider.Sqlite))
        {
            setup.Database.EnsureCreated();
            setup.Database.ExecuteSqlRaw("CREATE VIEW \"ArchivedPostSummaries\" AS SELECT \"Title\" FROM \"Posts\"");
            if (sql is not null)
            {
                setup.Database.ExecuteSqlRaw(sql);
            }
        }

        var context = DbContextActivator.CreateInstance(contextType, entry, DatabaseProvider.Sqlite);
        var comparer = new SchemaComparer(new MigrationsOptions(), NullLogger<SchemaComparer>.Instance);
        return await comparer.CompareAsync(context, entry, DatabaseProvider.Sqlite, new SchemaComparisonRequest(), TestContext.Current.CancellationToken);
    }}
