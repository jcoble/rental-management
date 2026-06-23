# TSK-397 Pass 51 Comms And Operations UI Guide

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-51`
Local target: `https://localhost:6042`

## Scope

Start from a fresh landlord account and exercise the next real operating loop after a rental is created: appointments, messages, notices, and tenant-facing portal surfaces. Keep Plaid and connected QuickBooks workflows deferred.

## Setup

- Register a new synthetic landlord account in the local stack.
- Build at least one property, unit, tenant, and active lease from synthetic local data before using communications workflows.
- Prefer scan/photo-backed setup when the scan engine is available; otherwise create only the minimum records manually and record that as a setup limitation.
- Use local-safe provider settings only. Do not send provider-backed email/SMS.

## Actions And Assertions

1. Open the app as the new landlord.
   - Register, verify/login locally, and choose live setup.
   - Confirm the dashboard has no demo rows and shows setup progress from the new portfolio only.
2. Create the first rental business data.
   - Add or scan a property, unit, tenant, and lease using production-like values.
   - Attach at least one camera-style image document and verify the stored document opens as an image.
   - Confirm dashboard cards, recent activity, and the Unit Command Center reflect the new records.
3. Exercise appointments as a landlord.
   - Create a showing or maintenance appointment from the UI.
   - Verify list view search/filter/sort uses the paged endpoint and the detail page opens.
   - Edit/reschedule, cancel once, then save. Verify dashboard and detail state update.
4. Exercise staff-to-tenant messages without external sends.
   - Open Messages, compose to the active tenant, use portal-only delivery, and send.
   - Verify the conversation opens, message text persists, unread counts update, search works, and no provider-send action is triggered.
5. Exercise notice drafting without sending externally.
   - Open the tenant detail and create a forced renewal or move-out notice draft when eligible.
   - Verify draft subject/body, channel checkboxes, dismiss behavior, no-eligible copy, and reload persistence.
   - Do not press `Send` unless the channel is confirmed local-safe.
6. Exercise tenant-facing portal surfaces.
   - Open portal dashboard, lease, messages, maintenance, payments, notifications, and appointments for the synthetic tenant where available.
   - Verify route links, empty/current states, message reply controls, maintenance request creation with image preview/remove, notification read state, and payment unavailable/safe states.
7. Capture gaps.
   - Log any place where a real landlord cannot continue from point A to point B without using an admin/API workaround.
   - For each bug, record expected vs actual behavior, route, user, record ids, browser snapshot/screenshot, console/network evidence, and whether it is a setup gap or product bug.

## Evidence To Record

- Fresh account email and portfolio id.
- Property/unit/tenant/lease ids created in the pass.
- Uploaded/scanned document ids and file content type proof.
- Browser URLs and snapshots/screenshots for appointments, messages, notices, and portal routes.
- Network proof for server-side list/search/filter/sort paths where relevant.
- Explicit note that Plaid and connected QuickBooks are still deferred.
