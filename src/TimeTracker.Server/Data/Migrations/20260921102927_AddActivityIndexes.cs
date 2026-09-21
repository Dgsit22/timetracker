using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeTracker.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddActivityIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_UrlVisits_DeviceId_StartedAtUtc",
                table: "UrlVisits",
                columns: new[] { "DeviceId", "StartedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_UrlVisits_StartedAtUtc",
                table: "UrlVisits",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_UrlVisits_UserName_StartedAtUtc",
                table: "UrlVisits",
                columns: new[] { "UserName", "StartedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_SessionBreaks_BreakStartUtc",
                table: "SessionBreaks",
                column: "BreakStartUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SessionBreaks_DeviceId_BreakStartUtc",
                table: "SessionBreaks",
                columns: new[] { "DeviceId", "BreakStartUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_SessionBreaks_UserName_BreakStartUtc",
                table: "SessionBreaks",
                columns: new[] { "UserName", "BreakStartUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_Screenshots_CapturedAtUtc",
                table: "Screenshots",
                column: "CapturedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Screenshots_DeviceId_CapturedAtUtc",
                table: "Screenshots",
                columns: new[] { "DeviceId", "CapturedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_Screenshots_UserName_CapturedAtUtc",
                table: "Screenshots",
                columns: new[] { "UserName", "CapturedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_IdlePeriods_DeviceId_StartedAtUtc",
                table: "IdlePeriods",
                columns: new[] { "DeviceId", "StartedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_IdlePeriods_StartedAtUtc",
                table: "IdlePeriods",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_IdlePeriods_UserName_StartedAtUtc",
                table: "IdlePeriods",
                columns: new[] { "UserName", "StartedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_AppUsageEvents_DeviceId_StartedAtUtc",
                table: "AppUsageEvents",
                columns: new[] { "DeviceId", "StartedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_AppUsageEvents_StartedAtUtc",
                table: "AppUsageEvents",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AppUsageEvents_UserName_StartedAtUtc",
                table: "AppUsageEvents",
                columns: new[] { "UserName", "StartedAtUtc" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UrlVisits_DeviceId_StartedAtUtc",
                table: "UrlVisits");

            migrationBuilder.DropIndex(
                name: "IX_UrlVisits_StartedAtUtc",
                table: "UrlVisits");

            migrationBuilder.DropIndex(
                name: "IX_UrlVisits_UserName_StartedAtUtc",
                table: "UrlVisits");

            migrationBuilder.DropIndex(
                name: "IX_SessionBreaks_BreakStartUtc",
                table: "SessionBreaks");

            migrationBuilder.DropIndex(
                name: "IX_SessionBreaks_DeviceId_BreakStartUtc",
                table: "SessionBreaks");

            migrationBuilder.DropIndex(
                name: "IX_SessionBreaks_UserName_BreakStartUtc",
                table: "SessionBreaks");

            migrationBuilder.DropIndex(
                name: "IX_Screenshots_CapturedAtUtc",
                table: "Screenshots");

            migrationBuilder.DropIndex(
                name: "IX_Screenshots_DeviceId_CapturedAtUtc",
                table: "Screenshots");

            migrationBuilder.DropIndex(
                name: "IX_Screenshots_UserName_CapturedAtUtc",
                table: "Screenshots");

            migrationBuilder.DropIndex(
                name: "IX_IdlePeriods_DeviceId_StartedAtUtc",
                table: "IdlePeriods");

            migrationBuilder.DropIndex(
                name: "IX_IdlePeriods_StartedAtUtc",
                table: "IdlePeriods");

            migrationBuilder.DropIndex(
                name: "IX_IdlePeriods_UserName_StartedAtUtc",
                table: "IdlePeriods");

            migrationBuilder.DropIndex(
                name: "IX_AppUsageEvents_DeviceId_StartedAtUtc",
                table: "AppUsageEvents");

            migrationBuilder.DropIndex(
                name: "IX_AppUsageEvents_StartedAtUtc",
                table: "AppUsageEvents");

            migrationBuilder.DropIndex(
                name: "IX_AppUsageEvents_UserName_StartedAtUtc",
                table: "AppUsageEvents");
        }
    }
}
