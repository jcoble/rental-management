# Tenant Notices: on-demand menu, authored templates, and per-type auto-send

**Date:** 2026-06-29
**Status:** Approved design — ready for implementation plan
**Related tasks:** new task `38e394b0-689d-8142-af5d-ec7a79467b10` (on-demand menu + edit-before-send), `TSK-354` (per-type auto-send vs ask-first). Builds on `TSK-303` (per-tenant notice creation, DONE, PR #189). Adjacent: `TSK-201` (BYO lease template).

## Goal

Give the landlord one cohesive **tenant-notice system**:

1. **On demand**, from a tenant's page, generate any notice type, **edit** it, and send it.
2. Author a **good reusable template per notice type** whose dynamic fields auto-fill.
3. Per notice type, choose **autonomous send** vs **manual approval** ("ask me first"), so trusted types go out without the landlord touching them.

Built on the existing `NoticeDraft` + `ConversationService` (send) + `NoticeDraftWorker` (autopilot) machinery. Mobile **and** web.

**Out of scope (explicitly deferred):** suppression of the recurring/automatic notification when a manual notice is sent. The recurring Engine jobs (`RentChargeService`, `LateFeeService`, `LeaseExpiryReminderService`) keep running unchanged; double-contact is accepted for now.

## Current state (verified, file:line)

- **Notice letters** — entity `RentalCommand.Core/Entities/NoticeDraft.cs` (`NoticeType` is a free string; `ConversationId`, `ApprovedChannels`, `ApprovedAt`). Service `RentalCommand.Api/Services/Domain/NoticeDraftService.cs` generates only `RenewalOffer` (`:73`), `MoveOutReminder` (`:74`), `LateRentNotice` (`:137`); copy from `BuildDraftAsync` (~`:458`) using deterministic template fallback + optional LLM (`KnowledgeBase/notices.md`).
- **Endpoints** — `RentalCommand.Api/Controllers/NoticeDraftsController.cs`: `GET /notices` (`:23`), `POST /notices/generate` (`:38`, body `tenantId`+`noticeType?`), `PATCH /notices/{id}` (`:47`, edits subject/body — **already exists**), `POST /notices/{id}/approve` (`:64`), `POST /notices/{id}/dismiss` (`:77`).
- **Send path** — `approve` → `NoticeDraftService.ApproveAsync` (`:197`) → `ConversationService.StartAsync` → `FanOut` (`ConversationService.cs:512`) to Portal/Email/SMS; Fair-Housing gate can 422.
- **Autopilot** — `RentalCommand.Engine/Workers/NoticeDraftWorker.cs` (every 12h, runs unconditionally) → `NoticeDraftGenerationService` → **drafts only, never sends**.
- **Send-mode today** — `RentalCommand.Core/Entities/NotificationSettings.cs`: global toggles `EnableRentCharges`/`EnableLateFees`/`EnableLeaseExpiryReminders`/`NotifyTenants`. No per-type send mode.
- **Channel matrix** — `NotificationType` enum (`RentCharge`/`LateFee`/`LeaseExpiry`/`RentConfirmation`/`NoticeAutopilot`/`DailyBriefing`), `NotificationPreference` per `(PortfolioId, NotificationType)`, resolved via `NotificationsConfig.ResolveChannels` (`:71`).
- **Mobile** — `mobile/lib/features/notices/create_tenant_notice.dart`: type picker (`_noticeTypeChoices` `:10`, only 3 types); `_ReviewNoticeSheet` (`:128`) is **read-only** then Send/Keep-draft. Repo `notices_repository.dart`: `generateForTenant` (`:42`), `approve` (`:64`); **no `update`**.
- **Web** — `web/src/routes/(protected)/notices/+page.svelte`; per-tenant action `web/src/lib/tenants/tenant-notice-action.ts` / `tenant-notice-state.ts`; API client `web/src/lib/api/endpoints/notices.ts`.

## Notice types (final)

Five `NoticeDraft` types:

| Menu item | `NoticeType` | New? | Channel category |
|---|---|---|---|
| Rent reminder (coming due) | `RentReminder` | **new** | `RentCharge` |
| Lease renewal offer (extend a term) | `RenewalOffer` | exists | `LeaseExpiry` |
| Convert to month-to-month | `MonthToMonthConversion` | **new** | `LeaseExpiry` |
| Lease expiration / non-renewal | `MoveOutReminder` | exists | `LeaseExpiry` |
| Late rent / late fee | `LateRentNotice` | exists | `LeaseExpiry`/`LateFee` |

`RentReminder` is a *letter* type only — it reuses the existing `RentCharge` notification category for channel resolution; **no new matrix event** is added.

## Data model (Core + one migration)

- **`NoticeTemplate`** — `{ Id, PortfolioId, NoticeType (string), Subject (string), Body (string), IsActive (bool), CreatedAt, UpdatedAt }`. At most one **active** template per `(PortfolioId, NoticeType)` (filtered unique index on `IsActive = true`). `Subject`/`Body` contain merge tokens.
- **Send mode** — add per-type send mode to `NotificationSettings`: a `NoticeSendMode` enum `{ AskFirst, AutoSend }` per notice type (e.g. `AutoSendRentReminder`, `AutoSendRenewal`, `AutoSendMonthToMonth`, `AutoSendMoveOut`, `AutoSendLateRent`, defaulting to `AskFirst`). **Default every type to AskFirst** — the landlord opts into autonomy per type.
- **Merge-token catalog** — fixed, documented per type. Initial set: `{{tenant_name}}`, `{{property_address}}`, `{{unit_number}}`, `{{lease_start_date}}`, `{{lease_end_date}}`, `{{rent_amount}}`, `{{rent_due_date}}`, `{{overdue_amount}}`, `{{late_fee_amount}}`, `{{landlord_name}}`, `{{portfolio_name}}`. Unknown/unavailable tokens render to empty string and are surfaced to the editor as "available fields" so the landlord only inserts valid ones.

## Generation & render flow

`INoticeDraftService.GenerateAsync` gains a shared **`NoticeTemplateRenderer`**:

1. Resolve the active `NoticeTemplate` for `(portfolioId, noticeType)`.
2. **If present** — deterministically replace merge tokens from the lease/tenant/payment context (no LLM; predictable, fast). Produces `Subject` + `Body`.
3. **If absent** — fall back to the current system/LLM copy path (`BuildDraftAsync`), so behavior is unchanged until a template is authored.
4. Add generation context for the two new types (`RentReminder`: next rent period + amount; `MonthToMonthConversion`: current end date + MTM context). On-demand generation ignores the auto-generate timing windows.

The renderer is a standalone unit: input `(template, NoticeContext)` → output `(subject, body)`; testable in isolation; depends only on the token catalog.

## Edit-before-send

The freshly generated draft is shown with **editable Subject + Body**. On send: persist edits via existing `PATCH /notices/{id}`, then `POST /notices/{id}/approve` with the chosen channels.

- **Mobile** — make `_ReviewNoticeSheet` fields editable (`TextFormField` for subject/body); add `NoticesRepository.update(id, {subject, body})` → `PATCH /notices/{id}`; expand `_noticeTypeChoices` to all five types.
- **Web** — same editable review in the per-tenant notice flow (`tenant-notice-action.ts` / state), all five types.

## Auto-send (TSK-354)

Extend `NoticeDraftWorker` / generation so that for a type set to `AutoSend` it **generates and sends** automatically:

- **Guard:** AutoSend is honored **only when an active `NoticeTemplate` exists** for that type. Otherwise it degrades to a draft (the "autonomous *after* a good template" rule). Also respects `NotifyTenants` and the existing grace/lead windows.
- **Send:** reuse the approve → `ConversationService.FanOut` path on the type's resolved channels (`ResolveChannels(category)`); honor the Fair-Housing gate (if blocked, leave as draft, never auto-send a flagged notice).
- **Audit/visibility:** mark auto-sent drafts with a source/`ApprovedAt` so they appear in the notices queue with a clear "auto-sent" indicator and can be reviewed; dismiss/undo remains available.

## UI surfaces

- **Per-tenant (Piece 1)** — "Create notice" → all five types → generate → **editable** review → Send / Keep draft. Mobile `create_tenant_notice.dart`; web tenant page.
- **Settings → Notice templates (Pieces 2 + 3)** — one row per notice type with **[Edit template]** (Subject/Body editor + "insert field" from the type's token catalog) and a **[Auto-send | Ask first]** toggle. Mobile + web. Default toggles to Ask-first; a type with Auto-send on but no template shows a "needs a template to auto-send" hint.

## API surface

- `GET /notices/templates` — list templates for the portfolio.
- `GET /notices/templates/{type}` — fetch the active template (or the catalog + empty template) for a type.
- `PUT /notices/templates/{type}` — create/update the active template `{ subject, body }`.
- Send-mode fields added to the existing notification-settings DTO/endpoints.
- `POST /notices/generate`, `PATCH /notices/{id}`, `POST /notices/{id}/approve` — unchanged in shape; `generate` now renders templates and supports `RentReminder` + `MonthToMonthConversion`.

All template/settings endpoints are portfolio-scoped (Admin/staff), consistent with `NoticeDraftsController`.

## Components & boundaries

| Unit | Responsibility | Depends on |
|---|---|---|
| `NoticeTemplate` entity + migration | Persist authored templates + send-mode | EF Core |
| `NoticeTemplateRenderer` | `(template, context) → (subject, body)` token fill | token catalog only |
| `NoticeContext` builder | Gather lease/tenant/payment fields for a (tenant, type) | DbContext |
| `INoticeDraftService.GenerateAsync` (extended) | Template-or-fallback + new types | renderer, context builder |
| `NoticeTemplatesController` + service | Template CRUD | DbContext |
| Send-mode resolution | Per-type AskFirst/AutoSend + active-template guard | NotificationSettings, templates |
| `NoticeDraftWorker` (extended) | Auto-send AutoSend types | send-mode resolution, approve/fan-out |
| Mobile/web per-tenant flow | Menu + editable review + send | generate/PATCH/approve |
| Mobile/web settings | Template editor + send-mode toggles | template + settings endpoints |

## Error handling

- Template render with missing data → blank token, never throws; editor warns which fields are unavailable for a given tenant context.
- Auto-send with no active template → silently degrade to draft (logged), do not send.
- Fair-Housing block on auto-send → leave as draft, surface for manual review.
- Empty/no-applicable generation (e.g. late-rent with no overdue balance) → same "nothing applied" messaging as today (`create_tenant_notice.dart:104`), extended for new types.

## Testing

- **Unit:** `NoticeTemplateRenderer` (token fill across all five types, missing tokens, unknown tokens); template-vs-fallback selection in `GenerateAsync`; send-mode resolution (AutoSend requires active template); `NoticeDraftWorker` auto-send path (sends when AutoSend+template, drafts otherwise).
- **API:** template CRUD (portfolio scoping, single-active constraint); generate for the two new types.
- **Mobile/web widget:** editable review sheet persists edits before approve; template editor inserts fields and saves; send-mode toggle round-trips.

## Decisions locked in brainstorm

- Reuse existing types where possible: late-fee = `LateRentNotice`; expiration = `MoveOutReminder`; rent reminder = letter under `RentCharge` category (no new matrix event).
- New types: `MonthToMonthConversion` (and `RentReminder` as a letter type).
- On-demand generation ignores timing windows.
- Template fill is **deterministic token replacement (no LLM)**.
- Auto-send is **gated on an active template existing**; default send mode is **AskFirst** for every type.
- Suppression of recurring notifications is **deferred**.

## Open items to settle during planning

- Exact storage shape for per-type send mode (individual bool/enum columns on `NotificationSettings` vs a small child table). Lean: explicit enum columns per type for simplicity and migration clarity.
- Final token catalog per type and the proposed-value fields that aren't in data (e.g. new term length, MTM rent) — handled as editable template literals rather than tokens.
- Whether the standalone `/notices` queue stays as-is (review queue) — keep; no change required by this spec.
