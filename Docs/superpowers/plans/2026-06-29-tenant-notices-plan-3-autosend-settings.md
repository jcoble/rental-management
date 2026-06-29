# Tenant Notices — Plan 3: Auto-send + Settings UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the landlord author a template per notice type and choose, per type, whether notices **auto-send autonomously** (only once an active template exists) or wait for manual approval — surfaced in Settings on web and mobile, and honored by the autopilot worker.

**Architecture:** Plan 1 added the `NoticeTemplate` table, the renderer, and the `AutoSend*` columns on `NotificationSettings`. This plan (a) exposes those columns through the notification-settings API, (b) extends the autopilot generation service to **send** drafts whose type is `AutoSend` AND has an active template, and (c) builds the Settings UI (template editor + per-type send-mode toggles) on web and mobile.

**Tech Stack:** .NET 10 / EF Core / xUnit; SvelteKit 5 runes + TanStack Query / Vitest; Flutter / Riverpod / Dio.

## Global Constraints

- **Depends on Plans 1 & 2.** Plan 1 provides the entity, columns, renderer, template CRUD; Plan 2 provides the client notice client methods.
- **Auto-send safety:** default every type to ask-first (the `AutoSend*` columns default `false`). Auto-send fires ONLY when (`AutoSend{Type}` is true) AND (an active `NoticeTemplate` exists for that type) AND (`NotifyTenants` is true). Otherwise the draft is left for manual approval.
- **All queries DB-side**, portfolio-scoped. Preload per-portfolio templates/settings once per portfolio in the worker — no per-draft queries.
- Auto-send channels map from the type's matrix preference: InApp→`Portal`, Email→`Email`, Sms→`Sms` (Push is not a notice-letter channel).
- Enums serialize as string names. No `Co-Authored-By` trailer in commits.

---

### Task 1: Expose per-type send mode through the notification-settings API

**Files:**
- Modify: `RentalCommand.Api/DTOs/NotificationSettingsDtos.cs` (`NotificationSettingsResponse`, `UpdateNotificationSettingsRequest`)
- Modify: `RentalCommand.Api/Services/Domain/NotificationSettingsService.cs` (map to/from entity)
- Test: `RentalCommand.Api.Tests/Notices/NotificationSettingsSendModeTests.cs`

**Interfaces:**
- Consumes: the `AutoSend*` bool columns from Plan 1.
- Produces on both `NotificationSettingsResponse` and `UpdateNotificationSettingsRequest`: `bool AutoSendRentReminder; bool AutoSendRenewal; bool AutoSendMonthToMonth; bool AutoSendMoveOut; bool AutoSendLateRent;`

- [ ] **Step 1: Write the failing test**

```csharp
// RentalCommand.Api.Tests/Notices/NotificationSettingsSendModeTests.cs
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using Xunit;

namespace RentalCommand.Api.Tests.Notices;

public class NotificationSettingsSendModeTests
{
    [Fact]
    public async Task Update_then_get_round_trips_per_type_send_mode()
    {
        using var db = TestDb.Create();
        var portfolioId = await TestDb.SeedPortfolioAsync(db);
        var service = TestDb.NotificationSettingsService(db); // existing test factory; mirror other settings tests

        await service.UpdateAsync(portfolioId, new UpdateNotificationSettingsRequest
        {
            AutoSendRentReminder = true,
            AutoSendLateRent = true,
        }, default);

        var resp = await service.GetAsync(portfolioId, default);
        Assert.True(resp.AutoSendRentReminder);
        Assert.True(resp.AutoSendLateRent);
        Assert.False(resp.AutoSendRenewal);
    }
}
```

NOTE: match the actual method names on `INotificationSettingsService` (e.g. `GetAsync`/`UpdateAsync`) by reading `INotificationSettingsService.cs`; adjust the test calls accordingly.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter NotificationSettingsSendModeTests`
Expected: FAIL — properties don't exist.

- [ ] **Step 3: Add the fields to both DTOs**

In `NotificationSettingsResponse` (after `EnableDailyBriefingMessages` related fields) and in `UpdateNotificationSettingsRequest` (same place), add:

```csharp
    public bool AutoSendRentReminder { get; set; }
    public bool AutoSendRenewal { get; set; }
    public bool AutoSendMonthToMonth { get; set; }
    public bool AutoSendMoveOut { get; set; }
    public bool AutoSendLateRent { get; set; }
```

- [ ] **Step 4: Map them in the service**

In `RentalCommand.Api/Services/Domain/NotificationSettingsService.cs`, find where the entity → `NotificationSettingsResponse` projection happens and where `UpdateNotificationSettingsRequest` → entity is applied (search `EnableLateFees`). Add the five fields in BOTH directions, e.g. in the response mapping:

```csharp
            AutoSendRentReminder = e.AutoSendRentReminder,
            AutoSendRenewal = e.AutoSendRenewal,
            AutoSendMonthToMonth = e.AutoSendMonthToMonth,
            AutoSendMoveOut = e.AutoSendMoveOut,
            AutoSendLateRent = e.AutoSendLateRent,
```

and in the update apply:

```csharp
            entity.AutoSendRentReminder = request.AutoSendRentReminder;
            entity.AutoSendRenewal = request.AutoSendRenewal;
            entity.AutoSendMonthToMonth = request.AutoSendMonthToMonth;
            entity.AutoSendMoveOut = request.AutoSendMoveOut;
            entity.AutoSendLateRent = request.AutoSendLateRent;
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter NotificationSettingsSendModeTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add RentalCommand.Api/DTOs/NotificationSettingsDtos.cs RentalCommand.Api/Services/Domain/NotificationSettingsService.cs RentalCommand.Api.Tests/Notices/NotificationSettingsSendModeTests.cs
git commit -m "Expose per-type notice send mode in notification settings API"
```

---

### Task 2: Autopilot auto-send (gated on active template)

**Files:**
- Modify: `RentalCommand.Engine/Services/NoticeDraftGenerationService.cs` (`GenerateAllAsync`)
- Test: `RentalCommand.Engine.Tests/NoticeAutoSendTests.cs`

**Interfaces:**
- Consumes: `INoticeDraftService.GenerateAsync` (returns `GenerateNoticeDraftsResponse` with `Drafts[]` carrying `Id`, `NoticeType`), `INoticeDraftService.ApproveAsync`, `RentalCommandDbContext.NotificationSettings`, `RentalCommandDbContext.NoticeTemplates`, `NotificationsConfig.ResolveChannels`.
- Produces: after generating a portfolio's drafts, any draft whose type is set to `AutoSend` AND has an active template is approved/sent on the type's resolved channels; everything else stays a draft.

- [ ] **Step 1: Write the failing test**

```csharp
// RentalCommand.Engine.Tests/NoticeAutoSendTests.cs
using RentalCommand.Core.Entities;
using Xunit;

namespace RentalCommand.Engine.Tests;

public class NoticeAutoSendTests
{
    [Fact]
    public async Task AutoSend_type_with_template_is_approved_not_left_draft()
    {
        using var db = EngineTestDb.Create();
        var (portfolioId, _) = await EngineTestDb.SeedLeaseEndingSoonAsync(db);
        // Enable autopilot + tenant sends + auto-send renewal, and author a template.
        await EngineTestDb.SetSettingsAsync(db, portfolioId, enableAutopilot: true, notifyTenants: true, autoSendRenewal: true);
        db.NoticeTemplates.Add(new NoticeTemplate { PortfolioId = portfolioId, NoticeType = "RenewalOffer", Subject = "S", Body = "B {{tenant_name}}", IsActive = true });
        await db.SaveChangesAsync();

        var svc = EngineTestDb.NoticeDraftGenerationService(db);
        await svc.GenerateAllAsync(default);

        var renewal = db.NoticeDrafts.Single(d => d.PortfolioId == portfolioId && d.NoticeType == "RenewalOffer");
        Assert.Equal("Approved", renewal.Status);
        Assert.NotNull(renewal.ApprovedAt);
    }

    [Fact]
    public async Task AutoSend_without_template_stays_draft()
    {
        using var db = EngineTestDb.Create();
        var (portfolioId, _) = await EngineTestDb.SeedLeaseEndingSoonAsync(db);
        await EngineTestDb.SetSettingsAsync(db, portfolioId, enableAutopilot: true, notifyTenants: true, autoSendRenewal: true);
        // No template authored.
        await db.SaveChangesAsync();

        var svc = EngineTestDb.NoticeDraftGenerationService(db);
        await svc.GenerateAllAsync(default);

        var renewal = db.NoticeDrafts.Single(d => d.PortfolioId == portfolioId && d.NoticeType == "RenewalOffer");
        Assert.Equal("Draft", renewal.Status);
    }
}
```

NOTE: `EngineTestDb` helpers mirror the Api test harness for the Engine project (real `RentalCommandDbContext`, a stub `IConversationService` returning `Conversation { Id = 1 }`, no-op `ILlmProvider`, an `INotificationSettingsService` over the same db). If absent, add them in `RentalCommand.Engine.Tests` following the existing engine test setup.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test RentalCommand.Engine.Tests/RentalCommand.Engine.Tests.csproj --filter NoticeAutoSendTests`
Expected: FAIL — drafts stay `Draft` (no auto-send logic yet).

- [ ] **Step 3: Implement auto-send in `GenerateAllAsync`**

In `NoticeDraftGenerationService.GenerateAllAsync`, inside the `try` block, after `var result = await _notices.GenerateAsync(portfolioId, ct: ct);`, add the auto-send pass:

```csharp
                created += result.CreatedCount;

                // Auto-send: for each freshly-created draft whose type is set to AutoSend AND has an
                // active template, approve+send it now. Gated by NotifyTenants. Load the portfolio's
                // settings + active-template types once (DB-side) — no per-draft queries.
                if (cfg.NotifyTenants && result.Drafts.Count > 0)
                {
                    var settings = await _db.NotificationSettings
                        .AsNoTracking()
                        .FirstOrDefaultAsync(s => s.PortfolioId == portfolioId, ct);

                    var templatedTypes = await _db.NoticeTemplates
                        .AsNoTracking()
                        .Where(t => t.PortfolioId == portfolioId && t.IsActive)
                        .Select(t => t.NoticeType)
                        .ToListAsync(ct);
                    var templated = templatedTypes.ToHashSet();

                    foreach (var draft in result.Drafts)
                    {
                        if (draft.Status != "Draft") continue;
                        if (!AutoSendEnabled(settings, draft.NoticeType)) continue;
                        if (!templated.Contains(draft.NoticeType)) continue;

                        var channels = ChannelsFor(cfg, draft.NoticeType);
                        if (channels.Count == 0) continue;

                        try
                        {
                            await _notices.ApproveAsync(
                                portfolioId,
                                draft.Id,
                                new RentalCommand.Api.DTOs.ApproveNoticeDraftRequest { Channels = channels },
                                ct);
                        }
                        catch (Exception ex)
                        {
                            // A blocked/failed auto-send leaves the draft for manual review.
                            _logger.LogWarning(ex, "Auto-send failed for draft {DraftId} ({Type}); left as draft.", draft.Id, draft.NoticeType);
                        }
                    }
                }
```

Add these helpers to the class:

```csharp
    private static bool AutoSendEnabled(Core.Entities.NotificationSettings? s, string noticeType) => s != null && noticeType switch
    {
        "RentReminder" => s.AutoSendRentReminder,
        "RenewalOffer" => s.AutoSendRenewal,
        "MonthToMonthConversion" => s.AutoSendMonthToMonth,
        "MoveOutReminder" => s.AutoSendMoveOut,
        "LateRentNotice" => s.AutoSendLateRent,
        _ => false,
    };

    private static List<string> ChannelsFor(NotificationsConfig cfg, string noticeType)
    {
        var category = noticeType switch
        {
            "RentReminder" => Core.Enums.NotificationType.RentCharge,
            "LateRentNotice" => Core.Enums.NotificationType.LateFee,
            _ => Core.Enums.NotificationType.LeaseExpiry, // renewal / month-to-month / move-out
        };
        var pref = cfg.ResolveChannels(category);
        var channels = new List<string>();
        if (pref.EnableInApp) channels.Add("Portal");
        if (pref.EnableEmail) channels.Add("Email");
        if (pref.EnableSms) channels.Add("Sms");
        return channels;
    }
```

Add any missing `using` (e.g. `using RentalCommand.Core.Configuration;` is already present; ensure `Microsoft.EntityFrameworkCore` is imported — it is).

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test RentalCommand.Engine.Tests/RentalCommand.Engine.Tests.csproj --filter NoticeAutoSendTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Build the engine + run its notice tests**

Run: `dotnet build RentalCommand.Engine/RentalCommand.Engine.csproj && dotnet test RentalCommand.Engine.Tests/RentalCommand.Engine.Tests.csproj --filter Notice`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add RentalCommand.Engine/Services/NoticeDraftGenerationService.cs RentalCommand.Engine.Tests/NoticeAutoSendTests.cs
git commit -m "Autopilot auto-sends notices when type is AutoSend and has a template"
```

---

### Task 3: Web Settings — notice templates editor + send-mode toggles

**Files:**
- Modify: `web/src/lib/types` (add `NoticeTemplateResponse`, `UpsertNoticeTemplateRequest`; add the 5 `autoSend*` fields to the notification-settings types)
- Modify: `web/src/lib/api/endpoints/notices.ts` (add `templates` sub-client)
- Create: `web/src/lib/components/settings/NoticeTemplatesSection.svelte`
- Modify: `web/src/routes/(protected)/settings/+page.svelte` (mount the section)
- Test: `web/src/lib/components/settings/notice-templates-section.test.ts` (or a unit test for the field-insertion helper)

**Interfaces:**
- Consumes: `GET/PUT /notices/templates/{type}`, `GET /notices/templates`; the `autoSend*` settings fields from Task 1.
- Produces: a Settings section listing the five types, each with an Auto-send/Ask-first toggle and an "Edit template" editor (subject + body + insert-merge-field), saving via the template + settings endpoints.

- [ ] **Step 1: Add the template client**

In `web/src/lib/api/endpoints/notices.ts`, extend the exported `notices` object:

```ts
	templates: {
		list: () => api.get<NoticeTemplateResponse[]>('/notices/templates'),
		get: (type: string) => api.get<NoticeTemplateResponse>(`/notices/templates/${encodeURIComponent(type)}`),
		upsert: (type: string, body: UpsertNoticeTemplateRequest) =>
			api.put<NoticeTemplateResponse>(`/notices/templates/${encodeURIComponent(type)}`, body)
	}
```

Add the imports/types in `$lib/types`:

```ts
export interface NoticeTemplateResponse {
	noticeType: string;
	subject: string;
	body: string;
	hasTemplate: boolean;
	availableFields: string[];
	updatedAt?: string | null;
}
export interface UpsertNoticeTemplateRequest {
	subject: string;
	body: string;
}
```

And add to the notification-settings response/request types: `autoSendRentReminder`, `autoSendRenewal`, `autoSendMonthToMonth`, `autoSendMoveOut`, `autoSendLateRent` (all `boolean`).

- [ ] **Step 2: Write the failing test (field-insertion helper)**

Extract the "insert a `{{token}}` at the cursor" logic into a pure helper so it's testable:

```ts
// web/src/lib/components/settings/notice-templates-section.test.ts
import { describe, it, expect } from 'vitest';
import { insertFieldToken } from './insert-field-token';

describe('insertFieldToken', () => {
	it('inserts {{token}} at the caret', () => {
		const result = insertFieldToken('Hi , welcome', 3, 'tenant_name');
		expect(result.text).toBe('Hi {{tenant_name}}, welcome');
		expect(result.caret).toBe(3 + '{{tenant_name}}'.length);
	});
});
```

- [ ] **Step 3: Run to verify it fails**

Run: `cd web && pnpm vitest run src/lib/components/settings/notice-templates-section.test.ts`
Expected: FAIL — `insert-field-token` not found.

- [ ] **Step 4: Implement the helper**

```ts
// web/src/lib/components/settings/insert-field-token.ts
export function insertFieldToken(text: string, caret: number, field: string): { text: string; caret: number } {
	const token = `{{${field}}}`;
	const next = text.slice(0, caret) + token + text.slice(caret);
	return { text: next, caret: caret + token.length };
}
```

- [ ] **Step 5: Run to verify it passes**

Run: `cd web && pnpm vitest run src/lib/components/settings/notice-templates-section.test.ts`
Expected: PASS.

- [ ] **Step 6: Build the section component + mount it**

Create `web/src/lib/components/settings/NoticeTemplatesSection.svelte`:
- On mount, `notices.templates.list()` to get the five types with `availableFields` + current subject/body.
- Render one card per type: a header label, an **Auto-send / Ask first** toggle bound to the matching `autoSend*` settings field, and an expandable editor with a subject `<input>`, a body `<textarea>`, and a row of "insert field" buttons (one per `availableFields`, using `insertFieldToken` against the focused field).
- A "Save template" button → `notices.templates.upsert(type, { subject, body })`.
- The send-mode toggles save with the existing notification-settings save flow (the page already PUTs settings); include the five `autoSend*` fields in that payload.
- Show a hint "Add a template to enable auto-send" when a type has Auto-send on but `hasTemplate` is false.

Mount `<NoticeTemplatesSection />` in `web/src/routes/(protected)/settings/+page.svelte` within the notifications/automation area (locate the existing notification matrix section and place it directly below).

Use the friendly labels: Rent reminder (coming due) / Lease renewal offer / Convert to month-to-month / Lease expiration · move-out / Late rent · late fee.

- [ ] **Step 7: Typecheck + build**

Run: `cd web && pnpm check && pnpm build`
Expected: no type errors; build succeeds.

- [ ] **Step 8: Commit**

```bash
git add web/src/lib/types web/src/lib/api/endpoints/notices.ts web/src/lib/components/settings/ "web/src/routes/(protected)/settings/+page.svelte"
git commit -m "Web settings: notice template editor and per-type send-mode toggles"
```

---

### Task 4: Mobile Settings — notice templates editor + send-mode toggles

**Files:**
- Create: `mobile/lib/features/notices/notice_templates_repository.dart`
- Create: `mobile/lib/features/notices/notice_templates_screen.dart`
- Modify: `mobile/lib/features/settings/notification_settings_repository.dart` (add the 5 `autoSend*` fields to the settings model + JSON)
- Modify: `mobile/lib/features/settings/settings_screen.dart` (entry point to the templates/send-mode screen)
- Test: `mobile/test/notice_templates_repository_test.dart`

**Interfaces:**
- Produces: `NoticeTemplate` model `{ String noticeType; String subject; String body; bool hasTemplate; List<String> availableFields; }`; `NoticeTemplatesRepository` with `list()`, `upsert(type, {subject, body})`.
- Produces: the 5 `autoSend*` bool fields on the settings model used by `notification_settings_repository.dart`.

- [ ] **Step 1: Write the failing repository test**

```dart
// mobile/test/notice_templates_repository_test.dart
import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/notices/notice_templates_repository.dart';

void main() {
  test('list parses templates with available fields', () async {
    final adapter = _Adapter(jsonEncode([
      {'noticeType': 'LateRentNotice', 'subject': 'S', 'body': 'B', 'hasTemplate': true, 'availableFields': ['tenant_name', 'overdue_amount']},
    ]));
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))..httpClientAdapter = adapter;
    final repo = NoticeTemplatesRepository(dio);

    final list = await repo.list();

    expect(adapter.path, '/notices/templates');
    expect(list.single.noticeType, 'LateRentNotice');
    expect(list.single.availableFields, contains('overdue_amount'));
  });
}

class _Adapter implements HttpClientAdapter {
  _Adapter(this.body);
  final String body;
  String? path;
  @override
  Future<ResponseBody> fetch(RequestOptions o, Stream<Uint8List>? s, Future<void>? c) async {
    path = o.path;
    return ResponseBody.fromString(body, 200, headers: { Headers.contentTypeHeader: [Headers.jsonContentType] });
  }
  @override
  void close({bool force = false}) {}
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd mobile && flutter test test/notice_templates_repository_test.dart`
Expected: FAIL — repository not defined.

- [ ] **Step 3: Implement the model + repository**

```dart
// mobile/lib/features/notices/notice_templates_repository.dart
import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';

class NoticeTemplate {
  const NoticeTemplate({
    required this.noticeType,
    required this.subject,
    required this.body,
    required this.hasTemplate,
    required this.availableFields,
  });

  final String noticeType;
  final String subject;
  final String body;
  final bool hasTemplate;
  final List<String> availableFields;

  factory NoticeTemplate.fromJson(Map<String, dynamic> j) => NoticeTemplate(
        noticeType: j['noticeType'] as String? ?? '',
        subject: j['subject'] as String? ?? '',
        body: j['body'] as String? ?? '',
        hasTemplate: j['hasTemplate'] as bool? ?? false,
        availableFields: ((j['availableFields'] as List<dynamic>?) ?? const [])
            .whereType<String>()
            .toList(),
      );
}

class NoticeTemplatesRepository {
  NoticeTemplatesRepository(this._dio);
  final Dio _dio;

  Future<List<NoticeTemplate>> list() async {
    try {
      final res = await _dio.get<List<dynamic>>('/notices/templates');
      return (res.data ?? [])
          .whereType<Map<String, dynamic>>()
          .map(NoticeTemplate.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<NoticeTemplate> upsert(String type, {required String subject, required String body}) async {
    try {
      final res = await _dio.put<Map<String, dynamic>>(
        '/notices/templates/$type',
        data: {'subject': subject, 'body': body},
      );
      final data = res.data;
      if (data == null) {
        throw const ApiException(statusCode: 0, message: 'Empty response from server.');
      }
      return NoticeTemplate.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

final noticeTemplatesRepositoryProvider = Provider<NoticeTemplatesRepository>((ref) {
  return NoticeTemplatesRepository(ref.watch(dioProvider));
});
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd mobile && flutter test test/notice_templates_repository_test.dart`
Expected: PASS.

- [ ] **Step 5: Add send-mode fields to the settings model**

In `mobile/lib/features/settings/notification_settings_repository.dart`, add `autoSendRentReminder/autoSendRenewal/autoSendMonthToMonth/autoSendMoveOut/autoSendLateRent` (bool, default false) to the settings model class, its `fromJson` (`j['autoSendRentReminder'] as bool? ?? false`, etc.), its `toJson`/update payload, and `copyWith`. Follow the exact pattern the file already uses for `enableLateFees`/`notifyTenants`.

- [ ] **Step 6: Build the templates screen + settings entry**

Create `mobile/lib/features/notices/notice_templates_screen.dart`: a `ConsumerStatefulWidget` that loads `noticeTemplatesRepositoryProvider.list()` and the settings, then renders one expandable tile per type with:
- an **Auto-send / Ask first** `SwitchListTile` bound to the matching `autoSend*` setting (saved through the existing settings update flow),
- a subject `TextField`, a body `TextField` (multiline), and a `Wrap` of `ActionChip`s (one per `availableFields`) that insert `{{field}}` at the body cursor,
- a "Save template" button → `upsert(type, subject:, body:)`,
- a hint when Auto-send is on but `hasTemplate` is false: "Add a template to enable auto-send."

Add a `ListTile` ("Notice templates & auto-send") in `mobile/lib/features/settings/settings_screen.dart` that pushes `NoticeTemplatesScreen` (place it near the existing notifications row).

- [ ] **Step 7: Analyze**

Run: `cd mobile && flutter analyze lib/features/notices/notice_templates_repository.dart lib/features/notices/notice_templates_screen.dart lib/features/settings/notification_settings_repository.dart lib/features/settings/settings_screen.dart`
Expected: No issues found.

- [ ] **Step 8: Commit**

```bash
git add mobile/lib/features/notices/notice_templates_repository.dart mobile/lib/features/notices/notice_templates_screen.dart mobile/lib/features/settings/notification_settings_repository.dart mobile/lib/features/settings/settings_screen.dart mobile/test/notice_templates_repository_test.dart
git commit -m "Mobile settings: notice template editor and per-type send-mode toggles"
```

---

### Task 5: Full verification

- [ ] **Step 1: Backend** — `dotnet build RentalCommand.sln` then `dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter Notice` and `dotnet test RentalCommand.Engine.Tests/RentalCommand.Engine.Tests.csproj --filter Notice`. Expected: all PASS.
- [ ] **Step 2: Web** — `cd web && pnpm check && pnpm build && pnpm vitest run src/lib`. Expected: PASS.
- [ ] **Step 3: Mobile** — `cd mobile && flutter analyze && flutter test`. Expected: PASS.

---

## Self-Review

**Spec coverage (Plan 3 = auto-send + settings UI):**
- Per-type send mode exposed via API → Task 1. ✓
- Autonomous send, gated on active template + NotifyTenants → Task 2. ✓
- Settings template editor + send-mode toggles on web → Task 3; mobile → Task 4. ✓
- Auto-sent items remain visible/undoable → they become `Approved` drafts in the notices queue (existing list), and `dismiss` is unchanged. ✓
- "Add a template to enable auto-send" guidance → Tasks 3 & 4. ✓

**Placeholder scan:** No TBD/TODO. Tasks 1, 3, 4 reference existing files (settings service, web/mobile settings screens) with exact field names and clear anchors; the executing subagent reads each file first. Test-harness factories (`TestDb`/`EngineTestDb`) are called out where they must be added.

**Type consistency:** `AutoSend*` field names identical across Plan 1 (entity/migration), Task 1 (DTOs/service), and Task 2 (`AutoSendEnabled`). `NoticeTemplateResponse` shape matches Plan 1's DTO (`noticeType`, `subject`, `body`, `hasTemplate`, `availableFields`, `updatedAt`). Channel strings `Portal/Email/Sms` consistent with Plan 2 and the API normalizer. Notice type strings consistent across all three plans.
```

## Cross-plan note

Suppression of recurring notifications when a manual notice is sent is intentionally **out of scope** for all three plans (deferred per the spec). The recurring Engine jobs continue to run independently.
