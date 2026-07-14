<script lang="ts">
	import type { UnitLifecycleStage, UnitNextBestAction } from '$lib/types';
	import { Check, ChevronRight } from '@lucide/svelte';

	let {
		stage,
		nextBestAction,
		onStageClick,
	}: {
		stage: UnitLifecycleStage;
		nextBestAction: UnitNextBestAction;
		/** Clicking a stage jumps to the tab where that stage's work happens. */
		onStageClick: (tab: string) => void;
	} = $props();

	// The nine ordered stages (Ready → … → Turnover → Ready again). Read-only/derived; each maps to the
	// tab where that stage's work is done, so the stepper doubles as navigation (analog of EdiPlatform's
	// order workflow stepper).
	const STAGES: { key: UnitLifecycleStage; label: string; tab: string }[] = [
		{ key: 'Ready', label: 'Ready', tab: 'summary' },
		{ key: 'Listed', label: 'Listed', tab: 'leasing' },
		{ key: 'Applicant', label: 'Applicant', tab: 'leasing' },
		{ key: 'Lease', label: 'Lease', tab: 'tenant-lease' },
		{ key: 'MoveIn', label: 'Move-In', tab: 'tenant-lease' },
		{ key: 'Active', label: 'Active', tab: 'money' },
		{ key: 'Renewal', label: 'Renewal', tab: 'tenant-lease' },
		{ key: 'MoveOut', label: 'Move-Out', tab: 'maintenance' },
		{ key: 'Turnover', label: 'Turnover', tab: 'maintenance' },
	];

	const currentIndex = $derived(Math.max(0, STAGES.findIndex((s) => s.key === stage)));
</script>

<section class="rounded-xl border bg-card p-4" data-testid="lifecycle-rail" data-stage={stage}>
	<!-- Clickable workflow stepper: prior = done, current = highlighted, future = upcoming. -->
	<ol class="flex flex-wrap items-center gap-1.5 overflow-x-auto" aria-label="Unit lifecycle">
		{#each STAGES as s, i (s.key)}
			{@const isDone = i < currentIndex}
			{@const isCurrent = i === currentIndex}
			<li class="flex items-center gap-1.5">
				<button
					type="button"
					onclick={() => onStageClick(s.tab)}
					aria-current={isCurrent ? 'step' : undefined}
					title={`Go to ${s.label}`}
					class="inline-flex items-center gap-1 whitespace-nowrap rounded-full border px-2.5 py-1 text-xs font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/40
						{isCurrent
							? 'border-primary bg-primary text-primary-foreground'
							: isDone
								? 'border-success/40 bg-success/10 text-success hover:bg-success/20'
								: 'border-border bg-muted/40 text-muted-foreground hover:bg-muted'}"
					data-testid="lifecycle-stage-{s.key}"
					data-current={isCurrent ? 'true' : undefined}
				>
					{#if isDone}
						<Check class="h-3 w-3" />
					{/if}
					{s.label}
				</button>
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
