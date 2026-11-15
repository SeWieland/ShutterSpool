using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShutterSpool.Infrastructure.Workspaces.Migrations
{
    /// <inheritdoc />
    public partial class InitialWorkspaceControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "__EFMigrationsHistory") OR EXISTS (
                        SELECT 1 FROM pg_class r JOIN pg_namespace n ON n.oid=r.relnamespace
                        WHERE left(n.nspname,3)<>'pg_' AND n.nspname<>'information_schema'
                          AND r.relkind IN ('r','p','v','m','S','f')
                          AND NOT(n.nspname='public' AND r.relname='__EFMigrationsHistory')) THEN
                        RAISE EXCEPTION 'Control initialization requires a fresh database. Existing state is preserved; no conversion is supported.';
                    END IF;
                END $$;
                """);
            migrationBuilder.CreateTable(
                name: "workspaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    StorageIdentity = table.Column<string>(type: "text", nullable: false),
                    StorageRoot = table.Column<string>(type: "text", nullable: false),
                    AllocatedBytes = table.Column<long>(type: "bigint", nullable: false),
                    RuntimeTarget = table.Column<string>(type: "text", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provisioning = table.Column<string>(type: "text", nullable: false),
                    Ready = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspaces", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ingress_routes",
                columns: table => new
                {
                    Handle = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ingress_routes", x => x.Handle);
                    table.ForeignKey(
                        name: "FK_ingress_routes_workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ingress_routes_WorkspaceId",
                table: "ingress_routes",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_workspaces_Name",
                table: "workspaces",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workspaces_OperationId",
                table: "workspaces",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workspaces_RuntimeTarget",
                table: "workspaces",
                column: "RuntimeTarget",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workspaces_StorageIdentity",
                table: "workspaces",
                column: "StorageIdentity",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workspaces_StorageRoot",
                table: "workspaces",
                column: "StorageRoot",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ingress_routes");

            migrationBuilder.DropTable(
                name: "workspaces");
        }
    }
}
