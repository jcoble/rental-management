using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Services.Domain;

/// <summary>Data needed to render an inspection report PDF (kept provider-agnostic).</summary>
public sealed class InspectionReportData
{
    public required Inspection Inspection { get; init; }
    public required IReadOnlyList<InspectionItem> Items { get; init; }
    public string PropertyLine { get; init; } = string.Empty;
    public string? UnitLine { get; init; }

    /// <summary>Item id → decoded photo bytes (only items with a loadable photo appear here).</summary>
    public IReadOnlyDictionary<int, byte[]> PhotosByItemId { get; init; } =
        new Dictionary<int, byte[]>();

    /// <summary>Item id → spawned work-order id (for the failed-items summary).</summary>
    public IReadOnlyDictionary<int, int> WorkOrderIdByItemId { get; init; } =
        new Dictionary<int, int>();
}

/// <summary>Renders a clean inspection report PDF via QuestPDF.</summary>
public interface IInspectionReportPdfGenerator
{
    byte[] Generate(InspectionReportData data);
}

/// <inheritdoc cref="IInspectionReportPdfGenerator"/>
public sealed class InspectionReportPdfGenerator : IInspectionReportPdfGenerator
{
    private static readonly string PassColor = Colors.Green.Darken2;
    private static readonly string FailColor = Colors.Red.Darken2;
    private static readonly string NaColor = Colors.Grey.Darken1;
    private static readonly string PendingColor = Colors.Orange.Darken2;

    public byte[] Generate(InspectionReportData data)
    {
        var inspection = data.Inspection;
        var items = data.Items.OrderBy(i => i.SortOrder).ThenBy(i => i.Id).ToList();
        var failed = items.Where(i => i.Result == InspectionItemResult.Fail).ToList();

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(t => t.FontSize(10).FontColor(Colors.Black));

                page.Header().Element(e => ComposeHeader(e, data));
                page.Content().Element(e => ComposeContent(e, data, items, failed));
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

    private static void ComposeHeader(IContainer container, InspectionReportData data)
    {
        var inspection = data.Inspection;
        container.Column(col =>
        {
            col.Item().Text("Inspection Report").FontSize(20).Bold();
            col.Item().PaddingTop(4).Text(data.PropertyLine).FontSize(12).SemiBold();
            if (!string.IsNullOrWhiteSpace(data.UnitLine))
                col.Item().Text(data.UnitLine!).FontSize(11);

            col.Item().PaddingTop(6).Row(row =>
            {
                row.RelativeItem().Text($"Type: {SplitCamel(inspection.Type.ToString())}");
                row.RelativeItem().Text($"Status: {inspection.Status}");
            });
            col.Item().Row(row =>
            {
                var date = inspection.CompletedAt ?? inspection.ScheduledFor;
                row.RelativeItem().Text($"Date: {date:yyyy-MM-dd}");
                row.RelativeItem().Text($"Inspector: {(string.IsNullOrWhiteSpace(inspection.Inspector) ? "—" : inspection.Inspector)}");
            });

            col.Item().PaddingTop(8).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
        });
    }

    private static void ComposeContent(
        IContainer container,
        InspectionReportData data,
        IReadOnlyList<InspectionItem> items,
        IReadOnlyList<InspectionItem> failed)
    {
        container.PaddingTop(8).Column(col =>
        {
            // ---- Summary counts ----
            var pass = items.Count(i => i.Result == InspectionItemResult.Pass);
            var fail = failed.Count;
            var na = items.Count(i => i.Result == InspectionItemResult.NotApplicable);
            var pending = items.Count(i => i.Result == InspectionItemResult.Pending);

            col.Item().Text(t =>
            {
                t.Span($"{items.Count} items  ·  ");
                t.Span($"{pass} pass").FontColor(PassColor);
                t.Span("  ·  ");
                t.Span($"{fail} fail").FontColor(FailColor);
                t.Span("  ·  ");
                t.Span($"{na} n/a").FontColor(NaColor);
                if (pending > 0)
                {
                    t.Span("  ·  ");
                    t.Span($"{pending} not checked").FontColor(PendingColor);
                }
            });

            if (items.Count == 0)
            {
                col.Item().PaddingTop(12).Text("No checklist items were recorded for this inspection.")
                    .Italic().FontColor(Colors.Grey.Darken1);
            }

            // ---- Items grouped by area ----
            foreach (var group in items.GroupBy(i => i.Area))
            {
                col.Item().PaddingTop(14).Text(string.IsNullOrWhiteSpace(group.Key) ? "General" : group.Key)
                    .FontSize(13).Bold().FontColor(Colors.Blue.Darken2);

                col.Item().PaddingTop(4).Table(table =>
                {
                    table.ColumnsDefinition(cols =>
                    {
                        cols.RelativeColumn(4); // label
                        cols.ConstantColumn(70); // result
                        cols.RelativeColumn(5); // note + photo
                    });

                    table.Header(header =>
                    {
                        header.Cell().Element(HeaderCell).Text("Item");
                        header.Cell().Element(HeaderCell).Text("Result");
                        header.Cell().Element(HeaderCell).Text("Notes");
                    });

                    foreach (var item in group)
                    {
                        table.Cell().Element(BodyCell).Text(item.Label);
                        table.Cell().Element(BodyCell).Text(ResultLabel(item.Result))
                            .FontColor(ResultColor(item.Result)).SemiBold();

                        table.Cell().Element(BodyCell).Column(noteCol =>
                        {
                            if (!string.IsNullOrWhiteSpace(item.Note))
                                noteCol.Item().Text(item.Note);

                            if (data.PhotosByItemId.TryGetValue(item.Id, out var photo) && photo.Length > 0)
                            {
                                noteCol.Item().PaddingTop(4).MaxWidth(180).Image(photo).FitWidth();
                            }

                            if (data.WorkOrderIdByItemId.TryGetValue(item.Id, out var woId))
                            {
                                noteCol.Item().PaddingTop(2)
                                    .Text($"→ Work order #{woId} created")
                                    .FontColor(FailColor).Italic().FontSize(9);
                            }
                        });
                    }
                });
            }

            // ---- Failed-items summary ----
            if (failed.Count > 0)
            {
                col.Item().PaddingTop(18).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
                col.Item().PaddingTop(8).Text("Failed items & work orders created")
                    .FontSize(13).Bold().FontColor(FailColor);

                col.Item().PaddingTop(4).Table(table =>
                {
                    table.ColumnsDefinition(cols =>
                    {
                        cols.RelativeColumn(3); // area
                        cols.RelativeColumn(4); // item
                        cols.ConstantColumn(90); // work order
                    });

                    table.Header(header =>
                    {
                        header.Cell().Element(HeaderCell).Text("Area");
                        header.Cell().Element(HeaderCell).Text("Item");
                        header.Cell().Element(HeaderCell).Text("Work Order");
                    });

                    foreach (var item in failed)
                    {
                        table.Cell().Element(BodyCell).Text(string.IsNullOrWhiteSpace(item.Area) ? "General" : item.Area);
                        table.Cell().Element(BodyCell).Text(item.Label);
                        var woText = data.WorkOrderIdByItemId.TryGetValue(item.Id, out var woId)
                            ? $"#{woId}"
                            : "—";
                        table.Cell().Element(BodyCell).Text(woText);
                    }
                });
            }
        });
    }

    private static IContainer HeaderCell(IContainer c) =>
        c.Background(Colors.Grey.Lighten3).PaddingVertical(4).PaddingHorizontal(6)
         .BorderBottom(1).BorderColor(Colors.Grey.Lighten1);

    private static IContainer BodyCell(IContainer c) =>
        c.PaddingVertical(4).PaddingHorizontal(6).BorderBottom(1).BorderColor(Colors.Grey.Lighten2);

    private static string ResultLabel(InspectionItemResult r) => r switch
    {
        InspectionItemResult.Pass => "Pass",
        InspectionItemResult.Fail => "Fail",
        InspectionItemResult.NotApplicable => "N/A",
        _ => "Not checked",
    };

    private static string ResultColor(InspectionItemResult r) => r switch
    {
        InspectionItemResult.Pass => PassColor,
        InspectionItemResult.Fail => FailColor,
        InspectionItemResult.NotApplicable => NaColor,
        _ => PendingColor,
    };

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
