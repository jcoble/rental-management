# TSK-397 Pass 82 - Docs And Help Surface

Date: 2026-06-25
Branch: `tsk-397-real-user-pass-82`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

## Scope

Real-user verification of the public documentation and authenticated in-app Help entry point.

Covered routes and controls:

- Anonymous `/docs`
- Anonymous `/docs/:slug`
- Missing article `/docs/qx-missing-doc-1782364211`
- Tenant portal header Help link to `/docs`
- Authenticated docs header `Back to app`
- Docs search input, filtered category cards, empty search state, category/article links, article breadcrumb, article left nav, generated article body, prev/next article cards, and right-rail table-of-contents anchors

Deferred boundaries:

- No production docs content was edited.
- No provider-bound banking/accounting/QuickBooks/Plaid workflow was exercised.
- No production data, sensitive data, SMS/email delivery, or Go Live action was used.

## Acceptance Criteria

- Anonymous users can open the docs index and docs articles without the authenticated app shell.
- Anonymous docs header exposes public `Sign in` and `Get started` actions.
- Search filters the finite docs index by title, summary, and category, updates the match count, and hides unrelated categories.
- Empty search shows a clear no-results state without a crash or stale results.
- Article pages render the trusted, scrubbed markdown body with breadcrumb context, persistent left navigation, generated table of contents, and prev/next navigation.
- Table-of-contents anchors update the hash and scroll the docs scroll container to the selected heading.
- Missing article slugs show the docs error page with a recovery action back to `/docs`.
- Signed-in users who enter docs from the app Help link see `Back to app`, not public auth CTAs.
- Tenant users clicking `Back to app` return to `/portal` through normal role-aware routing.
- Clean docs index load has no browser console warnings/errors and no failed dynamic app requests.

## Browser Evidence

Environment:

- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Anonymous docs index:

- Opened `https://localhost:6042/docs`.
- Verified public docs shell with `Rental Command / Docs`, `Sign in`, `Get started`, persistent documentation nav, search, start-here card, and 30 total article entries across categories.
- Screenshot proof: `output/playwright/pass82-docs-index.png`.

Search:

- Query `lease` returned `9 of 30 articles match "lease"`.
- Visible filtered categories were `Tenants & Leases` (4), `Money` (2), `Scan & Intake` (1), `Mobile` (1), and `Tenant Portal` (1).
- Query `qx-no-doc-match-1782364200` returned `0 of 30 articles match "qx-no-doc-match-1782364200"` and showed `No articles match ... Try a different word or clear the search.`

Article reader:

- Opened `/docs/welcome`.
- Verified breadcrumb `Docs / Getting Started / Welcome to Rental Command`, title, summary, rendered article body, left navigation, `On this page` rail, and `Next Getting Started`.
- Clicked `Next`; `/docs/getting-started` rendered `Previous Welcome to Rental Command`, `Next How Scanning Works`, and regenerated TOC items for the new article.
- Clicked TOC item `Set up your real portfolio`; browser location became `https://localhost:6042/docs/getting-started#set-up-your-real-portfolio` and the target heading landed at `95.671875px` from the viewport top, below the sticky docs header.

Missing article:

- Opened `/docs/qx-missing-doc-1782364211`.
- Verified `Article not found`, `That documentation article could not be found.`, and `Back to docs`.
- Browser console recorded the expected resource-load line for the deliberate route response: `Failed to load resource: the server responded with a status of 404`.
- Clicked `Back to docs`; returned to `/docs`.

Authenticated Help loop:

- Signed in as tenant-only Blake Hayes Portal (`blake.hayes.portal.pass55@example.local`) with local test password.
- Login landed on `/portal` with portal-only navigation.
- Clicked tenant app header Help icon; navigated to `/docs`.
- Verified authenticated docs header showed `Back to app` instead of `Sign in` / `Get started`.
- Clicked `Back to app`; role-aware root routing returned to `/portal`.

Clean final signal:

- Closed the browser context, reopened only `https://localhost:6042/docs`, and checked console/network.
- Console proof: `Total messages: 2 (Errors: 0, Warnings: 0)` with only Vite debug messages.
- Network proof: no dynamic failed app requests were listed; the Playwright request log reported only static requests hidden.

## Bugs Found

None in this pass.

## Status

Pass with documentation-only verification. No code fix or regression test was added for this slice.
