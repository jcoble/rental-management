using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace RentalCommand.Api.Services.Domain;

/// <summary>A single itemised deduction from a security deposit.</summary>
public sealed record DepositDeduction(string Reason, decimal Amount, string? Notes);

/// <summary>Data needed to render a security-deposit move-out statement PDF (provider-agnostic).</summary>
public sealed class MoveOutStatementData
{
    public string? ManagementCompanyName { get; init; }
    public string? PortfolioName { get; init; }

    public string TenantName { get; init; } = string.Empty;
    public string PropertyLine { get; init; } = string.Empty;
    public string? UnitLine { get; init; }
    public string? LeaseNumber { get; init; }

    public DateTime StatementDate { get; init; }
    public DateTime? MoveOutDate { get; init; }

    public decimal DepositHeld { get; init; }
    public IReadOnlyList<DepositDeduction> Deductions { get; init; } = [];

    /// <summary>Decoded photo bytes attached to the deposit (best-effort; only loadable images appear here).</summary>
    public IReadOnlyList<byte[]> Photos { get; init; } = [];

    public string? Notes { get; init; }

    public decimal TotalDeductions => Deductions.Sum(d => d.Amount);

    /// <summary>Positive = refund owed to the tenant; never below zero (overage shown as amount owed).</summary>
    public decimal NetRefund => Math.Max(0m, DepositHeld - TotalDeductions);

    /// <summary>Positive when deductions exceed the deposit — the tenant owes this much.</summary>
    public decimal AmountOwedByTenant => Math.Max(0m, TotalDeductions - DepositHeld);
}

/// <summary>Renders a photo-backed, plain-language security-deposit move-out statement PDF via QuestPDF.</summary>
public interface IMoveOutStatementPdfGenerator
{
    byte[] Generate(MoveOutStatementData data);
}

/// <inheritdoc cref="IMoveOutStatementPdfGenerator"/>
public sealed class MoveOutStatementPdfGenerator : IMoveOutStatementPdfGenerator
{
    private static readonly string Accent = Colors.Blue.Darken2;
    private static readonly string NegativeColor = Colors.Red.Darken2;
    private static readonly string PositiveColor = Colors.Green.Darken2;
    private static readonly string MutedColor = Colors.Grey.Darken1;

    public byte[] Generate(MoveOutStatementData data)
    {
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(t => t.FontSize(10).FontColor(Colors.Black));

                page.Header().Element(e => ComposeHeader(e, data));
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

    // ── Header ──────────────────────────────────────────────────────────────────────────────────

    private static void ComposeHeader(IContainer container, MoveOutStatementData data)
    {
        container.Column(col =>
        {
            col.Item().Text("Security Deposit Statement").FontSize(20).Bold().FontColor(Accent);

            var company = string.IsNullOrWhiteSpace(data.ManagementCompanyName)
                ? data.PortfolioName
                : data.ManagementCompanyName;
            if (!string.IsNullOrWhiteSpace(company))
                col.Item().PaddingTop(2).Text(company!).FontSize(11).SemiBold().FontColor(MutedColor);

            col.Item().PaddingTop(8).Row(row =>
            {
                row.RelativeItem().Text($"Tenant: {(string.IsNullOrWhiteSpace(data.TenantName) ? "—" : data.TenantName)}");
                row.RelativeItem().AlignRight().Text($"Statement date: {data.StatementDate:MMMM d, yyyy}");
            });
            col.Item().Text(data.PropertyLine).FontSize(10);
            if (!string.IsNullOrWhiteSpace(data.UnitLine))
                col.Item().Text(data.UnitLine!).FontSize(10);

            col.Item().Row(row =>
            {
                row.RelativeItem().Text(string.IsNullOrWhiteSpace(data.LeaseNumber)
                    ? string.Empty
                    : $"Lease: {data.LeaseNumber}");
                row.RelativeItem().AlignRight().Text(data.MoveOutDate.HasValue
                    ? $"Move-out: {data.MoveOutDate.Value:MMMM d, yyyy}"
                    : string.Empty);
            });

            col.Item().PaddingTop(8).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
        });
    }

    // ── Content ─────────────────────────────────────────────────────────────────────────────────

    private static void ComposeContent(IContainer container, MoveOutStatementData data)
    {
        container.PaddingTop(8).Column(col =>
        {
            col.Item().Text(
                "This statement itemises your security deposit and any amounts withheld for damages or " +
                "unpaid charges, as required by law. Supporting photos are included where available.")
                .FontSize(9).Italic().FontColor(MutedColor);

            // ── Deposit held ──
            col.Item().PaddingTop(12).Table(table =>
            {
                table.ColumnsDefinition(cols =>
                {
                    cols.RelativeColumn(5);
                    cols.ConstantColumn(120);
                });

                table.Header(header =>
                {
                    header.Cell().Element(HeaderCell).Text("Item");
                    header.Cell().Element(HeaderCellRight).Text("Amount");
                });

                table.Cell().Element(BodyCell).Text("Security deposit held");
                table.Cell().Element(BodyCellRight).Text(Money(data.DepositHeld));
            });

            // ── Deductions ──
            col.Item().PaddingTop(16).Text("Deductions").FontSize(13).Bold().FontColor(Accent);
            col.Item().PaddingTop(2).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);

            if (data.Deductions.Count == 0)
            {
                col.Item().PaddingTop(6).Text("No deductions were taken from the deposit.")
                    .Italic().FontColor(MutedColor);
            }
            else
            {
                col.Item().PaddingTop(6).Table(table =>
                {
                    table.ColumnsDefinition(cols =>
                    {
                        cols.RelativeColumn(5); // reason + notes
                        cols.ConstantColumn(120); // amount
                    });

                    table.Header(header =>
                    {
                        header.Cell().Element(HeaderCell).Text("Reason");
                        header.Cell().Element(HeaderCellRight).Text("Amount");
                    });

                    foreach (var d in data.Deductions)
                    {
                        table.Cell().Element(BodyCell).Column(c =>
                        {
                            c.Item().Text(string.IsNullOrWhiteSpace(d.Reason) ? "Deduction" : d.Reason);
                            if (!string.IsNullOrWhiteSpace(d.Notes))
                                c.Item().Text(d.Notes!).FontSize(8).Italic().FontColor(MutedColor);
                        });
                        table.Cell().Element(BodyCellRight).Text(Money(d.Amount)).FontColor(NegativeColor);
                    }

                    table.Cell().Element(TotalCell).Text("Total deductions").Bold();
                    table.Cell().Element(TotalCellRight).Text(Money(data.TotalDeductions)).Bold()
                        .FontColor(NegativeColor);
                });
            }

            // ── Net refund / amount owed ──
            col.Item().PaddingTop(16).Table(table =>
            {
                table.ColumnsDefinition(cols =>
                {
                    cols.RelativeColumn(5);
                    cols.ConstantColumn(120);
                });

                if (data.AmountOwedByTenant > 0m)
                {
                    table.Cell().Element(TotalCell).Text("Balance owed by tenant").Bold().FontSize(12);
                    table.Cell().Element(TotalCellRight).Text(Money(data.AmountOwedByTenant)).Bold()
                        .FontSize(12).FontColor(NegativeColor);
                }
                else
                {
                    table.Cell().Element(TotalCell).Text("Net refund to tenant").Bold().FontSize(12);
                    table.Cell().Element(TotalCellRight).Text(Money(data.NetRefund)).Bold()
                        .FontSize(12).FontColor(PositiveColor);
                }
            });

            if (data.AmountOwedByTenant > 0m)
            {
                col.Item().PaddingTop(4).Text(
                    "The itemised deductions above exceed the deposit held. The balance shown is owed by the tenant.")
                    .FontSize(8).Italic().FontColor(MutedColor);
            }

            if (!string.IsNullOrWhiteSpace(data.Notes))
            {
                col.Item().PaddingTop(16).Text("Notes").FontSize(12).Bold().FontColor(Accent);
                col.Item().PaddingTop(2).Text(data.Notes!).FontSize(9);
            }

            // ── Photos ──
            if (data.Photos.Count > 0)
            {
                col.Item().PaddingTop(18).Text("Supporting photos").FontSize(13).Bold().FontColor(Accent);
                col.Item().PaddingTop(2).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);

                col.Item().PaddingTop(6).Column(photoCol =>
                {
                    var index = 1;
                    foreach (var photo in data.Photos)
                    {
                        if (photo.Length == 0) continue;
                        photoCol.Item().PaddingTop(index == 1 ? 0 : 8)
                            .Text($"Photo {index}").FontSize(9).SemiBold().FontColor(MutedColor);
                        photoCol.Item().PaddingTop(2).MaxHeight(280).Image(photo).FitArea();
                        index++;
                    }
                });
            }
        });
    }

    // ── Shared helpers ──────────────────────────────────────────────────────────────────────────

    private static IContainer HeaderCell(IContainer c) =>
        c.Background(Colors.Grey.Lighten3).PaddingVertical(4).PaddingHorizontal(6)
         .BorderBottom(1).BorderColor(Colors.Grey.Lighten1);

    private static IContainer HeaderCellRight(IContainer c) => HeaderCell(c).AlignRight();

    private static IContainer BodyCell(IContainer c) =>
        c.PaddingVertical(4).PaddingHorizontal(6).BorderBottom(1).BorderColor(Colors.Grey.Lighten2);

    private static IContainer BodyCellRight(IContainer c) => BodyCell(c).AlignRight();

    private static IContainer TotalCell(IContainer c) =>
        c.PaddingVertical(4).PaddingHorizontal(6).BorderTop(1).BorderColor(Colors.Grey.Darken1);

    private static IContainer TotalCellRight(IContainer c) => TotalCell(c).AlignRight();

    /// <summary>Formats money: negatives parenthesized, accountant-style.</summary>
    private static string Money(decimal value) =>
        value.ToString("$#,0.00;($#,0.00);$0.00");
}
