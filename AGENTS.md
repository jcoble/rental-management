# Rental Command — Agent Instructions

Full project context, architecture, auth model, and conventions are in
[`CLAUDE.md`](./CLAUDE.md). Read it. The rules below are the ones agents most
often get wrong, repeated here so they are not missed.

## Transaction atomicity — HARD RULE (all apps, no matter what)

Every user action that writes an aggregate or workflow must be all-or-nothing inside **one explicit database transaction**. Multiple `SaveChanges` / `SaveChangesAsync` calls are allowed when generated IDs or ordered writes require them, but every one of those calls must participate in the same transaction. Never split one user action across independently committed HTTP requests or saves.

- Composite workflows (for example tenant + lease + memberships + occupancy + deposit + opening balance + initial payments) belong in one server-side transaction.
- On any failure, the transaction must roll back every row written by that action; no half-created or "half-cooked" state is acceptable.
- Realtime broadcasts, emails, provider calls, and other external side effects run only after commit. Use the outbox for side effects that must be guaranteed or retried.
- Validating before save is not enough for concurrency safety; retain database constraints and use appropriate isolation or locking where races are possible.

## Worktrees (read before creating any worktree)

Create git worktrees under a **single shared root**, one subfolder per repo, one
worktree per task:

```bash
git worktree add ~/dev/work/worktrees/rental-management/<task-slug> -b <branch>
```

**Never** create worktrees:
- inside the repo (`./worktrees/`, `.claude/`, `.claire/`), or
- under `~/.codex/worktrees/`.

Scattered/in-repo worktrees get indexed by the IDE and Spotlight, bloat the
checkout, and get lost track of.

**Clean up the moment you're done.** As soon as the branch is merged (or the task
is abandoned), remove the worktree — do not leave clean/finished worktrees behind:

```bash
git worktree remove ~/dev/work/worktrees/rental-management/<task-slug>
git worktree prune
```

The default after finishing is **remove it now.** Keep a worktree only if it has
uncommitted or unmerged work that must survive — and if so, state its path, branch,
and dirty status explicitly rather than leaving it silently.

## Commit conventions

Do **NOT** add a `Co-Authored-By: Claude …` trailer (or any AI-attribution line) to
commit messages. Keep messages to a clear subject + body only.
