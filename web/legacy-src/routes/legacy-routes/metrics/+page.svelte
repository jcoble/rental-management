<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { projects } from '$lib/api/endpoints/projects';
	import { tasks as tasksApi } from '$lib/api/endpoints/tasks';
	import type { Dashboard } from '$lib/types';
	import {
		Bug,
		Zap,
		Wrench,
		FileText,
		Beaker,
		Server,
		Search,
		Circle
	} from '@lucide/svelte';
	import { getCurrentProjectId } from '$lib/stores/project.svelte';

	const dashboardQuery = createQuery(() => ({
		queryKey: ['dashboard', getCurrentProjectId()],
		queryFn: () => projects.dashboard(getCurrentProjectId()),
	}));

	const tasksQuery = createQuery(() => ({
		queryKey: ['tasks', getCurrentProjectId()],
		queryFn: () => tasksApi.list({ projectId: String(getCurrentProjectId()) }),
	}));

	let dashboard = $derived(dashboardQuery.data as Dashboard | undefined);
	let taskSummary = $derived(dashboard?.taskSummary);
	let allTasks = $derived(tasksQuery.data || []);

	let totalTasks = $derived(taskSummary?.total ?? 0);
	let completedTasks = $derived(taskSummary?.byStatus?.['Done'] ?? 0);
	let completionRate = $derived(
		totalTasks > 0 ? Math.round((completedTasks / totalTasks) * 100) : 0
	);

	let tasksByType = $derived.by(() => {
		const counts: Record<string, number> = {};
		for (const t of allTasks) {
			counts[t.type] = (counts[t.type] || 0) + 1;
		}
		return counts;
	});

	let tasksByPriority = $derived.by(() => {
		const counts: Record<string, number> = {};
		for (const t of allTasks) {
			counts[t.priority] = (counts[t.priority] || 0) + 1;
		}
		return counts;
	});

	let aiVsManual = $derived.by(() => {
		let ai = 0, manual = 0;
		for (const t of allTasks) {
			if (t.source === 'Claude') ai++;
			else manual++;
		}
		return { ai, manual };
	});

	let testStats = $derived.by(() => {
		const ts = dashboard?.testSummary;
		if (ts) return { passing: ts.passing, failing: ts.failing, total: ts.total };
		return { passing: 0, failing: 0, total: 0 };
	});

	const priorityColors: Record<string, string> = {
		P1: 'text-p1',
		P2: 'text-p2',
		P3: 'text-p3',
		P4: 'text-p4',
	};

	const typeIcons: Record<string, typeof Bug> = {
		Bug: Bug,
		Feature: Zap,
		Refactor: Wrench,
		Docs: FileText,
		Test: Beaker,
		Infra: Server,
		Research: Search,
	};
</script>

<svelte:head>
	<title>Metrics - Lifecycle Tracker</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6">
	<div class="mb-6">
		<h1 class="text-2xl font-bold text-text-primary">Metrics</h1>
	</div>

	{#if dashboardQuery.isLoading}
		<p class="text-text-tertiary">Loading metrics...</p>
	{:else if taskSummary}
		<!-- Overview -->
		<div class="mb-8 grid grid-cols-2 gap-4 lg:grid-cols-4">
			<div class="rounded-lg border border-border bg-surface p-4">
				<p class="text-xs text-text-tertiary">Completion Rate</p>
				<p class="mt-1 text-3xl font-bold text-text-primary">{completionRate}%</p>
				<p class="text-xs text-text-tertiary">{completedTasks} of {totalTasks}</p>
			</div>
			<div class="rounded-lg border border-border bg-surface p-4">
				<p class="text-xs text-text-tertiary">Test Pass Rate</p>
				<p class="mt-1 text-3xl font-bold text-text-primary">
					{testStats.total > 0 ? Math.round((testStats.passing / testStats.total) * 100) : 0}%
				</p>
				<p class="text-xs text-text-tertiary">{testStats.passing} of {testStats.total} tests</p>
			</div>
			<div class="rounded-lg border border-border bg-surface p-4">
				<p class="text-xs text-text-tertiary">AI vs Manual</p>
				<p class="mt-1 text-3xl font-bold text-text-primary">
					{allTasks.length > 0 ? Math.round((aiVsManual.ai / allTasks.length) * 100) : 0}%
				</p>
				<p class="text-xs text-text-tertiary">{aiVsManual.ai} AI / {aiVsManual.manual} Manual</p>
			</div>
			<div class="rounded-lg border border-border bg-surface p-4">
				<p class="text-xs text-text-tertiary">Tests Failing</p>
				<p class="mt-1 text-3xl font-bold {testStats.failing > 0 ? 'text-danger' : 'text-success'}">
					{testStats.failing}
				</p>
				<p class="text-xs text-text-tertiary">of {testStats.total} total tests</p>
			</div>
		</div>

		<!-- Tasks by Priority & Type -->
		<div class="mb-8 grid gap-6 lg:grid-cols-2">
			<div class="rounded-lg border border-border bg-surface p-4">
				<h2 class="mb-4 text-sm font-semibold text-text-primary">Tasks by Priority</h2>
				<div class="space-y-3">
					{#each ['P1', 'P2', 'P3', 'P4'] as p}
						{@const count = tasksByPriority[p] || 0}
						{@const pct = allTasks.length > 0 ? (count / allTasks.length) * 100 : 0}
						<div>
							<div class="mb-1 flex items-center justify-between text-sm">
								<span class="{priorityColors[p]}">{p}</span>
								<span class="text-text-tertiary">{count}</span>
							</div>
							<div class="h-1.5 rounded-full bg-surface-hover">
								<div
									class="h-1.5 rounded-full transition-all {p === 'P1' ? 'bg-p1' : p === 'P2' ? 'bg-p2' : p === 'P3' ? 'bg-p3' : 'bg-p4'}"
									style="width: {pct}%"
								></div>
							</div>
						</div>
					{/each}
				</div>
			</div>

			<div class="rounded-lg border border-border bg-surface p-4">
				<h2 class="mb-4 text-sm font-semibold text-text-primary">Tasks by Type</h2>
				<div class="space-y-3">
					{#each Object.entries(tasksByType).sort((a, b) => b[1] - a[1]) as [type, count]}
						{@const pct = allTasks.length > 0 ? (count / allTasks.length) * 100 : 0}
						{@const Icon = typeIcons[type] || Circle}
						<div>
							<div class="mb-1 flex items-center justify-between text-sm">
								<span class="flex items-center gap-1.5 text-text-secondary">
									<Icon class="h-3.5 w-3.5" />
									{type}
								</span>
								<span class="text-text-tertiary">{count}</span>
							</div>
							<div class="h-1.5 rounded-full bg-surface-hover">
								<div class="h-1.5 rounded-full bg-accent" style="width: {pct}%"></div>
							</div>
						</div>
					{/each}
				</div>
			</div>
		</div>
	{/if}
</div>
