# Rental Command

Residential property-management platform for small landlords and property managers.
Built to mirror the EdiPlatform architecture: a .NET 10 multi-project backend on
PostgreSQL, a SvelteKit web app, and (planned) a Flutter mobile app over the same API.

The product spine is **"the computer does the typing for you"** — capture a document
(photo, scanned PDF, email, or a spoken sentence), have an LLM extract the fields with
per-field confidence, present a **draft** the user confirms, and only then create the
record. See the master spec under `Docs/superpowers/specs/` for the full vision.

## What's here today (Phase 0 — re-platform)

- Portfolio dashboard (occupancy, leasing, accounting, maintenance, activity KPIs)
- Property + unit inventory; owners / owner-entities (Person/LLC/Trust); vendors
- Tenant + lease lifecycle; payment ledger; expense tracking; cash-basis accounting summary
- Work orders, appointments, inspections
- Tenant/owner portal endpoints
- ASP.NET Identity + JWT (15-min access) + rotated refresh tokens; API-key auth for webhooks
- Real-time updates via SignalR backed by a DB outbox dispatched by the Engine

The flagship scan→draft→confirm intake and the AI "brains" land in later phases
(see `Docs/superpowers/plans/`).

## Architecture

| Project | Role |
|---------|------|
| `RentalCommand.Core` | Entities, enums, interfaces, configuration (no infra deps) |
| `RentalCommand.Data` | `RentalCommandDbContext` (EF Core + Npgsql) + migrations |
| `RentalCommand.Api`  | ASP.NET controllers under `/api/v1/*`, auth, SignalR hub |
| `RentalCommand.Engine` | Background workers (outbox dispatch); no HTTP port |
| `RentalCommand.TestCommon` + `*.Tests` / `IntegrationTests` | Test scaffolding + suites |
| `web/` | SvelteKit 5 (runes) + TanStack Query + Tailwind + SignalR client |
| `mcp/` | Legacy Lifecycle MCP server — retained, to be repurposed in Phase 3 for Portfolio Q&A |
| `Docs/` | Specs, phase plans, and competitive research |

Messaging is intentionally light: a DB-backed outbox + Engine workers + SignalR — **no RabbitMQ**.

## Quick start (local dev)

Prereqs: **.NET 10 SDK**, **Node 20+ with pnpm**, **Docker**, and **mkcert**
(`brew install mkcert && mkcert -install`) for locally-trusted HTTPS.

```bash
./scripts/start-dev.sh
```

This generates mkcert certs, reuses any Postgres already listening on `:5432`
(otherwise starts one), launches the Engine + API, and runs the web dev server.

| Service | URL |
|---------|-----|
| Web     | https://localhost:5667 |
| API     | https://localhost:5666 (http: http://localhost:5665) |
| Postgres | localhost:5432 / db `rentalcommand` |

The DB connection string is read from **.NET User Secrets** in Development (not committed):

```bash
dotnet user-secrets set "ConnectionStrings:DefaultConnection" \
  "Host=localhost;Port=5432;Database=rentalcommand;Username=postgres;Password=..." \
  --project RentalCommand.Api
```

On first API start the app seeds roles + a dev admin: **`admin@rentalcommand.local` / `Admin123!`**.
(The login page has a dev-only "Fill dev login" button in `DEV` builds.)

## Manual setup

```bash
# Backend
dotnet build RentalCommand.sln
dotnet run --project RentalCommand.Engine          # background workers
dotnet run --project RentalCommand.Api -- --urls "https://localhost:5666;http://localhost:5665"

# Web
cd web && pnpm install && pnpm dev
```

EF migrations live in `RentalCommand.Data/Migrations`; apply with
`dotnet ef database update --project RentalCommand.Data --startup-project RentalCommand.Api`.

## Production (docker-compose)

`docker-compose.yml` brings up Postgres + API + Engine + Web behind **Traefik**
(see `traefik/dynamic.yml`). Configure secrets via environment — see `.env.example`
(`JWT_SECRET_KEY`, `POSTGRES_*`, `ANTHROPIC_API_KEY`, `WEB_ORIGIN`, etc.).

## Tests

```bash
dotnet test RentalCommand.sln          # unit + integration
cd web && pnpm exec svelte-check       # web typecheck
```

## Docs

- `Docs/superpowers/specs/` — master vision + phased spec
- `Docs/superpowers/plans/` — per-phase implementation plans (0, 1+2, 3, 4)
- `Docs/Research/` — competitive gap analysis
