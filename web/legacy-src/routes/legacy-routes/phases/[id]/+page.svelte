<script lang="ts">
	import { page } from '$app/stores';
	import { goto } from '$app/navigation';
	import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
	import { phases as phasesApi } from '$lib/api/endpoints/phases';
	import { tasks as tasksApi } from '$lib/api/endpoints/tasks';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import BoardCard from '$lib/components/board/BoardCard.svelte';
	import TaskDetail from '$lib/components/tasks/TaskDetail.svelte';
	import type { Task, PhaseStatus } from '$lib/types';
	import { marked } from 'marked';
	import { formatDate } from '$lib/utils/date';
	import { ArrowLeft, Pencil, Trash2, Save, X } from '@lucide/svelte';

	const queryClient = useQueryClient();
	let id = $derived(Number($page.params.id));

	const phaseQuery = createQuery(() => ({
		queryKey: ['phases', id],
		queryFn: () => phasesApi.get(id),
	}));

	const tasksQuery = createQuery(() => ({
		queryKey: ['tasks', { phaseId: id }],
		queryFn: () => tasksApi.list({ phaseId: String(id) }),
	}));

	let selectedTask = $state<Task | null>(null);
	let renderedCriteria = $derived(
		phaseQuery.data?.successCriteria
			? (marked.parse(phaseQuery.data.successCriteria) as string)
			: ''
	);

	// Edit mode
	let editing = $state(false);
	let editName = $state('');
	let editGoal = $state('');
	let editDescription = $state('');
	let editSuccessCriteria = $state('');

	function startEditing() {
		if (!phaseQuery.data) return;
		editName = phaseQuery.data.name;
		editGoal = phaseQuery.data.goal || '';
		editDescription = phaseQuery.data.description || '';
		editSuccessCriteria = phaseQuery.data.successCriteria || '';
		editing = true;
	}

	function cancelEditing() {
		editing = false;
	}

	const updateMutation = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => phasesApi.update(id, data),
		onSuccess: () => {
			queryClient.invalidateQueries({ queryKey: ['phases', id] });
			editing = false;
		},
	}));

	function savePhase() {
		if (!editName.trim()) return;
		updateMutation.mutate({
			name: editName.trim(),
			goal: editGoal.trim() || undefined,
			description: editDescription.trim() || undefined,
			successCriteria: editSuccessCriteria.trim() || undefined,
		});
	}

	// Status transitions
	const validTransitions: Record<string, PhaseStatus[]> = {
		NotStarted: ['Planning', 'Cancelled'],
		Planning: ['InProgress', 'Blocked', 'Cancelled'],
		InProgress: ['Review', 'Blocked', 'Cancelled'],
		Review: ['Completed', 'InProgress', 'Blocked', 'Cancelled'],
		Blocked: ['Planning', 'InProgress', 'Review', 'Cancelled'],
		Cancelled: ['NotStarted'],
	};

	const statusChangeMutation = createMutation(() => ({
		mutationFn: (status: string) => phasesApi.changeStatus(id, status),
		onSuccess: () => {
			queryClient.invalidateQueries({ queryKey: ['phases', id] });
			queryClient.invalidateQueries({ queryKey: ['phases', 'all'] });
		},
	}));

	function handleStatusChange(e: Event) {
		const newStatus = (e.target as HTMLSelectElement).value;
		if (newStatus === 'Completed' || newStatus === 'Cancelled') {
			if (!window.confirm(`Are you sure you want to mark this phase as ${newStatus}?`)) {
				// Reset the select
				(e.target as HTMLSelectElement).value = phaseQuery.data?.status || '';
				return;
			}
		}
		statusChangeMutation.mutate(newStatus);
	}

	// Delete
	const deleteMutation = createMutation(() => ({
		mutationFn: () => phasesApi.delete(id),
		onSuccess: () => goto('/phases'),
	}));

	function handleDelete() {
		if (!phaseQuery.data) return;
		if (window.confirm(`Delete phase '${phaseQuery.data.name}'? Tasks in this phase will be unassigned but not deleted.`)) {
			deleteMutation.mutate();
		}
	}

	function handleTaskClick(task: Task) {
		selectedTask = task;
	}

	function handleTaskUpdate() {
		selectedTask = null;
		queryClient.invalidateQueries({ queryKey: ['tasks', { phaseId: id }] });
	}
</script>

<svelte:head>
	<title>{phaseQuery.data?.name || 'Phase'} - Lifecycle Tracker</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6">
	{#if phaseQuery.isLoading}
		<p class="text-text-tertiary">Loading phase...</p>
	{:else if phaseQuery.data}
		{@const phase = phaseQuery.data}

		<a href="/phases" class="mb-4 inline-flex items-center gap-1 text-sm text-text-tertiary hover:text-text-secondary">
			<ArrowLeft class="h-3.5 w-3.5" />
			All Phases
		</a>

		{#if editing}
			<div class="mb-6 rounded-lg border border-border bg-surface p-5">
				<h2 class="mb-3 text-sm font-semibold text-text-primary">Edit Phase</h2>
				<div class="space-y-3">
					<div>
						<label for="edit-phase-name" class="mb-1 block text-xs text-text-tertiary">Name</label>
						<input
							id="edit-phase-name"
							type="text"
							bind:value={editName}
							class="w-full rounded-md border border-border bg-bg px-3 py-1.5 text-sm text-text-primary focus:border-accent focus:outline-none"
						/>
					</div>
					<div>
						<label for="edit-phase-goal" class="mb-1 block text-xs text-text-tertiary">Goal</label>
						<textarea
							id="edit-phase-goal"
							bind:value={editGoal}
							rows={2}
							class="w-full resize-none rounded-md border border-border bg-bg px-3 py-1.5 text-sm text-text-primary focus:border-accent focus:outline-none"
						></textarea>
					</div>
					<div>
						<label for="edit-phase-desc" class="mb-1 block text-xs text-text-tertiary">Description</label>
						<textarea
							id="edit-phase-desc"
							bind:value={editDescription}
							rows={2}
							class="w-full resize-none rounded-md border border-border bg-bg px-3 py-1.5 text-sm text-text-primary focus:border-accent focus:outline-none"
						></textarea>
					</div>
					<div>
						<label for="edit-phase-criteria" class="mb-1 block text-xs text-text-tertiary">Success Criteria</label>
						<textarea
							id="edit-phase-criteria"
							bind:value={editSuccessCriteria}
							rows={3}
							class="w-full resize-none rounded-md border border-border bg-bg px-3 py-1.5 text-sm text-text-primary focus:border-accent focus:outline-none"
							placeholder="Markdown supported"
						></textarea>
					</div>
					<div class="flex items-center gap-2 pt-1">
						<button
							onclick={savePhase}
							disabled={!editName.trim() || updateMutation.isPending}
							class="flex items-center gap-1 rounded-md bg-accent px-3 py-1.5 text-sm text-white transition-colors hover:bg-accent-hover disabled:opacity-50"
						>
							<Save class="h-3.5 w-3.5" />
							Save
						</button>
						<button
							onclick={cancelEditing}
							class="flex items-center gap-1 rounded-md border border-border px-3 py-1.5 text-sm text-text-secondary transition-colors hover:bg-surface-hover"
						>
							<X class="h-3.5 w-3.5" />
							Cancel
						</button>
					</div>
					{#if updateMutation.isError}
						<p class="text-xs text-danger">Failed to update phase. Please try again.</p>
					{/if}
				</div>
			</div>
		{:else}
			<div class="mb-6">
				<div class="flex items-center gap-3">
					<h1 class="text-2xl font-bold text-text-primary">{phase.name}</h1>
					{#if phase.status === 'Completed'}
						<StatusBadge status={phase.status} />
					{:else}
						{@const transitions = validTransitions[phase.status] || []}
						{#if transitions.length > 0}
							<select
								value={phase.status}
								onchange={handleStatusChange}
								class="rounded-md border border-border bg-bg px-2 py-1 text-sm text-text-primary focus:border-accent focus:outline-none"
							>
								<option value={phase.status}>{phase.status}</option>
								{#each transitions as s}
									<option value={s}>{s}</option>
								{/each}
							</select>
						{:else}
							<StatusBadge status={phase.status} />
						{/if}
					{/if}
					<button
						onclick={startEditing}
						class="rounded p-1 text-text-tertiary transition-colors hover:bg-surface-hover hover:text-text-primary"
						title="Edit phase"
					>
						<Pencil class="h-3.5 w-3.5" />
					</button>
					<button
						onclick={handleDelete}
						class="rounded p-1 text-text-tertiary transition-colors hover:bg-danger/10 hover:text-danger"
						title="Delete phase"
					>
						<Trash2 class="h-3.5 w-3.5" />
					</button>
				</div>
				{#if phase.goal}
					<p class="mt-2 text-sm text-text-secondary">{phase.goal}</p>
				{/if}
			</div>

			{#if renderedCriteria}
				<div class="mb-6 rounded-lg border border-border bg-surface p-4">
					<h2 class="mb-2 text-sm font-semibold text-text-primary">Success Criteria</h2>
					<div class="prose prose-invert prose-sm max-w-none text-text-secondary">
						{@html renderedCriteria}
					</div>
				</div>
			{/if}
		{/if}

		<div>
			<h2 class="mb-3 text-sm font-semibold text-text-primary">
				Tasks ({tasksQuery.data?.length || 0})
			</h2>
			{#if tasksQuery.isLoading}
				<p class="text-text-tertiary">Loading tasks...</p>
			{:else if tasksQuery.data && tasksQuery.data.length > 0}
				<div class="grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
					{#each tasksQuery.data as task}
						<BoardCard {task} onclick={handleTaskClick} />
					{/each}
				</div>
			{:else}
				<p class="text-sm text-text-tertiary">No tasks in this phase</p>
			{/if}
		</div>

		<!-- Metadata -->
		<div class="mt-8 border-t border-border pt-4 text-xs text-text-tertiary">
			<span>Phase #{phase.phaseNumber}</span>
			<span class="mx-2">|</span>
			<span>Created {formatDate(phase.createdAt)}</span>
			{#if phase.startedAt}
				<span class="mx-2">|</span>
				<span>Started {formatDate(phase.startedAt)}</span>
			{/if}
			{#if phase.completedAt}
				<span class="mx-2">|</span>
				<span>Completed {formatDate(phase.completedAt)}</span>
			{/if}
		</div>
	{/if}
</div>

{#if selectedTask}
	<TaskDetail
		task={selectedTask}
		onclose={() => (selectedTask = null)}
		onupdate={handleTaskUpdate}
		ondelete={() => { selectedTask = null; queryClient.invalidateQueries({ queryKey: ['tasks', { phaseId: id }] }); }}
	/>
{/if}
