# Rental Command

Residential property-management platform. Primary user is a **non-technical landlord**
(owns ~15–40 units, runs the business from a phone + paper), then co-owners/employees,
then a potential SaaS for other small landlords. Design rule: **simple on the surface,
full management system underneath.** The flagship is *"the computer does the typing for
you"* — scan/capture a document → LLM extracts fields with confidence → user confirms a
**draft** → record is created.

Architecture mirrors the sister project **EdiPlatform** (`/Users/blackcolours/dev/work/EdiPlatform`):
.NET 10 multi-project on PostgreSQL, ASP.NET Identity + JWT/rotated-refresh auth, light
messaging (DB outbox + Engine workers + SignalR — **no RabbitMQ**), SvelteKit web,
and a planned **Flutter** mobile app over the same API.

## Where work is tracked (Notion Command Center)

This project's tasks, status, and "what's next" live in the user's Notion **Command Center**
(project: **Rental Command**) — the source of truth, richer than git history, with the *why*
up front in each task body. `TODO.md` is now archival.

- At the start of substantive work, or whenever the user asks "what's next / where are we /
  what should I work on", **FIRST list the open tasks** and ground your answer in them:
  `~/.claude/skills/capture-task/list-tasks.sh "Rental Command"`.
- New ideas / specs / rapid-fire items → the `capture-task` skill records them automatically.
- Branch / PR / issue activity syncs to these tasks via `.github/workflows/notion-tasks.yml`.

## Project layout

| Project | Role |
|---------|------|
| `RentalCommand.Core` | Entities, enums, interfaces, config — no infra dependencies |
| `RentalCommand.Data` | `RentalCommandDbContext` (EF Core + Npgsql) + `Migrations/` |
| `RentalCommand.Api` | Controllers under `/api/v1/*`, auth, SignalR hub at `/api/v1/hubs/updates` |
| `RentalCommand.Engine` | Background workers (`OutboxDispatchWorker`); no HTTP port |
| `RentalCommand.*Tests`, `IntegrationTests`, `TestCommon` | Test suites + scaffolding |
| `web/` | SvelteKit 5 (runes) + TanStack Query + Tailwind + SignalR client |
| `mcp/` | **Legacy Lifecycle MCP server.** Kept as-is; to be *repurposed* in Phase 3 for the Portfolio Q&A moat. Do not treat its current tools as part of Rental Command. |
| `Docs/` | `superpowers/specs/` (vision), `superpowers/plans/` (per-phase), `Research/` |

## Local development

```bash
./scripts/start-dev.sh
```

Generates mkcert certs, reuses any Postgres on `:5432` (else starts one), then runs
Engine + API + web. Requires `dotnet`, `pnpm`, `docker`, and `mkcert` (`mkcert -install`).

| Service | URL |
|---------|-----|
| Web | https://localhost:5667 |
| API | https://localhost:5666 (http: http://localhost:5665) |
| DB  | localhost:5432 / db `rentalcommand` |

- **DB connection** comes from **.NET User Secrets** in Development (not committed). Do
  NOT export `ConnectionStrings__DefaultConnection` in the dev script — it would override
  the secret. Set it via `dotnet user-secrets set "ConnectionStrings:DefaultConnection" "<conn>" --project RentalCommand.Api`.
  `DefaultConnection` supplies the credential, but steady-state API/Engine connections immediately
  assume the NOLOGIN `rentalcommand_api` / `rentalcommand_engine` roles. Startup migrations use
  `ConnectionStrings:MigratorConnection` without a runtime-role interceptor; it defaults to
  `DefaultConnection` for local development. In deployed environments set both explicitly to the
  owner/migrator credential. Never put a runtime-role password in configuration—the roles are NOLOGIN.
- **HTTPS** is real mkcert TLS for both web and API; Node trusts the API cert via
  `NODE_EXTRA_CA_CERTS` (set by the dev script). **Never disable TLS verification**
  (`NODE_TLS_REJECT_UNAUTHORIZED=0`) — use the mkcert CA instead.
- Seeded dev admin on first API start: **`admin@rentalcommand.local` / `Admin123!`**.
  The login page shows a dev-only "Fill dev login" button in `DEV` builds.

Logs: `/tmp/rentalcommand-api.log`, `/tmp/rentalcommand-engine.log`.

## Production & deployment (CI/CD)

Live at **https://rentalcommand.net** on a small Hetzner box (`/opt/rental-command`,
`deploy/docker-compose.prod.yml`: Traefik + Postgres + API + Engine + Web). Secrets live in
`/opt/rental-command/.env` on the box (never committed).

- **NEVER build images on the VPS.** It's a 2-vCPU / 3.7 GB box — compiling there starves the
  live app. The prod compose has **no `build:` blocks**; the box only `docker compose pull`s
  GHCR images. Building belongs in CI.
- **Deploys are CI-driven** via `.github/workflows/deploy.yml`: GitHub-hosted Actions runners build
  `api`/`engine`/`web`, pushes them to `ghcr.io/jcoble/rentalcommand-*`, then SSHes in to pull +
  `up -d`. The web image bakes **`VITE_API_URL=/api/v1`** (the API mounts under `/api/v1/*`; the
  client appends bare paths — a bare `/api` 404s every browser call).
- **Triggering a deploy** (ordinary branch pushes and merges do not run this workflow):
  - Push a version tag → deploys that exact commit: `git tag vX.Y.Z && git push origin vX.Y.Z`
  - Or **Actions ▸ Deploy ▸ Run workflow** (optionally paste a SHA/ref).
- Deploy needs repo secrets `DEPLOY_SSH_KEY` (dedicated `rc-deploy` key, **not** the personal
  multi-machine key), `DEPLOY_HOST`, `DEPLOY_USER`, `DEPLOY_KNOWN_HOSTS`. Migrations self-apply
  on boot under a shared Postgres advisory lock. Hetzner **blocks outbound SMTP 25/465** — use
  Google Workspace SMTP relay or another provider on **587/STARTTLS**. Full box/SSH details are in agent memory
  (`rental-command-deployment`).

## Auth model

- ASP.NET Identity with **int keys** (`ApplicationUser : IdentityUser<int>`), matching the
  int-keyed domain entities. JWT access tokens (~15 min) + single-use **rotated** refresh
  tokens. `ApiKeyAuthenticationHandler` covers webhook/server callers.
- The web app stores tokens in **app-namespaced httpOnly cookies first-party to the SvelteKit origin**.
  Current names are `rc_access_token`, `rc_access_token_expiration`, and `rc_refresh_token`;
  avoid generic names like `access_token`/`refresh_token` because localhost cookies are shared
  across ports and can collide with sister apps/worktrees.
  `web/src/hooks.server.ts` validates `rc_access_token` via `GET /auth/me` (refreshing on 401)
  and populates `locals.user` for SSR guards. Route groups `(protected)`/`(admin)`/`(portal)`
  guard via `+layout.server.ts`.
- Client refresh goes through the **same-origin proxy** `POST /api/auth/refresh`
  (`web/src/routes/api/auth/refresh/+server.ts`); the Vite proxy `bypass` in
  `web/vite.config.ts` keeps that path on SvelteKit instead of forwarding it to the API.
  `clearAuth()` navigates to `/logout` (which clears cookies) — never straight to `/login`.
- Refresh-token **reuse revokes the whole token family** (server-side). The shared
  single-flight + brief cache in `web/src/lib/server/token-refresh.ts` dedupes concurrent
  refreshes so a normal session never double-uses a token.

## API surface

All under `/api/v1`. Controllers: Auth, Portfolio (incl. `/{id}/dashboard`), Property, Unit,
OwnerEntity, Vendor, Tenant, Lease, Payment, Expense, Accounting (`/summary`), WorkOrder,
Appointment, Inspection, Activity (`/activities`), Portal. Most inherit
`AuthenticatedPortfolioControllerBase` and are **portfolio-scoped** — inbound FK references
are validated to be in-portfolio (cross-tenant IDOR guard). SignalR hub: `/api/v1/hubs/updates`.

## Worktrees

Create git worktrees under a **single shared root**, one subfolder per repo, one worktree
per task:

```bash
git worktree add ~/dev/work/worktrees/rental-management/<task-slug> -b <branch>
```

**Never** create worktrees inside the repo (`./worktrees/`, `.claude/`, `.claire/`) or under
`~/.codex/worktrees/`. Scattered/in-repo worktrees get indexed by the IDE and Spotlight, bloat
the checkout, and get lost track of.

**Clean up the moment you're done.** As soon as a worktree's branch is merged (or the task is
abandoned), remove it — do not leave clean/finished worktrees lying around:

```bash
git worktree remove ~/dev/work/worktrees/rental-management/<task-slug>
git worktree prune
```

Stale worktrees pile up fast, waste disk, and load the machine (Spotlight/fseventsd churn).
The default after finishing is **remove it now.** Only keep one if it has uncommitted or
unmerged work that must survive — and if so, say so explicitly with its path, branch, and
dirty status rather than leaving it silently.

## Conventions & known gotchas

- **PostgreSQL only** (Npgsql). No SQLite, no SQL Server. There is one baseline migration;
  add migrations with `dotnet ef migrations add <Name> --project RentalCommand.Data --startup-project RentalCommand.Api`.
- Web client API calls go through `web/src/lib/api/client.ts` (`fetchApi`/`api`), which
  attaches the bearer token, refreshes-and-retries once on 401, and throws `ApiError`.
- Enums serialize as **string names** app-wide: `JsonStringEnumConverter` is registered on
  both the controllers' JSON options and the SignalR protocol, matching the string enum
  values the web client sends/receives. Keep new enums working as strings.
- Testing posture (per project direction): a few UI/E2E tests now (~3–5), defer broad
  regression/unit suites until the system stabilizes. Run heavy spec/code review **per
  phase**, not per task.

## Commit conventions

- Do **NOT** add a `Co-Authored-By: Claude …` trailer (or any AI-attribution line) to commit
  messages. Keep messages to a clear subject + body only.

## Phased roadmap

Phase 0 (this re-platform) → Phase 1+2 (upload + scan→draft→confirm) → Phase 3 (real AI:
Daily Briefing + Portfolio Q&A) and Phase 4 (automation/notifications). Plans live in
`Docs/superpowers/plans/2026-05-30-phase-*.md`; the master spec is in `Docs/superpowers/specs/`.
