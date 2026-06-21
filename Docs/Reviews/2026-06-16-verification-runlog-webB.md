# Rental Command — Web Lane B Verification Run Log

**Date:** 2026-06-16 · **Session:** verifyB · **Steps:** W30–W39, W42–W46 (skip W40/W41)

## State carried from Web Lane A
- PID = 6 ("Verify Landlord" portfolio); landlord `verify-landlord@rc.local` / `Rc!Verify2026`
- LID = 32 (Tenant A lease, Maple Court unit "2", rent 950, Active)
- Maple Court = property 41 (units 32="1"/1200, 33="2"/950); Birch Plaza = property 42
- Tenant A = Alice Anderson id 52; Tenant B = Bob Brown id 53

## Captures
- Conversation Id (CID): 5 (W36, Subject "Welcome", Tenant 52) — mobile M12 replies to THIS
- NoticeDraft id (W39): 28 (RenewalOffer, Approved, channels Portal,Email)
- Notifications created: id 8 "Welcome" (W36), id 9 "Lease Renewal Offer" (W39)
- Primary WO id (W31): 41  · final status: Completed (4)
- Vendor id (W30): 7 (Vic's Plumbing)
- VendorDispatch id: 1 (WO 41 -> vendor 7)
- Expense id (W35): 687 (Repairs/110/Paid, WorkOrderId=41, VendorId=7)

## Step results

| Step | Result | Notes |
|---|---|---|
| W30 | PASS | Vendor 7 "Vic's Plumbing"/Plumbing/+13305550147 created |
| W31 | PASS | WO 41 New(0)/High(2)/cost 120, tenant 52, property 41 |
| W32 | PASS* | Scheduled->InProgress via confirm dialogs. Vendor assignment NOT in edit form (FINDING); VendorId set via W33 dispatch instead |
| W33 | PASS | VendorDispatch 1 created; dispatch ALSO set WO.VendorId=7. tel/sms/mailto contact block only renders after vendor linked |
| W34 | PASS | Completed(4), ActualCost=110, CompletedAt set; WorkOrderStatusEvents 24-27 = New->Scheduled->InProgress->Completed |
| W35 | PASS | Expense 687 linked to WO 41 + vendor 7, Repairs/110/Paid |
| W36 | PASS | Conversation 5 "Welcome" / Tenant 52 / StartedByLandlord=t; 1 Landlord message. Channel "Portal inbox" default-checked. Notification id 8 created |
| W37 | DEFERRED | Conversation + landlord message confirmed; awaits mobile M12 reply (Tenant-role row) |
| W38 | PASS (no drafts) | "Generate drafts" returned HTTP 200 but created 0 drafts for PID 6 — no leases meet auto-lifecycle criteria today (lease 31 ends 2026-08-31, lease 32 move-out 2026-06-30). Not a bug; the manual W39 path produces a draft |
| W39 | PASS | Forced RenewalOffer via tenant-detail "Create / Send notice"; NoticeDraft 28 Draft->Approved, channels Portal,Email (unchecked SMS - no provider yet). Notification id 9 + 1 outbox row created |
| W42 | PARTIAL/FINDINGS | KPI cards + money snapshot render. Confirmed PID 6 ("0/3 occupied"). 3 data discrepancies found (see FINDINGS F1-F3) |
| W43 | PASS | /reports catalog loads (P&L, Property P&L, GL, Cash Flow, Schedule E, Year-End Packet, Rent Roll, Rent Ledger). Rent Roll shows 2 active leases (Maple Unit 1 Daniel $1875, Unit 2 Alice $950) total $2825 - corroborates F1 (reports use lease-based occupancy, dashboard uses stale Unit.Status). /analytics route redirects to dashboard |
| W44 | PASS | Notification email saved (PUT /notifications/email 200). Channel matrix saved (PUT /notifications/settings 200) - NotificationPreferences now 6 rows for PID 6; toggled LateFee SMS on -> EnableSms=t persisted |
| W45 | PASS (REQUIRED) | Automations: enabled Auto-post rent charges + Auto-assess late fees; days 5/5/60. SQL: EnableRentCharges=t, EnableLateFees=t, LateFeeGraceDays=5, RentChargeLeadDays=5, LeaseExpiryReminderDays=60. GATE FOR CONTROLLER AUTOMATION IS SET |
| W46 | PASS | SMS provider=SignalWire saved (SmsProvider='SignalWire', creds in SmsCredentialA/B/C + SmsFromNumberCipherText - all set). Test send: POST /notifications/settings/test-sms 200, made real SignalWire API call, returned clear surfaced error "SignalWire rejected the send: 404 ... space verify-test doesn't exist" (expected w/ dummy creds). Note: SignalWire-named cipher columns (SignalWireProjectIdCipherText etc.) stay NULL - creds use generic columns (F4, LOW) |

## FINDINGS

**F1 (MEDIUM) — Dashboard occupancy shows 0% despite 2 active leases.**
- Step: W42. Symptom: dashboard "Occupancy 0% (0/3 occupied)" for PID 6, but 2 Active leases exist on units 32,33 (3 units total -> should be 66.7%).
- Expected: occupancy reflects units with an active lease.
- Root cause: `RentalCommand.Api/Services/Domain/DashboardService.cs:75` defines Occupied = `Unit.Status == UnitStatus.Occupied`. All 3 PID-6 units have Status=0 (Vacant) in DB despite active leases -> creating/assigning an Active lease does NOT flip Unit.Status to Occupied. Reports (Rent Roll, W43) use lease-based logic and correctly show 2 active leases, so the two surfaces disagree. Either the lease-create path must set Unit.Status, or the dashboard occupancy should derive from active leases (as the reports do).

**F2 (MEDIUM) — Two different "Net This Month" on the same dashboard; money-snapshot "Collected" counts unpaid scheduled rent.**
- Step: W42. Symptom: KPI card "Net This Month = -$185" vs "Your money" snapshot net = "$765" (Collected $950 - Spent $185). For PID 6, $0 was actually Paid this month; the only $950 is a Scheduled (unpaid) rent row (Payment 1098, Status=Scheduled, no PaidDate).
- Expected: a single consistent net; "Collected" should count only actually-paid money.
- Root cause: two different backends feed one page. KPI net is `DashboardService.cs:150` NetThisMonth = PaidThisMonth($0) - expenses($185) = -$185 (CORRECT; PaidThisMonth at :122 requires Status=Paid + PaidDate this month). The "Your money" snapshot comes from a separate endpoint `GET /api/v1/accounting/snapshot` whose "collected" includes the Scheduled rent ($950) -> wrong $765 net. The accounting-snapshot collected aggregation should exclude non-Paid statuses to match DashboardService.

**F3 (MEDIUM) — Dashboard "Overdue / Receivables past due" shows $0 despite overdue scheduled rent.**
- Step: W42. Symptom: dashboard Overdue=$0 for PID 6, but Payment 1098 (Rent, Scheduled, $950, DueDate 2026-06-01 < now) meets the overdue predicate at `DashboardService.cs:113-115` (Scheduled/Partial/Late AND past due). Expected at least $950 overdue.
- Expected: overdue sum includes past-due Scheduled rent.
- Root cause hypothesis: needs deeper trace - either the overdue grouping isn't seeing PID-6 payments at render time, or a query-filter/portfolio-scoping issue. Timeboxed; flagged for the dashboard-fix lane to reconcile against DashboardService.cs:108-132. (Possible interaction: during the session some dashboard API calls hit /portfolios/1/dashboard and some /portfolios/6/dashboard - see F5.)

**F4 (LOW) — SignalWire credentials persist to generic SmsCredentialA/B/C columns, leaving the SignalWire-named cipher columns NULL.**
- Step: W46. Symptom: after saving SignalWire provider, `SignalWireProjectIdCipherText/TokenCipherText/FromNumberCipherText` stay NULL; values land in `SmsCredentialACipherText/B/C` + `SmsFromNumberCipherText`. Functionally fine (test send resolved the saved creds and called the real API), but the SignalWire-named columns appear to be dead/legacy schema. Cleanup candidate.

**F5 (LOW) — Dashboard made API calls to BOTH /portfolios/1/dashboard and /portfolios/6/dashboard during the session.**
- Step: W42. Symptom: API log shows 8 calls to portfolios/6/dashboard and 6 to portfolios/1/dashboard while logged in as verify-landlord (bound to PID 6). The rendered "0/3 occupied" confirms PID 6 was the surface shown, but the stray PID-1 calls suggest a portfolio-context/prefetch path that queries portfolio 1 (Coble Limited, the seeded first portfolio). Worth confirming the portfolio switcher/default-portfolio resolution isn't leaking another tenant's portfolio id. May interact with F3.

**F6 (LOW, playbook drift) — Work-order Edit form has no Vendor/Tenant/Unit selector.**
- Step: W32. Symptom: playbook W32 expects vendor assignment via `work-order-edit` form, but the edit form only exposes Title/Description/Property/Priority/Category/dates/costs. Vendor gets linked via the W33 "Text a vendor" dispatch (which also sets WorkOrders.VendorId). Net effect: vendor assignment works, just via a different control than the playbook assumes. Documentation/playbook drift, not an app bug.

**Note (not a finding) — W38 "Generate drafts" produced 0 drafts.** Endpoint returned 200; PID-6 leases don't currently meet the auto-lifecycle criteria (lease 31 ends 2026-08-31, lease 32 move-out 2026-06-30). The manual W39 path produced a draft successfully. Expected behavior.

