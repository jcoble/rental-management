# Rental Command

Full-stack rental management web app for small property managers.
Built from the Lifecycle template architecture (SvelteKit + .NET + SQLite + MCP), repurposed around leasing, maintenance, accounting, scheduling, and AI operations.

## What this app includes

- Portfolio dashboard with occupancy, lease, accounting, maintenance, and activity KPIs
- Property + unit inventory management
- Tenant and lease lifecycle management
- Payment ledger (charges + mark-paid workflow)
- Expense tracking with vendor and property linkage
- Work-order operations and status transitions
- Appointment scheduling (showings, move-ins, inspections, maintenance visits)
- Inspection scheduling and status tracking
- Owner and vendor management (including 1099/W-9 metadata)
- AI endpoints for intake triage, risk summary, and notice generation
- MCP server exposing rental workflows as tools for AI agents
- SSE real-time invalidation events

## Architecture

- `api/`: .NET 10 Minimal API + EF Core + SQLite
- `web/`: SvelteKit 5 frontend + TanStack Query + Tailwind
- `mcp/`: MCP server with rental-management tools
- `Docs/Research/rental-management-market-research.md`: market/feature research and sources

## Quick start (local)

Prereqs:

- .NET 10 SDK
- Node.js 20+ with pnpm

Run:

```bash
./start.sh
```

- Web: `http://localhost:5667`
- API: `http://localhost:5666`

`start.sh` auto-seeds demo data on first run.

## Manual setup

### API

```bash
cd api
dotnet restore
dotnet ef database update
dotnet run
```

### Web

```bash
cd web
pnpm install
pnpm dev
```

### Seed demo data

```bash
./seed.sh
```

## MCP setup

Build MCP:

```bash
cd mcp
npm install
npm run build
```

Register MCP server (example):

```bash
claude mcp add rental-command \
  -s user \
  -e RENTAL_API_URL=http://localhost:5666 \
  -e RENTAL_API_KEY=your-api-key \
  -e RENTAL_PORTFOLIO_ID=1 \
  -- node /path/to/rental-management/mcp/build/index.js
```

## Environment variables

### API

- `ApiKey` in `api/appsettings.Local.json` (optional)

### MCP

- `RENTAL_API_URL` (fallback: `LIFECYCLE_API_URL`)
- `RENTAL_API_KEY` (fallback: `LIFECYCLE_API_KEY`)
- `RENTAL_PORTFOLIO_ID` (fallback: `LIFECYCLE_PROJECT_ID`)

## Research basis

See `Docs/Research/rental-management-market-research.md` for competitor and feature research that informed this implementation.
