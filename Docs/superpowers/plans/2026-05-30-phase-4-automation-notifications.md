# Phase 4 — Operational automation + notifications + lease lifecycle — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Build automated rent charging, late-fee assessment, lease-lifecycle watch, SMS-first notifications (Twilio inbound/outbound), and a DB-backed outbox worker that drives reliable email/SMS delivery with retry and human-approval gates throughout.

**Architecture:** Four Postgres advisory-lock-protected workers (RentChargeWorker, LateFeeWorker, LeaseExpiryReminderWorker, NotificationDispatchWorker) poll a DB-backed `OutboxMessage` table, each achieving financial-correctness via unique constraints and idempotent payloads. `INotificationChannel` abstraction (email via SendGrid, SMS via Twilio) sends from the outbox; Twilio inbound webhook (`POST /api/sms/inbound`) parses replies and can trigger payment confirmation (LLM interprets "yes/yep/ok"). Phase 3's notice generator is wired in here; Lease Lifecycle Autopilot uses the same LLM + SignalR to draft renewal/late/move-out notices for one-tap approval. SignalR real-time updates broadcast to web/mobile on `portfolio-{portfolioId}` groups. Security deposits transition from `Lease.SecurityDeposit` to a `SecurityDepositHolding` entity (amount, status, itemized deductions) for Phase 7 move-out handling.

**Tech Stack:** .NET 10 (RentalCommand.Engine BackgroundServices), PostgreSQL advisory locks + unique constraints, SendGrid API, Twilio SMS API (inbound + outbound), SignalR hubs (NotificationHub, DataUpdateHub), EF Core migrations, xUnit + Playwright integration tests. Expense.Category → ScheduleECategory enum (not yet, reserved for Phase 7; Phase 4 keeps string category + adds enum conversion mapping for automation triage). Configuration via `appsettings.json` (SendGrid API key, Twilio AccountSid/AuthToken, SMS grace period, late-fee caps by state, renewal-reminder offset).

**Depends on:** Phase 0 (re-platform: Postgres, EF Core, JWT/refresh auth, SignalR, RentalCommand namespace, advisory-lock infrastructure), Phase 1 (upload pipeline / StoredFile / polymorphic attachments), Phase 2 (ScanDraft, ILlmProvider, LLM extraction), Phase 3 (real Anthropic provider, notice generation, lease reading). Phases 3 and 4 share the LLM core and can be built in parallel once Phase 2 is firm.

---

## File / project structure

**New Entities (RentalCommand.Data/Entities):**
- `OutboxMessage.cs` — DB-backed queue for reliable SMS/email send; Status { Pending, Sent, Failed }; RetryCount; FailureReason; CreatedAt; ProcessedAt; PortfolioId scoped.
- `SecurityDepositHolding.cs` — tracks deposit amount, status { Held, PartiallyReturned, Returned }, held-on-lease, itemized deductions (JSON array { reason, amount, photos[] }), processed date.
- `LeaseRenewalDraft.cs` — AI-generated renewal offer for one-tap approval; Status { Draft, Approved, Sent, Executed }; new-term-end, new-rent, escalation %, generated-prompt, generated-notice (long text), ExternalReference (Twilio message SID if sent).

**New Enums (RentalCommand.Data/Enums):**
- `OutboxMessageStatus.cs` — Pending, Sent, Failed.
- `OutboxMessageChannel.cs` — Email, Sms.
- `NotificationType.cs` — RentDue, RentOverdue, LateFeeAssessed, LeaseExpiring, RenewalProposal, WorkOrderUpdate, AppointmentReminder, MaintenanceConfirmed, Reply (inbound).
- `SecurityDepositStatus.cs` — Held, PartiallyReturned, Returned.
- `ScheduleECategory.cs` — Advertising, AutoTravel, CleaningMaintenance, Commissions, Insurance, LegalProfessional, ManagementFees, MortgageInterest, Repairs, Supplies, Taxes, Utilities, Depreciation, Other (for Phase 7; Phase 4 reserves the enum and stores the mapping).

**New Interfaces (RentalCommand.Core/Services):**
- `INotificationChannel.cs` — `SendEmailAsync(to, subject, body, attachments, portfolioId, ct)` → `OutboxMessage`; `SendSmsAsync(to, body, portfolioId, replyCallbackKey, ct)` → `OutboxMessage` with `InboundWebhookKey`.
- `IOutboxService.cs` — `EnqueueAsync(message, channel, recipient, subject?, body, portfolioId, ct)`; `MarkProcessedAsync(id, ct)`.
- `IRentChargeService.cs` — `GenerateRentChargesAsync(ct)` → processes active leases, creates Scheduled payments idempotently, returns count.
- `ILateFeeService.cs` — `AssessLateFeesAsync(gracePeriodDays, ct)` → finds overdue leases, applies late fees per state caps, enqueues outbox notice, returns count.
- `ILeaseExpiryService.cs` — `ProcessExpiryRemindersAsync(daysBefore, ct)` → finds leases ending ≤N days, enqueues reminders, returns count.
- `ILeaseLifecycleAutopilotService.cs` — `DraftRenewalNoticeAsync(leaseId, ct)` → reads lease + tenant, LLM-drafts renewal offer, saves LeaseRenewalDraft, signals approval UI, returns draft; `DraftLateNoticeAsync(leaseId, escalationLevel, ct)` → escalating copy; `DraftMoveOutReminderAsync(leaseId, ct)`.
- `ITwilioInboundService.cs` — `ProcessInboundSmsAsync(from, body, messageId, accountSid, ct)` → parses, matches to portfolio + outbox context, triggers reply handlers (rent confirm, work-order status, etc.).

**New Services (RentalCommand.Api/Services):**
- `SendGridNotificationChannel.cs` — `INotificationChannel` impl; calls SendGrid API; wraps response in OutboxMessage.
- `TwilioNotificationChannel.cs` — `INotificationChannel` impl; calls Twilio Send API; enqueues OutboxMessage + webhook key for inbound routing.
- `OutboxService.cs` — implements `IOutboxService`; transactional enqueue; marks sent/failed.
- `RentChargeService.cs` — query active leases, calculate next-due-date per RentDueDay, check for existing Payment (idempotent via lease+period unique constraint), insert Scheduled Payment, enqueue rent-due notice, set advisory lock scoped to lease.
- `LateFeeService.cs` — query leases with overdue Payments, state-aware late-fee caps (CA: can't exceed 6% or 1 day's rent; TX: can't exceed 10% or rent amount; varies by state in config), insert late-fee Payment, enqueue notice, idempotent via lease+month constraint.
- `LeaseExpiryService.cs` — query leases ending in next N days (default 60), enqueue reminders.
- `LeaseLifecycleAutopilotService.cs` — uses ILlmProvider to read lease + tenant data, draft renewal/late/move-out notices with grounding (property address, tenant name, current rent, new terms), saves draft, calls SignalR to wake approval UI.
- `TwilioInboundService.cs` — parse webhook signature, match inbound SMS to portfolio + outbox context, trigger reply handlers.

**New Worker (RentalCommand.Engine/Workers):**
- `RentChargeWorker.cs` — inherits `EngineWorkerBase`; calls `IRentChargeService.GenerateRentChargesAsync()` under advisory lock.
- `LateFeeWorker.cs` — inherits `EngineWorkerBase`; calls `ILateFeeService.AssessLateFeesAsync()` under advisory lock.
- `LeaseExpiryReminderWorker.cs` — inherits `EngineWorkerBase`; calls `ILeaseExpiryService.ProcessExpiryRemindersAsync()`.
- `NotificationDispatchWorker.cs` — inherits `EngineWorkerBase`; polls OutboxMessage with Status=Pending; calls `INotificationChannel.SendAsync()`; on success marks Sent + ProcessedAt; on failure increments RetryCount (retry up to 5x over 24h); on final failure marks Failed + FailureReason.

**New Controllers (RentalCommand.Api/Controllers):**
- `SmsInboundController.cs` — `POST /api/sms/inbound` → validates Twilio signature via `ApiKeyAuthenticationHandler`, calls `ITwilioInboundService.ProcessInboundSmsAsync()`, returns 200 OK (empty).

**New SignalR Hubs (RentalCommand.Api/Hubs):**
- `NotificationHub.cs` — clients subscribe to group `portfolio-{portfolioId}`; server broadcasts rent-due, late-fee, lease-expiry, work-order, appointment events + Lease Autopilot drafts (renewal/late/move-out) for approval.
- `DataUpdateHub.cs` (extends Phase 0's realtime) — broadcasts Payment.Status changes, Lease updates, SecurityDepositHolding adjustments.

**New DTOs (RentalCommand.Api/Dtos):**
- `OutboxMessageDto.cs` — id, channel, recipient, subject, body, status, retryCount, createdAt.
- `RentChargeResultDto.cs` — chargesCreated, noticesEnqueued, errors[].
- `TwilioWebhookDto.cs` — from, to, body, messageId, accountSid, signature.
- `LeaseRenewalDraftDto.cs` — id, leaseId, draftNotice, newTermEndDate, newRentAmount, escalationPercent, status, createdAt, approvedBy?, approvedAt?.
- `SmsReplyContextDto.cs` — messageId, portfolioId, leaseId?, workOrderId?, notificationType, originalOutboxId.

**Modified Entities:**
- `Lease.cs` — add `LeaseRenewalDraftId?`, remove `SecurityDeposit` (move to SecurityDepositHolding in Phase 7 but keep for now as bridge), add `IsTenantTwoWay` (opt-in SMS + reply support).
- `Payment.cs` — add `OutboxMessageId?` (link to the notice that prompted it), `ConfirmedByReplyTo?` (Twilio MessageSid if "yes" reply).
- `RentalCommandDbContext.cs` — add DbSet `OutboxMessage`, `SecurityDepositHolding`, `LeaseRenewalDraft`; add unique constraints: `(LeaseId, YearMonth)` on Payment for idempotent rent, `(LeaseId, Month)` on late-fee Payment.

**Modified Controllers:**
- `PaymentController.cs` — add `PATCH /api/payments/{id}/mark-paid-by-reply` (idempotent, used by inbound SMS handler); add filtering for pending scheduled vs confirmed.

**Configuration (appsettings.json):**
```json
{
  "SendGridApiKey": "SG...",
  "TwilioAccountSid": "AC...",
  "TwilioAuthToken": "...",
  "TwilioPhoneNumber": "+1234567890",
  "NotificationSettings": {
    "RentGracePeriodDays": 5,
    "LateFeeGracePeriodDays": 2,
    "LeaseExpiryReminderDaysBefore": 60,
    "OutboxRetryMaxAttempts": 5,
    "OutboxRetryIntervalMinutes": 10,
    "StateLateFeeCaps": {
      "CA": { "MaxPercent": 6, "MaxDaysRent": 1 },
      "TX": { "MaxPercent": 10, "MaxDaysRent": null },
      "NY": { "MaxPercent": 5, "MaxDaysRent": 5 }
    }
  }
}
```

---

## Tasks

### Task 1: Add entities, enums, and DbContext updates

**Files:**
- Create: `RentalCommand.Data/Entities/OutboxMessage.cs`
- Create: `RentalCommand.Data/Entities/SecurityDepositHolding.cs`
- Create: `RentalCommand.Data/Entities/LeaseRenewalDraft.cs`
- Create: `RentalCommand.Data/Enums/OutboxMessageStatus.cs`
- Create: `RentalCommand.Data/Enums/OutboxMessageChannel.cs`
- Create: `RentalCommand.Data/Enums/NotificationType.cs`
- Create: `RentalCommand.Data/Enums/SecurityDepositStatus.cs`
- Create: `RentalCommand.Data/Enums/ScheduleECategory.cs`
- Modify: `RentalCommand.Data/RentalCommandDbContext.cs` (add DbSets, constraints, conversions)
- Modify: `RentalCommand.Data/Entities/Lease.cs` (add LeaseRenewalDraftId?, IsTenantTwoWay)
- Modify: `RentalCommand.Data/Entities/Payment.cs` (add OutboxMessageId?, ConfirmedByReplyTo?)

**Steps:**

- [ ] Create `OutboxMessage.cs`: public int Id; int PortfolioId; string Channel (email/sms); string Recipient; string? Subject; string? Body; string Status (enum); int RetryCount; string? FailureReason; string? ExternalRef (Twilio SID / SendGrid MessageId); DateTime CreatedAt; DateTime? ProcessedAt; string? InboundWebhookKey (for SMS reply routing); Portfolio nav.

- [ ] Create `SecurityDepositHolding.cs`: int Id; int PortfolioId; int LeaseId; decimal Amount; string Status (enum); List<{ Reason, Amount, Photos[] }> Deductions (as JSON); DateTime HeldOnDate; DateTime? ReturnedOnDate; string? Notes; Portfolio, Lease navs.

- [ ] Create `LeaseRenewalDraft.cs`: int Id; int PortfolioId; int LeaseId; string Status (Draft/Approved/Sent/Executed); DateTime? NewTermEndDate; decimal? NewRentAmount; decimal? EscalationPercent; string GeneratedPrompt; string GeneratedNotice (the renewal letter); string? ExternalReference (Twilio message ID if sent); int? ApprovedBy (ApplicationUserId); DateTime? ApprovedAt; DateTime CreatedAt; Portfolio, Lease navs.

- [ ] Create enums: `OutboxMessageStatus.cs` (Pending, Sent, Failed); `OutboxMessageChannel.cs` (Email, Sms); `NotificationType.cs` (RentDue, RentOverdue, LateFeeAssessed, LeaseExpiring, RenewalProposal, WorkOrderUpdate, AppointmentReminder, MaintenanceConfirmed, Reply); `SecurityDepositStatus.cs` (Held, PartiallyReturned, Returned); `ScheduleECategory.cs` (all 14 IRS Schedule E categories).

- [ ] Modify `RentalCommandDbContext.cs`: add `public DbSet<OutboxMessage> OutboxMessages { get; set; }`, `public DbSet<SecurityDepositHolding> SecurityDepositHoldings { get; set; }`, `public DbSet<LeaseRenewalDraft> LeaseRenewalDrafts { get; set; }`.

- [ ] In `OnModelCreating()`: add unique constraint `modelBuilder.Entity<Payment>().HasIndex(p => new { p.LeaseId, p.PaymentType, p.DueDate }).IsUnique().HasName("IX_Payment_Idempotent_RentCharge")` (idempotent rent per lease+type+due-date); add unique constraint on late fees similarly. Add `modelBuilder.Entity<OutboxMessage>().HasIndex(o => o.Status).HasName("IX_OutboxMessage_Pending")` for fast pending queries. Add HasConversion for all new enums.

- [ ] Modify `Lease.cs`: add `public int? LeaseRenewalDraftId { get; set; }`, `public LeaseRenewalDraft? LeaseRenewalDraft { get; set; }`, `public bool IsTenantTwoWay { get; set; } = false`.

- [ ] Modify `Payment.cs`: add `public int? OutboxMessageId { get; set; }`, `public string? ConfirmedByReplyTo { get; set; }` (Twilio MessageSid of the "yes" reply).

- [ ] Run `dotnet ef migrations add Phase4_AutomationNotificationsAndLeaseLifecycle` in RentalCommand.Api; verify migration creates tables + constraints.

- [ ] Build & verify no compile errors.

### Task 2: Create core service interfaces

**Files:**
- Create: `RentalCommand.Core/Services/INotificationChannel.cs`
- Create: `RentalCommand.Core/Services/IOutboxService.cs`
- Create: `RentalCommand.Core/Services/IRentChargeService.cs`
- Create: `RentalCommand.Core/Services/ILateFeeService.cs`
- Create: `RentalCommand.Core/Services/ILeaseExpiryService.cs`
- Create: `RentalCommand.Core/Services/ILeaseLifecycleAutopilotService.cs`
- Create: `RentalCommand.Core/Services/ITwilioInboundService.cs`

**Steps:**

- [ ] Create `INotificationChannel.cs`: `Task<OutboxMessage> SendEmailAsync(string to, string subject, string body, List<(string filename, Stream data)>? attachments, int portfolioId, CancellationToken ct)` → OutboxMessage with Channel=Email, ExternalReference=SendGrid MessageId on success. `Task<OutboxMessage> SendSmsAsync(string to, string body, int portfolioId, string? replyCallbackKey, CancellationToken ct)` → OutboxMessage with Channel=Sms, ExternalReference=Twilio MessageId, InboundWebhookKey=replyCallbackKey. Both transactional.

- [ ] Create `IOutboxService.cs`: `Task<OutboxMessage> EnqueueAsync(string channel, string recipient, string? subject, string body, int portfolioId, NotificationType notificationType, string? contextKey, CancellationToken ct)` → creates OutboxMessage with Status=Pending, returns it. `Task MarkProcessedAsync(int outboxMessageId, string? externalRef, CancellationToken ct)` → marks Sent + ProcessedAt. `Task MarkFailedAsync(int id, string reason, CancellationToken ct)` → marks Failed + increments RetryCount.

- [ ] Create `IRentChargeService.cs`: `Task<(int created, int errors)> GenerateRentChargesAsync(CancellationToken ct)` → returns tuple (count of new rent payments created, error count).

- [ ] Create `ILateFeeService.cs`: `Task<(int assessed, int errors)> AssessLateFeesAsync(int gracePeriodDays, CancellationToken ct)` → returns (late fees assessed, errors).

- [ ] Create `ILeaseExpiryService.cs`: `Task<int> ProcessExpiryRemindersAsync(int daysBefore, CancellationToken ct)` → returns count of reminders enqueued.

- [ ] Create `ILeaseLifecycleAutopilotService.cs`: `Task<LeaseRenewalDraft> DraftRenewalNoticeAsync(int leaseId, CancellationToken ct)` → reads Lease + Tenant, calls LLM with grounding, saves draft, signals UI, returns it. `Task<(string notice, decimal newRent, DateTime newEnd)> DraftLateNoticeAsync(int leaseId, int escalationLevel, CancellationToken ct)` → Level 1: "rent is overdue," 2: "payment required by X," 3: "eviction notice." `Task<string> DraftMoveOutReminderAsync(int leaseId, CancellationToken ct)` → lease-end date checklist (security deposit return, utility transfer, final walkthrough).

- [ ] Create `ITwilioInboundService.cs`: `Task ProcessInboundSmsAsync(string from, string body, string messageId, string accountSid, string signature, CancellationToken ct)` → validates Twilio signature, matches sender to tenant/vendor, parses intent, triggers handler (rent confirm, work-order update, etc.). Returns Task (fire-and-forget; error logged).

- [ ] Build & verify.

### Task 3: Create and register notification channels

**Files:**
- Create: `RentalCommand.Api/Services/Notifications/SendGridNotificationChannel.cs`
- Create: `RentalCommand.Api/Services/Notifications/TwilioNotificationChannel.cs`
- Create: `RentalCommand.Api/Services/Notifications/OutboxService.cs`
- Modify: `RentalCommand.Api/Program.cs` (register services)

**Steps:**

- [ ] Create `SendGridNotificationChannel.cs`: inject HttpClient + ILogger + config (SendGridApiKey); `SendEmailAsync()` calls SendGrid API `POST /v3/mail/send` with from (noreply@rentalcommand.app), to, subject, body (HTML + plain text), attachments. On 202 Accepted, extract X-Message-Id header, create OutboxMessage (Channel=Email, Status=Sent, ExternalReference=MessageId, ProcessedAt=now). On error, OutboxMessage(Status=Failed, FailureReason=error). **Note:** for MVP, save OutboxMessage directly (synchronous); Phase 4 can defer to a job if needed.

- [ ] Create `TwilioNotificationChannel.cs`: inject HttpClient + ILogger + config (AccountSid, AuthToken, PhoneNumber); `SendSmsAsync()` calls Twilio Send API `POST https://api.twilio.com/2010-04-01/Accounts/{SID}/Messages.json` with From, To, Body, StatusCallback (post to `/api/sms/status` for delivery tracking). Extract MessageSid, save OutboxMessage (Channel=Sms, Status=Pending, ExternalReference=MessageSid, InboundWebhookKey=replyCallbackKey, ProcessedAt=null until status callback). On error, OutboxMessage(Status=Failed, FailureReason=error).

- [ ] Create `OutboxService.cs`: inject DbContext, ILogger, NotificationChannels (dict<string Channel, INotificationChannel impl>). `EnqueueAsync()` creates OutboxMessage (Status=Pending), saves to DB, returns it (async queue; does NOT send immediately — that's the worker's job). `MarkProcessedAsync()` loads OutboxMessage by id, sets Status=Sent, ProcessedAt=utcNow, externalRef, saves. `MarkFailedAsync()` increments RetryCount, on >5 sets Status=Failed + reason, on ≤5 keeps Status=Pending for retry.

- [ ] In `Program.cs`: register `services.AddScoped<INotificationChannel, SendGridNotificationChannel>()`, `services.AddScoped<INotificationChannel, TwilioNotificationChannel>()` (note: this allows multiple registrations per interface, so the worker can choose by type or add a factory). Better: create `NotificationChannelFactory` that returns impl by channel enum. Register `services.AddScoped<IOutboxService, OutboxService>()`. Add SendGrid + Twilio config to options binding.

- [ ] Build & verify DI.

### Task 4: Implement rent charge, late fee, and lease expiry services

**Files:**
- Create: `RentalCommand.Api/Services/Automation/RentChargeService.cs`
- Create: `RentalCommand.Api/Services/Automation/LateFeeService.cs`
- Create: `RentalCommand.Api/Services/Automation/LeaseExpiryService.cs`
- Modify: `RentalCommand.Api/Program.cs` (register services)

**Steps:**

- [ ] Create `RentChargeService.cs`: inject DbContext, IOutboxService, ILogger, config. `GenerateRentChargesAsync()`: (1) query `Lease` where Status=Active and StartDate ≤ today ≤ EndDate; (2) for each, calculate NextRentDueDate = lease.StartDate + N months, where month's RentDueDay = the config day; (3) check if Payment already exists for that lease+dueDate (via the unique constraint — this is the idempotency check); (4) if not, insert Payment(LeaseId, Amount=lease.MonthlyRent, DueDate=nextDueDate, Status=Scheduled, Type=Rent); (5) enqueue OutboxMessage via IOutboxService (channel=SMS if IsTenantTwoWay, else Email; body="Rent due {amount} on {dueDate}"; notificationType=RentDue); (6) return (count, errors). **Idempotency:** the unique constraint on (LeaseId, PaymentType, DueDate) makes re-running this safe — duplicate inserts fail at the DB level, are caught and logged (not error), skipped.

- [ ] Create `LateFeeService.cs`: inject DbContext, IOutboxService, ILogger, config. `AssessLateFeesAsync(gracePeriodDays, ct)`: (1) query `Payment` where Type=Rent, Status=Scheduled, DueDate < today - gracePeriodDays; (2) for each overdue payment, load Lease + Property; (3) look up state-aware late-fee cap from config (e.g., CA max 6% of rent or 1 day's rent); (4) calculate lateFeeAmount = min(lease.LateFeeAmount, cap); (5) check if a late-fee Payment already exists for this Lease+Month (via unique constraint); (6) if not, insert Payment(LeaseId, Amount=lateFeeAmount, DueDate=today, Type=LateFee, Status=Scheduled); (7) enqueue notice (channel per IsTenantTwoWay, body="{lateFeeAmount} late fee assessed due to {gracePeriodDays}-day grace period expiry"; notificationType=LateFeeAssessed); (8) return (count, errors). **Financial correctness:** late fees are applied once per month per lease, capped by state law, and audited via activity log.

- [ ] Create `LeaseExpiryService.cs`: inject DbContext, IOutboxService, ILogger. `ProcessExpiryRemindersAsync(daysBefore=60, ct)`: (1) query `Lease` where Status=Active and EndDate = today + daysBefore (±1 day window to catch daily runs); (2) for each, load Tenant; (3) enqueue reminder (channel=SMS/Email, body="{tenantName}, your lease for {propertyAddress} ends on {endDate}. Please contact {ownerName} to discuss renewal or move-out."; notificationType=LeaseExpiring); (4) return count. Guard: don't re-enqueue if already sent (check ActivityLog for this lease+today).

- [ ] In `Program.cs`: register all three services as `services.AddScoped<IRentChargeService, RentChargeService>()` etc.

- [ ] Write red-green test: `RentChargeServiceTests`: (1) create test lease (active, RentDueDay=1, MonthlyRent=1000), (2) call `GenerateRentChargesAsync()`, (3) assert Payment created with DueDate on the 1st of next month, Amount=1000, Status=Scheduled; (4) call again, (5) assert count=0 (idempotent). Repeat for late fees (create overdue payment, call service, assert late-fee created and capped). Use transaction-rollback fixture.

### Task 5: Implement Lease Lifecycle Autopilot service

**Files:**
- Create: `RentalCommand.Api/Services/Automation/LeaseLifecycleAutopilotService.cs`
- Create: `RentalCommand.Api/Dtos/LeaseRenewalDraftDto.cs`
- Modify: `RentalCommand.Api/Program.cs` (register service)

**Steps:**

- [ ] Create `LeaseLifecycleAutopilotService.cs`: inject DbContext, ILlmProvider, IOutboxService, IDataUpdateService (SignalR), ILogger.

  `DraftRenewalNoticeAsync(leaseId, ct)`: (1) load Lease + Tenant + Property + Unit; (2) build grounding context: `{ lease: { tenantName, propertyAddress, unitNumber, currentRent, currentEndDate, rentEscalationPercent }, portfolio: { ownerName, ownerEntity, ownerPhone } }`; (3) call `ILlmProvider.ChatAsync()` with system prompt = "You are a professional property manager. Draft a lease renewal offer letter (3-4 sentences, professional but warm) for the tenant. Include: current rent, proposed new rent (escalate by the given %), new end date (typically 12 months from renewal), and a deadline to respond (5 days). Use the grounding context provided." + function call schema `{ type: "renewal_offer", tenantName, newRent, newEndDate, escalationPercent, letter }`; (4) parse response; (5) save LeaseRenewalDraft(LeaseId, Status=Draft, GeneratedPrompt, GeneratedNotice=letter, NewRentAmount, NewTermEndDate, EscalationPercent); (6) broadcast via `_dataUpdateService.NotifyAsync()` to group `portfolio-{portfolioId}` with event type "LeaseRenewalDraftReady" + draft dto; (7) return draft. On LLM error, log and return null (UI shows "Failed to generate — try again").

  `DraftLateNoticeAsync(leaseId, escalationLevel, ct)`: escalationLevel 1 = "friendly reminder"; 2 = "second notice + payment deadline"; 3 = "eviction notice per state law (include statutory language)". Use LLM with state-specific templates. Save to ActivityLog but do NOT save a separate entity (late notices are transient). Return the notice text only.

  `DraftMoveOutReminderAsync(leaseId, ct)`: build checklist: (1) return security deposit by X date (lease.EndDate + state-mandated days, e.g., CA 30 days); (2) coordinate final walkthrough (email date/time); (3) transfer utilities; (4) provide forwarding address; (5) return keys. Return as structured checklist text.

- [ ] Create `LeaseRenewalDraftDto.cs`: int Id; int LeaseId; string LeaseNumber; string TenantName; string PropertyAddress; string Status; decimal CurrentRent; decimal NewRent; decimal EscalationPercent; DateTime CurrentEndDate; DateTime NewEndDate; string DraftLetter; DateTime CreatedAt; string? ApprovedBy; DateTime? ApprovedAt.

- [ ] Register in `Program.cs`: `services.AddScoped<ILeaseLifecycleAutopilotService, LeaseLifecycleAutopilotService>()`.

- [ ] Write test: mock ILlmProvider, call `DraftRenewalNoticeAsync()`, assert LeaseRenewalDraft saved with non-empty letter + correct new rent escalation. Verify SignalR broadcast (mock IDataUpdateService).

- [ ] Build & verify.

### Task 6: Implement background workers (rent charge, late fee, lease expiry, notification dispatch)

**Files:**
- Create: `RentalCommand.Engine/Workers/RentChargeWorker.cs`
- Create: `RentalCommand.Engine/Workers/LateFeeWorker.cs`
- Create: `RentalCommand.Engine/Workers/LeaseExpiryReminderWorker.cs`
- Create: `RentalCommand.Engine/Workers/NotificationDispatchWorker.cs`
- Modify: `RentalCommand.Engine/Program.cs` (register workers + advisory lock setup)

**Steps:**

- [ ] Create `RentChargeWorker.cs`: inherit `EngineWorkerBase`. `WorkerName = "RentCharge"`, `PollInterval = TimeSpan.FromHours(1)`, `StepTimeout = TimeSpan.FromSeconds(60)`. `ExecuteCycleAsync()`: (1) acquire advisory lock for rent charges (advisory lock key = hash("RentCharge")); (2) inject `IRentChargeService`, call `GenerateRentChargesAsync(ct)`; (3) log results + error count; (4) return count. **Advisory lock pattern:** use Postgres `SELECT pg_advisory_lock(12345)` (scoped key) at start of cycle, auto-release on connection close. If another worker holds it, wait with timeout. Copy pattern from EdiPlatform.Engine/Workers.

- [ ] Create `LateFeeWorker.cs`: similar structure, `PollInterval = TimeSpan.FromHours(4)` (less frequent), advisory lock key = hash("LateFee"). Inject `ILateFeeService`, call with grace period from config.

- [ ] Create `LeaseExpiryReminderWorker.cs`: `PollInterval = TimeSpan.FromHours(6)`, NO advisory lock (idempotent by query window). Inject `ILeaseExpiryService`, call with days-before from config.

- [ ] Create `NotificationDispatchWorker.cs`: `PollInterval = TimeSpan.FromSeconds(10)` (fast, frequent), NO advisory lock (SQL isolation). Inject DbContext, factory for `INotificationChannel` by channel enum, IOutboxService, ILogger. `ExecuteCycleAsync()`: (1) query `OutboxMessage` where Status=Pending AND RetryCount < 5 AND (ProcessedAt is null OR now - LastAttemptAt > {exponential backoff}), limit 10 (batch processing); (2) for each, get impl by message.Channel (SendGrid for Email, Twilio for Sms); (3) call `SendAsync(message.Recipient, message.Subject, message.Body, ...)`; (4) on success, call `IOutboxService.MarkProcessedAsync()`; (5) on transient error (timeout, 429), increment RetryCount, save LastAttemptAt = now (for backoff); (6) on permanent error (invalid address, auth fail), call `MarkFailedAsync(reason)`; (7) return items processed. **Error handling:** differentiate transient (retry) vs permanent (fail) via HTTP status or exception type.

- [ ] In `RentalCommand.Engine/Program.cs`: (1) set up advisory lock on startup (copy from EdiPlatform; Program.cs holds static `AdvisoryLockConnection`); (2) register all four workers: `builder.Services.AddHostedService<RentChargeWorker>()` etc.; (3) register `AdvisoryLockWatcherService` (from EdiPlatform pattern); (4) register `WorkerWatchdogService` (health checks on heartbeats).

- [ ] Build Engine & verify DI.

- [ ] Write integration test: `NotificationDispatchWorkerIntegrationTests`: (1) create OutboxMessage(Status=Pending, Channel=Email), save; (2) mock SendGrid to respond 202; (3) run worker cycle; (4) assert OutboxMessage now Status=Sent, ProcessedAt set, ExternalReference set. Repeat for SMS with Twilio mock. Repeat for retry exhaustion (5+ failures → Status=Failed).

### Task 7: Add Twilio inbound webhook handler

**Files:**
- Create: `RentalCommand.Api/Controllers/SmsInboundController.cs`
- Create: `RentalCommand.Api/Services/TwilioInboundService.cs`
- Modify: `RentalCommand.Api/Program.cs` (register TwilioInboundService)

**Steps:**

- [ ] Create `SmsInboundController.cs`: `[Route("api/sms")]` `POST /inbound`: (1) extract Twilio request body: `{ From, To, Body, MessageSid, AccountSid, ... }`; (2) validate Twilio signature via `ApiKeyAuthenticationHandler` (validate HMAC-SHA1 of request against Twilio AuthToken); (3) inject `ITwilioInboundService`, call `ProcessInboundSmsAsync(from, body, messageSid, accountSid, signature, ct)`, (4) return `Ok()` to Twilio (empty 200 response; Twilio needs fast response). Do NOT do business logic in the controller — fire-and-forget the service call (log and return; let service handle async processing).

- [ ] Create `TwilioInboundService.cs`: inject DbContext, ILlmProvider, IOutboxService, IDataUpdateService, ILogger. `ProcessInboundSmsAsync()`: (1) validate Twilio signature (HMAC-SHA1 with AuthToken); (2) parse body and intent using ILlmProvider: call `ChatAsync()` with system prompt = "Parse the following SMS reply. Determine if it's: (1) rent confirmation ('yes', 'yep', 'ok', 'paid'), (2) work-order status ('done', 'complete'), (3) general question. Return JSON { intent, confidence, entities: { leaseId?, workOrderId?, paymentId?, ... } }"; (3) match sender (From) to tenant/vendor phone in DB (case-insensitive, normalize); (4) route to handler: *Rent confirmation:* load Lease, query pending Scheduled Payment for this month, call `PaymentController.MarkPaidByReply()` (PATCH idempotent endpoint), enqueue confirmation SMS "Thanks! Rent payment confirmed."; *Work-order status:* load WorkOrder, update Status=Completed, notify owner + tenant via SignalR; *Question:* enqueue "Thanks for reaching out. Please contact {ownerPhone} for questions." On error (LLM fail, no match), log + enqueue "Sorry, we didn't understand that. Reply HELP for options." Do NOT throw — return gracefully.

- [ ] In `Program.cs`: register `services.AddScoped<ITwilioInboundService, TwilioInboundService>()`.

- [ ] Create test: mock Twilio signature validation (copy a real Twilio webhook), send "yes" for a pending rent payment, assert Payment marked paid + confirmation SMS enqueued.

- [ ] Build & verify.

### Task 8: Add notification approval UI wiring via SignalR

**Files:**
- Create: `RentalCommand.Api/Hubs/NotificationHub.cs`
- Modify: `RentalCommand.Api/Hubs/DataUpdateHub.cs` (if exists; else create)
- Create: `RentalCommand.Api/Services/DataUpdateService.cs`
- Modify: `RentalCommand.Api/Program.cs` (register SignalR)

**Steps:**

- [ ] Create `NotificationHub.cs`: `public class NotificationHub : Hub { public async Task JoinPortfolioGroup(int portfolioId) => await Groups.AddToGroupAsync(Context.ConnectionId, $"portfolio-{portfolioId}"); }`

- [ ] Create `DataUpdateService.cs`: inject `IHubContext<NotificationHub>`. Broadcast methods: `NotifyRentDueAsync(portfolioId, leaseNumber, tenantName, amount, dueDate, ct)` → sends to group `portfolio-{portfolioId}` with event { type: "RentDue", lease, amount, dueDate }. `NotifyLeaseRenewalDraftAsync(portfolioId, draftDto, ct)` → sends { type: "LeaseRenewalDraftReady", draft }. `NotifyLateFeeAsync(portfolioId, leaseNumber, amount, ct)` → sends { type: "LateFeeAssessed", ... }. etc.

- [ ] In `LeaseLifecycleAutopilotService.DraftRenewalNoticeAsync()`, after saving draft, call `_dataUpdateService.NotifyLeaseRenewalDraftAsync()`.

- [ ] In notification workers, after enqueueing OutboxMessage, call `_dataUpdateService.Notify...()` to wake the UI in real-time.

- [ ] Register in `Program.cs`: `services.AddScoped<IDataUpdateService, DataUpdateService>()`. Add SignalR: `builder.Services.AddSignalR()`. Add hub mapping: `app.MapHub<NotificationHub>("/hubs/notifications")`. Ensure JWT token is read from query string or header for hub auth.

- [ ] Update web app (`web/src/lib/api/signalr.ts`): import NotificationHub, add listener for "LeaseRenewalDraftReady", display approval UI (show draft notice, "Approve" / "Edit" / "Cancel" buttons). On approve, call API `PATCH /api/leases/{id}/renewal-drafts/{draftId}/approve`.

- [ ] Build both api + web, verify SignalR connection in dev.

### Task 9: Add payment approval endpoints

**Files:**
- Modify: `RentalCommand.Api/Controllers/PaymentController.cs`
- Create: `RentalCommand.Api/Controllers/LeaseRenewalController.cs`

**Steps:**

- [ ] In `PaymentController.cs`: add `PATCH /api/payments/{id}/mark-paid-by-reply` → idempotent, used by SMS inbound handler. Load Payment by id, if Status=Scheduled, set Status=Confirmed, PaidDate=today, ConfirmedByReplyTo=messageId (passed in body). Return PaymentDto. Guard: only tenants of the associated Lease can call (auth via JWT).

- [ ] In `PaymentController.cs`: add `PATCH /api/payments/{id}/mark-paid` → same logic but without ConfirmedByReplyTo (used by check-scan flow in Phase 2).

- [ ] Create `LeaseRenewalController.cs`: `PATCH /api/leases/{leaseId}/renewal-drafts/{draftId}/approve` → load LeaseRenewalDraft, set Status=Approved, ApprovedBy=currentUserId, ApprovedAt=now. Enqueue SMS/Email "Renewal offer has been sent to {tenantName}. They have 5 days to respond." Call Autopilot.SendRenewalNoticeAsync (new method: loads draft, sends via INotificationChannel, marks Sent). Return dto.

- [ ] Add `DELETE /api/leases/{leaseId}/renewal-drafts/{draftId}` → soft-delete or Status=Rejected.

- [ ] Build & test.

### Task 10: Wire notice generation from Phase 3 into actual delivery

**Files:**
- Modify: `RentalCommand.Api/Services/Automation/RentChargeService.cs`
- Modify: `RentalCommand.Api/Services/Automation/LateFeeService.cs`
- Modify: `RentalCommand.Api/Services/Automation/LeaseExpiryService.cs`

**Steps:**

- [ ] In `RentChargeService.GenerateRentChargesAsync()`, after inserting Payment, build notice body: load Lease + Tenant, determine channel (SMS if IsTenantTwoWay else Email), call `IOutboxService.EnqueueAsync()` with channel, recipient (tenant.Phone or tenant.Email), body = "Rent reminder: {amount} due on {dueDate}. Pay via {portal-link} or reply YES to confirm.", notificationType=RentDue.

- [ ] Similarly in `LateFeeService.AssessLateFeesAsync()`: enqueue late-fee notice with escalating tone (Level 1: friendly; escalate if already late).

- [ ] Similarly in `LeaseExpiryService`: enqueue expiry reminder with link to renewal/move-out options.

- [ ] In all three, after enqueuing, call `_dataUpdateService.Notify...()` to broadcast real-time event to web UI.

- [ ] Build & integration-test end-to-end: create lease, run RentChargeWorker, assert Payment created + OutboxMessage enqueued + NotificationHub event broadcast + UI receives it.

### Task 11: Configuration and deployment

**Files:**
- Modify: `RentalCommand.Api/appsettings.json`
- Modify: `RentalCommand.Api/appsettings.Local.json.example`
- Create: `RentalCommand.Api/Options/NotificationSettings.cs`
- Modify: `RentalCommand.Engine/appsettings.json`
- Modify: `docker-compose.yml` (if exists)

**Steps:**

- [ ] Add to `appsettings.json`: `SendGridApiKey`, `TwilioAccountSid`, `TwilioAuthToken`, `TwilioPhoneNumber`, `NotificationSettings { RentGracePeriodDays, LateFeeGracePeriodDays, LeaseExpiryReminderDaysBefore, OutboxRetryMaxAttempts, OutboxRetryIntervalSeconds, StateLateFeeCaps { CA: { MaxPercent, MaxDaysRent }, ... } }`.

- [ ] Create `NotificationSettings.cs` options class: public class NotificationSettings { public int RentGracePeriodDays { get; set; } = 5; public int LateFeeGracePeriodDays { get; set; } = 2; public int LeaseExpiryReminderDaysBefore { get; set; } = 60; public int OutboxRetryMaxAttempts { get; set; } = 5; public int OutboxRetryIntervalSeconds { get; set; } = 600; public Dictionary<string, StateLateFeeCap> StateLateFeeCaps { get; set; } = new(); } where StateLateFeeCap = { MaxPercent, MaxDaysRent }. Bind in `Program.cs`: `services.Configure<NotificationSettings>(config.GetSection("NotificationSettings"))`.

- [ ] Create `.example` files (safe defaults; no secrets).

- [ ] In Engine `appsettings.json`: include same SendGrid + Twilio + notification settings (Engine may need to read config for health checks).

- [ ] In `docker-compose.yml` (if adding Engine service): add RentalCommand.Engine container, environment variables for DB connection + API keys, depends_on DB + API.

- [ ] Document in README: "To enable notifications: set SENDGRID_API_KEY, TWILIO_ACCOUNT_SID, TWILIO_AUTH_TOKEN, TWILIO_PHONE_NUMBER in environment or appsettings.json. Automated workers (rent charge, late fee, lease expiry, notification dispatch) run in the RentalCommand.Engine background service."

- [ ] Build & verify all secrets are read from config, never hardcoded.

### Task 12: Integration tests

**Files:**
- Create: `RentalCommand.Api.Tests/Services/Automation/RentChargeServiceTests.cs`
- Create: `RentalCommand.Api.Tests/Services/Automation/LateFeeServiceTests.cs`
- Create: `RentalCommand.Api.Tests/Services/TwilioInboundServiceTests.cs`
- Create: `RentalCommand.Engine.Tests/Workers/NotificationDispatchWorkerTests.cs`
- Create: `RentalCommand.IntegrationTests/Phase4_AutomationAndNotificationsJourneys.cs`

**Steps:**

- [ ] `RentChargeServiceTests`: (1) create active lease with RentDueDay=1, (2) set mock clock to Jan 2, (3) call GenerateRentChargesAsync(), (4) assert Payment created for Jan 1 due date, (5) assert OutboxMessage enqueued, (6) call again, (7) assert count=0 (idempotent). Repeat with multiple leases, mix of active/expired.

- [ ] `LateFeeServiceTests`: (1) create Payment(Type=Rent, Status=Scheduled, DueDate=yesterday), (2) call AssessLateFeesAsync(gracePeriodDays=2), (3) assert late-fee Payment created, amount capped per state, (4) assert OutboxMessage enqueued. Repeat with different states (CA, TX, NY).

- [ ] `TwilioInboundServiceTests`: (1) create Lease + Tenant with pending rent Payment, (2) mock Twilio webhook signature validation, (3) post `ProcessInboundSmsAsync(from=tenant.Phone, body="yes", ...)`, (4) assert Payment marked paid + ConfirmedByReplyTo set, (5) assert confirmation SMS enqueued. Repeat for work-order status updates.

- [ ] `NotificationDispatchWorkerTests`: (1) create OutboxMessage(Status=Pending, Channel=Email), (2) mock SendGrid to 202 Accepted, (3) run worker cycle, (4) assert OutboxMessage Status=Sent, ProcessedAt set. Repeat for Twilio, and for failure scenarios (5+ retries → Failed).

- [ ] `Phase4_AutomationAndNotificationsJourneys.cs` (Playwright): (1) create portfolio + lease, (2) travel in time to rent due date, (3) trigger RentChargeWorker (via admin endpoint or direct call), (4) assert rent Payment created, (5) subscribe to NotificationHub (SignalR), (6) assert RentDue event received, (7) click "Confirm Payment" (reply-YES flow simulation), (8) assert Payment marked paid. Repeat for late-fee + lease expiry scenarios.

- [ ] Run full test suite, ensure >90% coverage on new services, fix any gaps.

### Task 13: Documentation and handoff

**Files:**
- Create: `Docs/Phase4-AutomationAndNotificationsGuide.md`

**Steps:**

- [ ] Document: (1) Architecture overview (workers, advisory lock, outbox, SignalR, SMS/email channels), (2) Configuration required (API keys, grace periods, state caps), (3) Worker schedule (rent charge hourly, late fee 4h, expiry reminder 6h, dispatch 10s), (4) Advisory lock mechanism (single Engine instance per DB), (5) Idempotency patterns (unique constraints on Payment, OutboxMessage retry logic), (6) SMS two-way flow (inbound webhook, reply parsing, payment confirmation), (7) Lease Lifecycle Autopilot (LLM-generated notices, approval UI, SignalR broadcast), (8) Testing checklist (workers, notification channels, inbound, end-to-end journeys).

- [ ] Add ADR (Architecture Decision Record): why DB-backed outbox + advisory lock instead of RabbitMQ (scope: keep it simple for Phase 4; RabbitMQ is swappable later via IMessagePublisher interface).

- [ ] Commit: "Phase 4: Automation, notifications, and lease lifecycle — TDD red-green, advisorylock on rent/late-fee, SMS Twilio inbound reply, LLM-drafted renewal offers, NotificationHub real-time approval UI."

- [ ] Verify build passes, all tests green, no secrets in code.

---

## Acceptance criteria

✓ Rent charges are generated automatically once per calendar month per active lease, on or after the lease's RentDueDay, idempotent (no double-charges via unique constraint + worker advisory lock).
✓ Late fees are assessed after a configurable grace period (default 2 days), state-aware caps are applied (CA 6%/1day, TX 10%, etc.), assessments are idempotent.
✓ Lease-expiry reminders are sent ≤60 days before lease end date, once per lease.
✓ OutboxMessage table queues all SMS/email for reliable delivery with retry (up to 5x over ~1h with exponential backoff); failed messages are marked Failed + reason logged.
✓ SendGrid sends email; Twilio sends SMS (both with ExternalReference tracking).
✓ Twilio inbound webhook (`POST /api/sms/inbound`) validates signature, parses reply, and triggers handlers (e.g., "yes" → marks pending rent payment as paid + ConfirmedByReplyTo).
✓ Lease Lifecycle Autopilot (using ILlmProvider) drafts renewal offers with tenant name, property address, escalated rent, new end date; saves draft with Status=Draft; broadcasts via SignalR NotificationHub to group `portfolio-{portfolioId}`.
✓ Owner approves renewal draft via web UI; on approve, notice is sent to tenant via SMS/Email.
✓ Each worker runs under Postgres advisory lock (rent charge, late fee) or single-instance isolation (expiry, dispatch); only one Engine instance acquires lock at startup; if another Engine is detected, the old one is killed and new one takes over.
✓ All financial operations (payment creation, late-fee assessment) are transactional and logged to ActivityLog with provenance (actor, timestamp, reason).
✓ Configuration for grace periods, state late-fee caps, grace-period days, reminder offset, Twilio/SendGrid API keys is external (appsettings.json, environment variables).
✓ No hardcoded API keys, no secrets in source code.
✓ Integration tests cover: rent-charge idempotency, late-fee caps by state, Twilio inbound reply → payment confirmation, notification dispatch with retry, lease-expiry reminders, LLM draft renewal notice.
✓ Build passes, unit + integration tests green, Playwright journeys demonstrate end-to-end automation (lease created → rent charged → SMS sent → reply parsed → payment confirmed).

---

## Plan self-review notes

**Spec sections covered by this phase:**
- Phase 4 headline: "Engine workers: recurring rent auto-posting (idempotent per lease+period), auto late fees, lease-expiry reminders. SMS-first communication (Twilio). Reply-YES rent confirmation. Lease Lifecycle Autopilot (AI drafts renewal/late notices). SecurityDepositHolding."
- §4 (Capture → Draft → Confirm): Notice generation from Phase 3 is wired into actual delivery via INotificationChannel + OutboxMessage. Approval gates via LeaseRenewalDraft.Status + approval UI.
- §5 (Two AI brains): Daily briefing uses rent/late/lease-expiry automation; Portfolio Q&A includes new "is lease up soon?" query. Both inherit the LLM core. Lease Lifecycle Autopilot is new "notices as a service" via LLM.
- §6 (Architecture): EngineWorkerBase + advisory lock from EdiPlatform; DB-backed outbox (not RabbitMQ); INotificationChannel + IOutboxService abstractions; SignalR for real-time UI updates.
- §9 (Cross-cutting): Audit trail on all payment mutations; FCRA late-notice language baked in (per state law); approval gates on renewal offers; LLM provenance (prompt + token usage) stored per draft.
- Not covered (deferred to later phases): online payment (Phase 5), screening/FCRA on IDs (Phase 6), expense categorization to Schedule E (Phase 7), field maintenance dispatch to vendors (Phase 8).

**Critical implementation notes:**
1. **Idempotency via unique constraints + advisory lock:** Rent charges use DB unique(LeaseId, PaymentType, DueDate) so duplicate inserts fail safely. Workers hold advisory lock(12345) for rent/late-fee so only one Engine instance runs them concurrently.
2. **Two-way SMS:** Outbound via Twilio API + OutboxMessage. Inbound via webhook (Twilio POST /api/sms/inbound) → TwilioInboundService → LLM intent parsing → handler trigger (e.g., mark payment paid). This is MVP two-way; Phase 5 adds work-order dispatch + replies.
3. **LLM grounding:** Lease Lifecycle Autopilot reads actual lease PDF + tenant data to draft renewal/late/move-out notices. Not template-based; each notice is contextual. Prompt is stored in LeaseRenewalDraft for audit.
4. **Financial correctness:** Every payment create/update is logged to ActivityLog with reason + actor. Late-fee caps are state-aware (config-driven, not hardcoded). Deposits move to SecurityDepositHolding in Phase 7 but infrastructure is here.
5. **SignalR real-time:** Workers broadcast events (RentDue, LateFeeAssessed, LeaseRenewalDraftReady) to web UI via NotificationHub → group `portfolio-{portfolioId}`. Approval UI wakes when draft is ready.
6. **No magic numbers:** All delays/periods (grace, retry backoff, reminder offset) are config-driven. Secrets (API keys) are environment-injected. Model name for LLM is config-driven (from Phase 2).

**Testing strategy:**
- Unit tests on services (RentChargeService, LateFeeService, TwilioInboundService) with mocked DbContext + ILlmProvider + IOutboxService.
- Integration tests on workers (NotificationDispatchWorker) with real DB (transaction rollback), mocked HTTP clients (SendGrid, Twilio).
- Playwright journeys: full flow (lease created → time travel → rent charged → SMS sent → reply → payment confirmed → state verified).
- Coverage target: >85% on new code (services, workers, controllers).

**Known risks & mitigations:**
- **Risk:** Twilio webhook timeout if business logic is slow. **Mitigation:** Controller returns 200 immediately; service call is fire-and-forget (logged, not blocking).
- **Risk:** Advisory lock contention if Engine restart is slow. **Mitigation:** Startup kills old instance via `pg_terminate_backend()` before acquiring lock; AdvisoryLockWatcherService detects dead connection and shuts down gracefully.
- **Risk:** LLM hallucination in renewal drafts (wrong rent, dates). **Mitigation:** draft is Status=Draft, requires human approval before sending. Approval UI shows generated values for edit.
- **Risk:** Double-charged rent if constraint migration fails. **Mitigation:** Unique constraint is tested at DB layer; pre-deployment smoke test runs rent charge twice on test lease, asserts count=0 on second run.

**Phase 4 ships with Phases 0–3 complete.** Phases 5–9 depend on Phase 4 for notifications + automation infrastructure.
