<script lang="ts">
	import { createMutation } from '@tanstack/svelte-query';
	import { milestones as milestonesApi } from '$lib/api/endpoints/milestones';
	import type { Milestone } from '$lib/types';
	import { XIcon, Check } from '@lucide/svelte';

	let {
		projectId,
		onCreated,
		onClose
	}: {
		projectId: number;
		onCreated: () => void;
		onClose: () => void;
	} = $props();

	let name = $state('');
	let description = $state('');
	let version = $state('');
	let targetDate = $state('');

	const createMut = createMutation(() => ({
		mutationFn: (data: Partial<Milestone>) => milestonesApi.create(projectId, data),
		onSuccess: () => {
			onCreated();
			onClose();
		},
	}));

	function submit() {
		if (!name.trim()) return;
		createMut.mutate({
			name: name.trim(),
			description: description.trim() || undefined,
			version: version.trim() || undefined,
			targetDate: targetDate || undefined,
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
			<h2 class="text-sm font-semibold text-text-primary">New Milestone</h2>
			<button
				onclick={onClose}
				class="rounded p-1 text-text-tertiary transition-colors hover:bg-surface-hover hover:text-text-primary"
			>
				<XIcon class="h-4 w-4" />
			</button>
		</div>

		<div class="space-y-3">
			<div>
				<label for="new-milestone-name" class="mb-1 block text-xs text-text-tertiary">Name *</label>
				<!-- svelte-ignore a11y_autofocus -->
				<input
					id="new-milestone-name"
					type="text"
					bind:value={name}
					autofocus
					onkeydown={(e) => { if (e.key === 'Enter') submit(); }}
					class="w-full rounded-md border border-border bg-bg px-3 py-1.5 text-sm text-text-primary focus:border-accent focus:outline-none"
					placeholder="Milestone name"
				/>
			</div>
			<div>
				<label for="new-milestone-desc" class="mb-1 block text-xs text-text-tertiary">Description</label>
				<textarea
					id="new-milestone-desc"
					bind:value={description}
					rows={3}
					class="w-full resize-none rounded-md border border-border bg-bg px-3 py-1.5 text-sm text-text-primary focus:border-accent focus:outline-none"
					placeholder="What does this milestone deliver?"
				></textarea>
			</div>
			<div>
				<label for="new-milestone-version" class="mb-1 block text-xs text-text-tertiary">Version</label>
				<input
					id="new-milestone-version"
					type="text"
					bind:value={version}
					class="w-full rounded-md border border-border bg-bg px-3 py-1.5 text-sm font-mono text-text-primary focus:border-accent focus:outline-none"
					placeholder="e.g., 1.0.0"
				/>
			</div>
			<div>
				<label for="new-milestone-target" class="mb-1 block text-xs text-text-tertiary">Target Date</label>
				<input
					id="new-milestone-target"
					type="date"
					bind:value={targetDate}
					class="w-full rounded-md border border-border bg-bg px-3 py-1.5 text-sm text-text-primary focus:border-accent focus:outline-none"
				/>
			</div>
		</div>

		<div class="mt-4 flex items-center gap-2">
			<button
				onclick={submit}
				disabled={!name.trim() || createMut.isPending}
				class="flex items-center gap-1 rounded-md bg-accent px-3 py-1.5 text-sm text-white transition-colors hover:bg-accent-hover disabled:opacity-50"
			>
				<Check class="h-3.5 w-3.5" />
				Create Milestone
			</button>
			<button
				onclick={onClose}
				class="rounded-md border border-border px-3 py-1.5 text-sm text-text-secondary transition-colors hover:bg-surface-hover"
			>
				Cancel
			</button>
		</div>

		{#if createMut.isError}
			<p class="mt-2 text-xs text-danger">Failed to create milestone. Please try again.</p>
		{/if}
	</div>
</div>
