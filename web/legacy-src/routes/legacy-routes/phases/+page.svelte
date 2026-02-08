<script lang="ts">
	import { createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { phases as phasesApi } from '$lib/api/endpoints/phases';
	import { milestones as milestonesApi } from '$lib/api/endpoints/milestones';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import CreatePhaseDialog from '$lib/components/phases/CreatePhaseDialog.svelte';
	import { ArrowRight, Plus } from '@lucide/svelte';
	import { getCurrentProjectId } from '$lib/stores/project.svelte';

	const queryClient = useQueryClient();

	const milestonesQuery = createQuery(() => ({
		queryKey: ['milestones', getCurrentProjectId()],
		queryFn: () => milestonesApi.list(getCurrentProjectId()),
	}));

	let activeMilestone = $derived(
		milestonesQuery.data?.find((m) => m.status === 'InProgress' || m.status === 'Planning') ||
		milestonesQuery.data?.[0]
	);

	const phasesQuery = createQuery(() => ({
		queryKey: ['phases', 'all', activeMilestone?.id],
		queryFn: () => phasesApi.list(activeMilestone?.id || 1),
		enabled: !!activeMilestone,
	}));

	let sortedPhases = $derived(
		[...(phasesQuery.data || [])].sort((a, b) => a.phaseNumber - b.phaseNumber)
	);

	let showCreateDialog = $state(false);
</script>

<svelte:head>
	<title>Phases - Lifecycle Tracker</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6">
	<div class="mb-6 flex items-center justify-between">
		<div>
			<h1 class="text-2xl font-bold text-text-primary">Phases</h1>
			{#if activeMilestone}
				<p class="mt-1 text-sm text-text-secondary">{activeMilestone.name}</p>
			{/if}
		</div>
		{#if activeMilestone}
			<button
				onclick={() => (showCreateDialog = true)}
				class="flex items-center gap-1 rounded-md bg-accent px-3 py-1.5 text-sm text-white transition-colors hover:bg-accent-hover"
			>
				<Plus class="h-3.5 w-3.5" />
				Add Phase
			</button>
		{/if}
	</div>

	{#if phasesQuery.isLoading || milestonesQuery.isLoading}
		<p class="text-text-tertiary">Loading phases...</p>
	{:else if sortedPhases.length === 0}
		<p class="text-text-tertiary">No phases found</p>
	{:else}
		<div class="space-y-3">
			{#each sortedPhases as phase, i}
				{@const total = phase.taskCount || 0}
				{@const completed = phase.doneCount || 0}
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
							<p class="mb-2 text-sm text-text-secondary line-clamp-2">{phase.goal}</p>
						{/if}
						<div class="flex items-center gap-4 text-xs text-text-tertiary">
							<span>{completed}/{total} tasks</span>
							{#if total > 0}
								<div class="h-1 w-24 rounded-full bg-surface-hover">
									<div
										class="h-1 rounded-full bg-accent"
										style="width: {(completed / total) * 100}%"
									></div>
								</div>
							{/if}
						</div>
					</div>

					<ArrowRight class="mt-1 h-4 w-4 shrink-0 text-text-tertiary opacity-0 group-hover:opacity-100" />
				</a>
			{/each}
		</div>
	{/if}
</div>

{#if showCreateDialog && activeMilestone}
	<CreatePhaseDialog
		milestoneId={activeMilestone.id}
		milestoneName={activeMilestone.name}
		onCreated={() => {
			queryClient.invalidateQueries({ queryKey: ['phases'] });
			showCreateDialog = false;
		}}
		onClose={() => (showCreateDialog = false)}
	/>
{/if}
