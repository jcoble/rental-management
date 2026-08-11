<!--
  "Who's behind" — one row per tenant account currently behind on rent, with Record payment and Text
  actions. This is the web counterpart of the mobile OverdueScreen and the destination behind the
  dashboard "tenants behind" KPI.

  The rows come from GET /accounting/past-due (the `accounting.pastDue()` endpoint), which shares the
  money snapshot's past-due definition server-side. That guarantees the count here equals the dashboard
  KPI — both read from the same source, so they can't drift. We do NOT re-derive "who's behind" on the
  client.
-->
<script lang="ts">
	import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { accounting } from '$lib/api/endpoints/accounting';
	import { payments } from '$lib/api/endpoints/payments';
	import type { PastDueLease } from '$lib/types';
	import { loadAllOpenCharges } from '$lib/accounting/past-due-preview';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { showError, showSuccess, apiErrorMessage } from '$lib/utils/toast';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import PageHeader from '$lib/components/m3/PageHeader.svelte';
	import PastDuePaymentDialog, { type PastDuePaymentSubmission } from '$lib/components/accounting/PastDuePaymentDialog.svelte';
	import { AlertTriangle, CheckCircle2, MessageSquare, ChevronLeft, ChevronRight, CircleCheckBig, Loader2 } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const PAGE_SIZE = 20;
	let skip = $state(0);

	const pastDueQuery = createQuery(() => ({
		queryKey: ['accounting-past-due', portfolioId, skip, PAGE_SIZE],
		enabled: portfolioId > 0,
		queryFn: () => accounting.pastDue({ skip, take: PAGE_SIZE }),
	}));

	const result = $derived(pastDueQuery.data);
	const behind = $derived(result?.items ?? []);
	const hasPrevious = $derived((result?.skip ?? skip) > 0);
	const hasNext = $derived(
		result != null && result.skip + result.items.length < result.totalCount
	);

	$effect(() => {
		if (result && result.totalCount > 0 && result.items.length === 0 && skip > 0) {
			skip = Math.max(0, skip - PAGE_SIZE);
		}
	});

	function money(value: number) {
		return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', maximumFractionDigits: 0 }).format(value || 0);
	}

	function displayName(lease: PastDueLease): string {
		if (lease.tenantName && lease.tenantName.trim()) return lease.tenantName.trim();
		if (lease.relationshipNumber && lease.relationshipNumber.trim()) return lease.relationshipNumber.trim();
		if (lease.unitNumber && lease.unitNumber.trim()) return `Unit ${lease.unitNumber.trim()}`;
		return 'Tenant account';
	}

	function daysLate(lease: PastDueLease): number | null {
		if (!result?.businessDate) return null;
		const due = Date.parse(`${lease.oldestDueOn.slice(0, 10)}T00:00:00Z`);
		const business = Date.parse(`${result.businessDate.slice(0, 10)}T00:00:00Z`);
		if (!Number.isFinite(due) || !Number.isFinite(business)) return null;
		return Math.max(0, Math.floor((business - due) / 86_400_000));
	}

	function rowSubtitle(lease: PastDueLease): string {
		const parts: string[] = [];
		if (lease.overduePaymentCount > 1) {
			parts.push(`${lease.overduePaymentCount} charges`);
		}
		if (lease.propertyName && lease.propertyName.trim()) {
			parts.push(
				lease.unitNumber && lease.unitNumber.trim()
					? `${lease.propertyName} · Unit ${lease.unitNumber}`
					: lease.propertyName
			);
		}
		return parts.join(' · ');
	}

	// Which tenant account is currently receiving a payment (its buttons show a spinner).
	let busyAccountId = $state<number | null>(null);

	let showMarkPaidForm = $state(false);
	let markPaidTarget = $state<PastDueLease | null>(null);
	let receiptOperationKey = $state<string | null>(null);

	function openMarkPaid(lease: PastDueLease) {
		markPaidTarget = lease;
		showMarkPaidForm = true;
		receiptOperationKey = null;
	}
	function closeMarkPaid() {
		showMarkPaidForm = false;
		markPaidTarget = null;
		receiptOperationKey = null;
	}

	const openChargesQuery = createQuery(() => {
		const tenantAccountId = markPaidTarget?.tenantAccountId;
		return {
			queryKey: ['past-due-open-charges', portfolioId, tenantAccountId],
			enabled: showMarkPaidForm && tenantAccountId != null,
			queryFn: () => loadAllOpenCharges(tenantAccountId as number)
		};
	});
	const openCharges = $derived(openChargesQuery.data ?? []);

	// Record one receipt against the continuous tenant account. Afterwards we
	// refresh the shared sources (past-due list + dashboard snapshot) so the KPI count and these rows
	// update together and stay in lockstep.
	const markPaidMutation = createMutation(() => ({
		mutationFn: async ({ lease, data }: { lease: PastDueLease; data: PastDuePaymentSubmission }) => {
			receiptOperationKey ??= crypto.randomUUID();
			return payments.recordReceipt(lease.tenantAccountId, receiptOperationKey, {
				amount: data.amount,
				effectiveOn: data.paidDate,
				description: data.notes || `Payment for ${lease.relationshipNumber || 'tenant account'}`,
				paymentMethodSummary: data.method,
				externalReference: data.externalReference,
				payerName: lease.tenantName || undefined,
				targetChargeEntryId: null,
				allocateOldestCharges: true
			});
		},
		onMutate: ({ lease }) => {
			busyAccountId = lease.tenantAccountId;
		},
		onSuccess: (_result, _vars) => {
			showSuccess('Payment recorded and allocated oldest-first.');
			closeMarkPaid();
			skip = 0;
			queryClient.invalidateQueries({ queryKey: ['accounting-past-due', portfolioId] });
			queryClient.invalidateQueries({ queryKey: ['accounting-snapshot', portfolioId] });
			queryClient.invalidateQueries({ queryKey: ['dashboard', portfolioId] });
			queryClient.invalidateQueries({ queryKey: ['payments'] });
		},
		onError: (err) => showError(apiErrorMessage(err, "Couldn't record this payment. Please try again.")),
		onSettled: () => {
			busyAccountId = null;
		},
	}));

	function submitMarkPaid(data: PastDuePaymentSubmission) {
		if (!markPaidTarget) return;
		markPaidMutation.mutate({ lease: markPaidTarget, data });
	}

	// Open the device/desktop SMS composer pre-filled with a friendly reminder. We pre-fill the
	// recipient when we have a phone on file; otherwise the user picks the contact in their app.
	function textTenant(lease: PastDueLease) {
		const first = lease.tenantName?.trim().split(' ')[0];
		const greeting = first ? ` ${first}` : '';
		const body = encodeURIComponent(
			`Hi${greeting}, a friendly reminder that ${money(lease.pastDueAmount)} rent is past due.`
		);
		const phone = lease.tenantPhone?.trim() ?? '';
		window.location.href = phone ? `sms:${phone}?body=${body}` : `sms:?body=${body}`;
	}

	function openTenantAccount(lease: PastDueLease) {
		goto(`/units/${lease.unitId}?tab=money&view=tenant-account&tenantAccount=${lease.tenantAccountId}`);
	}
</script>

<svelte:head>
	<title>Who's behind - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="past-due-page">
	<PageHeader
		class="mb-4"
		band
		art={3}
		eyebrow="Money"
		title="Who's behind"
		description="Tenants currently behind on rent. Send a reminder or record money received."
		data-testid="past-due-header"
	/>

	{#if pastDueQuery.isLoading}
		<LoadingState label="Loading past-due accounts" variant="page" testid="past-due-loading" />
	{:else if pastDueQuery.isError}
		<div class="flex min-h-48 flex-wrap items-center justify-center gap-3 rounded-lg border border-destructive/40 bg-destructive/5 p-6" role="alert" data-testid="past-due-error">
			<p class="text-sm text-destructive">Couldn't load who's behind.</p>
			<Button size="sm" variant="outline" onclick={() => pastDueQuery.refetch()}>Retry past-due accounts</Button>
		</div>
	{:else if (result?.totalCount ?? 0) === 0}
		<Card.Root data-testid="past-due-empty">
			<Card.Content class="flex flex-col items-center gap-3 py-14 text-center">
				<CircleCheckBig class="h-12 w-12 text-success" />
				<p class="text-base font-medium text-foreground">Everyone is current. Nice.</p>
				<p class="max-w-sm text-sm text-muted-foreground">
					No tenants are behind on rent right now. This list fills in automatically when a payment
					goes past due.
				</p>
			</Card.Content>
		</Card.Root>
	{:else if !result?.businessDate}
		<div class="flex h-48 items-center justify-center text-sm text-destructive" data-testid="past-due-error">
			Couldn't read the portfolio business date.
		</div>
	{:else}
		<!-- Headline summary: restates the KPI number so the dashboard "N behind" ties to these rows. -->
		<div
			class="mb-4 flex items-center gap-2 rounded-lg border border-warning/40 bg-warning/10 px-4 py-3 text-sm text-foreground"
			data-testid="past-due-summary"
		>
			<AlertTriangle class="h-4 w-4 shrink-0 text-warning" />
			<span>
				<span class="font-semibold">{result?.totalCount ?? behind.length}</span>
				{(result?.totalCount ?? behind.length) === 1 ? 'rental' : 'rentals'} behind, owing
				<span class="font-semibold">{money(result?.totalPastDueAmount ?? 0)}</span>
			</span>
		</div>

		<div class="space-y-3" data-testid="past-due-list">
			{#each behind as lease (lease.tenantAccountId)}
				{@const days = daysLate(lease)}
				{@const subtitle = rowSubtitle(lease)}
				{@const busy = busyAccountId === lease.tenantAccountId}
				<Card.Root data-testid="past-due-row" data-tenant-account-id={lease.tenantAccountId}>
					<Card.Content class="p-4">
						<button
							type="button"
							class="flex w-full items-center gap-3 text-left"
							onclick={() => openTenantAccount(lease)}
							data-testid="past-due-row-open"
						>
							<span class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-destructive/10 text-destructive">
								<AlertTriangle class="h-5 w-5" />
							</span>
							<span class="min-w-0 flex-1">
								<span class="block truncate font-semibold text-foreground" data-testid="past-due-row-name">{displayName(lease)}</span>
								<span class="block text-sm font-medium text-destructive">
									{money(lease.pastDueAmount)} · {days !== null && days > 0 ? `${days} ${days === 1 ? 'day' : 'days'} late` : 'Past due'}
								</span>
								{#if subtitle}
									<span class="block truncate text-xs text-muted-foreground">{subtitle}</span>
								{/if}
							</span>
							<ChevronRight class="h-5 w-5 shrink-0 text-muted-foreground" />
						</button>

						<div class="mt-3 flex gap-2">
							<Button
								variant="secondary"
								size="sm"
								class="flex-1 gap-1.5"
								disabled={busy}
								onclick={() => openMarkPaid(lease)}
								data-testid="past-due-mark-paid"
							>
								{#if busy}
									<Loader2 class="h-4 w-4 animate-spin" />
								{:else}
									<CheckCircle2 class="h-4 w-4" />
								{/if}
								Record payment
							</Button>
							<Button
								variant="outline"
								size="sm"
								class="flex-1 gap-1.5"
								onclick={() => textTenant(lease)}
								data-testid="past-due-text"
							>
								<MessageSquare class="h-4 w-4" />
								Text
							</Button>
						</div>
					</Card.Content>
				</Card.Root>
			{/each}
		</div>

		{#if hasPrevious || hasNext}
			<div class="mt-4 flex items-center justify-between gap-3" data-testid="past-due-pagination">
				<Button
					variant="outline"
					disabled={!hasPrevious || pastDueQuery.isFetching}
					onclick={() => { skip = Math.max(0, skip - PAGE_SIZE); }}
					data-testid="past-due-previous"
				>
					<ChevronLeft class="h-4 w-4" />
					Previous
				</Button>
				<span class="text-xs text-muted-foreground">
					Showing {(result?.skip ?? 0) + 1}–{(result?.skip ?? 0) + behind.length} of {result?.totalCount ?? 0}
				</span>
				<Button
					variant="outline"
					disabled={!hasNext || pastDueQuery.isFetching}
					onclick={() => { skip += PAGE_SIZE; }}
					data-testid="past-due-next"
				>
					Next
					<ChevronRight class="h-4 w-4" />
				</Button>
			</div>
		{/if}
	{/if}
</div>

<PastDuePaymentDialog
	open={showMarkPaidForm}
	target={markPaidTarget}
	openCharges={openCharges}
	totalOpenAmount={markPaidTarget?.totalOpenBalance ?? 0}
	businessDate={result?.businessDate}
	openChargesLoading={openChargesQuery.isLoading}
	openChargesError={openChargesQuery.isError}
	pending={markPaidMutation.isPending}
	onclose={closeMarkPaid}
	onsubmit={submitMarkPaid}
/>
