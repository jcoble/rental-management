# Rental Command Batch 2 receipt

## Scope

- E2E run: `2026-08-09-001`
- Batch: `2 — authentication and invitation safety`
- Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-batch2-auth-safety`
- Branch: `tsk-batch2-auth-safety`
- Receipt was initialized before implementation in this worktree.
- Requested external receipt path `/Users/blackcolours/dev/work/reports/rental-command/evidence/batch2-receipt.md` is outside the writable sandbox; the attempted write was denied. This file is the complete fallback receipt.

## Findings

- S10-BUG-1 — fixed. Identity lockout is explicit in `Program.cs`, the `ApplicationUser` invariant, and all five production account-construction paths. Migration `20260809010000_EnableIdentityLockout` backfills existing `AspNetUsers` rows and has a fail-closed down path. The regression test covers attempts 1–4, the fifth/sixth lockout response, and successful login after expiry.
- S10-BUG-2 — fixed. Email-confirmation tokens are validated before the already-confirmed shortcut. Invalid-token and unknown-user outcomes use the same neutral bad-request message and error type; the regression test compares both outcomes.
- S10-BUG-4 — fixed. The post-baseline RLS authority refresh had overwritten the auth-session revoke audit admission. Migration `20260809011000_RestoreAuthSessionRevokeAudit` restores the narrow same-transaction admission so logout revokes credentials and writes its audit row atomically.
- S10-BUG-3 — fixed. The forgot-password action now treats non-2xx and network failures as retryable `503` form failures, preserves the submitted email, and only returns neutral success after a successful API response. The page rehydrates the preserved email on action failure.
- S10-POT-1 — excluded by request; no terms/privacy product decision was invented.
- S10-POT-2 — not changed. The activation page has only a token-consuming POST endpoint; no safe invitation-preview endpoint exists from which to obtain email/workspace/role/property context. Adding that context would require a new product/security contract rather than a mechanical fix. Existing missing/malformed-link handling remains neutral.

## Verification

- Notion task-list lookup: blocked by DNS resolution for `api.notion.com`; continuing under the supplied batch contract.
- Baseline command before the fixes: `MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests --filter "FullyQualifiedName~Auth"` — `Failed: 6, Passed: 310, Total: 316`. The six failures were the three requested auth regressions plus the three named pre-existing failures.
- Focused post-fix command: `MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests --filter "FullyQualifiedName~CanonicalRegistrationBootstrapTests"` — `Failed: 0, Passed: 5, Total: 5`.
- Final required command: `MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests --filter "FullyQualifiedName~Auth"` — `Failed: 3, Passed: 313, Skipped: 0, Total: 316`. The only failures were the explicitly exempt `AnalyticsServiceAuthorizationTests` (2) and `BankingServiceTests.MatchAsync_ConcurrentSameOperation_ReplaysOneAuthorizedReconciliation` (1).
- Red-first web contract test initially failed because the action had no `response.ok`/retryable-failure branch. After the fix, `pnpm --dir web exec node --test --experimental-strip-types src/routes/forgot-password/forgot-password-action.test.ts` passed (`1` test).
- `pnpm --dir web test:unit` passed (`812` tests, `0` failures). This run used a temporary read-only dependency link to the existing sibling worktree and the link was removed immediately afterward.
- `pnpm --dir web check` passed with `0 ERRORS` and `18` pre-existing warnings. `pnpm --dir web check:native` exited `0`; both checks encountered the sandbox's Vite `.vite-temp` `EPERM` and used SvelteKit's config fallback. The temporary dependency link was removed after each check.
- `MSBUILDDISABLENODEREUSE=1 dotnet build RentalCommand.sln --no-restore --disable-build-servers -m:1 -nr:false` passed: `0 Error(s)` (55 warnings, including pre-existing nullability/conflict warnings and offline NuGet vulnerability-feed warnings).
- `dotnet build-server shutdown` is required as the final cleanup step after the commit.
- Commit: created with the exact requested message; no push performed.
