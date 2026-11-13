using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Migrations;
using DotnetEfCoreMcp.Server.Tests.TestSupport;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace DotnetEfCoreMcp.Server.Tests.Migrations;

public sealed class MigrationOperationIndexTests : IDisposable
{
    private const string InitialCreateId = "20260906122510_InitialCreate";
    private const string AddCustomerEmailId = "20261113093414_AddCustomerEmail";

    private readonly SqliteTestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    private MigrationOperationIndex BuildSampleAppIndex()
    {
        var handle = new AssemblyLoaderService().Load(FixturePaths.SampleAppDllPath);
        var contextType = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors.Single(d => d.Name == "SampleAppDbContext").ClrType;
        using var context = DbContextActivator.CreateInstance(contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite);
        return MigrationInspector.BuildOperationIndex(context, DatabaseProvider.Sqlite);
    }

    [Fact]
    public void SampleApp_AttributesAddedColumnToLaterMigration()
    {
        var index = BuildSampleAppIndex();

        Assert.Equal(AddCustomerEmailId, index.FindColumn(null, "Customers", "Email"));
        Assert.Equal(AddCustomerEmailId, index.FindColumn("main", "customers", "EMAIL"));
    }

    [Fact]
    public void SampleApp_AttributesCreateTableAndItsColumnsToInitialCreate()
    {
        var index = BuildSampleAppIndex();

        Assert.Equal(InitialCreateId, index.FindColumn(null, "Customers", "Name"));
        Assert.Equal(InitialCreateId, index.FindTable(null, "Customers"));
        Assert.Null(index.FindColumn(null, "Customers", "Missing"));
        Assert.Null(index.FindTable(null, "Missing"));
    }

    [Fact]
    public void Renames_AttributeNewNamesToTheRenamingMigration()
    {
        var index = MigrationOperationIndex.Build(
        [
            ("1_Create", [new CreateTableOperation { Name = "Orders", Schema = "sales", Columns = { new AddColumnOperation { Name = "Total", Table = "Orders" } } }]),
            ("2_RenameColumn", [new RenameColumnOperation { Table = "Orders", Schema = "sales", Name = "Total", NewName = "Amount" }]),
            ("3_RenameTable", [new RenameTableOperation { Name = "Orders", Schema = "sales", NewName = "PurchaseOrders" }]),
        ]);

        Assert.Equal("1_Create", index.FindColumn("sales", "Orders", "Total"));
        Assert.Equal("2_RenameColumn", index.FindColumn("sales", "Orders", "Amount"));
        Assert.Equal("3_RenameTable", index.FindTable("sales", "PurchaseOrders"));
        Assert.Equal("3_RenameTable", index.FindColumn("sales", "PurchaseOrders", "Amount"));
        Assert.Null(index.FindTable("other", "Orders"));
    }

    [Fact]
    public void ResolveMigrationId_WithAmbiguousSuffix_ListsMatches()
    {
        string[] known = ["1_AddEmail", "2_FixAddEmail"];

        var ex = Assert.Throws<MigrationInspectionException>(() => MigrationInspector.ResolveMigrationId(known, "Email"));

        Assert.Contains("ambiguous", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1_AddEmail", ex.Message, StringComparison.Ordinal);
        Assert.Contains("2_FixAddEmail", ex.Message, StringComparison.Ordinal);
        Assert.Equal("1_AddEmail", MigrationInspector.ResolveMigrationId(known, "addemail"));
    }
}
