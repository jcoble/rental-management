<script lang="ts">
	import KanbanBoard from '$lib/components/board/KanbanBoard.svelte';
	import { createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { tasks as tasksApi } from '$lib/api/endpoints/tasks';
	import { phases as phasesApi } from '$lib/api/endpoints/phases';
	import { labels as labelsApi } from '$lib/api/endpoints/labels';
	import { getCurrentProjectId } from '$lib/stores/project.svelte';

	const queryClient = useQueryClient();

	const tasksQuery = createQuery(() => ({
		queryKey: ['tasks', getCurrentProjectId()],
		queryFn: () => tasksApi.list({ projectId: String(getCurrentProjectId()) }),
	}));

	const phasesQuery = createQuery(() => ({
		queryKey: ['phases', 'all', getCurrentProjectId()],
		queryFn: () => phasesApi.list(getCurrentProjectId()),
	}));

	const labelsQuery = createQuery(() => ({
		queryKey: ['labels', getCurrentProjectId()],
		queryFn: () => labelsApi.list(getCurrentProjectId()),
	}));

	function handleTaskUpdated() {
		queryClient.invalidateQueries({ queryKey: ['tasks'] });
	}
</script>

<svelte:head>
	<title>Board - Lifecycle Tracker</title>
</svelte:head>

<div class="h-full">
	{#if tasksQuery.isLoading}
		<div class="flex h-full items-center justify-center">
			<p class="text-text-tertiary">Loading board...</p>
		</div>
	{:else if tasksQuery.isError}
		<div class="flex h-full items-center justify-center">
			<p class="text-danger">Failed to load tasks</p>
		</div>
	{:else}
		<KanbanBoard
			tasks={tasksQuery.data || []}
			phases={phasesQuery.data || []}
			labels={labelsQuery.data || []}
			onTaskUpdated={handleTaskUpdated}
		/>
	{/if}
</div>
