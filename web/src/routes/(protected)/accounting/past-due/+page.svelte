<!--
  "Who's behind" — one row per tenant/lease currently behind on rent, with one-tap Mark-paid and Text
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
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { showError, showSuccess, apiErrorMessage } from '$lib/utils/toast';
	import { daysFromTodayUtc } from '$lib/utils/date';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';
	import PageHeader from '$lib/components/m3/PageHeader.svelte';
	import { AlertTriangle, CheckCircle2, MessageSquare, ChevronRight, Loader2, CircleCheckBig } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

	const pastDueQuery = createQuery(() => ({
		queryKey: ['accounting-past-due', portfolioId],
		enabled: portfolioId > 0,
		queryFn: () => accounting.pastDue(),
	}));

	const result = $derived(pastDueQuery.data);
	const behind = $derived(result?.items ?? []);

	function money(value: number) {
		return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', maximumFractionDigits: 0 }).format(value || 0);
	}

	function displayName(lease: PastDueLease): string {
		if (lease.tenantName && lease.tenantName.trim()) return lease.tenantName.trim();
		if (lease.leaseNumber) return `Lease ${lease.leaseNumber}`;
		return `Lease #${lease.leaseId}`;
	}

	function daysLate(lease: PastDueLease): number {
		const d = daysFromTodayUtc(lease.oldestDueDate);
		return d === null ? 0 : Math.max(0, -d);
	}

	function rowSubtitle(lease: PastDueLease): string {
		const parts: string[] = [];
		if (lease.overduePaymentCount > 1) {
			parts.push(`${lease.overduePaymentCount} payments`);
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

	// Which lease's row is currently being marked caught-up (its buttons show a spinner).
	let busyLeaseId = $state<number | null>(null);

	// Mark every still-owed past-due payment on a lease as paid. Mirrors the mobile OverdueScreen:
	// mark-paid is a per-payment action, so we resolve this lease's open payments and settle them. A
	// lease has only a handful of open payments, so this is a small bounded set — not an N+1 over the
	// whole list. Afterwards we refresh the shared sources (past-due list + dashboard snapshot) so the
	// KPI count and these rows update together and stay in lockstep.
	const markPaidMutation = createMutation(() => ({
		mutationFn: async (lease: PastDueLease) => {
			const leasePayments = await payments.list(portfolioId, { leaseId: lease.leaseId, take: 200 });
			const today = new Date().toISOString().slice(0, 10);
			const now = Date.now();
			const owed = leasePayments.filter((p) => {
				if (p.status === 'Paid' || p.status === 'Waived' || p.status === 'Refunded' || p.status === 'Failed') {
					return false;
				}
				return p.status === 'Late' || (p.dueDate ? new Date(p.dueDate).getTime() < now : false);
			});
			for (const p of owed) {
				await payments.markPaid(p.id, { paidDate: today });
			}
			return owed.length;
		},
		onMutate: (lease) => {
			busyLeaseId = lease.leaseId;
		},
		onSuccess: (count) => {
			showSuccess(count > 1 ? `${count} payments marked paid.` : 'Payment marked paid.');
			queryClient.invalidateQueries({ queryKey: ['accounting-past-due', portfolioId] });
			queryClient.invalidateQueries({ queryKey: ['accounting-snapshot', portfolioId] });
			queryClient.invalidateQueries({ queryKey: ['dashboard', portfolioId] });
			queryClient.invalidateQueries({ queryKey: ['payments'] });
		},
		onError: (err) => showError(apiErrorMessage(err, "Couldn't mark this lease paid. Please try again.")),
		onSettled: () => {
			busyLeaseId = null;
		},
	}));

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

	function openOldestPayment(lease: PastDueLease) {
		if (lease.oldestPaymentId > 0) {
			goto(`/accounting/payments/${lease.oldestPaymentId}`);
		}
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
		description="Tenants currently behind on rent. Send a reminder or mark them caught up."
		data-testid="past-due-header"
	/>

	{#if pastDueQuery.isLoading}
		<div class="flex h-48 items-center justify-center gap-2 text-sm text-muted-foreground" data-testid="past-due-loading">
			<Loader2 class="h-4 w-4 animate-spin" />
			Loading…
		</div>
	{:else if pastDueQuery.isError}
		<div class="flex h-48 items-center justify-center text-sm text-destructive" data-testid="past-due-error">
			Couldn't load who's behind.
		</div>
	{:else if behind.length === 0}
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
	{:else}
		<!-- Headline summary: restates the KPI number so the dashboard "N behind" ties to these rows. -->
		<div
			class="mb-4 flex items-center gap-2 rounded-lg border border-warning/40 bg-warning/10 px-4 py-3 text-sm text-foreground"
			data-testid="past-due-summary"
		>
			<AlertTriangle class="h-4 w-4 shrink-0 text-warning" />
			<span>
				<span class="font-semibold">{result?.totalCount ?? behind.length}</span>
				{(result?.totalCount ?? behind.length) === 1 ? 'tenant' : 'tenants'} behind, owing
				<span class="font-semibold">{money(result?.totalPastDueAmount ?? 0)}</span>
			</span>
		</div>

		<div class="space-y-3" data-testid="past-due-list">
			{#each behind as lease (lease.leaseId)}
				{@const days = daysLate(lease)}
				{@const subtitle = rowSubtitle(lease)}
				{@const busy = busyLeaseId === lease.leaseId}
				<Card.Root data-testid="past-due-row" data-lease-id={lease.leaseId}>
					<Card.Content class="p-4">
						<button
							type="button"
							class="flex w-full items-center gap-3 text-left {lease.oldestPaymentId > 0 ? 'cursor-pointer' : 'cursor-default'}"
							onclick={() => openOldestPayment(lease)}
							disabled={lease.oldestPaymentId <= 0}
							data-testid="past-due-row-open"
						>
							<span class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-destructive/10 text-destructive">
								<AlertTriangle class="h-5 w-5" />
							</span>
							<span class="min-w-0 flex-1">
								<span class="block truncate font-semibold text-foreground" data-testid="past-due-row-name">{displayName(lease)}</span>
								<span class="block text-sm font-medium text-destructive">
									{money(lease.pastDueAmount)} · {days > 0 ? `${days} ${days === 1 ? 'day' : 'days'} late` : 'Past due'}
								</span>
								{#if subtitle}
									<span class="block truncate text-xs text-muted-foreground">{subtitle}</span>
								{/if}
							</span>
							{#if lease.oldestPaymentId > 0}
								<ChevronRight class="h-5 w-5 shrink-0 text-muted-foreground" />
							{/if}
						</button>

						<div class="mt-3 flex gap-2">
							<Button
								variant="secondary"
								size="sm"
								class="flex-1 gap-1.5"
								disabled={busy}
								onclick={() => markPaidMutation.mutate(lease)}
								data-testid="past-due-mark-paid"
							>
								{#if busy}
									<Loader2 class="h-4 w-4 animate-spin" />
								{:else}
									<CheckCircle2 class="h-4 w-4" />
								{/if}
								{lease.overduePaymentCount > 1 ? 'Mark all paid' : 'Mark paid'}
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
	{/if}
</div>
