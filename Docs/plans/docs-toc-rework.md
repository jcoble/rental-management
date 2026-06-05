# Plan: Rework docs pages — stop the duplicated table-of-contents

**Notion:** TSK-34 · **Branch:** `feat/docs-toc-rework` · **Scope:** `web/` route components only — no backend, no migration.

## Problem (verified)

The `/docs` knowledge base renders its article list twice on one screen, and the
article page re-renders that same full index:

- `web/src/routes/(public)/docs/+page.svelte` has BOTH a left sidebar listing every
  category -> article (`docs-sidebar`) AND a main-column card grid of the exact same
  articles. Two tables-of-contents, side by side.
- `web/src/routes/(public)/docs/[slug]/+page.svelte` re-renders that SAME full sidebar
  TOC, so an article reads as "the landing page again."

## Reference: EdiPlatform docs

`/Users/blackcolours/dev/work/EdiPlatform/ediplatform-web/src/routes/(public)/docs/`

- **Index** = curated landing: hero + a "new here?" quick-start card + a category card
  grid (each card -> that category's first article, with article count). No full sidebar
  duplicating the cards.
- **Article** = breadcrumb (Docs / Category / Article) + header + body + prev/next.
  A clean reading view, NOT a repeated index.

## Data we have (RC backend, unchanged)

`GET /docs` returns `{ categories: [{ category: string, articles: [{ slug, title,
summary, order, category }] }] }`, already ordered (Getting Started first). No category
IDs — the category *string* is the key. Real categories: Getting Started, Scan & Intake,
Tenants & Leases, Money, Operations, AI Assistant, Properties & Units, Tenant Portal,
Settings.

## Design direction

Calm, guide-like, consistent with the existing RC app and Edi for cross-product
consistency. Reuse RC tokens (`--primary`, `--card`, `--border`, `--secondary`,
`--muted-foreground`) and the existing `Input` component. Audience = non-technical
landlord, so the landing leads with a clear "Start here."

## Changes

### Index (`/docs/+page.svelte`) — ONE landing, no duplication

- Keep the hero (badge + "How can we help?" + intro) and the search box.
- Add a **Start here** quick-start card linking to the first Getting Started article
  (falls back to the first article overall), Edi-style accent treatment using RC's
  `--primary`.
- Replace the dual sidebar+grid with a **single category card grid**. Each card:
  per-category icon + accent (keyed by category name), title, article count, and a short
  list (first ~3) of its articles as direct links. Card header links to the category's
  first article. This keeps articles reachable in one click without a second full TOC.
- Keep search: filtering narrows the same card grid (and the quick-start card hides when
  it doesn't match). Keep the empty state. Drop the separate mobile "Browse topics"
  toggle on the index — the grid is the only nav now, so there's nothing to toggle.

### Article (`/docs/[slug]/+page.svelte`) — clean reading view

- Replace "All docs" back-link with an Edi-style **breadcrumb**: Docs / Category /
  Article (category links to its first article).
- Keep header (category eyebrow + title + summary), body, and the existing prev/next.
- Replace the full-index sidebar with a **quiet section nav**: only the *current
  category's* articles (with the active one highlighted), under a small "In this section"
  label. This is a section nav, not the whole index re-rendered. Keep the mobile toggle
  for it. On a single-article category, omit the nav entirely.

### Shared

- Category icon + accent map, keyed by RC's real category strings, kept inline per page
  (small, page-specific) — mirrors how Edi colocates its `iconMap`/`accentMap`. Sensible
  `FileText` / neutral-accent fallback for unknown categories.
- Preserve all `data-testid`s the routes already expose where they still apply; the
  index sidebar testid (`docs-sidebar`) goes away with the duplicate.

## Verification

- `cd web && npx svelte-check --threshold error && pnpm build`
- `dotnet build` sanity (no backend change, just confirm nothing broke).
- Playwright screenshots of `/docs` and one article if feasible.
- Code-review subagent; fix findings.

## Acceptance

`/docs` is a single curated landing (not the TOC twice); opening an article is a clean
view that does not replay the landing. Look mirrors Edi for cross-product consistency.
