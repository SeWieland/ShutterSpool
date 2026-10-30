using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShutterSpool.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkspaceRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "authorization_grants",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "Role",
                table: "authorization_grants",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Owner");

            migrationBuilder.CreateTable(
                name: "workspace_access_operations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Request = table.Column<string>(type: "text", nullable: false),
                    Result = table.Column<string>(type: "text", nullable: false),
                    ActorIssuer = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ActorSubject = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NotificationStatus = table.Column<string>(type: "text", nullable: true),
                    BeforeState = table.Column<string>(type: "text", nullable: true),
                    AfterState = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspace_access_operations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "workspace_viewer_policy",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IncludeUnreleased = table.Column<bool>(type: "boolean", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    AuthorityRevision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspace_viewer_policy", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "workspace_viewer_policy",
                columns: new[] { "Id", "AuthorityRevision", "IncludeUnreleased", "Revision" },
                values: new object[] { 1, 0L, false, 0L });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new InvalidOperationException("Workspace authorization and its audit cannot be downgraded. Restore a complete compatible recovery set and reconcile current authority before reopening.");
        }
    }
}
