# Homepage: sell the mobile story (TSK-24)

## Why

Mobile is the heart of Rental Command — *"run the whole business off your phone"* — but the
public homepage (`web/src/routes/welcome/+page.svelte`) never says so. We need the landing
page to land two pillars:

1. **"The computer does the typing for you"** — photo of a receipt / lease / check → scan →
   LLM extract with confidence → confirm-ready draft. This is the flagship and it is **LIVE
   today**, so present it as working.
2. **Speak commands** — say it and it happens. This ties to a separate Voice / Google App
   Actions task (TSK-26) that is **NOT built**. Frame strictly as **"coming soon."** Do not
   imply it works.

## Scope

- Add a dedicated **mobile section** below the hero on `/welcome` telling the phone-first
  story, cohesive with the existing dark theme tokens (`--primary` blue, chart accents,
  `--success` / `--warning`, `rounded-2xl` cards, `border-border`).
- A **device frame** (styled phone mockup, not a real screenshot) running a self-contained
  capture → extract → confirm animation: viewfinder snaps a photo, fields fly in with
  confidence chips, a "record created" toast appears. Built with CSS + the existing
  reveal/scan animation motif (no new heavy deps; GSAP not warranted).
- A **voice teaser** phone mock: a spoken command bubble → resulting action, clearly badged
  **"Coming soon."**
- Reuse + lean into the existing hero scan demo motif (the user likes it); extend the same
  visual language into the new section rather than introducing a different style.

## Constraints (honesty)

- We **cannot** capture real Galaxy-S22 screenshots. Use clean placeholder device frames with
  representative UI, and leave a clear `TODO` comment marking exactly where real app
  screenshots should drop in. **Do not fabricate fake screenshots** that misrepresent the app.
- Only flows that work **today** are shown as working (capture → draft → confirm).
  Voice = "coming soon" badge + forward-looking copy.
- `data-testid` on new interactive/section anchor elements.
- **No backend change, no migration.** Frontend-only.

## Design approach

- New `<section id="mobile">` placed directly after the HERO and before "How it works", so the
  phone story is the first thing after the fold.
- Two-column on `lg`: left = copy + two pillar cards ("Does the typing" / "Speak commands —
  coming soon"); right = an **animated phone frame** that auto-cycles the capture→extract→
  confirm loop, with `prefers-reduced-motion` showing the resolved end-state (fields +
  confirmed record) with no motion.
- A small "voice" mini-mock floats near the frame showing a spoken command → action, badged
  coming-soon.
- Add only the keyframes/utilities needed in `app.css`, all gated under the existing
  `prefers-reduced-motion` block. Prefer composing existing utilities
  (`animate-scan-sweep`, `reveal`, `animate-float`) before adding new ones.

## Files

- `web/src/routes/welcome/+page.svelte` — new mobile section + phone-frame markup + section
  data/state.
- `web/src/app.css` — only if a new keyframe is genuinely needed (e.g. a staged
  fly-in / cycle); otherwise reuse existing utilities. Any new motion must be added to the
  `prefers-reduced-motion` disable list.

## Verification

- `cd web && npx svelte-check --threshold error` → 0 errors.
- `cd web && pnpm build` → succeeds.
- `dotnet build` (sanity; no backend touched) → succeeds.
- Optional: Playwright screenshot of the new section to sanity-check layout.

## Out of scope

- Building voice / App Actions (TSK-26).
- A dedicated mobile docs section (TSK-25).
- Real device screenshots (left as marked TODO placeholders).
