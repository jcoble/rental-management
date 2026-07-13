<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { Receipt, CircleCheck, FileText } from '@lucide/svelte';
	import { tenantAccounts } from '$lib/api/endpoints/tenant-accounts';
	import { formatDateOnly } from '$lib/utils/date';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import RecordHistory from '$lib/components/shared/RecordHistory.svelte';
	import HeroCard from '$lib/components/shared/HeroCard.svelte';
	import { isMismatchedUnitSelection } from '$lib/unit/unit-membership-guard';

	let {
		tenantAccountId,
		tenantLedgerEntryId,
		expectedUnitId,
		onUnitMismatch,
	}: {
		tenantAccountId: number;
		tenantLedgerEntryId: number;
		expectedUnitId?: number;
		onUnitMismatch?: () => void;
	} = $props();

	const paymentQuery = createQuery(() => ({
		queryKey: ['tenant-ledger-entry', tenantAccountId, tenantLedgerEntryId],
		queryFn: () => tenantAccounts.entry(tenantAccountId, tenantLedgerEntryId),
		enabled: tenantAccountId > 0 && tenantLedgerEntryId > 0
	}));
	const receipt = $derived(paymentQuery.data);

	$effect(() => {
		if (isMismatchedUnitSelection(receipt, expectedUnitId)) onUnitMismatch?.();
	});

	function money(value: number, currency = 'USD') {
		return new Intl.NumberFormat('en-US', { style: 'currency', currency }).format(value);
	}

	function postedAt(value: string) {
		const date = new Date(value);
		return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
	}
</script>

<svelte:head>
	<title>Receipt - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="payment-detail-page">
	<div class="mb-5">
		<h1 class="text-2xl font-bold">Payment receipt</h1>
		<p class="text-sm text-muted-foreground">A posted receipt is permanent. Corrections use a separate reversal or adjustment.</p>
	</div>

	{#if paymentQuery.isLoading}
		<div class="rounded-lg border border-border bg-card p-6 text-sm text-muted-foreground">Loading receipt...</div>
	{:else if !receipt}
		<div class="rounded-lg border border-border bg-card p-6 text-sm text-muted-foreground">Receipt not found.</div>
	{:else}
		<HeroCard tone="success" testid="payment-hero" contentClass="flex flex-wrap items-end justify-between gap-6" class="mb-6">
			<div>
				<p class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Received</p>
				<p class="mt-1 font-mono text-4xl font-bold tabular-nums tracking-tight" data-testid="payment-hero-amount">{money(receipt.amount, receipt.currency)}</p>
				<p class="mt-2 text-sm text-muted-foreground">{formatDateOnly(receipt.effectiveOn)} · {receipt.tenantName || receipt.relationshipNumber}</p>
			</div>
			<p class="text-sm text-muted-foreground">Ledger entry #{receipt.tenantLedgerEntryId}</p>
		</HeroCard>

		<div class="grid gap-6 lg:grid-cols-2">
			<DetailCard title="Receipt" icon={Receipt} accent="success" testid="payment-card-receipt">
				<dl class="grid gap-4 sm:grid-cols-2">
					<div><dt class="text-xs text-muted-foreground">Date received</dt><dd class="font-medium">{formatDateOnly(receipt.effectiveOn)}</dd></div>
					<div><dt class="text-xs text-muted-foreground">Payment method</dt><dd class="font-medium">{receipt.providerAttempt?.paymentMethodSummary || 'Not specified'}</dd></div>
					<div><dt class="text-xs text-muted-foreground">Reference</dt><dd class="font-medium">{receipt.providerAttempt?.providerReference || '—'}</dd></div>
					<div><dt class="text-xs text-muted-foreground">Payer</dt><dd class="font-medium">{receipt.providerAttempt?.payerName || receipt.tenantName || '—'}</dd></div>
					{#if receipt.providerAttempt?.checkNumber}<div><dt class="text-xs text-muted-foreground">Check number</dt><dd class="font-medium">{receipt.providerAttempt.checkNumber}</dd></div>{/if}
					{#if receipt.providerAttempt?.bankName}<div><dt class="text-xs text-muted-foreground">Bank</dt><dd class="font-medium">{receipt.providerAttempt.bankName}</dd></div>{/if}
					<div class="sm:col-span-2"><dt class="text-xs text-muted-foreground">Description</dt><dd class="font-medium">{receipt.description}</dd></div>
				</dl>
			</DetailCard>

			<DetailCard title="Account" icon={CircleCheck} accent="primary" testid="payment-card-account">
				<dl class="grid gap-4 sm:grid-cols-2">
					<div><dt class="text-xs text-muted-foreground">Property</dt><dd class="font-medium">{receipt.propertyName || `Property #${receipt.propertyId}`}</dd></div>
					<div><dt class="text-xs text-muted-foreground">Unit</dt><dd class="font-medium">{receipt.unitNumber || `Unit #${receipt.unitId}`}</dd></div>
					<div><dt class="text-xs text-muted-foreground">Tenant account</dt><dd class="font-mono text-sm">{receipt.accountNumber}</dd></div>
					<div><dt class="text-xs text-muted-foreground">Relationship</dt><dd class="font-mono text-sm">{receipt.relationshipNumber}</dd></div>
					<div class="sm:col-span-2"><dt class="text-xs text-muted-foreground">Posted</dt><dd class="font-medium">{postedAt(receipt.postedAtUtc)}</dd></div>
				</dl>
			</DetailCard>

			{#if receipt.sourceStoredFileId}
				<DetailCard title="Source document" icon={FileText} accent="muted" testid="payment-card-scanned-document" class="lg:col-span-2">
					<a href="/document-file/{receipt.sourceStoredFileId}" target="_blank" rel="noopener noreferrer" class="text-sm font-medium text-primary underline underline-offset-4">Open the scanned source</a>
				</DetailCard>
			{/if}
		</div>

		<div class="mt-6 rounded-lg border border-border bg-card p-4" data-testid="payment-history-section">
			<h2 class="mb-1 text-base font-semibold">History</h2>
			<p class="mb-3 text-sm text-muted-foreground">The append-only posting and its audit trail.</p>
			<RecordHistory entityType="TenantLedgerEntry" entityId={tenantLedgerEntryId} />
		</div>
	{/if}
</div>
