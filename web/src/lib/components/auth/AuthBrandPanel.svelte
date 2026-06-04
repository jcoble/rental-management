<script lang="ts">
	import { Building, ScanLine, Sparkles, ShieldCheck } from '@lucide/svelte';

	// Decorative split-panel shown beside the login/register card on wide screens.
	// Reinforces the flagship promise: "the computer does the typing for you."
	let { tagline = 'Property management that does the typing for you.' }: { tagline?: string } =
		$props();

	const points = [
		{ icon: ScanLine, text: 'Scan a lease, invoice or receipt — we extract the details.' },
		{ icon: Sparkles, text: 'Confirm a draft instead of filling out forms.' },
		{ icon: ShieldCheck, text: 'Rent, maintenance, and the books — one calm place.' }
	];
</script>

<div
	class="relative hidden overflow-hidden bg-[#0b0f1a] lg:flex lg:flex-col lg:justify-between lg:p-12"
>
	<!-- Ambient aurora wash -->
	<div
		class="animate-aurora pointer-events-none absolute inset-0 opacity-60"
		style="background-image: radial-gradient(60% 60% at 20% 20%, color-mix(in oklab, var(--primary) 28%, transparent), transparent), radial-gradient(50% 50% at 85% 75%, color-mix(in oklab, var(--accent) 24%, transparent), transparent);"
		aria-hidden="true"
	></div>
	<!-- Subtle grid -->
	<div
		class="pointer-events-none absolute inset-0 opacity-[0.07]"
		style="background-image: linear-gradient(var(--border) 1px, transparent 1px), linear-gradient(90deg, var(--border) 1px, transparent 1px); background-size: 36px 36px;"
		aria-hidden="true"
	></div>

	<div class="relative">
		<a href="/" class="flex items-center gap-2">
			<span
				class="flex h-9 w-9 items-center justify-center rounded-xl bg-primary/15 text-primary ring-1 ring-inset ring-primary/30"
			>
				<Building class="h-5 w-5" />
			</span>
			<span class="text-lg font-semibold tracking-tight text-white">Rental Command</span>
		</a>
	</div>

	<div class="relative max-w-sm">
		<h2 class="text-3xl font-semibold leading-tight tracking-tight text-white">
			{tagline}
		</h2>
		<ul class="mt-8 space-y-4">
			{#each points as point}
				<li class="flex items-start gap-3 text-sm text-white/70">
					<span
						class="mt-0.5 flex h-7 w-7 shrink-0 items-center justify-center rounded-lg bg-white/5 text-primary ring-1 ring-inset ring-white/10"
					>
						<point.icon class="h-4 w-4" />
					</span>
					<span>{point.text}</span>
				</li>
			{/each}
		</ul>
	</div>

	<div class="relative text-xs text-white/40">
		Built for landlords who run the business from their phone.
	</div>
</div>
