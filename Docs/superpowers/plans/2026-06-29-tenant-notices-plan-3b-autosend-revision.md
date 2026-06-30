# Tenant Notices — Plan 3b: Auto-send model revision Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Make every notice type's auto-send actually fire as the landlord expects, without contradictory sends. Lease-end notices (renewal / month-to-month / non-renewal) become a single mutually-exclusive choice; rent-reminder and late-rent keep independent auto-send toggles, and the autopilot generates rent-reminder drafts on a schedule so its toggle works.

**Architecture:** Replace the three lease-end `AutoSend*` bool columns with one `LeaseEndAutoAction` enum on `NotificationSettings`. The autopilot keeps generating lease-end drafts for the manual queue, and auto-sends only the type matching `LeaseEndAutoAction` (template-gated). Add periodic rent-reminder draft generation (one per upcoming rent period). Update web + mobile settings UI to a single lease-end selector plus the two recurring toggles.

**Tech Stack:** .NET 10 / EF Core / xUnit; SvelteKit 5 runes / node:test; Flutter / Riverpod / Dio.

## Global Constraints

- Builds on Plans 1–3 (all committed on `tsk-354-tenant-notices-templates-autosend`).
- Enum stored as string: `entity.Property(e => e.LeaseEndAutoAction).HasConversion<string>().HasMaxLength(40)`. Enums serialize as string names in JSON.
- Auto-send fires ONLY when an active `NoticeTemplate` exists for the type AND `NotifyTenants` is true. Default lease-end action is `Draft` (send nothing automatically). Recurring toggles default false.
- Lease-end types are mutually exclusive per lease: auto-send AT MOST ONE per lease per run.
- All queries DB-side; per-portfolio settings/templates loaded once. No Co-Authored-By trailer.
- Notice type ↔ action map: Renewal→`RenewalOffer`, MonthToMonth→`MonthToMonthConversion`, NonRenewal→`MoveOutReminder`.

---

### Task 1: Replace lease-end bools with `LeaseEndAutoAction` enum (Core + migration)

**Files:**
- Create: `RentalCommand.Core/Enums/LeaseEndAutoAction.cs`
- Modify: `RentalCommand.Core/Entities/NotificationSettings.cs`
- Modify: `RentalCommand.Data/RentalCommandDbContext.cs` (NotificationSettings entity config — add the string conversion)
- Create: migration `*_ReplaceLeaseEndAutoSendBoolsWithAction.cs`

**Interfaces:**
- Produces: `enum LeaseEndAutoAction { Draft, Renewal, MonthToMonth, NonRenewal }`.
- Produces on `NotificationSettings`: remove `AutoSendRenewal`, `AutoSendMonthToMonth`, `AutoSendMoveOut`; add `LeaseEndAutoAction LeaseEndAutoAction { get; set; } = LeaseEndAutoAction.Draft;`. Keep `AutoSendRentReminder`, `AutoSendLateRent`.

- [ ] **Step 1: Create the enum**

```csharp
// RentalCommand.Core/Enums/LeaseEndAutoAction.cs
namespace RentalCommand.Core.Enums;

/// <summary>
/// What the autopilot does automatically when a lease nears its end. Lease-end notices
/// (renewal / month-to-month / non-renewal) are mutually exclusive for a given lease, so this is a
/// single choice rather than independent per-type toggles. <see cref="Draft"/> (default) auto-sends
/// nothing — drafts still appear in the notices queue for manual approval.
/// </summary>
public enum LeaseEndAutoAction
{
    Draft = 0,
    Renewal = 1,
    MonthToMonth = 2,
    NonRenewal = 3,
}
```

- [ ] **Step 2: Edit `NotificationSettings`**

Remove the three lines `AutoSendRenewal`, `AutoSendMonthToMonth`, `AutoSendMoveOut`. Keep `AutoSendRentReminder` and `AutoSendLateRent`. Add (with `using RentalCommand.Core.Enums;` if needed):

```csharp
    public LeaseEndAutoAction LeaseEndAutoAction { get; set; } = LeaseEndAutoAction.Draft;
```

- [ ] **Step 3: Configure the enum column**

In `RentalCommandDbContext.cs`, in the `modelBuilder.Entity<NotificationSettings>(...)` block, add:

```csharp
            entity.Property(e => e.LeaseEndAutoAction).HasConversion<string>().HasMaxLength(40);
```

- [ ] **Step 4: Build**

Run: `dotnet build RentalCommand.Data/RentalCommand.Data.csproj` — expect 0 errors (the API/Engine/test references to the removed bools will fail to build until later tasks; building just the Data project keeps this task's scope verifiable).

- [ ] **Step 5: Generate the migration**

Run: `dotnet ef migrations add ReplaceLeaseEndAutoSendBoolsWithAction --project RentalCommand.Data --startup-project RentalCommand.Api`

NOTE: the startup project (API) must compile for `ef` to run. If the API doesn't yet compile because of removed-property references, do Task 2's DTO/service edits FIRST, then generate the migration. Order Tasks 1–2 so the migration is generated once both compile. Confirm the migration drops `AutoSendRenewal/AutoSendMonthToMonth/AutoSendMoveOut` and adds `LeaseEndAutoAction` (string, default "Draft" — add `defaultValue: "Draft"` if missing). Commit entity + enum + migration + snapshot (include `RentalCommandDbContextModelSnapshot.cs`!).

- [ ] **Step 6: Commit** — `git add` the enum, entity, DbContext, and ALL migration files INCLUDING `RentalCommandDbContextModelSnapshot.cs`. Commit: "Replace lease-end auto-send bools with LeaseEndAutoAction enum".

---

### Task 2: Settings API — expose `LeaseEndAutoAction`, drop the three bools

**Files:**
- Modify: `RentalCommand.Api/DTOs/NotificationSettingsDtos.cs`
- Modify: `RentalCommand.Api/Services/Domain/NotificationSettingsService.cs`
- Modify: `RentalCommand.Api.Tests/Domain/NotificationSettingsServiceTests.cs` (the send-mode round-trip test from Plan 3 references the removed bools — update it)

**Interfaces:**
- On `NotificationSettingsResponse` + `UpdateNotificationSettingsRequest`: remove `AutoSendRenewal/AutoSendMonthToMonth/AutoSendMoveOut`; add `LeaseEndAutoAction LeaseEndAutoAction { get; set; }`. Keep `AutoSendRentReminder`, `AutoSendLateRent`.

- [ ] **Step 1: Update DTOs** — replace the three lease-end bools with `public LeaseEndAutoAction LeaseEndAutoAction { get; set; }` on both DTOs (add `using RentalCommand.Core.Enums;`).

- [ ] **Step 2: Update the service mapping** — in `UpdateAsync` apply (`row.LeaseEndAutoAction = request.LeaseEndAutoAction;`, remove the three `row.AutoSend{Renewal,MonthToMonth,MoveOut}` lines) and in `ToAdminResponse` (`LeaseEndAutoAction = row.LeaseEndAutoAction,`, remove the three). Keep the two recurring bools in both places.

- [ ] **Step 3: Update the existing test** — in `NotificationSettingsServiceTests.cs`, change the Plan-3 round-trip test that set `AutoSendRenewal=true` etc. to instead set `LeaseEndAutoAction = LeaseEndAutoAction.Renewal` and assert it round-trips (and `AutoSendRentReminder`/`AutoSendLateRent` still round-trip). Run it red→green.

- [ ] **Step 4: Verify** — `dotnet build RentalCommand.Api/RentalCommand.Api.csproj` 0 errors; `dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter NotificationSettings` passes.

- [ ] **Step 5:** If Task 1's migration wasn't generated yet (because the API didn't compile), generate it now (Task 1 Step 5) and commit it with the snapshot. Then commit this task: "Expose LeaseEndAutoAction in notification settings API".

---

### Task 3: Autopilot — lease-end single-action auto-send + month-to-month generation

**Files:**
- Modify: `RentalCommand.Api/Services/Domain/NoticeDraftService.cs` (`GenerateAsync`: also generate `MonthToMonthConversion` drafts portfolio-wide within the lease-end window, so the autopilot can auto-send it)
- Modify: `RentalCommand.Engine/Services/NoticeDraftGenerationService.cs` (auto-send gate: lease-end uses `LeaseEndAutoAction`, not per-type bools)
- Modify/Add: `RentalCommand.Engine.Tests/.../NoticeAutoSendTests.cs`

**Interfaces:**
- Consumes: `LeaseEndAutoAction` from settings; `_notices.ApproveAsync`.
- Produces: in a portfolio-wide `GenerateAsync` run, `MonthToMonthConversion` drafts are generated for near-end leases (alongside renewal + move-out); the autopilot auto-sends only the lease-end type matching `LeaseEndAutoAction` (when a template exists + NotifyTenants), plus rent-reminder/late-rent per their bools.

- [ ] **Step 1: Generate month-to-month portfolio-wide**

In `NoticeDraftService.GenerateAsync`, currently `wantsMonthToMonth` is true only on explicit request. Change so MonthToMonth is ALSO generated in portfolio-wide runs within the lease-end window (mirror `wantsMoveOut`: a 30–75 day window — use the same window as renewal, 75 days, so a month-to-month offer can go out with the same lead time). Concretely: set `var wantsMonthToMonth = requestedType == null ? true : string.Equals(requestedType, "MonthToMonthConversion", StringComparison.OrdinalIgnoreCase);` and include it in the lease-section guard and the window filter (treat like renewal for the window). Keep idempotency via `DraftExists(... "MonthToMonthConversion" ...)`. RentReminder stays explicit-only here (Task 4 handles its periodic generation).

TEST (API NoticeDraftServiceTests harness): a portfolio-wide `GenerateAsync` with a lease ending in 60 days now also produces a `MonthToMonthConversion` draft (in addition to renewal). Assert it. Also keep the existing "portfolio-wide does not create RentReminder" assertion (RentReminder still excluded here).

- [ ] **Step 2: Lease-end auto-send by action**

In `NoticeDraftGenerationService`, replace the lease-end portion of `AutoSendEnabled`. Keep the recurring bools for `RentReminder`/`LateRentNotice`. For the three lease-end types, auto-send is driven by `settings.LeaseEndAutoAction`:

```csharp
    private static bool AutoSendEnabled(NotificationSettings? s, string noticeType) => s != null && noticeType switch
    {
        "RentReminder" => s.AutoSendRentReminder,
        "LateRentNotice" => s.AutoSendLateRent,
        "RenewalOffer" => s.LeaseEndAutoAction == LeaseEndAutoAction.Renewal,
        "MonthToMonthConversion" => s.LeaseEndAutoAction == LeaseEndAutoAction.MonthToMonth,
        "MoveOutReminder" => s.LeaseEndAutoAction == LeaseEndAutoAction.NonRenewal,
        _ => false,
    };
```

Because only one lease-end type matches the action, at most one lease-end draft per lease is auto-sent; the others remain drafts in the queue.

TEST (Engine NoticeAutoSendTests): with `LeaseEndAutoAction = NonRenewal` + an active `MoveOutReminder` template + NotifyTenants, after `GenerateAllAsync` the MoveOutReminder draft is `Approved` while the RenewalOffer draft (also generated) stays `Draft`. Update the prior Plan-3 auto-send tests that assumed `AutoSendRenewal` bool to use `LeaseEndAutoAction = Renewal`.

- [ ] **Step 3: Verify** — `dotnet build RentalCommand.sln` 0 errors; `dotnet test RentalCommand.Api.Tests/... --filter Notice` and `RentalCommand.Engine.Tests/... --filter Notice` pass. Commit: "Autopilot: lease-end auto-send via LeaseEndAutoAction + month-to-month generation".

---

### Task 4: Autopilot — periodic rent-reminder generation + auto-send

**Files:**
- Modify: `RentalCommand.Api/Services/Domain/NoticeDraftService.cs` (`GenerateAsync`: generate `RentReminder` drafts for upcoming rent, per-period idempotent)
- Modify/Add: `RentalCommand.Api.Tests/.../` rent-reminder generation test

**Interfaces:**
- Produces: in a portfolio-wide `GenerateAsync` run, a `RentReminder` draft is created for each active lease with a scheduled rent payment due within `RentChargeLeadDays`, at most one per (lease, rent due date). Auto-send then happens via Task 3's gate (`AutoSendRentReminder`).

- [ ] **Step 1: Add periodic rent-reminder generation**

Currently `wantsRentReminder` is explicit-only. Add portfolio-wide generation: when `requestedType == null` (autopilot) OR explicitly requested, generate a `RentReminder` for leases whose next scheduled rent `Payment` (Status Scheduled, `DueDate >= today`) is due within `RentChargeLeadDays` (read from settings/config — the service already has access via the request path; if not available, default 5). 

Idempotency must be PER RENT PERIOD, not per-(lease,type) (reminders recur monthly): a RentReminder is skipped if an existing `RentReminder` draft/notice for that lease already has `TriggerDate == that payment's DueDate`. Implement a period-aware check: preload existing RentReminder `(LeaseId, TriggerDate)` pairs for the portfolio (any status, not just Draft, so a sent reminder isn't re-created) and skip when the (lease, dueDate) pair exists. Set the generated draft's `TriggerDate = payment.DueDate.Date`.

Query the upcoming rent payments DB-side (one query for the portfolio, joined to active leases), not per-lease. Build the draft via the existing `BuildRentReminderDraftAsync` (extend it to accept the due date so `rent_due_date` token + `TriggerDate` are populated).

- [ ] **Step 2: Test** (API harness): seed an active lease with a scheduled rent `Payment` due in 3 days; a portfolio-wide `GenerateAsync` creates exactly one `RentReminder` with `TriggerDate` == that due date; a second run creates none (idempotent). A lease whose rent is due in 30 days (beyond lead days) gets none.

- [ ] **Step 3: Verify** — build + `--filter Notice` tests pass. Commit: "Autopilot: periodic rent-reminder generation (per rent period)".

---

### Task 5: Web settings — lease-end selector + recurring toggles

**Files:**
- Modify: web settings types (the notification-settings request/response types) — replace the three `autoSend{Renewal,MonthToMonth,MoveOut}` booleans with `leaseEndAutoAction: 'Draft' | 'Renewal' | 'MonthToMonth' | 'NonRenewal'`; keep `autoSendRentReminder`, `autoSendLateRent`.
- Modify: `web/src/lib/components/settings/NoticeTemplatesSection.svelte`
- Modify: `web/src/routes/(protected)/settings/+page.svelte` and the onboarding `setSettings` forwarding (drop the three removed fields, add `leaseEndAutoAction`).

- [ ] **Step 1:** Update the settings types (remove the three booleans, add `leaseEndAutoAction` string union; default `'Draft'`). Fix every reference (page PUT payload, response re-sync, onboarding forwarding) so the project compiles.

- [ ] **Step 2:** In `NoticeTemplatesSection.svelte`: for the three lease-end types, REMOVE the per-type Auto-send toggle and instead render ONE selector ("When a lease is ending, automatically:" → Just draft it / Offer renewal / Offer month-to-month / Send non-renewal) bound to `leaseEndAutoAction`. Keep the independent Auto-send toggles for Rent reminder + Late rent. Keep the per-type template editors for all five. Keep the "add a template to enable auto-send" hint logic (for the lease-end type matching the selected action, and for the two recurring toggles).

- [ ] **Step 3: Verify** — `pnpm check` 0 errors; `node --test` passes; `pnpm build` succeeds. Commit: "Web settings: lease-end auto-send selector + recurring toggles".

---

### Task 6: Mobile settings — lease-end selector + recurring toggles

**Files:**
- Modify: `mobile/lib/features/settings/notification_settings_repository.dart` (replace the three lease-end bools with a `leaseEndAutoAction` String; keep the two recurring bools — update fields, constructor, copyWith, fromJson, toJson)
- Modify: `mobile/lib/features/notices/notice_templates_screen.dart`

- [ ] **Step 1:** In the settings model, remove `autoSendRenewal/autoSendMonthToMonth/autoSendMoveOut`; add `final String leaseEndAutoAction;` (default `'Draft'`), threaded through constructor/copyWith/fromJson (`json['leaseEndAutoAction'] as String? ?? 'Draft'`)/toJson. Keep `autoSendRentReminder`, `autoSendLateRent`.

- [ ] **Step 2:** In `notice_templates_screen.dart`: replace the three lease-end `SwitchListTile`s with ONE selector (e.g. a `DropdownButton`/segmented control or a group of `RadioListTile`s) titled "When a lease is ending, automatically" with options Just draft / Offer renewal / Offer month-to-month / Send non-renewal, bound to `leaseEndAutoAction` (saved via the existing settings update flow). Keep the independent Auto-send switches for Rent reminder + Late rent and all five template editors. Keep the "add a template to enable auto-send" hint.

- [ ] **Step 3: Verify** — `flutter analyze` (the four touched files) no issues; `flutter test test/notice_templates_repository_test.dart` passes (and any settings test). Commit: "Mobile settings: lease-end auto-send selector + recurring toggles".

---

### Task 7: Full verification

- [ ] Backend: `dotnet build RentalCommand.sln` 0 errors; `--filter Notice` (API + Engine) + `--filter NotificationSettings` pass.
- [ ] Web: `pnpm check` 0 errors; `node --test` pass; `pnpm build` ok.
- [ ] Mobile: `flutter analyze`; `flutter test` pass.

---

## Self-Review

**Coverage:** lease-end mutual-exclusion via `LeaseEndAutoAction` (Tasks 1,2,3,5,6); month-to-month now autopilot-generated (Task 3); rent-reminder periodic generation so its toggle works (Task 4); recurring toggles kept (Tasks 2,5,6); late-rent unchanged (already auto-sends). Migration drops old bools + adds enum, snapshot committed (Task 1, fixing the earlier snapshot-omission lesson).

**Type consistency:** `LeaseEndAutoAction` values `{Draft,Renewal,MonthToMonth,NonRenewal}` identical across Core enum, DTOs, `AutoSendEnabled` switch, and the web union/mobile string. Notice type ↔ action map consistent. `AutoSendRentReminder`/`AutoSendLateRent` retained everywhere.

**Note:** rent-reminder and late-rent auto-send may overlap the automatic matrix notifications (RentChargeService / LateFeeService); suppression remains deferred by explicit decision.
