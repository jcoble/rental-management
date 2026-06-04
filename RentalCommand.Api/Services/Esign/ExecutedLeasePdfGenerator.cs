using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Services.Esign;

/// <summary>One executed signer's details, used to stamp the signature block + certificate.</summary>
public sealed class ExecutedSigner
{
    public string Name { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public SignatureSignatureType SignatureType { get; init; }
    public string? TypedName { get; init; }
    public byte[]? DrawnSignatureImage { get; init; }
    public DateTime? SignedAtUtc { get; init; }
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
    public DateTime? ViewedAtUtc { get; init; }
    public bool ConsentGiven { get; init; }
}

/// <summary>Everything needed to render the final executed lease PDF.</summary>
public sealed class ExecutedLeaseData
{
    public required LeaseAgreementData Agreement { get; init; }
    public required IReadOnlyList<ExecutedSigner> Signers { get; init; }

    /// <summary>Management company / sender name (the landlord on the certificate).</summary>
    public string LandlordName { get; init; } = string.Empty;

    /// <summary>The opaque envelope id (request PublicId), printed on the certificate.</summary>
    public string EnvelopeId { get; init; } = string.Empty;

    public DateTime CompletedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Produces the EXECUTED lease PDF: the standard residential agreement with the signature block replaced
/// by the captured signatures (typed-in-script or the drawn image) + signed dates, followed by a
/// Certificate of Completion page listing every signer (name, email, signed timestamp, IP, user-agent),
/// the document SHA-256 hash, and the ESIGN/UETA statement. v1 produces a clean appended
/// signature+certificate section (no in-line field placement, no PKI/PAdES crypto — hash + audit trail).
/// </summary>
public interface IExecutedLeasePdfGenerator
{
    /// <summary>
    /// Renders the executed document. The SHA-256 over the returned bytes is computed by the caller and
    /// passed back to <see cref="ApplyHash"/>-style display only on the stored row; here we render a
    /// placeholder-free certificate that already embeds the supplied <paramref name="contentSha256"/>.
    /// Because the hash depends on the bytes, callers render once with an empty hash to obtain stable
    /// bytes for hashing, then this method re-renders with the real hash — see the provider for the
    /// two-pass flow. For simplicity v1 renders ONCE with the hash slot computed over the agreement body.
    /// </summary>
    byte[] Generate(ExecutedLeaseData data, string contentSha256);
}

/// <inheritdoc cref="IExecutedLeasePdfGenerator"/>
public sealed class ExecutedLeasePdfGenerator : IExecutedLeasePdfGenerator
{
    private static readonly string Accent = Colors.Blue.Darken2;
    private static readonly string MutedColor = Colors.Grey.Darken1;

    public byte[] Generate(ExecutedLeaseData data, string contentSha256)
    {
        var document = Document.Create(container =>
        {
            // Page 1..n: the full agreement (re-rendered) with executed signatures appended.
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(40);
                page.DefaultTextStyle(t => t.FontSize(10).FontColor(Colors.Black).LineHeight(1.3f));

                page.Header().Element(e => ComposeAgreementHeader(e, data));
                page.Content().Element(e => ComposeAgreementBody(e, data));
                page.Footer().Element(ComposeFooter);
            });

            // Certificate of Completion (its own page).
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(40);
                page.DefaultTextStyle(t => t.FontSize(10).FontColor(Colors.Black).LineHeight(1.3f));

                page.Content().Element(e => ComposeCertificate(e, data, contentSha256));
                page.Footer().Element(ComposeFooter);
            });
        });

        return document.GeneratePdf();
    }

    // ---------------------------------------------------------------------
    // Executed agreement (reuses the same clause copy as the unsigned PDF)
    // ---------------------------------------------------------------------

    private static void ComposeAgreementHeader(IContainer container, ExecutedLeaseData data)
    {
        container.Column(col =>
        {
            col.Item().Text("Residential Lease Agreement").FontSize(20).Bold().FontColor(Accent);
            col.Item().PaddingTop(2).Text(data.Agreement.LandlordName).FontSize(11).SemiBold();
            col.Item().PaddingTop(2).Text("EXECUTED — signed electronically").FontSize(9).SemiBold().FontColor(Colors.Green.Darken2);
            col.Item().PaddingTop(8).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
        });
    }

    private static void ComposeAgreementBody(IContainer container, ExecutedLeaseData data)
    {
        var a = data.Agreement;
        var lease = a.Lease;
        var stateLabel = string.IsNullOrWhiteSpace(a.State) ? "the State" : $"the State of {a.State}";
        var premises = string.IsNullOrWhiteSpace(a.UnitNumber)
            ? a.PropertyAddress
            : $"{a.PropertyAddress}, Unit {a.UnitNumber}";

        container.PaddingTop(10).Column(col =>
        {
            col.Spacing(10);

            col.Item().Text(t =>
            {
                t.Span("This Residential Lease Agreement (\"Agreement\") is made between ");
                t.Span(string.IsNullOrWhiteSpace(a.LandlordName) ? "the Landlord" : a.LandlordName).SemiBold();
                t.Span(" (\"Landlord\") and ");
                t.Span(string.IsNullOrWhiteSpace(a.TenantName) ? "the Tenant" : a.TenantName).SemiBold();
                t.Span(" (\"Tenant\"), for the rental of the premises described below.");
            });

            col.Item().PaddingTop(4).Element(e => ComposeTerms(e, data, premises));

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

            col.Item().PaddingTop(18).Element(e => ComposeExecutedSignatures(e, data));
        });
    }

    private static void ComposeTerms(IContainer container, ExecutedLeaseData data, string premises)
    {
        var a = data.Agreement;
        var lease = a.Lease;
        container.Border(1).BorderColor(Colors.Grey.Lighten1).Padding(10).Column(col =>
        {
            col.Spacing(3);
            TermRow(col, "Landlord", string.IsNullOrWhiteSpace(a.LandlordName) ? "—" : a.LandlordName);
            TermRow(col, "Tenant", string.IsNullOrWhiteSpace(a.TenantName) ? "—" : a.TenantName);
            TermRow(col, "Premises", premises);
            TermRow(col, "Lease Number", string.IsNullOrWhiteSpace(lease.LeaseNumber) ? "—" : lease.LeaseNumber);
            TermRow(col, "Term", $"{Date(lease.StartDate)} to {Date(lease.EndDate)}");
            TermRow(col, "Monthly Rent", $"{Money(lease.MonthlyRent)} (due the {Ordinal(lease.RentDueDay)} of each month)");
            TermRow(col, "Security Deposit", Money(lease.SecurityDeposit));
            TermRow(col, "Late Fee", lease.LateFeeAmount > 0m ? Money(lease.LateFeeAmount) : "—");
        });
    }

    private static void ComposeExecutedSignatures(IContainer container, ExecutedLeaseData data)
    {
        container.Column(col =>
        {
            col.Item().PaddingBottom(8).Text("By signing below, the parties agree to the terms of this Agreement. Each signature was applied electronically.")
                .Italic().FontColor(MutedColor);

            foreach (var signer in data.Signers)
            {
                col.Item().PaddingTop(12).Element(e => ComposeOneSignature(e, signer));
            }
        });
    }

    private static void ComposeOneSignature(IContainer container, ExecutedSigner signer)
    {
        container.Border(1).BorderColor(Colors.Grey.Lighten2).Padding(10).Column(col =>
        {
            col.Item().Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    // The signature mark itself.
                    if (signer.SignatureType == SignatureSignatureType.Drawn && signer.DrawnSignatureImage is { Length: > 0 })
                    {
                        c.Item().Height(48).Image(signer.DrawnSignatureImage).FitHeight();
                    }
                    else
                    {
                        var name = string.IsNullOrWhiteSpace(signer.TypedName) ? signer.Name : signer.TypedName!;
                        // Rendered large + italic to read as a handwritten-style signature. We deliberately
                        // avoid forcing a script font family — those are not guaranteed to be installed on a
                        // Linux server and would silently fall back, so italic is the portable choice.
                        c.Item().Text(name).FontSize(24).Italic().FontColor(Colors.Black);
                    }
                    c.Item().PaddingTop(2).LineHorizontal(1).LineColor(Colors.Grey.Darken1);
                    c.Item().PaddingTop(2).Text(signer.Name).SemiBold();
                    c.Item().Text(signer.Email).FontSize(8).FontColor(MutedColor);
                });
                row.ConstantItem(20);
                row.ConstantItem(150).AlignBottom().Column(c =>
                {
                    c.Item().Text(signer.SignedAtUtc is { } at ? at.ToString("MMM d, yyyy HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture) : "—")
                        .FontSize(9);
                    c.Item().PaddingTop(2).LineHorizontal(1).LineColor(Colors.Grey.Darken1);
                    c.Item().PaddingTop(2).Text("Date signed (UTC)").FontSize(8).FontColor(MutedColor);
                });
            });
        });
    }

    // ---------------------------------------------------------------------
    // Certificate of Completion
    // ---------------------------------------------------------------------

    private static void ComposeCertificate(IContainer container, ExecutedLeaseData data, string contentSha256)
    {
        container.Column(col =>
        {
            col.Spacing(8);

            col.Item().Text("Certificate of Completion").FontSize(18).Bold().FontColor(Accent);
            col.Item().LineHorizontal(1).LineColor(Colors.Grey.Lighten1);

            col.Item().PaddingTop(4).Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    c.Spacing(2);
                    CertRow(c, "Document", data.Agreement.Lease.LeaseNumber is { Length: > 0 } ln
                        ? $"Residential Lease Agreement — {ln}"
                        : "Residential Lease Agreement");
                    CertRow(c, "Sender", string.IsNullOrWhiteSpace(data.LandlordName) ? "—" : data.LandlordName);
                    CertRow(c, "Envelope ID", string.IsNullOrWhiteSpace(data.EnvelopeId) ? "—" : data.EnvelopeId);
                    CertRow(c, "Completed (UTC)", data.CompletedAtUtc.ToString("MMM d, yyyy HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture));
                    CertRow(c, "Document SHA-256", string.IsNullOrWhiteSpace(contentSha256) ? "(computed on storage)" : contentSha256);
                });
            });

            col.Item().PaddingTop(8).Text("Signers").FontSize(13).Bold();

            var index = 1;
            foreach (var signer in data.Signers)
            {
                col.Item().PaddingTop(4).Border(1).BorderColor(Colors.Grey.Lighten2).Padding(8).Column(c =>
                {
                    c.Spacing(2);
                    c.Item().Text($"{index}. {signer.Name}").SemiBold();
                    CertRow(c, "Email", string.IsNullOrWhiteSpace(signer.Email) ? "—" : signer.Email);
                    CertRow(c, "Signature", signer.SignatureType == SignatureSignatureType.Drawn ? "Drawn" : "Typed");
                    CertRow(c, "Consent", signer.ConsentGiven ? "Agreed to electronic records & signature (ESIGN/UETA)" : "—");
                    CertRow(c, "Viewed (UTC)", signer.ViewedAtUtc?.ToString("MMM d, yyyy HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture) ?? "—");
                    CertRow(c, "Signed (UTC)", signer.SignedAtUtc?.ToString("MMM d, yyyy HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture) ?? "—");
                    CertRow(c, "IP address", string.IsNullOrWhiteSpace(signer.IpAddress) ? "—" : signer.IpAddress);
                    CertRow(c, "User-agent", string.IsNullOrWhiteSpace(signer.UserAgent) ? "—" : signer.UserAgent);
                });
                index++;
            }

            col.Item().PaddingTop(10).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
            col.Item().PaddingTop(6).Text(EsignConsentText.CertificateStatement).FontSize(8).FontColor(MutedColor);
        });
    }

    private static void CertRow(ColumnDescriptor col, string label, string value)
    {
        col.Item().Row(row =>
        {
            row.ConstantItem(130).Text(label).SemiBold().FontColor(MutedColor).FontSize(9);
            row.RelativeItem().Text(value).FontSize(9);
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

    private static void ComposeFooter(IContainer container)
    {
        container.Column(col =>
        {
            col.Item().LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
            col.Item().PaddingTop(4).Row(row =>
            {
                row.RelativeItem().Text(
                    "Executed via Rental Command native e-signature. Hash + audit trail per ESIGN/UETA.")
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
