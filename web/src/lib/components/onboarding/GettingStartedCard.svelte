<!--
  GettingStartedCard — the persistent "Getting started" nudge on the dashboard. Shows the checklist
  progress and the next couple of incomplete steps, each deep-linking (with spotlight) to the exact
  spot to do it. Disappears once everything is done so it never nags a set-up landlord.

  Self-contained: runs the shared getting-started data hook (which reuses the app's existing list-query
  cache — no extra endpoints) and the per-portfolio manual-progress store. Safe in Sandbox AND Live;
  the in-app create flows are Live-only, so the card explains that and points Sandbox users at the
  read-through checklist rather than dead create links.
-->
<script lang="ts">
	import { ListChecks, ArrowRight, Check, CircleDashed } from '@lucide/svelte';
	import { createQuery } from '@tanstack/svelte-query';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';
	import Progress from '$lib/components/ui/Progress.svelte';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { portfolios } from '$lib/api/endpoints/portfolios';
	import {
		GETTING_STARTED_TASKS,
		computeProgress,
		isTaskDone,
		taskHref,
	} from '$lib/onboarding/getting-started-tasks';
	import { loadManualDone } from '$lib/onboarding/getting-started-progress.svelte';
	import { useGettingStarted } from '$lib/onboarding/use-getting-started.svelte';

	const gs = useGettingStarted();
	const portfolioId = $derived(getCurrentPortfolioId());

	// A1: decide Sandbox-vs-Live from the dedicated sandbox-state endpoint (shared TanStack cache with
	// SandboxBanner — same key + staleTime, so no extra network cost), NOT the gettingStarted hook's
	// isSandbox(): that one short-circuits to false once the optional-query gate closes, which would
	// wrongly drop the honest Sandbox framing for a seeded account whose signals all read "done".
	const sandboxStateQuery = createQuery(() => ({
		queryKey: ['sandbox-state', portfolioId],
		enabled: portfolioId > 0,
		queryFn: () => portfolios.sandboxState(),
		staleTime: 60_000,
	}));

	// Manual overrides reload when the portfolio changes (re-read so the right per-portfolio set shows).
	const manualDone = $derived.by(() => {
		void portfolioId;
		return loadManualDone(portfolioId);
	});

	const signals = $derived(gs.signals());
	const ready = $derived(gs.ready());
	const progress = $derived(computeProgress(signals, manualDone));

	// A1: in Sandbox the checklist is auto-satisfied by SEEDED demo records — so "core setup complete"
	// would be a lie (the account has no REAL property/tenant/lease). Only celebrate the spine on a Live
	// account; in Sandbox show honest "explore / set up for real" framing instead.
	const isSandbox = $derived(sandboxStateQuery.data?.isSandbox === true);
	const celebrateCoreDone = $derived(progress.allCoreDone && !isSandbox);

	// The next few not-yet-done tasks, to surface inline as one-tap shortcuts.
	const upNext = $derived(
		GETTING_STARTED_TASKS.filter((t) => !isTaskDone(t, signals, manualDone)).slice(0, 3)
	);

	// Hide entirely once everything's done — but NEVER treat Sandbox's seeded "all done" as real
	// completion (A1): in Sandbox we keep the card up to honestly point the user at setting up their own
	// rentals. Until the data settles, render nothing (avoid a flash of an "all incomplete" card).
	const visible = $derived(ready && (isSandbox || !progress.allDone));
</script>

{#if visible}
	<Card.Root
		class="m3-motion-enter mb-6 overflow-hidden border-primary/40 bg-primary/8"
		style="--m3-motion-index: 1"
		data-testid="dashboard-getting-started-card"
	>
		<Card.Content class="p-5">
			<div class="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
				<div class="flex items-start gap-3">
					<div class="flex h-10 w-10 shrink-0 items-center justify-center rounded-[var(--m3-shape-large)] bg-primary/15 text-primary">
						<ListChecks class="h-5 w-5" />
					</div>
					<div>
						<p class="font-semibold text-foreground">Getting started</p>
						<p class="mt-0.5 text-sm text-muted-foreground">
							A short checklist to get your portfolio up and running — each step takes you to the
							exact spot and shows you what to do.
						</p>
					</div>
				</div>
				<Button href="/get-started?view=checklist" class="shrink-0 gap-2" data-testid="dashboard-getting-started-cta">
					See the checklist
					<ArrowRight class="h-4 w-4" />
				</Button>
			</div>

			<!-- Progress bar + count. In Sandbox the counts come from seeded demo data, so we don't claim
			     "core setup complete" — that's reserved for a real, Live spine (A1). -->
			<div class="mt-4">
				<div class="mb-1.5 flex items-center justify-between text-xs text-muted-foreground">
					<span data-testid="dashboard-getting-started-progress-label">
						{#if isSandbox}
							Exploring with sample data
						{:else}
							{progress.doneCount} of {progress.totalCount} done
						{/if}
					</span>
					{#if celebrateCoreDone}
						<span class="font-medium text-success">Core setup complete</span>
					{/if}
				</div>
				{#if !isSandbox}
					<Progress value={progress.doneCount} max={progress.totalCount} />
				{/if}
			</div>

			<!-- Up-next shortcuts. In Sandbox, the seeded records auto-check everything, so instead of a
			     misleading "all done" we nudge the user to set up their own real rentals (A1). -->
			{#if isSandbox}
				<div
					class="mt-4 flex flex-col gap-2 rounded-lg border border-primary/40 bg-primary/5 px-3 py-2.5 text-sm text-foreground sm:flex-row sm:items-center sm:justify-between"
					data-testid="dashboard-getting-started-sandbox"
				>
					<span class="min-w-0">
						This is example data so you can look around. When you're ready, set up your own rentals.
					</span>
					<Button href="/get-started?view=checklist" variant="outline" size="sm" class="shrink-0 gap-1.5">
						Set up my rentals
						<ArrowRight class="h-4 w-4" />
					</Button>
				</div>
			{:else if upNext.length > 0}
				<div class="mt-4 space-y-1.5" data-testid="dashboard-getting-started-upnext">
					{#each upNext as task (task.key)}
						<a
							href={taskHref(task)}
							class="group flex items-center gap-2.5 rounded-lg border border-border bg-background px-3 py-2 transition-colors hover:border-primary/40 hover:bg-primary/5"
							data-testid="dashboard-getting-started-task-{task.key}"
						>
							<CircleDashed class="h-4 w-4 shrink-0 text-muted-foreground" />
							<span class="min-w-0 flex-1">
								<span class="block truncate text-sm font-medium text-foreground">{task.label}</span>
								<span class="block truncate text-xs text-muted-foreground">{task.eli5}</span>
							</span>
							<ArrowRight class="h-4 w-4 shrink-0 text-muted-foreground/60 transition-transform group-hover:translate-x-0.5 group-hover:text-primary" />
						</a>
					{/each}
				</div>
			{:else}
				<div
					class="mt-4 flex items-center gap-2 rounded-lg border border-success/40 bg-success/10 px-3 py-2 text-sm text-foreground"
					data-testid="dashboard-getting-started-optional-left"
				>
					<Check class="h-4 w-4 text-success" />
					The essentials are done — open the checklist to finish the optional add-ons.
				</div>
			{/if}
		</Card.Content>
	</Card.Root>
{/if}
