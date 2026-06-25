# TSK-397 Pass 76 App Shell Navigation Guide

## Scope

Verify the staff app shell as a real landlord using local sample data: header quick actions, theme toggle, notifications, account menu, desktop collapsed rail, mobile drawer, and the Money/Rentals/Work/Inbox/Settings nav groups.

## Setup

- Web: `https://localhost:6042`
- Login: visible dev-admin helper on `/login`
- Account: `admin@rentalcommand.local`
- Data mode: local example/sample data only

## Acceptance Criteria

- Header quick actions route to Scan / Add, Messages, Appointments, Help, and the notification dropdown without console errors.
- Theme toggle switches light/dark without layout overlap or route loss.
- Staff notification dropdown opens, handles empty/read states, and `Notification settings` routes to Settings.
- Account menu exposes Settings, Security, and Sign Out; Security lands on staff account security, and Sign Out ends at Login without auto-resuming the session.
- Desktop collapse turns the sidebar into an icon rail, keeps all visible route targets reachable, and expands back without losing route context.
- Mobile viewport exposes a drawer toggle, drawer links navigate, and the overlay closes the drawer.
- Money, Rentals, Work, Inbox, and Settings groups expand one at a time; every visible child link loads its intended page.

## Browser Steps

1. Clear browser data, open `/welcome`, sign in with the dev-admin helper, and confirm the dashboard/example-data shell loads.
2. Click header Scan / Add, Messages, Appointments, and Help; confirm each page title and route.
3. Toggle the theme twice; confirm the route remains stable and no text/control overlap appears.
4. Open Notifications; confirm the dropdown appears, then click Notification settings and confirm `/settings`.
5. Open the account menu; click Security and confirm `/settings/security`. Return to the dashboard.
6. Collapse the sidebar; use icon rail links for Dashboard, Scan / Add, Command Center, Money, Reports, Properties, Units, Tenants, Leases, Applications, Work Orders, Appointments, Vendors, Messages, Tenant notices, Ask, Help, Settings, Team, Owners, and Activity history where visible.
7. Expand the sidebar; open each nav group and click every visible child link.
8. Resize to a mobile viewport, open the drawer, click a route, reopen it, then close via overlay.
9. Open the account menu and click Sign Out; confirm `/login` and no authenticated shell remains visible.

## Evidence To Capture

- Playwright snapshots for desktop expanded nav, collapsed rail, notification dropdown, staff security route, mobile drawer, and signed-out login.
- Request log showing route data/API requests returning 2xx for route loads.
- Console log showing 0 app errors/warnings, except intentional 4xx probes if a step explicitly tests them.
