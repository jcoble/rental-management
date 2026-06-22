<script lang="ts">
	import type { UnitDocumentSummary } from '$lib/types';
	import { formatDateOnly } from '$lib/utils/date';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import { Button } from '$lib/components/ui/button';
	import { FileText, ScanLine, Image as ImageIcon } from '@lucide/svelte';

	let {
		docs,
		onScan,
	}: {
		/** The unit's document set (files on the unit + its lease/payment/work-order/inspection children). */
		docs: UnitDocumentSummary[];
		onScan: () => void;
	} = $props();

	// Group by the child entity type the file is attached to (e.g. Lease, WorkOrder), so the user sees
	// the unit's paperwork organized by where it lives.
	const grouped = $derived.by(() => {
		const map = new Map<string, UnitDocumentSummary[]>();
		for (const d of docs) {
			const key = d.entityType ?? 'Other';
			const arr = map.get(key) ?? [];
			arr.push(d);
			map.set(key, arr);
		}
		return [...map.entries()].sort((a, b) => a[0].localeCompare(b[0]));
	});
</script>

<div class="space-y-4" data-testid="unit-documents-tab">
	<div class="flex flex-wrap items-center justify-between gap-3 rounded-xl border bg-card p-4">
		<p class="text-sm text-muted-foreground">
			Documents on this unit and its lease, work orders, payments, and inspections.
		</p>
		<Button class="gap-2" onclick={() => onScan()} data-testid="documents-scan">
			<ScanLine class="h-4 w-4" /> Scan / upload
		</Button>
	</div>

	{#if docs.length === 0}
		<DetailCard title="No documents yet" icon={FileText} accent="muted" testid="documents-empty">
			<p class="text-sm text-muted-foreground">Scan or upload a document — the computer extracts the fields for you to confirm.</p>
		</DetailCard>
	{:else}
		{#each grouped as [type, items] (type)}
			<DetailCard title={type} icon={FileText} accent="primary" testid="documents-group-{type}">
				<ul class="divide-y text-sm">
					{#each items as d (d.id)}
						<li class="flex items-center justify-between py-1.5">
							<span class="flex min-w-0 items-center gap-2">
								{#if d.contentType.startsWith('image/')}<ImageIcon class="h-4 w-4 shrink-0 text-muted-foreground" />{:else}<FileText class="h-4 w-4 shrink-0 text-muted-foreground" />{/if}
								<span class="truncate">{d.fileName}</span>
							</span>
							<span class="shrink-0 text-xs text-muted-foreground">{formatDateOnly(d.uploadedAt)}</span>
						</li>
					{/each}
				</ul>
			</DetailCard>
		{/each}
	{/if}
</div>
