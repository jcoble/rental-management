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
- Prefer building the complete tracked object graph and saving once. If multiple saves are structurally required, use the EF execution strategy with an explicit transaction and commit only after the final save succeeds.
- Never keep a database transaction open across an AI, storage, payment, e-sign, email, SMS, or other remote call. Persist an idempotent intent/inbox/outbox atomically, perform the remote work, then persist its result as a new explicit atomic command.
- Required audit, ledger, inbox, and outbox rows fail and roll back with the business mutation. Do not preserve them independently after the business transaction fails.
- Every intentionally separate commit requires a unique idempotency/business key plus a documented recovery and reconciliation path.
- When changing a write path, inspect every `SaveChanges`, transaction, audit, ledger, inbox, outbox, signature, and document boundary in that operation and add failure-injection proof for all-or-nothing behavior.

## TypeScript 7 native compiler and Svelte compatibility

The web app intentionally installs the Go-based TypeScript 7 compiler side-by-side with the TypeScript 6 compatibility package:

- `@typescript/native` aliases TypeScript 7 and provides the native `tsc` binary used by `pnpm --dir web check:native`.
- `typescript` aliases `@typescript/typescript6` because SvelteKit and `svelte-check` still require the JavaScript compiler API that TypeScript 7.0 does not expose.
- Never replace the `typescript` compatibility alias with TypeScript 7 until Svelte officially supports the new API; doing so makes `svelte-check` crash before diagnostics run.
- The native check supplements rather than replaces `pnpm --dir web check`: native `tsc` checks the generated TypeScript/JavaScript graph, while `svelte-check` also validates `.svelte` templates.
- For web TypeScript changes, run both `pnpm --dir web check:native` and `pnpm --dir web check`.

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

## Keep it simple — rules from the 2026-08-25 structure audit

Four independent audits (`Docs/Reviews/2026-08-25-structure-audit/`) found the same recurring
shapes of waste. These rules stop them coming back.

- **Finish a migration in the PR that finishes it.** When a vocabulary or pattern changes, rename
  the files, delete the compatibility shims, throw-only "retired path" methods and migration
  markers in the same change. Thirty-five `*Handler.cs` files full of `*Rule` classes and 52
  `throw RetiredPath()` methods came from not doing this.
- **No enum, attribute, flag or config property with one legal value or zero readers.** If there is
  one value it is not a choice; if nothing reads it, it is not configuration. `rg` the name before
  adding it and before leaving it behind.
- **No `IFoo? foo = null` constructor parameters for services that are always registered.** Make
  the dependency required; tests pass a mock. Optional-for-tests dependencies produced 61
  `RequireFoo()` guards.
- **Never keep two overloads of a read that differ in authorization.** When a scope-authorized
  overload lands, the unauthorized one is deleted in the same change — otherwise the next caller
  can pick the unsafe one.
- **One formatter per concern per app**, in `web/src/lib/utils` / `mobile/lib/core/presentation`.
  Never define `_formatCurrency`, `_formatDate`, a month-name table or an `Intl.NumberFormat`
  inside a screen, route or component. Thirty-two web and twenty-nine mobile copies disagreed
  about cents and sign placement.
- **No source-text assertion tests.** Do not `readFileSync`/`readAsStringSync` a source file and
  regex it. Assert on rendered output, returned values or captured requests. 120 of 226 web test
  files and 40 of 109 mobile test files did this, and every legitimate cleanup broke dozens of
  them for reasons unrelated to behaviour. Existing ones are migrated when touched, not preserved.
- **Node's test runner needs explicit `.ts` extensions** on relative imports in any module a
  `*.test.ts` file imports directly (`import x from '../list-params.ts'`). Vite does not care;
  `node --test` does.
