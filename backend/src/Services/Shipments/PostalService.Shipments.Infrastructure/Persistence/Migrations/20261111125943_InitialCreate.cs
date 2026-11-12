using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PostalService.Shipments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IdempotencyKeys",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ShipmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdempotencyKeys", x => new { x.Key, x.UserId });
                });

            migrationBuilder.CreateTable(
                name: "InboxMessages",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxMessages", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    AggregateId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    IsDeadLettered = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PostOfficeReferences",
                columns: table => new
                {
                    ZipCode = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 150, nullable: false),
                    City = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    SourceVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostOfficeReferences", x => x.ZipCode);
                });

            migrationBuilder.CreateTable(
                name: "Shipments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ShipmentNumber = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    WeightGrams = table.Column<long>(type: "INTEGER", nullable: false),
                    WeightCategory = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    OriginZip = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    DestinationZip = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    CurrentZip = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shipments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ShipmentStatusHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ShipmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PreviousStatus = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    PostOfficeZip = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    PerformedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PerformedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Remarks = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsCorrection = table.Column<bool>(type: "INTEGER", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShipmentStatusHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShipmentStatusHistory_Shipments_ShipmentId",
                        column: x => x.ShipmentId,
                        principalTable: "Shipments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_ProcessedAtUtc_IsDeadLettered_OccurredAtUtc",
                table: "OutboxMessages",
                columns: new[] { "ProcessedAtUtc", "IsDeadLettered", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentStatusHistory_ShipmentId_OccurredAtUtc",
                table: "ShipmentStatusHistory",
                columns: new[] { "ShipmentId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_CreatedAtUtc",
                table: "Shipments",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_CurrentZip_Status",
                table: "Shipments",
                columns: new[] { "CurrentZip", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_DestinationZip",
                table: "Shipments",
                column: "DestinationZip");

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_OriginZip",
                table: "Shipments",
                column: "OriginZip");

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_ShipmentNumber",
                table: "Shipments",
                column: "ShipmentNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_Status",
                table: "Shipments",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_WeightCategory",
                table: "Shipments",
                column: "WeightCategory");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IdempotencyKeys");

            migrationBuilder.DropTable(
                name: "InboxMessages");

            migrationBuilder.DropTable(
                name: "OutboxMessages");

            migrationBuilder.DropTable(
                name: "PostOfficeReferences");

            migrationBuilder.DropTable(
                name: "ShipmentStatusHistory");

            migrationBuilder.DropTable(
                name: "Shipments");
        }
    }
}
