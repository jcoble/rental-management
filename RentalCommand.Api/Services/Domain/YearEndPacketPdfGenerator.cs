using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>Renders the year-end accountant packet PDF via QuestPDF.</summary>
public interface IYearEndPacketPdfGenerator
{
    byte[] Generate(YearEndPacketData data);
}

/// <inheritdoc cref="IYearEndPacketPdfGenerator"/>
public sealed class YearEndPacketPdfGenerator : IYearEndPacketPdfGenerator
{
    private static readonly string Accent = Colors.Blue.Darken2;
    private static readonly string NegativeColor = Colors.Red.Darken2;
    private static readonly string MutedColor = Colors.Grey.Darken1;

    public byte[] Generate(YearEndPacketData data)
    {
        var document = Document.Create(container =>
        {
            // Cover page.
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(t => t.FontSize(10).FontColor(Colors.Black));
                page.Content().Element(e => ComposeCover(e, data));
            });

            // Content pages.
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(t => t.FontSize(9).FontColor(Colors.Black));

                page.Header().Element(e => ComposeRunningHeader(e, data));
                page.Content().Element(e => ComposeContent(e, data));
                page.Footer().AlignCenter().Text(txt =>
                {
                    txt.Span("Page ");
                    txt.CurrentPageNumber();
                    txt.Span(" of ");
                    txt.TotalPages();
                });
            });
        });

        return document.GeneratePdf();
    }

    // ── Cover ─────────────────────────────────────────────────────────────────────────────────────

    private static void ComposeCover(IContainer container, YearEndPacketData data)
    {
        container.AlignMiddle().Column(col =>
        {
            col.Item().AlignCenter().Text("Year-End Tax Packet").FontSize(30).Bold().FontColor(Accent);
            col.Item().PaddingTop(8).AlignCenter().Text($"Tax Year {data.Year}").FontSize(20).SemiBold();

            col.Item().PaddingTop(28).AlignCenter().Text(
                string.IsNullOrWhiteSpace(data.PortfolioName) ? "Rental Portfolio" : data.PortfolioName)
                .FontSize(15).SemiBold();

            if (!string.IsNullOrWhiteSpace(data.ManagementCompanyName))
            {
                col.Item().PaddingTop(2).AlignCenter().Text(data.ManagementCompanyName)
                    .FontSize(11).FontColor(MutedColor);
            }

            col.Item().PaddingTop(40).AlignCenter().Text(
                $"Generated {data.GeneratedAt:MMMM d, yyyy}")
                .FontSize(10).FontColor(MutedColor);

            col.Item().PaddingTop(4).AlignCenter().Text(
                "Schedule E summary · per-property P&L · cash-flow summary · rent roll")
                .FontSize(9).Italic().FontColor(MutedColor);
        });
    }

    private static void ComposeRunningHeader(IContainer container, YearEndPacketData data)
    {
        container.Column(col =>
        {
            col.Item().Row(row =>
            {
                row.RelativeItem().Text(
                    string.IsNullOrWhiteSpace(data.PortfolioName) ? "Rental Portfolio" : data.PortfolioName)
                    .SemiBold();
                row.ConstantItem(120).AlignRight().Text($"Tax Year {data.Year}").FontColor(MutedColor);
            });
            col.Item().PaddingTop(4).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
        });
    }

    // ── Content ───────────────────────────────────────────────────────────────────────────────────

    private static void ComposeContent(IContainer container, YearEndPacketData data)
    {
        container.PaddingTop(6).Column(col =>
        {
            ComposeScheduleE(col, data.ScheduleE);
            col.Item().PaddingTop(18);
            ComposePropertyPnL(col, data.Properties);
            col.Item().PaddingTop(18);
            ComposeCashFlow(col, data);
            col.Item().PaddingTop(18);
            ComposeRentRoll(col, data.RentRoll);
        });
    }

    // ── Section 1: Schedule E summary ──────────────────────────────────────────────────────────────

    private static void ComposeScheduleE(ColumnDescriptor col, ScheduleEReport report)
    {
        SectionTitle(col, "Schedule E Summary");
        col.Item().PaddingBottom(2).Text(
            "IRS Schedule E income and deductible-expense totals across the portfolio for the tax year.")
            .FontSize(8).Italic().FontColor(MutedColor);

        if (report.UnallocatedActivity.RequiresAllocation)
        {
            col.Item().PaddingTop(6).Border(1).BorderColor(Colors.Orange.Lighten1).Padding(8).Text(
                $"Needs allocation before filing: {report.UnallocatedActivity.IncomeEntryCount} income entries " +
                $"({report.UnallocatedActivity.RentalIncome:C}) and {report.UnallocatedActivity.ExpenseCount} expenses " +
                $"({report.UnallocatedActivity.TotalExpenses:C}) are not assigned to a property. " +
                "They are excluded from the property Schedule E lines below.")
                .FontSize(8).FontColor(Colors.Orange.Darken3);
        }

        if (report.Properties.Count == 0 && !report.UnallocatedActivity.RequiresAllocation)
        {
            col.Item().PaddingTop(6).Text("No rental income or deductible expenses recorded for this year.")
                .Italic().FontColor(MutedColor);
            return;
        }

        col.Item().PaddingTop(6).Table(table =>
        {
            table.ColumnsDefinition(cols =>
            {
                cols.RelativeColumn(5); // line label
                cols.ConstantColumn(110); // amount
            });

            table.Header(header =>
            {
                header.Cell().Element(HeaderCell).Text("Line");
                header.Cell().Element(HeaderCellRight).Text("Amount");
            });

            MoneyRow(table, "Rental income received", report.TotalRentalIncome);

            foreach (var cat in report.ExpensesByCategory)
            {
                MoneyRow(table, $"  {SplitCamel(cat.Category)}", -cat.Amount);
            }

            MoneyRow(table, "Total expenses", -report.TotalExpenses, bold: true);
            MoneyRow(table, "Net income (loss)", report.NetIncome, bold: true);
        });

        if (report.UnallocatedActivity.RequiresAllocation)
        {
            col.Item().PaddingTop(5).Text(
                $"Reconciled activity including unallocated: income {report.ReconciledTotalRentalIncome:C}; " +
                $"expenses {report.ReconciledTotalExpenses:C}; net {report.ReconciledNetIncome:C}.")
                .FontSize(8).FontColor(MutedColor);
        }
    }

    // ── Section 2: Per-property P&L ────────────────────────────────────────────────────────────────

    private static void ComposePropertyPnL(ColumnDescriptor col, IReadOnlyList<YearEndPropertyPnL> properties)
    {
        SectionTitle(col, "Per-Property Profit & Loss");

        if (properties.Count == 0)
        {
            col.Item().PaddingTop(6).Text("No properties with activity this year.")
                .Italic().FontColor(MutedColor);
            return;
        }

        foreach (var prop in properties)
        {
            col.Item().PaddingTop(10).Text(prop.PropertyName).FontSize(11).Bold().FontColor(Accent);

            col.Item().PaddingTop(2).Table(table =>
            {
                table.ColumnsDefinition(cols =>
                {
                    cols.RelativeColumn(5);
                    cols.ConstantColumn(110);
                });

                table.Header(header =>
                {
                    header.Cell().Element(HeaderCell).Text("Item");
                    header.Cell().Element(HeaderCellRight).Text("Amount");
                });

                MoneyRow(table, "Rental income", prop.Income);

                foreach (var cat in prop.ExpensesByCategory)
                {
                    MoneyRow(table, $"  {SplitCamel(cat.Category)}", -cat.Amount);
                }

                MoneyRow(table, "Total expenses", -prop.TotalExpenses);
                MoneyRow(table, "Net", prop.Net, bold: true);
            });
        }
    }

    // ── Section 3: Cash-flow summary ───────────────────────────────────────────────────────────────

    private static void ComposeCashFlow(ColumnDescriptor col, YearEndPacketData data)
    {
        SectionTitle(col, "Cash-Flow Summary");
        col.Item().PaddingBottom(2).Text("Money in, money out, and net by month for the tax year.")
            .FontSize(8).Italic().FontColor(MutedColor);

        col.Item().PaddingTop(6).Table(table =>
        {
            table.ColumnsDefinition(cols =>
            {
                cols.RelativeColumn(3); // month
                cols.RelativeColumn(3); // in
                cols.RelativeColumn(3); // out
                cols.RelativeColumn(3); // net
            });

            table.Header(header =>
            {
                header.Cell().Element(HeaderCell).Text("Month");
                header.Cell().Element(HeaderCellRight).Text("Money In");
                header.Cell().Element(HeaderCellRight).Text("Money Out");
                header.Cell().Element(HeaderCellRight).Text("Net");
            });

            foreach (var m in data.CashFlow)
            {
                table.Cell().Element(BodyCell).Text(m.MonthName);
                table.Cell().Element(BodyCellRight).Text(Money(m.MoneyIn));
                table.Cell().Element(BodyCellRight).Text(Money(m.MoneyOut));
                table.Cell().Element(BodyCellRight).Text(Money(m.Net)).FontColor(SignColor(m.Net));
            }

            table.Cell().Element(TotalCell).Text("Total").Bold();
            table.Cell().Element(TotalCellRight).Text(Money(data.CashFlowMoneyIn)).Bold();
            table.Cell().Element(TotalCellRight).Text(Money(data.CashFlowMoneyOut)).Bold();
            table.Cell().Element(TotalCellRight).Text(Money(data.CashFlowNet)).Bold()
                .FontColor(SignColor(data.CashFlowNet));
        });
    }

    // ── Section 4: Rent roll ───────────────────────────────────────────────────────────────────────

    private static void ComposeRentRoll(ColumnDescriptor col, IReadOnlyList<YearEndRentRollRow> rentRoll)
    {
        SectionTitle(col, "Rent Roll");
        col.Item().PaddingBottom(2).Text("Current leases: unit, tenant, monthly rent, lease term, and past-due balance.")
            .FontSize(8).Italic().FontColor(MutedColor);

        if (rentRoll.Count == 0)
        {
            col.Item().PaddingTop(6).Text("No active leases.").Italic().FontColor(MutedColor);
            return;
        }

        col.Item().PaddingTop(6).Table(table =>
        {
            table.ColumnsDefinition(cols =>
            {
                cols.RelativeColumn(4); // property
                cols.RelativeColumn(2); // unit
                cols.RelativeColumn(4); // tenant
                cols.RelativeColumn(3); // rent
                cols.RelativeColumn(4); // term
                cols.RelativeColumn(3); // past due
            });

            table.Header(header =>
            {
                header.Cell().Element(HeaderCell).Text("Property");
                header.Cell().Element(HeaderCell).Text("Unit");
                header.Cell().Element(HeaderCell).Text("Tenant");
                header.Cell().Element(HeaderCellRight).Text("Monthly Rent");
                header.Cell().Element(HeaderCell).Text("Lease Term");
                header.Cell().Element(HeaderCellRight).Text("Past Due");
            });

            foreach (var r in rentRoll)
            {
                table.Cell().Element(BodyCell).Text(r.PropertyName);
                table.Cell().Element(BodyCell).Text(r.UnitNumber);
                table.Cell().Element(BodyCell).Text(r.TenantName);
                table.Cell().Element(BodyCellRight).Text(Money(r.MonthlyRent));
                table.Cell().Element(BodyCell).Text($"{r.LeaseStart:MM/dd/yy} – {r.LeaseEnd:MM/dd/yy}");
                table.Cell().Element(BodyCellRight).Text(r.PastDueBalance > 0 ? Money(r.PastDueBalance) : "—")
                    .FontColor(r.PastDueBalance > 0 ? NegativeColor : MutedColor);
            }

            var totalRent = rentRoll.Sum(r => r.MonthlyRent);
            var totalPastDue = rentRoll.Sum(r => r.PastDueBalance);

            table.Cell().ColumnSpan(3).Element(TotalCell).Text($"{rentRoll.Count} lease(s)").Bold();
            table.Cell().Element(TotalCellRight).Text(Money(totalRent)).Bold();
            table.Cell().Element(TotalCell).Text(string.Empty);
            table.Cell().Element(TotalCellRight).Text(totalPastDue > 0 ? Money(totalPastDue) : "—").Bold()
                .FontColor(totalPastDue > 0 ? NegativeColor : MutedColor);
        });
    }

    // ── Shared helpers ─────────────────────────────────────────────────────────────────────────────

    private static void SectionTitle(ColumnDescriptor col, string title)
    {
        col.Item().Text(title).FontSize(14).Bold().FontColor(Accent);
        col.Item().PaddingTop(2).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
    }

    private static void MoneyRow(TableDescriptor table, string label, decimal amount, bool bold = false)
    {
        var labelCell = table.Cell().Element(bold ? TotalCell : BodyCell).Text(label.TrimEnd());
        if (bold) labelCell.Bold();

        var amountCell = table.Cell().Element(bold ? TotalCellRight : BodyCellRight)
            .Text(Money(amount)).FontColor(SignColor(amount));
        if (bold) amountCell.Bold();
    }

    private static IContainer HeaderCell(IContainer c) =>
        c.Background(Colors.Grey.Lighten3).PaddingVertical(4).PaddingHorizontal(6)
         .BorderBottom(1).BorderColor(Colors.Grey.Lighten1);

    private static IContainer HeaderCellRight(IContainer c) => HeaderCell(c).AlignRight();

    private static IContainer BodyCell(IContainer c) =>
        c.PaddingVertical(3).PaddingHorizontal(6).BorderBottom(1).BorderColor(Colors.Grey.Lighten2);

    private static IContainer BodyCellRight(IContainer c) => BodyCell(c).AlignRight();

    private static IContainer TotalCell(IContainer c) =>
        c.PaddingVertical(3).PaddingHorizontal(6).BorderTop(1).BorderColor(Colors.Grey.Darken1);

    private static IContainer TotalCellRight(IContainer c) => TotalCell(c).AlignRight();

    private static string SignColor(decimal amount) => amount < 0 ? NegativeColor : Colors.Black;

    /// <summary>Formats money: negatives parenthesized, accountant-style.</summary>
    private static string Money(decimal value) =>
        value.ToString("$#,0.00;($#,0.00);$0.00");

    private static string SplitCamel(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var sb = new System.Text.StringBuilder(value.Length + 4);
        for (var i = 0; i < value.Length; i++)
        {
            if (i > 0 && char.IsUpper(value[i]) && !char.IsUpper(value[i - 1]))
                sb.Append(' ');
            sb.Append(value[i]);
        }
        return sb.ToString();
    }
}
