<script lang="ts">
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { ArrowLeft, Pencil, Save, Trash2, X, Receipt, CircleCheck, FileText } from '@lucide/svelte';
	import { payments } from '$lib/api/endpoints/payments';
	import { leases } from '$lib/api/endpoints/leases';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { paymentSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { formatDateOnly } from '$lib/utils/date';
	import { paymentTypeLabel } from '$lib/utils/payment-labels';
	import InlineField from '$lib/components/shared/InlineField.svelte';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import RecordHistory from '$lib/components/shared/RecordHistory.svelte';
	import HeroCard, { type HeroTone } from '$lib/components/shared/HeroCard.svelte';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { Button } from '$lib/components/ui/button';

	const queryClient = useQueryClient();
	const paymentId = $derived(parseInt(page.params.id ?? '0', 10));
	const portfolioId = $derived(getCurrentPortfolioId());
	const PAYMENT_TYPES = ['Rent', 'SecurityDeposit', 'LateFee', 'Utility', 'Other'];
	const PAYMENT_STATUSES = ['Scheduled', 'Paid', 'Partial', 'Late', 'Waived', 'Failed', 'Refunded'];

	let editing = $state(false);
	let form = $state({
		leaseId: '',
		amount: '',
		amountPaid: '',
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

	// View-mode lease label: mirror the edit-mode option text ("L2024-011 · Tyler Anderson")
	// so view mode shows the same lease the dropdown does, instead of a bare number or "—".
	const leaseDisplay = $derived(
		payment?.leaseNumber
			? payment.tenantName
				? `${payment.leaseNumber} · ${payment.tenantName}`
				: payment.leaseNumber
			: ''
	);

	const typeOptions = $derived(PAYMENT_TYPES.map((value) => ({ value, label: value })));
	const statusOptions = $derived(PAYMENT_STATUSES.map((value) => ({ value, label: value })));
	const leaseOptions = $derived([{ value: '', label: 'Select lease' }, ...(leasesQuery.data ?? []).map((lease) => ({ value: String(lease.id), label: `${lease.leaseNumber} · ${lease.tenantName}` }))]);

	function startEditing() {
		if (!payment) return;
		form = {
			leaseId: String(payment.leaseId),
			amount: String(payment.amount),
			amountPaid: payment.amountPaid != null ? String(payment.amountPaid) : '',
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
		// amountPaid only applies to a Partial payment; for any other status send null so the server
		// clears a stale collected-so-far (mirrors PaymentService.NormalizeAmountPaid).
		const { amountPaid, ...rest } = result.data;
		saveMutation.mutate(
			rest.status === 'Partial' ? { portfolioId, ...rest, amountPaid } : { portfolioId, ...rest, amountPaid: null }
		);
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
		{#if editing}
			<label class="mb-1 block text-xs font-medium text-muted-foreground" for={`${opts.testid}-input`}>{opts.label}</label>
			<DatePicker
				id={`${opts.testid}-input`}
				testid={`${opts.testid}-input`}
				value={opts.value}
				onchange={opts.setValue}
				placeholder={opts.label}
			/>
			{#if opts.error}<p class="mt-1 text-xs text-destructive" data-testid={`${opts.testid}-error`}>{opts.error}</p>{/if}
		{:else}
			<div class="m3-readonly-field flex flex-col justify-center" data-testid={`${opts.testid}-value`}>
				<span class="m3-readonly-field__label">{opts.label}</span>
				<span class="m3-readonly-field__value mt-1">{opts.display === '' ? '-' : opts.display}</span>
			</div>
		{/if}
	</div>
{/snippet}

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="payment-detail-page">
	<div class="mb-5 flex flex-wrap items-start justify-between gap-3">
		<div>
			<Button variant="ghost" href="/accounting" class="mb-2 -ml-3"><ArrowLeft class="h-4 w-4" />Money</Button>
			<h1 class="text-2xl font-bold">{payment?.tenantName || payment?.leaseNumber || 'Payment'}</h1>
			<p class="text-sm text-muted-foreground">{payment ? `${paymentTypeLabel(payment.paymentType)} · $${payment.amount} · ${payment.status}` : ''}</p>
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
				<p class="text-xs font-medium uppercase tracking-wide text-muted-foreground">{paymentTypeLabel(payment.paymentType)} payment</p>
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
				<InlineField label="Lease" bind:value={form.leaseId} display={leaseDisplay} {editing} type="select" options={leaseOptions} error={formErrors.leaseId} testid="payment-detail-lease" class="sm:col-span-2" />
				<InlineField label="Amount" bind:value={form.amount} display={`$${payment.amount}`} {editing} error={formErrors.amount} testid="payment-detail-amount" />
				<InlineField label="Payment type" bind:value={form.paymentType} display={payment.paymentType} {editing} type="select" options={typeOptions} testid="payment-detail-type" />
				{@render dateField({ label: 'Due date', value: form.dueDate, setValue: (v) => (form.dueDate = v), display: formatDateOnly(payment.dueDate), error: formErrors.dueDate, testid: 'payment-detail-due-date' })}
			</DetailCard>

			<DetailCard title="Payment tracking" icon={CircleCheck} accent="success" testid="payment-card-tracking" contentClass="grid gap-x-6 gap-y-4 sm:grid-cols-2">
				<InlineField label="Status" bind:value={form.status} display={payment.status} {editing} type="select" options={statusOptions} testid="payment-detail-status" />
				<!-- Amount paid (collected-so-far) only applies to a Partial payment. While editing show it
				     only when the chosen status is Partial; read-only show it only when the payment IS
				     Partial — every other status has no split to display. -->
				{#if editing ? form.status === 'Partial' : payment.status === 'Partial'}
					<InlineField label="Amount paid" bind:value={form.amountPaid} display={payment.amountPaid != null ? `$${payment.amountPaid}` : '-'} {editing} error={formErrors.amountPaid} testid="payment-detail-amount-paid" />
				{/if}
				{@render dateField({ label: 'Paid date', value: form.paidDate, setValue: (v) => (form.paidDate = v), display: payment.paidDate ? formatDateOnly(payment.paidDate) : '', testid: 'payment-detail-paid-date' })}
				<InlineField label="Method" bind:value={form.method} display={payment.method} {editing} testid="payment-detail-method" />
				<InlineField label="Reference" bind:value={form.externalReference} display={payment.externalReference} {editing} testid="payment-detail-reference" />
				<InlineField label="Notes" bind:value={form.notes} display={payment.notes} {editing} type="textarea" testid="payment-detail-notes" class="sm:col-span-2" />
			</DetailCard>

			<!-- Original scanned document this payment was created from (e.g. a paper check),
			     served through the same-origin /payment-file proxy. Renders only when one exists. -->
			{#if payment.hasScan}
				<DetailCard title="Scanned document" icon={FileText} accent="muted" testid="payment-card-scanned-document" class="lg:col-span-2">
					<div class="flex flex-col items-start gap-2">
						<p class="text-xs text-muted-foreground">The original document this payment was created from.</p>
						{#if payment.scanIsImage}
							<a
								href="/payment-file/{payment.id}"
								target="_blank"
								rel="noopener noreferrer"
								data-testid="payment-detail-scanned-document-link"
								aria-label="View scanned document full size"
								class="group inline-block"
							>
								<img
									src="/payment-file/{payment.id}?thumb=true"
									alt="Scanned document preview"
									class="max-h-80 w-auto rounded-md border border-border object-contain transition group-hover:ring-2 group-hover:ring-primary"
									loading="lazy"
								/>
								<span class="mt-1 block text-xs text-primary underline underline-offset-2 group-hover:text-primary/80">Open full size</span>
							</a>
						{:else}
							<a
								href="/payment-file/{payment.id}"
								target="_blank"
								rel="noopener noreferrer"
								data-testid="payment-detail-scanned-document-link"
								class="inline-flex items-center gap-2 rounded-md border border-border bg-background px-4 py-3 text-sm font-medium text-foreground transition hover:border-primary hover:text-primary"
							>
								<FileText class="h-5 w-5 shrink-0" />
								<span>View scanned document</span>
							</a>
						{/if}
					</div>
				</DetailCard>
			{/if}
		</div>

		<!-- Per-record audit history -->
		<div class="mt-6 rounded-lg border border-border bg-card p-4" data-testid="payment-history-section">
			<h2 class="mb-1 text-base font-semibold">History</h2>
			<p class="mb-3 text-sm text-muted-foreground">Every recorded change to this payment — who, what, and when.</p>
			<RecordHistory entityType="Payment" entityId={paymentId} />
		</div>
	{/if}
</div>
