# UX Advocate Review: Phases, Milestones, and Git Integration

## Executive Summary

All three design documents are well-structured and generally follow the app's established patterns. However, there are notable inconsistencies between proposals, a few discoverability gaps, and some areas where cognitive load could be reduced. Below are findings organized by UX dimension, with specific problems and proposed solutions.

---

## 1. Discoverability

### Good

- **Phases**: "Add Phase" button placement in the page header mirrors the Team page's "Add Member" button -- users will find it where they expect.
- **Milestones**: "New Milestone" button follows the same header placement pattern. Also adds a Quick Actions entry on the Dashboard, giving two discovery paths.
- **Git**: Clickable links on existing data (commit SHAs, branches) are a natural affordance -- monospace + accent color matches the codebase convention for interactive elements.

### Issues

**Problem 1: Phase dependency editing is buried.** The dependency editor only lives on the Phase Detail page in a dedicated section. Users creating phases won't intuitively know they can set dependencies. The Create Phase Dialog includes a "Dependencies" multi-select, which is good, but once created, the only way to modify dependencies requires navigating into the detail page and scrolling to the "Dependencies" section.

**Solution**: Add a small "depends on: [Phase X]" chip display on the phases list cards (the design already proposes this, which is good). Ensure the chips are clickable to navigate directly to the dependency editing section on the detail page, not just to the dependent phase.

**Problem 2: Git branch on Phases has no obvious entry point.** The git-integration design adds a `GitBranch` field to phases but does not specify where or how users set it. The phases-design document's inline editing list does not include `GitBranch` as an editable field. MCP/AI agents can set it, but web users have no way to.

**Solution**: Add `GitBranch` to the Phase Detail page's editable fields (metadata section). Show a text input with a placeholder like `feature/phase-name`. This should be explicitly listed in the phases-design's editable fields table.

**Problem 3: Milestone `GitTag` and `GitBranch` have no edit UI specified.** Same issue as phases -- the git-integration document adds fields to the entity but the milestones-design's inline editing only covers Name, Description, Version, and Target Date.

**Solution**: Add GitTag and GitBranch to the Milestone Detail page's edit mode. Group them in a "Git" subsection below the main fields to keep them visually separate from core metadata.

---

## 2. Consistency

### Good

- Both phases and milestones use the `CreateProjectDialog.svelte` modal pattern for creation -- fixed overlay, centered card, Escape/backdrop to close.
- Both use inline editing on detail pages with Save/Cancel buttons.
- Both restrict delete to detail pages only (not on list views), with confirmation.
- Both follow the "header: title + action buttons" layout used by Team and Settings pages.

### Issues

**Problem 4: Inconsistent edit patterns between Phases and Milestones.** The phases-design uses per-field auto-save (each field saves independently via PATCH on blur/Enter, no global Save button). The milestones-design uses a global edit mode toggle with Save/Cancel buttons (matching the Settings page pattern). These are two different mental models for what appears to be the same operation.

**Impact**: A user who edits a milestone, then edits a phase, will be confused when one has Save/Cancel and the other auto-saves on blur. This violates the principle of consistency.

**Recommendation**: Pick one pattern. Given that the Settings page already establishes the "pencil icon -> edit mode -> Save/Cancel" pattern, and that pattern is safer (users explicitly confirm changes), **standardize on the global edit mode with Save/Cancel for both phases and milestones**. Per-field auto-save is elegant but risky -- accidental blurs can save unintended changes with no undo.

**Problem 5: Inconsistent status transition UIs.** The phases-design uses a "primary action button + overflow dropdown" pattern (prominent "Start Work" button + "..." menu for secondary actions). The milestones-design uses a "clickable StatusBadge that opens a dropdown" pattern. These are fundamentally different interaction models for the same conceptual operation (changing status).

**Impact**: Users learn one pattern on phases and expect the same on milestones, but find a different one. The phases approach is more discoverable (explicit button with a verb label like "Move to Review" is clearer than a clickable badge). The milestones approach is more compact.

**Recommendation**: Use the phases pattern (primary action button + overflow dropdown) for both. The verb-labeled button ("Start Work", "Complete Milestone") is more discoverable and reduces ambiguity. A clickable StatusBadge looks like a display element, not an interactive control, violating the "affordance" principle. If space is a concern on milestones, the primary button can be smaller. Create a shared `StatusActions` component that takes entity type and current status, rather than two separate implementations.

**Problem 6: Delete confirmation inconsistency.** Phases use `window.confirm()` (matching TaskDetail). Milestones use a custom modal dialog (with impact counts: "X phases and Y tasks will be removed"). The custom modal is strictly better UX (shows consequences), but the inconsistency is jarring.

**Recommendation**: Upgrade phases to also use a custom confirmation modal showing impact ("X tasks will be unassigned"). Then standardize both on the modal pattern. The `window.confirm()` approach should be retired going forward -- it cannot be styled, does not support rich content, and looks foreign in a dark-themed app.

---

## 3. Happy Path User Flows

### Creating a Phase (phases-design)

1. Navigate to /phases (1 click from nav)
2. Click "Add Phase" (1 click)
3. Fill in Name (required), optionally other fields
4. Click "Create Phase" (1 click)
5. Dialog closes, phase appears in list

**Total: 3 clicks + typing. Good.**

**Issue**: The user must know which milestone to create the phase under. The design says the dialog takes a `milestoneId` prop, but the phases list page currently auto-selects a milestone. If the wrong milestone is selected, the user creates the phase in the wrong milestone with no easy way to move it. The milestones-design introduces a `MilestoneSelector` dropdown, which helps, but the phase creation dialog should also display which milestone the phase is being created under (as a read-only label at the top of the dialog).

### Creating a Milestone (milestones-design)

1. Navigate to /milestones (1 click from nav)
2. Click "New Milestone" (1 click)
3. Fill in Name (required), optionally other fields
4. Click "Create Milestone" (1 click)
5. Dialog closes, milestone appears in list

**Total: 3 clicks + typing. Good. Identical flow to phases. Consistent.**

### Changing Phase Status (phases-design)

1. Navigate to /phases (1 click)
2. Click phase card to go to detail (1 click)
3. Click primary action button e.g. "Start Work" (1 click)

**Total: 3 clicks. Good. Fast for the happy path.**

**Issue**: The design explicitly says "do NOT add status dropdowns to the list view." This means every status change requires navigating to the detail page. For a project with 8 phases, quickly progressing multiple phases through statuses requires 8 round-trips to detail pages. Consider allowing the primary action button on hover/click directly on the list cards as a progressive enhancement.

### Setting Up Git Links (git-integration)

1. User completes a task with git info via MCP tool (automatic)
2. Task detail shows clickable commit/branch links (automatic)

**Total: 0 manual clicks. Excellent for the AI workflow.**

**Issue**: For manual users who want to add git info to a task, there is no UI specified for editing `GitBranch`, `GitCommitSha`, or `PullRequestUrl` on a task. The git-integration design focuses on MCP/API input. Add these as editable fields on the TaskDetail page (similar to how phase number is editable in the phases-design).

---

## 4. Information Hierarchy

### Good

- **Dashboard**: Active milestone prominently displayed with progress bar. Stats cards show the four most important numbers at a glance.
- **Board cards (git-integration)**: Only showing branch shortname and PR badge (not full SHA) is the right call. Board cards stay scannable.
- **Phase detail**: Layout puts Name/Status at top, Goal next, then Description, then Success Criteria -- this is the right priority order for quick scanning.
- **Milestone detail**: Progress summary (phase count + task rollup) as stats cards is excellent -- users see health at a glance.

### Issues

**Problem 7: Phase dependencies on the list view could create visual noise.** Showing "Depends on: [Phase 1] [Phase 2]" chips on every card adds a row of information that many phases may not have. If only 2 of 8 phases have dependencies, 6 cards have unnecessary whitespace or missing sections.

**Solution**: Only render the dependency row if the phase actually has dependencies (the design implies this but should state it explicitly). Additionally, use a more subtle indicator -- a small link icon with a count (e.g., "2 deps") rather than full phase name chips. Full names can be seen on hover or on the detail page.

**Problem 8: Milestone detail page could become very tall.** The proposed layout includes: header card, progress cards (2-col), timeline info card, and a full phases list with vertical connector lines. For a milestone with 10 phases, this is a lot of scrolling.

**Solution**: Consider making the phases list collapsible (collapsed by default if there are more than 5 phases, with "Show all 10 phases" expander). Alternatively, limit the phases display to a compact table format rather than full cards with connector lines.

---

## 5. Error States and Edge Cases

### Good

- **Phases create dialog**: Specifies inline error message on failure (matching CreateProjectDialog pattern).
- **Milestones delete**: Specifies showing impact counts before deletion.
- **Git links**: Gracefully degrades -- if no repository URL is configured, shows plain text instead of links.

### Issues

**Problem 9: No empty state specified for Milestone Detail's phases section.** What does the milestone detail page show when a milestone has zero phases? The design should specify an empty state, following the Team page pattern: dashed border box with an icon, descriptive text ("No phases yet"), and a call-to-action button ("Add Phase").

**Problem 10: No loading states specified for the Milestone Detail page.** The phases-design and milestones-design specify data fetching via `createQuery` but do not describe loading skeletons or loading text. The Dashboard already has `isLoading` and `isError` handling -- both detail pages should follow the same pattern.

**Problem 11: Phase cycle detection needs user-facing feedback.** The phases-design mentions client-side cycle detection when adding dependencies, and says "show a toast/error if a cycle would be created." This is good, but the error message should be specific: "Adding this dependency would create a circular chain: Phase A -> Phase B -> Phase A" rather than a generic "Cycle detected."

**Problem 12: No validation feedback for milestone Target Date in the past.** The milestones-design says Target Date "must be today or future" but does not specify what happens when a user enters a past date. Show inline validation text below the input: "Target date must be today or later" in the danger color.

**Problem 13: Blocked status has no unblock path specified in detail.** The phases-design says "When unblocking, the phase returns to the status it was in before being blocked. Store the pre-block status in client state or derive it from activity log." Deriving from client state is fragile (lost on page refresh). Deriving from activity log adds API complexity. Better: store `previousStatus` as a field on the Phase entity (set when transitioning TO Blocked), and use it when unblocking. This is a backend concern but affects UX reliability.

---

## 6. Cognitive Load

### Good

- **Phases create**: Only Name is required. Optional fields are clearly marked. This is low-friction entry.
- **Milestones create**: Same -- only Name required. Good progressive disclosure.
- **Phase status**: Primary action button shows only the logical next step. Secondary actions are hidden in overflow. This reduces decision paralysis.

### Issues

**Problem 14: Phase dependency editor may be overwhelming for new users.** The chip multi-select with cycle detection, bidirectional display ("depends on" and "depended on by"), and immediate save-on-change is a lot of functionality in one section. A user unfamiliar with the project's phase structure may not understand what dependencies mean or why they matter.

**Solution**: Add a brief help text line above the dependency editor: "Dependencies control phase ordering. A phase won't start until its dependencies are complete." This is a single sentence that provides just enough context.

**Problem 15: Git integration adds fields across three entity types.** Tasks get `PullRequestUrl`, Phases get `GitBranch`, Milestones get `GitTag` and `GitBranch`. Users need to mentally map "which git concept goes where." This is actually a well-thought-out mapping (commit = task, branch = phase, tag = milestone), but the UI should make this hierarchy visible.

**Solution**: On the git fields in each entity's UI, use consistent iconography and labeling:
- Task: GitBranch icon + "Branch", GitCommit icon + "Commit", GitPullRequest icon + "PR"
- Phase: GitBranch icon + "Feature Branch"
- Milestone: Tag icon + "Release Tag", GitBranch icon + "Release Branch"

The label differentiation ("Branch" vs "Feature Branch" vs "Release Branch") helps users understand the semantic difference.

---

## 7. Cross-Proposal Consistency Issues

### Issue 16: Create pattern placement

Both phases and milestones correctly place create dialogs as modals triggered from page headers. However, the phases-design mentions a secondary trigger from a "Milestone detail page (future)" while the milestones-design has already designed that exact page with a phases section but does not mention an "Add Phase" button there. These need to be reconciled -- the Milestone Detail page's phases section should include an "Add Phase" button.

### Issue 17: Shared vs. entity-specific components

The milestones-design proposes a shared `StatusTransitionDropdown.svelte` component that takes an `entityType` parameter. The phases-design proposes an entity-specific `PhaseStatusActions.svelte`. These should be consolidated. Either:
- (a) Build the shared `StatusTransitionDropdown` and use it for both (milestones-design approach), or
- (b) Build entity-specific components that share a common internal utility for transition validation

Option (a) is better for consistency but requires handling the different status enums (PhaseStatus vs MilestoneStatus) and different UX (phases want a primary button + dropdown; milestones want a clickable badge dropdown). If the recommendation from Problem 5 is adopted (use primary button + dropdown for both), then a shared component becomes natural.

### Issue 18: Navigation between entities

The milestones-design links milestone detail -> phase detail (via clicking phases in the list). The phases-design links phase detail -> dependent phases (via clicking dependency chips). But there is no explicit "breadcrumb" or "back" link from a phase detail page to the milestone detail page. The phases-design only has "Back to Phases" (list page). Add "Back to [Milestone Name]" as an alternative when navigating from a milestone context.

### Issue 19: MCP tool naming convention

- Phases: `get_phase`, `list_phases`, `update_phase`, `delete_phase`, `change_phase_status`
- Milestones: `get_milestone`, `list_milestones`, `create_milestone`, `update_milestone`, `change_milestone_status`, `delete_milestone`

These are consistent with each other. The git-integration design modifies the existing `complete_task` tool but does not introduce new tools for setting git fields on phases/milestones. This means git fields on phases/milestones can only be set via the web UI's inline editing or direct API calls -- not via MCP. For consistency, the `update_phase` and `update_milestone` MCP tools should include the git fields in their parameter schemas.

---

## Summary of Recommendations (Priority Order)

| # | Issue | Priority | Effort |
|---|-------|----------|--------|
| 4 | Standardize edit pattern (global Save/Cancel for both) | High | Medium |
| 5 | Standardize status transition UI (primary button + dropdown for both) | High | Medium |
| 6 | Standardize delete confirmation (custom modal for both) | High | Low |
| 2 | Add GitBranch edit UI to Phase Detail | High | Low |
| 3 | Add GitTag/GitBranch edit UI to Milestone Detail | High | Low |
| 16 | Add "Add Phase" button to Milestone Detail page | High | Low |
| 18 | Add contextual back-navigation (phase -> milestone) | Medium | Low |
| 13 | Store previousStatus on Phase entity for reliable unblock | Medium | Low |
| 9 | Add empty state for Milestone Detail phases section | Medium | Low |
| 10 | Add loading/error states to detail pages | Medium | Low |
| 12 | Add inline validation for past Target Date | Medium | Low |
| 11 | Make cycle detection error messages specific | Medium | Low |
| 14 | Add help text to dependency editor | Low | Low |
| 15 | Differentiate git field labels by entity type | Low | Low |
| 7 | Use compact dependency indicators on list cards | Low | Low |
| 8 | Make phases list collapsible on milestone detail | Low | Low |
| 17 | Consolidate status transition components | Low | Medium |
| 19 | Add git fields to MCP update tools | Low | Low |
| 1 | Make dependency chips navigate to edit section | Low | Low |

---

## Overall Assessment

The three designs are solid and clearly informed by the existing codebase patterns. The main risk is inconsistency between the phases and milestones proposals -- they were written independently and made different UX choices for equivalent operations. Standardizing the edit pattern, status transition UI, and delete confirmation across both entities will create a coherent experience. The git-integration design is well-scoped and degrades gracefully, but needs explicit UI entry points for manual users (not just MCP/API input). With these adjustments, the combined feature set will feel like a natural extension of the existing app.
