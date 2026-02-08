<script lang="ts">
	import type { TestExecution } from '$lib/types';
	import { CheckCircle, XCircle, MinusCircle, Clock } from '@lucide/svelte';
	import { formatRelative } from '$lib/utils/date';

	let { execution }: { execution: TestExecution } = $props();

	const statusIcons: Record<string, typeof CheckCircle> = {
		Passed: CheckCircle,
		Failed: XCircle,
		Skipped: MinusCircle,
		Blocked: Clock,
	};

	const statusColors: Record<string, string> = {
		Passed: 'text-success',
		Failed: 'text-danger',
		Skipped: 'text-text-tertiary',
		Blocked: 'text-warning',
	};

	let progressPercent = $derived(
		execution.totalSteps > 0
			? Math.round(((execution.passedSteps + execution.failedSteps + execution.skippedSteps) / execution.totalSteps) * 100)
			: 0
	);
</script>

<div class="rounded-lg border border-border bg-surface p-4">
	<!-- Header -->
	<div class="mb-3 flex items-center justify-between">
		<div class="flex items-center gap-2">
			<span class="text-xs font-medium text-text-primary">{execution.executionMode}</span>
			{#if statusIcons[execution.status]}
				{@const OverallIcon = statusIcons[execution.status]}
				<span class="flex items-center gap-1 text-xs {statusColors[execution.status] || 'text-text-tertiary'}">
					<OverallIcon class="h-3.5 w-3.5" />
					{execution.status}
				</span>
			{:else}
				<span class="text-xs text-text-tertiary">{execution.status}</span>
			{/if}
		</div>
		<span class="text-[10px] text-text-tertiary">
			{execution.executedBy || 'Unknown'} - {formatRelative(execution.startedAt)}
		</span>
	</div>

	<!-- Progress bar -->
	<div class="mb-3 h-1.5 w-full rounded-full bg-border">
		<div
			class="h-full rounded-full transition-all {execution.failedSteps > 0 ? 'bg-danger' : 'bg-success'}"
			style="width: {progressPercent}%"
		></div>
	</div>

	<!-- Stats -->
	<div class="mb-3 flex items-center gap-4 text-xs">
		<span class="text-success">{execution.passedSteps} passed</span>
		<span class="text-danger">{execution.failedSteps} failed</span>
		<span class="text-text-tertiary">{execution.skippedSteps} skipped</span>
		<span class="text-text-tertiary">{execution.totalSteps} total</span>
	</div>

	<!-- Step results -->
	{#if execution.stepResults && execution.stepResults.length > 0}
		<div class="space-y-1.5">
			{#each execution.stepResults as result}
				{@const Icon = statusIcons[result.status] || Clock}
				<div class="flex items-start gap-2 rounded-md border border-border bg-bg px-2.5 py-1.5">
					<Icon class="mt-0.5 h-3.5 w-3.5 shrink-0 {statusColors[result.status] || ''}" />
					<div class="min-w-0 flex-1">
						<p class="text-xs text-text-primary">{result.stepDescription || `Step ${result.testStepId}`}</p>
						{#if result.actualResult}
							<p class="mt-0.5 text-[10px] text-text-secondary">{result.actualResult}</p>
						{/if}
						{#if result.errorMessage}
							<p class="mt-0.5 text-[10px] text-danger">{result.errorMessage}</p>
						{/if}
					</div>
					<span class="shrink-0 text-[10px] text-text-tertiary">{result.durationMs}ms</span>
				</div>
			{/each}
		</div>
	{/if}

	{#if execution.failureReason}
		<div class="mt-3 rounded-md bg-danger/10 px-3 py-2 text-xs text-danger">
			{execution.failureReason}
		</div>
	{/if}
</div>
