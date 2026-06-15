<script lang="ts">
	import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { browser } from '$app/environment';
	import { page } from '$app/state';
	import {
		FlaskConical,
		Rocket,
		TriangleAlert,
		Loader2,
		ArrowRight,
		ListChecks,
		Check,
		Lock,
		PartyPopper,
		RotateCcw,
	} from '@lucide/svelte';
	import * as Card from '$lib/components/ui/card';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import Progress from '$lib/components/ui/Progress.svelte';
	import { portfolios } from '$lib/api/endpoints/portfolios';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import {
		GETTING_STARTED_TASKS,
		computeProgress,
		isTaskDone,
		taskHref,
		type GettingStartedTask,
	} from '$lib/onboarding/getting-started-tasks';
	import {
		loadManualDone,
		saveManualDone,
		clearManualDone,
	} from '$lib/onboarding/getting-started-progress.svelte';
	import { useGettingStarted } from '$lib/onboarding/use-getting-started.svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

	const stateQuery = createQuery(() => ({
		queryKey: ['sandbox-state', portfolioId],
		enabled: portfolioId > 0,
		queryFn: () => portfolios.sandboxState(),
		staleTime: 60_000
	}));

	// --- First-run Sandbox fork (preserved from the original screen, F8) ----------------------------
	// A brand-new Sandbox account is offered the explore-vs-go-live choice ONCE. After they choose
	// (persisted), or on any Live account, this route becomes the durable getting-started checklist.
	const CHOICE_KEY = 'rc.getStarted.choice';
	function hasMadeChoice(): boolean {
		if (!browser) return false;
		try {
			return localStorage.getItem(CHOICE_KEY) === 'explored';
		} catch {
			return false;
		}
	}
	let choiceMade = $state(hasMadeChoice());
	const isSandbox = $derived(stateQuery.data?.isSandbox === true);
	// An explicit ?view=checklist (e.g. the dashboard "See the checklist" card) means "show me my
	// progress", NOT "let me pick explore-vs-go-live" — so it bypasses the first-run fork. Without this
	// a Sandbox user who hasn't persisted a choice was sent to the demo-data-wipe screen instead.
	const forceChecklist = $derived(page.url.searchParams.get('view') === 'checklist');
	// Show the fork only for a Sandbox account that hasn't answered yet AND didn't ask for the checklist
	// directly. Live (or errored state), post-choice Sandbox, and ?view=checklist fall through to it.
	const showFork = $derived(isSandbox && !choiceMade && !forceChecklist);

	function exploreSandbox() {
		if (browser) {
			try {
				localStorage.setItem(CHOICE_KEY, 'explored');
			} catch {
				/* storage may be unavailable */
			}
		}
		choiceMade = true;
	}

	// --- Checklist -----------------------------------------------------------------------------------
	const gs = useGettingStarted();
	const ready = $derived(gs.ready());
	const signals = $derived(gs.signals());

	// Manual done/skip overrides, persisted per portfolio. Held in component state so toggles re-render;
	// reloaded when the portfolio changes.
	let manualDone = $state<Set<string>>(new Set());
	let loadedFor = $state<number>(-1);
	$effect(() => {
		if (portfolioId > 0 && loadedFor !== portfolioId) {
			manualDone = loadManualDone(portfolioId);
			loadedFor = portfolioId;
		}
	});

	const progress = $derived(computeProgress(signals, manualDone));

	function done(task: GettingStartedTask): boolean {
		return isTaskDone(task, signals, manualDone);
	}
	// A task whose target needs a Live account but we're in Sandbox: the in-app create flow is disabled
	// there (Sandbox is read-only demo data), so we lock the deep-link and explain why.
	function locked(task: GettingStartedTask): boolean {
		return task.requiresLive === true && isSandbox;
	}

	function toggleManual(task: GettingStartedTask) {
		// Only meaningful when the data doesn't already satisfy it (auto-complete always wins).
		if (task.isComplete(signals)) return;
		const next = new Set(manualDone);
		if (next.has(task.key)) next.delete(task.key);
		else next.add(task.key);
		manualDone = next;
		saveManualDone(portfolioId, next);
	}

	function startOver() {
		manualDone = new Set();
		clearManualDone(portfolioId);
		showSuccess('Checklist reset — anything already set up stays checked.');
	}

	const coreTasks = $derived(GETTING_STARTED_TASKS.filter((t) => t.core));
	const optionalTasks = $derived(GETTING_STARTED_TASKS.filter((t) => !t.core));

	// --- Go Live (irreversible wipe of demo data) → onboarding. Unchanged from the original screen. --
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
			showSuccess("You're set up with your real rentals! The example data was cleared.");
			await goto('/onboarding');
		},
		onError: (err) =>
			showError(apiErrorMessage(err, 'Could not switch to a live account. Please try again.'))
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

<div class="box-border h-full overflow-y-auto bg-muted/30 p-6 pb-20" data-testid="get-started-page">
	{#if stateQuery.isLoading}
		<div class="flex h-64 items-center justify-center gap-2 text-sm text-muted-foreground" data-testid="get-started-loading">
			<Loader2 class="h-4 w-4 animate-spin" />
			Loading…
		</div>
	{:else if showFork}
		<!-- First-run fork: explore the example data, or wipe it and set up your real rentals. -->
		<div class="mx-auto w-full max-w-3xl">
			<div class="mb-6 text-center">
				<h1 class="text-2xl font-bold" data-testid="get-started-title">How do you want to start?</h1>
				<p class="mt-1 text-sm text-muted-foreground">
					Explore the app with sample data, or set up your own real rentals. You can switch to
					your real rentals anytime.
				</p>
			</div>
			<div class="grid gap-4 sm:grid-cols-2">
				<Card.Root class="flex flex-col" data-testid="get-started-sandbox-card">
					<Card.Content class="flex flex-1 flex-col items-start gap-3 p-6">
						<span class="flex h-10 w-10 items-center justify-center rounded-full bg-warning/15 text-warning">
							<FlaskConical class="h-5 w-5" />
						</span>
						<h2 class="text-lg font-semibold">Explore with sample data</h2>
						<p class="flex-1 text-sm text-muted-foreground">
							Jump into an account already filled with sample properties, tenants, and leases.
							Nothing sends real emails or texts, or charges any cards — poke around freely.
						</p>
						<Button variant="outline" class="gap-1.5" data-testid="get-started-explore-sandbox" onclick={exploreSandbox}>
							Start exploring
							<ArrowRight class="h-4 w-4" />
						</Button>
					</Card.Content>
				</Card.Root>

				<Card.Root class="flex flex-col border-primary/40" data-testid="get-started-real-card">
					<Card.Content class="flex flex-1 flex-col items-start gap-3 p-6">
						<span class="flex h-10 w-10 items-center justify-center rounded-full bg-primary/10 text-primary">
							<Rocket class="h-5 w-5" />
						</span>
						<h2 class="text-lg font-semibold">Set up my real portfolio</h2>
						<p class="flex-1 text-sm text-muted-foreground">
							Clear the example data and start clean with your own properties. We'll walk you through it
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
	{:else}
		<!-- The durable getting-started checklist. -->
		<div class="mx-auto w-full max-w-2xl">
			<div class="mb-5 flex items-start justify-between gap-3">
				<div class="flex items-start gap-3">
					<span class="flex h-10 w-10 shrink-0 items-center justify-center rounded-[var(--m3-shape-large)] bg-primary/10 text-primary">
						<ListChecks class="h-5 w-5" />
					</span>
					<div>
						<h1 class="text-2xl font-bold" data-testid="get-started-checklist-title">Getting started</h1>
						<p class="mt-1 text-sm text-muted-foreground">
							Work through these to get up and running. Tap any step and we'll take you to the exact
							spot and highlight what to do. Steps check themselves off as you go.
						</p>
					</div>
				</div>
			</div>

			<!-- Progress summary -->
			<Card.Root class="mb-5" data-testid="get-started-progress">
				<Card.Content class="p-5">
					<div class="mb-2 flex items-center justify-between gap-3">
						<div class="flex items-center gap-2">
							{#if progress.allDone && !isSandbox}
								<!-- A1: only celebrate on a real (Live) account. In Sandbox the steps are auto-checked
								     by seeded demo data, so "You're all set!" would be a lie. -->
								<PartyPopper class="h-5 w-5 text-success" />
								<span class="font-semibold text-foreground" data-testid="get-started-progress-text">You're all set!</span>
							{:else if isSandbox}
								<span class="font-semibold text-foreground" data-testid="get-started-progress-text">
									Exploring with sample data
								</span>
							{:else}
								<span class="font-semibold text-foreground" data-testid="get-started-progress-text">
									{progress.doneCount} of {progress.totalCount} done
								</span>
							{/if}
						</div>
						{#if manualDone.size > 0}
							<Button variant="ghost" size="sm" class="gap-1.5 text-muted-foreground" onclick={startOver} data-testid="get-started-reset">
								<RotateCcw class="h-3.5 w-3.5" />
								Start over
							</Button>
						{/if}
					</div>
					<Progress value={progress.doneCount} max={progress.totalCount} />
					{#if !progress.allCoreDone}
						<p class="mt-2 text-xs text-muted-foreground">
							The first {progress.coreTotalCount} steps are the essentials. The rest turn on automatic reminders.
						</p>
					{/if}
				</Card.Content>
			</Card.Root>

			{#if isSandbox}
				<div class="mb-5 flex flex-col gap-3 rounded-[var(--m3-shape-large)] border border-warning/40 bg-warning/10 px-4 py-3 sm:flex-row sm:items-center sm:justify-between" data-testid="get-started-sandbox-note">
					<p class="text-sm text-foreground">
						You're using <strong class="font-semibold">example data</strong>. Some steps are
						locked until you switch to your real rentals — that clears the example data and starts
						you clean.
					</p>
					<Button class="shrink-0 gap-1.5" size="sm" data-testid="get-started-go-live" onclick={openDialog}>
						<Rocket class="h-4 w-4" />
						Use my real rentals
					</Button>
				</div>
			{/if}

			{#if !ready}
				<div class="flex items-center gap-2 py-6 text-sm text-muted-foreground" data-testid="get-started-checklist-loading">
					<Loader2 class="h-4 w-4 animate-spin" />
					Checking what's already set up…
				</div>
			{:else}
				<!-- Core essentials -->
				<div class="mb-2 px-1 text-xs font-semibold uppercase tracking-wide text-muted-foreground">
					The essentials
				</div>
				<div class="mb-6 space-y-2" data-testid="get-started-core-tasks">
					{#each coreTasks as task (task.key)}
						{@render taskRow(task)}
					{/each}
				</div>

				<!-- Optional add-ons -->
				<div class="mb-2 px-1 text-xs font-semibold uppercase tracking-wide text-muted-foreground">
					Turn on reminders <span class="font-normal normal-case text-muted-foreground/70">(optional)</span>
				</div>
				<div class="space-y-2" data-testid="get-started-optional-tasks">
					{#each optionalTasks as task (task.key)}
						{@render taskRow(task)}
					{/each}
				</div>
			{/if}
		</div>
	{/if}
</div>

<!-- A single checklist row. Done → green check + struck label; not done → tappable deep-link that
     spotlights the target, plus a small "mark done" affordance for steps the data can't detect. -->
{#snippet taskRow(task: GettingStartedTask)}
	{@const isDone = done(task)}
	{@const isLocked = locked(task) && !isDone}
	{@const Icon = task.icon}
	<div
		class="flex items-center gap-3 rounded-[var(--m3-shape-large)] border bg-background p-3 transition-colors {isDone ? 'border-success/40 bg-success/5' : 'border-border'}"
		data-testid="get-started-task-{task.key}"
		data-task-done={isDone}
	>
		<!-- Status check / mark-done toggle -->
		<button
			type="button"
			class="flex h-6 w-6 shrink-0 items-center justify-center rounded-full border transition-colors {isDone ? 'border-success bg-success text-success-foreground' : 'border-border text-transparent hover:border-primary'}"
			aria-label={isDone ? `${task.label} done` : `Mark "${task.label}" done`}
			data-testid="get-started-task-check-{task.key}"
			disabled={task.isComplete(signals)}
			onclick={() => toggleManual(task)}
		>
			<Check class="h-4 w-4" />
		</button>

		<Icon class="h-4 w-4 shrink-0 {isDone ? 'text-success' : 'text-muted-foreground'}" />

		<div class="min-w-0 flex-1">
			<p class="truncate text-sm font-medium {isDone ? 'text-muted-foreground line-through' : 'text-foreground'}">
				{task.label}
			</p>
			<p class="truncate text-xs text-muted-foreground">{task.eli5}</p>
		</div>

		{#if isDone}
			<span class="shrink-0 text-xs font-medium text-success">Done</span>
		{:else if isLocked}
			<span class="flex shrink-0 items-center gap-1 text-xs text-muted-foreground" data-testid="get-started-task-locked-{task.key}">
				<Lock class="h-3.5 w-3.5" />
				Live only
			</span>
		{:else}
			<Button
				href={taskHref(task)}
				variant="outline"
				size="sm"
				class="shrink-0 gap-1.5"
				data-testid="get-started-task-go-{task.key}"
			>
				Show me
				<ArrowRight class="h-4 w-4" />
			</Button>
		{/if}
	</div>
{/snippet}

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
			<Button
				variant="outline"
				onclick={closeDialog}
				disabled={goLiveMutation.isPending}
				data-testid="get-started-cancel"
			>
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
