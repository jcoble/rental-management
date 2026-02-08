<script lang="ts">
	import type { TestPlan } from '$lib/types';
	import { Play, Eye, CheckCircle, XCircle, Clock, FileText, ChevronDown, ChevronRight } from '@lucide/svelte';

	let {
		plan,
		onexecute,
		onview,
	}: {
		plan: TestPlan;
		onexecute?: (planId: number) => void;
		onview?: (planId: number) => void;
	} = $props();

	let expandedTests = $state<Set<number>>(new Set());

	const levelColors: Record<string, string> = {
		Smoke: 'bg-blue-500/10 text-blue-400',
		Comprehensive: 'bg-orange-500/10 text-orange-400',
		FullE2E: 'bg-red-500/10 text-red-400',
	};

	const statusIcons: Record<string, typeof CheckCircle> = {
		Draft: FileText,
		ReadyForExecution: Clock,
		InProgress: Clock,
		Passing: CheckCircle,
		Failing: XCircle,
	};

	const statusColors: Record<string, string> = {
		Draft: 'text-text-tertiary',
		ReadyForExecution: 'text-warning',
		InProgress: 'text-accent',
		Passing: 'text-success',
		Failing: 'text-danger',
	};

	const typeColors: Record<string, string> = {
		Unit: 'bg-blue-500/10 text-blue-400',
		Integration: 'bg-purple-500/10 text-purple-400',
		UI: 'bg-orange-500/10 text-orange-400',
		Manual: 'bg-gray-500/10 text-gray-400',
	};

	const testStatusColors: Record<string, string> = {
		Passing: 'text-success',
		Failing: 'text-danger',
		Created: 'text-warning',
		NotCreated: 'text-text-tertiary',
		Skipped: 'text-text-tertiary',
	};

	function toggleTest(testId: number) {
		const next = new Set(expandedTests);
		if (next.has(testId)) next.delete(testId);
		else next.add(testId);
		expandedTests = next;
	}
</script>

<div class="rounded-lg border border-border bg-surface p-3">
	<div class="flex items-start justify-between gap-2">
		<div class="min-w-0 flex-1">
			<div class="mb-1 flex items-center gap-2">
				<span class="rounded-full px-2 py-0.5 text-[10px] font-medium {levelColors[plan.requiredLevel] || ''}">
					{plan.requiredLevel}
				</span>
				{#if statusIcons[plan.status]}
					{@const StatusIcon = statusIcons[plan.status]}
					<span class="flex items-center gap-1 text-xs {statusColors[plan.status] || ''}">
						<StatusIcon class="h-3 w-3" />
						{plan.status}
					</span>
				{:else}
					<span class="flex items-center gap-1 text-xs text-text-tertiary">
						<FileText class="h-3 w-3" />
						{plan.status}
					</span>
				{/if}
				<span class="text-[10px] text-text-tertiary">
					{plan.source === 'AI_Generated' ? 'AI' : plan.source}
				</span>
			</div>
			<p class="text-sm font-medium text-text-primary truncate">{plan.name}</p>
			{#if plan.description}
				<p class="mt-0.5 text-xs text-text-tertiary truncate">{plan.description}</p>
			{/if}
		</div>

		<div class="flex items-center gap-1 shrink-0">
			{#if onview}
				<button
					onclick={() => onview?.(plan.id)}
					class="rounded p-1 text-text-tertiary transition-colors hover:bg-surface-hover hover:text-text-primary"
					title="View details"
				>
					<Eye class="h-3.5 w-3.5" />
				</button>
			{/if}
			{#if onexecute && plan.status !== 'InProgress'}
				<button
					onclick={() => onexecute?.(plan.id)}
					class="rounded p-1 text-accent transition-colors hover:bg-accent/10"
					title="Execute test plan"
				>
					<Play class="h-3.5 w-3.5" />
				</button>
			{/if}
		</div>
	</div>

	<!-- Tests breakdown -->
	{#if plan.tests && plan.tests.length > 0}
		<div class="mt-2 space-y-1">
			{#each plan.tests as test}
				<div class="rounded-md border border-border bg-bg">
					<button
						onclick={() => toggleTest(test.id)}
						class="flex w-full items-center gap-2 px-2 py-1 text-left"
					>
						{#if expandedTests.has(test.id)}
							<ChevronDown class="h-3 w-3 text-text-tertiary shrink-0" />
						{:else}
							<ChevronRight class="h-3 w-3 text-text-tertiary shrink-0" />
						{/if}
						<span class="rounded-full px-1.5 py-0.5 text-[9px] font-medium {typeColors[test.type] || ''}">
							{test.type}
						</span>
						<span class="flex-1 truncate text-xs text-text-primary">{test.name}</span>
						<span class="text-[10px] {testStatusColors[test.status] || 'text-text-tertiary'}">
							{#if test.totalRuns > 0}
								{test.passedRuns}/{test.totalRuns}
							{:else}
								{test.status}
							{/if}
						</span>
					</button>

					{#if expandedTests.has(test.id) && test.steps && test.steps.length > 0}
						<div class="border-t border-border px-2 py-1 space-y-0.5">
							{#each test.steps as step, i}
								<div class="flex items-center gap-1.5 text-[10px] text-text-tertiary">
									<span class="text-text-tertiary w-3 text-right">{i + 1}.</span>
									<span class="truncate">{step.description}</span>
								</div>
							{/each}
						</div>
					{/if}
				</div>
			{/each}
		</div>
	{/if}

	<!-- Execution stats -->
	{#if plan.latestExecution}
		{@const exec = plan.latestExecution}
		<div class="mt-2 flex items-center gap-3 text-[10px] text-text-tertiary">
			<span class="text-success">{exec.passedSteps} passed</span>
			{#if exec.failedSteps > 0}
				<span class="text-danger">{exec.failedSteps} failed</span>
			{/if}
			<span>{exec.totalSteps} total steps</span>
		</div>
	{:else if !plan.tests || plan.tests.length === 0}
		<p class="mt-2 text-[10px] text-text-tertiary">No tests - not yet executed</p>
	{/if}
</div>
