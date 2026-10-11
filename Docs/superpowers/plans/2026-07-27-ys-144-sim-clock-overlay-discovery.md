# YS-144 Sim Clock Overlay Discovery Brief

**Goal:** Keep expanded simulated-clock controls usable without blocking unrelated sidebar navigation at the supported 1920x1080 desktop viewport.

## Acceptance criteria

- With Settings and the simulated-clock panel expanded, Activity history remains clickable.
- The simulated-clock header, date input, calendar trigger, and action buttons remain interactive.

## Explicit non-goals

- Sidebar layout or navigation changes.
- Simulated-clock behavior, API, data-access, or responsive-position redesign.
- Adjacent overlay or styling cleanup.

## Discovery questions

1. Which component owns the overlay and its desktop position relative to the sidebar?
2. Which focused test can lock the intended hit-area contract?
3. What is the smallest source and test change needed?

## Bounded investigation

- Inspect: `web/src/lib/dev/SimClockPanel.svelte`, the shared date-picker popover, and existing source-contract tests under `web/src/lib/`.
- Commands allowed: `rg`, `sed`, focused Node test, Svelte checks, and the original 1920x1080 browser flow.
- Do not: edit sidebar code, change clock behavior, or fix unrelated findings.

## Stop condition

Discovery ends when the exact component, CSS selectors, focused test file, commands, and same-viewport proof flow are known.

## Discovery evidence

- `web/src/routes/+layout.svelte` mounts `SimClockPanel` globally whenever simulation mode is enabled.
- `web/src/lib/dev/SimClockPanel.svelte` fixes a 224px-wide, high-z-index panel at `left: 12px`, placing it entirely inside the expanded desktop sidebar.
- `web/src/lib/components/AppShell.svelte` uses `w-60` (15rem) for the expanded desktop sidebar and switches its mobile behavior at the 768px breakpoint.
- Moving only the desktop panel to `15rem + 12px` eliminates the overlap while preserving its original narrow-screen position and every control's normal hit target.
- Existing focused UI contract tests use Node's built-in test runner and source assertions, making `web/src/lib/dev/sim-clock-panel.test.ts` the narrow regression-test location.
