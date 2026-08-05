using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Data;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Services.Esign;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Sandbox;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Documents;
using RentalCommand.Data.Sandbox;

namespace RentalCommand.Api.Services.Auth;

/// <summary>
/// Idempotent demo-data seeder. Creates a rich interlinked dataset (properties, units, tenants,
/// canonical lease relationships, agreement drafts, tenant/deposit ledgers, expenses, work orders,
/// appointments, and inspections) for a given portfolio so every report and analytics screen has realistic data.
///
/// Two callers: startup (seeds portfolio 1 = the dev admin and reconciles every other demo-marked
/// portfolio when <c>Seed:DemoData=true</c>), and new signups (each new portfolio is seeded as a
/// Sandbox to explore). Every row is parameterized on the passed portfolio id.
/// </summary>
public class DemoDataSeeder
{
    private readonly RentalCommandDbContext _db;
    private readonly ILogger<DemoDataSeeder> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IAtomicUnitOfWork _atomic;
    private readonly IAtomicCommandContext _atomicContext;
    private readonly ILeaseAgreementRenderer _agreementRenderer;
    private readonly ILeaseAgreementPdfGenerator _agreementPdf;
    private readonly IExecutedLeasePdfGenerator _executedLeasePdf;
    private readonly IPendingFileUploadStore _pendingUploads;
    private readonly IFileStorage _fileStorage;

    public DemoDataSeeder(
        RentalCommandDbContext db,
        ILogger<DemoDataSeeder> logger,
        TimeProvider timeProvider,
        IAtomicUnitOfWork atomic,
        IAtomicCommandContext atomicContext,
        ILeaseAgreementRenderer agreementRenderer,
        ILeaseAgreementPdfGenerator agreementPdf,
        IExecutedLeasePdfGenerator executedLeasePdf,
        IPendingFileUploadStore pendingUploads,
        IFileStorage fileStorage)
    {
        _db = db;
        _logger = logger;
        _timeProvider = timeProvider;
        _atomic = atomic;
        _atomicContext = atomicContext;
        _agreementRenderer = agreementRenderer;
        _agreementPdf = agreementPdf;
        _executedLeasePdf = executedLeasePdf;
        _pendingUploads = pendingUploads;
        _fileStorage = fileStorage;
    }

    /// <summary>
    /// Seeds the dev-admin portfolio (id 1), then reconciles every other portfolio that already
    /// contains canonical demo lease-management facts.
    /// </summary>
    public async Task SeedAsync(CancellationToken ct = default)
    {
        var failures = new List<Exception>();
        var successfulPortfolioCount = 0;

        async Task SeedOneAsync(int portfolioId)
        {
            // Each portfolio reconciliation is an independent startup action with its own receipt.
            var operationKey = $"startup:portfolio:{portfolioId}:{Guid.NewGuid():N}";
            try
            {
                await SeedStartupPortfolioAsync(portfolioId, operationKey, ct);
                successfulPortfolioCount++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures.Add(exception);
                _logger.LogError(
                    exception,
                    "Startup demo reconciliation failed for portfolio {PortfolioId}.",
                    portfolioId);
            }
        }

        await SeedOneAsync(1);

        var otherDemoPortfolioIds = await _db.LeaseManagements
            .AsNoTracking()
            .Where(relationship => relationship.PortfolioId != 1
                && EF.Functions.Like(relationship.RelationshipNumber, "DEMO-LM-%"))
            .Select(relationship => relationship.PortfolioId)
            .Distinct()
            .OrderBy(portfolioId => portfolioId)
            .ToListAsync(ct);

        foreach (var portfolioId in otherDemoPortfolioIds)
        {
            await SeedOneAsync(portfolioId);
        }

        if (successfulPortfolioCount > 0 || failures.Count == 0)
        {
            return;
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        throw new AggregateException(
            "Startup demo reconciliation failed for every eligible portfolio.",
            failures);
    }

    private async Task SeedStartupPortfolioAsync(
        int portfolioId,
        string operationKey,
        CancellationToken ct)
    {
        using var startupScope = DemoSeedStartupScope.Begin($"portfolio:{portfolioId}:{operationKey}");
        await SeedPortfolioAsync(portfolioId, operationKey, ct);
    }

    /// <summary>
    /// Seeds the full demo dataset for an arbitrary portfolio, or reconciles the stable lifecycle and
    /// legal-document fixtures when data already exists. Database graph creation and later artifact
    /// finalization are independently atomic; rendering/admission/storage run between them and retries
    /// reuse deterministic identities.
    /// </summary>
    public async Task SeedPortfolioAsync(
        int portfolioId,
        string operationKey,
        CancellationToken ct = default,
        bool requirePendingSandboxOnboarding = false)
    {
        var businessNowUtc = _timeProvider.UtcNow();
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        operationKey = operationKey.Trim();
        var command = new SeedDemoPortfolioCommand(
            portfolioId,
            requirePendingSandboxOnboarding,
            businessNowUtc,
            operationKey);
        await _atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "sandbox.demo-seed",
                $"portfolio:{portfolioId}:{operationKey}"),
            command,
            DemoSeedCommandHandler.ResultCodec,
            ct);
        await CompleteLegalArtifactsAsync(
            portfolioId,
            ct);
    }

    public async Task CompleteLegalArtifactsAsync(
        int portfolioId,
        CancellationToken ct = default)
    {
        var actorUserId = await ResolveActorUserIdAsync(portfolioId, ct);
        var legalIntents = await CanonicalDemoLeaseSeeder.BuildLegalDocumentIntentsAsync(
            _db, portfolioId, actorUserId, ct);
        _db.ChangeTracker.Clear();

        foreach (var legalIntent in legalIntents)
        {
            var prepared = await PrepareLegalDocumentAsync(legalIntent, ct);
            var finalizeCommand = ToFinalizeCommand(prepared);
            await _atomic.ExecuteAsync(
                new AtomicCommandIdentity(
                    "sandbox.demo-legal-finalize",
                    $"portfolio:{portfolioId}:agreement:{finalizeCommand.AgreementId}:v1"),
                finalizeCommand,
                DemoLegalDocumentFinalizeCommandHandler.ResultCodec,
                ct);
            _db.ChangeTracker.Clear();
        }
    }

    internal static async Task<SeedDemoPortfolioResult> SeedPortfolioCoreAsync(
        RentalCommandDbContext db,
        SeedDemoPortfolioCommand command,
        IAtomicCommandContext attempt,
        CancellationToken ct)
    {
        var portfolioId = command.PortfolioId;
        // Serialize the idempotency check and complete seed beneath the portfolio row. The
        // receipt-backed atomic attempt owns the enclosing transaction, including every flush.
        var lockedPortfolioId = 0;
        if (db.Database.IsNpgsql())
        {
            var lockedPortfolioRows = await db.QuerySqlAsync<int>($$"""
                    SELECT portfolio."Id" AS "Value"
                    FROM "Portfolios" AS portfolio
                    WHERE portfolio."Id" = {{portfolioId}}
                    FOR UPDATE
                    """,
                ct);
            lockedPortfolioId = lockedPortfolioRows.SingleOrDefault();
        }
        else
        {
            lockedPortfolioId = await db.Portfolios
                .Where(portfolio => portfolio.Id == portfolioId)
                .Select(portfolio => portfolio.Id)
                .SingleOrDefaultAsync(ct);
        }
        if (lockedPortfolioId == 0)
        {
            throw new InvalidOperationException($"Portfolio {portfolioId} does not exist.");
        }

        var now = command.BusinessNowUtc;

        var seedContext = await db.Portfolios
            .Where(portfolio => portfolio.Id == portfolioId)
            .Select(portfolio => new
            {
                Currency = portfolio.Currency,
                ActorUserId = db.Users
                    .Where(user => user.WorkspaceAccessContexts.Any(context =>
                        context.PortfolioId == portfolioId && context.Membership != null))
                    .OrderBy(user => user.Id)
                    .Select(user => (int?)user.Id)
                    .FirstOrDefault(),
            })
            .SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Portfolio {portfolioId} does not exist.");
        var actorUserId = seedContext.ActorUserId
            ?? throw new InvalidOperationException($"Portfolio {portfolioId} has no administering user for demo facts.");
        var currency = seedContext.Currency.Trim().ToUpperInvariant();

        // Existing demo portfolios still run the stable lifecycle/document reconciliation. This is
        // deliberately not a blanket reseed: only DEMO-LM facts are repaired and the stable legal
        // artifact intent is resumed.
        if (await db.Properties.AnyAsync(p => p.PortfolioId == portfolioId, ct))
        {
            var isExistingDemoPortfolio = await db.LeaseManagements.AnyAsync(relationship =>
                relationship.PortfolioId == portfolioId
                && relationship.RelationshipNumber.StartsWith("DEMO-LM-"), ct);
            if (!isExistingDemoPortfolio)
            {
                throw new InvalidOperationException(
                    $"Demo seeding refused populated non-demo portfolio {portfolioId}.");
            }

            var reconciled = await CanonicalDemoLeaseSeeder.ReconcileAsync(
                db, attempt, portfolioId, actorUserId, now, ct);
            await attempt.FlushBusinessAsync(ct);
            return Result(portfolioId, true, reconciled, legalAgreementId:
                reconciled.LegalDocumentIntents.FirstOrDefault()?.AgreementId);
        }

        // ── 1. OwnerEntities ──────────────────────────────────────────────────────────
        // Account bootstrap already creates the landlord's editable primary owner record. Reuse it
        // for the sample LLC instead of adding a third, unowned "Rental Command Admin" row that has
        // no relationship to any demo property.
        var primaryOwner = await db.OwnerEntities
            .SingleOrDefaultAsync(owner => owner.PortfolioId == portfolioId && owner.IsPrimary, ct);
        primaryOwner ??= new OwnerEntity
        {
            PortfolioId = portfolioId,
            IsPrimary = true,
            CreatedAt = now,
        };
        primaryOwner.OwnerEntityType = OwnerEntityType.LLC;
        primaryOwner.Name = "Maple Ridge Properties LLC";
        primaryOwner.TaxId = "82-3456789";
        primaryOwner.AddressLine1 = "1200 Commerce Dr";
        primaryOwner.City = "Columbus";
        primaryOwner.State = "OH";
        primaryOwner.PostalCode = "43215";
        primaryOwner.Phone = "614-555-0100";
        primaryOwner.UpdatedAt = now;

        var ownerPerson = new OwnerEntity
        {
            PortfolioId = portfolioId,
            OwnerEntityType = OwnerEntityType.Person,
            Name = "Robert J. Caldwell",
            TaxId = "XXX-XX-7890",
            AddressLine1 = "88 Westview Ct",
            City = "Gahanna",
            State = "OH",
            PostalCode = "43230",
            Phone = "614-555-0101",
            CreatedAt = now,
            UpdatedAt = now
        };
        var ownerEntities = new List<OwnerEntity> { primaryOwner, ownerPerson };
        if (primaryOwner.Id == 0) db.OwnerEntities.Add(primaryOwner);
        db.OwnerEntities.Add(ownerPerson);
        await attempt.FlushBusinessAsync(ct);

        // ── 2. Vendors ────────────────────────────────────────────────────────────────
        var vendors = new List<Vendor>
        {
            new() { PortfolioId = portfolioId, Name = "Apex Plumbing Co.", ServiceType = "Plumbing", Email = "dispatch@apexplumbing.example", Phone = "614-555-0200", Is1099Eligible = true, W9OnFile = true, Preferred = true, CreatedAt = now, UpdatedAt = now },
            new() { PortfolioId = portfolioId, Name = "Reliable Electric LLC", ServiceType = "Electrical", Email = "office@relelectric.example", Phone = "614-555-0201", Is1099Eligible = true, W9OnFile = true, Preferred = true, CreatedAt = now, UpdatedAt = now },
            new() { PortfolioId = portfolioId, Name = "ComfortZone HVAC", ServiceType = "HVAC", Email = "service@comfortzonehvac.example", Phone = "614-555-0202", Is1099Eligible = true, W9OnFile = false, Preferred = false, CreatedAt = now, UpdatedAt = now },
            new() { PortfolioId = portfolioId, Name = "Green Thumb Landscaping", ServiceType = "Landscaping", Email = "contact@greenthumb.example", Phone = "614-555-0203", Is1099Eligible = true, W9OnFile = true, Preferred = true, CreatedAt = now, UpdatedAt = now },
            new() { PortfolioId = portfolioId, Name = "Handy Pro Services", ServiceType = "Handyman", Email = "jobs@handypro.example", Phone = "614-555-0204", Is1099Eligible = false, W9OnFile = false, Preferred = false, CreatedAt = now, UpdatedAt = now },
            new() { PortfolioId = portfolioId, Name = "Summit Roofing Inc.", ServiceType = "Roofing", Email = "bids@summitroofing.example", Phone = "614-555-0205", Is1099Eligible = true, W9OnFile = true, Preferred = false, CreatedAt = now, UpdatedAt = now }
        };
        db.Vendors.AddRange(vendors);
        await attempt.FlushBusinessAsync(ct);

        // Local aliases for readability
        var vPlumber     = vendors[0];
        var vElectric    = vendors[1];
        var vHvac        = vendors[2];
        var vLandscape   = vendors[3];
        var vHandyman    = vendors[4];
        var vRoofer      = vendors[5];

        var ownerLlc     = ownerEntities[0];

        // ── 3. Properties + Units ─────────────────────────────────────────────────────
        // 8 properties; ~30 units total; 4 vacant units spread across.
        // PropertyDef: (name, address, city, state, zip, type, yearBuilt, ownerEntityIndex, unitDefs[])
        // UnitDef: (unitNumber, bedrooms, bathrooms, sqft, marketRent)

        var propertyDefs = new (string name, string addr, string city, string state, string zip,
                                PropertyType ptype, int yearBuilt, int ownerIdx,
                                (string num, decimal bed, decimal bath, int sqft, decimal rent, bool vacant)[] units)[]
        {
            (
                "Maple Ridge Duplex",
                "1410 Maple Ridge Rd",
                "Columbus", "OH", "43204",
                PropertyType.MultiFamily, 1988, 0,
                new[]
                {
                    ("A", 2m, 1m, 900,  1050m, false),
                    ("B", 2m, 1m, 900,  1050m, false)
                }
            ),
            (
                "Westview Four-Plex",
                "242 Westview Ave",
                "Columbus", "OH", "43207",
                PropertyType.MultiFamily, 1995, 0,
                new[]
                {
                    ("101", 1m, 1m, 650,  925m, false),
                    ("102", 1m, 1m, 650,  925m, false),
                    ("201", 2m, 1m, 850,  1100m, false),
                    ("202", 2m, 1m, 850,  1100m, true)  // vacant
                }
            ),
            (
                "Gahanna Single Family",
                "88 Birchwood Ln",
                "Gahanna", "OH", "43230",
                PropertyType.SingleFamily, 2003, 1,
                new[]
                {
                    ("Main", 3m, 2m, 1600, 1850m, false)
                }
            ),
            (
                "Clintonville Townhome",
                "55 High Pines Ct",
                "Columbus", "OH", "43214",
                PropertyType.Townhome, 2010, 1,
                new[]
                {
                    ("Main", 3m, 2.5m, 1750, 1950m, false)
                }
            ),
            (
                "Short North Condo",
                "300 N High St",
                "Columbus", "OH", "43215",
                PropertyType.Condo, 2015, 0,
                new[]
                {
                    ("4B",  1m, 1m,  780, 1400m, false)
                }
            ),
            (
                "Eastland 8-Plex",
                "750 Eastland Blvd",
                "Columbus", "OH", "43232",
                PropertyType.MultiFamily, 1979, 0,
                new[]
                {
                    ("1",  2m, 1m,  820, 975m,  false),
                    ("2",  2m, 1m,  820, 975m,  false),
                    ("3",  1m, 1m,  620, 850m,  false),
                    ("4",  1m, 1m,  620, 850m,  true),  // vacant
                    ("5",  2m, 1m,  820, 975m,  false),
                    ("6",  2m, 1m,  820, 975m,  false),
                    ("7",  1m, 1m,  620, 850m,  false),
                    ("8",  1m, 1m,  620, 850m,  false)
                }
            ),
            (
                "Dublin Single Family",
                "12 Overlook Ridge Dr",
                "Dublin", "OH", "43017",
                PropertyType.SingleFamily, 2001, 1,
                new[]
                {
                    ("Main", 4m, 2.5m, 2100, 2400m, false)
                }
            ),
            (
                "Hilliard Duplex",
                "510 Cedar Creek Pl",
                "Hilliard", "OH", "43026",
                PropertyType.MultiFamily, 1992, 0,
                new[]
                {
                    ("Left",  2m, 1m, 960, 1075m, false),
                    ("Right", 2m, 1m, 960, 1075m, true)   // vacant
                }
            )
        };

        var properties = new List<Property>();
        var unitsList   = new List<Unit>();
        var occupiedUnits = new List<Unit>();
        var vacantUnits = new List<Unit>();

        foreach (var pd in propertyDefs)
        {
            var prop = new Property
            {
                PortfolioId       = portfolioId,
                Name              = pd.name,
                AddressLine1      = pd.addr,
                City              = pd.city,
                State             = pd.state,
                PostalCode        = pd.zip,
                PropertyType      = pd.ptype,
                RentalStructure   = pd.units.Length == 1
                    ? RentalStructure.SingleRental
                    : RentalStructure.MultiRental,
                Status            = PropertyStatus.Active,
                YearBuilt         = pd.yearBuilt,
                ManagementFeePercent = 8m,
                CreatedAt         = now,
                UpdatedAt         = now
            };
            properties.Add(prop);
        }

        db.Properties.AddRange(properties);
        await attempt.FlushBusinessAsync(ct);   // get IDs

        var propertyOwnerships = properties.Select((property, index) =>
        {
            var owner = propertyDefs[index].ownerIdx == 0 ? ownerLlc : ownerPerson;
            return new PropertyOwnership
            {
                PortfolioId = portfolioId,
                PropertyId = property.Id,
                OwnerEntityId = owner.Id,
                OwnershipSharePercent = 100m,
                // The demo portfolio includes current and prior-period lease, payment, and
                // expense history. Its ownership history must cover those records or owner
                // statements correctly exclude the seeded financial activity.
                EffectiveFromUtc = new DateTime(
                    now.Year - 2, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                StatementRecipientName = owner.Name,
                StatementRecipientEmail = owner.Email,
                PayeeName = owner.Name,
            };
        }).ToList();
        db.PropertyOwnerships.AddRange(propertyOwnerships);
        await attempt.FlushBusinessAsync(ct);

        // Now add units
        for (int pi = 0; pi < propertyDefs.Length; pi++)
        {
            var pd = propertyDefs[pi];
            var prop = properties[pi];
            foreach (var ud in pd.units)
            {
                var unit = new Unit
                {
                    PortfolioId  = portfolioId,
                    PropertyId   = prop.Id,
                    UnitNumber   = ud.num,
                    Bedrooms     = ud.bed,
                    Bathrooms    = ud.bath,
                    SquareFeet   = ud.sqft,
                    MarketRent   = ud.rent,
                    CreatedAt    = now,
                    UpdatedAt    = now
                };
                unitsList.Add(unit);
                (ud.vacant ? vacantUnits : occupiedUnits).Add(unit);
            }
        }

        db.Units.AddRange(unitsList);
        await attempt.FlushBusinessAsync(ct);   // get unit IDs

        // ── 4. Tenants ────────────────────────────────────────────────────────────────
        // 22 tenants: 17 primary tenants and 2 co-tenants in current relationships, plus
        // 3 past tenants in ended relationships on currently vacant units.
        var tenantData = new (string first, string last, string email, string phone)[]
        {
            ("Marcus",   "Williams",  "marcus.williams@email.example",  "614-555-1001"),
            ("Priya",    "Patel",     "priya.patel@email.example",      "614-555-1002"),
            ("Jordan",   "Smith",     "jordan.smith@email.example",     "614-555-1003"),
            ("Angela",   "Torres",    "angela.torres@email.example",    "614-555-1004"),
            ("Derek",    "Johnson",   "derek.johnson@email.example",    "614-555-1005"),
            ("Samantha", "Lee",       "samantha.lee@email.example",     "614-555-1006"),
            ("Carlos",   "Rivera",    "carlos.rivera@email.example",    "614-555-1007"),
            ("Emily",    "Chen",      "emily.chen@email.example",       "614-555-1008"),
            ("Kevin",    "Brown",     "kevin.brown@email.example",      "614-555-1009"),
            ("Naomi",    "Davis",     "naomi.davis@email.example",      "614-555-1010"),
            ("Tyler",    "Anderson",  "tyler.anderson@email.example",   "614-555-1011"),
            ("Rachel",   "Martinez",  "rachel.martinez@email.example",  "614-555-1012"),
            ("James",    "Wilson",    "james.wilson@email.example",     "614-555-1013"),
            ("Fatima",   "Hassan",    "fatima.hassan@email.example",    "614-555-1014"),
            ("Chris",    "Thompson",  "chris.thompson@email.example",   "614-555-1015"),
            ("Leah",     "Garcia",    "leah.garcia@email.example",      "614-555-1016"),
            ("Darius",   "Clark",     "darius.clark@email.example",     "614-555-1017"),
            ("Sofia",    "Rodriguez", "sofia.rodriguez@email.example",  "614-555-1018"),
            ("Brendan",  "Hall",      "brendan.hall@email.example",     "614-555-1019"),
            // Past tenants for expired leases:
            ("Olivia",   "Scott",     "olivia.scott@email.example",     "614-555-1020"),
            ("Nathan",   "King",      "nathan.king@email.example",      "614-555-1021"),
            ("Danielle", "White",     "danielle.white@email.example",   "614-555-1022")
        };

        var tenants = tenantData.Select((t, i) => new Tenant
        {
            PortfolioId = portfolioId,
            FirstName   = t.first,
            LastName    = t.last,
            Email       = t.email,
            Phone       = t.phone,
            CreatedAt   = now.AddMonths(-18 + i / 2),
            UpdatedAt   = now
        }).ToList();

        db.Tenants.AddRange(tenants);
        await attempt.FlushBusinessAsync(ct);

        // ── 5. Canonical lease, agreement, account, and ledger graph ──────────────────
        var leaseSeed = await CanonicalDemoLeaseSeeder.SeedAsync(
            db,
            attempt,
            portfolioId,
            actorUserId,
            currency,
            now,
            occupiedUnits,
            vacantUnits,
            tenants,
            ct);
        var activeLeaseManagements = leaseSeed.ActiveManagements;

        // ── 8. Expenses (~35) ─────────────────────────────────────────────────────────
        // Spread across properties and categories, varied dates over the past 12 months.
        var expenseDefs = new (int propIdx, int vendorIdx, ScheduleECategory cat, string desc, decimal amount, int daysAgo, bool paid)[]
        {
            (0, 0, ScheduleECategory.Repairs,             "Fix leaking pipe under kitchen sink – Unit A",          285m,  320, true),
            (0, 4, ScheduleECategory.CleaningMaintenance, "Unit B turnover cleaning after previous tenant",         180m,  310, true),
            (1, 1, ScheduleECategory.Repairs,             "Replace faulty GFCI outlets in bathrooms",               220m,  305, true),
            (1, 2, ScheduleECategory.Repairs,             "AC unit refrigerant recharge – Unit 101",                350m,  298, true),
            (1, 3, ScheduleECategory.CleaningMaintenance, "Spring lawn fertilization and edging",                   160m,  290, true),
            (2, 4, ScheduleECategory.Repairs,             "Patch drywall in living room",                           145m,  280, true),
            (2, 2, ScheduleECategory.Repairs,             "Furnace tune-up and filter replacement",                 210m,  275, true),
            (3, 0, ScheduleECategory.Repairs,             "Replace bathroom faucet cartridge",                       95m,  265, true),
            (3, 4, ScheduleECategory.Supplies,            "Smoke/CO detectors replaced per code",                   120m,  250, true),
            (4, 1, ScheduleECategory.Repairs,             "Rewire kitchen outlet that tripped breaker",             310m,  240, true),
            (5, 2, ScheduleECategory.Repairs,             "Replace aging furnace – Unit 3",                        2850m, 230, true),
            (5, 3, ScheduleECategory.CleaningMaintenance, "Summer lawn mowing contract (12 cuts)",                  480m,  220, true),
            (5, 0, ScheduleECategory.Repairs,             "Repair burst supply line – Unit 6",                      195m,  210, true),
            (6, 4, ScheduleECategory.CleaningMaintenance, "Carpet steam cleaning after lease renewal",              175m,  200, true),
            (6, 4, ScheduleECategory.Repairs,             "Fix garage door spring",                                 230m,  190, true),
            (7, 0, ScheduleECategory.Repairs,             "Replace toilet fill valve – Right unit",                  85m,  185, true),
            (0, 5, ScheduleECategory.Repairs,             "Roof shingle repair after hailstorm",                    740m,  175, true),
            (1, 1, ScheduleECategory.Utilities,           "Electric panel inspection per code cycle",               280m,  165, true),
            (2, 3, ScheduleECategory.CleaningMaintenance, "Fall leaf removal and gutter cleaning",                  210m,  155, true),
            (3, 2, ScheduleECategory.Repairs,             "HVAC system annual maintenance contract",                395m,  145, true),
            (4, 4, ScheduleECategory.Repairs,             "Touch-up paint in hallways and stairwell",               155m,  135, true),
            (5, 0, ScheduleECategory.CleaningMaintenance, "Drain cleaning – Units 2 and 5",                        190m,  125, true),
            (6, 4, ScheduleECategory.Supplies,            "Exterior light fixtures replaced (4 units)",             260m,  115, true),
            (7, 3, ScheduleECategory.CleaningMaintenance, "Hedge trimming and mulch – Left unit yard",              140m,  105, true),
            (0, 2, ScheduleECategory.Repairs,             "Replace HVAC thermostat – Unit A",                      175m,   95, true),
            (1, 1, ScheduleECategory.Repairs,             "Ceiling fan wiring repair – Unit 202",                   130m,   85, true),
            (2, 4, ScheduleECategory.Repairs,             "Weatherstripping replacement on doors",                   90m,   75, true),
            (3, 0, ScheduleECategory.Repairs,             "Fix slow drain in master bathroom",                      115m,   65, true),
            (5, 5, ScheduleECategory.Repairs,             "Flat roof membrane patch – rear section",                890m,   55, true),
            (6, 2, ScheduleECategory.Repairs,             "Replace aging water heater (50 gal)",                   1150m,  45, true),
            (7, 4, ScheduleECategory.Repairs,             "Fix sticking exterior door – Right unit",                 80m,   35, true),
            // Pending expenses (recent, not yet paid)
            (0, 3, ScheduleECategory.CleaningMaintenance, "Spring landscaping and lawn care",                       195m,   20, false),
            (1, 2, ScheduleECategory.Repairs,             "Quote for central AC replacement – Unit 202",             0m,    15, false),
            (5, 5, ScheduleECategory.Insurance,           "Annual property insurance premium renewal",             3200m,   10, false),
            (4, 1, ScheduleECategory.Repairs,             "Inspect and repair exterior outlets (code)",             225m,    5, false)
        };

        // A subset of expenses simulate receipts/invoices captured via the scan→draft→confirm flow.
        // For these we populate the full scan-db schema exactly as ScanService does on confirm:
        // typed columns (Subtotal/TaxAmount/PaymentMethod/CardLast4/DocumentKind), the ReceiptData JSONB
        // superset (same shape as ScanService.BuildReceiptDataJson, so the expense detail UI parses it
        // identically to a real scan), and the ExpenseLineItem child rows. Keyed by index into expenseDefs.
        // Line totals always sum to less than the expense Amount; the remainder becomes TaxAmount.
        var scanReceipts =
            new Dictionary<int, (string documentKind, string? paymentMethod, string? cardLast4,
                                 string receiptNumber, decimal taxRate,
                                 (string desc, decimal? qty, decimal? unitPrice, decimal amount)[] lines)>
            {
                [0] = ("Invoice", "Check", null, "APX-10432", 0m, new[]
                {
                    ("Service call & labor (1.5 hrs)", (decimal?)1.5m, (decimal?)90m, 135m),
                    ("P-trap, supply line & fittings", (decimal?)null, (decimal?)null, 135m),
                }),
                [8] = ("Receipt", "Visa", "4821", "5582-1107", 0.075m, new[]
                {
                    ("First Alert smoke detector", (decimal?)3m, (decimal?)18.99m, 56.97m),
                    ("Kidde carbon-monoxide detector", (decimal?)2m, (decimal?)24.49m, 48.98m),
                }),
                [10] = ("Invoice", "Check", null, "CZ-22841", 0m, new[]
                {
                    ("Carrier 96% AFUE furnace (60k BTU)", (decimal?)1m, (decimal?)1950m, 1950m),
                    ("Installation labor", (decimal?)null, (decimal?)null, 700m),
                    ("Permit & old-unit disposal", (decimal?)null, (decimal?)null, 150m),
                }),
                [16] = ("Invoice", "Check", null, "SR-9043", 0m, new[]
                {
                    ("Architectural shingles (2 sq)", (decimal?)2m, (decimal?)140m, 280m),
                    ("Underlayment, flashing & nails", (decimal?)null, (decimal?)null, 160m),
                    ("Roofing labor", (decimal?)null, (decimal?)null, 280m),
                }),
                [22] = ("Receipt", "Mastercard", "7012", "RW-330815", 0.075m, new[]
                {
                    ("LED outdoor wall lantern", (decimal?)4m, (decimal?)49.97m, 199.88m),
                    ("Dusk-to-dawn photocell sensor", (decimal?)4m, (decimal?)12.50m, 50.00m),
                }),
                [29] = ("Invoice", "Check", null, "APX-11890", 0m, new[]
                {
                    ("Rheem 50-gal gas water heater", (decimal?)1m, (decimal?)749m, 749m),
                    ("Installation labor", (decimal?)null, (decimal?)null, 350m),
                    ("Expansion tank & fittings", (decimal?)null, (decimal?)null, 30m),
                }),
            };

        // Keep unit-specific sample expenses attached to the unit named in their description. A
        // property-only row that says “Unit A” teaches the wrong data model and renders a
        // contradictory “No unit” detail screen.
        var expenseUnitNumbers = new Dictionary<int, string>
        {
            [0] = "A",
            [1] = "B",
            [3] = "101",
            [10] = "3",
            [12] = "6",
            [15] = "Right",
            [23] = "Left",
            [24] = "A",
            [25] = "202",
            [30] = "Right",
            [32] = "202",
        };

        var expenses = expenseDefs.Select((ed, i) =>
        {
            var prop    = properties[ed.propIdx];
            var vendor  = vendors[ed.vendorIdx];
            var incDate = now.AddDays(-ed.daysAgo);
            var unit = expenseUnitNumbers.TryGetValue(i, out var unitNumber)
                ? unitsList.Single(candidate =>
                    candidate.PropertyId == prop.Id && candidate.UnitNumber == unitNumber)
                : null;
            var expense = new Expense
            {
                PortfolioId    = portfolioId,
                OperationalScope = unit is null
                    ? ExpenseOperationalScope.Property
                    : ExpenseOperationalScope.Unit,
                PropertyId     = prop.Id,
                UnitId         = unit?.Id,
                VendorId       = vendor.Id,
                Category       = ed.cat,
                Description    = ed.desc,
                Amount         = ed.amount,
                Status         = ed.paid ? ExpenseStatus.Paid : ExpenseStatus.Pending,
                IncurredAt     = incDate,
                PaidAt         = ed.paid ? incDate.AddDays(3 + i % 5) : null,
                BillableToOwner = i % 4 == 0,
                CreatedAt      = incDate,
                UpdatedAt      = now
            };

            if (scanReceipts.TryGetValue(i, out var s))
            {
                var subtotal  = s.lines.Sum(l => l.amount);
                var taxAmount = Math.Max(0m, Math.Round(ed.amount - subtotal, 2));

                expense.Subtotal      = subtotal;
                expense.TaxAmount     = taxAmount;
                expense.PaymentMethod = s.paymentMethod;
                expense.CardLast4     = s.cardLast4;
                expense.DocumentKind  = s.documentKind;
                expense.ReceiptData   = JsonSerializer.Serialize(new
                {
                    documentKind  = s.documentKind,
                    dueDate       = (string?)null,
                    vendor        = new { address = (string?)null, phone = vendor.Phone, website = (string?)null, taxId = (string?)null },
                    receiptNumber = s.receiptNumber,
                    paymentMethod = s.paymentMethod,
                    cardLast4     = s.cardLast4,
                    taxRate       = s.taxRate == 0m ? (decimal?)null : s.taxRate,
                    tip           = (decimal?)null,
                    discount      = (decimal?)null,
                    shipping      = (decimal?)null,
                    lineItems     = s.lines.Select(l => new
                    {
                        description = l.desc,
                        quantity    = l.qty,
                        unitPrice   = l.unitPrice,
                        amount      = (decimal?)l.amount,
                    }).ToArray(),
                    extra         = new { taxAmount, source = "demo-seed" },
                });

                var lineNo = 1;
                foreach (var l in s.lines)
                {
                    expense.LineItems.Add(new ExpenseLineItem
                    {
                        Description = l.desc,
                        Quantity    = l.qty,
                        UnitPrice   = l.unitPrice,
                        Amount      = l.amount,
                        LineNumber  = lineNo++,
                    });
                }
            }

            return expense;
        }).ToList();

        db.Expenses.AddRange(expenses);
        await attempt.FlushBusinessAsync(ct);

        // ── 9. WorkOrders (~15) ───────────────────────────────────────────────────────
        var woDefs = new (int propIdx, int? unitIdx, int vendorIdx, int? tenantLeaseIdx,
                          string title, string desc, string category,
                          WorkOrderPriority priority, WorkOrderStatus status,
                          int daysAgo, int? completedDaysAgo)[]
        {
            (5, 3,  0, null,  "Burst supply line repair",           "Water pipe burst in kitchen of Unit 6. Immediate repair required.",            "Plumbing",    WorkOrderPriority.Emergency, WorkOrderStatus.Completed, 210, 207),
            (5, 10, 2, null,  "Furnace replacement – Unit 3",        "Old furnace failed mid-winter, replace with new 96% efficiency unit.",         "HVAC",        WorkOrderPriority.High,      WorkOrderStatus.Completed, 230, 220),
            (0, 0,  0, 0,     "Leaking kitchen sink",               "Tenant reported dripping pipe under kitchen sink. Need plumber to assess.",    "Plumbing",    WorkOrderPriority.Normal,    WorkOrderStatus.Completed, 320, 315),
            (1, 2,  2, 2,     "AC not cooling – Unit 101",          "Tenant reports AC not maintaining temperature. Check refrigerant.",            "HVAC",        WorkOrderPriority.High,      WorkOrderStatus.Completed, 298, 292),
            (6, 14, 4, 12,    "Garage door spring broken",          "Main unit garage door spring snapped. Door won't open.",                       "Appliances",  WorkOrderPriority.Normal,    WorkOrderStatus.Completed, 190, 185),
            (2, 7,  2, 6,     "HVAC annual tune-up",                "Annual furnace service and filter change before winter season.",                "HVAC",        WorkOrderPriority.Low,       WorkOrderStatus.Completed, 275, 271),
            (4, 13, 1, 8,     "Outlet tripping breaker",            "Kitchen outlet keeps tripping. May need rewire or breaker replacement.",        "Electrical",  WorkOrderPriority.Normal,    WorkOrderStatus.Completed, 240, 235),
            (3, 9,  4, 5,     "Patch living room drywall",          "Small hole in living room wall from previous tenant move-out.",                 "Maintenance", WorkOrderPriority.Low,       WorkOrderStatus.Completed, 280, 276),
            (7, 16, 0, 15,    "Toilet not filling – Right unit",    "Toilet fill valve not working. Tenant using second bathroom temporarily.",      "Plumbing",    WorkOrderPriority.Normal,    WorkOrderStatus.Completed, 185, 182),
            (0, 0,  5, null,  "Roof shingle repair",                "Hail damage detected. Several shingles cracked or missing.",                   "Roofing",     WorkOrderPriority.High,      WorkOrderStatus.InProgress, 175, null),
            (1, 2,  2, null,  "HVAC quote – Unit 202 AC",           "Tenant leaving; get quote to replace aging central AC before re-leasing.",     "HVAC",        WorkOrderPriority.Normal,    WorkOrderStatus.Scheduled,  15, null),
            (5, 11, 4, 9,     "Exterior lights not working",        "Common area lights on south side not turning on. Check fixtures and wiring.",   "Electrical",  WorkOrderPriority.Normal,    WorkOrderStatus.InProgress, 12, null),
            (2, 7,  4, 6,     "Weatherstripping replacement",       "Cold air coming in under front door. Replace weatherstripping.",                "Maintenance", WorkOrderPriority.Low,       WorkOrderStatus.New,         8, null),
            (6, 14, 0, 12,    "Slow drain – master bath",           "Master bathroom drain slow. Need to snake or treat.",                           "Plumbing",    WorkOrderPriority.Normal,    WorkOrderStatus.New,         4, null),
            (5, 10, 1, null,  "Inspect exterior outlets",           "Annual safety inspection of all exterior GFCI outlets on building.",            "Electrical",  WorkOrderPriority.Low,       WorkOrderStatus.New,         2, null),
        };

        // Map unit index within our unitsList (0-based across all properties)
        // WO unit indices above are approximate positions in unitsList; we'll use modulo safety.
        var woList = new List<WorkOrder>();
        for (int i = 0; i < woDefs.Length; i++)
        {
            var wd = woDefs[i];
            var prop   = properties[wd.propIdx];
            var vendor = vendors[wd.vendorIdx];

            // Pick a unit safely from the property's own units
            var propUnits = unitsList.Where(u => u.PropertyId == prop.Id).ToList();
            Unit? unit = wd.unitIdx.HasValue && propUnits.Count > 0
                ? propUnits[wd.unitIdx.Value % propUnits.Count]
                : null;

            // Pick tenant/canonical lease relationship if specified.
            LeaseManagement? leaseManagement = null;
            Tenant? tenant = null;
            if (wd.tenantLeaseIdx.HasValue && wd.tenantLeaseIdx.Value < activeLeaseManagements.Count)
            {
                leaseManagement = activeLeaseManagements[wd.tenantLeaseIdx.Value];
                tenant = tenants[wd.tenantLeaseIdx.Value];
            }

            var requestedAt = now.AddDays(-wd.daysAgo);
            var wo = new WorkOrder
            {
                PortfolioId   = portfolioId,
                PropertyId    = prop.Id,
                UnitId        = unit?.Id,
                VendorId      = vendor.Id,
                TenantId      = tenant?.Id,
                LeaseManagementId = leaseManagement?.Id,
                Title         = wd.title,
                Description   = wd.desc,
                Category      = wd.category,
                Priority      = wd.priority,
                Status        = wd.status,
                RequestedAt   = requestedAt,
                ScheduledFor  = wd.status != WorkOrderStatus.New ? requestedAt.AddDays(2) : null,
                CompletedAt   = wd.completedDaysAgo.HasValue ? now.AddDays(-wd.completedDaysAgo.Value) : null,
                EstimatedCost = (i % 3 == 0) ? (decimal?)(150m + i * 40) : null,
                ActualCost    = wd.completedDaysAgo.HasValue ? (decimal?)(120m + i * 35) : null,
                CreatedBy     = "admin@rentalcommand.local",
                UpdatedAt     = now
            };

            // The AC-replacement quote represents a vendor estimate captured via the scan flow —
            // populate the ExtractedData JSONB so demo data exercises the WorkOrder scan record type.
            if (i == 10)
            {
                wo.ExtractedData = JsonSerializer.Serialize(new
                {
                    documentKind  = "Estimate",
                    vendorName    = vendor.Name,
                    estimateTotal = 4200m,
                    validUntil    = now.AddDays(30).ToString("yyyy-MM-dd"),
                    scope         = "Replace central AC condenser + air handler — Unit 202",
                    source        = "demo-seed",
                });
            }

            woList.Add(wo);
        }

        db.WorkOrders.AddRange(woList);
        await attempt.FlushBusinessAsync(ct);

        // ── 10. Appointments (~12) ────────────────────────────────────────────────────
        var apptDefs = new (int propIdx, AppointmentType type, AppointmentStatus status,
                            string title, int? tenantIdx, int daysOffset)[]
        {
            (1,  AppointmentType.Showing,         AppointmentStatus.Completed, "Showing – Unit 202 (vacant)",    null,  -45),
            (1,  AppointmentType.Showing,         AppointmentStatus.Completed, "Showing – Unit 202 prospect #2", null,  -38),
            (7,  AppointmentType.Showing,         AppointmentStatus.Scheduled, "Showing – Hilliard Right unit",  null,   7),
            (0,  AppointmentType.MoveIn,          AppointmentStatus.Completed, "Move-in walkthrough – Unit A",   0,     -540),
            (2,  AppointmentType.MoveIn,          AppointmentStatus.Completed, "Move-in walkthrough – Gahanna",  4,     -365),
            (6,  AppointmentType.MoveOut,         AppointmentStatus.Completed, "Move-out inspection – Dublin",   12,    -90),
            (3,  AppointmentType.Inspection,      AppointmentStatus.Completed, "Annual safety inspection",       5,     -60),
            (5,  AppointmentType.MaintenanceVisit,AppointmentStatus.Completed, "Furnace replacement access",     null,  -230),
            (0,  AppointmentType.MaintenanceVisit,AppointmentStatus.Scheduled, "Roofing crew access",            null,   3),
            (4,  AppointmentType.OwnerMeeting,    AppointmentStatus.Completed, "Owner review – Short North",     null,  -30),
            (6,  AppointmentType.Inspection,      AppointmentStatus.Scheduled, "Routine inspection – Dublin SFH",13,    10),
            (1,  AppointmentType.Showing,         AppointmentStatus.NoShow,    "Showing – Unit 202 no-show",     null,  -20),
        };

        var appts = new List<Appointment>();
        for (int i = 0; i < apptDefs.Length; i++)
        {
            var ad   = apptDefs[i];
            var prop = properties[ad.propIdx];
            var propUnits2 = unitsList.Where(u => u.PropertyId == prop.Id).ToList();

            Tenant?  apptTenant = ad.tenantIdx.HasValue ? tenants[ad.tenantIdx.Value] : null;
            LeaseManagement? apptLeaseManagement = null;
            if (ad.tenantIdx.HasValue && ad.tenantIdx.Value < activeLeaseManagements.Count)
                apptLeaseManagement = activeLeaseManagements[ad.tenantIdx.Value];

            var scheduledStart = now.AddDays(ad.daysOffset).Date.AddHours(10 + i % 4);
            appts.Add(new Appointment
            {
                PortfolioId    = portfolioId,
                PropertyId     = prop.Id,
                UnitId         = propUnits2.Count > 0 ? propUnits2[i % propUnits2.Count].Id : null,
                TenantId       = apptTenant?.Id,
                LeaseManagementId = apptLeaseManagement?.Id,
                Title          = ad.title,
                Type           = ad.type,
                Status         = ad.status,
                ScheduledStart = scheduledStart,
                ScheduledEnd   = scheduledStart.AddHours(1),
                AssignedTo     = "admin@rentalcommand.local",
                Notes          = i % 3 == 0 ? "Confirmed via phone day before." : null,
                ProspectName   = ad.type == AppointmentType.Showing && apptTenant == null ? $"Prospect {i + 1}" : null,
                ProspectEmail  = ad.type == AppointmentType.Showing && apptTenant == null ? $"prospect{i + 1}@email.example" : null,
                CreatedAt      = now.AddDays(ad.daysOffset - 3),
                UpdatedAt      = now
            });
        }

        db.Appointments.AddRange(appts);
        await attempt.FlushBusinessAsync(ct);

        // ── 11. Inspections (~6) ──────────────────────────────────────────────────────
        var inspDefs = new (int propIdx, InspectionType type, InspectionStatus status,
                            int? tenantIdx, int daysOffset, string? outcome)[]
        {
            (0,  InspectionType.MoveIn,      InspectionStatus.Completed,    0,  -540, "Good condition. Minor scuff on bedroom wall noted."),
            (2,  InspectionType.MoveIn,      InspectionStatus.Completed,    4,  -365, "Excellent condition. All appliances operational."),
            (5,  InspectionType.Routine,     InspectionStatus.Completed,    8,  -180, "Routine check passed. HVAC filter replaced."),
            (3,  InspectionType.AnnualSafety,InspectionStatus.Completed,    5,   -60, "All smoke detectors functional. CO detector replaced."),
            (6,  InspectionType.MoveOut,     InspectionStatus.Completed,    12,  -90, "Minor carpet wear in master bedroom. Normal wear and tear."),
            (6,  InspectionType.Routine,     InspectionStatus.Scheduled,    13,   10, null),
        };

        // Pick the built-in template that matches an inspection's type so the seeded inspections
        // carry a real checklist (the PASS/FAIL/N-A tiles populate). Falls back to the Routine →
        // Annual Safety list when an exact type match isn't a built-in.
        InspectionTemplate TemplateForType(InspectionType type) =>
            InspectionTemplateCatalog.BuiltIns.FirstOrDefault(t => t.InspectionType == type)
            ?? InspectionTemplateCatalog.BuiltIns.First(t => t.InspectionType == InspectionType.AnnualSafety);

        // Deterministic-but-varied result for a completed inspection's checklist item. Most items
        // pass; a couple per inspection fail (to spawn follow-ups / populate the FAIL tile) and a
        // couple are N/A — so demo inspections look like real walkthroughs.
        static (InspectionItemResult Result, string? Note) ResultForCompletedItem(int inspectionSeed, int itemIndex, string label)
        {
            var slot = (inspectionSeed * 7 + itemIndex) % 11;
            return slot switch
            {
                3 => (InspectionItemResult.Fail, $"{label}: needs attention — follow-up work order created."),
                7 => (InspectionItemResult.NotApplicable, $"{label}: not present in this unit."),
                _ => (InspectionItemResult.Pass, null),
            };
        }

        var inspections = new List<Inspection>();
        for (int i = 0; i < inspDefs.Length; i++)
        {
            var id   = inspDefs[i];
            var prop = properties[id.propIdx];
            var propUnits3 = unitsList.Where(u => u.PropertyId == prop.Id).ToList();

            LeaseManagement? inspectionLeaseManagement = null;
            if (id.tenantIdx.HasValue && id.tenantIdx.Value < activeLeaseManagements.Count)
                inspectionLeaseManagement = activeLeaseManagements[id.tenantIdx.Value];

            var scheduled = now.AddDays(id.daysOffset).Date.AddHours(9);
            var template  = TemplateForType(id.type);
            var completed = id.status == InspectionStatus.Completed;

            var inspection = new Inspection
            {
                PortfolioId  = portfolioId,
                PropertyId   = prop.Id,
                UnitId       = propUnits3.Count > 0 ? propUnits3[i % propUnits3.Count].Id : null,
                LeaseManagementId = inspectionLeaseManagement?.Id,
                Type         = id.type,
                Status       = id.status,
                ScheduledFor = scheduled,
                CompletedAt  = completed ? scheduled.AddHours(1) : null,
                Outcome      = id.outcome,
                Inspector    = "Property Manager",
                TemplateId   = template.Id,
                CreatedAt    = now.AddDays(id.daysOffset - 5),
                UpdatedAt    = now
            };

            // Materialize the template's checklist. Completed inspections get realistic
            // pass/fail/N-A results; the scheduled (not-yet-walked) one stays all-Pending.
            var sortOrder = 0;
            foreach (var templateItem in template.Items.OrderBy(it => it.SortOrder))
            {
                var (result, note) = completed
                    ? ResultForCompletedItem(i, sortOrder, templateItem.Label)
                    : (InspectionItemResult.Pending, (string?)null);

                inspection.Items.Add(new InspectionItem
                {
                    PortfolioId = portfolioId,
                    Area        = templateItem.Area,
                    Label       = templateItem.Label,
                    Result      = result,
                    Note        = note,
                    SortOrder   = sortOrder++,
                });
            }

            inspections.Add(inspection);
        }

        db.Inspections.AddRange(inspections);
        await attempt.FlushBusinessAsync(ct);

        // ── Done ──────────────────────────────────────────────────────────────────────
        return new SeedDemoPortfolioResult(
            portfolioId,
            false,
            ownerEntities.Count,
            vendors.Count,
            properties.Count,
            unitsList.Count,
            tenants.Count,
            leaseSeed.ActiveManagements.Count,
            leaseSeed.ExpiredManagementCount,
            leaseSeed.LedgerEntryCount,
            leaseSeed.SecurityDepositAccountCount,
            expenses.Count,
            woList.Count,
            appts.Count,
            inspections.Count,
            leaseSeed.LegalDocumentIntents.FirstOrDefault()?.AgreementId);
    }

    private async Task<int> ResolveActorUserIdAsync(int portfolioId, CancellationToken ct) =>
        await _db.Users
            .Where(user => user.WorkspaceAccessContexts.Any(context =>
                context.PortfolioId == portfolioId && context.Membership != null))
            .OrderBy(user => user.Id)
            .Select(user => user.Id)
            .FirstOrDefaultAsync(ct) is var actorUserId && actorUserId > 0
                ? actorUserId
                : throw new InvalidOperationException(
                    $"Portfolio {portfolioId} has no administering user for demo facts.");

    private static SeedDemoPortfolioResult Result(
        int portfolioId,
        bool alreadyPresent,
        CanonicalDemoLeaseSeedResult leaseSeed,
        int? legalAgreementId) =>
        new(
            portfolioId,
            alreadyPresent,
            0,
            0,
            0,
            0,
            0,
            leaseSeed.ActiveManagements.Count,
            leaseSeed.ExpiredManagementCount,
            leaseSeed.LedgerEntryCount,
            leaseSeed.SecurityDepositAccountCount,
            0,
            0,
            0,
            0,
            legalAgreementId);

    private FinalizeDemoLegalDocumentCommand ToFinalizeCommand(
        PreparedDemoLegalDocument prepared) =>
        new(
            prepared.Intent.PortfolioId,
            prepared.Intent.ActorUserId,
            prepared.Intent.AgreementId,
            prepared.IssuedAdmission.Id,
            prepared.IssuedAdmission.StoragePath,
            prepared.IssuedAdmission.RequestFingerprint,
            prepared.IssuedFileName,
            prepared.IssuedLength,
            prepared.IssuedHash,
            prepared.IssuanceFingerprint,
            prepared.ExecutedAdmission.Id,
            prepared.ExecutedAdmission.StoragePath,
            prepared.ExecutedAdmission.RequestFingerprint,
            prepared.ExecutedFileName,
            prepared.ExecutedLength,
            prepared.ExecutedHash,
            prepared.Intent.IssuedAtUtc,
            prepared.Intent.ExecutedAtUtc);

    private async Task<PreparedDemoLegalDocument> PrepareLegalDocumentAsync(
        CanonicalDemoLegalDocumentIntent intent,
        CancellationToken ct)
    {
        EnsureProviderIoIsOutsideAtomicAttempt();

        var issued = await _agreementRenderer.RenderExactAsync(
            intent.PortfolioId,
            intent.DocumentSourceVersionId,
            intent.RenderData,
            () => _agreementPdf.Generate(intent.RenderData),
            ct);
        EnsurePdf(issued.PdfBytes, "issued");
        var issuedHash = Sha256(issued.PdfBytes);
        var agreementNumber = intent.RenderData.AgreementNumber;
        var issuedFileName = $"{agreementNumber}-issued.pdf";
        var issuanceFingerprint = LegalDocumentIssuanceBinding.Create(
            nameof(LeaseAgreement),
            intent.PortfolioId,
            intent.LeaseManagementId,
            intent.AgreementId,
            intent.DraftRevision,
            intent.DocumentSourceVersionId,
            intent.TermsSchemaVersion,
            intent.TermsPayload,
            issuedHash,
            issued.PdfBytes.LongLength,
            issuedFileName);

        var executedBytes = _executedLeasePdf.Generate(
            new ExecutedLeaseData
            {
                Agreement = intent.RenderData,
                Signers =
                [
                    new ExecutedSigner
                    {
                        Name = intent.TenantName,
                        Email = intent.TenantEmail,
                        SignerRole = DocumentTemplateSignerRole.Tenant,
                        SignatureType = SignatureSignatureType.Typed,
                        TypedName = intent.TenantName,
                        SignedAtUtc = intent.ExecutedAtUtc,
                        ViewedAtUtc = intent.ExecutedAtUtc.AddMinutes(-2),
                        ConsentGiven = true,
                    },
                    new ExecutedSigner
                    {
                        Name = intent.RenderData.LandlordName,
                        Email = "admin@rentalcommand.local",
                        SignerRole = DocumentTemplateSignerRole.Landlord,
                        SignatureType = SignatureSignatureType.Typed,
                        TypedName = intent.RenderData.LandlordName,
                        SignedAtUtc = intent.ExecutedAtUtc,
                        ViewedAtUtc = intent.ExecutedAtUtc.AddMinutes(-1),
                        ConsentGiven = true,
                    },
                ],
                LandlordName = intent.RenderData.LandlordName,
                EnvelopeId = $"demo-{intent.PortfolioId}-{agreementNumber}",
                DocumentName = issuedFileName,
                OriginalDocumentBytes = issued.PdfBytes,
                TemplateFieldSnapshotJson = issued.TemplateFieldSnapshotJson,
                CompletedAtUtc = intent.ExecutedAtUtc,
            },
            issuedHash);
        EnsurePdf(executedBytes, "executed");
        var executedHash = Sha256(executedBytes);
        var executedFileName = $"{agreementNumber}-executed.pdf";

        var issuedAdmission = await _pendingUploads.PrepareAsync(
            intent.PortfolioId,
            intent.ActorUserId,
            "demo-legal-issued",
            $"demo-legal/{intent.PortfolioId}/{agreementNumber}/issued/v2/{issuanceFingerprint}",
            issuanceFingerprint,
            issuedFileName,
            "application/pdf",
            issued.PdfBytes.LongLength,
            intent.IssuedAtUtc,
            ct);
        await UploadPreparedAsync(issuedAdmission, issued.PdfBytes, issuedFileName, ct);

        var executedAdmission = await _pendingUploads.PrepareAsync(
            intent.PortfolioId,
            intent.ActorUserId,
            "demo-legal-executed",
            $"demo-legal/{intent.PortfolioId}/{agreementNumber}/executed/v2/{executedHash}",
            executedHash,
            executedFileName,
            "application/pdf",
            executedBytes.LongLength,
            intent.ExecutedAtUtc,
            ct);
        await UploadPreparedAsync(executedAdmission, executedBytes, executedFileName, ct);

        return new PreparedDemoLegalDocument(
            intent,
            issuedAdmission,
            issuedFileName,
            issued.PdfBytes.LongLength,
            issuedHash,
            issuanceFingerprint,
            executedAdmission,
            executedFileName,
            executedBytes.LongLength,
            executedHash);
    }

    private async Task UploadPreparedAsync(
        PendingFileUploadAdmission admission,
        byte[] bytes,
        string fileName,
        CancellationToken ct)
    {
        if (admission.State == PendingFileUploadState.Abandoned)
        {
            throw new InvalidOperationException("The deterministic demo legal-document upload was abandoned.");
        }
        if (admission.State != PendingFileUploadState.Prepared)
        {
            return;
        }

        EnsureProviderIoIsOutsideAtomicAttempt();
        await using var stream = new MemoryStream(bytes, writable: false);
        await _fileStorage.UploadAtAsync(stream, admission.StoragePath, fileName, "application/pdf", ct);
    }

    internal static async Task<FinalizeDemoLegalDocumentResult> FinalizeLegalDocumentAsync(
        RentalCommandDbContext db,
        FinalizeDemoLegalDocumentCommand command,
        IAtomicCommandContext attempt,
        CancellationToken ct)
    {
        var agreement = await db.LeaseAgreements
            .Include(candidate => candidate.LeaseManagement)
            .SingleAsync(candidate => candidate.PortfolioId == command.PortfolioId
                && candidate.Id == command.AgreementId
                && candidate.AgreementNumber.StartsWith("DEMO-AGR-ACTIVE-")
                && candidate.LeaseManagement!.RelationshipNumber.StartsWith("DEMO-LM-ACTIVE-")
                && candidate.LeaseManagement.RelationshipNumber
                    != CanonicalDemoLeaseSeeder.PossessionExceptionRelationshipNumber, ct);

        var pendingById = await LockPendingUploadsAsync(
            db,
            [command.IssuedPendingUploadId, command.ExecutedPendingUploadId],
            ct);
        if (!pendingById.TryGetValue(command.IssuedPendingUploadId, out var issuedPending)
            || !pendingById.TryGetValue(command.ExecutedPendingUploadId, out var executedPending))
        {
            throw new InvalidOperationException("The deterministic demo legal-document admissions are unavailable.");
        }
        ValidatePending(
            issuedPending,
            command.IssuedStoragePath,
            command.IssuedRequestFingerprint,
            command.IssuedLength);
        ValidatePending(
            executedPending,
            command.ExecutedStoragePath,
            command.ExecutedRequestFingerprint,
            command.ExecutedLength);

        var artifactStorageKeys = new[] { issuedPending.StoragePath, executedPending.StoragePath };
        var existingArtifactsByStorageKey = await db.LegalDocumentArtifacts
            .Where(artifact => artifact.PortfolioId == command.PortfolioId
                && artifactStorageKeys.Contains(artifact.StorageKey))
            .ToDictionaryAsync(artifact => artifact.StorageKey, ct);

        var issuedArtifact = await GetOrCreateArtifactAsync(
            db,
            attempt,
            command,
            issuedPending,
            existingArtifactsByStorageKey,
            LegalDocumentArtifactKind.IssuedAgreement,
            command.IssuedFileName,
            command.IssuedLength,
            command.IssuedHash,
            command.IssuanceFingerprint,
            ct);
        var executedArtifact = await GetOrCreateArtifactAsync(
            db,
            attempt,
            command,
            executedPending,
            existingArtifactsByStorageKey,
            LegalDocumentArtifactKind.ExecutedAgreement,
            command.ExecutedFileName,
            command.ExecutedLength,
            command.ExecutedHash,
            null,
            ct);

        if (agreement.IssuedArtifactId is not null && agreement.IssuedArtifactId != issuedArtifact.Id
            || agreement.ExecutedArtifactId is not null && agreement.ExecutedArtifactId != executedArtifact.Id)
        {
            throw new InvalidOperationException("The deterministic demo Agreement is already bound to different artifacts.");
        }

        agreement.IssuedArtifactId = issuedArtifact.Id;
        var alreadyFinalized = agreement.IssuedArtifactId == issuedArtifact.Id
            && agreement.ExecutedArtifactId == executedArtifact.Id;
        agreement.IssuedAtUtc = command.IssuedAtUtc;
        agreement.ExecutedArtifactId = executedArtifact.Id;
        agreement.FullyExecutedAtUtc = command.ExecutedAtUtc;
        agreement.UpdatedAtUtc = command.ExecutedAtUtc;
        agreement.LeaseManagement!.PossessionAgreementExceptionReason = null;
        agreement.LeaseManagement.PossessionAgreementExceptionAuthorizedByUserId = null;
        agreement.LeaseManagement.UpdatedAtUtc = command.ExecutedAtUtc;
        agreement.LeaseManagement.RowVersion = Guid.NewGuid();

        issuedPending.State = PendingFileUploadState.Finalized;
        issuedPending.StoredFileId = issuedArtifact.StoredFileId;
        issuedPending.UpdatedAtUtc = command.ExecutedAtUtc;
        executedPending.State = PendingFileUploadState.Finalized;
        executedPending.StoredFileId = executedArtifact.StoredFileId;
        executedPending.UpdatedAtUtc = command.ExecutedAtUtc;
        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType = nameof(LeaseAgreement),
                entityId = command.AgreementId,
                operation = "update",
                data = new { },
            }),
            IdempotencyKey =
                $"demo-legal/{command.PortfolioId}/{command.AgreementId}/finalized/v1",
            CreatedAtUtc = command.ExecutedAtUtc,
            NextAttemptAtUtc = command.ExecutedAtUtc,
        });
        await attempt.FlushBusinessAsync(ct);
        return new FinalizeDemoLegalDocumentResult(
            command.PortfolioId,
            command.AgreementId,
            issuedArtifact.Id,
            executedArtifact.Id,
            alreadyFinalized);
    }

    private static async Task<IReadOnlyDictionary<Guid, PendingFileUpload>> LockPendingUploadsAsync(
        RentalCommandDbContext db,
        Guid[] ids,
        CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
        {
            return await db.PendingFileUploads
                .FromSqlInterpolated($$"""
                    SELECT upload.*
                    FROM "PendingFileUploads" AS upload
                    WHERE upload."Id" = ANY ({{ids}})
                    ORDER BY upload."Id"
                    FOR UPDATE
                    """)
                .AsTracking()
                .ToDictionaryAsync(upload => upload.Id, ct);
        }

        return await db.PendingFileUploads
            .Where(upload => ids.Contains(upload.Id))
            .OrderBy(upload => upload.Id)
            .ToDictionaryAsync(upload => upload.Id, ct);
    }

    private static async Task<LegalDocumentArtifact> GetOrCreateArtifactAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext attempt,
        FinalizeDemoLegalDocumentCommand command,
        PendingFileUpload pending,
        IReadOnlyDictionary<string, LegalDocumentArtifact> existingArtifactsByStorageKey,
        LegalDocumentArtifactKind kind,
        string fileName,
        long length,
        string hash,
        string? issuanceFingerprint,
        CancellationToken ct)
    {
        if (existingArtifactsByStorageKey.TryGetValue(pending.StoragePath, out var existing))
        {
            if (existing.ArtifactKind != kind || existing.ContentSha256 != hash
                || existing.ByteLength != length || existing.FileName != fileName)
            {
                throw new InvalidOperationException("The deterministic demo artifact identity conflicts with existing content.");
            }
            return existing;
        }

        var storedFile = new StoredFile
        {
            PortfolioId = command.PortfolioId,
            FileName = fileName,
            FilePath = pending.StoragePath,
            ContentType = "application/pdf",
            FileSize = length,
            EntityType = nameof(LeaseAgreement),
            EntityId = command.AgreementId,
            UploadedAt = kind == LegalDocumentArtifactKind.IssuedAgreement
                ? command.IssuedAtUtc
                : command.ExecutedAtUtc,
        };
        db.StoredFiles.Add(storedFile);
        await attempt.FlushBusinessAsync(ct);

        var artifact = new LegalDocumentArtifact
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = command.PortfolioId,
            StoredFileId = storedFile.Id,
            ArtifactKind = kind,
            StorageKey = pending.StoragePath,
            FileName = fileName,
            ContentType = "application/pdf",
            ByteLength = length,
            ContentSha256 = hash,
            LegalIssuanceFingerprint = issuanceFingerprint,
            CreatedAtUtc = kind == LegalDocumentArtifactKind.IssuedAgreement
                ? command.IssuedAtUtc
                : command.ExecutedAtUtc,
            CreatedByUserId = command.ActorUserId,
        };
        db.LegalDocumentArtifacts.Add(artifact);
        await attempt.FlushBusinessAsync(ct);
        return artifact;
    }

    private static void ValidatePending(
        PendingFileUpload pending,
        string storagePath,
        string requestFingerprint,
        long expectedLength)
    {
        if (pending.State == PendingFileUploadState.Abandoned
            || pending.StoragePath != storagePath
            || pending.RequestFingerprint != requestFingerprint
            || pending.SizeBytes != expectedLength)
        {
            throw new InvalidOperationException("The deterministic demo legal-document admission changed before finalization.");
        }
    }

    private void EnsureProviderIoIsOutsideAtomicAttempt()
    {
        if (_atomicContext.IsActive)
        {
            throw new AtomicArchitectureException(
                "Demo legal-document rendering and storage I/O cannot run inside a database transaction.");
        }
    }

    private void EnsurePdf(byte[] bytes, string artifactName)
    {
        if (bytes.Length < 4 || bytes[0] != (byte)'%' || bytes[1] != (byte)'P'
            || bytes[2] != (byte)'D' || bytes[3] != (byte)'F')
        {
            throw new InvalidOperationException($"The {artifactName} demo legal document is not a non-empty PDF.");
        }
    }

    private string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record PreparedDemoLegalDocument(
        CanonicalDemoLegalDocumentIntent Intent,
        PendingFileUploadAdmission IssuedAdmission,
        string IssuedFileName,
        long IssuedLength,
        string IssuedHash,
        string IssuanceFingerprint,
        PendingFileUploadAdmission ExecutedAdmission,
        string ExecutedFileName,
        long ExecutedLength,
        string ExecutedHash);
}

public sealed class DemoSeedCommandHandler
    : IAtomicCommandHandler<SeedDemoPortfolioCommand, SeedDemoPortfolioResult>
{
    private readonly RentalCommandDbContext _db;

    public DemoSeedCommandHandler(RentalCommandDbContext db) => _db = db;

    internal static readonly AtomicJsonResultCodec<SeedDemoPortfolioResult> ResultCodec =
        new("demo-portfolio-seed-result:v1");

    public async Task<SeedDemoPortfolioResult> HandleAsync(
        SeedDemoPortfolioCommand command,
        IAtomicCommandContext attempt,
        CancellationToken ct)
    {
        Validate(command);
        await AuthorizeAsync(command, _db, ct);
        var result = await DemoDataSeeder.SeedPortfolioCoreAsync(_db, command, attempt, ct);
        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(Portfolio),
            command.PortfolioId,
            AuditLogOperation.Updated,
            ChangeReason: result.AlreadyPresent
                ? "Canonical demo facts reconciled."
                : "Rich demo portfolio graph seeded."));
        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType = nameof(Portfolio),
                entityId = command.PortfolioId,
                operation = "update",
                data = new { },
            }),
            IdempotencyKey = $"demo-seed/{command.PortfolioId}/{command.OperationKey}",
            CreatedAtUtc = command.BusinessNowUtc,
            NextAttemptAtUtc = command.BusinessNowUtc,
        });
        return result;
    }

    public Task AuthorizeReplayAsync(
        SeedDemoPortfolioCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        return AuthorizeAsync(command, _db, ct);
    }

    private async Task AuthorizeAsync(
        SeedDemoPortfolioCommand command,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        if (!db.Database.IsNpgsql())
        {
            var authorizedForProviderTests = await db.Set<Portfolio>()
                .AsNoTracking()
                .AnyAsync(portfolio =>
                    portfolio.Id == command.PortfolioId
                    && db.Set<WorkspaceMembership>().Any(membership =>
                        membership.PortfolioId == portfolio.Id
                        && membership.Status == WorkspaceMembershipStatus.Active
                        && membership.SuspendedAtUtc == null
                        && membership.RevokedAtUtc == null), ct);
            if (!authorizedForProviderTests)
            {
                throw new UnauthorizedAccessException(
                    "The portfolio has no active administering membership for demo seeding.");
            }
            return;
        }

        var authorized = await db.QuerySqlAsync<int>($$"""
            SELECT 1 AS "Value"
            FROM "Portfolios" AS portfolio
            WHERE portfolio."Id" = {{command.PortfolioId}}
              AND (
                    NOT {{command.RequirePendingSandboxOnboarding}}
                    OR COALESCE(portfolio."Settings"::jsonb #>> '{onboarding,choice}', 'pending')
                        = 'pending')
              AND EXISTS (
                    SELECT 1
                    FROM "WorkspaceAccessContexts" AS context
                    JOIN "WorkspaceMemberships" AS membership
                      ON membership."AccessContextId" = context."Id"
                     AND membership."PortfolioId" = context."PortfolioId"
                    WHERE context."PortfolioId" = portfolio."Id"
                      AND context."Status" = 'Active'
                      AND context."SuspendedAtUtc" IS NULL
                      AND context."RevokedAtUtc" IS NULL
                      AND membership."Status" = 'Active'
                      AND membership."SuspendedAtUtc" IS NULL
                      AND membership."RevokedAtUtc" IS NULL)
            LIMIT 1
            """,
            ct);
        if (authorized.Count == 0)
        {
            throw new UnauthorizedAccessException(
                "The portfolio has no active administering membership for demo seeding.");
        }
    }

    private void Validate(SeedDemoPortfolioCommand command)
    {
        if (command.PortfolioId <= 0
            || command.BusinessNowUtc.Kind != DateTimeKind.Utc
            || string.IsNullOrWhiteSpace(command.OperationKey)
            || command.OperationKey.Length > 128
            || !string.Equals(command.OperationKey, command.OperationKey.Trim(), StringComparison.Ordinal))
        {
            throw new DomainValidationException("A valid demo portfolio seed command is required.");
        }
    }
}

public sealed class DemoLegalDocumentFinalizeCommandHandler
    : IAtomicCommandHandler<FinalizeDemoLegalDocumentCommand, FinalizeDemoLegalDocumentResult>
{
    private readonly RentalCommandDbContext _db;

    public DemoLegalDocumentFinalizeCommandHandler(RentalCommandDbContext db) => _db = db;

    internal static readonly AtomicJsonResultCodec<FinalizeDemoLegalDocumentResult> ResultCodec =
        new("demo-legal-document-finalize-result:v1");

    public async Task<FinalizeDemoLegalDocumentResult> HandleAsync(
        FinalizeDemoLegalDocumentCommand command,
        IAtomicCommandContext attempt,
        CancellationToken ct)
    {
        Validate(command);
        await AuthorizeAsync(command, _db, ct);
        await attempt.AcquireLockAsync("LeaseAgreement", command.AgreementId, ct);
        return await DemoDataSeeder.FinalizeLegalDocumentAsync(_db, command, attempt, ct);
    }

    public Task AuthorizeReplayAsync(
        FinalizeDemoLegalDocumentCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        return AuthorizeAsync(command, _db, ct);
    }

    private async Task AuthorizeAsync(
        FinalizeDemoLegalDocumentCommand command,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        var authorized = await db.Set<LeaseAgreement>()
            .AsNoTracking()
            .AnyAsync(agreement =>
                agreement.Id == command.AgreementId
                && agreement.PortfolioId == command.PortfolioId
                && db.Set<WorkspaceMembership>().Any(membership =>
                    membership.PortfolioId == command.PortfolioId
                    && membership.AccessContext!.UserId == command.ActorUserId
                    && membership.AccessContext.Status == WorkspaceAccessContextStatus.Active
                    && membership.AccessContext.SuspendedAtUtc == null
                    && membership.AccessContext.RevokedAtUtc == null
                    && membership.Status == WorkspaceMembershipStatus.Active
                    && membership.SuspendedAtUtc == null
                    && membership.RevokedAtUtc == null), ct);
        if (!authorized)
        {
            throw new UnauthorizedAccessException(
                "The demo legal document cannot be finalized in this portfolio.");
        }
    }

    private void Validate(FinalizeDemoLegalDocumentCommand command)
    {
        if (command.PortfolioId <= 0
            || command.ActorUserId <= 0
            || command.AgreementId <= 0
            || command.IssuedPendingUploadId == Guid.Empty
            || command.ExecutedPendingUploadId == Guid.Empty
            || command.IssuedLength <= 0
            || command.ExecutedLength <= 0
            || command.IssuedAtUtc.Kind != DateTimeKind.Utc
            || command.ExecutedAtUtc.Kind != DateTimeKind.Utc
            || string.IsNullOrWhiteSpace(command.IssuedStoragePath)
            || string.IsNullOrWhiteSpace(command.ExecutedStoragePath)
            || string.IsNullOrWhiteSpace(command.IssuedHash)
            || string.IsNullOrWhiteSpace(command.ExecutedHash)
            || string.IsNullOrWhiteSpace(command.IssuanceFingerprint))
        {
            throw new DomainValidationException(
                "A complete immutable demo legal-document finalization command is required.");
        }
    }
}
