<script lang="ts">
	import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { browser } from '$app/environment';
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
		staleTime: 60_000
	}));

	// Persist the "I've answered this" choice so returning sandbox users skip the fork (F8).
	const CHOICE_KEY = 'rc.getStarted.choice';
	function hasMadeChoice(): boolean {
		if (!browser) return false;
		try {
			return localStorage.getItem(CHOICE_KEY) === 'explored';
		} catch {
			return false;
		}
	}

	// Already Live (or we can't tell) → there's no Sandbox choice to make; go to the dashboard.
	// Failing forward to the dashboard (rather than showing the "wipe demo & set up" path) is the
	// safe default for a Live account whose state fetch errored — matches the app's fail-open-to-Live.
	// Also forward if the user already answered the fork once (persisted choice) — F8.
	let forwarded = $state(false);
	$effect(() => {
		if (forwarded) return;
		const isSandbox = stateQuery.data?.isSandbox === true;
		if ((stateQuery.data && !isSandbox) || stateQuery.isError || (isSandbox && hasMadeChoice())) {
			forwarded = true;
			goto('/');
		}
	});

	function exploreSandbox() {
		if (browser) {
			try {
				localStorage.setItem(CHOICE_KEY, 'explored');
			} catch {
				/* storage may be unavailable */
			}
		}
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
			// Demo data was wiped server-side — every cached query is now stale.
			await queryClient.invalidateQueries();
			dialogOpen = false;
			confirmText = '';
			showSuccess("You're live! The demo data was cleared — let's set up your real portfolio.");
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

<div
	class="box-border flex min-h-full items-center justify-center bg-muted/30 p-6"
	data-testid="get-started-page"
>
	{#if stateQuery.data?.isSandbox !== true}
		<!-- Loading, errored, or Live: show a spinner while the effect forwards to the dashboard. -->
		<div
			class="flex items-center gap-2 text-sm text-muted-foreground"
			data-testid="get-started-loading"
		>
			<Loader2 class="h-4 w-4 animate-spin" />
			Loading…
		</div>
	{:else}
		<div class="w-full max-w-3xl">
			<div class="mb-6 text-center">
				<h1 class="text-2xl font-bold" data-testid="get-started-title">How do you want to start?</h1>
				<p class="mt-1 text-sm text-muted-foreground">
					Explore the app with sample data, or set up your own real portfolio. You can always go
					live later.
				</p>
			</div>
			<div class="grid gap-4 sm:grid-cols-2">
				<Card.Root class="flex flex-col" data-testid="get-started-sandbox-card">
					<Card.Content class="flex flex-1 flex-col items-start gap-3 p-6">
						<span class="flex h-10 w-10 items-center justify-center rounded-full bg-warning/15 text-warning">
							<FlaskConical class="h-5 w-5" />
						</span>
						<h2 class="text-lg font-semibold">Explore in Sandbox</h2>
						<p class="flex-1 text-sm text-muted-foreground">
							Jump into a demo account already filled with sample properties, tenants, and leases.
							Nothing sends real emails or texts, or charges any cards — poke around freely.
						</p>
						<Button
							variant="outline"
							class="gap-1.5"
							data-testid="get-started-explore-sandbox"
							onclick={exploreSandbox}
						>
							Explore the demo
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
