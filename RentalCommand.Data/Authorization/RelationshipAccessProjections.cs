namespace RentalCommand.Data.Authorization;

/// <summary>One effective owner relationship/property row; every restricted owner query starts here.</summary>
public sealed class EffectiveOwnerAccessProjection
{
    public int AccessContextId { get; set; }
    public int UserId { get; set; }
    public int PortfolioId { get; set; }
    public long AccessRevision { get; set; }
    public int OwnerUserAccessId { get; set; }
    public int OwnerEntityId { get; set; }
    public int? PropertyId { get; set; }
}

/// <summary>One effective tenant relationship/account row; portal reads never infer this from a user FK.</summary>
public sealed class EffectiveTenantAccessProjection
{
    public int AccessContextId { get; set; }
    public int UserId { get; set; }
    public int PortfolioId { get; set; }
    public long AccessRevision { get; set; }
    public int TenantUserAccessId { get; set; }
    public int LeaseManagementPartyId { get; set; }
    public int TenantId { get; set; }
    public int LeaseManagementId { get; set; }
    public int? TenantAccountId { get; set; }
    public int PropertyId { get; set; }
    public int UnitId { get; set; }
}

internal static class RelationshipAccessProjectionSql
{
    public const string Drop = """
        DROP VIEW IF EXISTS "vw_effective_tenant_access";
        DROP VIEW IF EXISTS "vw_effective_owner_access";
        """;

    public const string Create = """
        CREATE VIEW "vw_effective_owner_access" WITH (security_invoker = true) AS
        SELECT c."Id" AS "AccessContextId", c."UserId", c."PortfolioId", c."AccessRevision",
               access."Id" AS "OwnerUserAccessId", access."OwnerEntityId", property."Id" AS "PropertyId"
        FROM "WorkspaceAccessContexts" c
        JOIN "OwnerUserAccesses" access
          ON access."AccessContextId" = c."Id" AND access."ApplicationUserId" = c."UserId"
         AND access."PortfolioId" = c."PortfolioId"
        LEFT JOIN "Properties" property
          ON property."PortfolioId" = access."PortfolioId"
         AND property."OwnerEntityId" = access."OwnerEntityId" AND property."DeletedAt" IS NULL
        JOIN "Portfolios" portfolio ON portfolio."Id" = c."PortfolioId" AND portfolio."DeletedAt" IS NULL
        WHERE c."Status" = 'Active' AND c."SuspendedAtUtc" IS NULL AND c."RevokedAtUtc" IS NULL
          AND access."RevokedAtUtc" IS NULL AND access."EffectiveFromUtc" <= CURRENT_TIMESTAMP
          AND (access."EffectiveToUtc" IS NULL OR access."EffectiveToUtc" > CURRENT_TIMESTAMP);

        CREATE VIEW "vw_effective_tenant_access" WITH (security_invoker = true) AS
        SELECT c."Id" AS "AccessContextId", c."UserId", c."PortfolioId", c."AccessRevision",
               access."Id" AS "TenantUserAccessId", party."Id" AS "LeaseManagementPartyId",
               party."TenantId", party."LeaseManagementId", account."Id" AS "TenantAccountId",
               relationship."PropertyId", relationship."UnitId"
        FROM "WorkspaceAccessContexts" c
        JOIN "TenantUserAccesses" access
          ON access."AccessContextId" = c."Id" AND access."ApplicationUserId" = c."UserId"
         AND access."PortfolioId" = c."PortfolioId"
        JOIN "LeaseManagementParties" party
          ON party."Id" = access."LeaseManagementPartyId" AND party."PortfolioId" = access."PortfolioId"
        JOIN "LeaseManagements" relationship
          ON relationship."Id" = party."LeaseManagementId" AND relationship."PortfolioId" = party."PortfolioId"
        LEFT JOIN "TenantAccounts" account
          ON account."LeaseManagementId" = relationship."Id" AND account."PortfolioId" = relationship."PortfolioId"
        JOIN "Portfolios" portfolio ON portfolio."Id" = c."PortfolioId" AND portfolio."DeletedAt" IS NULL
        WHERE c."Status" = 'Active' AND c."SuspendedAtUtc" IS NULL AND c."RevokedAtUtc" IS NULL
          AND access."RevokedAtUtc" IS NULL
          AND party."EffectiveFrom" <= rc_business_date(c."PortfolioId")
          AND (party."EffectiveThrough" IS NULL
               OR party."EffectiveThrough" >= rc_business_date(c."PortfolioId"));
        """;
}
