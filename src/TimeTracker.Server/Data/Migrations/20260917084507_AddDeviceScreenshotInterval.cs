using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeTracker.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceScreenshotInterval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ScreenshotIntervalMinutes",
                table: "Devices",
                type: "integer",
                nullable: false,
                // 10, not 0: existing devices keep the interval they were already capturing at.
                defaultValue: 10);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ScreenshotIntervalMinutes",
                table: "Devices");
        }
    }
}
