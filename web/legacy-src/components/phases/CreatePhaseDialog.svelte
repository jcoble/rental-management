<script lang="ts">
	import { createMutation } from '@tanstack/svelte-query';
	import { phases as phasesApi } from '$lib/api/endpoints/phases';
	import type { Phase } from '$lib/types';
	import { XIcon, Check } from '@lucide/svelte';

	let {
		milestoneId,
		milestoneName,
		onCreated,
		onClose
	}: {
		milestoneId: number;
		milestoneName?: string;
		onCreated: () => void;
		onClose: () => void;
	} = $props();

	let name = $state('');
	let goal = $state('');
	let description = $state('');
	let successCriteria = $state('');

	const createMut = createMutation(() => ({
		mutationFn: (data: Partial<Phase>) => phasesApi.create(milestoneId, data),
		onSuccess: () => {
			onCreated();
			onClose();
		},
	}));

	function submit() {
		if (!name.trim()) return;
		createMut.mutate({
			name: name.trim(),
			goal: goal.trim() || undefined,
			description: description.trim() || undefined,
			successCriteria: successCriteria.trim() || undefined,
		});
	}
</script>

<svelte:window onkeydown={(e) => { if (e.key === 'Escape') onClose(); }} />

<!-- svelte-ignore a11y_click_events_have_key_events -->
<!-- svelte-ignore a11y_no_static_element_interactions -->
<div
	class="fixed inset-0 z-50 flex items-center justify-center bg-black/50"
	onclick={(e) => { if (e.target === e.currentTarget) onClose(); }}
>
	<div class="mx-4 w-full max-w-md rounded-lg border border-border bg-surface p-5 shadow-xl">
		<div class="mb-4 flex items-center justify-between">
			<div>
				<h2 class="text-sm font-semibold text-text-primary">New Phase</h2>
				{#if milestoneName}
					<p class="mt-0.5 text-xs text-text-tertiary">Creating phase in: {milestoneName}</p>
				{/if}
			</div>
			<button
				onclick={onClose}
				class="rounded p-1 text-text-tertiary transition-colors hover:bg-surface-hover hover:text-text-primary"
			>
				<XIcon class="h-4 w-4" />
			</button>
		</div>

		<div class="space-y-3">
			<div>
				<label for="new-phase-name" class="mb-1 block text-xs text-text-tertiary">Name *</label>
				<!-- svelte-ignore a11y_autofocus -->
				<input
					id="new-phase-name"
					type="text"
					bind:value={name}
					autofocus
					onkeydown={(e) => { if (e.key === 'Enter') submit(); }}
					class="w-full rounded-md border border-border bg-bg px-3 py-1.5 text-sm text-text-primary focus:border-accent focus:outline-none"
					placeholder="Phase name"
				/>
			</div>
			<div>
				<label for="new-phase-goal" class="mb-1 block text-xs text-text-tertiary">Goal</label>
				<textarea
					id="new-phase-goal"
					bind:value={goal}
					rows={2}
					class="w-full resize-none rounded-md border border-border bg-bg px-3 py-1.5 text-sm text-text-primary focus:border-accent focus:outline-none"
					placeholder="What should this phase achieve?"
				></textarea>
			</div>
			<div>
				<label for="new-phase-desc" class="mb-1 block text-xs text-text-tertiary">Description</label>
				<textarea
					id="new-phase-desc"
					bind:value={description}
					rows={2}
					class="w-full resize-none rounded-md border border-border bg-bg px-3 py-1.5 text-sm text-text-primary focus:border-accent focus:outline-none"
					placeholder="Additional details..."
				></textarea>
			</div>
			<div>
				<label for="new-phase-criteria" class="mb-1 block text-xs text-text-tertiary">Success Criteria</label>
				<textarea
					id="new-phase-criteria"
					bind:value={successCriteria}
					rows={3}
					class="w-full resize-none rounded-md border border-border bg-bg px-3 py-1.5 text-sm text-text-primary focus:border-accent focus:outline-none"
					placeholder="Markdown supported"
				></textarea>
			</div>
		</div>

		<div class="mt-4 flex items-center gap-2">
			<button
				onclick={submit}
				disabled={!name.trim() || createMut.isPending}
				class="flex items-center gap-1 rounded-md bg-accent px-3 py-1.5 text-sm text-white transition-colors hover:bg-accent-hover disabled:opacity-50"
			>
				<Check class="h-3.5 w-3.5" />
				Create Phase
			</button>
			<button
				onclick={onClose}
				class="rounded-md border border-border px-3 py-1.5 text-sm text-text-secondary transition-colors hover:bg-surface-hover"
			>
				Cancel
			</button>
		</div>

		{#if createMut.isError}
			<p class="mt-2 text-xs text-danger">Failed to create phase. Please try again.</p>
		{/if}
	</div>
</div>
