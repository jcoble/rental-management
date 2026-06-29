# Tenant Notices — Plan 1: Backend Foundation (templates + new types) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add landlord-authored notice templates with deterministic field-fill, plus two new notice types (`RentReminder`, `MonthToMonthConversion`), so that on-demand generation renders a portfolio's template when one exists and falls back to today's copy otherwise.

**Architecture:** A new `NoticeTemplate` entity (one active template per portfolio+type) and per-type auto-send columns on `NotificationSettings`. A pure `NoticeTemplateRenderer` fills `{{token}}` merge fields from a per-draft token dictionary. `NoticeDraftService.GenerateAsync` preloads the portfolio's active templates once and passes the matching template into each draft builder; a builder renders the template when present, else uses the existing LLM/deterministic copy. A `NoticeTemplatesController` exposes portfolio-scoped CRUD.

**Tech Stack:** .NET 10, EF Core + Npgsql (PostgreSQL only), xUnit (`RentalCommand.Api.Tests`).

## Global Constraints

- **PostgreSQL only** (Npgsql). No SQLite/SQL Server. Add migrations with `dotnet ef migrations add <Name> --project RentalCommand.Data --startup-project RentalCommand.Api`.
- **All aggregation/filtering/joins run DB-side** as EF-translated SQL — no in-memory grouping or per-row follow-up queries. Preload the portfolio's templates in ONE query; never query templates per-lease inside a loop.
- **Enums serialize as string names** app-wide (`JsonStringEnumConverter` already registered). Any new enum must work as strings.
- **Portfolio scoping:** every endpoint/query filters by the caller's `portfolioId` claim (cross-tenant IDOR guard), matching `NoticeDraftsController`.
- **No `Co-Authored-By`/AI-attribution trailer** in commit messages. Subject + body only.
- Notice types are free strings on `NoticeDraft.NoticeType` (max length 80). New values this plan adds: `"RentReminder"`, `"MonthToMonthConversion"`.

---

### Task 1: `NoticeTemplate` entity + send-mode columns + DbContext mapping

**Files:**
- Create: `RentalCommand.Core/Entities/NoticeTemplate.cs`
- Modify: `RentalCommand.Core/Entities/NotificationSettings.cs` (add per-type auto-send flags)
- Modify: `RentalCommand.Data/RentalCommandDbContext.cs:50` (add DbSet near other notice sets) and `:545` (add entity config after the `NoticeDraft` block)

**Interfaces:**
- Produces: `NoticeTemplate { int Id; int PortfolioId; string NoticeType; string Subject; string Body; bool IsActive; DateTime CreatedAt; DateTime UpdatedAt; Portfolio? Portfolio; }`
- Produces: `RentalCommandDbContext.NoticeTemplates` (`DbSet<NoticeTemplate>`)
- Produces on `NotificationSettings`: `bool AutoSendRentReminder; bool AutoSendRenewal; bool AutoSendMonthToMonth; bool AutoSendMoveOut; bool AutoSendLateRent;` (all default `false` = "ask me first"). Plan 3 maps these to the `AskFirst|AutoSend` UI.

- [ ] **Step 1: Create the entity**

```csharp
// RentalCommand.Core/Entities/NoticeTemplate.cs
namespace RentalCommand.Core.Entities;

/// <summary>
/// A landlord-authored, reusable notice template for one notice type within a portfolio. The
/// <see cref="Subject"/> and <see cref="Body"/> hold <c>{{merge_token}}</c> fields that are filled
/// deterministically at generation time. At most one row per (PortfolioId, NoticeType) has
/// <see cref="IsActive"/> = true; that is the template used for generation.
/// </summary>
public class NoticeTemplate
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }

    /// <summary>One of the NoticeDraft type names: RentReminder, RenewalOffer, MonthToMonthConversion, MoveOutReminder, LateRentNotice.</summary>
    public string NoticeType { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Portfolio? Portfolio { get; set; }
}
```

- [ ] **Step 2: Add send-mode columns to `NotificationSettings`**

In `RentalCommand.Core/Entities/NotificationSettings.cs`, add these properties immediately after `public bool EnableDailyBriefingMessages { get; set; }`:

```csharp
    // Per-notice-type tenant-facing send mode. false (default) = "ask me first" (draft for approval);
    // true = auto-send once an active NoticeTemplate exists for the type (consumed by Plan 3's worker).
    public bool AutoSendRentReminder { get; set; }
    public bool AutoSendRenewal { get; set; }
    public bool AutoSendMonthToMonth { get; set; }
    public bool AutoSendMoveOut { get; set; }
    public bool AutoSendLateRent { get; set; }
```

- [ ] **Step 3: Register the DbSet**

In `RentalCommand.Data/RentalCommandDbContext.cs`, after line 50 (`public DbSet<NoticeDraft> NoticeDrafts => Set<NoticeDraft>();`) add:

```csharp
    public DbSet<NoticeTemplate> NoticeTemplates => Set<NoticeTemplate>();
```

- [ ] **Step 4: Configure the entity**

In `RentalCommand.Data/RentalCommandDbContext.cs`, immediately after the closing `});` of the `modelBuilder.Entity<NoticeDraft>(...)` block (ends at line 545), add:

```csharp
        modelBuilder.Entity<NoticeTemplate>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.NoticeType).IsRequired().HasMaxLength(80);
            entity.Property(e => e.Subject).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Body).IsRequired().HasMaxLength(4000);
            entity.HasIndex(e => e.PortfolioId);
            // One active template per (portfolio, type) — enforced by a filtered unique index.
            entity.HasIndex(e => new { e.PortfolioId, e.NoticeType })
                .HasFilter("\"IsActive\" = true")
                .IsUnique();
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
        });
```

- [ ] **Step 5: Build to verify it compiles**

Run: `dotnet build RentalCommand.Data/RentalCommand.Data.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add RentalCommand.Core/Entities/NoticeTemplate.cs RentalCommand.Core/Entities/NotificationSettings.cs RentalCommand.Data/RentalCommandDbContext.cs
git commit -m "Add NoticeTemplate entity and per-type auto-send columns"
```

---

### Task 2: EF migration

**Files:**
- Create: `RentalCommand.Data/Migrations/*_AddNoticeTemplatesAndSendMode.cs` (generated)

**Interfaces:**
- Consumes: the model changes from Task 1.
- Produces: a migration creating table `NoticeTemplates` and the five `AutoSend*` boolean columns on `NotificationSettings`.

- [ ] **Step 1: Generate the migration**

Run:
```bash
dotnet ef migrations add AddNoticeTemplatesAndSendMode --project RentalCommand.Data --startup-project RentalCommand.Api
```
Expected: a new migration file under `RentalCommand.Data/Migrations/`.

- [ ] **Step 2: Inspect the generated migration**

Open the generated `*_AddNoticeTemplatesAndSendMode.cs` and confirm:
- `CreateTable("NoticeTemplates", ...)` with columns `Id, PortfolioId, NoticeType, Subject, Body, IsActive, CreatedAt, UpdatedAt`.
- A unique filtered index on `(PortfolioId, NoticeType)` with filter `"IsActive" = true`.
- `AddColumn<bool>` for each of `AutoSendRentReminder, AutoSendRenewal, AutoSendMonthToMonth, AutoSendMoveOut, AutoSendLateRent` on `NotificationSettings` with `defaultValue: false`.

If the boolean columns are missing a default, edit them to include `defaultValue: false`.

- [ ] **Step 3: Build the migration**

Run: `dotnet build RentalCommand.Data/RentalCommand.Data.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add RentalCommand.Data/Migrations/
git commit -m "Add migration for notice templates and send-mode columns"
```

---

### Task 3: Merge-token catalog + `NoticeTemplateRenderer`

**Files:**
- Create: `RentalCommand.Api/Services/Domain/NoticeMergeFields.cs`
- Create: `RentalCommand.Api/Services/Domain/NoticeTemplateRenderer.cs`
- Test: `RentalCommand.Api.Tests/Notices/NoticeTemplateRendererTests.cs`

**Interfaces:**
- Produces: `static class NoticeMergeFields` with token name constants and `IReadOnlyList<string> ForType(string noticeType)` returning the available token names for a type.
- Produces: `static class NoticeTemplateRenderer` with `(string Subject, string Body) Render(string subjectTemplate, string bodyTemplate, IReadOnlyDictionary<string,string> tokens)`. Replaces every `{{token}}` with its value; unknown or unmapped tokens render to empty string. Token matching is case-insensitive on the inner name and tolerates surrounding spaces (`{{ tenant_name }}`).

- [ ] **Step 1: Write the failing test**

```csharp
// RentalCommand.Api.Tests/Notices/NoticeTemplateRendererTests.cs
using RentalCommand.Api.Services.Domain;
using Xunit;

namespace RentalCommand.Api.Tests.Notices;

public class NoticeTemplateRendererTests
{
    private static readonly Dictionary<string, string> Tokens = new()
    {
        ["tenant_name"] = "Jordan Lee",
        ["property_address"] = "12 Oak St",
        ["rent_amount"] = "$1,200",
    };

    [Fact]
    public void Render_replaces_known_tokens()
    {
        var (subject, body) = NoticeTemplateRenderer.Render(
            "Rent for {{tenant_name}}",
            "Hi {{tenant_name}}, rent of {{rent_amount}} for {{property_address}}.",
            Tokens);

        Assert.Equal("Rent for Jordan Lee", subject);
        Assert.Equal("Hi Jordan Lee, rent of $1,200 for 12 Oak St.", body);
    }

    [Fact]
    public void Render_blanks_unknown_or_unmapped_tokens()
    {
        var (subject, body) = NoticeTemplateRenderer.Render(
            "{{unknown_token}}!",
            "Hello {{tenant_name}}{{missing}}",
            Tokens);

        Assert.Equal("!", subject);
        Assert.Equal("Hello Jordan Lee", body);
    }

    [Fact]
    public void Render_tolerates_spaces_and_case_in_token()
    {
        var (_, body) = NoticeTemplateRenderer.Render(
            "x",
            "{{ Tenant_Name }}",
            Tokens);

        Assert.Equal("Jordan Lee", body);
    }

    [Fact]
    public void ForType_returns_late_rent_fields()
    {
        var fields = NoticeMergeFields.ForType("LateRentNotice");
        Assert.Contains("overdue_amount", fields);
        Assert.Contains("tenant_name", fields);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter NoticeTemplateRendererTests`
Expected: FAIL to compile / "type or namespace NoticeTemplateRenderer/NoticeMergeFields not found".

- [ ] **Step 3: Implement the token catalog**

```csharp
// RentalCommand.Api/Services/Domain/NoticeMergeFields.cs
namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// The fixed catalog of merge tokens available to notice templates, and which apply to each type.
/// The editor shows these so a landlord only inserts tokens that will actually fill.
/// </summary>
public static class NoticeMergeFields
{
    public const string TenantName = "tenant_name";
    public const string PropertyAddress = "property_address";
    public const string UnitNumber = "unit_number";
    public const string LeaseStartDate = "lease_start_date";
    public const string LeaseEndDate = "lease_end_date";
    public const string RentAmount = "rent_amount";
    public const string RentDueDate = "rent_due_date";
    public const string OverdueAmount = "overdue_amount";
    public const string LateFeeAmount = "late_fee_amount";
    public const string LandlordName = "landlord_name";
    public const string PortfolioName = "portfolio_name";

    private static readonly string[] Common =
        [TenantName, PropertyAddress, UnitNumber, LandlordName, PortfolioName];

    public static IReadOnlyList<string> ForType(string noticeType) => noticeType switch
    {
        "RentReminder" => [.. Common, RentAmount, RentDueDate],
        "RenewalOffer" => [.. Common, LeaseStartDate, LeaseEndDate, RentAmount],
        "MonthToMonthConversion" => [.. Common, LeaseEndDate, RentAmount],
        "MoveOutReminder" => [.. Common, LeaseEndDate],
        "LateRentNotice" => [.. Common, OverdueAmount, RentDueDate, LateFeeAmount],
        _ => Common,
    };

    public static IReadOnlyList<string> All =>
        [TenantName, PropertyAddress, UnitNumber, LeaseStartDate, LeaseEndDate,
         RentAmount, RentDueDate, OverdueAmount, LateFeeAmount, LandlordName, PortfolioName];
}
```

- [ ] **Step 4: Implement the renderer**

```csharp
// RentalCommand.Api/Services/Domain/NoticeTemplateRenderer.cs
using System.Text.RegularExpressions;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Pure, deterministic merge-token fill for notice templates. Replaces every <c>{{token}}</c> with the
/// matching value from <paramref name="tokens"/> (case-insensitive, space-tolerant). Unknown or unmapped
/// tokens render to an empty string — never throws, so a template can never break generation.
/// </summary>
public static partial class NoticeTemplateRenderer
{
    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}")]
    private static partial Regex TokenRegex();

    public static (string Subject, string Body) Render(
        string subjectTemplate,
        string bodyTemplate,
        IReadOnlyDictionary<string, string> tokens)
    {
        return (Fill(subjectTemplate, tokens), Fill(bodyTemplate, tokens));
    }

    private static string Fill(string template, IReadOnlyDictionary<string, string> tokens)
    {
        if (string.IsNullOrEmpty(template)) return template ?? string.Empty;
        return TokenRegex().Replace(template, m =>
        {
            var key = m.Groups[1].Value.ToLowerInvariant();
            return tokens.TryGetValue(key, out var value) ? value : string.Empty;
        });
    }
}
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter NoticeTemplateRendererTests`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add RentalCommand.Api/Services/Domain/NoticeMergeFields.cs RentalCommand.Api/Services/Domain/NoticeTemplateRenderer.cs RentalCommand.Api.Tests/Notices/NoticeTemplateRendererTests.cs
git commit -m "Add notice merge-field catalog and deterministic template renderer"
```

---

### Task 4: Template-aware copy in `ComposeAsync` + preload templates in `GenerateAsync`

**Files:**
- Modify: `RentalCommand.Api/Services/Domain/NoticeDraftService.cs` (`GenerateAsync` `:57`, `ComposeAsync` `:455`, the three `Build*DraftAsync` builders, add a token-dictionary helper)
- Test: `RentalCommand.Api.Tests/Notices/NoticeTemplateGenerationTests.cs`

**Interfaces:**
- Consumes: `NoticeTemplateRenderer.Render`, `NoticeMergeFields`, `RentalCommandDbContext.NoticeTemplates`.
- Produces: `ComposeAsync` gains a parameter `NoticeTemplate? template` and a `IReadOnlyDictionary<string,string> tokens`; when `template != null` it renders the template (skipping the LLM) and sets `GenerationPrompt = null`. `GenerateAsync` preloads `Dictionary<string, NoticeTemplate>` of active templates keyed by `NoticeType` once and threads the matching one into each builder.

- [ ] **Step 1: Write the failing test**

This test drives the service against a real (in-memory Npgsql is not used here; use the existing test DB harness). Mirror the existing notice-service test setup if present; otherwise use `RentalCommandDbContext` with the project's test fixture. The assertion: when an active template exists for `RenewalOffer`, the generated draft's subject/body come from the rendered template, not the deterministic copy.

```csharp
// RentalCommand.Api.Tests/Notices/NoticeTemplateGenerationTests.cs
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using Xunit;

namespace RentalCommand.Api.Tests.Notices;

public class NoticeTemplateGenerationTests
{
    [Fact]
    public async Task Generate_renders_active_template_when_present()
    {
        using var db = TestDb.Create(); // existing helper in RentalCommand.Api.Tests; see TestCommon
        var (portfolioId, tenantId) = await TestDb.SeedLeaseEndingSoonAsync(db, tenantName: "Jordan Lee");

        db.NoticeTemplates.Add(new NoticeTemplate
        {
            PortfolioId = portfolioId,
            NoticeType = "RenewalOffer",
            Subject = "Renewal for {{tenant_name}}",
            Body = "Hi {{tenant_name}}, renew {{property_address}}?",
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var service = TestDb.NoticeDraftService(db); // wires no-op ILlmProvider + stub IConversationService
        var result = await service.GenerateAsync(
            portfolioId,
            new GenerateNoticeDraftsRequest { TenantId = tenantId, NoticeType = "RenewalOffer" });

        var draft = Assert.Single(result.Drafts);
        Assert.Equal("Renewal for Jordan Lee", draft.Subject);
        Assert.StartsWith("Hi Jordan Lee, renew", draft.Body);
    }
}
```

NOTE for the implementer: if `TestDb` helpers named above do not exist, add them to `RentalCommand.Api.Tests` mirroring how other `NoticeDraftService` tests construct the context, a no-op `ILlmProvider` (returns empty string), and a stub `IConversationService` whose `StartAsync` returns a `Conversation { Id = 1 }`. Seed: one `Portfolio`, one `Tenant`, one `Property`, one active `Lease` with `EndDate = today.AddDays(10)`.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter NoticeTemplateGenerationTests`
Expected: FAIL — generated subject is the deterministic `"Lease renewal for …"`, not the template.

- [ ] **Step 3: Preload templates in `GenerateAsync`**

In `NoticeDraftService.GenerateAsync`, after the `existingDrafts` HashSet is built (after line 91), add a single-query preload:

```csharp
        // Preload this portfolio's active notice templates once (DB-side), keyed by type, so each
        // builder can render the landlord's template without a per-lease query.
        var activeTemplates = await _db.NoticeTemplates
            .AsNoTracking()
            .Where(t => t.PortfolioId == portfolioId && t.IsActive)
            .ToDictionaryAsync(t => t.NoticeType, ct);

        NoticeTemplate? TemplateFor(string type) =>
            activeTemplates.TryGetValue(type, out var t) ? t : null;
```

- [ ] **Step 4: Thread the template + tokens through the builders**

Change `ComposeAsync`'s signature to accept the template and a token map, and short-circuit to the renderer when a template is present. Replace the `ComposeAsync` method body's copy-selection section. The new signature and the template branch:

```csharp
    private async Task<NoticeDraft> ComposeAsync(
        int portfolioId,
        Lease lease,
        string noticeType,
        string intent,
        string facts,
        string deterministicSubject,
        string deterministicBody,
        string reason,
        DateTime triggerDate,
        DateTime now,
        NoticeTemplate? template,
        IReadOnlyDictionary<string, string> tokens,
        CancellationToken ct)
    {
        var subject = deterministicSubject;
        var body = deterministicBody;
        string? usedPrompt = null;

        if (template != null)
        {
            // Landlord template takes precedence over LLM/deterministic copy. Deterministic fill,
            // no LLM call. GenerationPrompt stays null to mark non-LLM provenance.
            var rendered = NoticeTemplateRenderer.Render(template.Subject, template.Body, tokens);
            subject = Truncate(rendered.Subject, 200);
            body = Truncate(rendered.Body, 4000);
        }
        else
        {
            var prompt = BuildPrompt(intent, facts);
            try
            {
                var raw = await GenerateCopyAsync(prompt, noticeType, lease.Id, ct);
                if (TryParseCopy(raw, out var llmSubject, out var llmBody))
                {
                    subject = Truncate(llmSubject, 200);
                    body = Truncate(llmBody, 4000);
                    usedPrompt = Truncate(prompt, 8000);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LLM copy generation failed for {NoticeType} (lease {LeaseId}); using template.", noticeType, lease.Id);
            }
        }

        return new NoticeDraft
        {
            PortfolioId = portfolioId,
            LeaseId = lease.Id,
            TenantId = lease.TenantId,
            PropertyId = lease.PropertyId,
            NoticeType = noticeType,
            Subject = subject,
            Body = body,
            Reason = reason,
            GenerationPrompt = usedPrompt,
            TriggerDate = triggerDate,
            CreatedAt = now,
            UpdatedAt = now
        };
    }
```

- [ ] **Step 5: Add a token-builder helper and pass tokens + template from each builder**

Add this helper to `NoticeDraftService` (near `UnitSuffix`):

```csharp
    /// <summary>
    /// Builds the merge-token values for a draft from grounded facts. Only includes tokens that have a
    /// real value; the renderer blanks anything missing. Currency/dates are pre-formatted strings.
    /// </summary>
    private static Dictionary<string, string> BuildTokens(
        Lease lease,
        string tenantName,
        string propertyName,
        string? unitNumber,
        (string Key, string Value)[] extra)
    {
        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [NoticeMergeFields.TenantName] = tenantName,
            [NoticeMergeFields.PropertyAddress] = propertyName,
            [NoticeMergeFields.UnitNumber] = unitNumber ?? "",
            [NoticeMergeFields.LeaseStartDate] = lease.StartDate.ToString("MMMM d, yyyy"),
            [NoticeMergeFields.LeaseEndDate] = lease.EndDate.ToString("MMMM d, yyyy"),
            [NoticeMergeFields.RentAmount] = lease.MonthlyRent.ToString("C0"),
        };
        foreach (var (key, value) in extra) tokens[key] = value;
        return tokens;
    }
```

In `BuildRenewalDraftAsync`, add a `NoticeTemplate? template` parameter and build tokens before the `ComposeAsync` call, then pass both:

```csharp
    private async Task<NoticeDraft> BuildRenewalDraftAsync(
        int portfolioId, Lease lease, int daysToEnd, DateTime now, NoticeTemplate? template, CancellationToken ct)
    {
        // ... existing tenant/propertyName/unit/tenantName/proposedRent/newEndDate/facts/deterministic* ...

        var tokens = BuildTokens(lease, tenantName, propertyName, lease.Unit?.UnitNumber,
            [(NoticeMergeFields.LandlordName, ""), (NoticeMergeFields.PortfolioName, "")]);

        var draft = await ComposeAsync(
            portfolioId, lease, "RenewalOffer",
            intent: "a friendly lease-renewal offer",
            facts: facts,
            deterministicSubject: deterministicSubject,
            deterministicBody: deterministicBody,
            reason: $"Lease ends in {daysToEnd} days. Proposed {proposedRent:C0}/mo ({RenewalEscalationPercent:0.#}% increase) through {newEndDate:MMM d, yyyy}.",
            triggerDate: lease.EndDate.Date,
            now: now,
            template: template,
            tokens: tokens,
            ct: ct);

        return draft;
    }
```

Apply the same change to `BuildMoveOutDraftAsync` (token extras: none beyond common; `LeaseEndDate` already in `BuildTokens`) and `BuildLateDraftAsync` (extras: `(NoticeMergeFields.OverdueAmount, payment.Amount.ToString("C0"))`, `(NoticeMergeFields.RentDueDate, payment.DueDate.ToString("MMMM d, yyyy"))`; `LateFeeAmount` left "" — not computed here). For `BuildLateDraftAsync`, build tokens from `payment.Lease!` and pass `template`.

Update the three call sites inside `GenerateAsync` to pass the template:
- `created.Add(await BuildRenewalDraftAsync(portfolioId, lease, daysToEnd, now, TemplateFor("RenewalOffer"), ct));`
- `created.Add(await BuildMoveOutDraftAsync(portfolioId, lease, daysToEnd, now, TemplateFor("MoveOutReminder"), ct));`
- `created.Add(await BuildLateDraftAsync(portfolioId, payment, daysLate, now, TemplateFor("LateRentNotice"), ct));`

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter NoticeTemplateGenerationTests`
Expected: PASS.

- [ ] **Step 7: Run the existing notice tests to confirm no regression**

Run: `dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter Notice`
Expected: PASS (existing renewal/move-out/late tests still green — no template means unchanged copy).

- [ ] **Step 8: Commit**

```bash
git add RentalCommand.Api/Services/Domain/NoticeDraftService.cs RentalCommand.Api.Tests/Notices/NoticeTemplateGenerationTests.cs
git commit -m "Render landlord template in notice generation when one exists"
```

---

### Task 5: New notice types — `RentReminder` and `MonthToMonthConversion`

**Files:**
- Modify: `RentalCommand.Api/Services/Domain/NoticeDraftService.cs` (`GenerateAsync` lease section; two new `Build*DraftAsync` builders)
- Modify: `RentalCommand.Api/DTOs/NoticeDraftDtos.cs:` (`GenerateNoticeDraftsRequest.NoticeType` doc comment — add the two new type names)
- Test: `RentalCommand.Api.Tests/Notices/NewNoticeTypesTests.cs`

**Interfaces:**
- Consumes: the template-aware `ComposeAsync` + `BuildTokens` from Task 4.
- Produces: generation support for `"RentReminder"` and `"MonthToMonthConversion"`. Both are generated **only when explicitly requested** (a non-null `NoticeType`), never in portfolio-wide all-types runs, so autopilot/portfolio behavior is unchanged.

- [ ] **Step 1: Write the failing test**

```csharp
// RentalCommand.Api.Tests/Notices/NewNoticeTypesTests.cs
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using Xunit;

namespace RentalCommand.Api.Tests.Notices;

public class NewNoticeTypesTests
{
    [Fact]
    public async Task Generate_RentReminder_for_tenant_produces_one_draft()
    {
        using var db = TestDb.Create();
        var (portfolioId, tenantId) = await TestDb.SeedActiveLeaseAsync(db, tenantName: "Sam Rivera", monthlyRent: 1500m);
        var service = TestDb.NoticeDraftService(db);

        var result = await service.GenerateAsync(
            portfolioId, new GenerateNoticeDraftsRequest { TenantId = tenantId, NoticeType = "RentReminder" });

        var draft = Assert.Single(result.Drafts);
        Assert.Equal("RentReminder", draft.NoticeType);
        Assert.Contains("Sam Rivera", draft.Body);
    }

    [Fact]
    public async Task Generate_MonthToMonth_for_tenant_produces_one_draft()
    {
        using var db = TestDb.Create();
        var (portfolioId, tenantId) = await TestDb.SeedActiveLeaseAsync(db, tenantName: "Sam Rivera", monthlyRent: 1500m);
        var service = TestDb.NoticeDraftService(db);

        var result = await service.GenerateAsync(
            portfolioId, new GenerateNoticeDraftsRequest { TenantId = tenantId, NoticeType = "MonthToMonthConversion" });

        var draft = Assert.Single(result.Drafts);
        Assert.Equal("MonthToMonthConversion", draft.NoticeType);
    }

    [Fact]
    public async Task Portfolio_wide_generate_does_not_create_new_types()
    {
        using var db = TestDb.Create();
        await TestDb.SeedActiveLeaseAsync(db, tenantName: "Sam Rivera", monthlyRent: 1500m);
        var service = TestDb.NoticeDraftService(db);

        // No NoticeType, no TenantId → portfolio-wide all-applicable run.
        var result = await service.GenerateAsync(db.Portfolios.Select(p => p.Id).First() is var pid ? pid : 0, null);

        Assert.DoesNotContain(result.Drafts, d => d.NoticeType is "RentReminder" or "MonthToMonthConversion");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter NewNoticeTypesTests`
Expected: FAIL — the two new types produce zero drafts.

- [ ] **Step 3: Recognize the new types in `GenerateAsync`**

In `GenerateAsync`, after `var wantsMoveOut = WantsType("MoveOutReminder");` add (these are true ONLY on explicit request, never portfolio-wide):

```csharp
        var wantsRentReminder = string.Equals(requestedType, "RentReminder", StringComparison.OrdinalIgnoreCase);
        var wantsMonthToMonth = string.Equals(requestedType, "MonthToMonthConversion", StringComparison.OrdinalIgnoreCase);
```

Change the lease-section guard from `if (wantsRenewal || wantsMoveOut)` to:

```csharp
        if (wantsRenewal || wantsMoveOut || wantsRentReminder || wantsMonthToMonth)
```

Inside the `foreach (var lease in leases)` loop, after the move-out block, add:

```csharp
                if (wantsRentReminder
                    && !DraftExists(existingDrafts, lease.Id, "RentReminder", created))
                {
                    created.Add(await BuildRentReminderDraftAsync(portfolioId, lease, now, TemplateFor("RentReminder"), ct));
                }

                if (wantsMonthToMonth
                    && !DraftExists(existingDrafts, lease.Id, "MonthToMonthConversion", created))
                {
                    created.Add(await BuildMonthToMonthDraftAsync(portfolioId, lease, daysToEnd, now, TemplateFor("MonthToMonthConversion"), ct));
                }
```

(Note: `daysToEnd` is already computed at the top of the loop. Because these two are only `true` when `forced`, the `leaseQuery` window filter is skipped, so the tenant's active lease is loaded regardless of end date.)

- [ ] **Step 4: Implement the two builders**

Add after `BuildLateDraftAsync`:

```csharp
    private async Task<NoticeDraft> BuildRentReminderDraftAsync(
        int portfolioId, Lease lease, DateTime now, NoticeTemplate? template, CancellationToken ct)
    {
        var tenant = lease.Tenant!;
        var propertyName = lease.Property?.Name ?? "your home";
        var unit = UnitSuffix(lease.Unit?.UnitNumber);
        var tenantName = $"{tenant.FirstName} {tenant.LastName}".Trim();

        var facts =
            $"- Tenant: {tenantName}\n" +
            $"- Property/unit: {propertyName}{unit}\n" +
            $"- Monthly rent: {lease.MonthlyRent:C0}\n" +
            "- This is a friendly heads-up that rent is coming due.\n" +
            "- Ask the tenant to reply with any questions.";

        var deterministicSubject = $"Rent reminder for {propertyName}{unit}";
        var deterministicBody =
            $"Hi {tenantName}, this is a friendly reminder that your rent of {lease.MonthlyRent:C0} "
            + $"for {propertyName}{unit} is coming due. Please reach out with any questions. Thank you!";

        var tokens = BuildTokens(lease, tenantName, propertyName, lease.Unit?.UnitNumber,
            [(NoticeMergeFields.RentDueDate, "")]);

        return await ComposeAsync(
            portfolioId, lease, "RentReminder",
            intent: "a friendly upcoming-rent reminder",
            facts: facts,
            deterministicSubject: deterministicSubject,
            deterministicBody: deterministicBody,
            reason: "Manual rent reminder.",
            triggerDate: now.Date,
            now: now,
            template: template,
            tokens: tokens,
            ct: ct);
    }

    private async Task<NoticeDraft> BuildMonthToMonthDraftAsync(
        int portfolioId, Lease lease, int daysToEnd, DateTime now, NoticeTemplate? template, CancellationToken ct)
    {
        var tenant = lease.Tenant!;
        var propertyName = lease.Property?.Name ?? "your home";
        var unit = UnitSuffix(lease.Unit?.UnitNumber);
        var tenantName = $"{tenant.FirstName} {tenant.LastName}".Trim();

        var facts =
            $"- Tenant: {tenantName}\n" +
            $"- Property/unit: {propertyName}{unit}\n" +
            $"- Current lease ends: {lease.EndDate:MMMM d, yyyy}\n" +
            $"- Current rent: {lease.MonthlyRent:C0}/month\n" +
            "- Offer to continue on a month-to-month basis at the current rent after the lease ends.\n" +
            "- Ask the tenant to reply to accept or ask questions.";

        var deterministicSubject = $"Month-to-month option for {propertyName}{unit}";
        var deterministicBody =
            $"Hi {tenantName}, your lease for {propertyName}{unit} ends on {lease.EndDate:MMMM d, yyyy}. "
            + $"We're happy to continue on a month-to-month basis at {lease.MonthlyRent:C0} per month. "
            + "Please reply to accept or with any questions.";

        var tokens = BuildTokens(lease, tenantName, propertyName, lease.Unit?.UnitNumber, []);

        return await ComposeAsync(
            portfolioId, lease, "MonthToMonthConversion",
            intent: "a friendly month-to-month continuation offer",
            facts: facts,
            deterministicSubject: deterministicSubject,
            deterministicBody: deterministicBody,
            reason: "Manual month-to-month conversion offer.",
            triggerDate: lease.EndDate.Date,
            now: now,
            template: template,
            tokens: tokens,
            ct: ct);
    }
```

- [ ] **Step 5: Update the request DTO doc comment**

In `RentalCommand.Api/DTOs/NoticeDraftDtos.cs`, change the `GenerateNoticeDraftsRequest.NoticeType` summary to list all five:

```csharp
    /// <summary>One of <c>RentReminder</c>, <c>RenewalOffer</c>, <c>MonthToMonthConversion</c>, <c>MoveOutReminder</c>, <c>LateRentNotice</c>; null = all applicable.</summary>
    public string? NoticeType { get; set; }
```

- [ ] **Step 6: Run to verify it passes**

Run: `dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter NewNoticeTypesTests`
Expected: PASS (3 tests).

- [ ] **Step 7: Commit**

```bash
git add RentalCommand.Api/Services/Domain/NoticeDraftService.cs RentalCommand.Api/DTOs/NoticeDraftDtos.cs RentalCommand.Api.Tests/Notices/NewNoticeTypesTests.cs
git commit -m "Add RentReminder and MonthToMonthConversion notice types"
```

---

### Task 6: Notice templates CRUD service + controller

**Files:**
- Create: `RentalCommand.Api/DTOs/NoticeTemplateDtos.cs`
- Create: `RentalCommand.Api/Services/Domain/INoticeTemplateService.cs`
- Create: `RentalCommand.Api/Services/Domain/NoticeTemplateService.cs`
- Create: `RentalCommand.Api/Controllers/NoticeTemplatesController.cs`
- Modify: `RentalCommand.Api/Program.cs` (register `INoticeTemplateService`) — find the line registering `INoticeDraftService` and add the new registration beside it.
- Test: `RentalCommand.Api.Tests/Notices/NoticeTemplateServiceTests.cs`

**Interfaces:**
- Produces DTOs: `NoticeTemplateResponse { string NoticeType; string Subject; string Body; bool HasTemplate; IReadOnlyList<string> AvailableFields; DateTime? UpdatedAt; }`, `UpsertNoticeTemplateRequest { string Subject; string Body; }`.
- Produces: `INoticeTemplateService` with `Task<IReadOnlyList<NoticeTemplateResponse>> ListAsync(int portfolioId, CancellationToken)`, `Task<NoticeTemplateResponse> GetAsync(int portfolioId, string noticeType, CancellationToken)`, `Task<NoticeTemplateResponse> UpsertAsync(int portfolioId, string noticeType, UpsertNoticeTemplateRequest, CancellationToken)`.
- Produces endpoints under `api/v1/notices/templates`: `GET` (list all five types, with or without a saved template), `GET /{type}`, `PUT /{type}`.

The five supported types are fixed: `RentReminder, RenewalOffer, MonthToMonthConversion, MoveOutReminder, LateRentNotice`.

- [ ] **Step 1: Write the failing test**

```csharp
// RentalCommand.Api.Tests/Notices/NoticeTemplateServiceTests.cs
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using Xunit;

namespace RentalCommand.Api.Tests.Notices;

public class NoticeTemplateServiceTests
{
    [Fact]
    public async Task List_returns_all_five_types_with_available_fields()
    {
        using var db = TestDb.Create();
        var portfolioId = await TestDb.SeedPortfolioAsync(db);
        var service = new NoticeTemplateService(db);

        var list = await service.ListAsync(portfolioId, default);

        Assert.Equal(5, list.Count);
        Assert.All(list, t => Assert.False(t.HasTemplate));
        var late = list.Single(t => t.NoticeType == "LateRentNotice");
        Assert.Contains("overdue_amount", late.AvailableFields);
    }

    [Fact]
    public async Task Upsert_creates_then_updates_single_active_template()
    {
        using var db = TestDb.Create();
        var portfolioId = await TestDb.SeedPortfolioAsync(db);
        var service = new NoticeTemplateService(db);

        await service.UpsertAsync(portfolioId, "RenewalOffer",
            new UpsertNoticeTemplateRequest { Subject = "S1", Body = "B1" }, default);
        var afterCreate = await service.GetAsync(portfolioId, "RenewalOffer", default);
        Assert.True(afterCreate.HasTemplate);
        Assert.Equal("S1", afterCreate.Subject);

        await service.UpsertAsync(portfolioId, "RenewalOffer",
            new UpsertNoticeTemplateRequest { Subject = "S2", Body = "B2" }, default);
        var afterUpdate = await service.GetAsync(portfolioId, "RenewalOffer", default);
        Assert.Equal("S2", afterUpdate.Subject);

        Assert.Equal(1, db.NoticeTemplates.Count(t => t.PortfolioId == portfolioId && t.NoticeType == "RenewalOffer" && t.IsActive));
    }

    [Fact]
    public async Task Upsert_rejects_unknown_type()
    {
        using var db = TestDb.Create();
        var portfolioId = await TestDb.SeedPortfolioAsync(db);
        var service = new NoticeTemplateService(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpsertAsync(portfolioId, "BogusType",
                new UpsertNoticeTemplateRequest { Subject = "x", Body = "y" }, default));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter NoticeTemplateServiceTests`
Expected: FAIL — `NoticeTemplateService` / DTOs do not exist.

- [ ] **Step 3: Create the DTOs**

```csharp
// RentalCommand.Api/DTOs/NoticeTemplateDtos.cs
namespace RentalCommand.Api.DTOs;

public class NoticeTemplateResponse
{
    public string NoticeType { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool HasTemplate { get; set; }
    public IReadOnlyList<string> AvailableFields { get; set; } = [];
    public DateTime? UpdatedAt { get; set; }
}

public class UpsertNoticeTemplateRequest
{
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
}
```

- [ ] **Step 4: Create the service interface + implementation**

```csharp
// RentalCommand.Api/Services/Domain/INoticeTemplateService.cs
using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

public interface INoticeTemplateService
{
    Task<IReadOnlyList<NoticeTemplateResponse>> ListAsync(int portfolioId, CancellationToken ct = default);
    Task<NoticeTemplateResponse> GetAsync(int portfolioId, string noticeType, CancellationToken ct = default);
    Task<NoticeTemplateResponse> UpsertAsync(int portfolioId, string noticeType, UpsertNoticeTemplateRequest request, CancellationToken ct = default);
}
```

```csharp
// RentalCommand.Api/Services/Domain/NoticeTemplateService.cs
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

public class NoticeTemplateService : INoticeTemplateService
{
    private static readonly string[] SupportedTypes =
        ["RentReminder", "RenewalOffer", "MonthToMonthConversion", "MoveOutReminder", "LateRentNotice"];

    private readonly RentalCommandDbContext _db;

    public NoticeTemplateService(RentalCommandDbContext db) => _db = db;

    public async Task<IReadOnlyList<NoticeTemplateResponse>> ListAsync(int portfolioId, CancellationToken ct = default)
    {
        var saved = await _db.NoticeTemplates
            .AsNoTracking()
            .Where(t => t.PortfolioId == portfolioId && t.IsActive)
            .ToDictionaryAsync(t => t.NoticeType, ct);

        return SupportedTypes
            .Select(type => saved.TryGetValue(type, out var t) ? ToResponse(type, t) : EmptyResponse(type))
            .ToList();
    }

    public async Task<NoticeTemplateResponse> GetAsync(int portfolioId, string noticeType, CancellationToken ct = default)
    {
        EnsureSupported(noticeType);
        var t = await _db.NoticeTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.PortfolioId == portfolioId && x.NoticeType == noticeType && x.IsActive, ct);
        return t == null ? EmptyResponse(noticeType) : ToResponse(noticeType, t);
    }

    public async Task<NoticeTemplateResponse> UpsertAsync(int portfolioId, string noticeType, UpsertNoticeTemplateRequest request, CancellationToken ct = default)
    {
        EnsureSupported(noticeType);
        var existing = await _db.NoticeTemplates
            .FirstOrDefaultAsync(x => x.PortfolioId == portfolioId && x.NoticeType == noticeType && x.IsActive, ct);

        var now = DateTime.UtcNow;
        if (existing == null)
        {
            existing = new NoticeTemplate
            {
                PortfolioId = portfolioId,
                NoticeType = noticeType,
                Subject = (request.Subject ?? "").Trim(),
                Body = (request.Body ?? "").Trim(),
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.NoticeTemplates.Add(existing);
        }
        else
        {
            existing.Subject = (request.Subject ?? "").Trim();
            existing.Body = (request.Body ?? "").Trim();
            existing.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);
        return ToResponse(noticeType, existing);
    }

    private static void EnsureSupported(string noticeType)
    {
        if (!SupportedTypes.Contains(noticeType))
            throw new ArgumentException($"Unknown notice type '{noticeType}'.", nameof(noticeType));
    }

    private static NoticeTemplateResponse EmptyResponse(string type) => new()
    {
        NoticeType = type,
        Subject = "",
        Body = "",
        HasTemplate = false,
        AvailableFields = NoticeMergeFields.ForType(type),
        UpdatedAt = null,
    };

    private static NoticeTemplateResponse ToResponse(string type, NoticeTemplate t) => new()
    {
        NoticeType = type,
        Subject = t.Subject,
        Body = t.Body,
        HasTemplate = true,
        AvailableFields = NoticeMergeFields.ForType(type),
        UpdatedAt = t.UpdatedAt,
    };
}
```

- [ ] **Step 5: Create the controller**

```csharp
// RentalCommand.Api/Controllers/NoticeTemplatesController.cs
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Portfolio-scoped CRUD for landlord-authored notice templates (one active template per notice type).
/// Scope comes from the JWT portfolioId claim, matching <see cref="NoticeDraftsController"/>.
/// </summary>
[ApiController]
[Route("api/v1/notices/templates")]
[Produces("application/json")]
public class NoticeTemplatesController : ManagementControllerBase
{
    private readonly INoticeTemplateService _service;

    public NoticeTemplatesController(INoticeTemplateService service) => _service = service;

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<NoticeTemplateResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<NoticeTemplateResponse>>> List(CancellationToken ct)
        => Ok(await _service.ListAsync(GetPortfolioId(), ct));

    [HttpGet("{type}")]
    [ProducesResponseType(typeof(NoticeTemplateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<NoticeTemplateResponse>> Get(string type, CancellationToken ct)
    {
        try { return Ok(await _service.GetAsync(GetPortfolioId(), type, ct)); }
        catch (ArgumentException e) { return BadRequest(new { error = e.Message }); }
    }

    [HttpPut("{type}")]
    [ProducesResponseType(typeof(NoticeTemplateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<NoticeTemplateResponse>> Upsert(string type, [FromBody] UpsertNoticeTemplateRequest request, CancellationToken ct)
    {
        try { return Ok(await _service.UpsertAsync(GetPortfolioId(), type, request, ct)); }
        catch (ArgumentException e) { return BadRequest(new { error = e.Message }); }
    }
}
```

NOTE: confirm `ManagementControllerBase` is the right base (it provides `GetPortfolioId()` and staff-role gating, per `NoticeDraftsController`). Match whatever `NoticeDraftsController` extends.

- [ ] **Step 6: Register the service in DI**

In `RentalCommand.Api/Program.cs`, find the registration of `INoticeDraftService` (search `INoticeDraftService`) and add immediately after it:

```csharp
builder.Services.AddScoped<INoticeTemplateService, NoticeTemplateService>();
```

(Match the existing lifetime used for `INoticeDraftService` — likely `AddScoped`.)

- [ ] **Step 7: Run the service test to verify it passes**

Run: `dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter NoticeTemplateServiceTests`
Expected: PASS (3 tests).

- [ ] **Step 8: Build the API to confirm controller + DI compile**

Run: `dotnet build RentalCommand.Api/RentalCommand.Api.csproj`
Expected: Build succeeded.

- [ ] **Step 9: Commit**

```bash
git add RentalCommand.Api/DTOs/NoticeTemplateDtos.cs RentalCommand.Api/Services/Domain/INoticeTemplateService.cs RentalCommand.Api/Services/Domain/NoticeTemplateService.cs RentalCommand.Api/Controllers/NoticeTemplatesController.cs RentalCommand.Api/Program.cs RentalCommand.Api.Tests/Notices/NoticeTemplateServiceTests.cs
git commit -m "Add notice template CRUD service and controller"
```

---

### Task 7: Full backend regression + verification

**Files:** none (verification only)

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build RentalCommand.sln`
Expected: Build succeeded, 0 errors.

- [ ] **Step 2: Run the notices test slice**

Run: `dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter Notice`
Expected: PASS — renderer, template generation, new types, and template CRUD all green; pre-existing notice tests unaffected.

- [ ] **Step 3: Confirm migration applies cleanly (optional, if a dev DB is available)**

Run: `dotnet ef database update --project RentalCommand.Data --startup-project RentalCommand.Api`
Expected: migration `AddNoticeTemplatesAndSendMode` applies with no error.

---

## Self-Review

**Spec coverage (Plan 1 scope = backend foundation):**
- `NoticeTemplate` entity + single-active-per-type index → Task 1, 2. ✓
- Per-type send-mode storage (default AskFirst) → Task 1, 2 (the `AutoSend*` columns; UI/worker consumption is Plans 3). ✓
- Merge-token catalog + deterministic renderer (no LLM) → Task 3. ✓
- Template-or-fallback generation → Task 4. ✓
- New types `RentReminder`, `MonthToMonthConversion` (on-demand only, ignore timing windows) → Task 5. ✓
- Template CRUD API (`GET`, `GET/{type}`, `PUT/{type}`) → Task 6. ✓
- Deferred to later plans (correctly out of Plan 1): edit-before-send UI + full menu (Plan 2), auto-send worker + settings UI + send-mode DTO exposure (Plan 3), suppression (dropped).

**Placeholder scan:** No TBD/TODO. The only conditional note is the `TestDb` helper guidance in Task 4/5 — explicit instructions are given for what to add if missing.

**Type consistency:** `ComposeAsync(... NoticeTemplate? template, IReadOnlyDictionary<string,string> tokens ...)` defined in Task 4 and called with those args in Tasks 4 and 5. `NoticeTemplateRenderer.Render(subject, body, tokens)` consistent across Tasks 3–4. `NoticeMergeFields.ForType` used in Tasks 3 and 6. DTO names `NoticeTemplateResponse`/`UpsertNoticeTemplateRequest` consistent across Task 6. `AutoSend*` column names match between Task 1 and Task 2.

---

## Follow-on plans (not in this file)

- **Plan 2 — Client on-demand UX:** mobile + web per-tenant notice menu (all five types) with **editable** subject/body review → persist via `PATCH /notices/{id}` → `approve`. (mobile `create_tenant_notice.dart`/`notices_repository.dart`; web `tenant-notice-action.ts`/`notices.ts`.)
- **Plan 3 — Auto-send + settings UI:** map the `AutoSend*` columns to the `AskFirst|AutoSend` settings DTO; extend `NoticeDraftWorker` to auto-send AutoSend types **gated on an active template**; build the Settings → Notice templates editor (subject/body + insert-field) and per-type send-mode toggles on web + mobile.
