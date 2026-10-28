using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Restaurant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OfflineOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NeedsReview",
                table: "Orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "OfflineNumber",
                table: "Orders",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewNote",
                table: "Orders",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TakenOfflineAt",
                table: "Orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_BranchId_NeedsReview",
                table: "Orders",
                columns: new[] { "BranchId", "NeedsReview" },
                filter: "\"NeedsReview\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_BranchId_NeedsReview",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "NeedsReview",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "OfflineNumber",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ReviewNote",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "TakenOfflineAt",
                table: "Orders");
        }
    }
}
