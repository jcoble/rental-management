# TSK-800 Accounting Help and Blog Implementation Plan

> **For agentic workers:** Implement each task in order and run the focused checks before the full verification gate.

**Goal:** Give spreadsheet-based landlords plain-English accounting guidance inside the app, in the public knowledge base, and in a logged-out public blog.

**Architecture:** Knowledge-base articles remain markdown under `RentalCommand.Api/KnowledgeBase` and continue through the existing anonymous docs API. Accounting components consume one typed help catalog so every popover uses an intentional article URL. Blog content lives in a typed web registry and is rendered by static public SvelteKit routes with server-rendered metadata.

**Tech Stack:** SvelteKit 2, Svelte 5, TypeScript, Node test runner, existing Rental Command UI components, existing ASP.NET knowledge-base file loader.

## Global Constraints

- Write for a non-technical landlord with 15–40 units who currently uses spreadsheets.
- Use plain English in all reader-facing copy and do not mention automated content generation.
- Keep all referenced help URLs stable and covered by an existence test.
- Keep blog pages public and include a sign-up action.
- Do not add mobile help in this lane.
- Do not change database access or accounting calculations.

---

### Task 1: Knowledge-base articles and help-link contract

**Files:**
- Create: `RentalCommand.Api/KnowledgeBase/simple-vs-advanced-accounting.md`
- Create: `RentalCommand.Api/KnowledgeBase/lease-and-tenant-ledgers.md`
- Create: `RentalCommand.Api/KnowledgeBase/general-ledger.md`
- Create: `RentalCommand.Api/KnowledgeBase/audit-ready-books.md`
- Create: `RentalCommand.Api/KnowledgeBase/cash-flow-basics.md`
- Create: `web/src/lib/accounting/accounting-help.ts`
- Create: `web/src/lib/accounting/accounting-help-links.test.ts`

**Interfaces:**
- Produces: `ACCOUNTING_HELP`, a typed record whose entries each expose `title`, `summary`, and `/docs/<slug>` `href` values.
- Verifies: every unique slug used by `ACCOUNTING_HELP` has a matching markdown file with matching frontmatter.

- [x] Write the five complete markdown articles with the required stable slugs and Money category metadata.
- [x] Add the shared help catalog using concise, surface-specific landlord language.
- [x] Add a Node unit test that iterates every catalog entry, resolves its slug, opens the matching knowledge-base markdown file, and asserts the declared frontmatter slug matches.
- [x] Run the focused accounting-help test and confirm the contract passes.

### Task 2: Accounting help affordances

**Files:**
- Modify: `web/src/routes/(protected)/accounting/+page.svelte`
- Modify: `web/src/lib/components/accounting/AccountingDetailMode.svelte`
- Modify: `web/src/lib/components/accounting/MoneyPositionPanel.svelte`
- Modify: `web/src/lib/components/accounting/CashFlowPanel.svelte`
- Modify: `web/src/lib/components/accounting/GeneralLedgerPanel.svelte`
- Modify: `web/src/lib/components/accounting/JournalDetailDrawer.svelte`
- Modify: `web/src/lib/components/accounting/TenantLedgerPanel.svelte`
- Modify: `web/src/lib/components/accounting/OneTimeChargeSheet.svelte`
- Modify: `web/src/lib/components/accounting/TenantCreditSheet.svelte`
- Modify: `web/src/lib/components/accounting/RecurringChargeSheet.svelte`
- Modify: `web/src/lib/components/accounting/FinancialStatementTable.svelte`
- Modify: `web/src/lib/components/accounting/AccountingImpactCard.svelte`
- Modify: `web/src/routes/(protected)/reports/+page.svelte`

**Interfaces:**
- Consumes: `ACCOUNTING_HELP` entries and the existing `HelpPopover` component.
- Produces: visible help affordances with non-default, resolving documentation URLs.

- [x] Place help beside the simple/advanced toggle, cash and deposit bridge, cash-flow headline, general-ledger filters, journal explanation, tenant-ledger summary, charge/credit/recurring forms, financial statements, reports catalog, and accounting-impact title.
- [x] Keep each tip to one or two sentences and preserve existing control behavior and responsive layout.
- [x] Use stable `data-testid` values on new help triggers where practical.

### Task 3: Public bookkeeping blog

**Files:**
- Create: `web/src/lib/blog/articles.ts`
- Create: `web/src/lib/blog/articles.test.ts`
- Create: `web/src/routes/(public)/blog/+page.svelte`
- Create: `web/src/routes/(public)/blog/[slug]/+page.server.ts`
- Create: `web/src/routes/(public)/blog/[slug]/+page.svelte`
- Modify: `web/src/lib/components/marketing/MarketingNav.svelte`
- Modify: `web/src/lib/components/marketing/MarketingFooter.svelte`

**Interfaces:**
- Produces: `BLOG_POSTS`, `getBlogPost(slug)`, and three serializable posts with unique slugs, SEO titles, meta descriptions, summaries, dates, reading times, sections, and sign-up calls to action.
- Routes: public `/blog` index and `/blog/[slug]` article pages.

- [x] Author the three requested posts with honest, useful bookkeeping guidance and no legal or tax promises.
- [x] Render a responsive public index in the existing marketing shell and add Blog to shared public navigation.
- [x] Render each post with server-provided article data, title, description, readable article typography, and `/register` call to action.
- [x] Add registry tests for exact slugs, unique values, non-empty metadata, content depth, and sign-up target.

### Task 4: Verification and delivery

**Files:**
- Inspect all changed files and final diff.

**Stop conditions:**
- `pnpm --dir web check` exits 0 with 0 errors.
- `pnpm --dir web test:unit` exits 0 with all tests passing.
- `git diff --check` exits 0.
- No required help URL is absent from the knowledge base.
- The final commit contains no attribution trailer and is not pushed.

- [x] Run the two required web commands serially and capture their exact output.
- [x] Search changed reader-facing content for forbidden terminology and repair any findings.
- [x] Review the requirement checklist against the diff and collect file-line receipts.
- [x] Commit with a plain subject and body, verify the commit hash and clean status, re-check TSK-800 remains Done, and touch `/tmp/rc-helpdocs.done`.
