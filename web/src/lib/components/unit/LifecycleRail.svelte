<script lang="ts">
	import type { UnitLifecycleStage, UnitNextBestAction } from '$lib/types';
	import { Check, ChevronRight } from '@lucide/svelte';

	let {
		stage,
		nextBestAction,
	}: {
		stage: UnitLifecycleStage;
		nextBestAction: UnitNextBestAction;
	} = $props();

	// The nine ordered stages a unit moves through (Ready → … → Turnover → Ready again). Read-only/derived.
	const STAGES: { key: UnitLifecycleStage; label: string }[] = [
		{ key: 'Ready', label: 'Ready' },
		{ key: 'Listed', label: 'Listed' },
		{ key: 'Applicant', label: 'Applicant' },
		{ key: 'Lease', label: 'Lease' },
		{ key: 'MoveIn', label: 'Move-In' },
		{ key: 'Active', label: 'Active' },
		{ key: 'Renewal', label: 'Renewal' },
		{ key: 'MoveOut', label: 'Move-Out' },
		{ key: 'Turnover', label: 'Turnover' },
	];

	const currentIndex = $derived(Math.max(0, STAGES.findIndex((s) => s.key === stage)));
</script>

<section class="rounded-xl border bg-card p-4" data-testid="lifecycle-rail" data-stage={stage}>
	<!-- Stage rail: prior = done, current = highlighted, future = upcoming. -->
	<ol class="flex flex-wrap items-center gap-1.5 overflow-x-auto" aria-label="Unit lifecycle">
		{#each STAGES as s, i (s.key)}
			{@const isDone = i < currentIndex}
			{@const isCurrent = i === currentIndex}
			<li class="flex items-center gap-1.5">
				<span
					class="inline-flex items-center gap-1 whitespace-nowrap rounded-full border px-2.5 py-1 text-xs font-medium transition-colors
						{isCurrent
							? 'border-primary bg-primary text-primary-foreground'
							: isDone
								? 'border-success/40 bg-success/10 text-success'
								: 'border-border bg-muted/40 text-muted-foreground'}"
					data-current={isCurrent ? 'true' : undefined}
				>
					{#if isDone}
						<Check class="h-3 w-3" />
					{/if}
					{s.label}
				</span>
				{#if i < STAGES.length - 1}
					<ChevronRight class="h-3 w-3 shrink-0 text-muted-foreground/50" />
				{/if}
			</li>
		{/each}
	</ol>

	<!-- Next-best-action line: links into the relevant flow/tab. -->
	{#if nextBestAction?.label}
		<a
			href={nextBestAction.href}
			class="mt-3 inline-flex items-center gap-1.5 text-sm font-medium text-primary hover:underline"
			data-testid="next-best-action"
		>
			{nextBestAction.label}
			<ChevronRight class="h-4 w-4" />
		</a>
	{/if}
</section>
