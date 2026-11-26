using Iemas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iemas.Infrastructure.Persistence.Migrations
{
    /// <summary>A company installer key may let a PC enroll itself with an email and password.</summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261126080000_AgentSelfEnrollment")]
    public partial class AgentSelfEnrollment : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowsSelfEnrollment",
                table: "agent_install_keys",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowsSelfEnrollment",
                table: "agent_install_keys");
        }
    }
}
