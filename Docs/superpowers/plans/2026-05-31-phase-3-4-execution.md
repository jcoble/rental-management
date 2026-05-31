# Phase 3 + 4 — Adapted execution plan (2026-05-31)

The original phase-3/phase-4 plans were written against an idealized structure
(`LifecycleDbContext`, `api/Api/Core/...`, MCP `list_portfolios` grounding). This doc adapts
them to the **actual** codebase and scopes the autonomous run for safety + value.

## Codebase realities (verified)
- `ILlmProvider` (`RentalCommand.Core/Interfaces`) already has `ChatAsync(prompt, ct)` (returns
  `string.Empty` when no API key) + `ExtractAsync(...)`. No tool-calling method yet.
- `RentalCommandDbContext` (`RentalCommand.Data`) holds all entities. Migrations:
  `dotnet ef migrations add <Name> --project RentalCommand.Data --startup-project RentalCommand.Api`.
- `OutboxMessage` + `OutboxDispatchWorker` + `INotificationChannel` (sms/email) **already exist** —
  Phase 4's reliable-delivery core is in place.
- `EngineWorkerBase` (WorkerName / PollInterval / StepTimeout / `ExecuteCycleAsync`); register hosted
  services in `RentalCommand.Engine/Program.cs`.
- SignalR: `DataUpdateHub` (`/api/v1/hubs/updates`) + `IDataUpdateService.BroadcastEntityUpdateAsync`;
  `NotificationHub` (`/api/v1/hubs/notifications`) + `INotificationHubService`.
- Controllers extend `AuthenticatedPortfolioControllerBase` (`GetPortfolioId()` from JWT claim);
  domain services registered in `RentalCommand.Api/Extensions/ServiceCollectionExtensions.cs`.
- No `AiController` yet. Web `/ai` page is a "coming soon" stub; `web/src/lib/api/endpoints/ai.ts`
  has stub methods (`intake`, `portfolioSummary`, `generateNotice`).

## Scope decisions (autonomous, unsupervised)
- **Deliver Phase 3's two brains**: Daily Briefing (outbound) + Portfolio Q&A (inbound).
- **Ground Q&A directly in the DB** via the existing domain data (NOT the legacy MCP — CLAUDE.md
  says the MCP is legacy / to be repurposed later).
- **Phase 4 safe slice only**: deliver the Daily Briefing via the existing outbox + a SignalR/alert
  push (notifications). **Defer** financially-mutating automation (rent charging, late-fee
  assessment) — too risky to build unsupervised; documented as next step.
- LLM features degrade gracefully when no API key (rules text / "AI unavailable" instead of errors).

## Tasks
1. **Daily Briefing backend** — `BriefingDtos`, `IDailyBriefingService`/`DailyBriefingService`
   (rules engine over Leases/Payments/WorkOrders/Appointments/Inspections + optional LLM prose via
   `ChatAsync`), register, `AiController GET /api/v1/ai/briefing`.
2. **Tool-calling LLM** — add `ChatWithToolsAsync` to `ILlmProvider` + OpenAI/Anthropic impls
   (+ no-op fallback).
3. **Portfolio Q&A backend** — `IPortfolioQaService` (tool-call loop; tools query existing domain
   services / DB), `AiController POST /api/v1/ai/ask`.
4. **Web `/ai` page** — Daily Briefing card + Q&A chat box; `ai.ts` client (`briefing()`, `ask()`);
   types. Replace the stub page.
5. **Phase 4 slice** — `DailyBriefingWorker` (Engine): once/day per portfolio, compose briefing,
   enqueue `OutboxMessage` (sms/email) to the owner + push a SignalR alert; idempotent per
   (portfolio, date).
6. **Deferred (documented)**: voice capture, lease-FAQ bot, maintenance photo triage, rent-charge /
   late-fee / lease-expiry financial workers, Twilio inbound.

Each task: build (`dotnet build`) / `svelte-check` green, then commit. Per-phase review at the end.

---

## Status — 2026-05-31 (autonomous run)

**Delivered (Phase 3 — the two brains), merged to main:**
- Daily Briefing: `IDailyBriefingService`/`DailyBriefingService` (rules engine over leases/payments/
  work-orders/appointments/inspections + optional LLM prose) → `GET /api/v1/ai/briefing`.
- Tool-calling: `ChatWithToolsAsync` on `ILlmProvider` (OpenAI + Anthropic + no-op fallback).
- Portfolio Q&A: `IPortfolioQaService` with 6 read-only, portfolio-scoped DB tools →
  `POST /api/v1/ai/ask`.
- Web `/ai` page: Daily Briefing card + Q&A chat (degrades to an "AI off" banner with no key).
- End-of-phase review applied: blocked injected `system` history turns, capped Q&A input size,
  logged-not-leaked tool/LLM exceptions, added the inspection date lower bound.

**Deferred (Phase 4 + secondary Phase 3) — intentionally NOT built unsupervised:**
- **Financial automation** (RentChargeWorker, LateFeeWorker) — creates/mutates financial records;
  must be built with supervision + idempotency unique constraints + state-specific late-fee caps.
- **External delivery** (Twilio SMS / SendGrid email) — no provider keys configured, so any
  outbox-dispatch worker would enqueue messages that cannot send. The outbox table +
  `OutboxDispatchWorker` + `INotificationChannel` already exist; what's missing is provider
  config + a `DailyBriefingWorker`/`LeaseExpiryReminderWorker` that enqueue to it.
- **Engine → client SignalR push** is currently a no-op (`EngineDataUpdateService`); cross-process
  push must be wired before Engine workers can notify the web in real time.
- **Other Phase 3 features**: Voice capture, Lease-FAQ bot, Maintenance photo triage — larger,
  some need extra providers (speech-to-text); deferred.

**Recommended next (supervised):** 1) configure Twilio/SendGrid; 2) wire Engine→SignalR push;
3) `DailyBriefingWorker` (hourly poll, idempotent per portfolio+date via an outbox marker, enqueue
to owner email/SMS); 4) `LeaseExpiryReminderWorker` (safe, non-financial); 5) then the financial
workers with unique-constraint idempotency.
