using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InterviewAssistant.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class Rc3RealtimeRelay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AllowanceAtHeartbeatSeconds",
                table: "interview_sessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowanceAtHeartbeatSeconds",
                table: "interview_sessions");
        }
    }
}
