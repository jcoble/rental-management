<script lang="ts">
	import { createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { milestones as milestonesApi } from '$lib/api/endpoints/milestones';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import CreateMilestoneDialog from '$lib/components/milestones/CreateMilestoneDialog.svelte';
	import { formatDate } from '$lib/utils/date';
	import type { Milestone } from '$lib/types';
	import { Target, Calendar, Plus } from '@lucide/svelte';
	import { getCurrentProjectId } from '$lib/stores/project.svelte';

	const queryClient = useQueryClient();

	const milestonesQuery = createQuery(() => ({
		queryKey: ['milestones', getCurrentProjectId()],
		queryFn: () => milestonesApi.list(getCurrentProjectId()),
	}));

	let grouped = $derived.by(() => {
		const data = milestonesQuery.data || [];
		const groups: Record<string, Milestone[]> = {
			Active: [],
			Planning: [],
			Completed: [],
			Other: [],
		};
		for (const m of data) {
			if (m.status === 'InProgress') groups.Active.push(m);
			else if (m.status === 'Planning') groups.Planning.push(m);
			else if (m.status === 'Completed') groups.Completed.push(m);
			else groups.Other.push(m);
		}
		return groups;
	});

	let showCreateDialog = $state(false);
</script>

<svelte:head>
	<title>Milestones - Lifecycle Tracker</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6">
	<div class="mb-6 flex items-center justify-between">
		<h1 class="text-2xl font-bold text-text-primary">Milestones</h1>
		<button
			onclick={() => (showCreateDialog = true)}
			class="flex items-center gap-1 rounded-md bg-accent px-3 py-1.5 text-sm text-white transition-colors hover:bg-accent-hover"
		>
			<Plus class="h-3.5 w-3.5" />
			New Milestone
		</button>
	</div>

	{#if milestonesQuery.isLoading}
		<p class="text-text-tertiary">Loading milestones...</p>
	{:else}
		{#each Object.entries(grouped) as [group, items]}
			{#if items.length > 0}
				<div class="mb-6">
					<h2 class="mb-3 text-sm font-semibold uppercase tracking-wider text-text-tertiary">{group}</h2>
					<div class="space-y-3">
						{#each items as milestone}
							<a
								href="/milestones/{milestone.id}"
								class="block rounded-lg border border-border bg-surface p-4 transition-colors hover:border-border-hover"
							>
								<div class="flex items-start justify-between">
									<div class="flex items-center gap-3">
										<Target class="h-5 w-5 shrink-0 text-text-tertiary" />
										<div>
											<h3 class="font-medium text-text-primary">{milestone.name}</h3>
											{#if milestone.version}
												<span class="text-xs text-text-tertiary">v{milestone.version}</span>
											{/if}
										</div>
									</div>
									<StatusBadge status={milestone.status} />
								</div>

								{#if milestone.description}
									<p class="mt-2 pl-8 text-sm text-text-secondary">{milestone.description}</p>
								{/if}

								<div class="mt-3 flex items-center gap-4 pl-8 text-xs text-text-tertiary">
									{#if milestone.targetDate}
										<span class="flex items-center gap-1">
											<Calendar class="h-3 w-3" />
											Target: {formatDate(milestone.targetDate)}
										</span>
									{/if}
									{#if milestone.phases}
										{@const completedPhases = milestone.phases.filter((p) => p.status === 'Completed').length}
										<span>{completedPhases}/{milestone.phases.length} phases completed</span>
									{/if}
								</div>
							</a>
						{/each}
					</div>
				</div>
			{/if}
		{/each}
	{/if}
</div>

{#if showCreateDialog}
	<CreateMilestoneDialog
		projectId={getCurrentProjectId()}
		onCreated={() => queryClient.invalidateQueries({ queryKey: ['milestones', getCurrentProjectId()] })}
		onClose={() => (showCreateDialog = false)}
	/>
{/if}
