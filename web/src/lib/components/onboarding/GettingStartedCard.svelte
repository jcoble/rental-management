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
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';
	import Progress from '$lib/components/ui/Progress.svelte';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
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

	// Manual overrides reload when the portfolio changes (re-read so the right per-portfolio set shows).
	const manualDone = $derived.by(() => {
		void portfolioId;
		return loadManualDone(portfolioId);
	});

	const signals = $derived(gs.signals());
	const ready = $derived(gs.ready());
	const progress = $derived(computeProgress(signals, manualDone));

	// The next few not-yet-done tasks, to surface inline as one-tap shortcuts.
	const upNext = $derived(
		GETTING_STARTED_TASKS.filter((t) => !isTaskDone(t, signals, manualDone)).slice(0, 3)
	);

	// Hide entirely once everything's done. Until the data settles, render nothing (avoid a flash of an
	// "all incomplete" card on first paint).
	const visible = $derived(ready && !progress.allDone);
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
				<Button href="/get-started" class="shrink-0 gap-2" data-testid="dashboard-getting-started-cta">
					See the checklist
					<ArrowRight class="h-4 w-4" />
				</Button>
			</div>

			<!-- Progress bar + count -->
			<div class="mt-4">
				<div class="mb-1.5 flex items-center justify-between text-xs text-muted-foreground">
					<span data-testid="dashboard-getting-started-progress-label">
						{progress.doneCount} of {progress.totalCount} done
					</span>
					{#if progress.allCoreDone}
						<span class="font-medium text-success">Core setup complete</span>
					{/if}
				</div>
				<Progress value={progress.doneCount} max={progress.totalCount} />
			</div>

			<!-- Up-next shortcuts -->
			{#if upNext.length > 0}
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
