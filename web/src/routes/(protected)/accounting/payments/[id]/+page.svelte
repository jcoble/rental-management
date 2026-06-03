<script lang="ts">
	import { goto } from '$app/navigation';
	import { page } from '$app/stores';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { ArrowLeft, Pencil, Save, Trash2, X } from '@lucide/svelte';
	import { payments } from '$lib/api/endpoints/payments';
	import { leases } from '$lib/api/endpoints/leases';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { paymentSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import InlineField from '$lib/components/shared/InlineField.svelte';
	import * as Card from '$lib/components/ui/card';
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
		<Card.Root>
			<Card.Header>
				<Card.Title>Payment Details</Card.Title>
				<Card.Description>Lease charge, due date, status, and payment tracking.</Card.Description>
			</Card.Header>
			<Card.Content>
				<div class="grid gap-4 md:grid-cols-3">
					<InlineField label="Lease" bind:value={form.leaseId} display={payment.leaseNumber} {editing} type="select" options={leaseOptions} error={formErrors.leaseId} testid="payment-detail-lease" />
					<InlineField label="Amount" bind:value={form.amount} display={`$${payment.amount}`} {editing} error={formErrors.amount} testid="payment-detail-amount" />
					<InlineField label="Due date" bind:value={form.dueDate} display={new Date(payment.dueDate).toLocaleDateString()} {editing} type="date" error={formErrors.dueDate} testid="payment-detail-due-date" />
					<InlineField label="Payment type" bind:value={form.paymentType} display={payment.paymentType} {editing} type="select" options={typeOptions} testid="payment-detail-type" />
					<InlineField label="Status" bind:value={form.status} display={payment.status} {editing} type="select" options={statusOptions} testid="payment-detail-status" />
					<InlineField label="Paid date" bind:value={form.paidDate} display={payment.paidDate ? new Date(payment.paidDate).toLocaleDateString() : ''} {editing} type="date" testid="payment-detail-paid-date" />
					<InlineField label="Method" bind:value={form.method} display={payment.method} {editing} testid="payment-detail-method" />
					<InlineField label="Reference" bind:value={form.externalReference} display={payment.externalReference} {editing} testid="payment-detail-reference" />
					<InlineField label="Notes" bind:value={form.notes} display={payment.notes} {editing} type="textarea" testid="payment-detail-notes" class="md:col-span-3" />
				</div>
			</Card.Content>
		</Card.Root>
	{/if}
</div>
