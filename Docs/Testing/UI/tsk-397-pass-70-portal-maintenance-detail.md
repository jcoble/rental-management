# TSK-397 Pass 70 Portal Maintenance Follow-Through And Validation

Date: 2026-06-25
Branch: `tsk-397-full-ui-pass-70`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

## Preconditions

- Local stack is running:
  - Web: `https://localhost:6042`
  - API: `https://localhost:6041` (`http://localhost:6040`)
  - DB: `rentalcommand_tsk397_pass26_clean` on host port `5583`
- Tenant portal account:
  - Email: `blake.hayes.portal.pass55@example.local`
  - Password: `TenantPass!23`
- Existing request from Pass 69:
  - `WorkOrders.Id = 7`
  - Title: `TSK-397 Pass 69 dashboard quick maintenance validation proof`
- Camera-style image fixture:
  - `output/qa/production-scale-scans/05-work-orders-camera/work-order-002.jpg`

## Detail Follow-Through Steps

1. Open `https://localhost:6042/portal` as Blake Hayes Portal.
2. Confirm the dashboard Maintenance card shows `3` open requests.
3. Click the Maintenance card.
4. Confirm `/portal/maintenance` lists `TSK-397 Pass 69 dashboard quick maintenance validation proof` first with `Normal` and `New`.
5. Open that request.
6. Confirm the detail modal shows:
   - Title: `TSK-397 Pass 69 dashboard quick maintenance validation proof`
   - Status: `New`
   - Priority: `Normal`
   - Description: `Kitchen cabinet door hinge is loose. Submitted from the tenant dashboard quick form after validation proof.`
   - Empty document state before upload.
7. Click `Upload` in the Photos & documents panel.
8. Upload `output/qa/production-scale-scans/05-work-orders-camera/work-order-002.jpg`.
9. Confirm the panel lists `work-order-002.jpg` as an image attachment.
10. Click the attachment name.
11. Confirm the file downloads and matches the uploaded image bytes.

## Validation Steps

1. Stay on `https://localhost:6042/portal/maintenance` as Blake Hayes Portal.
2. Leave `What's the problem?` and `Describe the issue so we can fix it fast` blank.
3. Click `Submit Request`.
4. Confirm both fields show invalid state.
5. Confirm inline errors show:
   - `Issue title is required.`
   - `Describe the issue before submitting.`
6. Fill title `TSK-397 Pass 70 full maintenance validation proof`.
7. Fill description `Bedroom closet door track keeps sticking after the last repair visit. Submitted from the full tenant maintenance page after validation proof.`
8. Click `Submit Request`.
9. Confirm the form clears, `Maintenance request submitted.` appears, and the new request is first in the list as `New` / `Normal`.

## Evidence To Record

- Browser snapshot after list navigation.
- Browser snapshot after detail open.
- Browser snapshot or screenshot after upload.
- Network proof for:
  - `GET /api/v1/portal/work-orders/7 => 200`
  - `GET /api/v1/documents?entityType=WorkOrder&entityId=7 => 200`
  - `POST /api/v1/documents => 201`
  - `GET /api/v1/documents/44/file => 200`
- DB proof from `StoredFiles` for the uploaded file.
- SHA-256 comparison between source fixture and downloaded file.
- Browser snapshot and screenshot for the empty-submit validation state.
- Browser snapshot and DB proof for the valid maintenance submit after validation.
