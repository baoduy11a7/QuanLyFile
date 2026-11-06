using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvoiceManager.Migrations
{
    /// <inheritdoc />
    public partial class AddComprehensiveInvoiceFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AmountBeforeTaxVnd",
                table: "Invoices",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "BuyerCitizenId",
                table: "Invoices",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "Invoices",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "DiscountAmount",
                table: "Invoices",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ExchangeRate",
                table: "Invoices",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "FeeAmount",
                table: "Invoices",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceProperty",
                table: "Invoices",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentMethod",
                table: "Invoices",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReplacedOrAdjustedNote",
                table: "Invoices",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SellerWebsite",
                table: "Invoices",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SignedDate",
                table: "Invoices",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxAmountVnd",
                table: "Invoices",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "TemplateCode",
                table: "Invoices",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "TotalAmountVnd",
                table: "Invoices",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "VehicleNumber",
                table: "Invoices",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerificationResult",
                table: "Invoices",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "AmountBeforeTaxForeign",
                table: "InvoiceDetails",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "AmountBeforeTaxVnd",
                table: "InvoiceDetails",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "DiscountAmount",
                table: "InvoiceDetails",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "DiscountRate",
                table: "InvoiceDetails",
                type: "decimal(5,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxAmountForeign",
                table: "InvoiceDetails",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxAmountVnd",
                table: "InvoiceDetails",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TotalAmountForeign",
                table: "InvoiceDetails",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TotalAmountVnd",
                table: "InvoiceDetails",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AmountBeforeTaxVnd",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "BuyerCitizenId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "DiscountAmount",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "ExchangeRate",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "FeeAmount",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "InvoiceProperty",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "PaymentMethod",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "ReplacedOrAdjustedNote",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "SellerWebsite",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "SignedDate",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "TaxAmountVnd",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "TemplateCode",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "TotalAmountVnd",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "VehicleNumber",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "VerificationResult",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "AmountBeforeTaxForeign",
                table: "InvoiceDetails");

            migrationBuilder.DropColumn(
                name: "AmountBeforeTaxVnd",
                table: "InvoiceDetails");

            migrationBuilder.DropColumn(
                name: "DiscountAmount",
                table: "InvoiceDetails");

            migrationBuilder.DropColumn(
                name: "DiscountRate",
                table: "InvoiceDetails");

            migrationBuilder.DropColumn(
                name: "TaxAmountForeign",
                table: "InvoiceDetails");

            migrationBuilder.DropColumn(
                name: "TaxAmountVnd",
                table: "InvoiceDetails");

            migrationBuilder.DropColumn(
                name: "TotalAmountForeign",
                table: "InvoiceDetails");

            migrationBuilder.DropColumn(
                name: "TotalAmountVnd",
                table: "InvoiceDetails");
        }
    }
}
