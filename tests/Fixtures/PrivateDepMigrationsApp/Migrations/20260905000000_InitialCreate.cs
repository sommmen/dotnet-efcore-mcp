using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PrivateDependencyLib;
using SplitContextApp;

#nullable disable

namespace PrivateDepMigrationsApp.Migrations
{
    [DbContext(typeof(SplitDbContext))]
    [Migration("20260905000000_InitialCreate")]
    public partial class InitialCreate : Migration
    {
        private readonly string _marker = PrivateMarker.Value;

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Widgets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Widgets", x => x.Id);
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "Widgets");
        }

        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
            modelBuilder.HasAnnotation("ProductVersion", "10.0.11");

            modelBuilder.Entity("SplitContextApp.Widget", b =>
                {
                    b.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");
                    b.Property<string>("Name").IsRequired().HasColumnType("TEXT");
                    b.HasKey("Id");
                    b.ToTable("Widgets");
                });
        }
    }
}
