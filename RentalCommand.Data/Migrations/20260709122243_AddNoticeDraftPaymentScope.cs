using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNoticeDraftPaymentScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PaymentId",
                table: "NoticeDrafts",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_NoticeDrafts_PaymentId",
                table: "NoticeDrafts",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_NoticeDrafts_PortfolioId_PaymentId_NoticeType_Status",
                table: "NoticeDrafts",
                columns: new[] { "PortfolioId", "PaymentId", "NoticeType", "Status" });

            migrationBuilder.AddForeignKey(
                name: "FK_NoticeDrafts_Payments_PaymentId",
                table: "NoticeDrafts",
                column: "PaymentId",
                principalTable: "Payments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_NoticeDrafts_Payments_PaymentId",
                table: "NoticeDrafts");

            migrationBuilder.DropIndex(
                name: "IX_NoticeDrafts_PaymentId",
                table: "NoticeDrafts");

            migrationBuilder.DropIndex(
                name: "IX_NoticeDrafts_PortfolioId_PaymentId_NoticeType_Status",
                table: "NoticeDrafts");

            migrationBuilder.DropColumn(
                name: "PaymentId",
                table: "NoticeDrafts");
        }
    }
}
