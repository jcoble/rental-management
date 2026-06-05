# Onboarding: auto-satisfy/skip steps + gate to Live + login choice

**Goal:** Make the setup wizard skip already-satisfied steps, never run in Sandbox mode, and let a
user pick "Explore in Sandbox" vs "Set up my real portfolio" right after login.

**Architecture:** All changes are web-only (SvelteKit). The sandbox flag already exists on the
backend (`Portfolio.IsSandbox`, exposed via `GET /portfolio/sandbox-state` → `portfolios.sandboxState()`).
We gate the onboarding page and the dashboard setup banner behind a live-only check, auto-advance the
wizard to the first incomplete step, and add a post-login choice screen at `/get-started` that only
shows when the account is still in Sandbox.

**Tech Stack:** SvelteKit 5 (runes), TanStack Query, existing `portfolios` API client, existing
`SandboxBanner` Go-Live dialog pattern, `data-testid` on interactive elements.

---

## Context / current behavior

- `web/src/routes/(protected)/onboarding/+page.svelte` — 5-step wizard (portfolio, owner, property,
  tenants, lease). Already detects existing owners/properties/tenants/leases (`stepDone`,
  "you already have N …" banners) but does NOT auto-advance and has NO sandbox gate.
- Sandbox state: `portfolios.sandboxState()` → `{ portfolioId, isSandbox, sandboxSeededAtUtc }`.
  Go Live: `portfolios.goLive()` (one-way wipe + flip; pattern lives in `SandboxBanner.svelte`).
- Login: `web/src/routes/login/+page.server.ts` action redirects staff to `/` (or `/portal`).
- Dashboard: `web/src/routes/(protected)/+page.svelte` shows an empty-portfolio onboarding banner
  (`isEmptyPortfolio`) and a "Start setup" CTA → `/onboarding`.

## File structure

- Modify: `web/src/routes/(protected)/onboarding/+page.svelte` — add sandbox gate + auto-advance.
- Modify: `web/src/routes/(protected)/+page.svelte` — hide setup banner when in Sandbox.
- Modify: `web/src/routes/login/+page.server.ts` — staff land on `/get-started`.
- Create: `web/src/routes/(protected)/get-started/+page.svelte` — post-login choice screen.

---

### Task 1: Auto-advance the wizard past already-satisfied steps

**Files:**
- Modify: `web/src/routes/(protected)/onboarding/+page.svelte`

- [ ] **Step 1: Add an "auto-advanced once" guard + effect after the `stepDone` derived (around line 109).**

```svelte
	// On first load, jump straight to the first step that still needs the user.
	// Existing portfolios/owners/properties (or sandbox-seeded data) pre-complete
	// earlier steps, so we don't make the user click "Skip" through them. Runs once
	// (and only after the detection queries have settled) so manual back/next still work.
	let autoAdvanced = $state(false);
	const detectionReady = $derived(
		portfolioQuery.isSuccess &&
			ownersQuery.isSuccess &&
			propertiesQuery.isSuccess &&
			tenantsQuery.isSuccess &&
			leasesQuery.isSuccess
	);
	$effect(() => {
		if (autoAdvanced || finished || !detectionReady) return;
		autoAdvanced = true;
		const firstIncomplete = STEPS.findIndex((s) => !stepDone[s.key]);
		// All steps already satisfied → land on the last step (lease) rather than past the end.
		stepIndex = firstIncomplete === -1 ? STEPS.length - 1 : firstIncomplete;
	});
```

- [ ] **Step 2: Verify with svelte-check (no new errors) — full verify runs in Task 5.**

---

### Task 2: Live-only gate on the onboarding page

**Files:**
- Modify: `web/src/routes/(protected)/onboarding/+page.svelte`

- [ ] **Step 1: Add the sandbox-state query near the other detection queries (after `leasesQuery`, ~line 95).**

```svelte
	// Onboarding is for LIVE accounts only. A Sandbox account is pre-seeded demo data — the user
	// should "Go Live" first (which wipes demo data), so we bounce them to the dashboard.
	const sandboxQuery = createQuery(() => ({
		queryKey: ['sandbox-state', portfolioId],
		enabled: portfolioId > 0,
		queryFn: () => portfolios.sandboxState(),
		staleTime: 60_000,
	}));
	let redirectedFromSandbox = $state(false);
	$effect(() => {
		if (redirectedFromSandbox) return;
		if (sandboxQuery.data?.isSandbox === true) {
			redirectedFromSandbox = true;
			showError('Setup runs on a live account. Go live first to set up your real portfolio.');
			goto('/');
		}
	});
```

- [ ] **Step 2: Guard the auto-advance effect (Task 1) so it does not run while we are bouncing a
  sandbox user. Update the early-return in the auto-advance `$effect` to also bail when sandboxed.**

Change:
```svelte
		if (autoAdvanced || finished || !detectionReady) return;
```
to:
```svelte
		if (autoAdvanced || finished || !detectionReady) return;
		if (sandboxQuery.data?.isSandbox === true) return;
```

---

### Task 3: Hide the dashboard setup banner in Sandbox mode

**Files:**
- Modify: `web/src/routes/(protected)/+page.svelte`

- [ ] **Step 1: Add a sandbox query + derived gate near the other queries (after `propertiesQuery`, ~line 39).**

```svelte
	// The setup banner points at /onboarding, which is live-only. Sandbox is pre-seeded, so suppress it.
	const sandboxQuery = createQuery(() => ({
		queryKey: ['sandbox-state', getCurrentPortfolioId()],
		queryFn: () => portfolios.sandboxState(),
		staleTime: 60_000,
	}));
	const isSandbox = $derived(sandboxQuery.data?.isSandbox === true);
```

- [ ] **Step 2: Gate the banner. Change the condition on line ~133 from `{#if isEmptyPortfolio}` to
  `{#if isEmptyPortfolio && !isSandbox}`.**

```svelte
			{#if isEmptyPortfolio && !isSandbox}
```

---

### Task 4: Post-login choice screen (Explore Sandbox vs Set up real portfolio)

**Files:**
- Create: `web/src/routes/(protected)/get-started/+page.svelte`
- Modify: `web/src/routes/login/+page.server.ts`

- [ ] **Step 1: Send staff to `/get-started` after login.** In `login/+page.server.ts`, update
  `defaultLandingFor` so staff land on the choice screen (portal users unchanged). The choice screen
  self-forwards Live users to `/`, so this is harmless for already-live accounts.

```ts
/** Where to send a user after login when no explicit redirect is requested. */
function defaultLandingFor(roles: string[]): string {
	// Owners/tenants live in the portal; staff get the Sandbox-vs-real choice (which
	// forwards straight to the dashboard when the account is already Live).
	if (roles.includes('Owner') || roles.includes('Tenant')) return '/portal';
	return '/get-started';
}
```

- [ ] **Step 2: Create the choice page.** It queries sandbox state; forwards Live accounts straight
  to `/`; for Sandbox accounts it offers two paths. "Explore in Sandbox" → dashboard. "Set up my real
  portfolio" → the existing Go-Live confirm (wipes demo data) → `/onboarding`.

```svelte
<script lang="ts">
	import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { FlaskConical, Rocket, TriangleAlert, Loader2, ArrowRight } from '@lucide/svelte';
	import * as Card from '$lib/components/ui/card';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { portfolios } from '$lib/api/endpoints/portfolios';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

	const stateQuery = createQuery(() => ({
		queryKey: ['sandbox-state', portfolioId],
		enabled: portfolioId > 0,
		queryFn: () => portfolios.sandboxState(),
		staleTime: 60_000,
	}));

	// Already Live → there's no choice to make; go straight to the dashboard.
	let forwarded = $state(false);
	$effect(() => {
		if (forwarded) return;
		if (stateQuery.data && stateQuery.data.isSandbox !== true) {
			forwarded = true;
			goto('/');
		}
	});

	function exploreSandbox() {
		goto('/');
	}

	// "Set up my real portfolio" = Go Live (irreversible wipe of demo data) → onboarding.
	let dialogOpen = $state(false);
	let confirmText = $state('');
	const CONFIRM_PHRASE = 'GO LIVE';
	const confirmed = $derived(confirmText.trim().toUpperCase() === CONFIRM_PHRASE);

	const goLiveMutation = createMutation(() => ({
		mutationFn: () => portfolios.goLive(),
		onSuccess: async () => {
			await queryClient.invalidateQueries();
			dialogOpen = false;
			confirmText = '';
			showSuccess("You're live! The demo data was cleared — let's set up your real portfolio.");
			await goto('/onboarding');
		},
		onError: (err) => showError(apiErrorMessage(err, 'Could not switch to a live account. Please try again.')),
	}));

	function openDialog() {
		confirmText = '';
		dialogOpen = true;
	}
	function closeDialog() {
		if (goLiveMutation.isPending) return;
		dialogOpen = false;
		confirmText = '';
	}
	function confirmGoLive() {
		if (!confirmed || goLiveMutation.isPending) return;
		goLiveMutation.mutate();
	}
</script>

<svelte:head>
	<title>Get started - Rental Command</title>
</svelte:head>

<div class="box-border flex min-h-full items-center justify-center bg-muted/30 p-6" data-testid="get-started-page">
	{#if stateQuery.isLoading || (stateQuery.data && stateQuery.data.isSandbox !== true)}
		<div class="flex items-center gap-2 text-sm text-muted-foreground" data-testid="get-started-loading">
			<Loader2 class="h-4 w-4 animate-spin" />
			Loading…
		</div>
	{:else}
		<div class="w-full max-w-3xl">
			<div class="mb-6 text-center">
				<h1 class="text-2xl font-bold" data-testid="get-started-title">How do you want to start?</h1>
				<p class="mt-1 text-sm text-muted-foreground">
					Explore the app with sample data, or set up your own real portfolio. You can always go live later.
				</p>
			</div>
			<div class="grid gap-4 sm:grid-cols-2">
				<Card.Root class="flex flex-col" data-testid="get-started-sandbox-card">
					<Card.Content class="flex flex-1 flex-col gap-3 p-6">
						<span class="flex h-10 w-10 items-center justify-center rounded-full bg-warning/15 text-warning">
							<FlaskConical class="h-5 w-5" />
						</span>
						<h2 class="text-lg font-semibold">Explore in Sandbox</h2>
						<p class="flex-1 text-sm text-muted-foreground">
							Jump into a demo account already filled with sample properties, tenants, and leases.
							Nothing sends real emails or texts, or charges any cards — poke around freely.
						</p>
						<Button variant="outline" class="gap-1.5" data-testid="get-started-explore-sandbox" onclick={exploreSandbox}>
							Explore the demo
							<ArrowRight class="h-4 w-4" />
						</Button>
					</Card.Content>
				</Card.Root>

				<Card.Root class="flex flex-col border-primary/40" data-testid="get-started-real-card">
					<Card.Content class="flex flex-1 flex-col gap-3 p-6">
						<span class="flex h-10 w-10 items-center justify-center rounded-full bg-primary/10 text-primary">
							<Rocket class="h-5 w-5" />
						</span>
						<h2 class="text-lg font-semibold">Set up my real portfolio</h2>
						<p class="flex-1 text-sm text-muted-foreground">
							Clear the demo data and start clean with your own properties. We'll walk you through it
							step by step — the computer does the typing.
						</p>
						<Button class="gap-1.5" data-testid="get-started-setup-real" onclick={openDialog}>
							Set up my portfolio
							<ArrowRight class="h-4 w-4" />
						</Button>
					</Card.Content>
				</Card.Root>
			</div>
		</div>
	{/if}
</div>

<!-- Go Live confirm (irreversible, type-to-confirm) — mirrors SandboxBanner -->
<Dialog.Root open={dialogOpen} onOpenChange={(v) => { if (!v) closeDialog(); }}>
	<Dialog.Content data-testid="get-started-go-live-dialog" class="max-w-md">
		<Dialog.Header>
			<Dialog.Title class="flex items-center gap-2">
				<TriangleAlert class="h-5 w-5 text-warning" />
				Start your real portfolio
			</Dialog.Title>
			<Dialog.Description>
				This <strong class="font-semibold text-foreground">permanently deletes the demo and sample
				data</strong> and switches the account to live for good. You'll start clean. There's no way
				back to the sandbox afterward.
			</Dialog.Description>
		</Dialog.Header>
		<div class="space-y-2">
			<label for="get-started-confirm-input" class="block text-xs text-muted-foreground">
				Type <span class="font-mono font-semibold text-foreground">{CONFIRM_PHRASE}</span> to confirm.
			</label>
			<Input
				id="get-started-confirm-input"
				bind:value={confirmText}
				placeholder={CONFIRM_PHRASE}
				autocomplete="off"
				disabled={goLiveMutation.isPending}
				data-testid="get-started-confirm-input"
				onkeydown={(e) => { if (e.key === 'Enter') confirmGoLive(); }}
			/>
		</div>
		<Dialog.Footer>
			<Button variant="outline" onclick={closeDialog} disabled={goLiveMutation.isPending} data-testid="get-started-cancel">
				Cancel
			</Button>
			<Button
				class="gap-1.5"
				onclick={confirmGoLive}
				disabled={!confirmed || goLiveMutation.isPending}
				data-testid="get-started-confirm"
			>
				{#if goLiveMutation.isPending}
					<Loader2 class="h-4 w-4 animate-spin" />
					Setting up…
				{:else}
					<Rocket class="h-4 w-4" />
					Wipe demo &amp; set up
				{/if}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
```

---

### Task 5: Verify

- [ ] **Step 1: Build the backend (no backend changes, but confirm nothing broke).**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 2: Type-check + build the web app.**

Run: `cd web && npx svelte-check --threshold error && pnpm build`
Expected: 0 errors; build completes.

- [ ] **Step 3: Run unit tests (skip integration if no Postgres).**

Run: `dotnet test` (or unit-only if integration needs live Postgres — note in report).

- [ ] **Step 4: Commit.**

```bash
git add -A
git commit -m "Onboarding: auto-skip satisfied steps, gate to Live, add login choice"
```

---

## Self-review

- **Smart skipping** → Task 1 (auto-advance to first incomplete step; existing detection reused).
- **Live-only** → Task 2 (onboarding bounces sandbox users) + Task 3 (dashboard banner hidden in sandbox).
- **Login choice** → Task 4 (`/get-started` with Explore Sandbox vs Set up real → Go Live → onboarding).
- All new interactive elements carry `data-testid`. No backend/DTO changes needed.
</content>
</invoke>
