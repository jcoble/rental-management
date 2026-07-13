using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Notifications;

/// <summary>
/// Migration-owned, immutable Rental Command notice content. IDs and publication timestamps are
/// deliberately fixed so a fresh database always receives the same platform baseline.
/// </summary>
public static class SuppliedNoticeTemplateBaseline
{
    public const int Version = 1;
    public const string Provenance = "Rental Command supplied default v1";

    public static readonly DateTime PublishedAtUtc =
        new(2026, 7, 13, 0, 0, 0, DateTimeKind.Utc);

    public static IReadOnlyList<SuppliedNoticeTemplateDefinition> V1 { get; } =
        Array.AsReadOnly<SuppliedNoticeTemplateDefinition>(
        [
            new(
                1,
                "rent-reminder",
                NoticeClassification.Courtesy,
                "Upcoming rent reminder",
                "Hello {{tenant_name}}, this is a reminder that {{rent_amount}} is due on {{rent_due_date}} for {{property_address}}."),
            new(
                2,
                "lease-renewal-offer",
                NoticeClassification.Operational,
                "Lease renewal offer",
                "Hello {{tenant_name}}, we would like to offer a renewal for {{property_address}} beginning {{renewal_start_date}}. Please review the attached terms."),
            new(
                3,
                "month-to-month-offer",
                NoticeClassification.Operational,
                "Month-to-month offer",
                "Hello {{tenant_name}}, your current agreement ends {{lease_end_date}}. We are offering a month-to-month arrangement beginning the following day."),
            new(
                4,
                "lease-non-renewal",
                NoticeClassification.Legal,
                "Lease expiration and non-renewal notice",
                "Hello {{tenant_name}}, this notice concerns the agreement for {{property_address}}, which ends {{lease_end_date}}. Review the attached notice and contact management with questions."),
            new(
                5,
                "late-rent-late-fee",
                NoticeClassification.Legal,
                "Past-due rent notice",
                "Hello {{tenant_name}}, our records show {{overdue_amount}} remains due for {{property_address}} as of {{today}}. This notice includes any applicable late fee described in your agreement."),
        ]);

    /// <summary>
    /// Creates the workspace's immutable v1 copies in one conflict-safe PostgreSQL command. The
    /// caller supplies the timestamp so registration and any later explicit seed request share the
    /// caller's command time and transaction.
    /// </summary>
    public static FormattableString BuildWorkspaceV1CopyCommand(
        int portfolioId,
        int actorUserId,
        DateTime createdAtUtc) => $"""
        INSERT INTO "WorkspaceNoticeTemplateVersions"
        (
            "PortfolioId",
            "SystemKey",
            "Version",
            "BasedOnSystemTemplateVersionId",
            "IsCustomized",
            "Subject",
            "Body",
            "JurisdictionCode",
            "JurisdictionReviewedAtUtc",
            "JurisdictionReviewedByUserId",
            "CreatedByUserId",
            "CreatedAtUtc"
        )
        SELECT
            {portfolioId},
            system."SystemKey",
            system."Version",
            system."Id",
            FALSE,
            system."Subject",
            system."Body",
            system."JurisdictionCode",
            NULL,
            NULL,
            {actorUserId},
            {createdAtUtc}
        FROM "SystemNoticeTemplateVersions" AS system
        WHERE system."Version" = 1
        ON CONFLICT ("PortfolioId", "SystemKey", "Version") DO NOTHING;

        INSERT INTO "TenantNoticePolicies"
        (
            "PortfolioId",
            "AutomationKey",
            "Mode",
            "Classification",
            "LeadDays",
            "SendHourLocal",
            "SendTenantPortal",
            "SendMobilePush",
            "SendEmail",
            "SendSms",
            "IncludePrimaryTenant",
            "IncludeCoTenant",
            "IncludeEligibleGuarantor",
            "IncludeOccupant",
            "FailureBehavior",
            "WorkspaceNoticeTemplateVersionId",
            "ReviewedJurisdictionCode",
            "JurisdictionReviewedAtUtc",
            "JurisdictionReviewedByUserId",
            "CreatedAtUtc",
            "UpdatedAtUtc"
        )
        SELECT
            workspace."PortfolioId",
            workspace."SystemKey",
            'Draft',
            system."Classification",
            CASE
                WHEN workspace."SystemKey" IN ('lease-renewal-offer', 'month-to-month-offer', 'lease-non-renewal') THEN 60
                ELSE 5
            END,
            9,
            TRUE,
            FALSE,
            TRUE,
            FALSE,
            TRUE,
            TRUE,
            FALSE,
            FALSE,
            'StopAndRequireReview',
            workspace."Id",
            NULL,
            NULL,
            NULL,
            {createdAtUtc},
            {createdAtUtc}
        FROM "WorkspaceNoticeTemplateVersions" AS workspace
        JOIN "SystemNoticeTemplateVersions" AS system
          ON system."Id" = workspace."BasedOnSystemTemplateVersionId"
        WHERE workspace."PortfolioId" = {portfolioId}
          AND NOT EXISTS
          (
              SELECT 1
              FROM "WorkspaceNoticeTemplateVersions" AS newer
              WHERE newer."PortfolioId" = workspace."PortfolioId"
                AND newer."SystemKey" = workspace."SystemKey"
                AND newer."Version" > workspace."Version"
          )
        ON CONFLICT ("PortfolioId", "AutomationKey") DO NOTHING;
        """;
}

public sealed record SuppliedNoticeTemplateDefinition(
    int Id,
    string SystemKey,
    NoticeClassification Classification,
    string Subject,
    string Body);
