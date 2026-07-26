# Generated deposits SQL and bound parameters

## Statement 1: DB-side count

Duration: `49.359 ms`

Bound parameters:

- `@scope_PortfolioId` = `1`
- `@scope_PortfolioId1` = `1`
- `p0` = `1`
- `p1` = `77ae966b-54bd-4642-b4cf-e9413b155fe0`
- `p2` = `2`
- `p3` = `1`
- `p4` = `1`
- `p5` = `[money.deposits.manage, leasing.deposits.read]`
- `p6` = `Property`

```sql
SELECT count(*)::int
FROM "TenantAccounts" AS t
INNER JOIN (
    SELECT p."Id", p."DeletedAt"
    FROM "Portfolios" AS p
    WHERE p."DeletedAt" IS NULL
) AS p0 ON t."PortfolioId" = p0."Id"
INNER JOIN (
    SELECT l."Id", l."PortfolioId", l."PropertyId"
    FROM "LeaseManagements" AS l
    INNER JOIN (
        SELECT p1."Id", p1."DeletedAt"
        FROM "Portfolios" AS p1
        WHERE p1."DeletedAt" IS NULL
    ) AS p2 ON l."PortfolioId" = p2."Id"
    WHERE p2."DeletedAt" IS NULL
) AS s ON t."PortfolioId" = s."PortfolioId" AND t."LeaseManagementId" = s."Id"
INNER JOIN (
    SELECT l0."Id", l0."PortfolioId"
    FROM "LeaseManagements" AS l0
    INNER JOIN (
        SELECT p4."Id", p4."DeletedAt"
        FROM "Portfolios" AS p4
        WHERE p4."DeletedAt" IS NULL
    ) AS p5 ON l0."PortfolioId" = p5."Id"
    WHERE p5."DeletedAt" IS NULL
) AS s0 ON t."PortfolioId" = s0."PortfolioId" AND t."LeaseManagementId" = s0."Id"
INNER JOIN (
    SELECT s1."PortfolioId", s1."TenantAccountId"
    FROM "SecurityDepositAccounts" AS s1
    INNER JOIN (
        SELECT p6."Id", p6."DeletedAt"
        FROM "Portfolios" AS p6
        WHERE p6."DeletedAt" IS NULL
    ) AS p7 ON s1."PortfolioId" = p7."Id"
    WHERE p7."DeletedAt" IS NULL
) AS s2 ON t."PortfolioId" = s2."PortfolioId" AND t."Id" = s2."TenantAccountId"
WHERE p0."DeletedAt" IS NULL AND t."PortfolioId" = @scope_PortfolioId AND EXISTS (
    SELECT 1
    FROM "Properties" AS p3
    WHERE p3."DeletedAt" IS NULL AND p3."PortfolioId" = @scope_PortfolioId1 AND EXISTS (
        SELECT 1
        FROM (
            SELECT effective_scope."AssignmentId",
                   effective_scope."WorkspaceMembershipId",
                   effective_scope."ScopeKind",
                   effective_scope."PropertyId"
            FROM public.rc_api_effective_capability_scopes(
                @p0,
                @p1,
                @p2,
                @p3,
                @p4,
                @p5,
                @p6) AS effective_scope
        ) AS r
        WHERE r."ScopeKind" = 'AllProperties' OR (r."ScopeKind" = 'SelectedProperties' AND r."PropertyId" = p3."Id")) AND p3."Id" = s."PropertyId")
```

## Statement 2: deterministic DB-side page

Duration: `22.126 ms`

Bound parameters:

- `@scope_PortfolioId` = `1`
- `@scope_PortfolioId1` = `1`
- `p0` = `1`
- `p1` = `77ae966b-54bd-4642-b4cf-e9413b155fe0`
- `p2` = `2`
- `p3` = `1`
- `p4` = `1`
- `p5` = `[money.deposits.manage, leasing.deposits.read]`
- `p6` = `Property`
- `@p40` = `20`
- `@p30` = `0`

```sql
SELECT s2."Id" AS "SecurityDepositAccountId", t."Id" AS "TenantAccountId", s0."Id" AS "LeaseManagementId", s2."OriginatingAgreementId", s0."PropertyId", p9."Name" AS "PropertyName", s0."UnitId", u0."UnitNumber", t."AccountNumber", s0."RelationshipNumber", v."CurrentPrimaryTenantName" AS "PrimaryTenantName", v0."Currency", s2."CreatedAtUtc", v0."EffectiveNowUtc", v0."BusinessDate", v0."TotalReceived", v0."TotalDeductions", v0."TotalRefunded", v0."TotalTransferredIn", v0."TotalTransferredOut", v0."NetAdjustments", v0."HeldBalance", v0."DepositStatus" AS "Status"
FROM "TenantAccounts" AS t
INNER JOIN (
    SELECT p."Id", p."DeletedAt"
    FROM "Portfolios" AS p
    WHERE p."DeletedAt" IS NULL
) AS p0 ON t."PortfolioId" = p0."Id"
INNER JOIN (
    SELECT l."Id", l."PortfolioId", l."PropertyId"
    FROM "LeaseManagements" AS l
    INNER JOIN (
        SELECT p1."Id", p1."DeletedAt"
        FROM "Portfolios" AS p1
        WHERE p1."DeletedAt" IS NULL
    ) AS p2 ON l."PortfolioId" = p2."Id"
    WHERE p2."DeletedAt" IS NULL
) AS s ON t."PortfolioId" = s."PortfolioId" AND t."LeaseManagementId" = s."Id"
INNER JOIN (
    SELECT l0."Id", l0."PortfolioId", l0."PropertyId", l0."RelationshipNumber", l0."UnitId"
    FROM "LeaseManagements" AS l0
    INNER JOIN (
        SELECT p4."Id", p4."DeletedAt"
        FROM "Portfolios" AS p4
        WHERE p4."DeletedAt" IS NULL
    ) AS p5 ON l0."PortfolioId" = p5."Id"
    WHERE p5."DeletedAt" IS NULL
) AS s0 ON t."PortfolioId" = s0."PortfolioId" AND t."LeaseManagementId" = s0."Id"
INNER JOIN vw_lease_management_lifecycle AS v ON s0."PortfolioId" = v."PortfolioId" AND s0."Id" = v."LeaseManagementId"
INNER JOIN (
    SELECT s1."Id", s1."CreatedAtUtc", s1."OriginatingAgreementId", s1."PortfolioId", s1."TenantAccountId"
    FROM "SecurityDepositAccounts" AS s1
    INNER JOIN (
        SELECT p6."Id", p6."DeletedAt"
        FROM "Portfolios" AS p6
        WHERE p6."DeletedAt" IS NULL
    ) AS p7 ON s1."PortfolioId" = p7."Id"
    WHERE p7."DeletedAt" IS NULL
) AS s2 ON t."PortfolioId" = s2."PortfolioId" AND t."Id" = s2."TenantAccountId"
INNER JOIN vw_security_deposit_balances AS v0 ON s2."PortfolioId" = v0."PortfolioId" AND s2."Id" = v0."SecurityDepositAccountId"
INNER JOIN (
    SELECT p8."Id", p8."Name", p8."PortfolioId"
    FROM "Properties" AS p8
    WHERE p8."DeletedAt" IS NULL
) AS p9 ON s0."PropertyId" = p9."Id" AND s0."PortfolioId" = p9."PortfolioId"
INNER JOIN (
    SELECT u."Id", u."PortfolioId", u."PropertyId", u."UnitNumber"
    FROM "Units" AS u
    WHERE u."DeletedAt" IS NULL
) AS u0 ON s0."UnitId" = u0."Id" AND s0."PropertyId" = u0."PropertyId" AND s0."PortfolioId" = u0."PortfolioId"
WHERE p0."DeletedAt" IS NULL AND t."PortfolioId" = @scope_PortfolioId AND EXISTS (
    SELECT 1
    FROM "Properties" AS p3
    WHERE p3."DeletedAt" IS NULL AND p3."PortfolioId" = @scope_PortfolioId1 AND EXISTS (
        SELECT 1
        FROM (
            SELECT effective_scope."AssignmentId",
                   effective_scope."WorkspaceMembershipId",
                   effective_scope."ScopeKind",
                   effective_scope."PropertyId"
            FROM public.rc_api_effective_capability_scopes(
                @p0,
                @p1,
                @p2,
                @p3,
                @p4,
                @p5,
                @p6) AS effective_scope
        ) AS r
        WHERE r."ScopeKind" = 'AllProperties' OR (r."ScopeKind" = 'SelectedProperties' AND r."PropertyId" = p3."Id")) AND p3."Id" = s."PropertyId")
ORDER BY p9."Name", u0."UnitNumber", s2."Id"
LIMIT @p40 OFFSET @p30
```