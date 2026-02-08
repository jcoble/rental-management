<script lang="ts">
	import type { TaskPriority, TaskType, TaskSource, Phase, Label } from '$lib/types';
	import { Search, X } from '@lucide/svelte';

	let {
		search = '',
		phase = '',
		priority = '',
		type = '',
		source = '',
		label = '',
		phases = [] as Phase[],
		labels = [] as Label[],
		onchange,
	}: {
		search?: string;
		phase?: string;
		priority?: string;
		type?: string;
		source?: string;
		label?: string;
		phases?: Phase[];
		labels?: Label[];
		onchange?: (filters: Record<string, string>) => void;
	} = $props();

	function emit() {
		onchange?.({ search, phase, priority, type, source, label });
	}

	function clearAll() {
		search = '';
		phase = '';
		priority = '';
		type = '';
		source = '';
		label = '';
		emit();
	}

	let hasFilters = $derived(
		!!search || !!phase || !!priority || !!type || !!source || !!label
	);

	const priorities: TaskPriority[] = ['P1', 'P2', 'P3', 'P4'];
	const types: TaskType[] = ['Feature', 'Bug', 'Refactor', 'Docs', 'Test', 'Infra', 'Research'];
	const sources: TaskSource[] = ['Manual', 'Claude'];
</script>

<div class="flex flex-wrap items-center gap-2 border-b border-border px-4 py-2">
	<!-- Search -->
	<div class="relative">
		<Search class="absolute left-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-text-tertiary" />
		<input
			type="text"
			bind:value={search}
			oninput={emit}
			placeholder="Search tasks..."
			class="h-8 w-48 rounded-md border border-border bg-surface pl-8 pr-3 text-sm text-text-primary placeholder:text-text-tertiary focus:border-accent focus:outline-none"
		/>
	</div>

	<!-- Phase -->
	{#if phases.length > 0}
		<select
			bind:value={phase}
			onchange={emit}
			class="h-8 rounded-md border border-border bg-surface px-2 text-sm text-text-secondary focus:border-accent focus:outline-none"
		>
			<option value="">All Phases</option>
			{#each phases as p}
				<option value={String(p.id)}>{p.name}</option>
			{/each}
		</select>
	{/if}

	<!-- Priority -->
	<select
		bind:value={priority}
		onchange={emit}
		class="h-8 rounded-md border border-border bg-surface px-2 text-sm text-text-secondary focus:border-accent focus:outline-none"
	>
		<option value="">All Priorities</option>
		{#each priorities as p}
			<option value={p}>{p}</option>
		{/each}
	</select>

	<!-- Type -->
	<select
		bind:value={type}
		onchange={emit}
		class="h-8 rounded-md border border-border bg-surface px-2 text-sm text-text-secondary focus:border-accent focus:outline-none"
	>
		<option value="">All Types</option>
		{#each types as t}
			<option value={t}>{t}</option>
		{/each}
	</select>

	<!-- Source -->
	<select
		bind:value={source}
		onchange={emit}
		class="h-8 rounded-md border border-border bg-surface px-2 text-sm text-text-secondary focus:border-accent focus:outline-none"
	>
		<option value="">All Sources</option>
		{#each sources as s}
			<option value={s}>{s}</option>
		{/each}
	</select>

	<!-- Label -->
	{#if labels.length > 0}
		<select
			bind:value={label}
			onchange={emit}
			class="h-8 rounded-md border border-border bg-surface px-2 text-sm text-text-secondary focus:border-accent focus:outline-none"
		>
			<option value="">All Labels</option>
			{#each labels as l}
				<option value={String(l.id)}>{l.name}</option>
			{/each}
		</select>
	{/if}

	<!-- Clear -->
	{#if hasFilters}
		<button
			onclick={clearAll}
			class="flex items-center gap-1 rounded-md px-2 py-1 text-xs text-text-tertiary hover:bg-surface-hover hover:text-text-secondary"
		>
			<X class="h-3 w-3" />
			Clear
		</button>
	{/if}
</div>
