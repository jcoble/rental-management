# Dashboard: "Today's Briefing" hero — separate AI narrative from action items

Notion: TSK-33. Scope: **UI only** in `web/`. No backend/API/migration change — uses the
existing `GET /ai/briefing` response (`BriefingResponse`: `summary`, `llmEnhanced`, `bullets[]`
with `title`, `detail`, `category`, `severity` ∈ info|warning|critical, `entityType`, `entityId`).

## Problem (verified)
On `web/src/routes/(protected)/+page.svelte` the Briefing is buried: it sits *below* the money
snapshot + KPI cards, in a 50/50 row sharing space with an often-empty "Latest Messages" panel.
The LLM narrative is muted grey (`text-muted-foreground`) and visually blends into the action
list — you can't tell what the AI is *saying* vs what it wants you to *do*.

## Goal
Open the dashboard → instantly see (1) **what the AI says today**, visually marked as AI, and
(2) the **concrete things to do**, as an obvious, severity-ranked, deep-linking action list.
Don't let an empty Latest Messages eat prime space.

## Design

### 1. Promote Briefing to the top — a hero
Move the Briefing **above** the money snapshot and KPI cards (first thing read each day),
**full width** (out of the 50/50). New order:
snapshot banner → **Briefing hero** → money snapshot → KPI cards → secondary row → rest.

### 2. Hero layout (full-width card, AI-accent)
- Header: `Sparkles` + "Today's Briefing" + reuse `<AIBadge />`. When `llmEnhanced`, a subtle
  "Powered by AI" cue; the summary block is the AI voice.
- **AI voice block** ("the assistant says"): only when `summary` present. Distinct treatment —
  subtle purple→blue gradient tint (matching AIBadge's `from-purple-500/20 to-blue-500/20`),
  purple ring, a small `Sparkles`/avatar, and clearer foreground type (NOT muted grey). Reads
  as "the computer talking."
- On a wide screen, hero is a 2-col split: AI voice (left, ~3/5) | action list (right, ~2/5);
  stacks on mobile. AI voice stays top on mobile.

### 3. Action list — make to-dos pop
- Header "**N things need attention**" (count of bullets), or a calm "You're all caught up"
  empty state when zero.
- Each item: a severity-colored left rail / dot (critical = destructive, warning = warning,
  info = muted/primary), title (foreground, medium), detail (muted), a severity pill.
- **Deep-link**: when `entityType` + `entityId` resolve to a route, render the item as an
  `<a>` to the record with a chevron; otherwise a plain `<div>`. Map:
  - `WorkOrder` → `/maintenance/work-orders/{id}`
  - `Payment`   → `/accounting/payments/{id}`
  - `Lease`     → `/leases/{id}`
  - `Appointment` → `/appointments/{id}`
  - `Inspection` → `/maintenance/inspections/{id}`
- Sort already arrives critical→warning→info from the server; preserve it. Show up to ~6,
  with "+N more" if longer (no new nav target needed — just a count hint).
- `data-testid` on the hero, AI-voice block, action-list, and each action item/link.

### 4. Don't let empty Latest Messages eat space
Remove Messages from the 50/50. Put **Latest Messages + Latest Maintenance** in the secondary
row; **collapse/hide Latest Messages when it resolves with zero threads** so it never occupies
prime real estate empty. Maintenance fills the row when Messages is hidden.

### 5. Loading / error
- Hero shows a tasteful skeleton while `briefingQuery.isLoading`.
- On error or `bullets.length === 0 && !summary`, hero still renders with a friendly
  "No priorities today / all caught up" state (the AI moat should never look broken).

## Acceptance
- Briefing is the visually dominant, first content block, full width.
- AI narrative is unmistakably "the AI" (badge + gradient/ring + clear type), separated from
  the action list.
- Action items are severity-ranked, color-coded, and deep-link to the record.
- "N things need attention" header; calm empty state.
- Empty Latest Messages does not occupy prime space.

## Verify
- `cd web && npx svelte-check --threshold error && pnpm build`
- `dotnet build` sanity (no backend touched).
- Playwright screenshot of the new dashboard if feasible.
