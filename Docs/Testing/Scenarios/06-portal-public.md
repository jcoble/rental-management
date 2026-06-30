# Scenario 06 — Tenant Portal & Public (apply / e-sign)

**Domain:** Portal & public. **Suggested tester session:** `tester2`.

## Mission
Test the surfaces a tenant (not the landlord) touches: the **tenant portal** (dashboard, pay rent,
maintenance requests, lease, notifications, appointments, security) and the **public** flows
(`/apply/[token]` rental application, and lease **e-sign**). These are the least-staff-supervised
paths, so guard/permission bugs and broken public links are high impact. Also confirm portal users
only ever see their own data.

## Get acquainted with the code first
- Frontend (portal): `web/src/routes/(portal)/portal/` and its children: `payments/`, `maintenance/`,
  `lease/`, `notifications/`, `appointments/`, `security/`, `messages/`.
- Frontend (public): `web/src/routes/apply/[token]/`, the `*-file/[id]` proxies, and the e-sign route
  (look for a sign route/component; web client `web/src/lib/api/endpoints/sign.ts`).
- Web API client: `web/src/lib/api/endpoints/portal.ts`, `sign.ts`, `applications.ts`,
  `notifications.ts`, `messages.ts`.
- API: `PortalController.cs`, `SignController.cs`, `PublicApplicationsController.cs`,
  `EsignWebhookController.cs`, `NotificationsController.cs`.
- Auth model: portal routes are guarded by `(portal)/+layout.server.ts`; tokens live in
  app-namespaced httpOnly cookies. Public routes must work **without** a staff session.

## Flows to exercise
1. **Portal dashboard**: log in as / navigate as a tenant portal user (or use a portal access link if
   the staff app exposes one — check the tenant detail "portal" affordance). Confirm the dashboard
   cards show that tenant's lease, balance, and next actions correctly.
2. **Pay rent (portal)**: walk the portal payment flow as far as the environment allows; confirm
   amounts and lease attribution are correct and a tenant can't pay against someone else's lease.
3. **Maintenance request (portal)**: submit a request (and try submitting an empty one — it should be
   blocked, not 500); confirm it appears for staff on the maintenance side.
4. **Notifications / messages (portal)**: confirm a tenant sees their notifications, can mark read,
   and can't see other tenants' messages.
5. **Public apply**: open an `/apply/[token]` link (generate one from the staff side if needed) in a
   context without a staff session; complete and submit an application; confirm it lands as an
   application for staff to review.
6. **E-sign**: exercise the lease e-sign path (request signature, open the signing link, sign);
   confirm the signed state reflects back to the lease.

## Watch especially for
- A portal/public route reachable without the right auth, OR a portal user seeing another tenant's
  data (IDOR) — high severity.
- Public `/apply/[token]` or `*-file/[id]` links that 404, error, or require a staff login they
  shouldn't.
- Empty/invalid submissions that 500 instead of validating.
- Signed/submitted state on the public side not reflecting back to the staff record (or vice-versa).
- Portal dashboard numbers (balance, next payment) not matching the staff-side truth.

## Data hygiene
Marker `QA-T2-<HHMMSS>`. Creating applications/requests is safe (new records). Don't sign/alter
leases the Money tester is actively using; prefer a lease you can identify as test data.

## Output
Write your report to: `Docs/Testing/Results/2026-06-28-001/06-portal-public.md`
