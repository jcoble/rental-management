# Git Integration Design

## 1. Bug Fix: AI Endpoint `complete_task` Drops Git Fields

### Diagnosis

The MCP `complete_task` tool (`mcp/src/tools/task-tools.ts:47-55`) sends `gitCommitSha` and `gitBranch` in the POST body when completing a task:

```ts
const result = await api.post(`/ai/tasks/${taskId}/transition`, {
  status: 'Done',
  gitCommitSha: commitSha,
  gitBranch,
});
```

But the API endpoint (`api/Api/AiEndpoints.cs:128`) binds the body to `TaskTransitionRequest`, which is defined as:

```csharp
public record TaskTransitionRequest(TaskStatus Status);
```

This record **only has `Status`**. The `gitCommitSha` and `gitBranch` fields are silently discarded during deserialization. The transition handler (lines 128-189) never reads or writes git fields to the task entity.

### Fix

Extend `TaskTransitionRequest` and add git field assignment in the handler:

```csharp
// Change the request record (AiEndpoints.cs line 414):
public record TaskTransitionRequest(
    TaskStatus Status,
    string? GitCommitSha = null,
    string? GitBranch = null
);

// In the /tasks/{id}/transition handler, after setting status (around line 143):
if (req.GitCommitSha is not null) task.GitCommitSha = req.GitCommitSha;
if (req.GitBranch is not null) task.GitBranch = req.GitBranch;
```

This is a backward-compatible change: existing callers that only send `Status` continue to work; the MCP tool's existing payload now gets properly captured.

---

## 2. Clickable Commit and Branch Links

### Problem
Currently `TaskDetail.svelte` (line 258-268) shows git branch and commit SHA as plain text with a `GitBranch` icon. There are no links to the actual repository.

### Design
Use `Project.Repository` to construct clickable URLs. The project's repository URL is the single source of truth (already stored, max 500 chars).

### URL Construction Algorithm

```ts
function buildCommitUrl(repoUrl: string, sha: string): string {
  const normalized = repoUrl.replace(/\.git$/, '').replace(/\/$/, '');

  if (normalized.includes('github.com') || normalized.includes('gitlab.com')) {
    return `${normalized}/commit/${sha}`;
  }
  if (normalized.includes('bitbucket.org')) {
    return `${normalized}/commits/${sha}`;
  }
  // Azure DevOps: https://dev.azure.com/org/project/_git/repo
  if (normalized.includes('dev.azure.com') || normalized.includes('visualstudio.com')) {
    return `${normalized}/commit/${sha}`;
  }
  // Default: assume GitHub-style
  return `${normalized}/commit/${sha}`;
}

function buildBranchUrl(repoUrl: string, branch: string): string {
  const normalized = repoUrl.replace(/\.git$/, '').replace(/\/$/, '');

  if (normalized.includes('github.com') || normalized.includes('gitlab.com')) {
    return `${normalized}/tree/${encodeURIComponent(branch)}`;
  }
  if (normalized.includes('bitbucket.org')) {
    return `${normalized}/src/${encodeURIComponent(branch)}`;
  }
  if (normalized.includes('dev.azure.com') || normalized.includes('visualstudio.com')) {
    return `${normalized}?version=GB${encodeURIComponent(branch)}`;
  }
  return `${normalized}/tree/${encodeURIComponent(branch)}`;
}

function buildPrUrl(repoUrl: string, prNumber: number): string {
  const normalized = repoUrl.replace(/\.git$/, '').replace(/\/$/, '');

  if (normalized.includes('github.com')) {
    return `${normalized}/pull/${prNumber}`;
  }
  if (normalized.includes('gitlab.com')) {
    return `${normalized}/-/merge_requests/${prNumber}`;
  }
  if (normalized.includes('bitbucket.org')) {
    return `${normalized}/pull-requests/${prNumber}`;
  }
  return `${normalized}/pull/${prNumber}`;
}
```

Place this in a new utility file: `web/src/lib/utils/git.ts`.

### Data Flow
The repository URL lives on `Project`. The frontend needs access to it when rendering tasks. Two options:

**Option A (Recommended): Include `repository` in the board/task API responses.**
The `GET /api/tasks` endpoint already includes phase info. Add `project.Repository` to the `/api/ai/context` response (it is already in `/api/projects/:id`). For the board page, the project is loaded at the layout/dashboard level -- pass it down as a prop or store.

**Option B: Frontend fetches project on mount and stores in a Svelte store.**
Already essentially happening via the dashboard endpoint. Just ensure the `repository` field is accessible in components that render git links.

### Updated TaskDetail Git Section

```svelte
<!-- Git info -->
{#if task.gitBranch || task.gitCommitSha}
  <div class="flex items-center gap-2 text-xs text-text-tertiary">
    <GitBranch class="h-3 w-3" />
    {#if task.gitBranch}
      {#if repositoryUrl}
        <a href={buildBranchUrl(repositoryUrl, task.gitBranch)}
           target="_blank" rel="noopener"
           class="font-mono text-accent hover:underline">
          {task.gitBranch}
        </a>
      {:else}
        <span class="font-mono">{task.gitBranch}</span>
      {/if}
    {/if}
    {#if task.gitCommitSha}
      {#if repositoryUrl}
        <a href={buildCommitUrl(repositoryUrl, task.gitCommitSha)}
           target="_blank" rel="noopener"
           class="font-mono text-accent hover:underline">
          {task.gitCommitSha.slice(0, 7)}
        </a>
      {:else}
        <span class="font-mono">{task.gitCommitSha.slice(0, 7)}</span>
      {/if}
    {/if}
  </div>
{/if}
```

---

## 3. Git Fields on Phases

### Rationale
A phase represents a coherent unit of work (e.g., "Implement auth system"). Phases are often developed on a single feature branch and merged when complete.

### New Fields on `Phase` Entity

| Field | Type | Purpose |
|-------|------|---------|
| `GitBranch` | `string?` | Feature branch for this phase (e.g., `feature/auth-system`) |

A phase does **not** need a commit SHA -- that is task-level granularity. A phase represents the branch where all its tasks' commits land.

### Entity Change

```csharp
// Phase.cs - add after CompletedAt
public string? GitBranch { get; set; }
```

### Database Migration

```csharp
// New migration
migrationBuilder.AddColumn<string>(
    name: "GitBranch",
    table: "Phases",
    type: "TEXT",
    maxLength: 300,
    nullable: true);
```

### DbContext Configuration

```csharp
entity.Property(e => e.GitBranch).HasMaxLength(300);
```

### Branch Naming Convention (Suggested)

When Claude creates phases via the `/ai/phases/breakdown` endpoint, auto-suggest a branch name based on the phase name:

```
phase/{phaseNumber}-{slugified-phase-name}
```

Example: Phase 3 "Add Auth System" -> `phase/3-add-auth-system`

### API Changes

- `PhaseBreakdownRequest`: Add `string? GitBranch = null`
- `AiEndpoints.cs` phase breakdown handler: Set `phase.GitBranch = req.GitBranch`
- Phase CRUD endpoints (if they exist): Include `GitBranch` in create/update/response DTOs
- MCP `breakdown_phase` tool: Add `gitBranch` parameter

### UI: Phase Card/Detail

Show the branch as a clickable link (same `buildBranchUrl` utility):

```svelte
{#if phase.gitBranch}
  <div class="flex items-center gap-1 text-xs text-text-tertiary">
    <GitBranch class="h-3 w-3" />
    <a href={buildBranchUrl(repositoryUrl, phase.gitBranch)}
       target="_blank" rel="noopener"
       class="font-mono text-accent hover:underline">
      {phase.gitBranch}
    </a>
  </div>
{/if}
```

---

## 4. Git Fields on Milestones

### Rationale
A milestone represents a version/release. Milestones already have a `Version` field. The natural git artifact for a release is a **tag** and optionally a **release branch**.

### New Fields on `Milestone` Entity

| Field | Type | Purpose |
|-------|------|---------|
| `GitTag` | `string?` | Release tag (e.g., `v1.2.0`) |
| `GitBranch` | `string?` | Release branch (e.g., `release/1.2`) |

### Entity Change

```csharp
// Milestone.cs - add after CompletedAt
public string? GitTag { get; set; }
public string? GitBranch { get; set; }
```

### Tag URL Builder

```ts
function buildTagUrl(repoUrl: string, tag: string): string {
  const normalized = repoUrl.replace(/\.git$/, '').replace(/\/$/, '');

  if (normalized.includes('github.com')) {
    return `${normalized}/releases/tag/${encodeURIComponent(tag)}`;
  }
  if (normalized.includes('gitlab.com')) {
    return `${normalized}/-/tags/${encodeURIComponent(tag)}`;
  }
  if (normalized.includes('bitbucket.org')) {
    return `${normalized}/src/${encodeURIComponent(tag)}`;
  }
  return `${normalized}/releases/tag/${encodeURIComponent(tag)}`;
}
```

### Convention
When a milestone with `Version = "1.2.0"` is completed, suggest:
- Tag: `v1.2.0`
- Branch: `release/1.2`

### API Changes
- Milestone CRUD: Add `GitTag` and `GitBranch` to create/update request records
- MCP tools: Not urgent -- milestones are rarely completed by AI

### UI: Milestone Card

```svelte
{#if milestone.gitTag}
  <span class="flex items-center gap-1 text-xs">
    <Tag class="h-3 w-3" />
    <a href={buildTagUrl(repositoryUrl, milestone.gitTag)} ...>
      {milestone.gitTag}
    </a>
  </span>
{/if}
{#if milestone.gitBranch}
  <span class="flex items-center gap-1 text-xs">
    <GitBranch class="h-3 w-3" />
    <a href={buildBranchUrl(repositoryUrl, milestone.gitBranch)} ...>
      {milestone.gitBranch}
    </a>
  </span>
{/if}
```

---

## 5. PR Tracking on Tasks

### New Field on `LifecycleTask` Entity

| Field | Type | Purpose |
|-------|------|---------|
| `PullRequestUrl` | `string?` | Full URL to the PR/MR |

We store the full URL rather than just a number because:
- Works across hosting providers without needing to construct URLs
- Handles mono-repos where PRs might cross repo boundaries
- Simple -- no need to parse or reconstruct

### Entity Change

```csharp
// LifecycleTask.cs - add after GitBranch
public string? PullRequestUrl { get; set; }
```

### API Changes

- `UpdateTaskRequest`: Add `string? PullRequestUrl = null`
- `TaskEndpoints.cs` PATCH handler: `if (req.PullRequestUrl is not null) task.PullRequestUrl = req.PullRequestUrl;`
- `TaskTransitionRequest`: Add `string? PullRequestUrl = null`
- MCP `complete_task` tool: Add `prUrl` parameter
- List/Detail DTOs: Include `PullRequestUrl`

### MCP Tool Change

```ts
server.tool(
  'complete_task',
  'Move a task to Done status with optional commit hash',
  {
    taskId: z.number().describe('Task ID to complete'),
    commitSha: z.string().optional().describe('Git commit SHA'),
    gitBranch: z.string().optional().describe('Git branch name'),
    prUrl: z.string().optional().describe('Pull request URL'),
  },
  async ({ taskId, commitSha, gitBranch, prUrl }) => {
    const result = await api.post(`/ai/tasks/${taskId}/transition`, {
      status: 'Done',
      gitCommitSha: commitSha,
      gitBranch,
      pullRequestUrl: prUrl,
    });
    return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
  }
);
```

### TypeScript Type

```ts
// types/index.ts - Task interface
pullRequestUrl?: string;
```

### UI: TaskDetail

Add below the git branch/commit section:

```svelte
{#if task.pullRequestUrl}
  <div class="flex items-center gap-2 text-xs text-text-tertiary">
    <GitPullRequest class="h-3 w-3" />
    <a href={task.pullRequestUrl}
       target="_blank" rel="noopener"
       class="font-mono text-accent hover:underline">
      {extractPrLabel(task.pullRequestUrl)}
    </a>
  </div>
{/if}
```

Where `extractPrLabel` pulls out a human-readable label:

```ts
function extractPrLabel(url: string): string {
  // "https://github.com/org/repo/pull/42" -> "PR #42"
  const match = url.match(/\/pull\/(\d+)/);
  if (match) return `PR #${match[1]}`;

  const mrMatch = url.match(/\/merge_requests\/(\d+)/);
  if (mrMatch) return `MR !${mrMatch[1]}`;

  const bbMatch = url.match(/\/pull-requests\/(\d+)/);
  if (bbMatch) return `PR #${bbMatch[1]}`;

  return 'PR';
}
```

---

## 6. Git Info on Board Cards

### Current State
`BoardCard.svelte` shows: priority badge, AI badge, ID, title, labels, tests, phase name, due date. No git information.

### Design Principle
Board cards must stay compact. Only show git badges when they provide quick-scan value.

### Proposed: Conditional Git Badges

Show at most two small badges in the bottom row (alongside tests and phase name):

1. **Branch badge** -- only if a branch is set (indicates active development)
2. **PR badge** -- only if a PR URL is set (indicates code is in review)

```svelte
<!-- Bottom row additions in BoardCard.svelte -->
{#if task.gitBranch}
  <span class="flex items-center gap-0.5 text-[10px] font-mono truncate max-w-[100px]"
        title={task.gitBranch}>
    <GitBranch class="h-2.5 w-2.5 shrink-0" />
    {task.gitBranch.split('/').pop()}
  </span>
{/if}

{#if task.pullRequestUrl}
  <a href={task.pullRequestUrl} target="_blank" rel="noopener"
     class="flex items-center gap-0.5 text-[10px] text-accent hover:underline"
     title="Pull Request"
     onclick={(e) => e.stopPropagation()}>
    <GitPullRequest class="h-2.5 w-2.5 shrink-0" />
    {extractPrLabel(task.pullRequestUrl)}
  </a>
{/if}
```

**What NOT to show on cards:**
- Commit SHA -- too granular for a board overview
- Commit count -- requires additional API work and is low-value at a glance
- Full branch name -- truncate to last segment only (e.g., `feature/auth-system` shows as `auth-system`)

---

## 7. Implementation Summary

### Database Migrations Required

```
Phase:     + GitBranch (string?, max 300)
Milestone: + GitTag (string?, max 200), + GitBranch (string?, max 300)
Task:      + PullRequestUrl (string?, max 1000)
```

### Files to Modify

| File | Changes |
|------|---------|
| `api/Data/Entities/Phase.cs` | Add `GitBranch` |
| `api/Data/Entities/Milestone.cs` | Add `GitTag`, `GitBranch` |
| `api/Data/Entities/LifecycleTask.cs` | Add `PullRequestUrl` |
| `api/Data/LifecycleDbContext.cs` | Configure new field lengths |
| `api/Api/AiEndpoints.cs` | Fix `TaskTransitionRequest`, add git fields to phase breakdown |
| `api/Api/TaskEndpoints.cs` | Add `PullRequestUrl` to update/DTOs |
| `api/Api/PhaseEndpoints.cs` | Add `GitBranch` to CRUD (if exists) |
| `api/Api/MilestoneEndpoints.cs` | Add `GitTag`, `GitBranch` to CRUD (if exists) |
| `mcp/src/tools/task-tools.ts` | Add `prUrl` to `complete_task` |
| `web/src/lib/types/index.ts` | Add new fields to `Task`, `Phase`, `Milestone` |
| `web/src/lib/utils/git.ts` | **New file**: URL builders |
| `web/src/lib/components/tasks/TaskDetail.svelte` | Clickable git links + PR display |
| `web/src/lib/components/board/BoardCard.svelte` | Branch + PR badges |

### Implementation Priority

1. **P1: Fix the AI endpoint bug** -- data is being lost right now
2. **P1: Add `PullRequestUrl` to tasks** -- most impactful new feature
3. **P2: Clickable links utility + TaskDetail update** -- immediate UX improvement
4. **P2: Board card git badges** -- visibility improvement
5. **P3: Phase `GitBranch`** -- useful but not urgent
6. **P3: Milestone `GitTag` + `GitBranch`** -- useful for releases
