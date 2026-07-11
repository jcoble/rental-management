# Deploying Rental Command to Hetzner (rentalcommand.net)

Production deploy of the full stack (Traefik + Postgres + API + Engine + Web) onto a
single fresh Ubuntu box, behind Let's Encrypt TLS, with **same-origin** routing
(`https://rentalcommand.net/api/*` → API, everything else → web).

The files this uses:

- `deploy/docker-compose.prod.yml` — the production stack.
- `deploy/.env.prod.example` — every variable, with notes on required vs optional.
- `.github/workflows/deploy.yml` — explicit version-tag/manual deployment from a
  GitHub-hosted runner. Ordinary branch pushes and merges never deploy.

Target box: **49.13.236.209** (Hetzner, Ubuntu 24.04).

---

## 1. DNS

Point an **A record** at the box, then wait for it to resolve before deploying
(Let's Encrypt validates over the public DNS + port 443):

```
A       rentalcommand.net       →   49.13.236.209
CNAME   www.rentalcommand.net   →   rentalcommand.net
```

Verify from your laptop:

```bash
dig +short rentalcommand.net   # should print 49.13.236.209
```

No `api.` subdomain — Rental Command is single-host on purpose (its auth uses
httpOnly cookies first-party to the web origin plus a same-origin `/api/auth/refresh`
proxy, so the API must live under the same host as the web app). `www` should redirect
to the apex host so sessions and app links stay canonical.

## 2. Firewall (Hetzner Cloud)

Open the only two ports the box needs from the internet:

- **80/tcp**  — HTTP (redirects to HTTPS; also used for the ACME challenge fallback)
- **443/tcp** — HTTPS (app traffic + the ACME TLS-ALPN-01 challenge)

Plus **22/tcp** for your own SSH. In the Hetzner console: *Firewalls → create → inbound
rules* for 22, 80, 443, attach it to the server. Postgres is **not** exposed — it lives
on an internal Docker network only.

## 3. Install Docker + the compose plugin (fresh Ubuntu 24.04)

SSH in as root and run:

```bash
ssh root@49.13.236.209

apt-get update && apt-get install -y ca-certificates curl git
install -m 0755 -d /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
chmod a+r /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] \
  https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo $VERSION_CODENAME) stable" \
  > /etc/apt/sources.list.d/docker.list
apt-get update
apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin

docker --version && docker compose version   # confirm both work
```

## 4. Create the deployment directory

```bash
mkdir -p /opt/rental-command/deploy
cd /opt/rental-command
```

The production box does not clone or compile the repository. The deploy workflow builds
the API, Engine, and Web images on a GitHub-hosted runner, pushes them to GHCR, and copies
the production compose file into this directory before pulling the selected images.

## 5. Configure the environment

```bash
nano .env
```

Use `deploy/.env.prod.example` from the repository as the field reference. The `.env`
file itself exists only on the production box and is never copied by the deploy workflow.

Fill in **at minimum** the required values:

- `ACME_EMAIL` — your email (Let's Encrypt expiry notices).
- `POSTGRES_PASSWORD` — generate: `openssl rand -base64 32`
- `JWT_SECRET_KEY` — generate: `openssl rand -base64 48`
  (the API refuses to start if this is missing, under 32 chars, or the dev placeholder).
- `DOMAIN` / `APP_WEB_BASE_URL` — already set to `rentalcommand.net`; leave them.

Then the integrations you actually want on day one (all optional, all gated off until set):

- **AI scanning** — `ASSISTANT_API_KEY` (OpenAI key, with `ASSISTANT_MODEL_ID=gpt-4o`).
  This powers the flagship scan→draft feature; without it the app runs but can't scan.
- **Email** — either SendGrid (`SENDGRID_*`) or Google Workspace/Gmail SMTP (see §8).
- **SMS** — `SIGNALWIRE_*` (preferred) for tenant texts. Only fires when `NOTIFY_TENANTS=true`.
- **Stripe** — `STRIPE_*` (LIVE keys) for online rent payments.
- **Google sign-in** — `GOOGLE_CLIENT_ID` / `GOOGLE_CLIENT_SECRET` (see §7).

> `.env` lives next to the compose file's working dir and is gitignored — it never gets
> committed. Keep a copy of the secrets in your password manager.

## 6. Deploy

```bash
# Release a specific commit by pushing a version tag:
git tag vX.Y.Z
git push origin vX.Y.Z

# Or use GitHub: Actions -> Deploy -> Run workflow (optionally enter a ref).
```

The workflow builds and pushes all three images, then the production box pulls and starts
them; it never builds application images locally. Traefik requests the TLS cert on the first
run. **Migrations apply automatically on boot** — both the API and the Engine run
the EF Core migrations themselves, serialized behind a shared PostgreSQL advisory lock so
they never race. There is no separate migration step.

Watch it come up:

```bash
docker compose -f deploy/docker-compose.prod.yml ps
docker compose -f deploy/docker-compose.prod.yml logs -f traefik   # cert issuance
docker compose -f deploy/docker-compose.prod.yml logs -f api       # migrations + seed
```

The first admin user is **not** auto-seeded in production (seeding is gated to
Development). Create your account through the app's normal registration flow, or via the
API, after the stack is up.

## 7. Google OAuth redirect URI

The web app builds its redirect URI as `{origin}/auth/google/callback`
(verified in `web/src/routes/auth/google/callback/+server.ts`). With `ORIGIN` set to the
subdomain, the **authorized redirect URI** to register in the Google Cloud console
(*APIs & Services → Credentials → your OAuth client → Authorized redirect URIs*) is
exactly:

```
https://rentalcommand.net/auth/google/callback
```

Add `https://rentalcommand.net` under *Authorized JavaScript origins* too. Until both
this and `GOOGLE_CLIENT_ID`/`GOOGLE_CLIENT_SECRET` are set, the "Sign in with Google"
button returns 501 and the rest of auth (email/password) works normally.

## 8. Flip email to Google Workspace SMTP (optional)

SendGrid is the default transport. For early testing on `rentalcommand.net`, Google
Workspace/Gmail authenticated SMTP is the fastest path: create an app password for
`jcoble@rentalcommand.net`, then set:

```
EMAIL_TRANSPORT=Smtp
SMTP_HOST=smtp.gmail.com
SMTP_PORT=587
SMTP_USE_SSL=true
SMTP_USERNAME=jcoble@rentalcommand.net
SMTP_PASSWORD=<Google app password>
SMTP_FROM_EMAIL=jcoble@rentalcommand.net
SMTP_FROM_NAME=Rental Command
```

Then re-apply:

```bash
docker compose -f deploy/docker-compose.prod.yml up -d engine
```

(Email is sent from the **engine** via the outbox dispatcher. Use a provider-issued
app-specific password, not the normal account password. If app passwords are blocked
later, Google Workspace SMTP relay is also supported: use `smtp-relay.gmail.com:587`,
allow the VPS IP `49.13.236.209` in Workspace Admin, and leave `SMTP_USERNAME` /
`SMTP_PASSWORD` blank.)

## 9. Verify

```bash
# Health endpoint (served by the API through the /api… same-origin route):
curl -fsS https://rentalcommand.net/health
# → {"status":"ok"}

# TLS cert is real (Let's Encrypt), not self-signed:
echo | openssl s_client -servername rentalcommand.net \
  -connect rentalcommand.net:443 2>/dev/null | openssl x509 -noout -issuer -dates

# HTTP redirects to HTTPS:
curl -sI http://rentalcommand.net | grep -i location   # → https://rentalcommand.net/
```

Then load **https://rentalcommand.net** in a browser — the SvelteKit app should serve
over a valid padlock, and login / API calls (which go to `/api` on the same origin) should
work. If the cert is missing, check `docker compose ... logs traefik` (usually DNS not yet
propagated or port 443 not open).

## Operations cheatsheet

```bash
cd /opt/rental-command

# Deploy a release from the repository checkout on your workstation:
git tag vX.Y.Z
git push origin vX.Y.Z

# Or run Actions -> Deploy -> Run workflow for an explicit manual deploy.

# Logs / restart one service:
docker compose -f deploy/docker-compose.prod.yml logs -f web
docker compose -f deploy/docker-compose.prod.yml restart engine

# Manual DB backup (data lives in the `rentalcommand-postgres` volume):
docker exec rentalcommand-postgres pg_dump -U rentalcommand_app rentalcommand \
  | gzip > /opt/rental-command/backup-$(date +%F).sql.gz

# Inspect the issued cert:
docker exec rentalcommand-traefik cat /letsencrypt/acme.json | head
```

### Data that persists (named Docker volumes)

| Volume | Holds |
|--------|-------|
| `postgres_data` | the database |
| `uploads` | scanned/uploaded documents + e-sign PDFs (shared by api + engine) |
| `dataprotection_keys` | ASP.NET DataProtection keys (token/cookie protection; shared by api + engine) |
| `letsencrypt` | the TLS cert (`acme.json`) |

Don't delete these between deploys — losing `dataprotection_keys` invalidates existing
auth tokens; losing `uploads` loses stored documents.
