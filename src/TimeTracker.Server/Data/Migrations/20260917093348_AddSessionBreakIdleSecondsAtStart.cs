using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeTracker.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionBreakIdleSecondsAtStart : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "IdleSecondsAtStart",
                table: "SessionBreaks",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IdleSecondsAtStart",
                table: "SessionBreaks");
        }
    }
}
