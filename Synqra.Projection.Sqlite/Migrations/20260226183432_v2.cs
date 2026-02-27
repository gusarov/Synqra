using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Synqra.Projection.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class v2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Command",
                columns: table => new
                {
                    CommandId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContainerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Discriminator = table.Column<string>(type: "TEXT", maxLength: 21, nullable: false),
                    TargetTypeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CollectionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TargetId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Command", x => x.CommandId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Command");
        }
    }
}
