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
                name: "workspace_invitation_recipients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Issuer = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Subject = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Label = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspace_invitation_recipients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "workspace_invitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientId = table.Column<Guid>(type: "uuid", nullable: false),
                    InviterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspace_invitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_workspace_invitations_authorization_grants_InviterId",
                        column: x => x.InviterId,
                        principalTable: "authorization_grants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workspace_invitations_workspace_invitation_recipients_Recip~",
                        column: x => x.RecipientId,
                        principalTable: "workspace_invitation_recipients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_workspace_invitation_recipients_Issuer_Subject",
                table: "workspace_invitation_recipients",
                columns: new[] { "Issuer", "Subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workspace_invitations_InviterId",
                table: "workspace_invitations",
                column: "InviterId");

            migrationBuilder.CreateIndex(
                name: "IX_workspace_invitations_RecipientId",
                table: "workspace_invitations",
                column: "RecipientId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workspace_invitations");

            migrationBuilder.DropTable(
                name: "workspace_invitation_recipients");
        }
    }
}
