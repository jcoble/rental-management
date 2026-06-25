# TSK-397 Pass 83 - Public Apply And Signing Edge States

Date: 2026-06-25
Branch: `tsk-397-real-user-pass-83`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

## Scope

Real-user verification of anonymous public rental-application links and anonymous native signing links.

Covered routes and controls:

- `/apply/:token`
- `/sign/:token`
- Public application invalid-token page
- Public application ID/image scan upload
- Public application required-field validation
- Public application duplicate-email conflict
- Public signing already-signed terminal page
- Public signing active decline modal
- Public signing declined terminal page
- Public signing reused-token action rejection

Deferred boundaries:

- No production data, sensitive data, real email/SMS delivery, Plaid, connected QuickBooks/accounting provider workflow, or final Go Live action was used.
- No production signing provider was used; this pass used the local native signing engine.

## Acceptance Criteria

- Invalid public application links show a clear terminal error page and do not render the form.
- Valid public application links render the scan control, property/unit selectors, required applicant fields, optional applicant fields, consent checkbox, and submit action for anonymous users.
- Image upload can accept a camera-like image file and, when extraction returns no fields, shows a manual-entry fallback without blocking the form.
- Empty submit keeps the user on the form and shows finite required-field/consent validation messages.
- Duplicate open applications in the same portfolio show the backend domain reason, not a generic HTTP title.
- Already-signed public signing links render a terminal signed state with document access but no sign/decline controls.
- Active signing links expose consent, typed/drawn signature controls, sign action, and decline action.
- Declining through the modal writes a declined terminal state and survives reload.
- Reusing a terminal signing token for a signing action returns 410 and does not mutate the terminal state.
- A clean public apply load has no browser console warnings/errors and no failed dynamic app requests.

## Browser Evidence

Environment:

- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Invalid public application token:

- Opened `https://localhost:6042/apply/not-a-real-token-pass83`.
- Verified terminal copy: `This link isn't working` and `This application link is invalid or has expired. Please contact the property manager for a new link.`
- Browser console recorded only the expected resource-load 404 for this deliberate negative path.
- Screenshot proof: `output/playwright/pass83-public-apply-invalid-link.png`.

Valid public application and image scan fallback:

- Opened `https://localhost:6042/apply/gC5Ob2CvcwMqZGq65xNRrfZuTVwSvr11iR7a9J1kJmg`.
- Verified `Apply to Avery Rowan Pass 26`, scan ID control, property/unit selectors, applicant details, address fields, employment fields, notes, FCRA consent, and submit action.
- Uploaded `output/playwright/pass83-unreadable-id.png`, a 1500x1125 PNG generated from a static app image to mimic a camera/file image that contains no extractable ID fields.
- Request proof: `POST /api/v1/public/applications/{token}/scan-id => 200`.
- Verified fallback copy: `We couldn't read details from that image automatically. No problem - just fill in the form below.`
- Screenshot proof: `output/playwright/pass83-public-apply-scan-fallback.png`.

Required-field validation:

- Submitted the public application form empty.
- Verified inline validation for missing first name, last name, email, phone, and consent.
- The user remained on the same form and no record was created.

Duplicate public application:

- Submitted the valid public application form with existing local applicant email `qa.applicant.001@example.local`.
- Before the fix, the UI showed only `Conflict` even though the API returned domain-specific ProblemDetails.
- After the fix, the UI showed: `An application for qa.applicant.001@example.local already exists as application #1. Review the existing application before creating another.`
- Request proof: `POST /api/v1/public/applications/{token} => 409`.
- Screenshot proof after fix: `output/playwright/pass83-public-apply-duplicate-fixed.png`.

Already-signed public signing link:

- Opened `https://localhost:6042/sign/4TldKZPIcxUNB1ovGGcsiXR4BLSy8ABIyyHl5j-zkd0`.
- Verified terminal copy: `Signed - all done`, signed signer name, document subject `Lease RC-2B-DRAFT-45`, and `Open the document`.
- Verified no sign/decline controls were exposed.
- Screenshot proof: `output/playwright/pass83-sign-already-signed.png`.

Declined public signing link:

- Opened pending token `https://localhost:6042/sign/wE9rVlM56Sv8nF3SjL-zwbZvVR_arNyQ8BXVhEmmccA`.
- Verified active signing controls for consent, typed signature, disabled sign button until consent, and `Decline to sign`.
- Opened the decline modal, entered `TSK-397 Pass 83 decline terminal proof.`, and confirmed.
- Request proof: `POST /api/v1/sign/{token}/decline => 200`.
- Verified terminal copy: `You declined to sign` and no remaining sign/decline controls.
- Reloaded the same URL and verified the declined terminal state persisted.
- Screenshot proof: `output/playwright/pass83-sign-declined-terminal.png`.

Reused signing token action:

- Local reuse attempt:

```bash
curl -k -sS -D - -o /tmp/pass83-sign-reuse.json \
  -X POST 'https://localhost:6042/api/v1/sign/wE9rVlM56Sv8nF3SjL-zwbZvVR_arNyQ8BXVhEmmccA' \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json' \
  --data '{"consent":true,"signatureType":"Typed","typedName":"Pass Thirtyeight"}'
```

- Response proof: `HTTP/2 410` with body `{"error":"This signing request was declined."}`.
- DB proof:

```sql
select s."Id", s."Status" as signer_status, r."Status" as request_status, s."ViewedAtUtc", s."SignedAtUtc"
from "SignatureSigners" s
join "SignatureRequests" r on r."Id" = s."SignatureRequestId"
where s."Token" = 'wE9rVlM56Sv8nF3SjL-zwbZvVR_arNyQ8BXVhEmmccA';
```

- Result: signer `2`, signer status `Declined`, request status `Declined`, viewed timestamp present, signed timestamp null.
- Audit proof: latest events for signer `2` were `Declined | Pass Thirtyeight declined to sign: TSK-397 Pass 83 decline terminal proof.` and `Viewed | Pass Thirtyeight opened the signing page.`

Clean final signal:

- Closed the browser context, reopened only the valid public application link, and checked console/network.
- Console proof: `Total messages: 2 (Errors: 0, Warnings: 0)` with only Vite debug messages.
- Network proof: dynamic requests were `GET /apply/{token}/__data.json => 200` and `GET /api/v1/public/applications/{token} => 200`.

## Bugs Found And Fixed

### TSK397-B087 - Public application duplicate errors showed only `Conflict`

Severity: medium

Reproduction:

1. Open a valid public application link.
2. Fill required fields using an email that already has an open application in the same portfolio.
3. Submit the form.

Observed before fix:

- API returned `409`.
- UI showed only `Conflict`.

Expected:

- UI should show the domain validation detail explaining that the email already has an application and which application to review.

Root cause:

- `web/src/lib/api/public-applications.ts` used a public-only error parser that checked `ProblemDetails.title` but did not check `ProblemDetails.detail`.
- The authenticated API client already had the correct precedence for domain-specific `detail`; the public application client had drifted from that behavior.

Fix:

- Extracted `readPublicError()` into `web/src/lib/api/public-error.ts`.
- Updated public application context, scan, and submit calls to use that parser.
- The parser now prefers nested `error.message`, then `ProblemDetails.detail`, then `message`, then `title`.

Regression:

- Added `web/src/lib/api/public-error.test.ts`.
- RED before fix: `node --test --experimental-strip-types src/lib/api/public-error.test.ts` failed because the parser module did not exist.
- GREEN after fix: the same focused test passed and verified `ProblemDetails.detail` is preferred over generic `title`.
- Browser rerun confirmed the duplicate application form now shows the full domain message.

Verification commands:

- `node --test --experimental-strip-types src/lib/api/public-error.test.ts` - passed.
- `pnpm --dir web check` - passed with 0 errors and 4 existing unused-CSS warnings in `web/src/lib/components/m3/PageHeader.svelte`.
- `pnpm --dir web test:unit` - passed 235/235.
- `git diff --check` - passed.

## Status

Pass after fixing `TSK397-B087`. Continue the real-user audit with the next remaining non-provider-bound route group. Deferred boundaries remain production/sensitive data, real SMS/email delivery, Plaid banking, connected QuickBooks/provider accounting, and final Go Live.
