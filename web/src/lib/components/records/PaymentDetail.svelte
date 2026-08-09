<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { Receipt, CircleCheck, FileText } from '@lucide/svelte';
	import { tenantAccounts } from '$lib/api/endpoints/tenant-accounts';
	import {
		isTenantPaymentRefundConflict,
		linkedTenantPaymentRefund,
		payments
	} from '$lib/api/endpoints/payments';
	import { ApiError } from '$lib/api/client';
	import { currentCapabilities } from '$lib/stores/auth.svelte';
	import {
		canCorrectPayment,
		paymentCorrectionContext
	} from '$lib/components/unit/money';
	import { formatDateOnly } from '$lib/utils/date';
	import { formatResidentName } from '$lib/accounting/money-display';
	import { apiErrorMessage } from '$lib/utils/toast';
	import AccountingImpactCard from '$lib/components/accounting/AccountingImpactCard.svelte';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import RecordHistory from '$lib/components/shared/RecordHistory.svelte';
	import HeroCard from '$lib/components/shared/HeroCard.svelte';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
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

	const queryClient = useQueryClient();
	const paymentQuery = createQuery(() => ({
		queryKey: ['tenant-ledger-entry', tenantAccountId, tenantLedgerEntryId],
		queryFn: () => tenantAccounts.entry(tenantAccountId, tenantLedgerEntryId),
		enabled: tenantAccountId > 0 && tenantLedgerEntryId > 0
	}));
	const receipt = $derived(paymentQuery.data);
	const entryKind = $derived.by(() => {
		if (!receipt) return 'payment';
		if (['RentCharge', 'AddendumCharge', 'LateFeeCharge', 'DepositCharge', 'ManualCharge', 'OpeningBalance'].includes(receipt.entryType)) return 'charge';
		if (['Credit', 'Adjustment', 'Refund', 'Reversal'].includes(receipt.entryType)) return 'credit';
		return 'payment';
	});
	const detailCopy = $derived(
		entryKind === 'charge'
			? { title: 'Charge detail', intro: 'This charge stays in the account history so the amount owed remains traceable.', amountLabel: 'Charged', cardTitle: 'Charge', recordLabel: 'Charge record', sourceType: 'TenantCharge' as const }
			: entryKind === 'credit'
				? { title: 'Credit detail', intro: 'This credit stays in the account history so the balance change remains traceable.', amountLabel: 'Credited', cardTitle: 'Credit', recordLabel: 'Credit record', sourceType: 'TenantConcession' as const }
				: { title: 'Payment receipt', intro: 'This receipt stays in your records. If something is wrong, record a correction so the history remains complete.', amountLabel: 'Received', cardTitle: 'Receipt', recordLabel: 'Payment record', sourceType: 'TenantReceipt' as const }
	);
	const correctionAllowed = $derived(
		canCorrectPayment(currentCapabilities(), receipt?.entryType)
	);
	let showCorrection = $state(false);
	let correctionKey = $state<string | null>(null);
	let correction = $state<ReturnType<typeof paymentCorrectionContext> | null>(null);
	let correctionResult = $state<NonNullable<ReturnType<typeof linkedTenantPaymentRefund>> | null>(null);
	let correctionError = $state('');

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

	function openCorrection() {
		if (!receipt || !correctionAllowed) return;
		correction = paymentCorrectionContext(receipt);
		correctionKey = crypto.randomUUID();
		correctionResult = null;
		correctionError = '';
		showCorrection = true;
	}

	function closeCorrection() {
		showCorrection = false;
		correction = null;
		correctionKey = null;
		correctionError = '';
	}

	const correctionMutation = createMutation(() => ({
		mutationFn: async () => {
			if (!correctionAllowed || !correction || !correctionKey) {
				throw new Error('Payment correction is not authorized.');
			}
			return payments.refundPayment(tenantAccountId, correctionKey, {
				paymentEntryId: tenantLedgerEntryId,
				effectiveOn: correction.effectiveOn,
				reason: correction.reason.trim(),
				paymentMethodSummary: correction.paymentMethodSummary.trim() || undefined,
				externalReference: correction.externalReference.trim() || undefined,
				sourceStoredFileId: correction.sourceStoredFileId
			});
		},
		onSuccess: (response) => {
			correctionResult = linkedTenantPaymentRefund(response);
			correctionError = correctionResult ? '' : 'The payment was not changed.';
			queryClient.invalidateQueries({ queryKey: ['tenant-account-entries', tenantAccountId] });
			queryClient.invalidateQueries({ queryKey: ['tenant-ledger-entry', tenantAccountId, tenantLedgerEntryId] });
		},
		onError: (error) => {
			correctionResult = null;
			const conflict = error instanceof ApiError ? error.extensions : error;
			correctionError = isTenantPaymentRefundConflict(conflict)
				? conflict.error?.trim() || 'This payment cannot be corrected again.'
				: apiErrorMessage(error, 'The payment was not changed.');
		}
	}));
</script>

<svelte:head><title>{detailCopy.title} - Rental Command</title></svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="payment-detail-page">
	<div class="mb-5 flex flex-wrap items-start justify-between gap-3">
		<div>
			<h1 class="text-2xl font-bold" data-testid="tenant-entry-detail-title">{detailCopy.title}</h1>
			<p class="text-sm text-muted-foreground">{detailCopy.intro}</p>
		</div>
		<Button variant="ghost" size="sm" href="/docs/recording-payments" data-testid="payment-detail-help-link">How this works</Button>
	</div>

	{#if paymentQuery.isLoading}
		<LoadingState label="Loading account entry" testid="payment-detail-loading" />
	{:else if paymentQuery.isError}
		<div class="rounded-lg border border-destructive/40 bg-destructive/5 p-6" role="alert" data-testid="payment-detail-error">
			<p class="font-medium text-destructive">Could not load this account entry.</p>
			<p class="mt-1 text-sm text-muted-foreground">Try again. The entry has not been reported as missing.</p>
			<Button class="mt-4" variant="outline" onclick={() => paymentQuery.refetch()}>Try again</Button>
		</div>
	{:else if !receipt}
		<div class="rounded-lg border border-border bg-card p-6 text-sm text-muted-foreground" data-testid="payment-detail-not-found">Account entry not found.</div>
	{:else}
		<HeroCard tone="success" testid="payment-hero" contentClass="flex flex-wrap items-end justify-between gap-6" class="mb-6">
			<div>
				<p class="text-xs font-medium uppercase tracking-wide text-muted-foreground">{detailCopy.amountLabel}</p>
				<p class="mt-1 font-mono text-4xl font-bold tabular-nums tracking-tight" data-testid="payment-hero-amount">{money(receipt.amount, receipt.currency)}</p>
				<p class="mt-2 text-sm text-muted-foreground">{formatDateOnly(receipt.effectiveOn)} · {formatResidentName(receipt.tenantName)}</p>
			</div>
			<div class="flex flex-col items-end gap-3">
				{#if correctionAllowed}
					<Button variant="outline" onclick={openCorrection} data-testid="correct-payment-action">Fix this payment</Button>
				{/if}
			</div>
		</HeroCard>

		{#if showCorrection && correction}
			<div class="mb-6 rounded-lg border border-border bg-card p-4" data-testid="payment-correction-form">
				<div class="flex items-start justify-between gap-4">
					<div>
						<h2 class="font-semibold">Fix this payment</h2>
						<p class="text-sm text-muted-foreground">The original receipt stays in history. Rental Command records a matching correction instead of rewriting it.</p>
					</div>
					<Button variant="ghost" size="sm" onclick={closeCorrection}>Cancel</Button>
				</div>
				<dl class="mt-4 grid gap-3 rounded-md bg-muted/30 p-3 text-sm sm:grid-cols-3">
					<div><dt class="text-xs text-muted-foreground">Rental</dt><dd class="font-medium">{correction.propertyName} · Unit {correction.unitNumber}</dd></div>
					<div><dt class="text-xs text-muted-foreground">Tenant</dt><dd class="font-medium">{correction.tenantName}</dd></div>
					<div><dt class="text-xs text-muted-foreground">Original payment</dt><dd class="font-medium">{money(correction.amount, receipt.currency)} · {formatDateOnly(receipt.effectiveOn)}</dd></div>
				</dl>
				<div class="mt-4 grid gap-3 sm:grid-cols-2">
					<label class="text-xs font-medium text-muted-foreground">Correction date<DatePicker bind:value={correction.effectiveOn} /></label>
					<label class="text-xs font-medium text-muted-foreground">Payment method<Input bind:value={correction.paymentMethodSummary} /></label>
					<label class="text-xs font-medium text-muted-foreground sm:col-span-2">Reason<Input bind:value={correction.reason} /></label>
					<label class="text-xs font-medium text-muted-foreground sm:col-span-2">Refund reference<Input bind:value={correction.externalReference} placeholder="Check or payout confirmation number" /></label>
				</div>
				{#if correctionError}
					<p class="mt-3 text-sm text-destructive" data-testid="payment-correction-conflict">{correctionError} Nothing was changed.</p>
				{/if}
				{#if correctionResult}
					<div class="mt-3 text-sm text-success" data-testid="payment-correction-result">
						<p>Payment correction recorded. The original receipt remains in history.</p>
						<details class="mt-2 text-xs text-muted-foreground">
							<summary class="cursor-pointer font-medium">Technical details</summary>
							<p class="mt-1">Correction entry #{correctionResult.refundEntryId}; {correctionResult.compensatedAllocationCount} linked amount assignment(s) corrected.</p>
						</details>
					</div>
				{/if}
				<div class="mt-4 flex justify-end">
					<Button
						onclick={() => correctionMutation.mutate()}
						disabled={correctionMutation.isPending || !correction.reason.trim() || !correction.paymentMethodSummary.trim() || !correction.externalReference.trim()}
						data-testid="payment-correction-submit"
					>
						{correctionMutation.isPending ? 'Recording…' : 'Record correction'}
					</Button>
				</div>
			</div>
		{/if}

		<div class="grid gap-6 lg:grid-cols-2">
			<DetailCard title={detailCopy.cardTitle} icon={Receipt} accent="success" testid="payment-card-receipt">
				<dl class="grid gap-4 sm:grid-cols-2">
					<div><dt class="text-xs text-muted-foreground">Date received</dt><dd class="font-medium">{formatDateOnly(receipt.effectiveOn)}</dd></div>
					{#if entryKind === 'payment'}
						<div><dt class="text-xs text-muted-foreground">Payment method</dt><dd class="font-medium">{receipt.providerAttempt?.paymentMethodSummary || 'Not specified'}</dd></div>
						<div><dt class="text-xs text-muted-foreground">Reference</dt><dd class="font-medium">{receipt.providerAttempt?.providerReference || '—'}</dd></div>
						<div><dt class="text-xs text-muted-foreground">Payer</dt><dd class="font-medium">{receipt.providerAttempt?.payerName || receipt.tenantName || '—'}</dd></div>
					{/if}
					{#if receipt.providerAttempt?.checkNumber}<div><dt class="text-xs text-muted-foreground">Check number</dt><dd class="font-medium">{receipt.providerAttempt.checkNumber}</dd></div>{/if}
					{#if receipt.providerAttempt?.bankName}<div><dt class="text-xs text-muted-foreground">Bank</dt><dd class="font-medium">{receipt.providerAttempt.bankName}</dd></div>{/if}
					<div class="sm:col-span-2"><dt class="text-xs text-muted-foreground">Description</dt><dd class="font-medium">{receipt.description}</dd></div>
				</dl>
			</DetailCard>

			<DetailCard title="Rental" icon={CircleCheck} accent="primary" testid="payment-card-account">
				<dl class="grid gap-4 sm:grid-cols-2">
					<div><dt class="text-xs text-muted-foreground">Property</dt><dd class="font-medium">{receipt.propertyName || 'Property not listed'}</dd></div>
					<div><dt class="text-xs text-muted-foreground">Unit</dt><dd class="font-medium">{receipt.unitNumber ? `Unit ${receipt.unitNumber}` : 'Unit not listed'}</dd></div>
					<div><dt class="text-xs text-muted-foreground">Tenant</dt><dd class="font-medium">{formatResidentName(receipt.tenantName)}</dd></div>
					<div class="sm:col-span-2"><dt class="text-xs text-muted-foreground">Posted</dt><dd class="font-medium">{postedAt(receipt.postedAtUtc)}</dd></div>
				</dl>
			</DetailCard>

			{#if receipt.sourceStoredFileId}
				<DetailCard title="Source document" icon={FileText} accent="muted" testid="payment-card-scanned-document" class="lg:col-span-2">
					<a href="/document-file/{receipt.sourceStoredFileId}" target="_blank" rel="noopener noreferrer" class="text-sm font-medium text-primary underline underline-offset-4">Open the scanned source</a>
				</DetailCard>
			{/if}

			<details class="rounded-lg border border-border bg-card lg:col-span-2" data-testid="payment-technical-details">
				<summary class="cursor-pointer px-4 py-3 text-sm font-medium">Technical details</summary>
				<dl class="grid gap-3 border-t border-border px-4 py-3 text-sm sm:grid-cols-3">
					<div><dt class="text-xs text-muted-foreground">Rental account reference</dt><dd class="font-mono">{receipt.accountNumber}</dd></div>
					<div><dt class="text-xs text-muted-foreground">Rental reference</dt><dd class="font-mono">{receipt.relationshipNumber}</dd></div>
					<div><dt class="text-xs text-muted-foreground">{detailCopy.recordLabel}</dt><dd class="font-mono">#{receipt.tenantLedgerEntryId}</dd></div>
				</dl>
			</details>
		</div>

		<AccountingImpactCard sourceType={detailCopy.sourceType} sourceId={tenantLedgerEntryId} />

		<div class="mt-6 rounded-lg border border-border bg-card p-4" data-testid="payment-history-section">
			<h2 class="mb-1 text-base font-semibold">History</h2>
			<p class="mb-3 text-sm text-muted-foreground">Every recorded change to this payment, including corrections.</p>
			<RecordHistory entityType="TenantAccount" entityId={tenantAccountId} />
		</div>
	{/if}
</div>
