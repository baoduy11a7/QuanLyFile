using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvoiceManager.Migrations
{
    /// <inheritdoc />
    public partial class AddRemoteConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RemoteConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TaxAccountId = table.Column<int>(type: "int", nullable: false),
                    ProviderName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Username = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EncryptedPassword = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RememberPassword = table.Column<bool>(type: "bit", nullable: false),
                    LastSyncAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSyncStatus = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    LastSyncMessage = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RemoteConnections_TaxAccounts_TaxAccountId",
                        column: x => x.TaxAccountId,
                        principalTable: "TaxAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteConnections_TaxAccountId_ProviderName",
                table: "RemoteConnections",
                columns: new[] { "TaxAccountId", "ProviderName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RemoteConnections");
        }
    }
}
