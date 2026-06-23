# TSK-397 Scan Front-Door Continuation UI Guide

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-28`
Local target: `https://localhost:6042`

## Scope

Exercise the scan front door as a live landlord user after the scan rejection reason fix has landed to `main`.

## Setup

- User: `tsk397.pass26.202606231127@example.local`
- Use the existing live portfolio data created from synthetic scan fixtures.
- Generated fixtures live in `output/qa/production-scale-scans`.

## Actions And Assertions

1. Open `/scan`.
   - Assert the document type picker, upload drop zone, voice-note control, batch import summary, and scan history tabs render.
   - Assert no warning-or-higher console messages caused by page load.
2. Try an unsupported file upload with a text or manifest file.
   - Assert no draft is created.
   - Assert the UI gives visible feedback that the file type is unsupported.
   - Assert the scan history row count does not increase.
3. Open Pending, Reviewing, Confirmed, Failed, and Rejected filters.
   - Assert each URL/status filter changes the server request to `/api/v1/scans/page`.
   - Assert empty states distinguish filtered emptiness from first-run setup.
4. Sort Confirmed and Rejected history by Type and Created.
   - Assert URL sort params change and rows remain stable after reload.
5. Open a confirmed Lease, Payment, Expense, Application, and Work Order scan row.
   - Assert each row reaches the correct created-record page.
   - Assert the page shows the scanned document/source image, key extracted fields, and history/timeline if available.
6. Open rejected scan `9`.
   - Assert reject reason text is visible.
   - Assert review inputs and confirm/retry controls are disabled or absent.
7. Click `Record voice note` with microphone permission denied.
   - Assert visible recovery/status exists. If not, log `TSK397-B048` as still open.

## Evidence To Record

- Browser route, row/draft/record ids, and screenshot paths for any newly captured evidence.
- Network request proof for server-side scan history filtering/sorting.
- SQL proof only when browser state is ambiguous.
