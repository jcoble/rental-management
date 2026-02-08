<script lang="ts">
	import type { Task, TaskStatus, Phase, Label } from '$lib/types';
	import BoardColumn from './BoardColumn.svelte';
	import FilterBar from './FilterBar.svelte';
	import TaskDetail from '$lib/components/tasks/TaskDetail.svelte';
	import { tasks as tasksApi } from '$lib/api/endpoints/tasks';
	import { getCurrentProjectId } from '$lib/stores/project.svelte';

	let {
		tasks,
		phases = [] as Phase[],
		labels = [] as Label[],
		onTaskUpdated,
	}: {
		tasks: Task[];
		phases?: Phase[];
		labels?: Label[];
		onTaskUpdated?: () => void;
	} = $props();

	let selectedTask = $state<Task | null>(null);
	let filters = $state<Record<string, string>>({});

	const columns: { status: TaskStatus; title: string }[] = [
		{ status: 'Backlog', title: 'Backlog' },
		{ status: 'Todo', title: 'Todo' },
		{ status: 'InProgress', title: 'In Progress' },
		{ status: 'Review', title: 'Review' },
		{ status: 'Blocked', title: 'Blocked' },
		{ status: 'Done', title: 'Done' },
	];

	let filteredTasks = $derived.by(() => {
		let result = tasks;
		if (filters.search) {
			const s = filters.search.toLowerCase();
			result = result.filter(
				(t) => t.title.toLowerCase().includes(s) || t.description?.toLowerCase().includes(s)
			);
		}
		if (filters.phase) result = result.filter((t) => t.phaseId === Number(filters.phase));
		if (filters.priority) result = result.filter((t) => t.priority === filters.priority);
		if (filters.type) result = result.filter((t) => t.type === filters.type);
		if (filters.source) result = result.filter((t) => t.source === filters.source);
		if (filters.label)
			result = result.filter((t) => t.labels?.some((l) => l.id === Number(filters.label)));
		return result;
	});

	// Group tasks by status into column data
	let columnData = $state<Record<TaskStatus, Task[]>>({
		Backlog: [],
		Todo: [],
		InProgress: [],
		Review: [],
		Blocked: [],
		Done: [],
		Cancelled: [],
	});

	// Update column data when filtered tasks change
	$effect(() => {
		const grouped: Record<string, Task[]> = {};
		for (const col of columns) {
			grouped[col.status] = [];
		}
		for (const task of filteredTasks) {
			if (grouped[task.status]) {
				grouped[task.status].push(task);
			}
		}
		// Sort each column by orderInColumn
		for (const key of Object.keys(grouped)) {
			grouped[key].sort((a, b) => a.orderInColumn - b.orderInColumn);
		}
		columnData = grouped as Record<TaskStatus, Task[]>;
	});

	function handleConsider(status: TaskStatus, e: CustomEvent) {
		columnData[status] = e.detail.items;
		columnData = columnData; // trigger reactivity
	}

	async function handleFinalize(status: TaskStatus, e: CustomEvent) {
		columnData[status] = e.detail.items;
		columnData = columnData;

		// Find the task that was dropped and update it
		const droppedInfo = e.detail.info;
		if (droppedInfo?.id) {
			const droppedId = droppedInfo.id;
			const orderInColumn = columnData[status].findIndex((t) => t.id === droppedId);
			if (orderInColumn >= 0) {
				try {
					await tasksApi.move(droppedId, status, orderInColumn);
					onTaskUpdated?.();
				} catch {
					// Revert will happen on refetch
					onTaskUpdated?.();
				}
			}
		}
	}

	async function handleQuickAdd(status: TaskStatus, title: string) {
		try {
			await tasksApi.create({
				title,
				status,
				projectId: getCurrentProjectId(),
				priority: 'P3',
				type: 'Feature',
				source: 'Manual',
				orderInColumn: columnData[status].length,
			});
			onTaskUpdated?.();
		} catch {
			// Handle error
		}
	}

	function handleCardClick(task: Task) {
		selectedTask = task;
	}

	function handleTaskUpdate(updated: Task) {
		selectedTask = updated;
		onTaskUpdated?.();
	}
</script>

<div class="flex h-full flex-col">
	<FilterBar
		{phases}
		{labels}
		onchange={(f) => (filters = f)}
	/>

	<!-- Board -->
	<div class="flex flex-1 gap-4 overflow-x-auto p-4">
		{#each columns as col}
			<BoardColumn
				status={col.status}
				title={col.title}
				tasks={columnData[col.status] || []}
				onCardClick={handleCardClick}
				onDndConsider={handleConsider}
				onDndFinalize={handleFinalize}
				onQuickAdd={handleQuickAdd}
			/>
		{/each}
	</div>
</div>

<!-- Task detail slide-in -->
{#if selectedTask}
	<TaskDetail
		task={selectedTask}
		{phases}
		onclose={() => (selectedTask = null)}
		onupdate={handleTaskUpdate}
		ondelete={() => { selectedTask = null; onTaskUpdated?.(); }}
	/>
{/if}
