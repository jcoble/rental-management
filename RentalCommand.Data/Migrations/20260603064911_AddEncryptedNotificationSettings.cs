using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEncryptedNotificationSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SignalWireProjectIdCipherText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    SignalWireTokenCipherText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    SignalWireSpaceUrlCipherText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    SignalWireFromNumberCipherText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    EnableDailyBriefingMessages = table.Column<bool>(type: "boolean", nullable: false),
                    DailyBriefingSendHourLocal = table.Column<int>(type: "integer", nullable: false),
                    DailyBriefingIncludeEmpty = table.Column<bool>(type: "boolean", nullable: false),
                    DailyBriefingSmsRecipientsCipherText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    DailyBriefingEmailRecipientsCipherText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationSettings");
        }
    }
}
