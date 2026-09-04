using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Schema;
using DotnetEfCoreMcp.Server.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace DotnetEfCoreMcp.Server.Tests.Schema;

public sealed class SchemaBuilderTests
{
    private static (Microsoft.EntityFrameworkCore.DbContext Context, IDisposable Db) CreateSampleAppContext()
    {
        var db = new SqliteTestDatabase();
        var service = new AssemblyLoaderService();
        var handle = service.Load(FixturePaths.SampleAppDllPath);
        var descriptor = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors.Single(d => d.Name == "SampleAppDbContext");
        var context = DbContextActivator.CreateInstance(descriptor.ClrType, db.ToRegistryEntry(), DatabaseProvider.Sqlite);
        return (context, db);
    }

    [Fact]
    public void Build_IncludesBothFixtureEntities()
    {
        var (context, db) = CreateSampleAppContext();
        using (context)
        using (db)
        {
            var schema = SchemaBuilder.Build(context);

            var entityNames = schema.Entities.Select(e => e.Name).ToHashSet();
            Assert.Contains("Customer", entityNames);
            Assert.Contains("Order", entityNames);
        }
    }

    [Fact]
    public void Build_CustomerHasOrdersCollectionNavigation()
    {
        var (context, db) = CreateSampleAppContext();
        using (context)
        using (db)
        {
            var schema = SchemaBuilder.Build(context);

            var customer = schema.Entities.Single(e => e.Name == "Customer");
            var navigation = customer.Navigations.Single(n => n.Name == "Orders");

            Assert.True(navigation.IsCollection);
            Assert.Equal("Order", navigation.TargetEntity);
        }
    }

    [Fact]
    public void Build_OrderHasCustomerReferenceNavigationAndForeignKey()
    {
        var (context, db) = CreateSampleAppContext();
        using (context)
        using (db)
        {
            var schema = SchemaBuilder.Build(context);

            var order = schema.Entities.Single(e => e.Name == "Order");
            var navigation = order.Navigations.Single(n => n.Name == "Customer");
            Assert.False(navigation.IsCollection);

            var foreignKey = Assert.Single(order.ForeignKeys);
            Assert.Contains("CustomerId", foreignKey.Properties);
            Assert.Equal("Customer", foreignKey.PrincipalEntity);
        }
    }

    [Fact]
    public void Build_CustomerPrimaryKeyIsId()
    {
        var (context, db) = CreateSampleAppContext();
        using (context)
        using (db)
        {
            var schema = SchemaBuilder.Build(context);

            var customer = schema.Entities.Single(e => e.Name == "Customer");
            Assert.Equal(["Id"], customer.PrimaryKeyProperties);
        }
    }

    [Fact]
    public void Build_CustomerEntityIncludesRelationalTableMappingAndComment()
    {
        var (context, db) = CreateSampleAppContext();
        using (context)
        using (db)
        {
            var schema = SchemaBuilder.Build(context);

            var customer = schema.Entities.Single(e => e.Name == "Customer");
            // Sqlite has no schema/catalog concept, so Schema legitimately stays null even
            // though it's a relational provider - only Comment/PrimaryKeyName are asserted here.
            Assert.Null(customer.Schema);
            Assert.Equal("Registered customers.", customer.Comment);
            Assert.NotNull(customer.PrimaryKeyName);
        }
    }

    [Fact]
    public void Build_CustomerNameHasRelationalAndCoreFacetsPopulated()
    {
        var (context, db) = CreateSampleAppContext();
        using (context)
        using (db)
        {
            var schema = SchemaBuilder.Build(context);

            var customer = schema.Entities.Single(e => e.Name == "Customer");
            var name = customer.Properties.Single(p => p.Name == "Name");

            Assert.Equal(200, name.MaxLength);
            Assert.False(name.IsUnicode);
            Assert.False(name.IsFixedLength);
            Assert.Equal("The customer's display name.", name.Comment);
        }
    }

    [Fact]
    public void Build_CustomerIdHasValueGeneratedOnAdd()
    {
        var (context, db) = CreateSampleAppContext();
        using (context)
        using (db)
        {
            var schema = SchemaBuilder.Build(context);

            var customer = schema.Entities.Single(e => e.Name == "Customer");
            var id = customer.Properties.Single(p => p.Name == "Id");

            Assert.Equal("OnAdd", id.ValueGenerated);
        }
    }

    [Fact]
    public void Build_CustomerHasUniqueIndexOnName()
    {
        var (context, db) = CreateSampleAppContext();
        using (context)
        using (db)
        {
            var schema = SchemaBuilder.Build(context);

            var customer = schema.Entities.Single(e => e.Name == "Customer");
            var index = Assert.Single(customer.Indexes ?? []);

            Assert.Equal(["Name"], index.Properties);
            Assert.True(index.IsUnique);
            Assert.Equal("IX_Customers_Name", index.Name);
        }
    }

    [Fact]
    public void Build_OrderAmountHasPrecisionScaleAndDefaultValueSql()
    {
        var (context, db) = CreateSampleAppContext();
        using (context)
        using (db)
        {
            var schema = SchemaBuilder.Build(context);

            var order = schema.Entities.Single(e => e.Name == "Order");
            var amount = order.Properties.Single(p => p.Name == "Amount");

            Assert.Equal(18, amount.Precision);
            Assert.Equal(2, amount.Scale);
            Assert.Equal("0.0", amount.DefaultValueSql);
        }
    }

    [Fact]
    public void Build_OrderCreatedAtUtcHasDefaultValueSql()
    {
        var (context, db) = CreateSampleAppContext();
        using (context)
        using (db)
        {
            var schema = SchemaBuilder.Build(context);

            var order = schema.Entities.Single(e => e.Name == "Order");
            var createdAt = order.Properties.Single(p => p.Name == "CreatedAtUtc");

            Assert.Equal("CURRENT_TIMESTAMP", createdAt.DefaultValueSql);
        }
    }

    [Fact]
    public void Build_OrderToCustomerForeignKeyHasCascadeDeleteBehaviorAndIsNotUnique()
    {
        var (context, db) = CreateSampleAppContext();
        using (context)
        using (db)
        {
            var schema = SchemaBuilder.Build(context);

            var order = schema.Entities.Single(e => e.Name == "Order");
            var foreignKey = Assert.Single(order.ForeignKeys);

            Assert.Equal("Cascade", foreignKey.DeleteBehavior);
            Assert.False(foreignKey.IsUnique);
            Assert.NotNull(foreignKey.ConstraintName);
        }
    }

    [Fact]
    public void Build_OrderToCustomerNavigationMirrorsForeignKeyDeleteBehaviorAndProperties()
    {
        var (context, db) = CreateSampleAppContext();
        using (context)
        using (db)
        {
            var schema = SchemaBuilder.Build(context);

            var order = schema.Entities.Single(e => e.Name == "Order");
            var navigation = order.Navigations.Single(n => n.Name == "Customer");

            Assert.True(navigation.IsOnDependent);
            Assert.Equal("Cascade", navigation.DeleteBehavior);
            Assert.Equal(["CustomerId"], navigation.ForeignKeyProperties);
        }
    }

    [Fact]
    public void Build_CustomerToOrdersNavigationIsNotOnDependent()
    {
        var (context, db) = CreateSampleAppContext();
        using (context)
        using (db)
        {
            var schema = SchemaBuilder.Build(context);

            var customer = schema.Entities.Single(e => e.Name == "Customer");
            var navigation = customer.Navigations.Single(n => n.Name == "Orders");

            Assert.False(navigation.IsOnDependent);
        }
    }

    [Fact]
    public void Build_DoesNotRequireAnOpenDatabaseConnection()
    {
        // Uses a connection string pointing at a database file that is never created - schema
        // discovery must still succeed since it only reads compiled model metadata.
        var directory = Path.Combine(AppContext.BaseDirectory, "TestData");
        Directory.CreateDirectory(directory);
        var neverCreatedPath = Path.Combine(directory, $"never_created_{Guid.NewGuid():N}.db");

        var service = new AssemblyLoaderService();
        var handle = service.Load(FixturePaths.SampleAppDllPath);
        var descriptor = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors.Single(d => d.Name == "SampleAppDbContext");
        var entry = new Server.Connections.ConnectionRegistryEntry
        {
            Name = "Unreachable",
            Provider = Server.Connections.DatabaseProvider.Sqlite,
            ConnectionString = $"Data Source={neverCreatedPath}",
            AccessPolicy = new Server.Connections.ConnectionAccessPolicy
            {
                AllowContexts = [],
                DenyContexts = [],
                AllowEntities = [],
                DenyEntities = [],
            },
        };

        using var context = DbContextActivator.CreateInstance(descriptor.ClrType, entry, DatabaseProvider.Sqlite);

        var schema = SchemaBuilder.Build(context);

        Assert.NotEmpty(schema.Entities);
    }

    [Fact]
    public void Build_NonRelationalProviderProducesNullStoreTypeInsteadOfThrowing()
    {
        // IProperty.GetColumnType() throws InvalidCastException for providers (e.g. InMemory)
        // that don't supply a RelationalTypeMapping. SchemaBuilder must detect this up front via
        // Database.IsRelational() rather than relying on a catch-all around GetColumnType(),
        // so this exercises that non-relational code path end-to-end.
        var options = new DbContextOptionsBuilder<InMemoryProbeContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString("N"))
            .Options;
        using var context = new InMemoryProbeContext(options);

        var schema = SchemaBuilder.Build(context);

        var entity = schema.Entities.Single(e => e.Name == nameof(InMemoryProbeEntity));
        Assert.NotEmpty(entity.Properties);
        Assert.All(entity.Properties, p => Assert.Null(p.StoreType));
    }

    [Fact]
    public void Build_NonRelationalProviderKeepsRelationalOnlyFacetsNullButPopulatesCoreFacets()
    {
        // Relational-only facets (Schema/ViewName/ViewSchema/Comment/PrimaryKeyName on entities;
        // IsFixedLength/DefaultValueSql/ComputedColumnSql/DefaultValue/Comment on properties;
        // ConstraintName on foreign keys; Name/Filter on indexes) must stay null for a
        // non-relational provider instead of throwing. Core EF metadata that isn't
        // relational-specific (MaxLength/Precision/Scale/IsUnicode/ValueGenerated on properties;
        // DeleteBehavior/IsUnique on foreign keys; IsOnDependent/IsEagerLoaded/ForeignKeyProperties
        // on navigations; IsUnique on indexes) is still populated regardless of provider.
        var options = new DbContextOptionsBuilder<InMemoryProbeContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString("N"))
            .Options;
        using var context = new InMemoryProbeContext(options);

        var schema = SchemaBuilder.Build(context);

        var parent = schema.Entities.Single(e => e.Name == nameof(InMemoryProbeEntity));
        Assert.Null(parent.Schema);
        Assert.Null(parent.ViewName);
        Assert.Null(parent.ViewSchema);
        Assert.Null(parent.Comment);
        Assert.Null(parent.PrimaryKeyName);

        var nameProperty = parent.Properties.Single(p => p.Name == nameof(InMemoryProbeEntity.Name));
        Assert.Null(nameProperty.IsFixedLength);
        Assert.Null(nameProperty.DefaultValueSql);
        Assert.Null(nameProperty.ComputedColumnSql);
        Assert.Null(nameProperty.DefaultValue);
        Assert.Null(nameProperty.Comment);
        // Core (non-relational) facets remain populated even for a non-relational provider.
        Assert.NotNull(nameProperty.ValueGenerated);
        Assert.False(nameProperty.IsUnicode);

        var indexedProperty = parent.Properties.Single(p => p.Name == nameof(InMemoryProbeEntity.Code));
        Assert.Equal(5, indexedProperty.MaxLength);

        var index = Assert.Single(parent.Indexes ?? []);
        Assert.Null(index.Name);
        Assert.Null(index.Filter);
        Assert.True(index.IsUnique);

        var child = schema.Entities.Single(e => e.Name == nameof(InMemoryProbeChild));
        var foreignKey = Assert.Single(child.ForeignKeys);
        Assert.Null(foreignKey.ConstraintName);
        Assert.Equal("Cascade", foreignKey.DeleteBehavior);
        Assert.NotNull(foreignKey.IsUnique);

        var navigation = child.Navigations.Single(n => n.Name == nameof(InMemoryProbeChild.Parent));
        Assert.True(navigation.IsOnDependent);
        Assert.Equal("Cascade", navigation.DeleteBehavior);
        Assert.Equal([nameof(InMemoryProbeChild.ParentId)], navigation.ForeignKeyProperties);
    }

    [Fact]
    public void Build_FormatsNumericDefaultValueWithInvariantCulture()
    {
        // A CLR (non-SQL) default value on a decimal property exercises FormatDefaultValue's
        // IFormattable branch. Using InvariantCulture avoids a decimal separator that would
        // differ under e.g. a comma-decimal culture, keeping serialized output stable.
        using var db = new SqliteTestDatabase();
        var options = new DbContextOptionsBuilder<DefaultValueProbeContext>()
            .UseSqlite(db.ConnectionString)
            .Options;
        using var context = new DefaultValueProbeContext(options);

        var schema = SchemaBuilder.Build(context);

        var entity = schema.Entities.Single(e => e.Name == nameof(DefaultValueProbeEntity));
        var amount = entity.Properties.Single(p => p.Name == nameof(DefaultValueProbeEntity.Amount));
        Assert.Equal("1.5", amount.DefaultValue);
    }

    [Fact]
    public void Build_FormatsByteArrayDefaultValueWithoutThrowing()
    {
        // byte[] is neither IFormattable nor safely handled by Convert.ToString for arbitrary
        // types; FormatDefaultValue must special-case it instead of throwing.
        using var db = new SqliteTestDatabase();
        var options = new DbContextOptionsBuilder<DefaultValueProbeContext>()
            .UseSqlite(db.ConnectionString)
            .Options;
        using var context = new DefaultValueProbeContext(options);

        var schema = SchemaBuilder.Build(context);

        var entity = schema.Entities.Single(e => e.Name == nameof(DefaultValueProbeEntity));
        var rowVersion = entity.Properties.Single(p => p.Name == nameof(DefaultValueProbeEntity.RowVersion));
        Assert.Equal("010203", rowVersion.DefaultValue);
    }

    private sealed class DefaultValueProbeContext(DbContextOptions<DefaultValueProbeContext> options)
        : Microsoft.EntityFrameworkCore.DbContext(options)
    {
        public DbSet<DefaultValueProbeEntity> Probes => Set<DefaultValueProbeEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<DefaultValueProbeEntity>(builder =>
            {
                builder.Property(p => p.Amount).HasDefaultValue(1.5m);
                builder.Property(p => p.RowVersion).HasDefaultValue(new byte[] { 1, 2, 3 });
            });
        }
    }

    private sealed class DefaultValueProbeEntity
    {
        public int Id { get; set; }

        public decimal Amount { get; set; }

        public byte[] RowVersion { get; set; } = [];
    }

    private sealed class InMemoryProbeContext(DbContextOptions<InMemoryProbeContext> options)
        : Microsoft.EntityFrameworkCore.DbContext(options)
    {
        public DbSet<InMemoryProbeEntity> Probes => Set<InMemoryProbeEntity>();

        public DbSet<InMemoryProbeChild> ProbeChildren => Set<InMemoryProbeChild>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<InMemoryProbeEntity>(builder =>
            {
                builder.Property(p => p.Code).HasMaxLength(5);
                builder.Property(p => p.Name).IsUnicode(false);
                builder.HasIndex(p => p.Code).IsUnique();
            });

            modelBuilder.Entity<InMemoryProbeChild>()
                .HasOne(c => c.Parent)
                .WithMany()
                .HasForeignKey(c => c.ParentId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }

    private sealed class InMemoryProbeEntity
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Code { get; set; } = string.Empty;
    }

    private sealed class InMemoryProbeChild
    {
        public int Id { get; set; }

        public int ParentId { get; set; }

        public InMemoryProbeEntity? Parent { get; set; }
    }
}
