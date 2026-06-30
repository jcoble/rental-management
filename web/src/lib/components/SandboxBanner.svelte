<script lang="ts">
	import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { FlaskConical, Rocket, TriangleAlert, Loader2 } from '@lucide/svelte';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { portfolios } from '$lib/api/endpoints/portfolios';
	import { getAuthState } from '$lib/stores/auth.svelte';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';

	let {
		/**
		 * `banner` — slim always-visible bar for the app shell (hidden when Live).
		 * `card`   — a Settings card that always renders (shows Live state too).
		 */
		variant = 'banner'
	}: { variant?: 'banner' | 'card' } = $props();

	const queryClient = useQueryClient();
	const authState = getAuthState();

	// Track the active portfolio so the state re-fetches on portfolio switch.
	let portfolioId = $derived(getCurrentPortfolioId());

	const stateQuery = createQuery(() => ({
		queryKey: ['sandbox-state', portfolioId],
		enabled: authState.isAuthenticated && portfolioId > 0,
		queryFn: () => portfolios.sandboxState(),
		staleTime: 60_000
	}));

	let isSandbox = $derived(stateQuery.data?.isSandbox === true);

	let dialogOpen = $state(false);
	let confirmText = $state('');
	const CONFIRM_PHRASE = 'GO LIVE';
	let confirmed = $derived(confirmText.trim().toUpperCase() === CONFIRM_PHRASE);

	const goLiveMutation = createMutation(() => ({
		mutationFn: () => portfolios.goLive(),
		onSuccess: async () => {
			// Demo data was wiped server-side — every cached query is now stale.
			await queryClient.invalidateQueries();
			dialogOpen = false;
			confirmText = '';
			showSuccess("You're set up with your real rentals! The example data was cleared.");
			await goto('/');
		},
		onError: (err) => showError(apiErrorMessage(err, 'Could not go live. Please try again.'))
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

{#if variant === 'banner'}
	{#if isSandbox}
		<div
			role="status"
			data-testid="sandbox-banner"
			class="flex w-full items-center gap-2 border-b border-[color-mix(in_oklab,var(--warning)_40%,transparent)] bg-[color-mix(in_oklab,var(--warning)_14%,var(--background))] px-3 py-1.5 text-xs text-foreground sm:px-4"
		>
			<FlaskConical class="h-4 w-4 shrink-0 text-warning" />
				<p class="min-w-0 flex-1 leading-tight">
					<span class="font-semibold">Example data</span>
					<span class="hidden text-muted-foreground sm:inline">
						— you're exploring with sample records. Configured email and text providers still work for testing.
					</span>
					<span class="text-muted-foreground sm:hidden">— sample records.</span>
				</p>
			<Button
				size="sm"
				class="h-7 shrink-0 gap-1 bg-warning px-2.5 text-xs font-semibold text-warning-foreground hover:bg-[color-mix(in_oklab,var(--warning)_88%,black)]"
				onclick={openDialog}
				data-testid="go-live-button"
			>
				<Rocket class="h-3.5 w-3.5" />
				Use my real rentals
			</Button>
		</div>
	{/if}
{:else}
	<!-- Settings card variant -->
	<div
		class="rounded-lg border p-4 {isSandbox
			? 'border-[color-mix(in_oklab,var(--warning)_35%,transparent)] bg-[color-mix(in_oklab,var(--warning)_8%,var(--card))]'
			: 'border-border bg-muted/30'}"
		data-testid="settings-sandbox-card"
	>
		<div class="flex items-start gap-3">
			<span
				class="mt-0.5 flex h-8 w-8 shrink-0 items-center justify-center rounded-full {isSandbox
					? 'bg-warning/15 text-warning'
					: 'bg-primary/10 text-primary'}"
			>
				{#if isSandbox}
					<FlaskConical class="h-4 w-4" />
				{:else}
					<Rocket class="h-4 w-4" />
				{/if}
			</span>
			<div class="min-w-0 flex-1">
				{#if stateQuery.isLoading}
					<p class="text-sm text-muted-foreground">Checking account mode…</p>
					{:else if isSandbox}
						<p class="text-sm font-semibold">Example data</p>
						<p class="mt-1 text-xs text-muted-foreground">
							This account is loaded with example records so you can explore the app. Configured email
							and text providers still run for testing, so use reachable test recipients intentionally.
							When you're ready to run your real rentals, switch over — this permanently clears the example data and starts you clean.
							<span class="font-medium text-foreground">This can't be undone.</span>
						</p>
					<div class="mt-3">
						<Button
							class="gap-1.5 bg-warning text-warning-foreground hover:bg-[color-mix(in_oklab,var(--warning)_88%,black)]"
							onclick={openDialog}
							data-testid="go-live-button"
						>
							<Rocket class="h-4 w-4" />
							Use my real rentals
						</Button>
					</div>
				{:else}
					<p class="text-sm font-semibold">My real rentals</p>
					<p class="mt-1 text-xs text-muted-foreground">
						You're all set up with your real rentals — the example data is gone and everything here
						is real.
					</p>
				{/if}
			</div>
		</div>
	</div>
{/if}

<!-- Shared Go Live confirm dialog (irreversible, type-to-confirm) -->
<Dialog.Root open={dialogOpen} onOpenChange={(v) => { if (!v) closeDialog(); }}>
	<Dialog.Content data-testid="go-live-dialog" class="max-w-md">
		<Dialog.Header>
			<Dialog.Title class="flex items-center gap-2">
				<TriangleAlert class="h-5 w-5 text-warning" />
				Switch to my real rentals — this can't be undone
			</Dialog.Title>
			<Dialog.Description>
				This <strong class="font-semibold text-foreground">permanently deletes all the example
				data</strong> in this account and switches it over to your real rentals for good. You'll start
				with a clean account. There's no way back to the example data afterward.
			</Dialog.Description>
		</Dialog.Header>

		<div class="space-y-2">
			<label for="go-live-confirm-input" class="block text-xs text-muted-foreground">
				Type <span class="font-mono font-semibold text-foreground">{CONFIRM_PHRASE}</span> to confirm.
			</label>
			<Input
				id="go-live-confirm-input"
				bind:value={confirmText}
				placeholder={CONFIRM_PHRASE}
				autocomplete="off"
				disabled={goLiveMutation.isPending}
				data-testid="go-live-confirm-input"
				onkeydown={(e) => { if (e.key === 'Enter') confirmGoLive(); }}
			/>
		</div>

		<Dialog.Footer>
			<Button
				variant="outline"
				onclick={closeDialog}
				disabled={goLiveMutation.isPending}
				data-testid="go-live-cancel"
			>
				Cancel
			</Button>
			<Button
				class="gap-1.5 bg-warning text-warning-foreground hover:bg-[color-mix(in_oklab,var(--warning)_88%,black)] disabled:opacity-50"
				onclick={confirmGoLive}
				disabled={!confirmed || goLiveMutation.isPending}
				data-testid="go-live-confirm"
			>
				{#if goLiveMutation.isPending}
					<Loader2 class="h-4 w-4 animate-spin" />
					Switching over…
				{:else}
					<Rocket class="h-4 w-4" />
					Yes, switch to my real rentals
				{/if}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
