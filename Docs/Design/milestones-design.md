# Milestones UI & MCP Tools Design

## Overview

The backend has full Milestone CRUD with status transitions. The web UI currently shows a read-only grouped list (`/milestones`). The frontend API client (`milestones.ts`) already has `create`, `update`, `delete` methods that are unused. This design adds: create dialog, detail page, inline editing, status transitions, delete confirmation, MCP tools, and a reusable milestone picker.

---

## 1. Create Milestone Dialog

### Trigger Points
- **Primary**: "New Milestone" button in the `/milestones` page header (top-right, matching the Team page's "Add Member" button pattern)
- **Secondary**: Quick Actions section on Dashboard (add a "New Milestone" link alongside "New Task")

### Dialog Component: `CreateMilestoneDialog.svelte`

Location: `web/src/lib/components/shared/CreateMilestoneDialog.svelte`

Follow the `CreateProjectDialog.svelte` pattern: fixed overlay, centered card, escape/backdrop to close.

**Fields:**

| Field | Type | Required | Validation | Notes |
|-------|------|----------|------------|-------|
| Name | text input | Yes | Non-empty, max 200 chars | Auto-focus on open |
| Description | textarea (3 rows) | No | Max 2000 chars | Placeholder: "What will this milestone deliver?" |
| Version | text input | No | Max 50 chars | Placeholder: "e.g., 1.0.0" - monospace font |
| Target Date | date input | No | Must be today or future | HTML `<input type="date">` |

**Behavior:**
- Enter key on Name field submits the form
- Submit button disabled when Name is empty or mutation is pending
- On success: close dialog, invalidate `['milestones']` query, show the new milestone (scroll into view or navigate to detail)
- On error: show error message below the form (matching CreateProjectDialog pattern)
- Pass `projectId` from `getCurrentProjectId()` store

**API fix needed:** The frontend client currently posts to `/milestones` without `projectId`. Must change to `api.post<Milestone>(\`/projects/${projectId}/milestones\`, data)` -- or pass `projectId` in the body and update the create method signature to `create: (projectId: number, data: ...)`.

### Wireframe
```
┌─────────────────────────────────────┐
│  New Milestone                   [X]│
│─────────────────────────────────────│
│  Name *                             │
│  [________________________________]│
│                                     │
│  Description                        │
│  [________________________________]│
│  [________________________________]│
│                                     │
│  Version            Target Date     │
│  [______________]   [____________] │
│                                     │
│  [Create Milestone]  [Cancel]       │
└─────────────────────────────────────┘
```

---

## 2. Milestone Detail Page (`/milestones/[id]`)

### Route
`web/src/routes/milestones/[id]/+page.svelte`

Follow the `phases/[id]/+page.svelte` pattern: use `$page.params.id`, `createQuery` for data, back link to `/milestones`.

### Layout

```
┌──────────────────────────────────────────────────────────────────┐
│ ← All Milestones                                                 │
│                                                                   │
│ ┌──────────────────────────────────────────────────────────────┐ │
│ │  Milestone Name              [Planning ▾]     [Edit] [...]  │ │
│ │  v1.2.0                                                     │ │
│ │  Description text goes here...                              │ │
│ │                                                             │ │
│ │  Created: Jan 15  |  Target: Mar 1  |  Started: --          │ │
│ └──────────────────────────────────────────────────────────────┘ │
│                                                                   │
│ ┌─────────────────┐  ┌────────────────────────────────────────┐ │
│ │  Progress        │  │  Timeline / Date Info                  │ │
│ │  ██████░░░░ 60%  │  │  Created:   2026-01-15                │ │
│ │  3/5 phases done │  │  Started:   2026-01-20                │ │
│ │                  │  │  Target:    2026-03-01                │ │
│ │  12/20 tasks     │  │  Completed: --                        │ │
│ └─────────────────┘  └────────────────────────────────────────┘ │
│                                                                   │
│ Phases (5)                                                        │
│ ┌──────────────────────────────────────────────────────────────┐ │
│ │  [1]  Setup & Infrastructure        [Completed]  3/3 tasks  │ │
│ │   │                                                         │ │
│ │  [2]  Core Features                 [InProgress] 5/8 tasks  │ │
│ │   │                                                         │ │
│ │  [3]  Testing & QA                  [NotStarted] 0/4 tasks  │ │
│ │   │                                                         │ │
│ │  [4]  Documentation                 [NotStarted] 0/3 tasks  │ │
│ │   │                                                         │ │
│ │  [5]  Release                       [NotStarted] 0/2 tasks  │ │
│ └──────────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────┘
```

### Sections

#### Header Section
- Back link: `← All Milestones` (links to `/milestones`)
- Milestone name as `<h1>` (editable inline -- see Section 3)
- Version badge if present (small text, e.g., `v1.2.0`)
- Status badge (clickable -- opens status transition dropdown, see Section 4)
- Kebab menu (`...`) or explicit buttons: Edit, Delete
- Description paragraph below name (editable inline)
- Metadata row: Created date, Target date, Started date (when applicable)

#### Progress Summary (stats cards, 2-column grid)
- **Phase progress**: Bar chart showing completed/total phases, percentage
- **Task rollup**: Aggregate task counts across all phases in this milestone (query tasks for all phases). Show `done/total` with a progress bar.
- **Timeline info**: Created, Started, Target, Completed dates in a clean list

#### Phases List
Reuse the same vertical timeline/stepper layout from the `/phases` page:
- Phase number in a circle (colored by status)
- Phase name (links to `/phases/[id]`)
- Status badge
- Task count with mini progress bar
- Vertical connector line between phases

### Data Fetching
```typescript
// Fetch milestone with phases included
const milestoneQuery = createQuery(() => ({
  queryKey: ['milestones', id],
  queryFn: () => milestonesApi.get(id),
}));
```

The existing `GET /milestones/{id}` endpoint already includes phases with their status and orderIndex. For task counts per phase, we may need to either:
- (a) Extend the milestone detail endpoint to include `taskCount`/`doneCount` per phase (preferred), or
- (b) Make separate queries per phase (not scalable)

**Recommendation:** Extend `GET /milestones/{id}` to include task rollup per phase. The backend should join tasks and return counts.

### Navigation Updates
- Milestone cards on the `/milestones` list page should link to `/milestones/[id]` instead of toggling an expand
- The phases section in the expand can be removed (detail page replaces it)
- Dashboard "Active Milestone" card should link to `/milestones/[id]`

---

## 3. Edit Milestone

### Approach: Inline Editing on Detail Page

Follow the Settings page pattern (`settings/+page.svelte`): toggle between view and edit modes with a pencil icon.

**Editable fields on the detail page:**
- **Name**: Click pencil icon next to name -> shows text input in place
- **Description**: Click pencil icon -> shows textarea in place
- **Version**: Click pencil icon -> shows text input
- **Target Date**: Click pencil icon -> shows date input

### UX Flow
1. User clicks pencil icon (top-right of header card, matching Settings pattern)
2. Fields become editable inputs (pre-filled with current values)
3. Save/Cancel buttons appear
4. Save calls `PATCH /milestones/{id}` with changed fields only
5. On success: exit edit mode, invalidate query
6. On error: show error message, stay in edit mode

### Alternative Considered: Edit Dialog
A modal dialog was considered but rejected because:
- The detail page is the natural place to edit a milestone
- Inline editing provides better context (you see the phases while editing)
- Matches the Settings page pattern already used in the app

### Implementation
```svelte
<script>
  let editing = $state(false);
  let editName = $state('');
  let editDescription = $state('');
  let editVersion = $state('');
  let editTargetDate = $state('');

  const updateMut = createMutation(() => ({
    mutationFn: (data) => milestonesApi.update(id, data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['milestones', id] });
      queryClient.invalidateQueries({ queryKey: ['milestones'] });
      editing = false;
    },
  }));

  function startEditing() {
    const m = milestoneQuery.data;
    editName = m.name;
    editDescription = m.description || '';
    editVersion = m.version || '';
    editTargetDate = m.targetDate ? m.targetDate.split('T')[0] : '';
    editing = true;
  }
</script>
```

---

## 4. Status Transition UI

### Status Flow
```
Planning ──→ InProgress ──→ Completed
    │             │
    │             ├──→ OnHold ──→ InProgress (resume)
    │             │
    │             └──→ Cancelled
    │
    └──→ Cancelled
```

### Valid Transitions
| Current Status | Allowed Next Statuses |
|---------------|----------------------|
| Planning | InProgress, Cancelled |
| InProgress | Completed, OnHold, Cancelled |
| OnHold | InProgress, Cancelled |
| Completed | (terminal -- no transitions) |
| Cancelled | Planning (reopen) |

### UI: Status Dropdown on Detail Page

The `StatusBadge` on the detail page header becomes clickable (only on the detail page, not on the list).

**Interaction:**
1. Click the status badge -> dropdown appears below it showing valid next statuses
2. Each option shows the status name with its colored badge
3. Selecting a status calls `POST /milestones/{id}/status` with the new status
4. Dropdown closes, query invalidates

**Special transitions with side effects:**
- **Planning -> InProgress**: Sets `startedAt` to now (backend handles this already)
- **InProgress -> Completed**: Sets `completedAt` to now (backend handles this). Show a brief confirmation: "Mark milestone as completed? This will record the completion date."
- **Any -> Cancelled**: Show confirmation: "Cancel this milestone? Phases and tasks will not be deleted."

### Component: `StatusTransitionDropdown.svelte`

Location: `web/src/lib/components/shared/StatusTransitionDropdown.svelte`

Generic component that takes `currentStatus`, `entityType` (milestone/phase), and `onTransition` callback. Computes valid transitions based on entity type and current status.

```svelte
<script lang="ts">
  let { currentStatus, entityType, onTransition } = $props();
  let open = $state(false);

  const milestoneTransitions: Record<string, string[]> = {
    Planning: ['InProgress', 'Cancelled'],
    InProgress: ['Completed', 'OnHold', 'Cancelled'],
    OnHold: ['InProgress', 'Cancelled'],
    Cancelled: ['Planning'],
  };

  let validNext = $derived(
    entityType === 'milestone'
      ? milestoneTransitions[currentStatus] || []
      : [] // extend for phases later
  );
</script>
```

### Validation
- Backend already accepts any status via `ChangeMilestoneStatusRequest`. We should add server-side validation of valid transitions too (future enhancement), but the UI will enforce them client-side for now.

---

## 5. Delete Milestone

### Trigger
- Kebab menu (`...`) or explicit "Delete" button on the detail page header
- NOT available from the list page (too easy to accidentally delete)

### Confirmation Flow
1. User clicks Delete
2. Confirmation dialog appears:
   - "Delete milestone '{name}'?"
   - Warning text: "This will permanently delete this milestone and all its phases and tasks."
   - Show impact: "X phases and Y tasks will be deleted" (compute from loaded data)
   - Red "Delete" button + "Cancel" button
3. On confirm: call `DELETE /milestones/{id}`
4. On success: navigate to `/milestones` list, invalidate queries
5. On error: show error toast/message

### Cascade Behavior
The backend currently does a simple `db.Milestones.Remove(milestone)`. This relies on EF Core cascade delete (if configured) or will fail with a foreign key violation if phases exist.

**Backend recommendation:** The delete endpoint should either:
- (a) Cascade delete phases and their tasks (current apparent behavior if FK cascade is configured), or
- (b) Reject deletion if phases exist, returning 409 Conflict with a message

For the UI, show the warning regardless. If the backend rejects, display the error.

### Component
Use a simple confirmation dialog (similar to `window.confirm` used on the Team page, but upgraded to a proper modal for better UX).

```svelte
<!-- DeleteConfirmDialog.svelte -->
<div class="fixed inset-0 z-50 flex items-center justify-center bg-black/50">
  <div class="mx-4 w-full max-w-sm rounded-lg border border-border bg-surface p-5">
    <h2 class="text-sm font-semibold text-text-primary">Delete Milestone</h2>
    <p class="mt-2 text-sm text-text-secondary">
      This will permanently delete "{name}" and all its phases and tasks.
    </p>
    <p class="mt-1 text-xs text-danger">
      {phaseCount} phases and {taskCount} tasks will be removed.
    </p>
    <div class="mt-4 flex items-center gap-2">
      <button class="rounded-md bg-danger px-3 py-1.5 text-sm text-white">Delete</button>
      <button class="rounded-md border border-border px-3 py-1.5 text-sm text-text-secondary">Cancel</button>
    </div>
  </div>
</div>
```

---

## 6. MCP Tools

### New File: `mcp/src/tools/milestone-tools.ts`

Register in `mcp/src/index.ts` alongside existing tools:
```typescript
import { registerMilestoneTools } from './tools/milestone-tools.js';
// ...
registerMilestoneTools(server);
```

### Tool Definitions

#### `list_milestones`
```typescript
server.tool(
  'list_milestones',
  'List all milestones for the active project, optionally filtered by status',
  {
    projectId: z.number().optional().describe('Project ID (defaults to active project)'),
    status: z.enum(['Planning', 'InProgress', 'OnHold', 'Completed', 'Cancelled']).optional()
      .describe('Filter by status'),
  },
  async ({ projectId, status }) => {
    const pid = projectId ?? getActiveProjectId();
    let milestones = await api.get(`/projects/${pid}/milestones`);
    if (status) milestones = milestones.filter(m => m.status === status);
    return { content: [{ type: 'text', text: JSON.stringify(milestones, null, 2) }] };
  }
);
```

#### `get_milestone`
```typescript
server.tool(
  'get_milestone',
  'Get detailed milestone info including phases',
  {
    milestoneId: z.number().describe('Milestone ID'),
  },
  async ({ milestoneId }) => {
    const result = await api.get(`/milestones/${milestoneId}`);
    return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
  }
);
```

#### `create_milestone`
```typescript
server.tool(
  'create_milestone',
  'Create a new milestone for a project',
  {
    projectId: z.number().optional().describe('Project ID (defaults to active project)'),
    name: z.string().describe('Milestone name'),
    description: z.string().optional().describe('Milestone description'),
    version: z.string().optional().describe('Version string, e.g., "1.0.0"'),
    targetDate: z.string().optional().describe('Target completion date (ISO 8601, e.g., "2026-03-01")'),
  },
  async ({ projectId, name, description, version, targetDate }) => {
    const pid = projectId ?? getActiveProjectId();
    const result = await api.post(`/projects/${pid}/milestones`, {
      name, description, version,
      targetDate: targetDate ? new Date(targetDate).toISOString() : undefined,
    });
    return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
  }
);
```

#### `update_milestone`
```typescript
server.tool(
  'update_milestone',
  'Update milestone fields (name, description, version, targetDate)',
  {
    milestoneId: z.number().describe('Milestone ID'),
    name: z.string().optional().describe('New name'),
    description: z.string().optional().describe('New description'),
    version: z.string().optional().describe('New version string'),
    targetDate: z.string().optional().describe('New target date (ISO 8601)'),
  },
  async ({ milestoneId, ...data }) => {
    const body: any = {};
    if (data.name) body.name = data.name;
    if (data.description) body.description = data.description;
    if (data.version) body.version = data.version;
    if (data.targetDate) body.targetDate = new Date(data.targetDate).toISOString();
    const result = await api.patch(`/milestones/${milestoneId}`, body);
    return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
  }
);
```

#### `change_milestone_status`
```typescript
server.tool(
  'change_milestone_status',
  'Change a milestone status (Planning, InProgress, OnHold, Completed, Cancelled)',
  {
    milestoneId: z.number().describe('Milestone ID'),
    status: z.enum(['Planning', 'InProgress', 'OnHold', 'Completed', 'Cancelled'])
      .describe('New status'),
  },
  async ({ milestoneId, status }) => {
    const result = await api.post(`/milestones/${milestoneId}/status`, { status });
    return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
  }
);
```

#### `delete_milestone`
```typescript
server.tool(
  'delete_milestone',
  'Delete a milestone and all its phases/tasks (irreversible)',
  {
    milestoneId: z.number().describe('Milestone ID to delete'),
  },
  async ({ milestoneId }) => {
    await api.delete(`/milestones/${milestoneId}`);
    return { content: [{ type: 'text', text: `Milestone ${milestoneId} deleted successfully` }] };
  }
);
```

---

## 7. Milestone Selector/Picker Component

### Purpose
Several views need milestone context:
- **Phases page** (`/phases`) -- currently auto-selects "first InProgress or Planning" milestone
- **Metrics page** -- could filter metrics by milestone
- **Board** -- could filter tasks by milestone's phases

### Component: `MilestoneSelector.svelte`

Location: `web/src/lib/components/shared/MilestoneSelector.svelte`

### Design

A compact dropdown that shows the current milestone and allows switching. Appears as a sub-header element on pages that need milestone context.

```
┌──────────────────────────────────┐
│  ◎ Milestone: [v1.2 - Auth ▾]   │
└──────────────────────────────────┘
         │
         ▼ (dropdown)
┌──────────────────────────────────┐
│  ● v1.2 - Auth Overhaul  [Active]│
│  ○ v1.3 - Dashboard      [Plan] │
│  ○ v1.1 - Initial Setup  [Done] │
└──────────────────────────────────┘
```

### Props
```typescript
interface MilestoneSelectorProps {
  /** Currently selected milestone ID */
  selected: number | null;
  /** Callback when selection changes */
  onselect: (milestoneId: number) => void;
  /** Optional: only show milestones with these statuses */
  filterStatuses?: MilestoneStatus[];
  /** Show "All milestones" option */
  showAll?: boolean;
}
```

### Behavior
- Fetches milestones from the `['milestones']` query (shared cache)
- Groups by status in dropdown: Active first, then Planning, then Completed
- Shows status badge next to each option
- Emits `onselect` when changed
- If `selected` is null and no `showAll`, auto-selects first InProgress milestone (or first Planning, or first overall)

### Usage on Phases Page
Replace the current auto-selection logic:
```svelte
<!-- Before -->
let activeMilestone = $derived(
  milestonesQuery.data?.find(m => m.status === 'InProgress' || m.status === 'Planning')
);

<!-- After -->
<MilestoneSelector
  selected={selectedMilestoneId}
  onselect={(id) => selectedMilestoneId = id}
/>
```

This gives users explicit control over which milestone's phases they're viewing.

---

## 8. Frontend API Client Fixes

The `milestones.ts` client needs these fixes:

```typescript
export const milestones = {
  list: (projectId: number) =>
    api.get<Milestone[]>(`/projects/${projectId}/milestones`),
  get: (id: number) =>
    api.get<Milestone>(`/milestones/${id}`),
  create: (projectId: number, data: { name: string; description?: string; version?: string; targetDate?: string }) =>
    api.post<Milestone>(`/projects/${projectId}/milestones`, data),
  update: (id: number, data: Partial<Milestone>) =>
    api.patch<Milestone>(`/milestones/${id}`, data),
  delete: (id: number) =>
    api.delete(`/milestones/${id}`),
  changeStatus: (id: number, status: MilestoneStatus) =>
    api.post<Milestone>(`/milestones/${id}/status`, { status }),
};
```

**Changes:**
1. `create` now takes `projectId` as first arg (matches backend `POST /projects/{projectId}/milestones`)
2. Added `changeStatus` method (was missing, maps to `POST /milestones/{id}/status`)

---

## 9. Milestones List Page Updates

The current `/milestones` list page needs these changes:

1. **Add "New Milestone" button** in the header (top-right)
2. **Make milestone cards link to detail page** (`/milestones/[id]`) instead of expand-in-place
3. **Remove the inline expand** (phases list) -- the detail page replaces this
4. **Keep the grouped layout** (Active, Planning, Completed, Other) -- this is good UX

### Updated Card
Each milestone card becomes an `<a href="/milestones/{milestone.id}">` with:
- Name, version, status badge (same as now)
- Description preview (truncated to 2 lines)
- Target date if set
- Phase count summary: "3/5 phases completed"
- Right arrow icon on hover (matching phase cards in dashboard)

---

## 10. Dashboard Updates

### Active Milestone Card
The "Active Milestone" section on the dashboard should link to the milestone detail:
```svelte
<a href="/milestones/{data.activeMilestone.id}" class="...">
  <!-- existing content -->
</a>
```

### Quick Actions
Add "New Milestone" to the Quick Actions section:
```svelte
<a href="/milestones" class="...">
  <Plus class="h-3.5 w-3.5" />
  New Milestone
</a>
```

---

## 11. File Summary

### New Files
| File | Purpose |
|------|---------|
| `web/src/routes/milestones/[id]/+page.svelte` | Milestone detail page |
| `web/src/lib/components/shared/CreateMilestoneDialog.svelte` | Create dialog |
| `web/src/lib/components/shared/MilestoneSelector.svelte` | Reusable picker |
| `web/src/lib/components/shared/StatusTransitionDropdown.svelte` | Status change dropdown |
| `mcp/src/tools/milestone-tools.ts` | MCP tool definitions |

### Modified Files
| File | Changes |
|------|---------|
| `web/src/routes/milestones/+page.svelte` | Add create button, link cards to detail, remove expand |
| `web/src/routes/phases/+page.svelte` | Use MilestoneSelector component |
| `web/src/routes/+page.svelte` | Link active milestone card to detail page |
| `web/src/lib/api/endpoints/milestones.ts` | Fix create URL, add changeStatus method |
| `mcp/src/index.ts` | Register milestone tools |

### Backend Enhancements (Optional but Recommended)
| File | Changes |
|------|---------|
| `api/Api/MilestoneEndpoints.cs` | Extend GET detail to include task counts per phase |
| `api/Api/MilestoneEndpoints.cs` | Add server-side status transition validation |

---

## 12. Implementation Order

1. **API client fixes** (`milestones.ts`) -- unblocks everything
2. **Create Milestone Dialog** -- enables creating milestones from UI
3. **Milestone Detail Page** -- the key new page
4. **Inline Editing** on detail page
5. **Status Transition Dropdown**
6. **Delete Confirmation**
7. **Milestones List Page Updates** (link to detail, add create button)
8. **Dashboard Updates** (link active milestone)
9. **MilestoneSelector Component**
10. **Phases page update** (use MilestoneSelector)
11. **MCP Tools** (can be done in parallel with UI work)
