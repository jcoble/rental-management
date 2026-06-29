# Fixer Procedure — Rental Command E2E loop

A fixer is dispatched to resolve ONE triaged bug inside a pre-created, isolated git worktree.
The orchestrator owns worktree creation, the (serial) merge to main, and worktree removal — so a
fixer never merges and never deletes the worktree. This keeps the live main checkout safe and
guarantees cleanup even if a fixer fails.

## Division of responsibility
**Orchestrator (before dispatch):**
- Create the worktree + branch off main:
  `git worktree add ~/dev/work/worktrees/rental-management/<slug> -b <branch> main`
  Branch name: `e2e-fix-<slug>` (or `tsk-<NN>-<slug>` if the bug maps to a Notion task).
- Dispatch the fixer scoped to that worktree path with the full bug details.

**Fixer (in the worktree only):**
1. `cd` into the given worktree path. Confirm you are NOT in the main checkout.
2. Read the bug's Code Reference and surrounding code; confirm the root cause before changing
   anything (don't pattern-match a fix — understand it).
3. Apply the minimal correct fix. Add `data-testid` to any new/changed interactive UI element.
   Respect the project SQL HARD RULE: any aggregation/grouping/filter/paging stays DB-side (EF →
   one SQL statement, or a view) — never materialize-then-sum in C#.
4. Verify in the worktree (do NOT touch the running dev app):
   - Frontend changed → `cd web && pnpm exec svelte-check --threshold error` (or `npx svelte-check`).
   - Backend changed → `dotnet build <changed .csproj or the .sln>`; if you changed logic, add/adjust
     a focused test and run it with `dotnet test --filter <Name>` (don't run the whole suite).
   - Set `MSBUILDDISABLENODEREUSE=1` for dotnet to avoid orphan MSBuild workers.
5. Self-review your diff (`git -C <worktree> diff`) against the bug: does it fix the reported
   behavior, with no scope creep and no regressions? For a risky/multi-file change, say so in your
   return so the orchestrator can add a review pass.
6. Commit on the branch, in the worktree, with a clear subject + body. **No `Co-Authored-By` / AI
   attribution trailer** (project rule). Do NOT merge. Do NOT remove the worktree.
7. Return a compact summary: bug id, files changed, what the fix does, verification command output
   (build/test pass/fail), commit hash, and whether you flagged it as risky.

**Orchestrator (after the fixer returns, SERIALLY — one bug at a time):**
1. If build/test failed → re-dispatch or send the fixer back; do not merge a red build.
2. Merge the branch into the live main checkout:
   `git -C /Users/blackcolours/dev/work/rental-management merge --no-ff <branch> -m "<msg>"`
   (Merges are serialized — never merge two fixer branches concurrently.)
3. **Remove the worktree immediately** and prune:
   `git worktree remove ~/dev/work/worktrees/rental-management/<slug> && git worktree prune`
   Report cleanup status (removed / not-removed + reason).
4. Make the running app reflect the fix:
   - Frontend-only → Vite HMR picks it up automatically (the main checkout's tree just changed).
   - Backend → rebuild + restart the API process (it runs `dotnet run --no-build`), e.g. kill the
     RentalCommand.Api PID on :5666, `dotnet build RentalCommand.Api`, relaunch it, wait healthy.
5. Dispatch a verification (a tester re-running just the bug's reproduction steps against the now-
   updated running app). Verification dispatches do not count against the exploration budget.

## Merge-to-main is deploy-safe
Plain merges/pushes to `main` do NOT deploy (deploys are tag-driven: `git tag vX.Y.Z && git push
--tags`, or the manual Actions ▸ Deploy). The loop never tags and never pushes unless asked.
