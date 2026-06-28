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
	import { recordHref } from '$lib/navigation/record-href';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { PAYMENT_METHODS } from '$lib/constants/payments';
	import { showError, showSuccess, apiErrorMessage } from '$lib/utils/toast';
	import { daysFromTodayUtc } from '$lib/utils/date';
	import * as Card from '$lib/components/ui/card';
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Select from '$lib/components/ui/select';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
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

	// --- Mark Paid modal ---
	// Capture how/when the money arrived once, then apply it to every still-owed payment on the lease.
	// Smart defaults: date = today, method = last-used (remembered in localStorage). The mark-paid
	// endpoint accepts paidDate / method / externalReference / notes. Methods come from the shared
	// canonical list so web + mobile offer identical values.
	const LAST_METHOD_KEY = 'rc.payments.lastMethod';
	function loadLastMethod(): string {
		if (typeof localStorage === 'undefined') return '';
		try {
			return localStorage.getItem(LAST_METHOD_KEY) ?? '';
		} catch {
			return '';
		}
	}
	function rememberLastMethod(method: string) {
		if (!method || typeof localStorage === 'undefined') return;
		try {
			localStorage.setItem(LAST_METHOD_KEY, method);
		} catch {
			/* storage may be unavailable */
		}
	}
	function todayLocal(): string {
		const d = new Date();
		const p = (n: number) => String(n).padStart(2, '0');
		return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
	}

	let showMarkPaidForm = $state(false);
	let markPaidTarget = $state<PastDueLease | null>(null);
	let markPaidForm = $state({ paidDate: '', method: '', externalReference: '', notes: '' });

	function openMarkPaid(lease: PastDueLease) {
		markPaidTarget = lease;
		markPaidForm = { paidDate: todayLocal(), method: loadLastMethod(), externalReference: '', notes: '' };
		showMarkPaidForm = true;
	}
	function closeMarkPaid() {
		showMarkPaidForm = false;
		markPaidTarget = null;
	}

	// Mark every still-owed past-due payment on a lease as paid in one server-side action. Afterwards we
	// refresh the shared sources (past-due list + dashboard snapshot) so the KPI count and these rows
	// update together and stay in lockstep.
	const markPaidMutation = createMutation(() => ({
		mutationFn: async ({ lease, data }: { lease: PastDueLease; data: Record<string, unknown> }) => {
			const result = await payments.markLeasePastDuePaid(lease.leaseId, data);
			return result.markedPaidCount;
		},
		onMutate: ({ lease }) => {
			busyLeaseId = lease.leaseId;
		},
		onSuccess: (count, vars) => {
			showSuccess(count > 1 ? `${count} payments marked paid.` : 'Payment marked paid.');
			rememberLastMethod(String(vars.data.method ?? ''));
			closeMarkPaid();
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

	function submitMarkPaid() {
		if (!markPaidTarget) return;
		const data: Record<string, unknown> = {};
		data.paidDate = markPaidForm.paidDate || todayLocal();
		if (markPaidForm.method) data.method = markPaidForm.method;
		if (markPaidForm.externalReference.trim()) data.externalReference = markPaidForm.externalReference.trim();
		if (markPaidForm.notes.trim()) data.notes = markPaidForm.notes.trim();
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

	function openOldestPayment(lease: PastDueLease) {
		if (lease.oldestPaymentId > 0) {
			goto(recordHref('payment', { id: lease.oldestPaymentId, unitId: lease.unitId }));
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
				{(result?.totalCount ?? behind.length) === 1 ? 'rental' : 'rentals'} behind, owing
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
								onclick={() => openMarkPaid(lease)}
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

<!-- Mark Paid: capture how/when the money arrived once, then settle every owed payment on the lease.
	 Smart defaults (today + last-used method) keep it a quick confirm. -->
<Dialog.Root
	open={showMarkPaidForm}
	onOpenChange={(v) => { if (!v) closeMarkPaid(); }}
>
	<Dialog.Content class="max-w-md" data-testid="past-due-mark-paid-dialog">
		<Dialog.Header>
			<Dialog.Title>Mark paid</Dialog.Title>
			{#if markPaidTarget}
				<Dialog.Description data-testid="past-due-mark-paid-summary">
					{displayName(markPaidTarget)} · {money(markPaidTarget.pastDueAmount)}
					{#if markPaidTarget.overduePaymentCount > 1}
						· {markPaidTarget.overduePaymentCount} payments
					{/if}
				</Dialog.Description>
			{/if}
		</Dialog.Header>
		<div class="space-y-3" data-testid="past-due-mark-paid-form">
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">Date received</span>
				<DatePicker testid="past-due-mark-paid-date-input" bind:value={markPaidForm.paidDate} placeholder="Date received" />
			</div>
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">Method</span>
				<Select.Root type="single" bind:value={markPaidForm.method}>
					<Select.Trigger class="w-full" data-testid="past-due-mark-paid-method-input">
						{markPaidForm.method || 'Select method'}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value="" label="No method">No method</Select.Item>
						{#each PAYMENT_METHODS as m}
							<Select.Item value={m} label={m}>{m}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
			</div>
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">Reference</span>
				<Input
					data-testid="past-due-mark-paid-reference-input"
					bind:value={markPaidForm.externalReference}
					placeholder="Check #, confirmation #, etc. (optional)"
				/>
			</div>
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">Notes</span>
				<textarea
					data-testid="past-due-mark-paid-notes-input"
					bind:value={markPaidForm.notes}
					rows={2}
					maxlength={2000}
					placeholder="Anything to remember about this payment (optional)"
					class="w-full resize-none rounded-md border border-border bg-background px-3 py-2 text-sm shadow-xs outline-none placeholder:text-muted-foreground focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50"
				></textarea>
			</div>
		</div>
		<Dialog.Footer>
			<Button data-testid="past-due-mark-paid-cancel" variant="outline" onclick={closeMarkPaid}>Cancel</Button>
			<Button data-testid="past-due-mark-paid-confirm" onclick={submitMarkPaid} disabled={markPaidMutation.isPending}>
				{markPaidMutation.isPending ? 'Saving…' : 'Mark paid'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
