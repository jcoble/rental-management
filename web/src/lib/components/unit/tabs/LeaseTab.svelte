<script lang="ts">
	import type { UnitDashboard } from '$lib/types';
	import { money } from '../money';
	import { formatDateOnly } from '$lib/utils/date';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { Button } from '$lib/components/ui/button';
	import { FileText, ExternalLink, ScanLine } from '@lucide/svelte';

	let {
		dashboard,
		onScan,
	}: {
		dashboard: UnitDashboard;
		onScan: () => void;
	} = $props();

	const lease = $derived(dashboard.currentLease);
	const tenant = $derived(dashboard.currentTenant);
</script>

<div class="space-y-4" data-testid="unit-lease-tab">
	{#if lease}
		<DetailCard title="Current lease" icon={FileText} accent="primary" testid="lease-current">
			{#snippet actions()}
				<a href="/leases/{lease.id}" class="inline-flex items-center gap-1 text-xs text-primary hover:underline">
					Open lease <ExternalLink class="h-3 w-3" />
				</a>
			{/snippet}
			<dl class="grid grid-cols-2 gap-3 text-sm sm:grid-cols-3">
				<div><dt class="text-muted-foreground">Lease #</dt><dd class="font-medium">{lease.leaseNumber}</dd></div>
				<div><dt class="text-muted-foreground">Status</dt><dd><StatusBadge status={lease.status} /></dd></div>
				<div><dt class="text-muted-foreground">Tenant</dt><dd class="font-medium">{tenant?.name ?? '—'}</dd></div>
				<div><dt class="text-muted-foreground">Rent</dt><dd class="font-medium">{money(lease.monthlyRent)}</dd></div>
				<div><dt class="text-muted-foreground">Deposit</dt><dd class="font-medium">{money(lease.securityDeposit)}</dd></div>
				<div><dt class="text-muted-foreground">Start</dt><dd>{formatDateOnly(lease.startDate)}</dd></div>
				<div><dt class="text-muted-foreground">End</dt><dd>{formatDateOnly(lease.endDate)}</dd></div>
			</dl>
		</DetailCard>
	{:else}
		<DetailCard title="No current lease" icon={FileText} accent="muted" testid="lease-empty">
			<p class="text-sm text-muted-foreground">
				This unit has no current lease. Scan an existing signed lease to extract its terms, or create one from the Leases page.
			</p>
		</DetailCard>
	{/if}

	<div class="flex flex-wrap gap-2">
		<Button variant="outline" class="gap-2" onclick={() => onScan()} data-testid="lease-scan">
			<ScanLine class="h-4 w-4" /> Scan / upload lease
		</Button>
	</div>
</div>
