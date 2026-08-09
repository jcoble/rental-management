using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Services.Auditing;

/// <summary>
/// Turns a raw <see cref="AtomicAuditLog"/> row into one plain-English sentence for a non-technical
/// landlord, e.g. "Recorded a payment", "Updated lease", "Deleted expense". Deterministic — no LLM.
/// Soft-deletes already arrive as <see cref="AuditLogOperation.Deleted"/> from the interceptor, so a
/// delete reads as "Deleted {entity}". Falls back to "{Operation} {EntityType} #{Id}" for anything
/// unmapped, so a new audited entity is never blank.
/// </summary>
public sealed class AuditDescriber
{
    public string Describe(AtomicAuditLog row)
    {
        var noun = EntityNoun(row.EntityType);

        return row.Operation switch
        {
            AuditLogOperation.Created => Created(row.EntityType, noun),
            AuditLogOperation.Updated => $"Updated {noun}",
            AuditLogOperation.Deleted => $"Deleted {noun}",
            AuditLogOperation.Approved => $"Approved {noun}",
            AuditLogOperation.Rejected => $"Rejected {noun}",
            _ => $"{row.Operation} {row.EntityType} #{row.EntityId}",
        };
    }

    // "Created" reads better as a domain verb for several types ("Recorded a payment").
    private static string Created(string entityType, string noun) => entityType switch
    {
        "TenantLedgerEntry" => "Posted a tenant account entry",
        "Expense" => "Recorded an expense",
        "WorkOrder" => "Created a work order",
        "Appointment" => "Scheduled an appointment",
        "Inspection" => "Scheduled an inspection",
        "RentalApplication" => "Received a rental application",
        "SecurityDeposit" => "Held a security deposit",
        "SecurityDepositAccount" => "Opened a security deposit account",
        "SecurityDepositEntry" => "Recorded a security-deposit movement",
        "TenantPaymentAttempt" => "Recorded a payment attempt",
        "TenantAutopayEnrollment" => "Updated autopay enrollment",
        "StoredFile" => "Added a document",
        "LegalDocumentArtifact" => "Recorded a legal document",
        "LeaseAddendum" => "Added a lease addendum",
        "SignatureRequest" => "Created a signature request",
        "NoticeDraft" => "Drafted a tenant notice",
        "RenderedNotice" => "Rendered a tenant notice",
        "NoticeDeliveryEvidence" => "Recorded notice delivery",
        "TenantNoticeWorkItem" => "Queued a tenant notice",
        "Conversation" => "Opened a conversation",
        "ConversationMessage" => "Added a conversation message",
        _ => $"Added {noun}",
    };

    private static string EntityNoun(string entityType) => entityType switch
    {
        "TenantAccount" => "tenant account",
        "TenantLedgerEntry" => "tenant account entry",
        "SecurityDepositAccount" => "security deposit account",
        "SecurityDepositEntry" => "security-deposit movement",
        "TenantPaymentAttempt" => "payment attempt",
        "TenantAccountConditionPeriod" => "tenant account condition",
        "TenantAutopayEnrollment" => "autopay enrollment",
        "LeaseManagement" => "tenant and lease relationship",
        "LeaseAgreement" => "lease agreement",
        "LeaseAgreementSigner" => "agreement signer",
        "LeaseAddendum" => "lease addendum",
        "LeaseAddendumSigner" => "addendum signer",
        "LeaseAddendumFinancialEffect" => "addendum financial effect",
        "LeaseRenewalAddendumDecision" => "addendum renewal decision",
        "StoredFile" => "document",
        "LegalDocumentArtifact" => "legal document",
        "SignatureRequest" => "signature request",
        "SignatureSigner" => "signature signer",
        "NoticeDraft" => "tenant notice draft",
        "RenderedNotice" => "rendered tenant notice",
        "NoticeDeliveryEvidence" => "notice delivery evidence",
        "TenantNoticeWorkItem" => "tenant notice work item",
        "Conversation" => "conversation",
        "ConversationMessage" => "conversation message",
        "Notification" => "notification",
        "LeaseManagementParty" => "tenant relationship party",
        "TenantUserAccess" => "tenant portal access",
        "UnitOperationalPeriod" => "unit operational period",
        "EvictionCase" => "eviction case",
        "EvictionCaseEvent" => "eviction case event",
        "WorkOrderStatusEvent" => "work-order activity",
        "Expense" => "expense",
        "Tenant" => "tenant",
        "Property" => "property",
        "Unit" => "unit",
        "WorkOrder" => "work order",
        "Vendor" => "vendor",
        "OwnerEntity" => "owner",
        "Appointment" => "appointment",
        "Inspection" => "inspection",
        "RentalApplication" => "rental application",
        "SecurityDeposit" => "security deposit",
        _ => entityType,
    };
}
