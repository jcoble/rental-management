using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Auth;

/// <summary>
/// Idempotent demo-data seeder. Creates a rich interlinked dataset (properties, units, tenants,
/// leases, 12 months of payments, expenses, work orders, appointments, inspections, and canonical
/// tenant/deposit ledger facts) for a given portfolio so every report and analytics screen has realistic data.
///
/// Two callers: startup (seeds portfolio 1 = the dev admin when <c>Seed:DemoData=true</c>), and new
/// signups (each new portfolio is seeded as a Sandbox to explore). Every row is parameterized on the
/// passed portfolio id; nothing is hardcoded to portfolio 1.
/// </summary>
public class DemoDataSeeder
{
    private readonly RentalCommandDbContext _db;
    private readonly ILogger<DemoDataSeeder> _logger;
    private readonly TimeProvider _timeProvider;

    public DemoDataSeeder(RentalCommandDbContext db, ILogger<DemoDataSeeder> logger, TimeProvider timeProvider)
    {
        _db = db;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <summary>Startup convenience: seeds the dev-admin portfolio (id 1).</summary>
    public Task SeedAsync(CancellationToken ct = default) => SeedPortfolioAsync(1, ct);

    /// <summary>
    /// Seeds the full demo dataset for an arbitrary portfolio. Idempotent: a no-op if the portfolio
    /// already has any properties. Atomic: a failure mid-way rolls the whole thing back, so a partial
    /// dataset can never strand the idempotency guard (which checks for any property).
    /// </summary>
    public async Task SeedPortfolioAsync(int portfolioId, CancellationToken ct = default)
    {
        // Idempotency guard — if any properties exist for this portfolio we are already seeded.
        if (await _db.Properties.AnyAsync(p => p.PortfolioId == portfolioId, ct))
        {
            _logger.LogDebug("Demo data already present for portfolio {PortfolioId}; skipping.", portfolioId);
            return;
        }

        var now = _timeProvider.UtcNow();

        var seedContext = await _db.Portfolios
            .Where(portfolio => portfolio.Id == portfolioId)
            .Select(portfolio => new
            {
                Currency = portfolio.Currency,
                ActorUserId = _db.Users
                    .Where(user => user.PortfolioId == portfolioId && user.TenantId == null)
                    .OrderBy(user => user.Id)
                    .Select(user => (int?)user.Id)
                    .FirstOrDefault(),
            })
            .SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Portfolio {portfolioId} does not exist.");
        var actorUserId = seedContext.ActorUserId
            ?? throw new InvalidOperationException($"Portfolio {portfolioId} has no administering user for demo facts.");
        var currency = seedContext.Currency.Trim().ToUpperInvariant();

        // Seed atomically: if any step fails the whole thing rolls back, so a partial
        // dataset can never strand the idempotency guard (which checks for any property).
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // ── 1. OwnerEntities ──────────────────────────────────────────────────────────
        var ownerEntities = new List<OwnerEntity>
        {
            new()
            {
                PortfolioId = portfolioId,
                OwnerEntityType = OwnerEntityType.LLC,
                Name = "Maple Ridge Properties LLC",
                TaxId = "82-3456789",
                AddressLine1 = "1200 Commerce Dr",
                City = "Columbus",
                State = "OH",
                PostalCode = "43215",
                Address = AddressComposer.Compose("1200 Commerce Dr", null, "Columbus", "OH", "43215"),
                Phone = "614-555-0100",
                CreatedAt = now,
                UpdatedAt = now
            },
            new()
            {
                PortfolioId = portfolioId,
                OwnerEntityType = OwnerEntityType.Person,
                Name = "Robert J. Caldwell",
                TaxId = "XXX-XX-7890",
                AddressLine1 = "88 Westview Ct",
                City = "Gahanna",
                State = "OH",
                PostalCode = "43230",
                Address = AddressComposer.Compose("88 Westview Ct", null, "Gahanna", "OH", "43230"),
                Phone = "614-555-0101",
                CreatedAt = now,
                UpdatedAt = now
            }
        };
        _db.OwnerEntities.AddRange(ownerEntities);
        await _db.SaveChangesAsync(ct);

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
        _db.Vendors.AddRange(vendors);
        await _db.SaveChangesAsync(ct);

        // Local aliases for readability
        var vPlumber     = vendors[0];
        var vElectric    = vendors[1];
        var vHvac        = vendors[2];
        var vLandscape   = vendors[3];
        var vHandyman    = vendors[4];
        var vRoofer      = vendors[5];

        var ownerLlc     = ownerEntities[0];
        var ownerPerson  = ownerEntities[1];

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

        foreach (var pd in propertyDefs)
        {
            var prop = new Property
            {
                PortfolioId       = portfolioId,
                OwnerEntityId     = pd.ownerIdx == 0 ? ownerLlc.Id : ownerPerson.Id,
                Name              = pd.name,
                AddressLine1      = pd.addr,
                City              = pd.city,
                State             = pd.state,
                PostalCode        = pd.zip,
                PropertyType      = pd.ptype,
                Status            = PropertyStatus.Active,
                YearBuilt         = pd.yearBuilt,
                ManagementFeePercent = 8m,
                CreatedAt         = now,
                UpdatedAt         = now
            };
            properties.Add(prop);
        }

        _db.Properties.AddRange(properties);
        await _db.SaveChangesAsync(ct);   // get IDs

        // Now add units
        for (int pi = 0; pi < propertyDefs.Length; pi++)
        {
            var pd = propertyDefs[pi];
            var prop = properties[pi];
            foreach (var ud in pd.units)
            {
                var unit = new Unit
                {
                    PropertyId   = prop.Id,
                    UnitNumber   = ud.num,
                    Bedrooms     = ud.bed,
                    Bathrooms    = ud.bath,
                    SquareFeet   = ud.sqft,
                    MarketRent   = ud.rent,
                    Status       = ud.vacant ? UnitStatus.Vacant : UnitStatus.Occupied,
                    CreatedAt    = now,
                    UpdatedAt    = now
                };
                unitsList.Add(unit);
            }
        }

        _db.Units.AddRange(unitsList);
        await _db.SaveChangesAsync(ct);   // get unit IDs

        // ── 4. Tenants ────────────────────────────────────────────────────────────────
        // 22 tenants; first 19 will be on active leases, last 3 on expired leases.
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

        _db.Tenants.AddRange(tenants);
        await _db.SaveChangesAsync(ct);

        // ── 5. Leases ─────────────────────────────────────────────────────────────────
        // Collect occupied units (non-vacant) in order. We have 19 occupied units to match
        // 19 active-lease tenants.
        var occupiedUnits = unitsList.Where(u => u.Status == UnitStatus.Occupied).ToList();

        // Rent amounts per occupied unit index (deterministic, varied $900-$2500)
        static decimal RentForUnit(Unit u) => u.MarketRent; // use the market rent we already set

        // Active lease start dates: stagger over last 18 months so payment histories vary
        static DateTime ActiveStart(int idx, DateTime @base) =>
            @base.AddMonths(-(18 - idx % 12)).Date;

        var leases = new List<Lease>();

        for (int i = 0; i < occupiedUnits.Count && i < 19; i++)
        {
            var unit   = occupiedUnits[i];
            var prop   = properties.First(p => p.Id == unit.PropertyId);
            var tenant = tenants[i];
            var rent   = RentForUnit(unit);
            var deposit = rent; // 1 month deposit
            var start  = ActiveStart(i, now);
            var end    = start.AddMonths(12);

            var lease = new Lease
            {
                PortfolioId     = portfolioId,
                PropertyId      = prop.Id,
                UnitId          = unit.Id,
                TenantId        = tenant.Id,
                LeaseNumber     = $"L{2024 + i / 12:D4}-{i + 1:D3}",
                Status          = LeaseStatus.Active,
                StartDate       = start,
                EndDate         = end,
                MoveInDate      = start,
                MonthlyRent     = rent,
                SecurityDeposit = deposit,
                LateFeeAmount   = Math.Round(rent * 0.05m, 2),
                RentDueDay      = 1,
                CreatedAt       = start.AddDays(-14),
                UpdatedAt       = now
            };

            // A couple of active leases simulate a lease document captured via the scan→draft→confirm
            // flow — populate the ExtractedData JSONB superset so demo data exercises the scan
            // persistence schema for the Lease record type too.
            if (i < 2)
            {
                lease.ExtractedData = JsonSerializer.Serialize(new
                {
                    documentKind    = "Lease",
                    tenantName      = $"{tenant.FirstName} {tenant.LastName}",
                    monthlyRent     = rent,
                    securityDeposit = deposit,
                    startDate       = start.ToString("yyyy-MM-dd"),
                    endDate         = end.ToString("yyyy-MM-dd"),
                    termMonths      = 12,
                    source          = "demo-seed",
                });
            }

            leases.Add(lease);
        }

        // 3 expired leases for the last 3 tenants on vacant units that were previously occupied.
        // Use the first 3 vacant units.
        var vacantUnits = unitsList.Where(u => u.Status == UnitStatus.Vacant).ToList();
        for (int i = 0; i < 3 && i < vacantUnits.Count; i++)
        {
            var unit   = vacantUnits[i];
            var prop   = properties.First(p => p.Id == unit.PropertyId);
            var tenant = tenants[19 + i]; // tenants 19-21
            var rent   = unit.MarketRent;
            var expiredEnd  = now.AddMonths(-(3 + i));
            var expiredStart = expiredEnd.AddMonths(-12);

            var expiredLease = new Lease
            {
                PortfolioId     = portfolioId,
                PropertyId      = prop.Id,
                UnitId          = unit.Id,
                TenantId        = tenant.Id,
                LeaseNumber     = $"L{2023}-EXP-{i + 1:D3}",
                Status          = LeaseStatus.Expired,
                StartDate       = expiredStart,
                EndDate         = expiredEnd,
                MoveInDate      = expiredStart,
                MoveOutDate     = expiredEnd,
                MonthlyRent     = rent,
                SecurityDeposit = rent,
                LateFeeAmount   = Math.Round(rent * 0.05m, 2),
                RentDueDay      = 1,
                CreatedAt       = expiredStart.AddDays(-14),
                UpdatedAt       = now
            };
            leases.Add(expiredLease);
        }

        _db.Leases.AddRange(leases);
        await _db.SaveChangesAsync(ct);

        // ── 6. Payments ───────────────────────────────────────────────────────────────
        // For each active lease: ~12 months of Rent history + 1 upcoming Scheduled,
        // a few Late, one SecurityDeposit, and one LateFee per lease (some).
        // For expired leases: 12 months of Rent history (all Paid).
        var payments = new List<Payment>();
        var activeLeasesForPayments = leases.Where(l => l.Status == LeaseStatus.Active).ToList();

        // Deterministic payment pattern: leases at index % 5 == 2 get one Late month,
        // index % 7 == 4 get two late months.
        for (int li = 0; li < activeLeasesForPayments.Count; li++)
        {
            var lease     = activeLeasesForPayments[li];
            var leaseStart = lease.StartDate;

            // Security deposit — paid at move-in
            var depositPayment = new Payment
            {
                PortfolioId  = portfolioId,
                LeaseId      = lease.Id,
                PaymentType  = PaymentType.SecurityDeposit,
                Status       = PaymentStatus.Paid,
                Amount       = lease.SecurityDeposit,
                DueDate      = leaseStart,
                PaidDate     = leaseStart,
                Method       = "Check",
                PeriodKey    = null, // one-off / manual
                CreatedAt    = leaseStart,
                UpdatedAt    = leaseStart
            };

            // The first few deposit checks simulate a paper check captured via the scan→draft→confirm
            // flow — populate the check-specific typed columns (PayerName/CheckNumber/BankName) plus the
            // ExtractedData JSONB superset, so demo data exercises the scan schema for the Payment type.
            if (li < 3)
            {
                var depTenant = tenants[li];
                depositPayment.PayerName   = $"{depTenant.FirstName} {depTenant.LastName}";
                depositPayment.CheckNumber = (1040 + li * 7).ToString();
                depositPayment.BankName    = (li % 3) switch
                {
                    0 => "Huntington National Bank",
                    1 => "Chase Bank",
                    _ => "PNC Bank",
                };
                depositPayment.ExtractedData = JsonSerializer.Serialize(new
                {
                    documentKind = "Check",
                    payerName    = depositPayment.PayerName,
                    checkNumber  = depositPayment.CheckNumber,
                    bankName     = depositPayment.BankName,
                    amount       = depositPayment.Amount,
                    memo         = "Security deposit",
                    source       = "demo-seed",
                });
            }

            payments.Add(depositPayment);

            // Generate rent rows for each month from lease start through the current month.
            // "now" is at start of the month for DueDate purposes.
            var currentMonthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var monthCursor       = new DateTime(leaseStart.Year, leaseStart.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            int monthsGenerated   = 0;

            while (monthCursor <= currentMonthStart && monthsGenerated < 13)
            {
                var periodKey  = monthCursor.ToString("yyyy-MM");
                var dueDate    = new DateTime(monthCursor.Year, monthCursor.Month, lease.RentDueDay, 0, 0, 0, DateTimeKind.Utc);
                bool isUpcoming = monthCursor == currentMonthStart;

                PaymentStatus status;
                DateTime?     paidDate;

                if (isUpcoming)
                {
                    status   = PaymentStatus.Scheduled;
                    paidDate = null;
                }
                else if (li % 5 == 2 && monthsGenerated == 3)
                {
                    // One late payment
                    status   = PaymentStatus.Late;
                    paidDate = dueDate.AddDays(12);
                }
                else if (li % 7 == 4 && monthsGenerated == 7)
                {
                    // Another late payment
                    status   = PaymentStatus.Late;
                    paidDate = dueDate.AddDays(8);
                }
                else
                {
                    status   = PaymentStatus.Paid;
                    paidDate = dueDate.AddDays(-(li % 3)); // paid 0-2 days early
                }

                payments.Add(new Payment
                {
                    PortfolioId  = portfolioId,
                    LeaseId      = lease.Id,
                    PaymentType  = PaymentType.Rent,
                    Status       = status,
                    Amount       = lease.MonthlyRent,
                    DueDate      = dueDate,
                    PaidDate     = paidDate,
                    Method       = status == PaymentStatus.Scheduled ? null : ((li % 3) switch { 0 => "ACH", 1 => "Check", _ => "Zelle" }),
                    PeriodKey    = periodKey,
                    CreatedAt    = dueDate.AddDays(-1),
                    UpdatedAt    = paidDate ?? dueDate
                });

                monthsGenerated++;
                monthCursor = monthCursor.AddMonths(1);
            }

            // Late fee for leases that had a Late payment
            if (li % 5 == 2 || li % 7 == 4)
            {
                var latePeriod = li % 5 == 2
                    ? new DateTime(leaseStart.Year, leaseStart.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(3).ToString("yyyy-MM")
                    : new DateTime(leaseStart.Year, leaseStart.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(7).ToString("yyyy-MM");

                payments.Add(new Payment
                {
                    PortfolioId  = portfolioId,
                    LeaseId      = lease.Id,
                    PaymentType  = PaymentType.LateFee,
                    Status       = PaymentStatus.Paid,
                    Amount       = lease.LateFeeAmount,
                    DueDate      = DateTime.SpecifyKind(DateTime.ParseExact(latePeriod, "yyyy-MM", null), DateTimeKind.Utc).AddDays(5),
                    PaidDate     = DateTime.SpecifyKind(DateTime.ParseExact(latePeriod, "yyyy-MM", null), DateTimeKind.Utc).AddDays(12),
                    Method       = "Check",
                    PeriodKey    = latePeriod,
                    CreatedAt    = now.AddMonths(-6),
                    UpdatedAt    = now.AddMonths(-6)
                });
            }
        }

        // Expired lease payments — all Paid, monthly for 12 months
        var expiredLeases = leases.Where(l => l.Status == LeaseStatus.Expired).ToList();
        foreach (var lease in expiredLeases)
        {
            var monthCursor = new DateTime(lease.StartDate.Year, lease.StartDate.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var endMonth    = new DateTime(lease.EndDate.Year, lease.EndDate.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            int cnt = 0;

            while (monthCursor <= endMonth && cnt < 12)
            {
                var periodKey = monthCursor.ToString("yyyy-MM");
                var dueDate   = new DateTime(monthCursor.Year, monthCursor.Month, lease.RentDueDay, 0, 0, 0, DateTimeKind.Utc);
                payments.Add(new Payment
                {
                    PortfolioId  = portfolioId,
                    LeaseId      = lease.Id,
                    PaymentType  = PaymentType.Rent,
                    Status       = PaymentStatus.Paid,
                    Amount       = lease.MonthlyRent,
                    DueDate      = dueDate,
                    PaidDate     = dueDate.AddDays(1),
                    Method       = "Check",
                    PeriodKey    = periodKey,
                    CreatedAt    = dueDate.AddDays(-1),
                    UpdatedAt    = dueDate.AddDays(1)
                });
                cnt++;
                monthCursor = monthCursor.AddMonths(1);
            }
        }

        _db.Payments.AddRange(payments);
        await _db.SaveChangesAsync(ct);

        // ── 7. SecurityDepositHoldings ────────────────────────────────────────────────
        var demoLeaseTemplate = new DocumentTemplate
        {
            PortfolioId = portfolioId,
            Kind = DocumentTemplateKind.Lease,
            Status = DocumentTemplateStatus.Active,
            RenderMode = DocumentTemplateRenderMode.Restyle,
            Name = "Demo lease template",
            Description = "Seed-only template backing canonical demo agreements.",
            DraftHtml = "<p>Demo lease agreement</p>",
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        _db.DocumentTemplates.Add(demoLeaseTemplate);

        var securityDepositAccountsSeeded = 0;
        foreach (var lease in activeLeasesForPayments.Where(candidate => candidate.SecurityDeposit > 0m))
        {
            var effectiveOn = DateOnly.FromDateTime(lease.StartDate);
            var management = new LeaseManagement
            {
                PublicId = Guid.NewGuid(), PortfolioId = portfolioId, PropertyId = lease.PropertyId,
                UnitId = lease.UnitId, RelationshipNumber = $"DEMO-LM-{lease.Id:D8}",
                PlannedPossessionAtUtc = lease.MoveInDate ?? lease.StartDate,
                PossessionGivenAtUtc = lease.MoveInDate ?? lease.StartDate,
                PossessionAgreementExceptionReason = "Canonical demo relationship seeded from fixture.",
                PossessionAgreementExceptionAuthorizedByUserId = actorUserId,
                CreatedAtUtc = lease.CreatedAt, CreatedByUserId = actorUserId,
                UpdatedAtUtc = now, RowVersion = Guid.NewGuid(),
            };
            var account = new TenantAccount
            {
                PublicId = Guid.NewGuid(), PortfolioId = portfolioId, LeaseManagement = management,
                AccountNumber = $"DEMO-TA-{lease.Id:D8}", Currency = currency,
                OpenedAtUtc = lease.StartDate, CreatedAtUtc = lease.CreatedAt,
                CreatedByUserId = actorUserId,
            };
            var agreement = new LeaseAgreement
            {
                PublicId = Guid.NewGuid(), PortfolioId = portfolioId, LeaseManagement = management,
                VersionNumber = 1, AgreementNumber = $"DEMO-AGR-{lease.Id:D8}-V1",
                ChangeType = LeaseAgreementChangeType.Initial, TermType = LeaseAgreementTermType.FixedTerm,
                TermStartOn = effectiveOn, TermEndOn = DateOnly.FromDateTime(lease.EndDate),
                GoverningFromOn = effectiveOn, BaseRentAmount = lease.MonthlyRent,
                RentDueDay = checked((short)lease.RentDueDay), SecurityDepositObligation = lease.SecurityDeposit,
                LateFeeAmount = lease.LateFeeAmount, GracePeriodDays = 0, Currency = currency,
                TermsSchemaVersion = 1,
                TermsPayload = JsonSerializer.Serialize(new { source = "demo-seed", legacyLeaseId = lease.Id }),
                DocumentTemplate = demoLeaseTemplate, DocumentTemplateVersion = 1,
                CreatedAtUtc = lease.CreatedAt, CreatedByUserId = actorUserId, UpdatedAtUtc = now,
            };
            var party = new LeaseManagementParty
            {
                PortfolioId = portfolioId, LeaseManagement = management, TenantId = lease.TenantId,
                Role = LeaseManagementPartyRole.PrimaryTenant, EffectiveFrom = effectiveOn,
                ChangeReason = "Canonical demo household seeded from lease fixture.",
                CreatedAtUtc = lease.CreatedAt, CreatedByUserId = actorUserId,
            };
            var depositAccount = new SecurityDepositAccount
            {
                PortfolioId = portfolioId, TenantAccount = account, OriginatingAgreement = agreement,
                Currency = currency, CreatedAtUtc = lease.StartDate, CreatedByUserId = actorUserId,
            };
            var depositCharge = new TenantLedgerEntry
            {
                PublicId = Guid.NewGuid(), PortfolioId = portfolioId, TenantAccount = account,
                EntryType = TenantLedgerEntryType.DepositCharge, Direction = TenantLedgerDirection.Debit,
                Amount = lease.SecurityDeposit, Currency = currency, EffectiveOn = effectiveOn, DueOn = effectiveOn,
                PostedAtUtc = lease.StartDate, Description = "Security deposit due",
                BusinessKey = $"demo:{lease.Id}:deposit-charge", LeaseAgreement = agreement,
                CreatedByUserId = actorUserId,
            };
            var depositReceipt = new TenantLedgerEntry
            {
                PublicId = Guid.NewGuid(), PortfolioId = portfolioId, TenantAccount = account,
                EntryType = TenantLedgerEntryType.PaymentReceipt, Direction = TenantLedgerDirection.Credit,
                Amount = lease.SecurityDeposit, Currency = currency, EffectiveOn = effectiveOn,
                PostedAtUtc = lease.StartDate, Description = "Security deposit received",
                BusinessKey = $"demo:{lease.Id}:deposit-receipt", CreatedByUserId = actorUserId,
            };
            var allocation = new TenantLedgerAllocation
            {
                PortfolioId = portfolioId, TenantAccount = account, DebitEntry = depositCharge,
                CreditEntry = depositReceipt, Amount = lease.SecurityDeposit, AllocatedAtUtc = lease.StartDate,
                BusinessKey = $"demo:{lease.Id}:deposit-allocation", CreatedByUserId = actorUserId,
            };
            var depositEntry = new SecurityDepositEntry
            {
                PublicId = Guid.NewGuid(), PortfolioId = portfolioId, SecurityDepositAccount = depositAccount,
                EntryType = SecurityDepositEntryType.Receipt, Direction = SecurityDepositDirection.Increase,
                Amount = lease.SecurityDeposit, Currency = currency, EffectiveOn = effectiveOn,
                PostedAtUtc = lease.StartDate, BusinessKey = $"demo:{lease.Id}:deposit-fund",
                Description = "Security deposit funded", LeaseAgreement = agreement,
                TenantLedgerEntry = depositReceipt, CreatedByUserId = actorUserId,
            };

            _db.LeaseManagements.Add(management);
            _db.TenantAccounts.Add(account);
            _db.LeaseAgreements.Add(agreement);
            _db.LeaseManagementParties.Add(party);
            _db.SecurityDepositAccounts.Add(depositAccount);
            _db.TenantLedgerEntries.AddRange(depositCharge, depositReceipt);
            _db.TenantLedgerAllocations.Add(allocation);
            _db.SecurityDepositEntries.Add(depositEntry);
            securityDepositAccountsSeeded++;
        }
        await _db.SaveChangesAsync(ct);

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
        // For these we populate the full scan-persistence schema exactly as ScanService does on confirm:
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

        var expenses = expenseDefs.Select((ed, i) =>
        {
            var prop    = properties[ed.propIdx];
            var vendor  = vendors[ed.vendorIdx];
            var incDate = now.AddDays(-ed.daysAgo);
            var expense = new Expense
            {
                PortfolioId    = portfolioId,
                PropertyId     = prop.Id,
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

        _db.Expenses.AddRange(expenses);
        await _db.SaveChangesAsync(ct);

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

            // Pick tenant/lease if specified
            Lease?  lease  = null;
            Tenant? tenant = null;
            if (wd.tenantLeaseIdx.HasValue && wd.tenantLeaseIdx.Value < activeLeasesForPayments.Count)
            {
                lease  = activeLeasesForPayments[wd.tenantLeaseIdx.Value];
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
                LeaseId       = lease?.Id,
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

        _db.WorkOrders.AddRange(woList);
        await _db.SaveChangesAsync(ct);

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
            Lease?   apptLease  = null;
            if (ad.tenantIdx.HasValue && ad.tenantIdx.Value < activeLeasesForPayments.Count)
                apptLease = activeLeasesForPayments[ad.tenantIdx.Value];

            var scheduledStart = now.AddDays(ad.daysOffset).Date.AddHours(10 + i % 4);
            appts.Add(new Appointment
            {
                PortfolioId    = portfolioId,
                PropertyId     = prop.Id,
                UnitId         = propUnits2.Count > 0 ? propUnits2[i % propUnits2.Count].Id : null,
                TenantId       = apptTenant?.Id,
                LeaseId        = apptLease?.Id,
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

        _db.Appointments.AddRange(appts);
        await _db.SaveChangesAsync(ct);

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

            Lease? inspLease = null;
            if (id.tenantIdx.HasValue && id.tenantIdx.Value < activeLeasesForPayments.Count)
                inspLease = activeLeasesForPayments[id.tenantIdx.Value];

            var scheduled = now.AddDays(id.daysOffset).Date.AddHours(9);
            var template  = TemplateForType(id.type);
            var completed = id.status == InspectionStatus.Completed;

            var inspection = new Inspection
            {
                PortfolioId  = portfolioId,
                PropertyId   = prop.Id,
                UnitId       = propUnits3.Count > 0 ? propUnits3[i % propUnits3.Count].Id : null,
                LeaseId      = inspLease?.Id,
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

        _db.Inspections.AddRange(inspections);
        await _db.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);

        // ── Done ──────────────────────────────────────────────────────────────────────
        _logger.LogInformation(
            "Demo data seeded for portfolio {PortfolioId}: " +
            "{OwnerEntities} ownerEntities, {Vendors} vendors, {Properties} properties, " +
            "{Units} units, {Tenants} tenants, {Leases} leases ({Active} active / {Expired} expired), " +
            "{Payments} payments, {Holdings} security deposit holdings, " +
            "{Expenses} expenses, {WorkOrders} work orders, {Appointments} appointments, {Inspections} inspections.",
            portfolioId,
            ownerEntities.Count,
            vendors.Count,
            properties.Count,
            unitsList.Count,
            tenants.Count,
            leases.Count,
            activeLeasesForPayments.Count,
            expiredLeases.Count,
            payments.Count,
            securityDepositAccountsSeeded,
            expenses.Count,
            woList.Count,
            appts.Count,
            inspections.Count);
    }
}
