<script lang="ts">
	import { goto } from '$app/navigation';
	import { FlaskConical, Rocket, Check, Loader2, ArrowRight } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import BrandMark from '$lib/components/BrandMark.svelte';
	import { portfolios } from '$lib/api/endpoints/portfolios';
	import { showError, apiErrorMessage } from '$lib/utils/toast';
	import type { PageData } from './$types';

	let { data }: { data: PageData } = $props();

	// Which card is selected (for the highlight ring); null until the user picks.
	let selected = $state<'sandbox' | 'live' | null>(null);
	// Set while the Live choice is being recorded (Sandbox hands off to /setting-up immediately).
	let submitting = $state(false);

	const firstName = $derived((data.displayName ?? '').trim().split(/\s+/)[0] ?? '');

	function chooseSandbox() {
		if (submitting) return;
		selected = 'sandbox';
		// The seeding screen owns the API call + the ~20s "setting up" theater so the animation can
		// begin the instant we land there.
		void goto('/setting-up');
	}

	async function chooseLive() {
		if (submitting) return;
		selected = 'live';
		submitting = true;
		try {
			await portfolios.onboardingChoice('live');
			// Empty, real portfolio → straight into the property-setup wizard.
			await goto('/onboarding');
		} catch (err) {
			submitting = false;
			selected = null;
			showError(apiErrorMessage(err, 'Could not set up your account. Please try again.'));
		}
	}
</script>

<svelte:head>
	<title>Choose how to start - Rental Command</title>
</svelte:head>

<div class="relative min-h-dvh w-full overflow-y-auto bg-background">
	<!-- soft brand gradient backdrop -->
	<div
		class="pointer-events-none absolute inset-0 -z-10 bg-[radial-gradient(60%_50%_at_50%_0%,color-mix(in_oklab,var(--primary)_12%,transparent),transparent)]"
	></div>

	<div class="mx-auto flex min-h-dvh max-w-4xl flex-col items-center justify-center px-5 py-12 sm:py-16">
		<!-- Brand + heading -->
		<div class="mb-10 flex flex-col items-center text-center">
			<BrandMark class="mb-5 h-12 w-12 shadow-md" alt="Rental Command" />
			<h1 class="text-2xl font-semibold tracking-tight text-foreground sm:text-3xl">
				{firstName ? `Welcome, ${firstName}` : 'Welcome to Rental Command'}
			</h1>
			<p class="mt-2 max-w-md text-sm text-muted-foreground sm:text-base">
				How would you like to begin? You can switch from the sample data to your real portfolio at
				any time.
			</p>
		</div>

		<!-- Two choices -->
		<div class="grid w-full gap-4 sm:grid-cols-2">
			<!-- Sandbox -->
			<button
				type="button"
				onclick={chooseSandbox}
				disabled={submitting}
				data-testid="choose-sandbox"
				aria-pressed={selected === 'sandbox'}
				class="group relative flex flex-col rounded-2xl border bg-card p-6 text-left transition-all hover:-translate-y-0.5 hover:shadow-lg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-warning disabled:pointer-events-none disabled:opacity-60 {selected ===
				'sandbox'
					? 'border-warning ring-2 ring-warning'
					: 'border-border hover:border-[color-mix(in_oklab,var(--warning)_50%,var(--border))]'}"
			>
				<span
					class="mb-4 flex h-11 w-11 items-center justify-center rounded-xl bg-warning/15 text-warning"
				>
					<FlaskConical class="h-5 w-5" />
				</span>
				<span class="text-base font-semibold text-foreground">Explore with sample data</span>
				<span
					class="mt-0.5 inline-flex w-fit items-center gap-1 rounded-full bg-warning/15 px-2 py-0.5 text-[11px] font-semibold uppercase tracking-wide text-warning"
				>
					Example data
				</span>
				<p class="mt-3 flex-1 text-sm text-muted-foreground">
					Jump into a fully loaded demo portfolio — properties, tenants, leases, payments and work
					orders — so you can try everything risk-free.
					<span class="font-medium text-foreground">
						It's fake sample data for exploring the app. Configured email and text providers can still send for testing.
					</span>
				</p>
				<span
					class="mt-5 inline-flex items-center gap-1.5 text-sm font-semibold text-warning"
				>
					{#if selected === 'sandbox'}
						<Loader2 class="h-4 w-4 animate-spin" /> Setting up…
					{:else}
						Start exploring <ArrowRight class="h-4 w-4 transition-transform group-hover:translate-x-0.5" />
					{/if}
				</span>
			</button>

			<!-- Live -->
			<button
				type="button"
				onclick={chooseLive}
				disabled={submitting}
				data-testid="choose-live"
				aria-pressed={selected === 'live'}
				class="group relative flex flex-col rounded-2xl border bg-card p-6 text-left transition-all hover:-translate-y-0.5 hover:shadow-lg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary disabled:pointer-events-none disabled:opacity-60 {selected ===
				'live'
					? 'border-primary ring-2 ring-primary'
					: 'border-border hover:border-[color-mix(in_oklab,var(--primary)_50%,var(--border))]'}"
			>
				<span
					class="mb-4 flex h-11 w-11 items-center justify-center rounded-xl bg-primary/10 text-primary"
				>
					<Rocket class="h-5 w-5" />
				</span>
				<span class="text-base font-semibold text-foreground">Set up my real portfolio</span>
				<span
					class="mt-0.5 inline-flex w-fit items-center gap-1 rounded-full bg-primary/10 px-2 py-0.5 text-[11px] font-semibold uppercase tracking-wide text-primary"
				>
					My real rentals
				</span>
				<p class="mt-3 flex-1 text-sm text-muted-foreground">
					Start with a clean, empty account and add your own properties, tenants and leases. A quick
					guided setup walks you through the first steps.
					<span class="font-medium text-foreground">No sample data — this is the real thing.</span>
				</p>
				<span class="mt-5 inline-flex items-center gap-1.5 text-sm font-semibold text-primary">
					{#if selected === 'live' && submitting}
						<Loader2 class="h-4 w-4 animate-spin" /> Setting up…
					{:else}
						Set up my portfolio <ArrowRight
							class="h-4 w-4 transition-transform group-hover:translate-x-0.5"
						/>
					{/if}
				</span>
			</button>
		</div>

		<!-- reassurance footnote -->
		<p class="mt-8 flex items-center gap-1.5 text-xs text-muted-foreground">
			<Check class="h-3.5 w-3.5 text-primary" />
			Not sure? Start with the example data — switching to your real rentals later clears it and starts you clean.
		</p>
	</div>
</div>
