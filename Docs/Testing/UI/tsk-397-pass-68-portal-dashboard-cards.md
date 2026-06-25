# TSK-397 Pass 68 Portal Dashboard Cards

## Scope

Verify the tenant portal dashboard behaves like a real tenant landing page for existing local pass data:

- due-today rent is outstanding but not overdue
- upcoming appointments are visible from the dashboard
- appointment rows navigate to the tenant appointments route
- existing dashboard notifications still navigate without leaving unread state stale

## Setup

- Local API: `http://localhost:6040`
- Local web: `https://localhost:6042`
- Tenant user: `blake.hayes.portal.pass55@example.local`
- Existing appointment fixture: `Pass 51 service visit date fixed`
- Existing overdue rent rows: `$1,200.00` and `$12.34`, due June 24, 2026
- Synthetic due-today rent row: `$22.25`, due June 25, 2026, note `TSK-423 due-today browser proof`

## Steps

1. Sign in as the tenant and open `/portal`.
2. Confirm the Overdue card shows `$1,212.34` and `2 overdue items`; the due-today `$22.25` row must not be included in that total.
3. Confirm the Next Rent card shows `0` days and references the due-today `$22.25` rent amount.
4. Confirm the Payments panel lists the due-today `$22.25` row as due today (`0 days`), while older June 24 rows remain late.
5. Confirm the Appointments panel shows `Pass 51 service visit date fixed`, its time window, `Riverside Flats Unit 2B`, `Maintenance`, and `Scheduled`.
6. Click the dashboard appointment row.
7. Confirm navigation lands on `/portal/appointments` and the same appointment is visible there.

## Failure Rules

- A due-today scheduled payment counted as overdue fails this guide.
- An appointment that exists in the tenant-scoped API but is absent from the dashboard fails this guide.
- Navigation through a dashboard appointment row that does not reach the tenant appointments route fails this guide.
