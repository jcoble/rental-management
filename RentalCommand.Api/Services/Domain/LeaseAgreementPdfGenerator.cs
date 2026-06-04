using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using RentalCommand.Core.Entities;

namespace RentalCommand.Api.Services.Domain;

/// <summary>Data needed to render a residential lease agreement PDF (kept provider-agnostic).</summary>
public sealed class LeaseAgreementData
{
    public required Lease Lease { get; init; }

    /// <summary>Landlord / management company name (the lessor on the agreement).</summary>
    public string LandlordName { get; init; } = string.Empty;

    /// <summary>Tenant full name (the lessee).</summary>
    public string TenantName { get; init; } = string.Empty;

    /// <summary>Property name + full address line for the "premises" recital.</summary>
    public string PropertyName { get; init; } = string.Empty;
    public string PropertyAddress { get; init; } = string.Empty;

    /// <summary>Unit number / label, if the premises is a specific unit.</summary>
    public string? UnitNumber { get; init; }

    /// <summary>Two-letter state used in the governing-law clause placeholder.</summary>
    public string State { get; init; } = string.Empty;
}

/// <summary>Renders a clean standard residential lease agreement PDF via QuestPDF.</summary>
public interface ILeaseAgreementPdfGenerator
{
    byte[] Generate(LeaseAgreementData data);
}

/// <inheritdoc cref="ILeaseAgreementPdfGenerator"/>
/// <remarks>
/// Produces a generic, standard residential lease agreement from the 5 captured terms (parties,
/// premises, term, rent, deposit/late-fee/due-day) plus boilerplate clauses. The clauses are sensible
/// defaults only and are NOT legal advice; the governing-law line is a placeholder built from the
/// property's state.
/// </remarks>
public sealed class LeaseAgreementPdfGenerator : ILeaseAgreementPdfGenerator
{
    private static readonly string Accent = Colors.Blue.Darken2;
    private static readonly string MutedColor = Colors.Grey.Darken1;

    public byte[] Generate(LeaseAgreementData data)
    {
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(40);
                page.DefaultTextStyle(t => t.FontSize(10).FontColor(Colors.Black).LineHeight(1.3f));

                page.Header().Element(e => ComposeHeader(e, data));
                page.Content().Element(e => ComposeContent(e, data));
                page.Footer().Element(ComposeFooter);
            });
        });

        return document.GeneratePdf();
    }

    private static void ComposeHeader(IContainer container, LeaseAgreementData data)
    {
        container.Column(col =>
        {
            col.Item().Text("Residential Lease Agreement").FontSize(20).Bold().FontColor(Accent);
            col.Item().PaddingTop(2).Text(data.LandlordName).FontSize(11).SemiBold();
            col.Item().PaddingTop(8).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
        });
    }

    private static void ComposeContent(IContainer container, LeaseAgreementData data)
    {
        var lease = data.Lease;
        var stateLabel = string.IsNullOrWhiteSpace(data.State) ? "the State" : $"the State of {data.State}";
        var premises = string.IsNullOrWhiteSpace(data.UnitNumber)
            ? data.PropertyAddress
            : $"{data.PropertyAddress}, Unit {data.UnitNumber}";

        container.PaddingTop(10).Column(col =>
        {
            col.Spacing(10);

            // Intro recital.
            col.Item().Text(t =>
            {
                t.Span("This Residential Lease Agreement (\"Agreement\") is made between ");
                t.Span(string.IsNullOrWhiteSpace(data.LandlordName) ? "the Landlord" : data.LandlordName).SemiBold();
                t.Span(" (\"Landlord\") and ");
                t.Span(string.IsNullOrWhiteSpace(data.TenantName) ? "the Tenant" : data.TenantName).SemiBold();
                t.Span(" (\"Tenant\"), for the rental of the premises described below.");
            });

            // Key terms table.
            col.Item().PaddingTop(4).Element(e => ComposeTerms(e, data, premises));

            // Standard clauses.
            Clause(col, 1, "Premises",
                $"The Landlord leases to the Tenant the residential premises located at {premises} (the \"Premises\"). " +
                "The Premises are to be used solely as a private residence for the Tenant and the Tenant's immediate family.");

            Clause(col, 2, "Term",
                $"The lease term begins on {Date(lease.StartDate)} and ends on {Date(lease.EndDate)}, unless terminated earlier " +
                "in accordance with this Agreement. Upon expiration, this Agreement does not automatically renew unless agreed in writing by both parties.");

            Clause(col, 3, "Rent",
                $"The Tenant agrees to pay monthly rent of {Money(lease.MonthlyRent)}, due on the {Ordinal(lease.RentDueDay)} day of each month. " +
                "Rent shall be paid to the Landlord at the address designated by the Landlord, without demand, deduction, or set-off.");

            Clause(col, 4, "Late Charges",
                lease.LateFeeAmount > 0m
                    ? $"If rent is not received by the Landlord within the grace period allowed by applicable law, the Tenant shall pay a late charge of {Money(lease.LateFeeAmount)}."
                    : "If rent is not paid when due, the Tenant may be subject to late charges as permitted by applicable law.");

            Clause(col, 5, "Security Deposit",
                lease.SecurityDeposit > 0m
                    ? $"The Tenant shall pay a security deposit of {Money(lease.SecurityDeposit)} prior to occupancy. The deposit will be held and returned in accordance with applicable law, less any lawful deductions for unpaid rent or damage beyond normal wear and tear."
                    : "No security deposit is required under this Agreement, except as the parties may otherwise agree in writing.");

            Clause(col, 6, "Utilities",
                "Except as otherwise agreed in writing, the Tenant shall be responsible for arranging and paying for all utilities and services for the Premises.");

            Clause(col, 7, "Use and Occupancy",
                "The Tenant shall keep the Premises clean and sanitary, comply with all applicable laws and ordinances, and not engage in any illegal or nuisance activity. The Tenant shall not sublet or assign the Premises without the Landlord's prior written consent.");

            Clause(col, 8, "Maintenance and Repairs",
                "The Tenant shall promptly notify the Landlord of any needed repairs. The Tenant is responsible for damage caused by the Tenant, the Tenant's guests, or invitees. The Landlord shall maintain the Premises in a habitable condition as required by law.");

            Clause(col, 9, "Pets",
                "No pets or animals shall be kept on the Premises without the Landlord's prior written consent, except for assistance animals as required by law.");

            Clause(col, 10, "Entry by Landlord",
                "The Landlord may enter the Premises to inspect, make repairs, or show the Premises, with reasonable advance notice as required by law, except in cases of emergency.");

            Clause(col, 11, "Default",
                "If the Tenant fails to pay rent or otherwise breaches this Agreement, the Landlord may pursue all remedies available under applicable law, including termination of the tenancy and recovery of possession.");

            Clause(col, 12, "Governing Law",
                $"This Agreement shall be governed by and construed in accordance with the laws of {stateLabel}. " +
                "If any provision is found unenforceable, the remaining provisions shall remain in full force and effect.");

            Clause(col, 13, "Entire Agreement",
                "This Agreement constitutes the entire agreement between the parties and supersedes any prior understandings. Any amendment must be in writing and signed by both parties.");

            // Signatures.
            col.Item().PaddingTop(20).Element(ComposeSignatures);
        });
    }

    private static void ComposeTerms(IContainer container, LeaseAgreementData data, string premises)
    {
        var lease = data.Lease;
        container.Border(1).BorderColor(Colors.Grey.Lighten1).Padding(10).Column(col =>
        {
            col.Spacing(3);
            TermRow(col, "Landlord", string.IsNullOrWhiteSpace(data.LandlordName) ? "—" : data.LandlordName);
            TermRow(col, "Tenant", string.IsNullOrWhiteSpace(data.TenantName) ? "—" : data.TenantName);
            TermRow(col, "Premises", premises);
            TermRow(col, "Lease Number", string.IsNullOrWhiteSpace(lease.LeaseNumber) ? "—" : lease.LeaseNumber);
            TermRow(col, "Term", $"{Date(lease.StartDate)} to {Date(lease.EndDate)}");
            TermRow(col, "Monthly Rent", $"{Money(lease.MonthlyRent)} (due the {Ordinal(lease.RentDueDay)} of each month)");
            TermRow(col, "Security Deposit", Money(lease.SecurityDeposit));
            TermRow(col, "Late Fee", lease.LateFeeAmount > 0m ? Money(lease.LateFeeAmount) : "—");
        });
    }

    private static void TermRow(ColumnDescriptor col, string label, string value)
    {
        col.Item().Row(row =>
        {
            row.ConstantItem(120).Text(label).SemiBold().FontColor(MutedColor);
            row.RelativeItem().Text(value);
        });
    }

    private static void Clause(ColumnDescriptor col, int number, string heading, string body)
    {
        col.Item().PaddingTop(4).Text(t =>
        {
            t.Span($"{number}. {heading}. ").Bold();
            t.Span(body);
        });
    }

    private static void ComposeSignatures(IContainer container)
    {
        container.Column(col =>
        {
            col.Item().PaddingBottom(6).Text("By signing below, the parties agree to the terms of this Agreement.")
                .Italic().FontColor(MutedColor);

            col.Item().PaddingTop(20).Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    c.Item().LineHorizontal(1).LineColor(Colors.Grey.Darken1);
                    c.Item().PaddingTop(2).Text("Landlord signature").FontColor(MutedColor);
                    c.Item().PaddingTop(10).LineHorizontal(1).LineColor(Colors.Grey.Darken1);
                    c.Item().PaddingTop(2).Text("Date").FontColor(MutedColor);
                });
                row.ConstantItem(40);
                row.RelativeItem().Column(c =>
                {
                    c.Item().LineHorizontal(1).LineColor(Colors.Grey.Darken1);
                    c.Item().PaddingTop(2).Text("Tenant signature").FontColor(MutedColor);
                    c.Item().PaddingTop(10).LineHorizontal(1).LineColor(Colors.Grey.Darken1);
                    c.Item().PaddingTop(2).Text("Date").FontColor(MutedColor);
                });
            });
        });
    }

    private static void ComposeFooter(IContainer container)
    {
        container.Column(col =>
        {
            col.Item().LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
            col.Item().PaddingTop(4).Row(row =>
            {
                row.RelativeItem().Text(
                    "Generated by Rental Command. This document is a template for convenience only and is not legal advice.")
                    .FontSize(8).FontColor(MutedColor);
                row.AutoItem().Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontSize(8).FontColor(MutedColor));
                    t.Span("Page ");
                    t.CurrentPageNumber();
                    t.Span(" of ");
                    t.TotalPages();
                });
            });
        });
    }

    private static string Money(decimal value) => value.ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("en-US"));

    private static string Date(DateTime value) => value.ToString("MMMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture);

    private static string Ordinal(int day)
    {
        if (day is <= 0 or > 31) return $"{day}th";
        var suffix = (day % 100) is >= 11 and <= 13
            ? "th"
            : (day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return $"{day}{suffix}";
    }
}
