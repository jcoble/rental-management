# Tenant Notices — Plan 2: Client On-Demand UX (mobile + web) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** From a tenant's page on mobile and web, let the landlord generate any of the five notice types on demand, **edit the subject/body before sending**, and send — using the Plan 1 backend.

**Architecture:** The per-tenant flow already calls `POST /notices/generate` then `POST /notices/{id}/approve`. This plan (a) expands the type menu to all five types, and (b) inserts an **edit** step: the review screen's subject/body become editable and persist via the existing `PATCH /notices/{id}` before approve.

**Tech Stack:** Flutter (Riverpod, Dio) for mobile; SvelteKit 5 runes + TanStack Query for web; xUnit-style Dart tests + Vitest.

## Global Constraints

- **Depends on Plan 1** (backend supports `RentReminder`, `MonthToMonthConversion`, and renders templates). Plan 1 must be merged/available first.
- Notice channels sent to `approve` are exactly `"Portal"`, `"Email"`, `"Sms"` (the API normalizes case).
- The five notice type names (exact strings): `RentReminder`, `RenewalOffer`, `MonthToMonthConversion`, `MoveOutReminder`, `LateRentNotice`.
- Mobile API calls go through `dioProvider`; web calls through `web/src/lib/api/client.ts` (`api`).
- No `Co-Authored-By`/AI-attribution trailer in commits.

---

### Task 1: Mobile — `NoticesRepository.update`

**Files:**
- Modify: `mobile/lib/features/notices/notices_repository.dart`
- Test: `mobile/test/notices_repository_update_test.dart`

**Interfaces:**
- Produces: `Future<NoticeDraft> NoticesRepository.update(int id, {String? subject, String? body})` → `PATCH /notices/{id}` returning the updated `NoticeDraft`.

- [ ] **Step 1: Write the failing test**

```dart
// mobile/test/notices_repository_update_test.dart
import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/notices/notices_repository.dart';

void main() {
  test('update PATCHes the notice and parses the response', () async {
    final adapter = _Adapter();
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final repo = NoticesRepository(dio);

    final draft = await repo.update(7, subject: 'New subj', body: 'New body');

    expect(adapter.method, 'PATCH');
    expect(adapter.path, '/notices/7');
    expect(adapter.data, containsPair('subject', 'New subj'));
    expect(adapter.data, containsPair('body', 'New body'));
    expect(draft.id, 7);
    expect(draft.subject, 'New subj');
  });
}

class _Adapter implements HttpClientAdapter {
  String? method;
  String? path;
  Map<String, dynamic>? data;

  @override
  Future<ResponseBody> fetch(RequestOptions options, Stream<Uint8List>? requestStream, Future<void>? cancelFuture) async {
    method = options.method;
    path = options.path;
    data = options.data as Map<String, dynamic>?;
    return ResponseBody.fromString(
      jsonEncode({
        'id': 7,
        'tenantName': 'Jordan Lee',
        'noticeType': 'RentReminder',
        'status': 'Draft',
        'subject': 'New subj',
        'body': 'New body',
        'reason': '',
        'triggerDate': '2026-06-29T00:00:00.000Z',
      }),
      200,
      headers: { Headers.contentTypeHeader: [Headers.jsonContentType] },
    );
  }

  @override
  void close({bool force = false}) {}
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd mobile && flutter test test/notices_repository_update_test.dart`
Expected: FAIL — `update` not defined.

- [ ] **Step 3: Implement `update`**

In `mobile/lib/features/notices/notices_repository.dart`, add this method to `NoticesRepository` (after `generateForTenant`):

```dart
  /// Edits a draft's subject/body before sending. PATCH /notices/{id}.
  Future<NoticeDraft> update(int id, {String? subject, String? body}) async {
    try {
      final response = await _dio.patch<Map<String, dynamic>>(
        '/notices/$id',
        data: {
          if (subject != null) 'subject': subject,
          if (body != null) 'body': body,
        },
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(statusCode: 0, message: 'Empty response from server.');
      }
      return NoticeDraft.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd mobile && flutter test test/notices_repository_update_test.dart`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add mobile/lib/features/notices/notices_repository.dart mobile/test/notices_repository_update_test.dart
git commit -m "Add mobile NoticesRepository.update for editing notice drafts"
```

---

### Task 2: Mobile — expand the notice type menu to five types

**Files:**
- Modify: `mobile/lib/features/notices/create_tenant_notice.dart:10` (`_noticeTypeChoices`) and the empty-state messaging at `:104`.

**Interfaces:**
- Consumes: `generateForTenant(tenantId, noticeType:)` (existing).
- Produces: a five-entry `_noticeTypeChoices` list.

- [ ] **Step 1: Replace `_noticeTypeChoices`**

In `mobile/lib/features/notices/create_tenant_notice.dart`, replace the `_noticeTypeChoices` constant (lines 10–29) with:

```dart
const _noticeTypeChoices = <({String type, String label, String hint, IconData icon})>[
  (
    type: 'RentReminder',
    label: 'Rent reminder (coming due)',
    hint: 'Friendly heads-up that rent is coming due.',
    icon: Icons.event_available_outlined,
  ),
  (
    type: 'RenewalOffer',
    label: 'Lease renewal offer',
    hint: 'Offer to extend the lease for another term.',
    icon: Icons.event_repeat_outlined,
  ),
  (
    type: 'MonthToMonthConversion',
    label: 'Convert to month-to-month',
    hint: 'Offer to continue month-to-month after the lease ends.',
    icon: Icons.sync_alt_outlined,
  ),
  (
    type: 'MoveOutReminder',
    label: 'Lease expiration / move-out',
    hint: 'Let them know the lease is ending and coordinate move-out.',
    icon: Icons.logout_outlined,
  ),
  (
    type: 'LateRentNotice',
    label: 'Late rent / late fee',
    hint: 'Remind the tenant about an overdue balance.',
    icon: Icons.warning_amber_outlined,
  ),
];
```

- [ ] **Step 2: Update the empty-result messaging for the new types**

In `showCreateTenantNoticeFlow`, replace the `why` computation (around line 104–107) with:

```dart
  if (drafts.isEmpty) {
    final why = switch (choice) {
      'LateRentNotice' => 'No overdue payment to base a late-rent notice on.',
      'RentReminder' || 'MonthToMonthConversion' =>
        'This tenant needs an active lease to create that notice.',
      _ => 'There is already an open draft of this notice for this tenant.',
    };
    messenger
      ..hideCurrentSnackBar()
      ..showSnackBar(SnackBar(content: Text(why)));
    return;
  }
```

- [ ] **Step 3: Analyze**

Run: `cd mobile && flutter analyze lib/features/notices/create_tenant_notice.dart`
Expected: No issues found.

- [ ] **Step 4: Commit**

```bash
git add mobile/lib/features/notices/create_tenant_notice.dart
git commit -m "Expand mobile tenant notice menu to all five types"
```

---

### Task 3: Mobile — editable review sheet (edit then send)

**Files:**
- Modify: `mobile/lib/features/notices/create_tenant_notice.dart` (`_ReviewNoticeSheet`, `:128`–`:304`)
- Test: `mobile/test/notice_review_edit_test.dart`

**Interfaces:**
- Consumes: `NoticesRepository.update` (Task 1), `NoticesRepository.approve` (existing).
- Produces: `_ReviewNoticeSheet` renders an editable Subject + Body `TextField` per draft; on Send, persists any edits via `update` before `approve`.

- [ ] **Step 1: Write the failing widget test**

```dart
// mobile/test/notice_review_edit_test.dart
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/notices/create_tenant_notice.dart';

void main() {
  testWidgets('review sheet shows editable subject and body fields', (tester) async {
    await tester.pumpWidget(
      const MaterialApp(
        home: Scaffold(
          body: ReviewNoticeSheetTestHarness(
            subject: 'Original subject',
            body: 'Original body',
          ),
        ),
      ),
    );

    // Subject + body are editable text fields pre-filled with the draft copy.
    expect(find.widgetWithText(TextField, 'Original subject'), findsOneWidget);
    expect(find.widgetWithText(TextField, 'Original body'), findsOneWidget);
    expect(find.text('Send notice'), findsOneWidget);
  });
}
```

NOTE: this test needs a small public test harness that renders the sheet's editable content without the full Riverpod/network stack. Add to `create_tenant_notice.dart` a lightweight public widget `ReviewNoticeSheetTestHarness({required String subject, required String body})` that builds the same editable Subject/Body `TextField`s (reusing the same field-building method) so the widget can be tested in isolation. If you prefer not to expose a harness, instead pump the real `_ReviewNoticeSheet` inside a `ProviderScope` with a one-draft list and assert the same fields; keep whichever is simpler in this codebase's existing test style (see `mobile/test/tabbed_form_sheet_test.dart`).

- [ ] **Step 2: Run to verify it fails**

Run: `cd mobile && flutter test test/notice_review_edit_test.dart`
Expected: FAIL — no editable fields / harness not defined.

- [ ] **Step 3: Make the review sheet editable**

In `_ReviewNoticeSheetState`:

1. Add controllers keyed by draft id:

```dart
  final Map<int, TextEditingController> _subjectCtrls = {};
  final Map<int, TextEditingController> _bodyCtrls = {};

  @override
  void initState() {
    super.initState();
    for (final d in widget.drafts) {
      _subjectCtrls[d.id] = TextEditingController(text: d.subject);
      _bodyCtrls[d.id] = TextEditingController(text: d.body);
    }
  }

  @override
  void dispose() {
    for (final c in _subjectCtrls.values) c.dispose();
    for (final c in _bodyCtrls.values) c.dispose();
    super.dispose();
  }
```

2. In `build`, replace the read-only `Text(d.subject)` / `Text(d.body)` card content with editable fields:

```dart
                      TextField(
                        controller: _subjectCtrls[d.id],
                        decoration: const InputDecoration(
                          labelText: 'Subject',
                          border: OutlineInputBorder(),
                        ),
                      ),
                      const SizedBox(height: 8),
                      TextField(
                        controller: _bodyCtrls[d.id],
                        minLines: 4,
                        maxLines: 10,
                        decoration: const InputDecoration(
                          labelText: 'Message',
                          border: OutlineInputBorder(),
                        ),
                      ),
```

3. In `_send`, persist edits before approving:

```dart
  Future<void> _send() async {
    final channels = _channels;
    if (channels.isEmpty) return;
    setState(() {
      _busy = true;
      _error = null;
    });
    final messenger = ScaffoldMessenger.of(context);
    try {
      for (final d in widget.drafts) {
        final subject = _subjectCtrls[d.id]?.text.trim() ?? d.subject;
        final body = _bodyCtrls[d.id]?.text.trim() ?? d.body;
        if (subject != d.subject || body != d.body) {
          await ref.read(noticesRepositoryProvider).update(d.id, subject: subject, body: body);
        }
        await ref.read(noticesRepositoryProvider).approve(d.id, channels);
      }
      ref.invalidate(noticeDraftsProvider);
      if (mounted) Navigator.of(context).pop();
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(const SnackBar(content: Text('Notice sent.')));
    } on ApiException catch (e) {
      setState(() => _error = e.message);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }
```

4. Add the `ReviewNoticeSheetTestHarness` public widget (or the ProviderScope alternative from Step 1) that renders the same Subject/Body `TextField`s for the widget test.

- [ ] **Step 4: Run to verify it passes**

Run: `cd mobile && flutter test test/notice_review_edit_test.dart`
Expected: PASS.

- [ ] **Step 5: Run the notices-related mobile tests**

Run: `cd mobile && flutter test test/notices_repository_update_test.dart test/notice_review_edit_test.dart`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add mobile/lib/features/notices/create_tenant_notice.dart mobile/test/notice_review_edit_test.dart
git commit -m "Make mobile notice review editable; persist edits before send"
```

---

### Task 4: Web — five-type menu + editable review

**Files:**
- Modify: `web/src/lib/tenants/tenant-notice-action.ts` (`FORCEABLE_NOTICE_TYPES`)
- Modify: `web/src/routes/(protected)/tenants/[id]/+page.svelte` (the create-notice menu + review UI)
- Test: `web/src/lib/tenants/tenant-notice-action.test.ts` (extend) and a component/integration test for the editable review if the page has a testable seam.

**Interfaces:**
- Consumes: `notices.generate(tenantId, noticeType)`, `notices.update(id, {subject, body})`, `notices.approve(id, {channels})` (all exist in `web/src/lib/api/endpoints/notices.ts`).
- Produces: web parity — five types selectable; review shows editable subject/body; Send persists edits via `update` then `approve`.

- [ ] **Step 1: Extend the forceable-types test**

In `web/src/lib/tenants/tenant-notice-action.test.ts`, add cases asserting the two new types are recognized:

```ts
it('recognizes the two new forceable notice types', () => {
	const reminder = readTenantNoticeAction(new URLSearchParams('action=create-notice&noticeType=RentReminder'));
	expect(reminder).toEqual({ noticeType: 'RentReminder' });

	const mtm = readTenantNoticeAction(new URLSearchParams('action=create-notice&noticeType=MonthToMonthConversion'));
	expect(mtm).toEqual({ noticeType: 'MonthToMonthConversion' });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd web && pnpm vitest run src/lib/tenants/tenant-notice-action.test.ts`
Expected: FAIL — new types fall through to `{}` because they aren't in `FORCEABLE_NOTICE_TYPES`.

- [ ] **Step 3: Add the new types to the forceable set**

In `web/src/lib/tenants/tenant-notice-action.ts`:

```ts
const FORCEABLE_NOTICE_TYPES = new Set([
	'RentReminder',
	'RenewalOffer',
	'MonthToMonthConversion',
	'MoveOutReminder',
	'LateRentNotice'
]);
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd web && pnpm vitest run src/lib/tenants/tenant-notice-action.test.ts`
Expected: PASS.

- [ ] **Step 5: Update the tenant page menu + review UI**

Open `web/src/routes/(protected)/tenants/[id]/+page.svelte`. Locate the existing "Create notice" menu (it lists notice types and calls `notices.generate(tenantId, type)`) and the review block that shows the generated draft(s) before approve.

5a. **Menu:** add menu entries for all five types with these labels (reuse the existing menu component/markup pattern):
- `RentReminder` → "Rent reminder (coming due)"
- `RenewalOffer` → "Lease renewal offer"
- `MonthToMonthConversion` → "Convert to month-to-month"
- `MoveOutReminder` → "Lease expiration / move-out"
- `LateRentNotice` → "Late rent / late fee"

5b. **Editable review:** for each generated draft, bind its `subject` and `body` to editable inputs (an `<input>` for subject and a `<textarea>` for body) using runes state, pre-filled from the generated draft. On **Send**, for each draft: if subject/body changed from the generated values, call `await notices.update(draft.id, { subject, body })` first, then `await notices.approve(draft.id, { channels })`. Use the existing channel selection + success/error handling already in the page.

Concrete send handler shape (adapt to the page's existing state names):

```ts
async function sendNotice(draft, subject, body, channels) {
	if (subject !== draft.subject || body !== draft.body) {
		await notices.update(draft.id, { subject, body });
	}
	await notices.approve(draft.id, { channels });
}
```

- [ ] **Step 6: Typecheck + build web**

Run: `cd web && pnpm check && pnpm build`
Expected: no type errors; build succeeds.

- [ ] **Step 7: Commit**

```bash
git add web/src/lib/tenants/tenant-notice-action.ts web/src/lib/tenants/tenant-notice-action.test.ts "web/src/routes/(protected)/tenants/[id]/+page.svelte"
git commit -m "Web: five-type tenant notice menu with editable review before send"
```

---

## Self-Review

**Spec coverage (Plan 2 = client on-demand UX):**
- Edit-before-send on mobile → Tasks 1, 3. ✓
- Edit-before-send on web → Task 4. ✓
- Full five-type menu (incl. the two new types) on mobile → Task 2; web → Task 4. ✓
- On-demand generation ignores timing windows → inherited from Plan 1 (`generate` forces type); clients just pass the type. ✓

**Placeholder scan:** No TBD/TODO. Task 3 Step 1 and Task 4 Step 5 reference existing large UI files; both give exact new code and a clear anchor ("locate the create-notice menu / review block"). The executing subagent reads the file first.

**Type consistency:** `update(id, {subject, body})` defined in Task 1 and used in Tasks 3 (mobile) and 4 (web). Notice type strings identical across Tasks 2/4 and Plan 1. `noticesRepositoryProvider`, `noticeDraftsProvider`, `approve` are existing names used as-is.
