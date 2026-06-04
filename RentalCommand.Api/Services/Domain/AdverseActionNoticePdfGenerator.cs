using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace RentalCommand.Api.Services.Domain;

/// <summary>Data needed to render an FCRA adverse-action notice PDF (provider-agnostic).</summary>
public sealed class AdverseActionNoticeData
{
    public string? ManagementCompanyName { get; init; }
    public string? PortfolioName { get; init; }

    public string ApplicantName { get; init; } = string.Empty;
    public string? PropertyLine { get; init; }

    public DateTime NoticeDate { get; init; }

    /// <summary>The principal reason(s) the application was denied.</summary>
    public string Reason { get; init; } = string.Empty;

    // --- Credit-reporting agency (CRA) identity (FCRA-required disclosures) ---
    public string CreditReportingAgencyName { get; init; } = string.Empty;
    public string CreditReportingAgencyAddress { get; init; } = string.Empty;
    public string CreditReportingAgencyPhone { get; init; } = string.Empty;
}

/// <summary>Renders an FCRA-compliant adverse-action (application denial) notice PDF via QuestPDF.</summary>
public interface IAdverseActionNoticePdfGenerator
{
    byte[] Generate(AdverseActionNoticeData data);
}

/// <inheritdoc cref="IAdverseActionNoticePdfGenerator"/>
public sealed class AdverseActionNoticePdfGenerator : IAdverseActionNoticePdfGenerator
{
    private static readonly string Accent = Colors.Blue.Darken2;
    private static readonly string MutedColor = Colors.Grey.Darken1;

    public byte[] Generate(AdverseActionNoticeData data)
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

    private static void ComposeHeader(IContainer container, AdverseActionNoticeData data)
    {
        container.Column(col =>
        {
            col.Item().Text("Adverse Action Notice").FontSize(20).Bold().FontColor(Accent);

            var company = string.IsNullOrWhiteSpace(data.ManagementCompanyName)
                ? data.PortfolioName
                : data.ManagementCompanyName;
            if (!string.IsNullOrWhiteSpace(company))
                col.Item().PaddingTop(2).Text(company!).FontSize(11).SemiBold().FontColor(MutedColor);

            col.Item().PaddingTop(8).Row(row =>
            {
                row.RelativeItem().Text($"Applicant: {(string.IsNullOrWhiteSpace(data.ApplicantName) ? "—" : data.ApplicantName)}");
                row.RelativeItem().AlignRight().Text($"Date: {data.NoticeDate:MMMM d, yyyy}");
            });
            if (!string.IsNullOrWhiteSpace(data.PropertyLine))
                col.Item().Text(data.PropertyLine!).FontSize(10);

            col.Item().PaddingTop(8).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
        });
    }

    private static void ComposeContent(IContainer container, AdverseActionNoticeData data)
    {
        container.PaddingTop(8).Column(col =>
        {
            col.Spacing(10);

            // 1. Action taken.
            col.Item().Text("Action taken").FontSize(13).Bold().FontColor(Accent);
            col.Item().Text(
                "We regret to inform you that your rental application has been denied. This is a notice of " +
                "adverse action as required by the federal Fair Credit Reporting Act (FCRA).");

            // 2. Principal reason(s).
            col.Item().PaddingTop(4).Text("Principal reason(s) for the decision").FontSize(13).Bold().FontColor(Accent);
            col.Item().Text(string.IsNullOrWhiteSpace(data.Reason)
                ? "Information contained in a consumer report obtained from the consumer reporting agency named below."
                : data.Reason);

            // 3. Credit-reporting agency that supplied the report.
            col.Item().PaddingTop(4).Text("Consumer reporting agency").FontSize(13).Bold().FontColor(Accent);
            col.Item().Text(
                "Our decision was based in whole or in part on information obtained from the consumer reporting " +
                "agency below. This agency only provided information about your credit history and did not make " +
                "the decision to take this action and is unable to provide you with the specific reasons why the " +
                "decision was made.");
            col.Item().PaddingTop(4).Column(cra =>
            {
                cra.Item().Text(data.CreditReportingAgencyName).SemiBold();
                if (!string.IsNullOrWhiteSpace(data.CreditReportingAgencyAddress))
                    cra.Item().Text(data.CreditReportingAgencyAddress);
                if (!string.IsNullOrWhiteSpace(data.CreditReportingAgencyPhone))
                    cra.Item().Text($"Phone: {data.CreditReportingAgencyPhone}");
            });

            // 4. Applicant's rights (free report within 60 days + dispute accuracy).
            col.Item().PaddingTop(4).Text("Your rights under the FCRA").FontSize(13).Bold().FontColor(Accent);
            col.Item().Text(
                "You have the right to obtain a free copy of your consumer report from the consumer reporting " +
                "agency named above if you request it no later than 60 days after you receive this notice. You " +
                "also have the right to dispute, directly with the consumer reporting agency, the accuracy or " +
                "completeness of any information in your report.");

            col.Item().PaddingTop(8).Text(
                "If you have questions about this decision, you may contact us using the information at the top of " +
                "this notice.")
                .FontSize(9).Italic().FontColor(MutedColor);
        });
    }
}
