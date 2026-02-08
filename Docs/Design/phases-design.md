# Phase UI CRUD, Status Workflows, and Dependency Visualization

## Current State

- **Backend**: Full CRUD at `POST /milestones/{milestoneId}/phases`, `GET /phases/{id}`, `PATCH /phases/{id}`, `DELETE /phases/{id}`, `POST /phases/{id}/status`
- **Frontend API client** (`phases.ts`): Has `list`, `get`, `create`, `update`, `delete` -- but `create` posts to `/phases` instead of `/milestones/{milestoneId}/phases` (BUG: needs fix)
- **Frontend API client**: Missing `changeStatus(id, status)` method
- **Web UI**: Entirely read-only. Phases list page (`/phases`) and phase detail page (`/phases/[id]`) only display data
- **MCP tools**: Only `create_phase` and `breakdown_phase` exist. Missing get, list, update, delete, change_status
- **PhaseStatus enum**: `NotStarted(0)`, `Planning(1)`, `InProgress(2)`, `Review(3)`, `Blocked(4)`, `Completed(5)`, `Cancelled(6)`
- **Phase entity fields**: `Id`, `MilestoneId`, `Name`, `Description`, `Goal`, `SuccessCriteria`, `Status`, `PhaseNumber`, `OrderIndex`, `DependsOnPhaseIds` (string, comma-separated), `StartedAt`, `CompletedAt`, `CreatedAt`, `UpdatedAt`

---

## 1. Create Phase Dialog

### Trigger Points

1. **Phases list page** (`/phases`): "Add Phase" button in the page header, next to the title
2. **Milestone detail page** (future): "Add Phase" button in the phases section

### Dialog Design

Modal dialog, following the `CreateProjectDialog.svelte` pattern (overlay backdrop, centered card).

**File**: `web/src/lib/components/phases/CreatePhaseDialog.svelte`

```
Props: { milestoneId: number; existingPhases: Phase[]; onCreated: (p: Phase) => void; onClose: () => void }
```

**Fields (in order)**:

| Field | Type | Required | Notes |
|-------|------|----------|-------|
| Name | text input | Yes | Placeholder: "Phase name". Auto-focus on open. Submit on Enter. |
| Phase Number | number input | No | Auto-calculated: max existing phaseNumber + 1. Editable. |
| Goal | textarea (2 rows) | No | Placeholder: "What does this phase achieve?" |
| Description | textarea (2 rows) | No | Placeholder: "Additional details..." |
| Success Criteria | textarea (3 rows) | No | Placeholder: "Markdown supported. What defines done?" |
| Dependencies | multi-select chips | No | Shows other phases in this milestone. See section 4. |

**Validation**:
- Name is required, trimmed, min 1 character
- PhaseNumber must be positive integer if provided
- No duplicate phase names within the same milestone (client-side warning, not a hard block)

**Behavior**:
- Dialog opens with Name input focused
- "Create Phase" button (accent color, Check icon) + "Cancel" button
- On success: close dialog, invalidate `['phases']` query, show the new phase in the list
- On error: inline error message below buttons (same as `CreateProjectDialog`)
- Keyboard: Enter in Name field submits. Escape closes.

### Frontend API Fix Required

The `phases.ts` `create` method currently posts to `/phases` which doesn't match the backend `POST /milestones/{milestoneId}/phases`. Fix:

```ts
create: (milestoneId: number, data: Partial<Phase>) =>
    api.post<Phase>(`/milestones/${milestoneId}/phases`, data),
```

Also add the missing `changeStatus` method:

```ts
changeStatus: (id: number, status: PhaseStatus) =>
    api.post<Phase>(`/phases/${id}/status`, { status }),
```

---

## 2. Edit Phase

### Approach: Inline editing on Phase Detail page

Follow the pattern from `TaskDetail.svelte` where the title is click-to-edit and fields use select dropdowns that save on change. No separate edit modal needed.

### Editable Fields on Phase Detail (`/phases/[id]`)

| Field | Edit Method | Save Trigger |
|-------|-------------|-------------|
| Name | Click title text to switch to input. Current pattern from TaskDetail. | Blur or Enter |
| Goal | Click text to switch to textarea | Blur |
| Description | MarkdownEditor component (already exists in codebase) | onChange callback |
| Success Criteria | MarkdownEditor component | onChange callback |
| Phase Number | Small number input in metadata section | Blur or Enter |
| Dependencies | Chip selector (see section 4) | On chip add/remove |
| Status | Dedicated section (see section 3) | Button click |

### Non-editable Fields

- `Id`, `MilestoneId`, `OrderIndex` (managed by reorder), `StartedAt`, `CompletedAt`, `CreatedAt`, `UpdatedAt` -- displayed as read-only metadata at the bottom

### Phase Detail Page Layout Redesign

```
[Back to Phases]

[Phase Name (click to edit)]  [StatusBadge]  [Status Actions ...]

Goal: [click to edit text]

[Description - MarkdownEditor]

--- Success Criteria ---
[MarkdownEditor with checkbox markdown support]

--- Dependencies ---
[Chip selector showing dependent phases]

--- Tasks (count) ---
[Grid of BoardCards, same as current]

--- Metadata ---
Phase #N | Created <date> | Started <date> | Completed <date>
```

### Save Pattern

Each field saves independently via `PATCH /phases/{id}` with only the changed field. Use `createMutation` with `onSuccess` invalidating `['phases', id]` query. No "Save All" button needed.

---

## 3. Status Transition UI

### Status Workflow

```
NotStarted --> Planning --> InProgress --> Review --> Completed
                   |            |           |
                   v            v           v
                Blocked      Blocked     Blocked
                   |            |           |
                   v            v           v
               (back to)   (back to)   (back to)
               Planning    InProgress   Review

Any status --> Cancelled
Cancelled --> NotStarted (reopen)
```

### Valid Transitions Table

| From | Can transition to |
|------|------------------|
| NotStarted | Planning, Cancelled |
| Planning | InProgress, Blocked, Cancelled |
| InProgress | Review, Blocked, Cancelled |
| Review | Completed, InProgress (send back), Blocked, Cancelled |
| Blocked | Planning, InProgress, Review (return to previous), Cancelled |
| Completed | (terminal - no transitions unless reopened via special action) |
| Cancelled | NotStarted (reopen) |

### UI Design: Action Buttons + Dropdown

Place status controls in the phase detail page header, right-aligned next to the phase name.

**Primary action button**: The most logical "next step" transition, shown as a prominent button.

| Current Status | Primary Button | Color |
|---|---|---|
| NotStarted | "Start Planning" | accent |
| Planning | "Start Work" | accent |
| InProgress | "Move to Review" | accent |
| Review | "Complete Phase" | success |
| Blocked | "Unblock" (returns to previous status) | warning |
| Completed | (no primary button) | -- |
| Cancelled | "Reopen" | accent |

**Secondary actions dropdown**: A "..." menu or small dropdown next to the primary button for less common transitions.

| Current Status | Dropdown Options |
|---|---|
| NotStarted | Cancel |
| Planning | Block, Cancel |
| InProgress | Block, Cancel |
| Review | Send Back to InProgress, Block, Cancel |
| Blocked | Cancel |
| Completed | (empty -- completed is terminal) |
| Cancelled | (empty -- reopen is the primary button) |

### Blocked Status Special Handling

When transitioning TO Blocked, show a small inline text input asking "What's blocking this phase?" which saves to a comment or the description. When unblocking, the phase returns to the status it was in before being blocked. Store the pre-block status in client state or derive it from activity log.

### Implementation

The status change calls `POST /phases/{id}/status` with the new status. The backend already handles setting `StartedAt` when moving to InProgress and `CompletedAt` when moving to Completed.

**Component**: `web/src/lib/components/phases/PhaseStatusActions.svelte`

```
Props: { phase: Phase; onStatusChange: (phase: Phase) => void }
```

### Phases List Page: Compact Status Actions

On the phases list (`/phases`), add a small status indicator that, on hover, shows a tooltip with the primary next action. Clicking the phase card navigates to detail (current behavior). Do NOT add status dropdowns to the list view -- that would be too cluttered. The list is for overview; detail page is for actions.

---

## 4. Phase Dependency Visualization

### Data Model

`DependsOnPhaseIds` is currently stored as a string (comma-separated IDs in the DB, e.g., `"1,3"`). The frontend type declares it as `number[]`. There may be a serialization mismatch -- needs verification. The backend `UpdatePhaseRequest` accepts it as a `string?`.

### Recommendation: Normalize to JSON array string

Store as `"[1,3]"` in the DB string field, parse on read. Or better: keep as comma-separated `"1,3"` since that's what the backend already handles.

### Dependency Display on Phases List

On each phase card in the list, if the phase has dependencies, show small "depends on" chips below the goal text:

```
Phase 3: Implementation
  Goal: Build the features
  Depends on: [Phase 1: Design] [Phase 2: Setup]
  3/10 tasks
```

The dependency chips are styled as small rounded pills with the dependent phase name, slightly muted color. Clicking a chip navigates to that phase.

### Dependency Display on Phase Detail

In the "Dependencies" section of the detail page, show:

```
--- Dependencies ---
This phase depends on:
  [Phase 1: Design ✓] [Phase 2: Setup ●]

Phases that depend on this:
  [Phase 4: Testing] [Phase 5: Deployment]
```

- Completed dependencies get a checkmark and green tint
- In-progress dependencies get a blue dot
- Not-started dependencies get a grey dot
- Blocked dependencies get a red dot

### Dependency Editor (on Phase Detail)

**Component**: `web/src/lib/components/phases/PhaseDependencyEditor.svelte`

A chip-based multi-select:

1. Show current dependencies as removable chips (X button on each chip)
2. Below the chips, a small "+ Add dependency" button
3. Clicking "+ Add dependency" opens a dropdown showing all other phases in the milestone (excluding self and already-selected)
4. Selecting a phase adds it as a chip
5. Removing a chip removes the dependency
6. Each add/remove immediately saves via `PATCH /phases/{id}` with the updated `dependsOnPhaseIds`

**Cycle detection**: Before saving, check client-side that adding a dependency doesn't create a cycle. E.g., if Phase A depends on Phase B, Phase B cannot depend on Phase A. Show a toast/error if a cycle would be created.

### Create Phase Dialog: Dependency Selection

In the create dialog, use the same chip multi-select pattern. Since the phase doesn't exist yet, only show existing phases from the same milestone.

---

## 5. Delete Phase

### Trigger

- **Phase Detail page**: Trash icon in the header (same placement as TaskDetail's delete button)
- **NOT on the phases list** -- delete is a destructive action that shouldn't be one click away from a list view

### Confirmation Dialog

Use `window.confirm()` for consistency with the existing TaskDetail delete pattern:

```
Delete phase "Phase Name"?

This will permanently delete the phase. Tasks in this phase will be unassigned
from any phase but will NOT be deleted.

[OK] [Cancel]
```

### Backend Behavior

The current `DELETE /phases/{id}` simply removes the phase from the database. However, **tasks in the phase have a `PhaseId` foreign key** that will be orphaned or cause a constraint error.

**Backend fix needed**: Before deleting a phase, set `PhaseId = null` on all tasks belonging to that phase. This should be done in a transaction:

```csharp
directGroup.MapDelete("/{id:int}", async (int id, LifecycleDbContext db) =>
{
    var phase = await db.Phases.Include(p => p.Tasks).FirstOrDefaultAsync(p => p.Id == id);
    if (phase is null) return Results.NotFound();

    // Unassign tasks from this phase
    foreach (var task in phase.Tasks)
        task.PhaseId = null;

    db.Phases.Remove(phase);
    await db.SaveChangesAsync();
    return Results.NoContent();
});
```

Also update `DependsOnPhaseIds` on other phases that reference the deleted phase ID -- remove the deleted ID from their dependency lists.

### Frontend Behavior

1. Call `DELETE /phases/{id}`
2. On success: navigate back to `/phases`, invalidate `['phases']` query
3. On error: show toast notification

---

## 6. Missing MCP Tools

### 6.1 `get_phase`

```ts
server.tool(
  'get_phase',
  'Get detailed information about a specific phase including its tasks',
  {
    phaseId: z.number().describe('Phase ID'),
  },
  async ({ phaseId }) => {
    const result = await api.get(`/phases/${phaseId}`);
    return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
  }
);
```

### 6.2 `list_phases`

```ts
server.tool(
  'list_phases',
  'List all phases for a milestone, ordered by phase number',
  {
    milestoneId: z.number().describe('Milestone ID'),
  },
  async ({ milestoneId }) => {
    const result = await api.get(`/milestones/${milestoneId}/phases`);
    return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
  }
);
```

### 6.3 `update_phase`

```ts
server.tool(
  'update_phase',
  'Update phase properties (name, description, goal, success criteria, dependencies, ordering)',
  {
    phaseId: z.number().describe('Phase ID to update'),
    name: z.string().optional().describe('New phase name'),
    description: z.string().optional().describe('New description'),
    goal: z.string().optional().describe('New goal'),
    successCriteria: z.string().optional().describe('New success criteria (markdown)'),
    phaseNumber: z.number().optional().describe('New phase number'),
    orderIndex: z.number().optional().describe('New order index'),
    dependsOnPhaseIds: z.string().optional().describe('Comma-separated phase IDs this depends on'),
  },
  async ({ phaseId, ...data }) => {
    const result = await api.patch(`/phases/${phaseId}`, data);
    return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
  }
);
```

### 6.4 `delete_phase`

```ts
server.tool(
  'delete_phase',
  'Delete a phase. Tasks in the phase will be unassigned but not deleted.',
  {
    phaseId: z.number().describe('Phase ID to delete'),
  },
  async ({ phaseId }) => {
    await api.delete(`/phases/${phaseId}`);
    return { content: [{ type: 'text' as const, text: `Phase ${phaseId} deleted successfully` }] };
  }
);
```

### 6.5 `change_phase_status`

```ts
server.tool(
  'change_phase_status',
  'Change a phase status. Valid statuses: NotStarted, Planning, InProgress, Review, Blocked, Completed, Cancelled',
  {
    phaseId: z.number().describe('Phase ID'),
    status: z.enum(['NotStarted', 'Planning', 'InProgress', 'Review', 'Blocked', 'Completed', 'Cancelled'])
      .describe('New phase status'),
  },
  async ({ phaseId, status }) => {
    const result = await api.post(`/phases/${phaseId}/status`, { status });
    return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
  }
);
```

---

## 7. Phase Ordering / Reordering

### Current Model

- `PhaseNumber`: User-visible number (displayed in the circle badge). Can have gaps (1, 2, 5).
- `OrderIndex`: Backend sort order. Dense, zero-based (0, 1, 2). Used by `ORDER BY OrderIndex`.

### Recommendation: Keep Both, Make PhaseNumber User-Editable

- `OrderIndex` controls the actual sort order in the list and is managed automatically
- `PhaseNumber` is the user-visible label and can be edited freely on the detail page
- When a new phase is created, `PhaseNumber` defaults to `maxExisting + 1` and `OrderIndex` defaults to `maxExisting + 1`

### Reorder UI: Drag-and-Drop on Phases List

**Implementation**: Use a lightweight drag library (e.g., `@dnd-kit` concepts adapted for Svelte, or simple HTML5 drag-and-drop).

**Behavior**:
1. Each phase card in the list gets a drag handle (grip dots icon, `GripVertical` from lucide) on the left side, replacing or augmenting the phase number circle
2. Dragging a phase card reorders it visually in the list
3. On drop, calculate new `OrderIndex` values for all affected phases
4. Send `PATCH /phases/{id}` with new `orderIndex` for each moved phase
5. Optionally auto-update `PhaseNumber` to match the new order (ask user preference or just update OrderIndex)

**Phase 1 (MVP)**: Skip drag-and-drop. Instead, add small up/down arrow buttons on each phase card in the list:
- Up arrow: swap `OrderIndex` with the phase above
- Down arrow: swap `OrderIndex` with the phase below
- First phase has no up arrow, last phase has no down arrow
- Each swap calls `PATCH /phases/{id}` for both phases

**Phase 2 (Enhancement)**: Full drag-and-drop.

### Backend Support

The backend already supports `PATCH /phases/{id}` with `orderIndex`, so no backend changes are needed for reordering. However, for bulk reordering efficiency, consider adding a batch endpoint later:

```
PUT /milestones/{milestoneId}/phases/reorder
Body: { phaseIds: [3, 1, 2, 5] }  // IDs in new order
```

---

## 8. Summary of Required Changes

### Frontend API Client (`phases.ts`)

```ts
import type { Phase, PhaseStatus } from '$lib/types';
import { api } from '../client';

export const phases = {
  list: (milestoneId: number) => api.get<Phase[]>(`/milestones/${milestoneId}/phases`),
  get: (id: number) => api.get<Phase>(`/phases/${id}`),
  create: (milestoneId: number, data: Partial<Phase>) =>
      api.post<Phase>(`/milestones/${milestoneId}/phases`, data),
  update: (id: number, data: Partial<Phase>) => api.patch<Phase>(`/phases/${id}`, data),
  delete: (id: number) => api.delete(`/phases/${id}`),
  changeStatus: (id: number, status: PhaseStatus) =>
      api.post<Phase>(`/phases/${id}/status`, { status }),
};
```

### New Components

| Component | Purpose |
|-----------|---------|
| `CreatePhaseDialog.svelte` | Modal for creating a new phase |
| `PhaseStatusActions.svelte` | Primary + dropdown status transition buttons |
| `PhaseDependencyEditor.svelte` | Chip multi-select for managing phase dependencies |
| `PhaseReorderControls.svelte` | Up/down arrows for MVP reordering |

### Modified Pages

| Page | Changes |
|------|---------|
| `/phases/+page.svelte` | Add "Add Phase" button, dependency chips on cards, reorder arrows |
| `/phases/[id]/+page.svelte` | Full inline editing, status actions, dependency editor, delete button, metadata display |

### Backend Changes

| Change | File |
|--------|------|
| Fix DELETE to unassign tasks first | `PhaseEndpoints.cs` |
| Clean up dependency references on delete | `PhaseEndpoints.cs` |

### MCP Tools

| Tool | Action |
|------|--------|
| `get_phase` | New |
| `list_phases` | New |
| `update_phase` | New |
| `delete_phase` | New |
| `change_phase_status` | New |

---

## 9. Implementation Priority

1. **Fix frontend API client** (create URL, add changeStatus) -- 15 min
2. **Create Phase dialog** -- allows creating phases from UI -- 1 hour
3. **Phase detail inline editing** -- core edit functionality -- 1.5 hours
4. **Status transition UI** -- unlock workflow management -- 1 hour
5. **Delete phase** (with backend fix) -- 30 min
6. **MCP tools** -- 30 min
7. **Dependency editor** -- 1 hour
8. **Dependency display on list** -- 30 min
9. **Reorder controls (MVP arrows)** -- 45 min
