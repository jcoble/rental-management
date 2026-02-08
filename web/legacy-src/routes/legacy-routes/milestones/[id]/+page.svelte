<script lang="ts">
	import { page } from '$app/stores';
	import { goto } from '$app/navigation';
	import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
	import { milestones as milestonesApi } from '$lib/api/endpoints/milestones';
	import { phases as phasesApi } from '$lib/api/endpoints/phases';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { formatDate } from '$lib/utils/date';
	import { ArrowLeft, Pencil, Trash2, Check, XIcon } from '@lucide/svelte';

	const queryClient = useQueryClient();
	let id = $derived(Number($page.params.id));

	const milestoneQuery = createQuery(() => ({
		queryKey: ['milestones', id],
		queryFn: () => milestonesApi.get(id),
	}));

	const phasesQuery = createQuery(() => ({
		queryKey: ['phases', 'all', id],
		queryFn: () => phasesApi.list(id),
		enabled: !!milestoneQuery.data,
	}));

	let sortedPhases = $derived(
		[...(phasesQuery.data || [])].sort((a, b) => a.phaseNumber - b.phaseNumber)
	);

	// Edit mode
	let editing = $state(false);
	let editName = $state('');
	let editDescription = $state('');
	let editVersion = $state('');
	let editTargetDate = $state('');

	function startEditing() {
		if (!milestoneQuery.data) return;
		editName = milestoneQuery.data.name;
		editDescription = milestoneQuery.data.description || '';
		editVersion = milestoneQuery.data.version || '';
		editTargetDate = milestoneQuery.data.targetDate?.split('T')[0] || '';
		editing = true;
	}

	function cancelEditing() {
		editing = false;
	}

	const updateMutation = createMutation(() => ({
		mutationFn: (data: { name: string; description?: string; version?: string; targetDate?: string }) =>
			milestonesApi.update(id, data),
		onSuccess: () => {
			queryClient.invalidateQueries({ queryKey: ['milestones', id] });
			editing = false;
		},
	}));

	function saveEdits() {
		if (!editName.trim()) return;
		updateMutation.mutate({
			name: editName.trim(),
			description: editDescription.trim() || undefined,
			version: editVersion.trim() || undefined,
			targetDate: editTargetDate || undefined,
		});
	}

	// Status transitions
	const statusTransitions: Record<string, string[]> = {
		Planning: ['InProgress', 'Cancelled'],
		InProgress: ['Completed', 'OnHold', 'Cancelled'],
		OnHold: ['InProgress', 'Cancelled'],
		Completed: [],
		Cancelled: ['Planning'],
	};

	const statusMutation = createMutation(() => ({
		mutationFn: (status: string) => milestonesApi.changeStatus(id, status),
		onSuccess: () => {
			queryClient.invalidateQueries({ queryKey: ['milestones', id] });
			queryClient.invalidateQueries({ queryKey: ['milestones'] });
		},
	}));

	function handleStatusChange(e: Event) {
		const newStatus = (e.target as HTMLSelectElement).value;
		if (newStatus === milestoneQuery.data?.status) return;
		if (newStatus === 'Completed' || newStatus === 'Cancelled') {
			if (!window.confirm(`Are you sure you want to mark this milestone as ${newStatus}?`)) {
				(e.target as HTMLSelectElement).value = milestoneQuery.data?.status || '';
				return;
			}
		}
		statusMutation.mutate(newStatus);
	}

	// Delete
	const deleteMutation = createMutation(() => ({
		mutationFn: () => milestonesApi.delete(id),
		onSuccess: () => goto('/milestones'),
		onError: (error: any) => {
			if (error?.status === 409 || error?.response?.status === 409) {
				alert('Cannot delete: milestone still has phases');
			}
		},
	}));

	function handleDelete() {
		if (!window.confirm('Delete milestone? Delete all phases first if it has any.')) return;
		deleteMutation.mutate();
	}

	// Status display labels
	const statusLabels: Record<string, string> = {
		Planning: 'Planning',
		InProgress: 'In Progress',
		OnHold: 'On Hold',
		Completed: 'Completed',
		Cancelled: 'Cancelled',
	};
</script>

<svelte:head>
	<title>{milestoneQuery.data?.name || 'Milestone'} - Lifecycle Tracker</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6">
	{#if milestoneQuery.isLoading}
		<p class="text-text-tertiary">Loading milestone...</p>
	{:else if milestoneQuery.data}
		{@const milestone = milestoneQuery.data}
		{@const transitions = statusTransitions[milestone.status] || []}

		<a href="/milestones" class="mb-4 inline-flex items-center gap-1 text-sm text-text-tertiary hover:text-text-secondary">
			<ArrowLeft class="h-3.5 w-3.5" />
			All Milestones
		</a>

		<div class="mb-6">
			{#if editing}
				<div class="space-y-3 rounded-lg border border-border bg-surface p-5">
					<div>
						<label for="edit-name" class="mb-1 block text-xs text-text-tertiary">Name</label>
						<input
							id="edit-name"
							type="text"
							bind:value={editName}
							class="w-full rounded-md border border-border bg-bg px-3 py-1.5 text-sm text-text-primary focus:border-accent focus:outline-none"
						/>
					</div>
					<div>
						<label for="edit-desc" class="mb-1 block text-xs text-text-tertiary">Description</label>
						<textarea
							id="edit-desc"
							bind:value={editDescription}
							rows={3}
							class="w-full resize-none rounded-md border border-border bg-bg px-3 py-1.5 text-sm text-text-primary focus:border-accent focus:outline-none"
						></textarea>
					</div>
					<div>
						<label for="edit-version" class="mb-1 block text-xs text-text-tertiary">Version</label>
						<input
							id="edit-version"
							type="text"
							bind:value={editVersion}
							class="w-full rounded-md border border-border bg-bg px-3 py-1.5 text-sm font-mono text-text-primary focus:border-accent focus:outline-none"
							placeholder="e.g., 1.0.0"
						/>
					</div>
					<div>
						<label for="edit-target" class="mb-1 block text-xs text-text-tertiary">Target Date</label>
						<input
							id="edit-target"
							type="date"
							bind:value={editTargetDate}
							class="w-full rounded-md border border-border bg-bg px-3 py-1.5 text-sm text-text-primary focus:border-accent focus:outline-none"
						/>
					</div>
					<div class="flex items-center gap-2 pt-1">
						<button
							onclick={saveEdits}
							disabled={!editName.trim() || updateMutation.isPending}
							class="flex items-center gap-1 rounded-md bg-accent px-3 py-1.5 text-sm text-white transition-colors hover:bg-accent-hover disabled:opacity-50"
						>
							<Check class="h-3.5 w-3.5" />
							Save
						</button>
						<button
							onclick={cancelEditing}
							class="flex items-center gap-1 rounded-md border border-border px-3 py-1.5 text-sm text-text-secondary transition-colors hover:bg-surface-hover"
						>
							<XIcon class="h-3.5 w-3.5" />
							Cancel
						</button>
					</div>
				</div>
			{:else}
				<div class="flex items-center gap-3">
					<h1 class="text-2xl font-bold text-text-primary">{milestone.name}</h1>
					{#if milestone.version}
						<span class="rounded-md bg-surface px-2 py-0.5 font-mono text-xs text-text-secondary">v{milestone.version}</span>
					{/if}

					<!-- Status select -->
					{#if transitions.length > 0}
						<select
							value={milestone.status}
							onchange={handleStatusChange}
							class="rounded-md border border-border bg-bg px-2 py-1 text-xs text-text-primary focus:border-accent focus:outline-none"
						>
							<option value={milestone.status}>{statusLabels[milestone.status] || milestone.status}</option>
							{#each transitions as t}
								<option value={t}>{statusLabels[t] || t}</option>
							{/each}
						</select>
					{:else}
						<StatusBadge status={milestone.status} />
					{/if}

					<button
						onclick={startEditing}
						class="rounded p-1 text-text-tertiary transition-colors hover:bg-surface-hover hover:text-text-primary"
						title="Edit milestone"
					>
						<Pencil class="h-3.5 w-3.5" />
					</button>
					<button
						onclick={handleDelete}
						class="rounded p-1 text-text-tertiary transition-colors hover:bg-danger/10 hover:text-danger"
						title="Delete milestone"
					>
						<Trash2 class="h-3.5 w-3.5" />
					</button>
				</div>

				{#if milestone.description}
					<p class="mt-2 text-sm text-text-secondary">{milestone.description}</p>
				{/if}

				<div class="mt-3 flex items-center gap-4 text-xs text-text-tertiary">
					<span>Created: {formatDate(milestone.createdAt)}</span>
					{#if milestone.targetDate}
						<span>Target: {formatDate(milestone.targetDate)}</span>
					{/if}
					<span>Started: {milestone.startedAt ? formatDate(milestone.startedAt) : '--'}</span>
					<span>Completed: {milestone.completedAt ? formatDate(milestone.completedAt) : '--'}</span>
				</div>
			{/if}
		</div>

		<!-- Phases -->
		<div>
			<h2 class="mb-3 text-sm font-semibold text-text-primary">
				Phases ({phasesQuery.data?.length || 0})
			</h2>

			{#if phasesQuery.isLoading}
				<p class="text-text-tertiary">Loading phases...</p>
			{:else if sortedPhases.length > 0}
				<div class="space-y-3">
					{#each sortedPhases as phase, i}
						<a
							href="/phases/{phase.id}"
							class="group flex gap-4 rounded-lg border border-border bg-surface p-4 transition-colors hover:border-border-hover"
						>
							<div class="flex flex-col items-center">
								<div class="flex h-8 w-8 shrink-0 items-center justify-center rounded-full border border-border text-sm font-mono {phase.status === 'Completed' ? 'bg-success/20 text-success border-success/30' : phase.status === 'InProgress' ? 'bg-accent/20 text-accent border-accent/30' : 'text-text-tertiary'}">
									{phase.phaseNumber}
								</div>
								{#if i < sortedPhases.length - 1}
									<div class="mt-1 h-full w-px bg-border"></div>
								{/if}
							</div>

							<div class="flex-1 min-w-0">
								<div class="mb-1 flex items-center gap-2">
									<h3 class="font-medium text-text-primary group-hover:text-accent">{phase.name}</h3>
									<StatusBadge status={phase.status} />
								</div>
								{#if phase.goal}
									<p class="text-sm text-text-secondary line-clamp-2">{phase.goal}</p>
								{/if}
							</div>
						</a>
					{/each}
				</div>
			{:else}
				<div class="rounded-lg border-2 border-dashed border-border p-8 text-center">
					<p class="text-sm text-text-tertiary">No phases yet</p>
				</div>
			{/if}
		</div>
	{/if}
</div>
