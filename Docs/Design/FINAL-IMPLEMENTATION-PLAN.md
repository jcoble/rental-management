# Final Implementation Plan: Phases, Milestones & Git Integration

## Synthesis Decisions

This plan incorporates proposals from three planners and critical feedback from the Devil's Advocate and UX Advocate reviews. Key synthesis decisions:

1. **Scope reduction**: Cut ~40% of proposed features per Devil's Advocate. Focus on core CRUD + bugs.
2. **Pattern standardization**: Per UX Advocate, unified editing pattern (Save/Cancel mode), unified status transition (simple `<select>` dropdown), unified delete (window.confirm for now).
3. **GitHub-only**: No multi-provider URL builders. Comment for extensibility.
4. **Deferred**: Dependency editor, reordering UI, MilestoneSelector, progress charts, Phase/Milestone git fields.

---

## Priority Tiers

### P1 — Must Have (Fix Bugs + Core CRUD)

| # | Feature | Effort | Files |
|---|---------|--------|-------|
| 1 | Fix AI endpoint bug (git fields dropped) | 20 min | `api/Api/AiEndpoints.cs` |
| 2 | Fix `phases.ts` API client (wrong create URL, missing changeStatus) | 15 min | `web/src/lib/api/endpoints/phases.ts` |
| 3 | Fix `milestones.ts` API client (create needs projectId, add changeStatus) | 15 min | `web/src/lib/api/endpoints/milestones.ts` |
| 4 | Fix backend DELETE phase (unassign tasks, clean dependency refs) | 30 min | `api/Api/PhaseEndpoints.cs` |
| 5 | Decide + implement milestone DELETE behavior | 30 min | `api/Api/MilestoneEndpoints.cs` |
| 6 | Create Phase dialog | 1 hr | New: `web/src/lib/components/phases/CreatePhaseDialog.svelte`, modify `web/src/routes/phases/+page.svelte` |
| 7 | Phase detail page: inline editing + status dropdown + delete | 2 hr | Modify: `web/src/routes/phases/[id]/+page.svelte` |
| 8 | Create Milestone dialog | 1 hr | New: `web/src/lib/components/milestones/CreateMilestoneDialog.svelte`, modify `web/src/routes/milestones/+page.svelte` |
| 9 | Milestone detail page (new route) | 2 hr | New: `web/src/routes/milestones/[id]/+page.svelte` |
| 10 | Milestone list: link cards to detail + add create button | 30 min | Modify: `web/src/routes/milestones/+page.svelte` |
| 11 | Phase MCP tools (5 tools) | 30 min | Modify: `mcp/src/tools/phase-tools.ts` |
| 12 | Milestone MCP tools (6 tools) | 30 min | New: `mcp/src/tools/milestone-tools.ts`, modify `mcp/src/index.ts` |
| 13 | Clickable git links in TaskDetail | 45 min | New: `web/src/lib/utils/git.ts`, modify TaskDetail component |

### P2 — Should Have

| # | Feature | Effort | Files |
|---|---------|--------|-------|
| 14 | Git badges on board cards (branch + PR) | 30 min | Modify: BoardCard component |
| 15 | PR tracking on tasks (PullRequestUrl field) | 1 hr | `api/Data/Entities/LifecycleTask.cs`, `api/Api/TaskEndpoints.cs`, `api/Api/AiEndpoints.cs`, `mcp/src/tools/task-tools.ts`, TaskDetail, BoardCard, types |
| 16 | Dashboard: link active milestone to detail page | 15 min | Modify: `web/src/routes/+page.svelte` |
| 17 | Add "Add Phase" button on Milestone Detail page | 15 min | Modify: `web/src/routes/milestones/[id]/+page.svelte` |

### P3 — Nice to Have (Defer until pain is felt)

| # | Feature | Notes |
|---|---------|-------|
| 18 | Phase dependency display (read-only chips on list/detail) | Only if users are confused about phase ordering |
| 19 | Phase dependency editor (interactive chip multi-select) | Only if dependency management becomes a bottleneck |
| 20 | Phase reordering UI (up/down arrows) | Use API/MCP directly for now |
| 21 | MilestoneSelector component | Auto-select logic is sufficient |
| 22 | Progress bar charts on milestone detail | Dashboard already shows this |
| 23 | Phase GitBranch field | Tasks already capture branches |
| 24 | Milestone GitTag/GitBranch fields | Release ceremony for personal tool |
| 25 | Multi-provider URL builders (GitLab, Bitbucket, Azure DevOps) | GitHub-only is sufficient |

---

## Standardized Patterns (Cross-Cutting Decisions)

### Editing Pattern: Save/Cancel Mode

Both Phase Detail and Milestone Detail use the **same** editing pattern:
- Pencil icon in the header toggles edit mode
- All fields become editable inputs (pre-filled with current values)
- Save/Cancel buttons appear at the bottom of the header card
- Save calls PATCH with changed fields only
- Cancel reverts to read-only view
- Matches the existing Settings page pattern

**Rationale**: UX Advocate identified that per-field auto-save (phases proposal) and global Save/Cancel (milestones proposal) are inconsistent. Save/Cancel is safer (explicit confirmation, no accidental saves on blur) and already established in the app.

### Status Transition: Simple Select Dropdown

Both Phase Detail and Milestone Detail use a simple `<select>` dropdown:
- Shows current status
- Options are the valid next statuses (filtered per transition rules)
- Selecting a new status immediately calls the status change endpoint
- Confirmation dialog for terminal states (Completed, Cancelled) via `window.confirm()`

**Rationale**: Devil's Advocate cut both the "primary button + secondary dropdown" (phases) and "clickable StatusBadge dropdown" (milestones) approaches as over-engineered. A `<select>` is 10 lines of code per page, universally understood, and consistent.

### Delete Confirmation: window.confirm()

Both Phase Detail and Milestone Detail use `window.confirm()`:
- Phase: "Delete phase '[name]'? Tasks in this phase will be unassigned but not deleted."
- Milestone: "Delete milestone '[name]'? This will permanently delete the milestone. Delete all phases first if it has any."

**Rationale**: Devil's Advocate correctly noted that a custom DeleteConfirmDialog with cascade impact counts is over-engineered. `window.confirm()` matches the existing TaskDetail pattern.

### Milestone DELETE Behavior: Reject if Phases Exist

The backend will return 409 Conflict if attempting to delete a milestone that has phases. Users must delete phases first. This prevents accidental multi-level cascade deletes.

**Rationale**: Devil's Advocate recommended this over cascade delete. No undo exists, so cascading milestone -> phases -> tasks deletion is too dangerous.

---

## Detailed Feature Specifications

### 1. Fix AI Endpoint Bug (P1)

**File**: `api/Api/AiEndpoints.cs`

**Problem**: `TaskTransitionRequest` only has `Status`. Git fields from MCP `complete_task` are silently dropped.

**Fix**:
```csharp
// Change the request record:
public record TaskTransitionRequest(
    TaskStatus Status,
    string? GitCommitSha = null,
    string? GitBranch = null
);

// In the handler, after setting status:
if (req.GitCommitSha is not null) task.GitCommitSha = req.GitCommitSha;
if (req.GitBranch is not null) task.GitBranch = req.GitBranch;
```

Backward-compatible: existing callers that only send `Status` continue to work.

---

### 2-3. Fix Frontend API Clients (P1)

**phases.ts**:
```ts
export const phases = {
  list: (milestoneId: number) => api.get<Phase[]>(`/milestones/${milestoneId}/phases`),
  get: (id: number) => api.get<Phase>(`/phases/${id}`),
  create: (milestoneId: number, data: Partial<Phase>) =>
      api.post<Phase>(`/milestones/${milestoneId}/phases`, data),
  update: (id: number, data: Partial<Phase>) => api.patch<Phase>(`/phases/${id}`, data),
  delete: (id: number) => api.delete(`/phases/${id}`),
  changeStatus: (id: number, status: string) =>
      api.post<Phase>(`/phases/${id}/status`, { status }),
};
```

**milestones.ts**:
```ts
export const milestones = {
  list: (projectId: number) => api.get<Milestone[]>(`/projects/${projectId}/milestones`),
  get: (id: number) => api.get<Milestone>(`/milestones/${id}`),
  create: (projectId: number, data: Partial<Milestone>) =>
      api.post<Milestone>(`/projects/${projectId}/milestones`, data),
  update: (id: number, data: Partial<Milestone>) => api.patch<Milestone>(`/milestones/${id}`, data),
  delete: (id: number) => api.delete(`/milestones/${id}`),
  changeStatus: (id: number, status: string) =>
      api.post<Milestone>(`/milestones/${id}/status`, { status }),
};
```

---

### 4. Fix Backend DELETE Phase (P1)

**File**: `api/Api/PhaseEndpoints.cs`

Before deleting a phase:
1. Set `PhaseId = null` on all tasks belonging to that phase
2. Remove the deleted phase's ID from `DependsOnPhaseIds` on all other phases in the same milestone
3. Wrap in a transaction

---

### 5. Milestone DELETE: Reject if Phases Exist (P1)

**File**: `api/Api/MilestoneEndpoints.cs`

```csharp
var milestone = await db.Milestones.Include(m => m.Phases).FirstOrDefaultAsync(m => m.Id == id);
if (milestone is null) return Results.NotFound();
if (milestone.Phases.Any()) return Results.Conflict("Cannot delete milestone with existing phases. Delete all phases first.");
db.Milestones.Remove(milestone);
await db.SaveChangesAsync();
return Results.NoContent();
```

---

### 6. Create Phase Dialog (P1)

**New file**: `web/src/lib/components/phases/CreatePhaseDialog.svelte`

**Pattern**: Follows `CreateProjectDialog.svelte` — fixed overlay, centered card, Escape/backdrop close.

**Props**: `{ milestoneId: number; onCreated: () => void; onClose: () => void }`

**Fields**:
| Field | Type | Required | Notes |
|-------|------|----------|-------|
| Name | text input | Yes | Auto-focus, submit on Enter |
| Goal | textarea (2 rows) | No | "What does this phase achieve?" |
| Description | textarea (2 rows) | No | |
| Success Criteria | textarea (3 rows) | No | "Markdown supported" |

PhaseNumber auto-calculated (max + 1). Dependencies NOT included in create dialog (deferred to P3).

**Triggered from**: "Add Phase" button in phases list page header.

**Milestone context**: Show "Creating phase in: [Milestone Name]" as a read-only label at the top of the dialog (UX Advocate recommendation).

---

### 7. Phase Detail Page: Editing + Status + Delete (P1)

**File**: `web/src/routes/phases/[id]/+page.svelte` (modify existing)

**Layout**:
```
[← Back to Phases]

[Phase Name]  [Status: select dropdown ▾]  [✏️ Edit] [🗑️ Delete]

Goal: ...
Description: ...

--- Success Criteria ---
[Markdown rendered]

--- Tasks (count) ---
[Grid of BoardCards, same as current]

--- Metadata ---
Phase #N | Created <date> | Started <date> | Completed <date>
```

**Edit mode** (pencil icon toggle):
- Name becomes text input
- Goal becomes textarea
- Description becomes textarea
- Success Criteria becomes textarea
- Save/Cancel buttons appear
- Save calls `PATCH /phases/{id}`

**Status dropdown**: Simple `<select>` with valid transitions:
| From | Options |
|------|---------|
| NotStarted | Planning, Cancelled |
| Planning | InProgress, Blocked, Cancelled |
| InProgress | Review, Blocked, Cancelled |
| Review | Completed, InProgress, Blocked, Cancelled |
| Blocked | Planning, InProgress, Review, Cancelled |
| Completed | — (terminal) |
| Cancelled | NotStarted (reopen) |

Confirmation via `window.confirm()` for Completed and Cancelled transitions.

**Delete**: Trash icon, `window.confirm()`, navigates to `/phases` on success.

---

### 8. Create Milestone Dialog (P1)

**New file**: `web/src/lib/components/milestones/CreateMilestoneDialog.svelte`

**Pattern**: Same as CreatePhaseDialog / CreateProjectDialog.

**Fields**:
| Field | Type | Required | Notes |
|-------|------|----------|-------|
| Name | text input | Yes | Auto-focus |
| Description | textarea (3 rows) | No | |
| Version | text input | No | Monospace, placeholder "e.g., 1.0.0" |
| Target Date | date input | No | Must be today or future (inline validation) |

**Triggered from**: "New Milestone" button in milestones list page header.

---

### 9. Milestone Detail Page (P1)

**New file**: `web/src/routes/milestones/[id]/+page.svelte`

**Layout** (simplified per Devil's Advocate — no progress charts or task rollup):
```
[← All Milestones]

[Milestone Name]  [v1.2.0]  [Status: select dropdown ▾]  [✏️ Edit] [🗑️ Delete]

Description: ...

Created: Jan 15  |  Target: Mar 1  |  Started: Jan 20  |  Completed: --

--- Phases (count) ---
[1] Setup & Infrastructure    [Completed]  → link to /phases/1
[2] Core Features              [InProgress] → link to /phases/2
[3] Testing & QA               [NotStarted] → link to /phases/3
```

**Edit mode**: Same Save/Cancel pattern as Phase Detail.

**Status dropdown**: Simple `<select>`:
| From | Options |
|------|---------|
| Planning | InProgress, Cancelled |
| InProgress | Completed, OnHold, Cancelled |
| OnHold | InProgress, Cancelled |
| Completed | — (terminal) |
| Cancelled | Planning (reopen) |

Confirmation for Completed/Cancelled.

**Delete**: Only available if milestone has zero phases (backend enforces). Shows `window.confirm()`.

**Phases section**: Vertical list with phase number badge, name (linked), status badge. Uses same visual style as current `/phases` page stepper.

**Empty state for phases**: Dashed border box with "No phases yet" + "Add Phase" button (UX Advocate recommendation).

---

### 10. Milestone List Page Updates (P1)

**File**: `web/src/routes/milestones/+page.svelte`

Changes:
- Add "New Milestone" button in header (top-right)
- Milestone cards become `<a href="/milestones/{id}">` links (instead of inline expand)
- Remove the inline phases expand (detail page replaces it)
- Keep grouped layout (Active, Planning, Completed, Other)

---

### 11. Phase MCP Tools (P1)

**File**: `mcp/src/tools/phase-tools.ts` (add to existing)

New tools:
1. `get_phase` — Get phase details + tasks. Params: `phaseId: number`
2. `list_phases` — List phases for milestone. Params: `milestoneId: number`
3. `update_phase` — Update phase fields. Params: `phaseId, name?, description?, goal?, successCriteria?, phaseNumber?, dependsOnPhaseIds?`
4. `delete_phase` — Delete phase. Params: `phaseId: number`
5. `change_phase_status` — Change status. Params: `phaseId, status: enum`

---

### 12. Milestone MCP Tools (P1)

**New file**: `mcp/src/tools/milestone-tools.ts`

Register in `mcp/src/index.ts`.

Tools:
1. `list_milestones` — List milestones for project. Params: `projectId?: number, status?: enum`
2. `get_milestone` — Get milestone + phases. Params: `milestoneId: number`
3. `create_milestone` — Create milestone. Params: `projectId?, name, description?, version?, targetDate?`
4. `update_milestone` — Update milestone. Params: `milestoneId, name?, description?, version?, targetDate?`
5. `change_milestone_status` — Change status. Params: `milestoneId, status: enum`
6. `delete_milestone` — Delete milestone. Params: `milestoneId: number`

---

### 13. Clickable Git Links (P1)

**New file**: `web/src/lib/utils/git.ts`

GitHub-only URL builders (comment for extensibility):
```ts
export function buildCommitUrl(repoUrl: string, sha: string): string {
  const normalized = repoUrl.replace(/\.git$/, '').replace(/\/$/, '');
  return `${normalized}/commit/${sha}`;
  // TODO: Add GitLab/Bitbucket support if needed
}

export function buildBranchUrl(repoUrl: string, branch: string): string {
  const normalized = repoUrl.replace(/\.git$/, '').replace(/\/$/, '');
  return `${normalized}/tree/${encodeURIComponent(branch)}`;
}
```

**Modify**: TaskDetail component — make git branch and commit SHA clickable links using these utilities. Gracefully degrade to plain text if no repository URL on the project.

**Data flow**: Project repository URL available via project store or passed as prop.

---

### 14. Git Badges on Board Cards (P2)

**File**: Modify BoardCard component

Show compact badges in the bottom row:
- Branch badge: `GitBranch` icon + last path segment of branch name (truncated)
- PR badge: `GitPullRequest` icon + "PR #N" (if PullRequestUrl is set, after P2 #15)

Only render if the field has a value. Keep cards compact.

---

### 15. PR Tracking on Tasks (P2)

**Entity change**: Add `PullRequestUrl` (string?, max 1000) to `LifecycleTask.cs`

**API changes**:
- `TaskEndpoints.cs`: Add to update DTO
- `AiEndpoints.cs`: Add to `TaskTransitionRequest`
- `mcp/src/tools/task-tools.ts`: Add `prUrl` parameter to `complete_task`

**UI**: TaskDetail shows PR link with `GitPullRequest` icon. Extract label: "PR #42" from GitHub URL.

**Frontend type**: Add `pullRequestUrl?: string` to Task type.

---

## Implementation Phases

### Phase 1: Bug Fixes & API Client Fixes (Items 1-5)
*~2 hours. No new UI. Fixes active data loss and unblocks all other work.*

Files touched:
- `api/Api/AiEndpoints.cs` — fix TaskTransitionRequest
- `api/Api/PhaseEndpoints.cs` — fix DELETE cascade
- `api/Api/MilestoneEndpoints.cs` — reject DELETE with phases
- `web/src/lib/api/endpoints/phases.ts` — fix create URL, add changeStatus
- `web/src/lib/api/endpoints/milestones.ts` — fix create URL, add changeStatus

### Phase 2: Phase CRUD UI (Items 6-7)
*~3 hours. Enables full phase management from the web.*

Files touched:
- New: `web/src/lib/components/phases/CreatePhaseDialog.svelte`
- Modify: `web/src/routes/phases/+page.svelte` (add create button)
- Modify: `web/src/routes/phases/[id]/+page.svelte` (editing, status, delete)

### Phase 3: Milestone CRUD UI (Items 8-10)
*~3.5 hours. Enables full milestone management from the web.*

Files touched:
- New: `web/src/lib/components/milestones/CreateMilestoneDialog.svelte`
- New: `web/src/routes/milestones/[id]/+page.svelte`
- Modify: `web/src/routes/milestones/+page.svelte` (create button, link to detail)

### Phase 4: MCP Tools + Git Links (Items 11-13)
*~1.5 hours. Can be done in parallel with Phases 2-3.*

Files touched:
- Modify: `mcp/src/tools/phase-tools.ts` (add 5 tools)
- New: `mcp/src/tools/milestone-tools.ts` (6 tools)
- Modify: `mcp/src/index.ts` (register milestone tools)
- New: `web/src/lib/utils/git.ts`
- Modify: TaskDetail component (clickable links)

### Phase 5: P2 Items (Items 14-17)
*~2 hours. Polish and additional features.*

Files touched:
- Modify: BoardCard component (git badges)
- `api/Data/Entities/LifecycleTask.cs` (PullRequestUrl)
- `api/Api/TaskEndpoints.cs`, `api/Api/AiEndpoints.cs` (PR field)
- `mcp/src/tools/task-tools.ts` (prUrl param)
- Modify: `web/src/routes/+page.svelte` (dashboard link)
- Modify: `web/src/routes/milestones/[id]/+page.svelte` (Add Phase button)

---

## Complete File Change Summary

### New Files (7)
| File | Purpose |
|------|---------|
| `web/src/lib/components/phases/CreatePhaseDialog.svelte` | Create phase modal |
| `web/src/lib/components/milestones/CreateMilestoneDialog.svelte` | Create milestone modal |
| `web/src/routes/milestones/[id]/+page.svelte` | Milestone detail page |
| `web/src/lib/utils/git.ts` | Git URL builder utilities |
| `mcp/src/tools/milestone-tools.ts` | Milestone MCP tools |
| DB migration for PullRequestUrl | Entity field addition |
| DB migration for milestone DELETE constraint (if needed) | Backend behavior |

### Modified Files (14)
| File | Changes |
|------|---------|
| `api/Api/AiEndpoints.cs` | Fix TaskTransitionRequest to capture git fields + PullRequestUrl |
| `api/Api/PhaseEndpoints.cs` | Fix DELETE to unassign tasks and clean dependencies |
| `api/Api/MilestoneEndpoints.cs` | Reject DELETE when phases exist |
| `api/Api/TaskEndpoints.cs` | Add PullRequestUrl to update DTO |
| `api/Data/Entities/LifecycleTask.cs` | Add PullRequestUrl field |
| `web/src/lib/api/endpoints/phases.ts` | Fix create URL, add changeStatus |
| `web/src/lib/api/endpoints/milestones.ts` | Fix create URL, add changeStatus |
| `web/src/routes/phases/+page.svelte` | Add "Add Phase" button |
| `web/src/routes/phases/[id]/+page.svelte` | Full editing, status dropdown, delete |
| `web/src/routes/milestones/+page.svelte` | Add create button, link cards to detail |
| `web/src/routes/+page.svelte` | Link active milestone to detail |
| `mcp/src/tools/phase-tools.ts` | Add 5 new tools |
| `mcp/src/tools/task-tools.ts` | Add prUrl to complete_task |
| `mcp/src/index.ts` | Register milestone tools |

### Frontend Types (1)
| File | Changes |
|------|---------|
| `web/src/lib/types/index.ts` (or equivalent) | Add `pullRequestUrl` to Task type |

---

## Verification Checklist

- [x] All proposals reviewed by Devil's Advocate — cuts applied, scope reduced by ~40%
- [x] All proposals reviewed by UX Advocate — inconsistencies resolved, patterns standardized
- [x] Feedback incorporated: unified edit mode, unified status dropdown, unified delete pattern
- [x] Clear P1/P2/P3 priority tiers with justifications
- [x] Each feature has specific files to create/modify
- [x] Implementation phases are concrete and ordered by dependencies
- [x] Total P1 effort: ~10 hours across 5 implementation phases
- [x] Total P2 effort: ~2 hours
- [x] P3 items documented for future reference but explicitly deferred
