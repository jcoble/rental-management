# Phase 4 — Automation + notifications — Build plan (2026-05-31)

Adapts the original phase-4 plan to the real codebase. Scope: the **operational automation backbone**
(rent charging, late fees, lease-expiry reminders) + **real notification channels** (Twilio/SendGrid),
all on the existing outbox. Deferred: Twilio inbound webhook, security-deposit holdings, lease-renewal
autopilot (LLM drafts) — noted at the end.

## Safety rules (unsupervised build)
- **Financial automations default OFF.** `Notifications.EnableRentCharges` and `EnableLateFees`
  default to `false`; nothing financial runs until the user opts in via config. Lease-expiry reminders
  (non-financial) default on.
- **Idempotency via DB, not flags alone.** Auto-generated rent/late-fee `Payment` rows carry a
  `PeriodKey` ("yyyy-MM"); a *filtered unique index* on `(LeaseId, PaymentType, PeriodKey) WHERE
  PeriodKey IS NOT NULL` makes double-charging impossible. Manual payments keep `PeriodKey = null` and
  are unaffected. The single-Engine advisory lock (59484) means no concurrent inserts.
- **Providers no-op without keys.** Twilio/SendGrid channels log-and-return when unconfigured (same
  behavior as today's `LoggingNotificationChannel`) — no exceptions, no retry storms.

## Verified integration points (from the map)
- `INotificationChannel` (`RentalCommand.Core/Interfaces`): `SendSmsAsync(toPhone, message, ct)`,
  `SendEmailAsync(toEmail, subject, body, ct)`. Current impl `LoggingNotificationChannel` (Engine,
  Singleton, line 28 of Engine `Program.cs`).
- Enqueue: `IMessagePublisher.PublishAsync<T>(portfolioId, messageType, payload, ct)` (Engine, Scoped)
  → writes `OutboxMessage`. `OutboxDispatchWorker` routes by `MessageType` "sms"/"email", reads Payload
  `{to, message|body, subject}`.
- `Payment`: PortfolioId, LeaseId, PaymentType (Rent/SecurityDeposit/LateFee/Utility/Other),
  Status (Scheduled/Paid/Partial/Late/Waived/Failed/Refunded), Amount, DueDate, PaidDate. No unique
  constraint today.
- `Lease`: Status (Draft/Active/NoticeGiven/Expired/Terminated/PendingSignature/Void), MonthlyRent,
  LateFeeAmount, RentDueDay (1–31), StartDate, EndDate, PropertyId, TenantId; soft-delete filter on
  DeletedAt. Navs: Property, Unit, Tenant.
- `Property.State` (US state, for late-fee caps). Owner contact: `Property.OwnerId` → `Owner.Phone` /
  `Owner.Email` (or `UserAccount.Email` where `OwnerId` matches). `Tenant.Email`/`Tenant.Phone` for
  tenant notices.
- Engine `Program.cs`: register config via `Configure<T>(GetSection(SectionName))`; workers via
  `AddHostedService<T>()`; services Scoped. `EngineWorkerBase` → override WorkerName/PollInterval/
  StepTimeout + `ExecuteCycleAsync(scoped, ct) → int`.
- Migration: `dotnet ef migrations add <Name> --project RentalCommand.Data --startup-project RentalCommand.Api`; applied on Api startup (`MigrateAsync`).
- Tests: `RentalCommand.Api.Tests/Scanning/ScanServiceTests.cs` has the inline `RentalCommandTestDbContext`
  (SQLite, remaps jsonb→TEXT, drops Postgres CKs). For Engine worker tests, lift that into
  `RentalCommand.TestCommon` (add `Microsoft.EntityFrameworkCore.Sqlite`).

## Task 1 — Foundation (schema + config + migration)  [DONE inline by lead/agent]
- `RentalCommand.Core/Configuration/NotificationsConfig.cs`: feature flags + lead/grace/reminder days +
  `NotifyTenants` + `TwilioOptions`/`SendGridOptions` (each with an `Enabled` computed from non-empty
  keys) + `Dictionary<string,LateFeeCap> StateLateFeeCaps` (LateFeeCap: `MaxPercentOfRent?`, `MaxFlat?`).
- `Payment.PeriodKey` (string?, maxlen 7). `Lease.ExpiryReminderSentAt` (DateTime?).
- DbContext: filtered unique index `(LeaseId, PaymentType, PeriodKey)` WHERE PeriodKey IS NOT NULL.
- Migration `Phase4Automation`.

## Task 2 — Notification channels (Twilio + SendGrid)
- `RentalCommand.Engine/Services/TwilioSmsSender.cs` + `SendGridEmailSender.cs` (HttpClient REST).
- `RentalCommand.Engine/Services/RoutingNotificationChannel.cs` implements `INotificationChannel`:
  SMS → Twilio if `Twilio.Enabled` else log; Email → SendGrid if `SendGrid.Enabled` else log.
- Replace the `LoggingNotificationChannel` registration with `RoutingNotificationChannel`.

## Task 3 — RentChargeService + RentChargeWorker  (gated EnableRentCharges)
- For each `Active` lease: periodKey = current "yyyy-MM"; dueDate = this month's `RentDueDay` (clamped to
  month length). When today ∈ [dueDate − RentChargeLeadDays, dueDate] and no Payment exists for
  (lease, Rent, periodKey): insert `Payment{ Type=Rent, Status=Scheduled, Amount=MonthlyRent, DueDate,
  PeriodKey }`. If `NotifyTenants` and tenant contact exists, enqueue a "rent due" notice.

## Task 4 — LateFeeService + LateFeeWorker  (gated EnableLateFees)
- For each unpaid rent Payment (Status ∈ {Scheduled, Late, Partial}) with `DueDate < today −
  LateFeeGraceDays`: if no LateFee Payment for (lease, LateFee, periodKey): fee = lease.LateFeeAmount,
  capped by `StateLateFeeCaps[property.State]` (min of MaxFlat and MaxPercentOfRent×rent when present);
  skip if fee ≤ 0. Insert `Payment{ Type=LateFee, Status=Scheduled, Amount=fee, DueDate=today,
  PeriodKey }`; mark the original rent Payment `Late`. Optionally notify.

## Task 5 — LeaseExpiryReminderService + Worker  (gated EnableLeaseExpiryReminders, default ON)
- For each `Active` lease with `EndDate ∈ [today, today + LeaseExpiryReminderDays]` and
  `ExpiryReminderSentAt == null`: enqueue an owner reminder; set `ExpiryReminderSentAt = now`.

## Task 6 — Integration (lead): DI + appsettings
- `Configure<NotificationsConfig>` in Api + Engine `Program.cs`. Swap notification channel. Register
  the three services (Scoped) + three workers (`AddHostedService`). Add a `Notifications` section to both
  `appsettings.json` (flags off, empty provider keys, example state caps commented).

## Task 7 — Tests
- Lift `RentalCommandTestDbContext` into `RentalCommand.TestCommon` (+ Sqlite pkg). Engine.Tests:
  rent-charge idempotency (run twice → one payment), late-fee cap math, period-key/clamp logic,
  expiry-reminder once-only.

## Deferred (documented for later, supervised)
- Twilio **inbound** webhook (`POST /api/sms/inbound`) + reply parsing ("yes" → confirm rent).
- `SecurityDepositHolding` entity + move-out deductions.
- **Lease Lifecycle Autopilot**: LLM-drafted renewal/late/move-out notices (`LeaseRenewalDraft`) for
  one-tap approval — uses `ChatAsync`; safe (drafts only) but larger; good next AI+automation tie-in.
- Web "Automation" settings/log surface.
