# Phase 5 — Flutter mobile app — Plan (draft, 2026-05-31)

> For agentic workers: this is a planning doc to get ready for Phase 5. It is written to be executed
> task-by-task mostly by Sonnet sub-agents. Tasks use `- [ ]`. File paths are proposals.

**Goal:** A native iOS + Android app (Flutter) over the **same** `/api/v1` API, mobile-first around the
flagship **scan-to-record** capture, plus the Daily Briefing, Portfolio Q&A, and core day-to-day
management. The landlord runs the business from their phone — this is the primary client, on par with web.

**Why now:** Phases 0–4 give a complete API (auth, portfolio-scoped CRUD, scan pipeline, AI endpoints,
SignalR realtime, outbox notifications). The mobile app consumes it; only small API additions are needed
(device push-token registration). Mobile makes camera capture first-class (the "computer does the typing").

## Tech stack
- **Flutter 3.x / Dart 3.x**, Material 3 with a theme matching the web (EdiPlatform tokens).
- **State:** Riverpod (v2, code-gen) — providers per feature; async notifiers for queries/mutations.
- **HTTP:** Dio with an auth interceptor (Bearer + single-flight refresh) mirroring `web/src/lib/api/client.ts`.
- **Auth storage:** `flutter_secure_storage` (Keychain/Keystore) for access + rotated refresh tokens.
- **Realtime:** `signalr_netcore` client to `/api/v1/hubs/updates` + `/api/v1/hubs/notifications`
  (accessTokenFactory mirrors `web/src/lib/realtime/signalr.ts`; invalidate Riverpod providers on events).
- **Camera/capture:** `image_picker` + `camera`; PDF/image upload as multipart to `/scans`.
- **Push:** `firebase_messaging` (FCM; APNs on iOS). Device token registered with the API; the Phase 4
  notification path also fans out to push.
- **Routing:** `go_router` with auth-guarded route groups mirroring web `(protected)/(admin)/(portal)`.
- **Models/serialization:** `freezed` + `json_serializable`, generated from the API DTO shapes.
- **Testing:** widget tests + a few integration tests (golden flows: login, scan→confirm, ask).

## Project structure (`mobile/` at repo root — distinct from legacy `mcp/`)
```
mobile/
  lib/
    main.dart
    core/
      api/        dio_client.dart, auth_interceptor.dart, api_endpoints.dart, api_error.dart
      auth/       auth_repository.dart, auth_controller.dart, token_store.dart
      realtime/   signalr_service.dart, realtime_invalidation.dart
      models/     (freezed: portfolio, property, unit, lease, tenant, payment, expense,
                   work_order, appointment, scan_draft, briefing, ask_response, …)
      theme/      app_theme.dart (mirror web tokens), colors.dart
      router/     app_router.dart, guards.dart
    features/
      auth/       login_screen.dart, portfolio_selector.dart
      home/       dashboard_screen.dart
      scan/       capture_screen.dart, scan_list_screen.dart, scan_review_screen.dart
      ai/         briefing_screen.dart, qa_screen.dart
      properties/ tenants/ leases/ payments/ maintenance/ appointments/ portal/
    shared/       widgets (cards, buttons, fields, confidence chips, empty/error states)
  test/
  pubspec.yaml
  ios/ android/   (platform shells; FCM config, camera/photo permissions, deep links)
```

## API additions needed (small)
- `POST /api/v1/devices` — register a push token `{ token, platform }` (portfolio-scoped from JWT). New
  `DeviceToken` entity + controller + EF migration.
- Phase 4 `NotificationDispatch`/outbox: add a **push** channel alongside sms/email that sends via FCM to
  registered tokens (so briefings/alerts reach the phone). Reuses the outbox; add `MessageType "push"`.
- Everything else (auth, scans, ai, CRUD, SignalR) is consumed as-is.

## Tasks
- [ ] **T1 Scaffold**: `flutter create mobile`; add deps (riverpod, dio, go_router, freezed,
  json_serializable, flutter_secure_storage, signalr_netcore, image_picker, camera, firebase_messaging);
  set up Material 3 theme matching web; CI lint/build. (1 agent)
- [ ] **T2 API client + auth**: Dio client + auth interceptor (Bearer, 401→refresh via the same
  same-origin refresh contract, single-flight), `token_store` over secure storage, `auth_repository`
  (login/logout/me), `auth_controller` (Riverpod), portfolio selection. Mirror `web/src/lib/api/client.ts`
  + `hooks.server.ts` semantics. (1 agent)
- [ ] **T3 Models**: freezed/json models generated from the API DTOs for the entities listed above
  (source of truth: `web/src/lib/types/index.ts` + the API DTOs). (1 agent)
- [ ] **T4 Realtime**: signalr service + Riverpod invalidation bridge (EntityUpdated/EntityDeleted →
  refresh affected providers). (1 agent)
- [ ] **T5 Scan capture (flagship)**: camera/gallery capture → multipart upload to `/scans` → poll/realtime
  until `Reviewing` → **scan review screen** mirroring web (`scan/[draftId]`): grouped fields, confidence
  chips, line-items, Expense-vs-Payment routing, lease selector for rent checks, confirm/reject. The no-op
  ("AI off") banner too. (2 agents: capture+list, then review)
- [ ] **T6 Daily Briefing + Q&A**: briefing screen (severity-grouped bullets, AI summary) consuming
  `GET /ai/briefing`; Q&A chat consuming `POST /ai/ask` (example chips, "what I looked at"). (1 agent)
- [ ] **T7 Core management**: list+detail (and light create/edit) for properties/units, leases, tenants,
  payments (incl. mark-paid), work orders, appointments. Reuse shared widgets. (several agents, one per area)
- [ ] **T8 Push**: FCM setup; `POST /devices` on login; foreground/background handlers; tap→deep-link to
  the relevant screen. Backend: DeviceToken entity + endpoint + outbox push channel (FCM HTTP v1). (1–2 agents)
- [ ] **T9 Polish + release**: theming parity, empty/error/loading states, offline-tolerant caching for
  read screens, app icons/splash, TestFlight/Play internal build. (1 agent)

## Sequencing
T1 → T2 → (T3, T4 parallel) → T5 (flagship, prioritize) → T6 → T7 (parallel per area) → T8 → T9.
Each task: `flutter analyze` clean + the screen builds/runs against the dev API; widget tests for the
flagship flows. Keep the API contract identical to web (no mobile-only divergence except `/devices`).

---

## Status — 2026-05-31 (started, merged to main)
**Done + verified (`flutter analyze` clean, `flutter build web` succeeds):**
- API groundwork: `DeviceToken` entity + `POST`/`DELETE /api/v1/devices` + migration `AddDeviceTokens`.
- **T1 Scaffold** — `mobile/` (Flutter 3.44 / Dart 3.12), iOS/Android/web.
- **T2 API client + auth** — Dio client (debug self-signed cert), secure token store, auth interceptor
  (bearer + single-flight refresh on 401), Riverpod auth controller (sealed `AuthState`) + repository
  against `/api/v1/auth`, `restoreSession` on startup, login screen (dev fill), go_router auth guard,
  `NavigationBar` app shell (Home/Scan/Properties/AI/More), Material 3 theme.

**Decisions:** plain Riverpod (no codegen) + plain model classes (no freezed) for a simple, analyze-clean
toolchain; freezed can be adopted later. State via Riverpod 3 `Notifier`.

**API follow-up needed (small):** the `/api/v1/auth/refresh` endpoint is **cookie-only**; mobile currently
sends the stored refresh token as a `Cookie:` header and reads the rotated token from `Set-Cookie`
(works, but fragile). Recommended: add a **body-based refresh** option to `AuthController.Refresh` for
non-browser clients. Marked `TODO(api)` in the mobile auth code.

**Next (per the task list above):** T3 models → T4 realtime → **T5 scan capture (flagship)** → T6
briefing/Q&A → T7 management → T8 push (DeviceToken endpoint already exists; needs the FCM outbox channel)
→ T9 polish.
