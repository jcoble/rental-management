# Azure remote build, test, and preview runner

Rental Command uses the private Azure machine for resource-heavy, explicitly
requested verification and temporary branch previews. It is **not** a deployment
target and receives no production secrets.

## Safety model

- Both workflows are `workflow_dispatch` only. Pushes, pull requests, and merges
  never start them.
- A required `ref` is resolved by GitHub checkout and the exact commit SHA is
  recorded in the run summary.
- Jobs require runner labels `azure-build` and `rental-command`.
- Repository concurrency plus `/run/lock/azure-build-runner.lock` serializes work with
  EdiPlatform jobs on the same host.
- Preview credentials are random, local to the preview, and stored mode `0600`.
  Provider, OAuth, payment, email, SMS, and production credentials stay empty.
- Disposable preview state lives under `/srv/dev-stacks/rental-command`, while
  PostgreSQL data and its generated preview credentials live under
  `/srv/dev-stack-data/rental-command`. Both are outside the Actions workspace.
  Stop, replacement, and TTL cleanup preserve the database; only the explicit
  `reset-data` action deletes it.

## Verify a pushed branch

The selected ref must exist on GitHub; uncommitted local files cannot be tested.

```bash
git push origin my-branch
gh workflow run remote-verify.yml --ref my-branch \
  -f ref=my-branch -f profile=standard
```

Profiles:

- `focused`: solution build plus Core tests; accepts an optional .NET
  `test_filter`.
- `standard`: solution build, normal .NET test projects, Svelte check, and web
  unit tests.
- `full`: standard plus PostgreSQL/Testcontainers integration tests and a web
  production build.

Watch the run and return its exit status to the calling agent:

```bash
run_id="$(gh run list --workflow remote-verify.yml --branch my-branch \
  --limit 1 --json databaseId --jq '.[0].databaseId')"
gh run watch "$run_id" --exit-status
```

## Start and view a branch preview

```bash
gh workflow run remote-preview.yml --ref my-branch \
  -f action=start -f ref=my-branch -f stack_id=rental -f ttl_hours=4
```

The run summary prints a private URL such as `http://100.x.y.z:15667`. Open that
URL on a Mac or phone signed into the same Tailscale tailnet. The gateway serves
the web app and same-origin `/api/v1` and `/health` routes on one port.

Lifecycle commands use the same `ref` for an auditable checkout:

```bash
gh workflow run remote-preview.yml --ref my-branch -f action=health -f ref=my-branch -f stack_id=rental -f ttl_hours=4
gh workflow run remote-preview.yml --ref my-branch -f action=logs   -f ref=my-branch -f stack_id=rental -f ttl_hours=4
gh workflow run remote-preview.yml --ref my-branch -f action=stop   -f ref=my-branch -f stack_id=rental -f ttl_hours=4
```

These normal lifecycle actions preserve the preview database, including tenant
and seeded/manual test data. To deliberately start with an empty database, run:

```bash
gh workflow run remote-preview.yml --ref my-branch \
  -f action=reset-data -f ref=my-branch -f stack_id=rental -f ttl_hours=4
```

`reset-data` stops the stack and permanently deletes that stack's PostgreSQL
files and stored preview credentials. The next `start` initializes a fresh
database. Starting the same `stack_id` otherwise replaces only the application
containers and source. Only one Rental Command preview is
allowed because the VM has four cores and the private port is intentionally
stable. Logs are uploaded as workflow artifacts.

## Required host setup

The runner account needs Docker access and ownership of the persistent roots:

```bash
sudo install -d -o github-runner -g github-runner /srv/dev-stacks/rental-command
sudo install -d -o github-runner -g github-runner /srv/dev-stack-data/rental-command
sudo install -d -o github-runner -g github-runner /opt/runner-tools/rental-command
sudo install -m 0755 scripts/remote/preview-stack.sh /opt/runner-tools/rental-command/preview-stack.sh
sudo install -m 0755 scripts/remote/cleanup-preview-stacks.sh /usr/local/bin/rental-preview-cleanup
sudo install -m 0644 deploy/systemd/rental-preview-cleanup.service /etc/systemd/system/
sudo install -m 0644 deploy/systemd/rental-preview-cleanup.timer /etc/systemd/system/
sudo install -m 0644 deploy/tmpfiles.d/azure-build-runner.conf /etc/tmpfiles.d/
sudo systemd-tmpfiles --create /etc/tmpfiles.d/azure-build-runner.conf
sudo systemctl daemon-reload
sudo systemctl enable --now rental-preview-cleanup.timer
```

The timer is the hard TTL guarantee; workflow starts also clean up expired
previews opportunistically. The host also needs GitHub runner, Docker +
Compose, .NET 10 SDK, Node 24, pnpm, `flock`, `openssl`, `curl`, and Tailscale.

Never add production environment files or deploy SSH keys to this runner.
