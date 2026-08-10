<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { inspections } from '$lib/api/endpoints/inspections';
	import type { InspectionItem } from '$lib/types';
	import { Button } from '$lib/components/ui/button';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { isMismatchedUnitSelection } from '$lib/unit/unit-membership-guard';
	import { formatDateOnly } from '$lib/utils/date';
	import { formatStatusLabel } from '$lib/utils/status-labels';

	let {
		inspectionId,
		expectedUnitId,
		onUnitMismatch,
	}: {
		inspectionId: number;
		expectedUnitId?: number;
		onUnitMismatch?: () => void;
	} = $props();

	const inspectionQuery = createQuery(() => ({
		queryKey: ['inspection', inspectionId],
		queryFn: () => inspections.get(inspectionId),
		enabled: Number.isInteger(inspectionId) && inspectionId > 0,
	}));

	const inspection = $derived(inspectionQuery.data);
	const inspectionTypeLabels: Record<string, string> = {
		MoveIn: 'Move-in',
		MoveOut: 'Move-out',
		AnnualSafety: 'Annual safety',
		Routine: 'Routine',
	};
	const inspectionTypeLabel = $derived(
		inspection ? (inspectionTypeLabels[inspection.type] ?? formatStatusLabel(inspection.type)) : 'Inspection',
	);
	const inspectionItems = $derived(inspection?.items ?? []);

	$effect(() => {
		if (isMismatchedUnitSelection(inspection, expectedUnitId)) onUnitMismatch?.();
	});

	function itemResultLabel(item: InspectionItem): string {
		return formatStatusLabel(item.result);
	}
</script>

<div class="space-y-4 rounded-xl border bg-card p-4" data-testid={`inspection-detail-${inspectionId}`}>
	{#if inspectionQuery.isLoading}
		<LoadingState label="Loading inspection details" variant="section" testid="inspection-detail-loading" />
	{:else if inspectionQuery.isError}
		<div class="rounded-lg border border-destructive/40 bg-destructive/5 p-4" role="alert" data-testid="inspection-detail-error">
			<p class="font-medium text-destructive" data-testid="inspection-detail-error-title">Could not load this inspection.</p>
			<p class="mt-1 text-sm text-muted-foreground" data-testid="inspection-detail-error-message">Try again. No inspection changes have been made.</p>
			<Button class="mt-3" variant="outline" size="sm" onclick={() => inspectionQuery.refetch()} data-testid="inspection-detail-retry">Try again</Button>
		</div>
	{:else if !inspection || isMismatchedUnitSelection(inspection, expectedUnitId)}
		<p class="py-8 text-center text-sm text-muted-foreground" data-testid="inspection-detail-not-found">Inspection not found for this rental.</p>
	{:else}
		<div class="flex flex-wrap items-start justify-between gap-3" data-testid="inspection-detail-header">
			<div class="space-y-1" data-testid="inspection-detail-heading">
				<div class="flex flex-wrap items-center gap-2" data-testid="inspection-detail-title-row">
					<h2 class="text-xl font-semibold" data-testid="inspection-detail-title">{inspectionTypeLabel} inspection</h2>
					<StatusBadge status={inspection.status} />
				</div>
				<p class="text-xs text-muted-foreground" data-testid="inspection-detail-id">Inspection #{inspection.id}</p>
			</div>
		</div>

		<dl class="grid gap-3 rounded-lg border p-3 text-sm sm:grid-cols-2 lg:grid-cols-4" data-testid="inspection-detail-facts">
			<div data-testid="inspection-detail-property-fact">
				<dt class="text-xs text-muted-foreground" data-testid="inspection-detail-property-label">Property</dt>
				<dd class="mt-1 font-medium" data-testid="inspection-detail-property">{inspection.propertyName ?? '—'}</dd>
			</div>
			<div data-testid="inspection-detail-unit-fact">
				<dt class="text-xs text-muted-foreground" data-testid="inspection-detail-unit-label">Unit</dt>
				<dd class="mt-1 font-medium" data-testid="inspection-detail-unit">{inspection.unitNumber ?? '—'}</dd>
			</div>
			<div data-testid="inspection-detail-scheduled-fact">
				<dt class="text-xs text-muted-foreground" data-testid="inspection-detail-scheduled-label">Scheduled</dt>
				<dd class="mt-1 font-medium" data-testid="inspection-detail-scheduled">{formatDateOnly(inspection.scheduledFor)}</dd>
			</div>
			<div data-testid="inspection-detail-completed-fact">
				<dt class="text-xs text-muted-foreground" data-testid="inspection-detail-completed-label">Completed</dt>
				<dd class="mt-1 font-medium" data-testid="inspection-detail-completed">{formatDateOnly(inspection.completedAt)}</dd>
			</div>
			{#if inspection.inspector}
				<div data-testid="inspection-detail-inspector-fact">
					<dt class="text-xs text-muted-foreground" data-testid="inspection-detail-inspector-label">Inspector</dt>
					<dd class="mt-1 font-medium" data-testid="inspection-detail-inspector">{inspection.inspector}</dd>
				</div>
			{/if}
		</dl>

		{#if inspection.outcome}
			<div class="rounded-lg border p-3 text-sm" data-testid="inspection-detail-outcome">
				<p class="text-xs text-muted-foreground" data-testid="inspection-detail-outcome-label">Outcome</p>
				<p class="mt-1" data-testid="inspection-detail-outcome-value">{inspection.outcome}</p>
			</div>
		{/if}
		{#if inspection.notes}
			<div class="rounded-lg border p-3 text-sm" data-testid="inspection-detail-notes">
				<p class="text-xs text-muted-foreground" data-testid="inspection-detail-notes-label">Notes</p>
				<p class="mt-1 whitespace-pre-wrap" data-testid="inspection-detail-notes-value">{inspection.notes}</p>
			</div>
		{/if}

		<section class="space-y-2" data-testid="inspection-detail-checklist">
			<h3 class="text-sm font-semibold" data-testid="inspection-detail-checklist-title">Checklist</h3>
			{#if inspectionItems.length === 0}
				<p class="rounded-lg border p-3 text-sm text-muted-foreground" data-testid="inspection-detail-checklist-empty">No checklist items were recorded.</p>
			{:else}
				<div class="divide-y rounded-lg border" data-testid="inspection-detail-checklist-items">
					{#each inspectionItems as item (item.id)}
						<article class="space-y-1 p-3" data-testid={`inspection-detail-item-${item.id}`}>
							<div class="flex flex-wrap items-center justify-between gap-2" data-testid={`inspection-detail-item-heading-${item.id}`}>
								<div class="min-w-0" data-testid={`inspection-detail-item-copy-${item.id}`}>
									<p class="font-medium" data-testid={`inspection-detail-item-label-${item.id}`}>{item.label}</p>
									<p class="text-xs text-muted-foreground" data-testid={`inspection-detail-item-area-${item.id}`}>{item.area || 'General'}</p>
								</div>
								<span data-testid={`inspection-detail-item-result-${item.id}`}><StatusBadge status={item.result} /></span>
							</div>
							<p class="text-xs text-muted-foreground" data-testid={`inspection-detail-item-result-label-${item.id}`}>{itemResultLabel(item)}</p>
							{#if item.note}
								<p class="whitespace-pre-wrap text-sm" data-testid={`inspection-detail-item-note-${item.id}`}>{item.note}</p>
							{/if}
						</article>
					{/each}
				</div>
			{/if}
		</section>
	{/if}
</div>
