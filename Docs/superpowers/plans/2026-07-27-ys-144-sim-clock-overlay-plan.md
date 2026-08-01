# YS-144 Sim Clock Overlay Execution Plan

**Goal:** Keep the expanded simulated-clock panel outside desktop sidebar navigation.
**Source brief:** `Docs/superpowers/plans/2026-07-27-ys-144-sim-clock-overlay-discovery.md`
**Active goal:** TSK-754 / YS-144
**Plan state:** Complete

## Contract

### Acceptance criteria

- AC-1: At 1920x1080, the simulated-clock panel sits outside the 15rem sidebar, Settings > Activity history is clickable while the panel is expanded, and all simulated-clock controls remain interactive.

### Explicit non-goals

- Sidebar, clock positioning, clock behavior, API, data-access, and unrelated responsive changes.

### Deferred items

- None.

### UI proof

- UI impact: Yes — pointer targeting changes on a visible desktop overlay.
- Supported scenario: Expand Settings and SIM CLOCK, then activate Activity history without collapsing SIM CLOCK.
- Required target: Chromium at 1920x1080.
- Proof artifacts: `output/qa/tsk754-evidence/YS-144-web-sim-clock-sidebar-fixed.png`.

## Task 1 — Keep the desktop clock panel outside the sidebar

**Status:** Complete
**Allowed files:**
- Modify: `web/src/lib/dev/SimClockPanel.svelte` scoped styles only.
- Test: `web/src/lib/dev/sim-clock-panel.test.ts`.

**Acceptance:** AC-1

1. [x] Add a focused source-contract test requiring the desktop panel to begin after the 15rem sidebar while retaining its narrow-screen inset.
2. [x] Apply the smallest scoped CSS change.
3. [x] Run the focused Node test; result: 1 passed.
4. [x] Run `pnpm check:native` and `pnpm check`; result: zero errors, with unrelated existing Svelte warnings.
5. [x] Run a closed-world relevance review against Task 1; result: `RELEVANCE PASS`.
6. [x] Repeat the supported flow in Chromium at 1920x1080; result: `UI PROOF PASS`.

## Completion gate

- [x] Every changed implementation/test file maps to Task 1.
- [x] AC-1 has fresh evidence.
- [x] No deferred item was implemented.
- [x] Final relevance review returned `RELEVANCE PASS`.
- [x] UI proof returned `UI PROOF PASS`; TSK-754 remains open for the continuing year simulation.
