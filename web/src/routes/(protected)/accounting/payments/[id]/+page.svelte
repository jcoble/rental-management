<script lang="ts">
	import { goto } from '$app/navigation';
	import { page } from '$app/stores';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { ArrowLeft, Pencil, Save, Trash2, X, Receipt, CircleCheck } from '@lucide/svelte';
	import { payments } from '$lib/api/endpoints/payments';
	import { leases } from '$lib/api/endpoints/leases';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { paymentSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import InlineField from '$lib/components/shared/InlineField.svelte';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import HeroCard, { type HeroTone } from '$lib/components/shared/HeroCard.svelte';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { Button } from '$lib/components/ui/button';

	const queryClient = useQueryClient();
	const paymentId = $derived(parseInt($page.params.id ?? '0', 10));
	const portfolioId = $derived(getCurrentPortfolioId());
	const PAYMENT_TYPES = ['Rent', 'SecurityDeposit', 'LateFee', 'Utility', 'Other'];
	const PAYMENT_STATUSES = ['Scheduled', 'Paid', 'Partial', 'Late', 'Waived', 'Failed', 'Refunded'];

	let editing = $state(false);
	let form = $state({
		leaseId: '',
		amount: '',
		dueDate: '',
		paymentType: 'Rent',
		status: 'Scheduled',
		paidDate: '',
		method: '',
		externalReference: '',
		notes: ''
	});
	let formErrors = $state<Record<string, string>>({});

	const paymentQuery = createQuery(() => ({ queryKey: ['payment', paymentId], queryFn: () => payments.get(paymentId), enabled: paymentId > 0 }));
	const leasesQuery = createQuery(() => ({ queryKey: ['leases', portfolioId], queryFn: () => leases.list(portfolioId, { take: 200 }) }));

	const payment = $derived(paymentQuery.data);

	// Hero amount, formatted as currency (the page otherwise shows the raw value).
	const heroAmount = $derived(
		payment ? new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(Number(payment.amount) || 0) : ''
	);
	// Context tone keyed by collection status: green = money in, warning = owed,
	// destructive = failed, primary otherwise.
	const heroTone = $derived.by<HeroTone>(() => {
		switch (payment?.status) {
			case 'Paid': return 'success';
			case 'Late':
			case 'Partial': return 'warning';
			case 'Failed': return 'destructive';
			default: return 'primary';
		}
	});

	const typeOptions = $derived(PAYMENT_TYPES.map((value) => ({ value, label: value })));
	const statusOptions = $derived(PAYMENT_STATUSES.map((value) => ({ value, label: value })));
	const leaseOptions = $derived([{ value: '', label: 'Select lease' }, ...(leasesQuery.data ?? []).map((lease) => ({ value: String(lease.id), label: `${lease.leaseNumber} · ${lease.tenantName}` }))]);

	function startEditing() {
		if (!payment) return;
		form = {
			leaseId: String(payment.leaseId),
			amount: String(payment.amount),
			dueDate: payment.dueDate?.slice(0, 10) ?? '',
			paymentType: payment.paymentType,
			status: payment.status,
			paidDate: payment.paidDate?.slice(0, 10) ?? '',
			method: payment.method ?? '',
			externalReference: payment.externalReference ?? '',
			notes: payment.notes ?? ''
		};
		formErrors = {};
		editing = true;
	}

	function cancelEditing() {
		editing = false;
		formErrors = {};
	}

	const saveMutation = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => payments.update(paymentId, data),
		onSuccess: () => {
			showSuccess('Payment updated.');
			editing = false;
			queryClient.invalidateQueries({ queryKey: ['payment', paymentId] });
			queryClient.invalidateQueries({ queryKey: ['payments', portfolioId] });
			queryClient.invalidateQueries({ queryKey: ['accounting-summary', portfolioId] });
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	function savePayment() {
		const result = parseForm(paymentSchema, form);
		if (result.errors) {
			formErrors = result.errors;
			return;
		}
		formErrors = {};
		saveMutation.mutate({ portfolioId, ...result.data });
	}

	const deleteMutation = createMutation(() => ({
		mutationFn: () => payments.delete(paymentId),
		onSuccess: () => {
			showSuccess('Payment deleted.');
			goto('/accounting');
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));
</script>

<svelte:head>
	<title>Payment - Rental Command</title>
</svelte:head>

<!-- Inline date field: mirrors InlineField's edit/display structure (same testids) but
     uses the shared DatePicker when editing. value is bound `yyyy-MM-dd`. -->
{#snippet dateField(opts: {
	label: string;
	value: string;
	setValue: (v: string) => void;
	display: string;
	testid: string;
	error?: string;
})}
	<div data-testid={`${opts.testid}-field`}>
		<label class="mb-1 block text-xs font-medium text-muted-foreground" for={`${opts.testid}-input`}>{opts.label}</label>
		{#if editing}
			<DatePicker
				id={`${opts.testid}-input`}
				testid={`${opts.testid}-input`}
				value={opts.value}
				onchange={opts.setValue}
				placeholder={opts.label}
			/>
			{#if opts.error}<p class="mt-1 text-xs text-destructive" data-testid={`${opts.testid}-error`}>{opts.error}</p>{/if}
		{:else}
			<p
				class="min-h-10 rounded-md py-2 text-sm font-medium text-foreground"
				data-testid={`${opts.testid}-value`}
			>
				{opts.display === '' ? '-' : opts.display}
			</p>
		{/if}
	</div>
{/snippet}

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="payment-detail-page">
	<div class="mb-5 flex flex-wrap items-start justify-between gap-3">
		<div>
			<Button variant="ghost" href="/accounting" class="mb-2 -ml-3"><ArrowLeft class="h-4 w-4" />Accounting</Button>
			<h1 class="text-2xl font-bold">{payment?.tenantName || payment?.leaseNumber || 'Payment'}</h1>
			<p class="text-sm text-muted-foreground">{payment ? `${payment.paymentType} · $${payment.amount} · ${payment.status}` : ''}</p>
		</div>
		{#if payment}
			<div class="flex gap-2">
				{#if editing}
					<Button variant="outline" onclick={cancelEditing} disabled={saveMutation.isPending}><X class="h-4 w-4" />Cancel</Button>
					<Button onclick={savePayment} disabled={saveMutation.isPending}><Save class="h-4 w-4" />{saveMutation.isPending ? 'Saving...' : 'Save'}</Button>
				{:else}
					<Button variant="outline" onclick={startEditing}><Pencil class="h-4 w-4" />Edit</Button>
					<Button variant="destructive" onclick={() => deleteMutation.mutate()} disabled={deleteMutation.isPending}><Trash2 class="h-4 w-4" />Delete</Button>
				{/if}
			</div>
		{/if}
	</div>

	{#if paymentQuery.isLoading}
		<div class="rounded-lg border border-border bg-card p-6 text-sm text-muted-foreground">Loading payment...</div>
	{:else if !payment}
		<div class="rounded-lg border border-border bg-card p-6 text-sm text-muted-foreground">Payment not found.</div>
	{:else}
		<!-- Hero: the amount + how it's being collected, washed by collection status. -->
		<HeroCard tone={heroTone} testid="payment-hero" contentClass="flex flex-wrap items-end justify-between gap-6" class="mb-6">
			<div class="min-w-0">
				<p class="text-xs font-medium uppercase tracking-wide text-muted-foreground">{payment.paymentType} payment</p>
				<p class="mt-1 font-mono text-4xl font-bold tabular-nums tracking-tight" data-testid="payment-hero-amount">{heroAmount}</p>
				<div class="mt-3 flex flex-wrap items-center gap-x-2 gap-y-1 text-sm text-muted-foreground">
					<StatusBadge status={payment.status} />
					{#if payment.leaseNumber}
						<span aria-hidden="true">·</span>
						<a href="/leases/{payment.leaseId}" class="font-medium text-foreground underline-offset-4 hover:underline">{payment.leaseNumber}</a>
					{/if}
					{#if payment.tenantName}
						<span aria-hidden="true">·</span>
						<span>{payment.tenantName}</span>
					{/if}
				</div>
			</div>
			{#if !editing && payment.status !== 'Paid' && payment.status !== 'Waived'}
				<Button size="sm" data-testid="payment-hero-cta" onclick={startEditing}>
					<Pencil class="h-4 w-4" />
					Update payment
				</Button>
			{/if}
		</HeroCard>

		<div class="grid gap-6 lg:grid-cols-2">
			<DetailCard title="Charge" icon={Receipt} accent="primary" testid="payment-card-charge" contentClass="grid gap-x-6 gap-y-4 sm:grid-cols-2">
				<InlineField label="Lease" bind:value={form.leaseId} display={payment.leaseNumber} {editing} type="select" options={leaseOptions} error={formErrors.leaseId} testid="payment-detail-lease" class="sm:col-span-2" />
				<InlineField label="Amount" bind:value={form.amount} display={`$${payment.amount}`} {editing} error={formErrors.amount} testid="payment-detail-amount" />
				<InlineField label="Payment type" bind:value={form.paymentType} display={payment.paymentType} {editing} type="select" options={typeOptions} testid="payment-detail-type" />
				{@render dateField({ label: 'Due date', value: form.dueDate, setValue: (v) => (form.dueDate = v), display: new Date(payment.dueDate).toLocaleDateString(), error: formErrors.dueDate, testid: 'payment-detail-due-date' })}
			</DetailCard>

			<DetailCard title="Payment tracking" icon={CircleCheck} accent="success" testid="payment-card-tracking" contentClass="grid gap-x-6 gap-y-4 sm:grid-cols-2">
				<InlineField label="Status" bind:value={form.status} display={payment.status} {editing} type="select" options={statusOptions} testid="payment-detail-status" />
				{@render dateField({ label: 'Paid date', value: form.paidDate, setValue: (v) => (form.paidDate = v), display: payment.paidDate ? new Date(payment.paidDate).toLocaleDateString() : '', testid: 'payment-detail-paid-date' })}
				<InlineField label="Method" bind:value={form.method} display={payment.method} {editing} testid="payment-detail-method" />
				<InlineField label="Reference" bind:value={form.externalReference} display={payment.externalReference} {editing} testid="payment-detail-reference" />
				<InlineField label="Notes" bind:value={form.notes} display={payment.notes} {editing} type="textarea" testid="payment-detail-notes" class="sm:col-span-2" />
			</DetailCard>
		</div>
	{/if}
</div>
