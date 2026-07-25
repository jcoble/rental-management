using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Scanning;

/// <summary>
/// Small first-pass schema used only when intake did not already name a destination. The result is
/// a proposal: the worker selects the typed extraction schema, while confirmation and authorization
/// still happen through the normal reviewed command.
/// </summary>
public static class DocumentClassificationExtractionSchema
{
    public const string Instructions =
        "Classify this rental-management document before extracting it. Choose exactly one target from " +
        "Expense, Payment, WorkOrder, LeaseAgreement, Application, or Loan. " +
        "Use Payment for tenant rent checks or payment confirmations; Expense for receipts, bills, invoices, " +
        "tax bills, and utility bills; WorkOrder for maintenance requests, estimates, and contractor job notes; " +
        "LeaseAgreement for leases, renewals, and lease addenda; Application for rental applications and applicant " +
        "identity or income packets; Loan for mortgage statements and closing disclosures. Do not invent a target " +
        "from the user identity, workspace role, or grounding data. If uncertain, select the " +
        "closest target and explain the uncertainty. " +
        "This is a routing proposal only and must not authorize or persist a business record.";

    public static IReadOnlyList<ExtractionFieldSpec> Fields { get; } =
    [
        new("target_entity_type", "enum", "Proposed typed destination.", Required: true,
            EnumValues: ["Expense", "Payment", "WorkOrder", "LeaseAgreement", "Application", "Loan"]),
        new("document_kind", "string", "Short human-readable document kind."),
        new("classification_reason", "string", "Brief evidence from the document supporting the route."),
    ];
}
