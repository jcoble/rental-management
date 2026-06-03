# Rental Command — Agent Instructions

Full project context, architecture, auth model, and conventions are in
[`CLAUDE.md`](./CLAUDE.md). Read it. The rules below are the ones agents most
often get wrong, repeated here so they are not missed.

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
