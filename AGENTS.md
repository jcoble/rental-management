[Notion Command Center](https://app.notion.com/p/376394b0689d810cb51dcebc28ceaa5b) is the source of truth for Rental Command tasks, decisions and all documentation: plans, specs, recon, handoffs and artifact records. Keep task material on its task and reusable docs under the [Rental Command project](https://app.notion.com/p/376394b0689d81d5a191fb89c98a4fe4); proof assets live on Google Drive, linked from Notion and PRs.

# Rental Command instructions

These project rules supplement the global instructions in `~/.codex/AGENTS.md`.

## Product and architecture

Residential property management for nontechnical landlords, primarily using a phone. Keep the surface simple. Document intake is capture/scan, extraction with confidence, owner confirmation of a draft, then record creation; AI output never approves itself.

The stack is .NET 10, EF Core/Npgsql and PostgreSQL, a SvelteKit 5 web app and Flutter mobile client sharing the API. Core holds domain contracts; Data owns persistence; Api exposes authenticated commands and queries; Engine runs outbox/background work. Use the database outbox and SignalR for reliable side effects and pushed updates.

## Project requirements

- Enforce portfolio authorization on the server, including every incoming foreign-key reference. Delete unsafe overloads when a scope-authorized replacement lands.
- A logical business action is all-or-nothing in one explicit transaction. Multiple `SaveChangesAsync` calls must share it, including required audit, ledger, inbox and outbox rows. Keep database constraints and appropriate isolation; prove rollback when changing a write boundary.
- Never hold a database transaction across AI, payment, storage, e-sign, email or other remote calls. Commit an idempotent intent/outbox, perform the external work, then commit its result atomically. Publish notifications after commit.
- Preserve ASP.NET Identity's integer keys, JWT and rotated refresh tokens. Keep app-namespaced `rc_*` httpOnly cookies, same-origin refresh and single-flight refresh handling; token reuse revokes its family.
- Enforce database runtime roles separately from migrations. Keep secrets out of code and output; never disable TLS verification.
- Web API calls use `web/src/lib/api/client.ts`. Share formatters in `web/src/lib/utils` and `mobile/lib/core/presentation`; do not duplicate them in screens.
- Preserve the dual TypeScript setup: `@typescript/native` supplies the native compiler; `typescript` aliases `@typescript/typescript6` for Svelte compatibility. For web TypeScript changes, run both `pnpm --dir web check:native` and `pnpm --dir web check`.
- Use existing components/theme tokens. Required services stay required; do not add optional-for-tests dependencies or configuration with no real choice/reader.

## Locations and verification

| | Mac | Workbox |
| --- | --- | --- |
| Repo | `/Users/blackcolours/dev/work/rental-management` | `/home/blackcolours/dev/work/rental-management` |
| Worktrees | `/Users/blackcolours/dev/work/worktrees/rental-management/<task>` | `/home/blackcolours/dev/work/worktrees/rental-management/<task>` |

The Mac preview uses `scripts/local/preview-stack-local.sh` and `http://127.0.0.1:15667`. Its database, uploads, credentials, keys and Docker volumes are persistent owner data; do not reset/delete them without explicit permission. Give each verification lane isolated data and ports.

Follow global independent verification and Drive evidence rules. Exercise affected web and phone flows against the real API, with realistic data; use focused behavior checks and transaction failure proof where relevant. Compilation or a source inspection alone is not completion.

Production images build in CI, never on the production VPS. Do not deploy without an explicit request.
