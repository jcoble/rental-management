# Scenario 30 — Public marketing, help/docs, privacy, blog, application links, and file delivery

## Purpose

Finish the public route surface and verify that visitors, applicants, signers, and document recipients see a coherent unauthenticated experience with safe token/file boundaries and working navigation.

## Preconditions and login

Use a clean unauthenticated browser context. Public docs/blog/features/privacy are read-only. Use only synthetic QA application/sign/file tokens; do not open a real tenant’s private document in a public context.

## Read the implementation first

Before opening a browser, read these named source files and the relevant controller/service code they call:

- web/src/routes/features/+page.svelte, web/src/routes/(public)/blog/+page.svelte, web/src/routes/(public)/blog/[slug]/+page.svelte, web/src/routes/(public)/privacy/+page.svelte
- web/src/routes/(public)/docs/+layout.svelte, web/src/routes/(public)/docs/+page.svelte, web/src/routes/(public)/docs/+page.server.ts, web/src/routes/(public)/docs/[slug]/+page.svelte, and web/src/routes/(public)/docs/[slug]/+page.server.ts
- web/src/routes/(public)/sign/[token]/+page.svelte and web/src/routes/(public)/sign/[token]/+page.server.ts
- web/src/routes/apply/[token]/+page.svelte
- web/src/routes/.well-known/apple-app-site-association/+server.ts and web/src/routes/.well-known/assetlinks.json/+server.ts
- web/src/routes/application-file/[id]/+server.ts, web/src/routes/document-file/[id]/+server.ts, web/src/routes/expense-file/[id]/+server.ts, web/src/routes/scan-file/[id]/+server.ts, and web/src/routes/workorder-file/[id]/+server.ts
- web/src/lib/components/marketing/MarketingNav.svelte and web/src/lib/accounting/accounting-help.ts
- RentalCommand.Api/Controllers/DocsController.cs, RentalCommand.Api/Controllers/PublicApplicationsController.cs, RentalCommand.Api/Controllers/SignController.cs, RentalCommand.Api/Controllers/DocumentsController.cs, and RentalCommand.Api/Controllers/AuthController.cs

## Rough exploration areas

Use these as exploration goals, not a step-by-step script:

- Browse features, blog index/detail, privacy, and docs index/detail/search/slug links; verify navigation, canonical URLs, external links, metadata, and back/forward behavior.
- Open docs from accounting/help surfaces and return to the originating landlord page without losing context; compare no-result and missing-slug states.
- Exercise valid, expired, malformed, reused, and wrong-recipient application/sign tokens in a clean context; confirm only intended fields/actions are exposed.
- Request public file/application/document content with the supported route; verify content type, disposition, authorization, and safe not-found behavior.
- Check the well-known association JSON endpoints for valid JSON, expected content type/cache policy, and no accidental auth redirect.

## Specific edge cases worth trying

- Unknown blog/doc slug, empty search, long title/markdown/code block, broken image/link, URL-encoded characters, and offline fetch.
- Public token missing/expired/reused/tampered, token opened in two tabs, wrong account/session cookie, and submit/retry after network loss.
- Missing/private file, unsupported range/content type, malformed ID, cache hit after authorization changes, and a direct protected route from the public shell.
- 390px marketing/docs layout, keyboard focus, reduced motion, print privacy/docs, and console/network errors.

## What to verify visually

- Marketing and docs typography, headings, breadcrumbs, code/content rendering, link states, loading/empty/error pages, and privacy contact details are polished and readable.
- Token pages clearly identify the action and recipient without leaking landlord navigation; forms and signature consent are usable on mobile.
- File download/preview and well-known responses have no blank success state; browser status, focus, and accessible names are correct.

## Data safety and evidence

Use synthetic QA tokens/files only and do not alter public content. Never expose or download a real shared tenant document outside its authorized session.

Record actual-versus-expected behavior, URLs, response/status evidence, persistence after reload, and console errors. Report confirmed bugs separately from potential issues and observations using the BUG-N format from the exploratory tester definition.
