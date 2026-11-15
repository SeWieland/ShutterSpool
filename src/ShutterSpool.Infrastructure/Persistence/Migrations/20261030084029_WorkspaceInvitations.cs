using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShutterSpool.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkspaceInvitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workspace_invitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientIssuer = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    RecipientSubject = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    RecipientLabel = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspace_invitations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_workspace_invitations_RecipientIssuer_RecipientSubject",
                table: "workspace_invitations",
                columns: new[] { "RecipientIssuer", "RecipientSubject" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workspace_invitations");
        }
    }
}
