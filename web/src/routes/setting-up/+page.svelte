<script lang="ts">
	import { onMount } from 'svelte';
	import { browser } from '$app/environment';
	import { goto, invalidateAll } from '$app/navigation';
	import { useQueryClient } from '@tanstack/svelte-query';
	import { FlaskConical, Check, Loader2, TriangleAlert, Building2, Users, Wallet, Wrench, Sparkles } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import { portfolios } from '$lib/api/endpoints/portfolios';
	import { apiErrorMessage } from '$lib/utils/toast';

	const queryClient = useQueryClient();

	// Staged labels for the deliberate "setting up" theater. The total is intentionally ~20s so it
	// reads as real work; the actual server seed is fast and runs concurrently underneath.
	const STAGES = [
		{ label: 'Creating sample properties…', icon: Building2, ms: 4200 },
		{ label: 'Adding tenants & leases…', icon: Users, ms: 4200 },
		{ label: 'Generating 6 months of payment history…', icon: Wallet, ms: 4600 },
		{ label: 'Seeding maintenance & work orders…', icon: Wrench, ms: 4200 },
		{ label: 'Finishing up…', icon: Sparkles, ms: 2800 }
	];
	const TOTAL_MS = STAGES.reduce((sum, s) => sum + s.ms, 0);

	let stageIndex = $state(0);
	let progress = $state(0); // 0..100, smoothly driven by rAF
	let animationDone = $state(false);
	let seedDone = $state(false);
	let seedError = $state<string | null>(null);

	const currentLabel = $derived(STAGES[Math.min(stageIndex, STAGES.length - 1)].label);

	// Fire the real seed in parallel with the animation. We only navigate once BOTH the animation has
	// finished AND the seed succeeded — so the dashboard always has data when we land.
	async function runSeed() {
		try {
			await portfolios.onboardingChoice('sandbox');
			seedDone = true;
			maybeFinish();
		} catch (err) {
			seedError = apiErrorMessage(err, 'We could not finish setting up your sandbox.');
		}
	}

	async function maybeFinish() {
		if (!animationDone || !seedDone || seedError) return;
		// Demo data now exists server-side; refresh server load (clears the first-login gate) and the
		// client query cache (so the sandbox banner + dashboard render the seeded state), then land.
		await invalidateAll();
		await queryClient.invalidateQueries();
		await goto('/');
	}

	function retry() {
		seedError = null;
		void runSeed();
	}

	onMount(() => {
		void runSeed();

		const reduce =
			browser && window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;

		if (reduce) {
			// Honor reduced-motion: skip the long theater, jump to the end (still waits on the seed).
			progress = 100;
			stageIndex = STAGES.length - 1;
			animationDone = true;
			void maybeFinish();
			return;
		}

		const start = performance.now();
		let raf = 0;
		const tick = (now: number) => {
			const elapsed = now - start;
			progress = Math.min(100, (elapsed / TOTAL_MS) * 100);

			// Advance the stage label by accumulated time.
			let acc = 0;
			let idx = 0;
			for (let i = 0; i < STAGES.length; i++) {
				acc += STAGES[i].ms;
				if (elapsed < acc) {
					idx = i;
					break;
				}
				idx = i;
			}
			stageIndex = idx;

			if (elapsed >= TOTAL_MS) {
				progress = 100;
				animationDone = true;
				void maybeFinish();
				return;
			}
			raf = requestAnimationFrame(tick);
		};
		raf = requestAnimationFrame(tick);
		return () => cancelAnimationFrame(raf);
	});
</script>

<svelte:head>
	<title>Setting up your sandbox - Rental Command</title>
</svelte:head>

<div class="relative flex min-h-dvh w-full items-center justify-center overflow-hidden bg-background px-5">
	<div
		class="pointer-events-none absolute inset-0 -z-10 bg-[radial-gradient(55%_45%_at_50%_30%,color-mix(in_oklab,var(--warning)_12%,transparent),transparent)]"
	></div>

	<div class="w-full max-w-md text-center">
		{#if seedError}
			<!-- Failure: the animation is irrelevant; let them retry. -->
			<div class="flex flex-col items-center" data-testid="setting-up-error">
				<span
					class="mb-5 flex h-14 w-14 items-center justify-center rounded-2xl bg-destructive/10 text-destructive"
				>
					<TriangleAlert class="h-7 w-7" />
				</span>
				<h1 class="text-xl font-semibold text-foreground">Setup hit a snag</h1>
				<p class="mt-2 text-sm text-muted-foreground">{seedError}</p>
				<div class="mt-6 flex items-center gap-2">
					<Button onclick={retry} data-testid="setting-up-retry">Try again</Button>
					<Button variant="outline" onclick={() => goto('/choose-setup')}>Back</Button>
				</div>
			</div>
		{:else}
			<div class="flex flex-col items-center" data-testid="setting-up-progress">
				<span
					class="mb-6 flex h-16 w-16 items-center justify-center rounded-2xl bg-warning/15 text-warning"
				>
					<FlaskConical class="h-8 w-8 animate-pulse" />
				</span>
				<h1 class="text-xl font-semibold tracking-tight text-foreground sm:text-2xl">
					Setting up your sandbox…
				</h1>
				<p class="mt-2 text-sm text-muted-foreground">
					We're filling your demo portfolio with realistic sample data so you can explore everything.
				</p>

				<!-- Progress bar -->
				<div class="mt-8 w-full">
					<div class="h-2 w-full overflow-hidden rounded-full bg-muted">
						<div
							class="h-full rounded-full bg-warning transition-[width] duration-150 ease-linear"
							style="width: {progress}%"
							data-testid="setting-up-bar"
						></div>
					</div>
					<div class="mt-3 flex min-h-5 items-center justify-center gap-2 text-sm font-medium text-foreground">
						{#if animationDone && !seedDone}
							<Loader2 class="h-4 w-4 animate-spin text-warning" />
							<span>Almost there…</span>
						{:else}
							<Loader2 class="h-4 w-4 animate-spin text-warning" />
							<span data-testid="setting-up-stage">{currentLabel}</span>
						{/if}
					</div>
				</div>

				<!-- Stage checklist -->
				<ul class="mt-8 w-full space-y-2 text-left">
					{#each STAGES as stage, i (stage.label)}
						{@const SIcon = stage.icon}
						{@const complete = i < stageIndex || (animationDone && seedDone)}
						{@const active = i === stageIndex && !complete}
						<li
							class="flex items-center gap-3 rounded-xl border px-3 py-2 text-sm transition-colors {complete
								? 'border-transparent bg-muted/40 text-muted-foreground'
								: active
									? 'border-warning/40 bg-warning/10 text-foreground'
									: 'border-transparent text-muted-foreground/60'}"
						>
							<span
								class="flex h-6 w-6 shrink-0 items-center justify-center rounded-full {complete
									? 'bg-warning/20 text-warning'
									: active
										? 'bg-warning/20 text-warning'
										: 'bg-muted text-muted-foreground/60'}"
							>
								{#if complete}
									<Check class="h-3.5 w-3.5" />
								{:else}
									<SIcon class="h-3.5 w-3.5" />
								{/if}
							</span>
							<span class="leading-tight">{stage.label}</span>
						</li>
					{/each}
				</ul>
			</div>
		{/if}
	</div>
</div>
