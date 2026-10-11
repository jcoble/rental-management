# Foundation remediation final evidence

Date: 2026-07-25  
Task: TSK-733, continuing TSK-668/670/672/674 foundation work

## Source and deployment identity

- Local base SHA: `77c69102bb440d7c47a69cdf62bb2848712f1d5f`
- Final Azure transport snapshot: `934a64370b352f329e7f7d8f72b7ba36de104e29`
- Final Azure transport tree: `78206565252f2276b162e9402b1877b9871f46b1`
- Stable preview: `https://redacted-host.example.invalid`
- Preview health: `{"status":"ok"}`
- Android APK SHA-256: `38d40e06ce870a9cb4ffa3a1a571e91ccd90fa18e207e3591261812fac444724`
- Local Git was not staged, committed, stashed, reset, cleaned, or reverted.

The transport commits exist only in the temporary Azure handoff repository. They identify the
deployed dirty source without changing local Git history.

## Automated acceptance

- API exact acceptance: 9/9 passed.
- Data exact acceptance: 28/28 passed.
- Authorization fail-closed focused integration test: 1/1 passed.
- Authorization clock regression focused integration test: 1/1 passed.
- Web native TypeScript check: passed.
- Svelte check: 0 errors and 23 pre-existing warnings.
- Web unit acceptance artifact: 587/587 passed.
- Google cookie/auth focused tests after the OAuth correction: 6/6 passed.
- Scan auto-classification upload focused test: 1/1 passed.
- Unit transfer server-page PostgreSQL test: 1/1 passed.
- Unit transfer remote-selector contract tests: 7/7 passed.

The broader Scan controller class compiled, but 22 tests could not run against its SQLite fixture
because that fixture does not provide the PostgreSQL
`public.rc_api_effective_capability_scopes` function. The exact new test and the deployed
PostgreSQL flow both passed.

## Performance

| Flow | Samples | Result |
| --- | ---: | --- |
| Dashboard endpoint | 10 | p50 0.912 s; p95 0.936 s |
| Accounting snapshot | 10 | p50 0.520 s; p95 0.542 s |
| Dashboard occupancy SQL | 10 | p50 18.018 ms; p95 19.573 ms |
| Dashboard tenant-money SQL | 10 | p50 168.161 ms; p95 178.556 ms |
| Dashboard recent-activity SQL | 10 | p50 130.264 ms; p95 135.642 ms |
| Security Deposits route | 10 | 1.266–1.452 s total |
| Security Deposits data endpoint | 10 | 0.912–1.063 s to response start |
| Scan batches endpoint | 10 | 0.341–0.383 s; p50 0.359 s; p95 0.383 s |

Security Deposits previously took about 10.6 seconds because its count query repeated expensive
authorization work. The corrected common path uses one DB-side count query. A forced three-second
refresh left all 20 cached rows enabled, with no blocking overlay, proving that background
revalidation no longer disables the grid.

The shared workspace authorization query also evaluated the same expensive capability scope twice;
it now evaluates the scope once. The Android emulator consumed substantial RAM and swap and made
local builds and tools slower, but it could not cause a browser HTTP endpoint itself to take 10.6
seconds. Both code/query cost and machine pressure contributed to the earlier overall experience.

## Web behavior

- An unauthorized role is redirected to its safe landing page. It does not receive a visible 403
  page, and navigation does not expose links to routes it cannot use.
- Lease detail now has 24 px horizontal padding, 16 px top spacing, a stable scrollbar gutter, and
  a working scroll container. Runtime geometry proved `clientHeight=803`, `scrollHeight=1671`, and
  `scrollTop=868`.
- Accounting Transactions uses a responsive two-row filter toolbar. All seven controls are 44 px
  high, vertically aligned, and contained without horizontal overflow at 1440 by 900.
- Security Deposit rows remain interactive while fresh data is fetched in the background.
- Accounting summary failures show a useful alert and Retry action; a forced 503 recovered after
  retry.
- Tenant lease failures have an explicit failure and Retry state in source. The preview tenant
  credential expired during the API recreation, so a final authenticated tenant runtime re-proof
  was not possible without changing user data.
- Google sign-in now completes through the existing Google service instead of returning a hardcoded
  503. After the corrected API and web images were recreated on the stable preview, the user
  completed the real Google authorization flow and confirmed it reached the authenticated app.
- AI Help links to and renders the provider-configuration article.
- Scan / Add accepts a missing target as automatic classification. A real upload created and
  processed a draft, classified it as an Expense, and displayed the useful configured-provider
  message when the workspace had no OpenAI or Anthropic credential.
- Scan batch summaries are one server-side grouped SQL query and no longer repeat the authorization
  subquery seven times.
- The occupied-relationship transfer dialog uses a remote, server-paged Unit selector instead of a
  fixed first-100 list. Live browser proof returned 200 for
  `/api/v1/units/page?take=20&search=Right&sort=unitNumber&availableForLease=true&excludeUnitId=1`
  and rendered only the matching available Unit.

Visual evidence:

- `rc-foundation-final-20260725T004022Z/lease-layout-fixed.png`
- `rc-foundation-final-20260725T004022Z/accounting-filters-fixed.png`
- `rc-foundation-final-20260725T004022Z/unit-transfer-remote-search-live.png`

## Final review gates

- Code review: passed after the Unit transfer selector correction; no remaining blocker.
- Foundation compliance review: passed after the same correction; the remote selector keeps
  search, availability filtering, ordering, count, and paging in translated PostgreSQL queries.

## Android behavior

The latest APK installed and signed in on Azure `emulator-5554`. The Unit command center exposed
exactly these six destinations:

1. Summary
2. Leasing
3. Tenant & lease
4. Money
5. Maintenance
6. Documents & history

The associated PNG and UI hierarchy evidence is under
`rc-foundation-final-20260725T004022Z/`.

## Intentionally left for the separate end-to-end pass

- Tenant SignalR latest-update proof, original signed lease PDF download, and plural overdue copy
  need a fresh authorized tenant fixture with the corresponding records.
- Outbound email, SMS, and Places smoke checks need controlled recipient/provider accounts.
- These checks were not broadened or simulated here. Per the handoff boundary, the separate
  `run-e2e-tests` workflow has not been started.
