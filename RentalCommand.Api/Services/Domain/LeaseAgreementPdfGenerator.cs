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

    /// <summary>Two-letter state used to select state-specific clauses and the governing-law clause.</summary>
    public string State { get; init; } = string.Empty;

    /// <summary>
    /// Year the property was built. Drives the federal pre-1978 lead-based-paint disclosure.
    /// Null means unknown (no lead-paint disclosure is rendered).
    /// </summary>
    public int? YearBuilt { get; init; }
}

/// <summary>Renders a clean standard residential lease agreement PDF via QuestPDF.</summary>
public interface ILeaseAgreementPdfGenerator
{
    byte[] Generate(LeaseAgreementData data);
}

/// <inheritdoc cref="ILeaseAgreementPdfGenerator"/>
/// <remarks>
/// Produces a standard residential lease agreement from the captured terms (parties, premises, term,
/// rent, deposit/late-fee/due-day) plus boilerplate clauses. Several clauses (security-deposit return,
/// landlord entry notice, late charges, governing law) are tailored to the property's state via
/// <see cref="StateLeaseRules"/>; the operating state (Ohio) carries real statutory parameters and
/// every other state falls back to a generic profile that defers to applicable law. A federal
/// lead-based-paint disclosure is appended for properties built before 1978. The generated clauses are
/// convenience boilerplate and are NOT legal advice — a disclaimer to that effect is rendered in the
/// document body as well as the footer.
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
        var rules = StateLeaseRules.For(data.State);
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
                (lease.LateFeeAmount > 0m
                    ? $"If rent is not received by the Landlord within the grace period allowed by applicable law, the Tenant shall pay a late charge of {Money(lease.LateFeeAmount)}. "
                    : "If rent is not paid when due, the Tenant may be subject to late charges as permitted by applicable law. ")
                + rules.LateFeeText);

            Clause(col, 5, "Security Deposit",
                lease.SecurityDeposit > 0m
                    ? $"The Tenant shall pay a security deposit of {Money(lease.SecurityDeposit)} prior to occupancy. {rules.DepositReturnText}"
                    : $"No security deposit is required under this Agreement, except as the parties may otherwise agree in writing. {rules.DepositReturnText}");

            Clause(col, 6, "Utilities",
                "Except as otherwise agreed in writing, the Tenant shall be responsible for arranging and paying for all utilities and services for the Premises.");

            Clause(col, 7, "Use and Occupancy",
                "The Tenant shall keep the Premises clean and sanitary, comply with all applicable laws and ordinances, and not engage in any illegal or nuisance activity. The Tenant shall not sublet or assign the Premises without the Landlord's prior written consent.");

            Clause(col, 8, "Maintenance and Repairs",
                "The Tenant shall promptly notify the Landlord of any needed repairs. The Tenant is responsible for damage caused by the Tenant, the Tenant's guests, or invitees. The Landlord shall maintain the Premises in a habitable condition as required by law.");

            Clause(col, 9, "Pets",
                "No pets or animals shall be kept on the Premises without the Landlord's prior written consent, except for assistance animals as required by law.");

            Clause(col, 10, "Entry by Landlord", rules.EntryNoticeText);

            Clause(col, 11, "Default",
                "If the Tenant fails to pay rent or otherwise breaches this Agreement, the Landlord may pursue all remedies available under applicable law, including termination of the tenancy and recovery of possession.");

            Clause(col, 12, "Governing Law",
                $"This Agreement shall be governed by and construed in accordance with the laws of {rules.GoverningLawLabel}"
                + (rules.StatuteCitation is null ? ". " : $", including {rules.StatuteCitation}. ")
                + "If any provision is found unenforceable, the remaining provisions shall remain in full force and effect.");

            Clause(col, 13, "Entire Agreement",
                "This Agreement constitutes the entire agreement between the parties and supersedes any prior understandings. Any amendment must be in writing and signed by both parties.");

            // Required disclosures + in-body legal disclaimer.
            col.Item().PaddingTop(8).Element(e => ComposeDisclosures(e, data, rules));

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

    /// <summary>
    /// Renders the "Required Disclosures" section: always an explicit in-body legal disclaimer, plus the
    /// federal lead-based-paint disclosure for properties built before 1978.
    /// </summary>
    private static void ComposeDisclosures(IContainer container, LeaseAgreementData data, StateLeaseRules rules)
    {
        var requiresLeadDisclosure = data.YearBuilt is { } year && year < 1978;

        container.Column(col =>
        {
            col.Spacing(6);

            col.Item().Text("Required Disclosures").FontSize(12).Bold().FontColor(Accent);

            if (requiresLeadDisclosure)
            {
                col.Item().Text(t =>
                {
                    t.Span("Lead-Based Paint Disclosure (Federal). ").Bold();
                    t.Span(
                        "Housing built before 1978 may contain lead-based paint. Lead from paint, paint chips, " +
                        "and dust can pose health hazards if not managed properly. Lead exposure is especially " +
                        "harmful to young children and pregnant women. Before renting pre-1978 housing, landlords " +
                        "must disclose the presence of known lead-based paint and lead-based paint hazards in the " +
                        "dwelling. The Landlord has provided the Tenant with any such known information and with the " +
                        "EPA/HUD pamphlet \"Protect Your Family From Lead in Your Home.\" "
                        + "(Residential Lead-Based Paint Hazard Reduction Act of 1992, Section 1018; 24 CFR Part 35; 40 CFR Part 745.)");
                });
            }

            col.Item().Text(t =>
            {
                t.Span("Disclaimer. ").Bold();
                t.Span(
                    "This Agreement was generated by Rental Command as a convenience template. The clauses, including " +
                    $"any clauses tailored to {rules.GoverningLawLabel}, are general boilerplate and are NOT legal advice. " +
                    "Laws change and vary by jurisdiction and locality. The Landlord and Tenant should have this " +
                    "Agreement reviewed by a qualified attorney before relying on it.").Italic().FontColor(MutedColor);
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

/// <summary>
/// State-specific statutory parameters used to phrase the security-deposit, landlord-entry, late-fee, and
/// governing-law clauses of a residential lease. The operating state (Ohio) carries real, sourced figures;
/// a few additional common states are seeded for sensible output; every unknown/blank state falls back to a
/// generic profile that defers to "applicable law" (matching the prior behavior but naming the jurisdiction).
/// </summary>
/// <remarks>
/// This is convenience boilerplate, NOT legal advice. Figures are drawn from publicly documented statutes
/// (see citations per state below) and can drift as laws change. Adding a state is a one-line entry — no
/// schema change is required because selection is driven entirely by the lease's existing two-letter state.
/// </remarks>
internal sealed record StateLeaseRules
{
    /// <summary>Two-letter code this rule was resolved for (e.g. "OH"); empty when the source state was blank.</summary>
    public required string StateCode { get; init; }

    /// <summary>Human-readable jurisdiction name used in the governing-law and disclaimer text.</summary>
    public required string GoverningLawLabel { get; init; }

    /// <summary>Sentence describing how/when the security deposit must be returned.</summary>
    public required string DepositReturnText { get; init; }

    /// <summary>Full body of the "Entry by Landlord" clause (notice period).</summary>
    public required string EntryNoticeText { get; init; }

    /// <summary>Sentence describing the state's late-fee posture, appended to the captured late-fee amount.</summary>
    public required string LateFeeText { get; init; }

    /// <summary>Statute reference (e.g. "Ohio Rev. Code Chapter 5321"); null for the generic fallback.</summary>
    public string? StatuteCitation { get; init; }

    private const string GenericDepositReturn =
        "The deposit will be held and returned in accordance with applicable law, less any lawful deductions " +
        "for unpaid rent or damage beyond normal wear and tear.";

    private const string GenericEntry =
        "The Landlord may enter the Premises to inspect, make repairs, or show the Premises, with reasonable " +
        "advance notice as required by law, except in cases of emergency.";

    private const string GenericLateFee =
        "Late charges shall not exceed the amount permitted by applicable law and must be reasonable.";

    /// <summary>
    /// Resolves the rules for a two-letter state code (case-insensitive, trimmed). Unknown or blank input
    /// returns a generic profile.
    /// </summary>
    public static StateLeaseRules For(string? stateCode)
    {
        var code = (stateCode ?? string.Empty).Trim().ToUpperInvariant();
        if (code.Length > 2) code = code[..2];

        return code switch
        {
            "OH" => new StateLeaseRules
            {
                StateCode = "OH",
                GoverningLawLabel = "the State of Ohio",
                StatuteCitation = "Ohio Rev. Code Chapter 5321",
                DepositReturnText =
                    "The deposit will be held and, within thirty (30) days after termination of the tenancy and " +
                    "delivery of possession, returned to the Tenant together with an itemized written statement of " +
                    "any deductions for unpaid rent or damage beyond normal wear and tear. Where a deposit greater " +
                    "than fifty dollars ($50) or one month's rent is held for more than six (6) months, it shall " +
                    "bear interest at five percent (5%) per annum as required by Ohio Rev. Code 5321.16.",
                EntryNoticeText =
                    "The Landlord may enter the Premises to inspect, make repairs, supply services, or show the " +
                    "Premises, and shall give the Tenant at least twenty-four (24) hours' notice of intent to enter " +
                    "and enter only at reasonable times, except in cases of emergency (Ohio Rev. Code 5321.04).",
                LateFeeText =
                    "Ohio does not cap residential late fees by statute, but any late charge must be reasonable.",
            },
            "PA" => new StateLeaseRules
            {
                StateCode = "PA",
                GoverningLawLabel = "the Commonwealth of Pennsylvania",
                StatuteCitation = "68 Pa. Stat. §§ 250.511a-250.512",
                DepositReturnText =
                    "A security deposit may not exceed two (2) months' rent for the first year of the tenancy. The " +
                    "deposit will be returned, with an itemized list of any deductions, within thirty (30) days after " +
                    "termination of the tenancy and delivery of possession.",
                EntryNoticeText =
                    "The Landlord may enter the Premises to inspect, make repairs, or show the Premises, with " +
                    "reasonable advance notice and at reasonable times, except in cases of emergency.",
                LateFeeText =
                    "Any late charge must be reasonable and is enforceable only as permitted by applicable law.",
            },
            "FL" => new StateLeaseRules
            {
                StateCode = "FL",
                GoverningLawLabel = "the State of Florida",
                StatuteCitation = "Fla. Stat. Chapter 83, Part II",
                DepositReturnText =
                    "If the Landlord does not intend to impose a claim on the security deposit, it will be returned " +
                    "within fifteen (15) days after the Tenant vacates. If the Landlord intends to impose a claim, the " +
                    "Landlord will provide written notice by certified mail within thirty (30) days, and the balance " +
                    "will be returned in accordance with Fla. Stat. 83.49.",
                EntryNoticeText =
                    "The Landlord may enter the Premises to inspect, make repairs, or show the Premises, and shall " +
                    "give the Tenant at least twenty-four (24) hours' notice and enter only at reasonable times, " +
                    "except in cases of emergency (Fla. Stat. 83.53).",
                LateFeeText =
                    "Any late charge must be reasonable and is enforceable only as permitted by applicable law.",
            },
            "TX" => new StateLeaseRules
            {
                StateCode = "TX",
                GoverningLawLabel = "the State of Texas",
                StatuteCitation = "Tex. Prop. Code Chapter 92",
                DepositReturnText =
                    "The deposit will be refunded, together with an itemized written description of any deductions, " +
                    "within thirty (30) days after the Tenant surrenders the Premises and provides a forwarding " +
                    "address (Tex. Prop. Code 92.103-92.104).",
                EntryNoticeText = GenericEntry,
                LateFeeText =
                    "A late fee may be charged only if it is a reasonable estimate of uncertain damages that are " +
                    "difficult to ascertain, as required by Tex. Prop. Code 92.019.",
            },
            "" => new StateLeaseRules
            {
                StateCode = string.Empty,
                GoverningLawLabel = "the state where the Premises are located",
                StatuteCitation = null,
                DepositReturnText = GenericDepositReturn,
                EntryNoticeText = GenericEntry,
                LateFeeText = GenericLateFee,
            },
            _ => new StateLeaseRules
            {
                StateCode = code,
                GoverningLawLabel = $"the State of {code}",
                StatuteCitation = null,
                DepositReturnText = GenericDepositReturn,
                EntryNoticeText = GenericEntry,
                LateFeeText = GenericLateFee,
            },
        };
    }
}
