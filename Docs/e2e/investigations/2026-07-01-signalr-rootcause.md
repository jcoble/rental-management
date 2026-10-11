# SignalR Real-Time — Root Cause & Fix Plan (2026-07-01)

**Source:** SignalRDoctor agent (read-only). **Verdict:** transport, auth, client lifecycle, and
API-originated broadcasts all work; the symptom is a **confirmed structural gap** — the Engine
broadcasts into a no-op, so automation-driven changes never reach browsers.

## Root cause #1 (PRIME — structural, High): Engine→hub has no backplane; the Engine broadcaster is a no-op

`IDataUpdateService` has two impls:
- **API = real, hub-backed:** `RentalCommand.Api/Program.cs:273` registers `DataUpdateService`;
  `Clients.Group($"portfolio-{id}").SendAsync("EntityUpdated", …)` at `Api/Services/DataUpdateService.cs:37`.
- **Engine = no-op:** `RentalCommand.Engine/Program.cs:108` registers `EngineDataUpdateService`,
  whose methods just `return Task.CompletedTask` + Debug-log (`Engine/Services/EngineDataUpdateService.cs:27,41`).
  Its own XML doc says cross-process broadcasting (Redis backplane / HTTP notify) is "a deferred
  enhancement (Phase 5+)."

SignalR is **in-memory per-process; no backplane exists** (no `AddStackExchangeRedis`/Redis anywhere;
`Api/Program.cs:269` is a bare `AddSignalR().AddJsonProtocol(...)`). Engine services call
`_dataUpdate.BroadcastEntityUpdateAsync(...)` in good faith and it evaporates:
`RentChargeService.cs:213`, `LateFeeService.cs:260`, `LeaseExpiryReminderService.cs:176`, and
`RecurringMaintenanceService`, `RecurringExpenseGenerationService`, `AutopayChargeService`,
`NoticeDraftGenerationService`, `ScanProcessingWorker`, `DailyBriefing*`, `AccountingPullWorker`
(all Engine-hosted, `Engine/Program.cs:155-169`).

**Consequence:** every automation event (rent charges, late fees, recurring expense/maintenance,
autopay, lease-expiry reminders, notice drafts, scan-draft completion, accounting imports) writes to
the DB but **never pushes live**. Only the scan-review page survives — it independently **polls** while
a draft is `Pending` (`web/src/routes/(protected)/scan/[draftId]/+page.svelte:81`).

**What works (ruled in):** API-originated writes broadcast correctly (`PaymentService`,
`WorkOrderService`, `ApplicationService`, `ConversationService`, `UnitService`, …). A change you make
yourself arrives live — hence "flaky," not "dead."

**EdiPlatform comparison:** same process split; its bridge is an **API-hosted** `BackgroundService`
consuming RabbitMQ (`EdiPlatform.Api/Services/Messaging/NotificationConsumer.cs:12,157,190`) that
resolves the hub-backed `IDataUpdateService` and broadcasts. RC dropped RabbitMQ for a DB outbox, but
that outbox is consumed **in the Engine** (SMS/email/push only) — nothing was added in the **API** to
replace the API-hosted broadcaster. `EngineDataUpdateService` is the placeholder where the bridge belongs.

## Root cause #2 (wiring, Medium): notification bell gets no automation pushes and doesn't poll
`notificationStore` (`web/src/lib/stores/notifications.svelte.ts:27-31`) refreshes only on
`EntityUpdated`+`entityType==='Notification'` or on init/open/settings-save — **no `refetchInterval`**.
The only emitter of `EntityUpdated("Notification")` is the **API** `ConversationService.cs:397,445`.
Automation notifications are created in the **Engine** via `AutomationNotifier.cs:18-19` → broadcast is
the #1 no-op. Also `Api/Services/Domain/NotificationService.CreateBroadcastAsync:110` doesn't inject
`IDataUpdateService`. **Fixing #1 fixes most of #2 for free.**

## Root cause #3 (dead code, misleads debugging): `/api/v1/hubs/notifications` is unused
`NotificationHub` is mapped (`Api/Program.cs:448`) + `INotificationHubService` registered (`:274`), but
nothing calls it and no web client connects (web only builds `…/hubs/updates`, `web/src/lib/config.ts:27`).
Delete or wire it.

## Ruled out (verified fine)
- **Vite dev proxy:** `web/vite.config.ts:35` proxies `/api` with `ws:true`; negotiate + ws upgrade forward correctly.
- **Traefik (prod):** routes `/api/v1` → single API replica, auto-upgrades ws; CSP `connect-src` includes `wss://${DOMAIN}`. Single replica → no sticky sessions needed.
- **Auth:** token → `accessTokenFactory` (`signalr.ts:78-89`, refreshes within 60s of expiry); API accepts `?access_token=` for `/api/v1/hubs` (`Program.cs:187-195`); connection gated on `data.accessToken`.
- **Reconnect:** automatic-reconnect + backoff + background retry + visibility-wake (`(protected)/+layout.svelte:54-88`); group naming consistent (`portfolio-{id}`/`user-{id}`).
- Minor watch-item: hub derives group from the JWT `portfolioId` claim at connect — stale if client-side portfolio switching without a fresh token is ever added (out of scope).

## Fix plan
**Fix #1 — bridge (pick one; all mirror EdiPlatform's API-hosted broadcaster):**
- **Option A (RECOMMENDED, effort M, no new infra): Postgres `LISTEN/NOTIFY` + API-hosted broadcaster.**
  New API `BackgroundService` holds a dedicated Npgsql connection doing `LISTEN rc_entity_change`; on
  notify, calls the real hub-backed `IDataUpdateService`. `EngineDataUpdateService` becomes
  `NOTIFY rc_entity_change,'<json>'`. Payload = `portfolioId`+`entityType`+`entityId` (client only reads
  `data` for the `Unit→propertyId` key, `web/src/lib/realtime/invalidate-keys.ts:100`) — well under
  NOTIFY's ~8 KB limit. Fits "no RabbitMQ / small box / outbox-centric." Must handle listen-connection
  reconnect (pattern exists: the Engine's advisory-lock connection).
- Option B: outbox-tailer (API-side re-broadcaster) — more durable/replayable, more latency+code.
- Option C: Redis backplane — correct but unnecessary infra for a single-instance app. Not recommended.
- Option D: Engine→API HTTP broadcast call — adds attack surface + a hop. Not recommended.

**Fix #2 (bell, S):** mostly falls out of #1; add `IDataUpdateService` broadcast to
`NotificationService.CreateBroadcastAsync`; optionally a modest bell `refetchInterval` as belt-and-suspenders.
**Fix #3 (dead hub, S):** delete `NotificationHub` + `NotificationHubService` + the `:448` map, or wire it.

## Recommendation: FIX NOW, via Option A
The recent commits are all Phase-4 automation whose value is the landlord seeing it happen **live** — #1
is the single reason none of it does. Contained M, no new infra, and resolves most of #2. Sequence:
Option A → broadcast 2 representative Engine events (rent charge, late fee), confirm live arrival in a
second browser → flip the bell broadcast + delete the dead hub.

**Synergy note (orchestrator):** Option A introduces a general `LISTEN/NOTIFY` + API-hosted-broadcaster
pattern. The Master Simulation Clock currently uses a 1s poll for cross-process `SimulationClock` sync
(deliberately, since a dev tool tolerates 1s staleness); once this NOTIFY infra exists, the clock's
refresher *could* optionally subscribe to a `sim_clock` channel for instant sync — a minor optional
optimization, not required.

**Key files:** `Engine/Services/EngineDataUpdateService.cs` (the no-op), `Engine/Program.cs:108`,
`Api/Program.cs:269-274,448-449`, `Api/Services/DataUpdateService.cs` (reuse), 
`Api/Services/Domain/NotificationService.cs` (#2), `web/src/lib/stores/notifications.svelte.ts` +
`web/src/lib/realtime/invalidate-keys.ts` (#2 client). Reference: `EdiPlatform.Api/Services/Messaging/NotificationConsumer.cs`.
