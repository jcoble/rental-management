<script lang="ts">
	import { createMutation } from '@tanstack/svelte-query';
	import { portfolios } from '$lib/api/endpoints/portfolios';
	import type { Portfolio } from '$lib/types';
	import { XIcon, Check } from '@lucide/svelte';
	import TimeZoneSelect from '$lib/components/shared/TimeZoneSelect.svelte';
	import { guessBrowserTimeZone } from '$lib/data/timezones';

	let { onCreated, onClose }: { onCreated: (p: Portfolio) => void; onClose: () => void } = $props();

	let name = $state('');
	let description = $state('');
	let managementCompanyName = $state('');
	let timeZone = $state(guessBrowserTimeZone());

	const createMut = createMutation(() => ({
		mutationFn: (data: Partial<Portfolio>) => portfolios.create(data),
		onSuccess: (portfolio: Portfolio) => onCreated(portfolio),
	}));

	function submit() {
		if (!name.trim()) return;
		createMut.mutate({
			name: name.trim(),
			description: description.trim() || undefined,
			managementCompanyName: managementCompanyName.trim() || name.trim(),
			timeZone,
		});
	}
</script>

<!-- svelte-ignore a11y_click_events_have_key_events -->
<!-- svelte-ignore a11y_no_static_element_interactions -->
<div
	class="fixed inset-0 z-50 flex items-center justify-center bg-black/50"
	onclick={(e) => { if (e.target === e.currentTarget) onClose(); }}
>
	<div class="mx-4 w-full max-w-md rounded-lg border border-border bg-card p-5 shadow-xl">
		<div class="mb-4 flex items-center justify-between">
			<h2 class="text-sm font-semibold text-foreground">New Portfolio</h2>
			<button
				onclick={onClose}
				class="rounded p-1 text-muted-foreground transition-colors hover:bg-secondary hover:text-foreground"
			>
				<XIcon class="h-4 w-4" />
			</button>
		</div>

		<div class="space-y-3">
			<div>
				<label for="new-portfolio-name" class="mb-1 block text-xs text-muted-foreground">Portfolio Name *</label>
				<input
					id="new-portfolio-name"
					type="text"
					bind:value={name}
					onkeydown={(e) => { if (e.key === 'Enter') submit(); }}
					class="w-full rounded-md border border-border bg-background px-3 py-1.5 text-sm text-foreground focus:border-ring focus:outline-none"
					placeholder="Downtown Portfolio"
				/>
			</div>
			<div>
				<label for="new-portfolio-company" class="mb-1 block text-xs text-muted-foreground">Management Company</label>
				<input
					id="new-portfolio-company"
					type="text"
					bind:value={managementCompanyName}
					class="w-full rounded-md border border-border bg-background px-3 py-1.5 text-sm text-foreground focus:border-ring focus:outline-none"
					placeholder="Acme Property Management"
				/>
			</div>
			<div>
				<label for="new-portfolio-timezone" class="mb-1 block text-xs text-muted-foreground">Time zone</label>
				<TimeZoneSelect id="new-portfolio-timezone" testid="new-portfolio-timezone" bind:value={timeZone} />
			</div>
			<div>
				<label for="new-portfolio-desc" class="mb-1 block text-xs text-muted-foreground">Description</label>
				<textarea
					id="new-portfolio-desc"
					bind:value={description}
					rows={2}
					class="w-full resize-none rounded-md border border-border bg-background px-3 py-1.5 text-sm text-foreground focus:border-ring focus:outline-none"
					placeholder="Scope, regions, and goals"
				></textarea>
			</div>
		</div>

		<div class="mt-4 flex items-center gap-2">
			<button
				onclick={submit}
				disabled={!name.trim() || createMut.isPending}
				class="flex items-center gap-1 rounded-md bg-primary px-3 py-1.5 text-sm text-white transition-colors hover:bg-primary/90 disabled:opacity-50"
			>
				<Check class="h-3.5 w-3.5" />
				Create Portfolio
			</button>
			<button
				onclick={onClose}
				class="rounded-md border border-border px-3 py-1.5 text-sm text-muted-foreground transition-colors hover:bg-secondary"
			>
				Cancel
			</button>
		</div>

		{#if createMut.isError}
			<p class="mt-2 text-xs text-destructive">Failed to create portfolio. Please try again.</p>
		{/if}
	</div>
</div>
