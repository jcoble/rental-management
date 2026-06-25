# TSK-397 Pass 87 - Audit Inspection Detail Links

Date: 2026-06-25
Branch: `tsk-397-430-real-user-pass-87-audit-inspection-links`
Scope: Real-user verification for Notion `TSK-430` / prior bug `TSK397-B086`.

## Acceptance Criteria

- Activity history inspection rows link to the canonical web inspection detail route: `/maintenance/inspections/{id}`.
- Clicking an Activity history inspection row loads the inspection detail page, not the SvelteKit 404 page.
- Admin forensic audit inspection entity links use the same canonical route.
- Admin forensic audit expanded-row `Open record` links use the same canonical route.
- Existing backend route-mapping regression tests stay green.

## Edge Cases Checked

- Cold guarded-route login redirect to `/audit`.
- Cold guarded-route login redirect to `/admin/audit`.
- Landlord-facing audit row click.
- Admin forensic inline entity badge link target.
- Admin forensic expanded `Open record` link click.

## Evidence

- Activity history row proof: `output/playwright/pass87-activity-inspection-row.png`
- Activity row click proof: `output/playwright/pass87-activity-inspection-detail.png`
- Admin forensic disclosure proof: `output/playwright/pass87-admin-audit-inspection-disclosure.png`

Browser findings:

- `/audit` showed inspection rows with `Inspection #1` linked to `/maintenance/inspections/1`.
- Clicking the first Activity history inspection row landed on `https://localhost:6042/maintenance/inspections/1` with title `MoveIn inspection - Rental Command`.
- `/admin/audit` showed `Inspection #1` linked to `/maintenance/inspections/1`.
- Expanding the first admin inspection row showed `Open record -> /maintenance/inspections/1`.
- Clicking `Open record` landed on `https://localhost:6042/maintenance/inspections/1` with title `MoveIn inspection - Rental Command`.

Regression test:

```bash
MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter FullyQualifiedName~AuditDetailHrefTests -m:1 --no-restore
```

Result: Passed, 2/2. Existing NU1903 warnings for `SQLitePCLRaw.lib.e_sqlite3` remained unchanged.

## Result

Pass. No product code change was needed for this slice because `AuditEntryResponse.BuildDetailHref("Inspection", id)` already emits `/maintenance/inspections/{id}` on current `main`, and both audited UI surfaces use that value.
