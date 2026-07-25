using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Auth;

internal sealed record CanonicalDemoLeaseSeedResult(
    IReadOnlyList<LeaseManagement> ActiveManagements,
    int ExpiredManagementCount,
    int LedgerEntryCount,
    int SecurityDepositAccountCount,
    CanonicalDemoLegalDocumentIntent? LegalDocumentIntent);

internal sealed record CanonicalDemoLegalDocumentIntent(
    int PortfolioId,
    int ActorUserId,
    int LeaseManagementId,
    int AgreementId,
    int DraftRevision,
    int DocumentSourceVersionId,
    int TermsSchemaVersion,
    string TermsPayload,
    LeaseAgreementRenderData RenderData,
    string TenantName,
    string TenantEmail,
    DateTime IssuedAtUtc,
    DateTime ExecutedAtUtc);

/// <summary>Builds demo lease facts directly in the canonical lifecycle and money model.</summary>
internal static class CanonicalDemoLeaseSeeder
{
    public static async Task<CanonicalDemoLeaseSeedResult> SeedAsync(
        RentalCommandDbContext db,
        ILegalDocumentSourceVersionResolver sourceVersions,
        IAtomicExecutionState atomicExecution,
        int portfolioId,
        int actorUserId,
        string currency,
        DateTime now,
        IReadOnlyList<Unit> occupiedUnits,
        IReadOnlyList<Unit> vacantUnits,
        IReadOnlyList<Tenant> tenants,
        CancellationToken ct)
    {
        if (!atomicExecution.IsInfrastructureActive)
        {
            throw new AtomicArchitectureException(
                "Canonical demo lease facts must be seeded inside the admitted infrastructure transaction.");
        }

        // Demo agreements use the same supplied renderer available to a real workspace. Do not
        // create an active placeholder template with no immutable PDF and expose it as issuable.
        var sourceVersionId = await sourceVersions.ResolveBuiltInAsync(
            portfolioId,
            BuiltInLeaseAgreementSource.BusinessKey,
            BuiltInLeaseAgreementSource.RendererKey,
            BuiltInLeaseAgreementSource.RendererVersion,
            BuiltInLeaseAgreementSource.SnapshotPayload,
            actorUserId,
            now,
            ct);
        if (sourceVersionId <= 0)
        {
            throw new InvalidOperationException(
                "Rental Command's supplied lease source could not be resolved for demo agreements.");
        }

        var graphs = new List<DemoLeaseGraph>();
        var activeManagements = new List<LeaseManagement>();
        var ledgerEntryCount = 0;

        void AddGraph(Unit unit, Tenant tenant, DateTime start, DateTime end, int sequence, bool isCurrent)
        {
            var rent = unit.MarketRent;
            var deposit = rent;
            var lateFee = Math.Round(rent * 0.05m, 2);
            var createdAt = start.AddDays(-14);
            var effectiveOn = DateOnly.FromDateTime(start);
            var key = $"{(isCurrent ? "ACTIVE" : "PAST")}-{sequence + 1:D3}";
            var management = new LeaseManagement
            {
                PublicId = Guid.NewGuid(),
                PortfolioId = portfolioId,
                PropertyId = unit.PropertyId,
                UnitId = unit.Id,
                RelationshipNumber = $"DEMO-LM-{key}",
                PlannedPossessionAtUtc = start,
                PossessionGivenAtUtc = start,
                PossessionAgreementExceptionReason =
                    "Demo relationship has synthetic terms and no fabricated legal-document bytes.",
                PossessionAgreementExceptionAuthorizedByUserId = actorUserId,
                PlannedMoveOutAtUtc = null,
                PossessionReturnedAtUtc = isCurrent ? null : end,
                // Historical money must be inserted while the account is open. Closed demo
                // relationships are finalized after their complete ledger is persisted below.
                AccountClosedAtUtc = null,
                EndingDisposition = isCurrent
                    ? LeaseManagementEndingDisposition.Undecided
                    : LeaseManagementEndingDisposition.NonRenewalMoveOut,
                EndingDispositionDecidedAtUtc = isCurrent ? null : end.AddDays(-30),
                EndingDispositionDecidedByUserId = isCurrent ? null : actorUserId,
                CreatedAtUtc = createdAt,
                CreatedByUserId = actorUserId,
                UpdatedAtUtc = now,
                RowVersion = Guid.NewGuid(),
            };
            var account = new TenantAccount
            {
                PublicId = Guid.NewGuid(),
                PortfolioId = portfolioId,
                LeaseManagement = management,
                AccountNumber = $"DEMO-TA-{key}",
                Currency = currency,
                OpenedAtUtc = start,
                ClosedAtUtc = null,
                CloseReasonCode = null,
                CloseNote = null,
                CreatedAtUtc = createdAt,
                CreatedByUserId = actorUserId,
            };
            var agreement = new LeaseAgreement
            {
                PublicId = Guid.NewGuid(),
                PortfolioId = portfolioId,
                LeaseManagement = management,
                VersionNumber = 1,
                AgreementNumber = $"DEMO-AGR-{key}-V1",
                ChangeType = LeaseAgreementChangeType.Initial,
                TermType = LeaseAgreementTermType.FixedTerm,
                TermStartOn = effectiveOn,
                TermEndOn = DateOnly.FromDateTime(end),
                GoverningFromOn = effectiveOn,
                BaseRentAmount = rent,
                RentDueDay = 1,
                SecurityDepositObligation = deposit,
                LateFeeAmount = lateFee,
                GracePeriodDays = 5,
                Currency = currency,
                TermsSchemaVersion = 1,
                TermsPayload = JsonSerializer.Serialize(new
                {
                    source = "demo-seed",
                    tenantName = $"{tenant.FirstName} {tenant.LastName}",
                    monthlyRent = rent,
                    securityDeposit = deposit,
                    startDate = start.ToString("yyyy-MM-dd"),
                    endDate = end.ToString("yyyy-MM-dd"),
                }),
                DocumentSourceVersionId = sourceVersionId,
                CreatedAtUtc = createdAt,
                CreatedByUserId = actorUserId,
                UpdatedAtUtc = createdAt,
            };
            var party = new LeaseManagementParty
            {
                PortfolioId = portfolioId,
                LeaseManagement = management,
                TenantId = tenant.Id,
                Role = LeaseManagementPartyRole.PrimaryTenant,
                EffectiveFrom = effectiveOn,
                EffectiveThrough = isCurrent ? null : DateOnly.FromDateTime(end),
                ChangeReason = "Demo household created with its canonical lease relationship.",
                CreatedAtUtc = createdAt,
                CreatedByUserId = actorUserId,
            };
            var signer = new LeaseAgreementSigner
            {
                PortfolioId = portfolioId,
                LeaseAgreement = agreement,
                LeaseManagementParty = party,
                TenantId = tenant.Id,
                SignerRole = LeaseLegalSignerRole.PrimaryTenant,
                NameSnapshot = $"{tenant.FirstName} {tenant.LastName}",
                EmailSnapshot = tenant.Email ?? "not-captured",
                SigningOrder = 1,
                IsRequired = true,
            };
            var depositAccount = new SecurityDepositAccount
            {
                PortfolioId = portfolioId,
                TenantAccount = account,
                OriginatingAgreement = agreement,
                Currency = currency,
                CreatedAtUtc = start,
                CreatedByUserId = actorUserId,
            };
            var depositCharge = Entry(account, agreement, TenantLedgerEntryType.DepositCharge,
                TenantLedgerDirection.Debit, deposit, effectiveOn, effectiveOn, start,
                "Security deposit due", $"demo:{key}:deposit-charge", portfolioId, currency, actorUserId);
            var depositReceipt = Entry(account, null, TenantLedgerEntryType.PaymentReceipt,
                TenantLedgerDirection.Credit, deposit, effectiveOn, null, start,
                "Security deposit received by check", $"demo:{key}:deposit-receipt",
                portfolioId, currency, actorUserId);

            db.LeaseManagements.Add(management);
            db.TenantAccounts.Add(account);
            db.LeaseAgreements.Add(agreement);
            db.LeaseManagementParties.Add(party);
            db.LeaseAgreementSigners.Add(signer);
            db.SecurityDepositAccounts.Add(depositAccount);
            db.TenantLedgerEntries.AddRange(depositCharge, depositReceipt);
            db.TenantLedgerAllocations.Add(Allocation(account, depositCharge, depositReceipt, deposit,
                start, $"demo:{key}:deposit-allocation", portfolioId, actorUserId));
            db.SecurityDepositEntries.Add(new SecurityDepositEntry
            {
                PublicId = Guid.NewGuid(), PortfolioId = portfolioId,
                SecurityDepositAccount = depositAccount,
                EntryType = SecurityDepositEntryType.Receipt,
                Direction = SecurityDepositDirection.Increase,
                Amount = deposit, Currency = currency, EffectiveOn = effectiveOn,
                PostedAtUtc = start, BusinessKey = $"demo:{key}:deposit-fund",
                Description = "Security deposit funded", LeaseAgreement = agreement,
                TenantLedgerEntry = depositReceipt, CreatedByUserId = actorUserId,
            });
            ledgerEntryCount += 2;

            if (!isCurrent)
            {
                db.SecurityDepositEntries.Add(new SecurityDepositEntry
                {
                    PublicId = Guid.NewGuid(), PortfolioId = portfolioId,
                    SecurityDepositAccount = depositAccount,
                    EntryType = SecurityDepositEntryType.Refund,
                    Direction = SecurityDepositDirection.Decrease,
                    Amount = deposit, Currency = currency, EffectiveOn = DateOnly.FromDateTime(end),
                    PostedAtUtc = end, BusinessKey = $"demo:{key}:deposit-refund",
                    Description = "Security deposit returned after move-out",
                    LeaseAgreement = agreement,
                    PayoutExternalReference = $"DEMO-REFUND-{sequence + 1:D3}",
                    CreatedByUserId = actorUserId,
                });
            }

            graphs.Add(new DemoLeaseGraph(management, account, agreement, rent, lateFee, start, end, isCurrent));
            if (isCurrent)
                activeManagements.Add(management);
        }

        for (var i = 0; i < occupiedUnits.Count; i++)
        {
            var startCandidate = now.AddMonths(-(1 + i % 10));
            var start = new DateTime(
                startCandidate.Year, startCandidate.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            AddGraph(occupiedUnits[i], tenants[i], start, start.AddMonths(12), i, true);
        }

        for (var i = 0; i < Math.Min(3, vacantUnits.Count); i++)
        {
            var endCandidate = now.AddMonths(-(3 + i));
            var end = new DateTime(
                endCandidate.Year, endCandidate.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            AddGraph(vacantUnits[i], tenants[19 + i], end.AddMonths(-12), end, i, false);
        }

        for (var i = 0; i < Math.Min(2, activeManagements.Count); i++)
        {
            var graph = graphs[i];
            var coTenant = tenants[17 + i];
            var coParty = new LeaseManagementParty
            {
                PortfolioId = portfolioId, LeaseManagement = graph.Management, TenantId = coTenant.Id,
                Role = LeaseManagementPartyRole.CoTenant,
                EffectiveFrom = DateOnly.FromDateTime(graph.Start),
                ChangeReason = "Demo co-tenant joined the household at move-in.",
                CreatedAtUtc = graph.Start.AddDays(-14), CreatedByUserId = actorUserId,
            };
            db.LeaseManagementParties.Add(coParty);
            db.LeaseAgreementSigners.Add(new LeaseAgreementSigner
            {
                PortfolioId = portfolioId, LeaseAgreement = graph.Agreement,
                LeaseManagementParty = coParty, TenantId = coTenant.Id,
                SignerRole = LeaseLegalSignerRole.CoTenant,
                NameSnapshot = $"{coTenant.FirstName} {coTenant.LastName}",
                EmailSnapshot = coTenant.Email ?? "not-captured", SigningOrder = 2, IsRequired = true,
            });
        }

        var currentMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var graphIndex = 0; graphIndex < graphs.Count; graphIndex++)
        {
            var graph = graphs[graphIndex];
            var month = new DateTime(graph.Start.Year, graph.Start.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var lastMonth = graph.IsCurrent
                ? currentMonth
                : new DateTime(graph.End.Year, graph.End.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1);
            for (var monthIndex = 0; month <= lastMonth && monthIndex < 13; monthIndex++, month = month.AddMonths(1))
            {
                var period = month.ToString("yyyy-MM");
                var effectiveOn = DateOnly.FromDateTime(month);
                var charge = Entry(graph.Account, graph.Agreement, TenantLedgerEntryType.RentCharge,
                    TenantLedgerDirection.Debit, graph.Rent, effectiveOn, effectiveOn, month,
                    $"Rent for {period}", $"demo:{graph.Management.RelationshipNumber}:rent:{period}",
                    portfolioId, currency, actorUserId);
                db.TenantLedgerEntries.Add(charge);
                ledgerEntryCount++;

                var leaveUnpaid = graph.IsCurrent && month == currentMonth && graphIndex % 4 == 0;
                if (leaveUnpaid)
                    continue;

                var paidAt = month.AddDays(graphIndex % 5 == 2 && monthIndex == 3 ? 12 : graphIndex % 3);
                var receipt = Entry(graph.Account, null, TenantLedgerEntryType.PaymentReceipt,
                    TenantLedgerDirection.Credit, graph.Rent, DateOnly.FromDateTime(paidAt), null, paidAt,
                    $"Rent payment for {period}",
                    $"demo:{graph.Management.RelationshipNumber}:rent-payment:{period}",
                    portfolioId, currency, actorUserId);
                db.TenantLedgerEntries.Add(receipt);
                db.TenantLedgerAllocations.Add(Allocation(graph.Account, charge, receipt, graph.Rent, paidAt,
                    $"demo:{graph.Management.RelationshipNumber}:rent-allocation:{period}",
                    portfolioId, actorUserId));
                ledgerEntryCount++;

                if (graphIndex % 5 != 2 || monthIndex != 3)
                    continue;

                var feeDue = month.AddDays(5);
                var feeCharge = Entry(graph.Account, graph.Agreement, TenantLedgerEntryType.LateFeeCharge,
                    TenantLedgerDirection.Debit, graph.LateFee, DateOnly.FromDateTime(feeDue),
                    DateOnly.FromDateTime(feeDue), feeDue, $"Late fee for {period}",
                    $"demo:{graph.Management.RelationshipNumber}:late-fee:{period}",
                    portfolioId, currency, actorUserId);
                var feeReceipt = Entry(graph.Account, null, TenantLedgerEntryType.PaymentReceipt,
                    TenantLedgerDirection.Credit, graph.LateFee, DateOnly.FromDateTime(paidAt), null, paidAt,
                    $"Late-fee payment for {period}",
                    $"demo:{graph.Management.RelationshipNumber}:late-fee-payment:{period}",
                    portfolioId, currency, actorUserId);
                db.TenantLedgerEntries.AddRange(feeCharge, feeReceipt);
                db.TenantLedgerAllocations.Add(Allocation(graph.Account, feeCharge, feeReceipt, graph.LateFee,
                    paidAt, $"demo:{graph.Management.RelationshipNumber}:late-fee-allocation:{period}",
                    portfolioId, actorUserId));
                ledgerEntryCount += 2;
            }
        }

        await db.SaveChangesAsync(ct);

        // Closing an account before its historical ledger is inserted correctly trips the
        // database guard that forbids new money on closed accounts. Seed the complete history,
        // then close the expired relationships in the same infrastructure transaction.
        foreach (var graph in graphs.Where(graph => !graph.IsCurrent))
        {
            graph.Account.ClosedAtUtc = graph.End;
            graph.Account.CloseReasonCode = "POSSESSION_RETURNED";
            graph.Account.CloseNote = "Demo tenant account closed after move-out.";
            graph.Management.AccountClosedAtUtc = graph.End;
        }

        await db.SaveChangesAsync(ct);
        return new CanonicalDemoLeaseSeedResult(
            activeManagements,
            graphs.Count - activeManagements.Count,
            ledgerEntryCount,
            graphs.Count,
            await BuildLegalDocumentIntentAsync(db, portfolioId, actorUserId, now, ct));
    }

    public static async Task<CanonicalDemoLeaseSeedResult> ReconcileAsync(
        RentalCommandDbContext db,
        IAtomicExecutionState atomicExecution,
        int portfolioId,
        int actorUserId,
        DateTime now,
        CancellationToken ct)
    {
        if (!atomicExecution.IsInfrastructureActive)
        {
            throw new AtomicArchitectureException(
                "Canonical demo lease reconciliation must run inside the admitted infrastructure transaction.");
        }

        var rowVersion = Guid.NewGuid();
        var openRelationshipIds = db.TenantAccounts
            .Where(account => account.PortfolioId == portfolioId && account.ClosedAtUtc == null)
            .Select(account => account.LeaseManagementId);
        await db.LeaseManagements
            .Where(relationship => relationship.PortfolioId == portfolioId
                && relationship.RelationshipNumber.StartsWith("DEMO-LM-")
                && relationship.PossessionGivenAtUtc != null
                && relationship.PossessionReturnedAtUtc == null
                && openRelationshipIds.Contains(relationship.Id)
                && relationship.EndingDisposition == LeaseManagementEndingDisposition.Undecided)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(relationship => relationship.PlannedMoveOutAtUtc, (DateTime?)null)
                .SetProperty(relationship => relationship.EndingDispositionDecidedAtUtc, (DateTime?)null)
                .SetProperty(relationship => relationship.EndingDispositionDecidedByUserId, (int?)null)
                .SetProperty(relationship => relationship.UpdatedAtUtc, now)
                .SetProperty(relationship => relationship.RowVersion, rowVersion)
                .SetProperty(
                    relationship => relationship.PossessionAgreementExceptionReason,
                    relationship => relationship.RelationshipNumber == "DEMO-LM-ACTIVE-017"
                        ? "Demo imported possession is intentionally retained without a governing Agreement."
                        : relationship.PossessionAgreementExceptionReason)
                .SetProperty(
                    relationship => relationship.PossessionAgreementExceptionAuthorizedByUserId,
                    relationship => relationship.RelationshipNumber == "DEMO-LM-ACTIVE-017"
                        ? actorUserId
                        : relationship.PossessionAgreementExceptionAuthorizedByUserId),
                ct);
        return new CanonicalDemoLeaseSeedResult(
            [],
            0,
            0,
            0,
            await BuildLegalDocumentIntentAsync(db, portfolioId, actorUserId, now, ct));
    }

    private static async Task<CanonicalDemoLegalDocumentIntent?> BuildLegalDocumentIntentAsync(
        RentalCommandDbContext db,
        int portfolioId,
        int actorUserId,
        DateTime now,
        CancellationToken ct)
    {
        var row = await (
            from candidateAgreement in db.LeaseAgreements.AsNoTracking()
            where candidateAgreement.PortfolioId == portfolioId
                && candidateAgreement.AgreementNumber == "DEMO-AGR-ACTIVE-001-V1"
            join relationship in db.LeaseManagements.AsNoTracking()
                on new { candidateAgreement.PortfolioId, Id = candidateAgreement.LeaseManagementId }
                equals new { relationship.PortfolioId, relationship.Id }
            join property in db.Properties.AsNoTracking()
                on new { relationship.PortfolioId, Id = relationship.PropertyId }
                equals new { property.PortfolioId, property.Id }
            join unit in db.Units.AsNoTracking()
                on new { relationship.PortfolioId, Id = relationship.UnitId }
                equals new { unit.PortfolioId, unit.Id }
            join signer in db.LeaseAgreementSigners.AsNoTracking()
                    .Where(candidate => candidate.SignerRole == LeaseLegalSignerRole.PrimaryTenant)
                on new { candidateAgreement.PortfolioId, AgreementId = candidateAgreement.Id }
                equals new { signer.PortfolioId, AgreementId = signer.LeaseAgreementId }
            join portfolio in db.Portfolios.AsNoTracking()
                on candidateAgreement.PortfolioId equals portfolio.Id
            select new
            {
                Agreement = candidateAgreement,
                relationship.PropertyId,
                property.Name,
                property.AddressLine1,
                property.AddressLine2,
                property.City,
                property.State,
                property.PostalCode,
                property.YearBuilt,
                unit.UnitNumber,
                signer.NameSnapshot,
                signer.EmailSnapshot,
                portfolio.ManagementCompanyName,
                PortfolioName = portfolio.Name,
            }).SingleOrDefaultAsync(ct);

        if (row is null)
        {
            return null;
        }

        var agreement = row.Agreement;
        var propertyAddress = string.Join(", ", new[]
        {
            row.AddressLine1,
            row.AddressLine2,
            $"{row.City}, {row.State} {row.PostalCode}".Trim(),
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var issuedAt = agreement.CreatedAtUtc.AddMinutes(1);
        var executedAt = issuedAt.AddMinutes(5);
        return new CanonicalDemoLegalDocumentIntent(
            portfolioId,
            actorUserId,
            agreement.LeaseManagementId,
            agreement.Id,
            agreement.DraftRevision,
            agreement.DocumentSourceVersionId,
            agreement.TermsSchemaVersion,
            agreement.TermsPayload,
            new LeaseAgreementRenderData
            {
                PropertyId = row.PropertyId,
                AgreementNumber = agreement.AgreementNumber,
                TermStartOn = agreement.TermStartOn,
                TermEndOn = agreement.TermEndOn,
                BaseRentAmount = agreement.BaseRentAmount,
                SecurityDepositObligation = agreement.SecurityDepositObligation,
                LateFeeAmount = agreement.LateFeeAmount,
                RentDueDay = agreement.RentDueDay,
                LandlordName = string.IsNullOrWhiteSpace(row.ManagementCompanyName)
                    ? row.PortfolioName
                    : row.ManagementCompanyName,
                TenantName = row.NameSnapshot,
                TenantEmail = row.EmailSnapshot,
                PropertyName = row.Name,
                PropertyAddress = propertyAddress,
                UnitNumber = row.UnitNumber,
                State = row.State,
                YearBuilt = row.YearBuilt,
            },
            row.NameSnapshot,
            row.EmailSnapshot,
            issuedAt,
            executedAt);
    }

    private static TenantLedgerEntry Entry(
        TenantAccount account, LeaseAgreement? agreement, TenantLedgerEntryType type,
        TenantLedgerDirection direction, decimal amount, DateOnly effectiveOn, DateOnly? dueOn,
        DateTime postedAt, string description, string businessKey, int portfolioId,
        string currency, int actorUserId) => new()
        {
            PublicId = Guid.NewGuid(), PortfolioId = portfolioId, TenantAccount = account,
            EntryType = type, Direction = direction, Amount = amount, Currency = currency,
            EffectiveOn = effectiveOn, DueOn = dueOn, PostedAtUtc = postedAt,
            Description = description, BusinessKey = businessKey, LeaseAgreement = agreement,
            CreatedByUserId = actorUserId,
        };

    private static TenantLedgerAllocation Allocation(
        TenantAccount account, TenantLedgerEntry debit, TenantLedgerEntry credit, decimal amount,
        DateTime allocatedAt, string businessKey, int portfolioId, int actorUserId) => new()
        {
            PortfolioId = portfolioId, TenantAccount = account, DebitEntry = debit,
            CreditEntry = credit, Amount = amount, AllocatedAtUtc = allocatedAt,
            BusinessKey = businessKey, CreatedByUserId = actorUserId,
        };

    private sealed record DemoLeaseGraph(
        LeaseManagement Management,
        TenantAccount Account,
        LeaseAgreement Agreement,
        decimal Rent,
        decimal LateFee,
        DateTime Start,
        DateTime End,
        bool IsCurrent);
}
