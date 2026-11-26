using Iemas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iemas.Infrastructure.Persistence.Migrations
{
    /// <summary>An employee can be marked as Management: they receive escalations only and no mailbox of theirs is watched.</summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261126090000_EmployeeIsManagement")]
    public partial class EmployeeIsManagement : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsManagement",
                table: "employees",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsManagement",
                table: "employees");
        }
    }
}
