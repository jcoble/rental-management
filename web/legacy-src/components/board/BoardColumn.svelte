<script lang="ts">
	import type { Task, TaskStatus } from '$lib/types';
	import BoardCard from './BoardCard.svelte';
	import { dndzone } from 'svelte-dnd-action';
	import { Plus } from '@lucide/svelte';

	let {
		status,
		title,
		tasks = [],
		onCardClick,
		onDndConsider,
		onDndFinalize,
		onQuickAdd,
	}: {
		status: TaskStatus;
		title: string;
		tasks: Task[];
		onCardClick?: (task: Task) => void;
		onDndConsider?: (status: TaskStatus, e: CustomEvent) => void;
		onDndFinalize?: (status: TaskStatus, e: CustomEvent) => void;
		onQuickAdd?: (status: TaskStatus, title: string) => void;
	} = $props();

	let quickAddVisible = $state(false);
	let quickAddTitle = $state('');

	function handleQuickAdd() {
		if (!quickAddTitle.trim()) return;
		onQuickAdd?.(status, quickAddTitle.trim());
		quickAddTitle = '';
		quickAddVisible = false;
	}

	function handleKeydown(e: KeyboardEvent) {
		if (e.key === 'Enter') handleQuickAdd();
		if (e.key === 'Escape') {
			quickAddVisible = false;
			quickAddTitle = '';
		}
	}

	const statusColors: Record<string, string> = {
		Backlog: 'text-text-tertiary',
		Todo: 'text-text-secondary',
		InProgress: 'text-accent',
		Review: 'text-warning',
		Blocked: 'text-danger',
		Done: 'text-success',
	};
</script>

<div class="flex h-full w-72 shrink-0 flex-col rounded-lg bg-surface/50">
	<!-- Header -->
	<div class="flex items-center justify-between px-3 py-2.5">
		<div class="flex items-center gap-2">
			<span class="h-2 w-2 rounded-full {statusColors[status] || 'text-text-tertiary'}"
				style="background: currentColor;"></span>
			<h3 class="text-sm font-medium text-text-primary">{title}</h3>
			<span class="rounded-full bg-surface-hover px-1.5 py-0.5 text-[10px] font-medium text-text-tertiary">
				{tasks.length}
			</span>
		</div>
		<button
			onclick={() => (quickAddVisible = !quickAddVisible)}
			class="rounded p-1 text-text-tertiary transition-colors hover:bg-surface-hover hover:text-text-secondary"
		>
			<Plus class="h-3.5 w-3.5" />
		</button>
	</div>

	<!-- Quick add -->
	{#if quickAddVisible}
		<div class="px-2 pb-2">
			<input
				type="text"
				bind:value={quickAddTitle}
				onkeydown={handleKeydown}
				placeholder="Task title..."
				class="w-full rounded-md border border-border bg-surface px-2.5 py-1.5 text-sm text-text-primary placeholder:text-text-tertiary focus:border-accent focus:outline-none"
				autofocus
			/>
		</div>
	{/if}

	<!-- Cards -->
	<div
		class="flex-1 space-y-2 overflow-y-auto px-2 pb-2"
		use:dndzone={{ items: tasks, type: 'task', dropTargetStyle: {} }}
		onconsider={(e) => onDndConsider?.(status, e)}
		onfinalize={(e) => onDndFinalize?.(status, e)}
	>
		{#each tasks as task (task.id)}
			<div>
				<BoardCard {task} onclick={onCardClick} />
			</div>
		{/each}
	</div>
</div>
