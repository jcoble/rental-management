# Exploratory Test Report: Foundation Authentication, Onboarding & Roles

Date: 2026-07-15  
Tester: `foundation-auth`  
Starting source SHA: `9a20158a4dbc0e3ac177ddd75d369e5c65ab55ec`  
Environment: `https://rental-command.chimp-map.ts.net`  
Status: Failed at authentication release gate; downstream walkthrough pending refreshed preview

## Scenario

`Docs/Testing/Scenarios/07-foundation-auth-onboarding-roles.md`

## Summary

The first live UI action exposed a release-blocking preview defect. Password authentication itself
works: a direct API login using the seeded administrator returned 200. The real browser form never
reached that API, however, because SvelteKit rejected the public same-origin POST with HTTP 403. The
page then rendered no error. Google was also absent because the preview hard-coded its client ID and
secret empty instead of loading the already-available protected integration configuration.

The preview database is a clean-reset state, not the earlier shared test state: it contains one
seeded administrator and no other identity, Property, Unit, or Tenant records. A previously created
“test customer” therefore cannot sign in and must not be used to judge password correctness.

## Section status

| Section | Status | Evidence / blocker |
|---|---|---|
| A. Login failures/recovery/valid password | **Fail** | Browser `POST /login` returned 403 with no visible error; direct API login returned 200 |
| B. Google and access contexts | **Fail / Blocked** | Google button absent; preview runtime values are empty. Context selection awaits login repair |
| C. First-run sample/live choice | **Blocked** | Cannot enter the protected UI through the browser form |
| D. Guided setup | **Blocked** | Cannot enter the protected UI through the browser form |
| E. Team presets/activation/scope | **Blocked** | Cannot enter the protected UI; clean DB has no invited role personas |
| F. Sole landlord/Owner/Tenant/account entry | **Blocked** | Cannot enter the protected UI; clean DB has no relationship personas |

## Bugs found

### BUG-1 — Critical — Stable public login POST is rejected before reaching the API

- **Role/route:** anonymous, `https://rental-command.chimp-map.ts.net/login`
- **Reproduction:** open the stable login page, enter the seeded administrator email/password, and
  press **Sign In**.
- **Expected:** the form action validates credentials, sets secure first-party cookies, and navigates
  to first-run choice or the authorized management landing page.
- **Actual:** the button briefly shows submission, the page remains `/login`, and the form request
  returns HTTP 403 with `Cross-site POST form submissions are forbidden`.
- **Corroboration:** direct `POST /api/v1/auth/login` with the same credentials returned HTTP 200;
  gateway logs showed `POST /login` 403 and no matching browser login request reached the API.
- **Root cause:** `scripts/remote/preview-stack.sh` wrote the private HTTP host/port into `WEB_ORIGIN`.
  `deploy/docker-compose.preview.yml` supplied it as SvelteKit `ORIGIN`, although the browser uses the
  stable public HTTPS service origin.
- **Suggested fix:** set `WEB_ORIGIN` to exactly
  `https://rental-command.chimp-map.ts.net`; retain the private host/port only for health checks and
  metadata. Add valid-admin navigation as a release check.
- **Business impact:** nobody can enter the preview through the real web UI, so no downstream user
  acceptance or role walkthrough is meaningful.

### BUG-2 — High — Preview drops Google, Places, and assistant integration configuration

- **Role/route:** anonymous login and management scan/address flows
- **Reproduction:** open `/login`; inspect runtime configuration by key presence only.
- **Expected:** protected persistent preview integration settings propagate to API/engine/web while
  outbound email/SMS/push delivery remains suppressed.
- **Actual:** no Google button is rendered. Preview compose hard-codes Google client ID/secret,
  `PUBLIC_GOOGLE_CLIENT_ID`, Google Places key, and assistant key empty. The persistent preview env
  contains only database/JWT values.
- **Root cause:** `deploy/docker-compose.preview.yml` deliberately replaced all these values with
  empty strings and `scripts/remote/preview-stack.sh` had no protected integration env source.
- **Suggested fix:** persist an allowlisted mode-600 `integrations.env` outside disposable source,
  propagate Google/Places/assistant settings, validate paired Google values, and report only
  enabled/disabled status. Never load delivery-provider secrets for this preview.
- **Business impact:** Google login is missing and scan/address workflows cannot be evaluated even
  though the user expects the preview to represent the product configuration.

### BUG-3 — High — Login transport failure is invisible

- **Role/route:** anonymous, `/login`
- **Reproduction:** submit the login form while the action transport returns a non-SvelteKit 403.
- **Expected:** a keyboard/screen-reader accessible actionable alert distinguishes connectivity/
  runtime failure from invalid credentials, and the spinner always resets.
- **Actual:** no alert is rendered; the button briefly spins and returns, leaving the user with no
  explanation.
- **Root cause:** the enhanced form callback always called `update()` and had no explicit branch for
  an action `result.type === 'error'` transport result.
- **Suggested fix:** render a `role="alert"` transport error and reset submission state in `finally`.
  Retain server action failures for accessible `Invalid email or password` rendering. Add regression
  coverage for unknown account, wrong password, raw transport failure, and valid navigation.
- **Business impact:** even a temporary runtime mistake looks like a dead button and prevents users
  from diagnosing or reporting the actual problem.

### BUG-4 — Medium — Clean-reset identity state was not made explicit to UI testers

- **Role/route:** all role/account login tests
- **Reproduction:** attempt to sign in using an identity created in an earlier preview database.
- **Expected:** the run handoff identifies available test personas or provides an intentional safe
  way to create/activate them.
- **Actual:** the database contains one user—the seeded administrator—and zero other users,
  Properties, Units, or Tenants. Old “test customer” credentials cannot work, but the login UI cannot
  currently distinguish that from BUG-1 because its error is invisible.
- **Evidence:** one server-side SQL aggregate query reported total users `1`, seeded admins `1`,
  other users `0`, confirmed emails `1`; separate server-side counts reported Properties `0`, Units
  `0`, Tenants `0`.
- **Suggested fix:** record current seeded/test identities in each preview run without exposing
  passwords beyond designated development credentials. Build the required role/relationship personas
  through the product flows after authentication is restored.
- **Business impact:** testers otherwise reuse erased identities, misdiagnose login, and cannot
  perform reliable role-specific acceptance.

## What was tested

- Live login page at desktop viewport using Playwright CLI snapshots only; no screenshot, video, or
  trace was captured.
- Google button discoverability: absent.
- Visible password submission with the seeded administrator: remained on `/login`, no error.
- Browser console/network: failed login form request with status 403.
- Same-origin HTTP reproduction: response body was
  `Cross-site POST form submissions are forbidden`.
- Direct API password login: success (200), proving the credential and API authentication path.
- Container/runtime configuration by key presence and non-secret enabled/disabled state.
- Database identity/domain counts using DB-side aggregate queries.

## Not yet tested

No pass is claimed for invalid-password copy, Google OAuth completion, first-run choice, sample
seeding/graduation, Guided Setup, Team activation/scoping, sole-landlord Owner relationship, Owner or
Tenant access, account/security entry, or mobile role shells. These resume against the refreshed SHA
using Playwright CLI session `foundation-auth` after the orchestrator confirms the stable preview is
login-ready and states which test identities/configuration are available.

## Created test data

None. The failing login POST did not reach the API.
