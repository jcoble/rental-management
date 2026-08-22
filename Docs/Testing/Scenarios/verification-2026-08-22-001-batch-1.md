# Verification: Batch 1 (Run 2026-08-22-001)

Verify these bug fixes on the running stack (all merged to main 799889a2; API/Engine/web all serve that build). For each fix, follow the steps and confirm the bug no longer occurs. Read the changed code first to understand what each fix did.

## Fix 1: TSK-965 — Engine starts and delivers notifications (was: Engine dead on missing IRequestWriteExecutor)
**Original bug:** Engine crashed at startup (DI validation), killing all email/notification/e-sign background work.
**Code changed:** RentalCommand.Engine/Program.cs (IRequestWriteExecutor registration), RentalCommand.Api/RentalCommand.Api.csproj (InternalsVisibleTo).
**Steps:**
1. Confirm the Engine process is alive and /tmp/rentalcommand-engine.log shows "Application started" with no DI errors (orchestrator already verified startup — your job is the behavior).
2. In the UI, trigger a notification-producing action (e.g. request a password reset for a QA account, or any action that enqueues an email) and confirm the Engine log shows the outbox dispatch/delivery (or suppression) within ~1 minute — not stuck Pending.

## Fix 2: TSK-967 — /register redirects an authenticated user
**Original bug:** Logged-in users saw the full registration form at /register.
**Code changed:** web/src/routes/register/+page.server.ts (locals.user guard).
**Steps:** logged in as admin, navigate directly to https://localhost:5667/register → expect redirect to the dashboard/safe landing, no form. Log out, /register again → form renders.

## Fix 3: TSK-968 — unit endpoints return 404 (not 403) for a nonexistent unit
**Code changed:** RentalCommand.Api/Controllers/UnitController.cs Get + Dashboard.
**Steps:** as the logged-in admin (management experience), GET https://localhost:5666/api/v1/units/99999/dashboard (browser fetch from the app origin or authenticated API call) → expect 404 with {"error":"Unit not found"}; same for /api/v1/units/99999. A REAL unit id must still return 200.

## Fix 4: TSK-969 — properties list filters clear their URL params
**Code changed:** web/src/routes/(protected)/properties/+page.svelte (all-sentinel for type AND status Selects).
**Steps:** on /properties filter by a concrete type → URL gains ?type=...; choose "All types" → the type param disappears from the URL. Repeat for status ("All statuses" clears ?status=). Reload after each to confirm no stale filter re-applies. Back/Forward still restores earlier filter states.

## Fix 5: TSK-973 — application detail refreshes after approve/decline/withdraw
**Code changed:** web/src/lib/components/records/ApplicationDetail.svelte (scoped query keys).
**Steps:** create a fresh application on an S-verify-prefixed unit (or reuse a Submitted one you create), approve it → WITHOUT reloading, the status badge must change from Submitted and the Approve/Decline/Withdraw buttons must disappear/update. Repeat conceptually for decline on a second application.

## Fix 6: TSK-974 — lease draft rejects due day 0
**Code changed:** RentalCommand.Data/Leasing/LeaseAgreementDraftCommandHandlers.cs (1-31 range in ValidateEditShape).
**Steps:** open/edit a lease agreement draft (create a lease on your own QA fixture), set due day 0, save → expect a validation error, not "Lease draft saved."; reopen to confirm the old value is unchanged. Due day 1 and 31 still save.

## Fix 7: TSK-975 — draft dialog closes after successful issuance
**Code changed:** web/src/lib/components/leases/AgreementDraftDialog.svelte (close before parent refresh).
**Steps:** issue a draft ("Prepare and send") → the dialog closes on success and the agreement row shows Awaiting signatures without manual navigation.

## Fix 8: TSK-976 — no console 404 on unit Money tab without a deposit
**Code changed:** web/src/lib/components/accounting/TenantLedgerPanel.svelte (404 → null).
**Steps:** open the Money tab of a unit whose tenant account has NO security deposit (create a fresh tenancy without funding a deposit) → console shows no 404 error for /tenant-accounts/{id}/deposit; the deposit section renders its empty state.

Data rules: create your own records prefixed "VB1". Do not modify S11–S15 records except read-only navigation. Do not change the admin password.
