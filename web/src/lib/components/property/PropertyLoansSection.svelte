<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { loans, type Loan } from '$lib/api/endpoints/loans';
	import { loanSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import FormStepper, { type FormStepperStep } from '$lib/components/shared/FormStepper.svelte';
	import StepperNextButton from '$lib/components/shared/StepperNextButton.svelte';
	import InlineField from '$lib/components/shared/InlineField.svelte';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import RangeDatePicker from '$lib/components/shared/RangeDatePicker.svelte';
	import AccountingImpactCard from '$lib/components/accounting/AccountingImpactCard.svelte';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';
	import { Plus, Pencil, Trash2, AlertTriangle, ChevronDown, ChevronRight, ScanLine } from '@lucide/svelte';
	import { scanHref } from '$lib/scan/scan-context';
	import { formatAccountingCurrency } from '$lib/accounting/accounting-display';
	import { formatDateOnly } from '$lib/utils/date';

	let { propertyId, canManage = false }: { propertyId: number; canManage?: boolean } = $props();

	const queryClient = useQueryClient();
	const PAGE_SIZE = 10;

	let loanPage = $state(1);
	let loanSort = $state('-startDate');
	let loanFrom = $state('');
	let loanTo = $state('');

	let loanFilterResetPrimed = false;
	$effect(() => {
		propertyId;
		loanFrom;
		loanTo;
		if (!loanFilterResetPrimed) {
			loanFilterResetPrimed = true;
			return;
		}
		loanPage = 1;
	});

	const loansQuery = createQuery(() => ({
		queryKey: ['loans', propertyId, 'page', loanPage, loanSort, loanFrom, loanTo, PAGE_SIZE],
		queryFn: () => loans.listPage({
			propertyId,
			skip: (loanPage - 1) * PAGE_SIZE,
			take: PAGE_SIZE,
			sort: loanSort || undefined,
			from: loanFrom || undefined,
			to: loanTo || undefined
		}),
		enabled: !isNaN(propertyId) && propertyId > 0
	}));

	const loansList = $derived(loansQuery.data?.items ?? []);
	const loansTotalCount = $derived(loansQuery.data?.totalCount ?? 0);


	const statusOptions = [
		{ value: 'Active', label: 'Active' },
		{ value: 'PaidOff', label: 'Paid off' },
		{ value: 'Closed', label: 'Closed' }
	];

	const emptyLoan = {
		lender: '',
		originalAmount: '',
		currentBalance: '',
		annualInterestRatePct: '',
		termMonths: '360',
		startDate: '',
		dayOfMonthDue: '1',
		monthlyPrincipalInterest: '',
		monthlyEscrow: '0',
		escrowCoversTaxes: false,
		escrowCoversInsurance: false,
		status: 'Active',
		notes: ''
	};

	let showForm = $state(false);
	let editingLoanId = $state<number | null>(null);
	let form = $state({ ...emptyLoan });
	let formErrors = $state<Record<string, string>>({});
	let loanStep = $state(0);
	let completedLoanSteps = $state<number[]>([]);

	const loanSteps: FormStepperStep[] = [
		{ id: 'basics', label: 'Basics', description: 'Lender and balances' },
		{ id: 'terms', label: 'Terms', description: 'Rate and schedule' },
		{ id: 'payment', label: 'Payment', description: 'Monthly amounts' },
		{ id: 'status', label: 'Status', description: 'Escrow and notes' },
	];
	const loanStepFields = [
		['lender', 'originalAmount', 'currentBalance'],
		['annualInterestRatePct', 'termMonths', 'startDate', 'dayOfMonthDue'],
		['monthlyPrincipalInterest', 'monthlyEscrow'],
		['escrowCoversTaxes', 'escrowCoversInsurance', 'status', 'notes'],
	] as const;

	function openAdd() {
		if (!canManage) return;
		editingLoanId = null;
		form = { ...emptyLoan };
		formErrors = {};
		loanStep = 0;
		completedLoanSteps = [];
		showForm = true;
	}

	function openEdit(l: Loan) {
		if (!canManage) return;
		editingLoanId = l.id;
		form = {
			lender: l.lender,
			originalAmount: String(l.originalAmount),
			currentBalance: String(l.currentBalance),
			annualInterestRatePct: String(l.annualInterestRatePct),
			termMonths: String(l.termMonths),
			startDate: l.startDate ? l.startDate.slice(0, 10) : '',
			dayOfMonthDue: String(l.dayOfMonthDue),
			monthlyPrincipalInterest: String(l.monthlyPrincipalInterest),
			monthlyEscrow: String(l.monthlyEscrow),
			escrowCoversTaxes: l.escrowCoversTaxes,
			escrowCoversInsurance: l.escrowCoversInsurance,
			status: l.status,
			notes: l.notes ?? ''
		};
		formErrors = {};
		loanStep = 0;
		completedLoanSteps = [];
		showForm = true;
	}

	function closeForm() {
		showForm = false;
		editingLoanId = null;
		formErrors = {};
		loanStep = 0;
		completedLoanSteps = [];
	}

	function invalidate() {
		queryClient.invalidateQueries({ queryKey: ['loans', propertyId] });
	}

	const createMut = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => loans.create({ propertyId, ...data }),
		onSuccess: () => {
			showSuccess('Loan added.');
			closeForm();
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	const updateMut = createMutation(() => ({
		mutationFn: ({ id, data }: { id: number; data: Record<string, unknown> }) => loans.update(id, data),
		onSuccess: () => {
			showSuccess('Loan updated.');
			closeForm();
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	let deleteTarget = $state<Loan | null>(null);
	const deleteMut = createMutation(() => ({
		mutationFn: (id: number) => loans.remove(id),
		onSuccess: () => {
			showSuccess('Loan removed.');
			deleteTarget = null;
			invalidate();
			if (expandedLoanId != null) expandedLoanId = null;
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	function submit() {
		// The escrow-cover flags are booleans the schema passes through; checkbox state lives in `form`.
		const result = parseForm(loanSchema, form);
		const errors = result.errors ?? {};
		if (Object.keys(errors).length > 0) {
			formErrors = errors;
			const firstErrorStep = firstLoanErrorStep(errors);
			if (firstErrorStep >= 0) {
				loanStep = firstErrorStep;
				markLoanStepInvalid(firstErrorStep);
			}
			return;
		}
		if (!result.data) return;
		formErrors = {};
		const data = { ...result.data, escrowCoversTaxes: form.escrowCoversTaxes, escrowCoversInsurance: form.escrowCoversInsurance };
		if (editingLoanId != null) {
			updateMut.mutate({ id: editingLoanId, data });
		} else {
			createMut.mutate(data);
		}
	}

	function loanStepErrorFields(step: number, errors: Record<string, string>) {
		const visibleFields = new Set<string>(loanStepFields[step] ?? []);
		return Object.entries(errors).filter(([field]) => visibleFields.has(field));
	}

	function firstLoanErrorStep(errors: Record<string, string>) {
		return loanStepFields.findIndex((fields) => fields.some((field) => errors[field]));
	}

	function markLoanStepInvalid(step: number) {
		completedLoanSteps = completedLoanSteps.filter((completedStep) => completedStep < step);
	}

	function validateLoanStep(step: number) {
		const result = parseForm(loanSchema, form);
		const allErrors = result.errors ?? {};
		const currentErrors = Object.fromEntries(loanStepErrorFields(step, allErrors));
		const currentFields = new Set<string>(loanStepFields[step] ?? []);
		const nextErrors = Object.fromEntries(Object.entries(formErrors).filter(([field]) => !currentFields.has(field)));
		formErrors = { ...nextErrors, ...currentErrors };
		const isValid = Object.keys(currentErrors).length === 0;
		if (!isValid) markLoanStepInvalid(step);
		return isValid;
	}

	function nextLoanStep() {
		if (!validateLoanStep(loanStep)) return;
		if (completedLoanSteps.includes(loanStep)) {
			loanStep = Math.min(loanStep + 1, loanSteps.length - 1);
			return;
		}
		completedLoanSteps = [...completedLoanSteps, loanStep];
		window.setTimeout(() => {
			loanStep = Math.min(loanStep + 1, loanSteps.length - 1);
		}, 260);
	}

	const columns: ColumnDef<Loan>[] = [
		{ key: 'lender', title: 'Lender', sortable: true, mobileRole: 'title' },
		{ key: 'currentBalance', title: 'Balance', format: 'currency', sortable: true, mobileRole: 'metric' },
		{ key: 'startDate', title: 'Start', format: 'date', sortable: true, mobileRole: 'meta' },
		{ key: 'annualInterestRatePct', title: 'Rate %', format: 'number', sortable: true, mobileRole: 'meta' },
		{ key: 'monthlyPrincipalInterest', title: 'P&I', format: 'currency', sortable: true, mobileRole: 'meta' },
		{ key: 'monthlyEscrow', title: 'Escrow', format: 'currency', mobileRole: 'meta' },
		{ key: 'status', title: 'Status', mobileRole: 'badge' },
		{ key: 'actions', title: '', align: 'right', width: '8rem', mobileRole: 'hidden', cell: actionsCell }
	];

	// ── Amortization schedule (read-only history), shown inline when a loan is expanded ──
	let expandedLoanId = $state<number | null>(null);
	const scheduleQuery = createQuery(() => ({
		queryKey: ['loan-payments', expandedLoanId],
		queryFn: () => loans.payments(expandedLoanId as number),
		enabled: expandedLoanId != null
	}));
	const scheduleRows = $derived(scheduleQuery.data ?? []);
	const postPaymentMut = createMutation(() => ({
		mutationFn: (paymentId: number) => loans.postPayment(expandedLoanId as number, paymentId),
		onSuccess: () => {
			showSuccess('Loan payment recorded.');
			queryClient.invalidateQueries({ queryKey: ['loan-payments', expandedLoanId] });
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	function toggleSchedule(id: number) {
		expandedLoanId = expandedLoanId === id ? null : id;
	}
</script>

{#snippet actionsCell(loan: Loan)}
	<div class="flex items-center justify-end gap-1">
		<Button variant="ghost" size="sm" class="h-7 gap-1 px-2" onclick={(e) => { e.stopPropagation(); toggleSchedule(loan.id); }} data-testid={`loan-schedule-toggle-${loan.id}`}>
			{#if expandedLoanId === loan.id}<ChevronDown class="h-4 w-4" />{:else}<ChevronRight class="h-4 w-4" />{/if}
			<span class="hidden sm:inline">Schedule</span>
		</Button>
		{#if canManage}
			<Button variant="ghost" size="sm" class="h-7 w-7 p-0" onclick={(e) => { e.stopPropagation(); openEdit(loan); }} aria-label="Edit loan">
				<Pencil class="h-4 w-4" />
			</Button>
			<Button variant="ghost" size="sm" class="h-7 w-7 p-0 text-destructive" onclick={(e) => { e.stopPropagation(); deleteTarget = loan; }} aria-label="Delete loan">
				<Trash2 class="h-4 w-4" />
			</Button>
		{/if}
	</div>
{/snippet}

<div class="mb-6" data-testid="property-detail-loans">
	<h2 class="mb-3 text-lg font-semibold">Mortgage / Loans</h2>
	<DataGrid
		data={loansList}
		{columns}
		loading={loansQuery.isLoading || loansQuery.isFetching}
		emptyMessage="No loans on this property yet."
		getRowKey={(l) => l.id}
		pageSize={PAGE_SIZE}
		page={loanPage}
		totalCount={loansTotalCount}
		serverSide
		sort={loanSort}
		onPageChange={(page) => (loanPage = page)}
		onSortChange={(sort) => { loanSort = sort ?? ''; loanPage = 1; }}
		data-testid="property-loans-grid"
	>
		{#snippet toolbar()}
			<div class="flex flex-1"></div>
			<RangeDatePicker
				bind:start={loanFrom}
				bind:end={loanTo}
				presets
				placeholder="Start dates"
				align="end"
				testid="property-loans-date-range"
			/>
			{#if canManage}
				<Button
					variant="outline"
					class="gap-2 shrink-0"
					href={scanHref({ type: 'Loan', propertyId, returnTo: `/properties/${propertyId}` })}
					data-testid="loan-scan-button"
				>
					<ScanLine class="h-4 w-4" />
					Scan / import
				</Button>
				<Button class="gap-2 shrink-0" onclick={openAdd} data-testid="loan-add-button">
					<Plus class="h-4 w-4" />
					Add Loan
				</Button>
			{/if}
		{/snippet}
	</DataGrid>

	<!-- Amortization schedule for the expanded loan -->
	{#if expandedLoanId != null}
		<div class="mt-3 rounded-lg border border-border bg-muted/30 p-4" data-testid="loan-amortization-schedule">
			<h3 class="mb-2 text-sm font-semibold">Amortization schedule</h3>
			{#if scheduleQuery.isLoading}
				<p class="text-sm text-muted-foreground">Loading…</p>
			{:else if scheduleRows.length === 0}
				<p class="text-sm text-muted-foreground">No payments generated yet. The debt-service worker fills this in monthly.</p>
			{:else}
				<div class="overflow-x-auto">
					<table class="w-full text-sm tabular-nums">
						<thead>
							<tr class="text-left text-xs uppercase tracking-wide text-muted-foreground">
								<th class="py-1 pr-3">Period</th>
								<th class="py-1 pr-3">Due</th>
								<th class="py-1 pr-3 text-right">Interest</th>
								<th class="py-1 pr-3 text-right">Principal</th>
								<th class="py-1 pr-3 text-right">Escrow</th>
								<th class="py-1 pr-3 text-right">Total</th>
								<th class="py-1 pr-3 text-right">Balance</th>
								<th class="py-1 text-right">Status</th>
							</tr>
						</thead>
						<tbody>
							{#each scheduleRows as row (row.id)}
								<tr class="border-t border-border/60">
									<td class="py-1 pr-3">{row.periodKey}</td>
									<td class="py-1 pr-3">{formatDateOnly(row.dueDate)}</td>
									<td class="py-1 pr-3 text-right">{formatAccountingCurrency(row.interestAmount)}</td>
									<td class="py-1 pr-3 text-right">{formatAccountingCurrency(row.principalAmount)}</td>
									<td class="py-1 pr-3 text-right">{formatAccountingCurrency(row.escrowAmount)}</td>
									<td class="py-1 pr-3 text-right font-medium">{formatAccountingCurrency(row.totalAmount)}</td>
									<td class="py-1 pr-3 text-right">{formatAccountingCurrency(row.balanceAfter)}</td>
									<td class="py-1 text-right">
										{#if row.status === 'Paid'}
											<span class="font-medium text-success">Paid {row.paidDate ? formatDateOnly(row.paidDate) : ''}</span>
										{:else if canManage}
											<Button
												variant="outline"
												size="sm"
												class="h-7"
												disabled={postPaymentMut.isPending}
												onclick={() => postPaymentMut.mutate(row.id)}
												data-testid={`loan-payment-post-${row.id}`}
											>
												{postPaymentMut.isPending && postPaymentMut.variables === row.id ? 'Recording…' : 'Record paid'}
											</Button>
										{:else}
											<span class="text-muted-foreground">Scheduled</span>
										{/if}
									</td>
								</tr>
								{#if row.status === 'Paid'}
									<tr data-testid={`loan-payment-accounting-impact-${row.id}`}>
										<td colspan="8" class="py-2">
											<AccountingImpactCard sourceType="LoanPayment" sourceId={row.id} />
										</td>
									</tr>
								{/if}
								{#if row.paymentDoesNotCoverInterest}
									<tr>
										<td colspan="8" class="pb-1 text-xs text-amber-600">
											<AlertTriangle class="mr-1 inline h-3 w-3" />Payment didn't cover interest this period.
										</td>
									</tr>
								{/if}
							{/each}
						</tbody>
					</table>
				</div>
			{/if}
		</div>
	{/if}
</div>

<!-- Loan add/edit dialog -->
<Dialog.Root open={canManage && showForm} onOpenChange={(v) => { if (!v) closeForm(); }}>
	<Dialog.Content class="max-h-[85vh] max-w-2xl overflow-y-auto">
		<Dialog.Header>
			<Dialog.Title>{editingLoanId == null ? 'Add Loan' : 'Edit Loan'}</Dialog.Title>
		</Dialog.Header>
		<FormStepper steps={loanSteps} bind:currentStep={loanStep} completedSteps={completedLoanSteps} testid="loan-stepper">
			<div class="grid gap-3 px-1" data-testid="loan-form">
				{#if loanStep === 0}
					<InlineField label="Lender" bind:value={form.lender} editing type="text" error={formErrors.lender} testid="loan-lender" />
					<div class="grid gap-3 sm:grid-cols-2">
						<InlineField label="Original amount" bind:value={form.originalAmount} editing type="number" error={formErrors.originalAmount} testid="loan-original-amount" />
						<InlineField label="Current balance" bind:value={form.currentBalance} editing type="number" placeholder="Defaults to original" error={formErrors.currentBalance} testid="loan-current-balance" />
					</div>
				{:else if loanStep === 1}
					<div class="grid gap-3 sm:grid-cols-2">
						<InlineField label="Interest rate %" bind:value={form.annualInterestRatePct} editing type="number" error={formErrors.annualInterestRatePct} testid="loan-rate" />
						<InlineField label="Term (months)" bind:value={form.termMonths} editing type="number" maxlength={4} error={formErrors.termMonths} testid="loan-term" />
					</div>
					<div class="grid gap-3 sm:grid-cols-2">
						<div data-testid="loan-start-date-field">
							<label class="mb-1 block text-xs font-medium text-muted-foreground" for="loan-start-date-input">Start date</label>
							<DatePicker id="loan-start-date-input" testid="loan-start-date-input" bind:value={form.startDate} />
							{#if formErrors.startDate}<p class="mt-1 text-xs text-destructive" data-testid="loan-start-date-error">{formErrors.startDate}</p>{/if}
						</div>
						<InlineField label="Day of month due" bind:value={form.dayOfMonthDue} editing type="number" maxlength={2} error={formErrors.dayOfMonthDue} testid="loan-day-due" />
					</div>
				{:else if loanStep === 2}
					<div class="grid gap-3 sm:grid-cols-2">
						<InlineField label="Monthly P&I" bind:value={form.monthlyPrincipalInterest} editing type="number" error={formErrors.monthlyPrincipalInterest} testid="loan-pi" />
						<InlineField label="Monthly escrow" bind:value={form.monthlyEscrow} editing type="number" error={formErrors.monthlyEscrow} testid="loan-escrow" />
					</div>
				{:else}
					<div class="space-y-3">
						<label class="flex items-center gap-2 text-sm">
							<input type="checkbox" bind:checked={form.escrowCoversTaxes} data-testid="loan-escrow-taxes" />
							Escrow covers property taxes
						</label>
						<label class="flex items-center gap-2 text-sm">
							<input type="checkbox" bind:checked={form.escrowCoversInsurance} data-testid="loan-escrow-insurance" />
							Escrow covers insurance
						</label>
					</div>
					<InlineField label="Status" bind:value={form.status} editing type="select" options={statusOptions} testid="loan-status" />
					<InlineField label="Notes" bind:value={form.notes} editing type="textarea" error={formErrors.notes} testid="loan-notes" />
				{/if}
			</div>
		</FormStepper>
		<div class="mt-4 flex justify-end gap-2">
			<Button variant="outline" onclick={closeForm}>Cancel</Button>
			{#if loanStep > 0}
				<Button variant="outline" onclick={() => (loanStep = Math.max(loanStep - 1, 0))} data-testid="loan-step-back">Back</Button>
			{/if}
			{#if loanStep < loanSteps.length - 1}
				<StepperNextButton
					testid="loan-step-next"
					complete={completedLoanSteps.includes(loanStep)}
					onclick={nextLoanStep}
				/>
			{:else}
				<Button onclick={submit} disabled={createMut.isPending || updateMut.isPending} data-testid="loan-save-button">
					{(createMut.isPending || updateMut.isPending) ? 'Saving…' : editingLoanId == null ? 'Add Loan' : 'Save Loan'}
				</Button>
			{/if}
		</div>
	</Dialog.Content>
</Dialog.Root>

<ConfirmDialog
	open={canManage && deleteTarget !== null}
	title="Remove loan"
	message={deleteTarget ? `Remove the loan from "${deleteTarget.lender}"? This removes its amortization history.` : ''}
	busy={deleteMut.isPending}
	testid="loan-delete-confirm"
	onconfirm={() => deleteTarget && deleteMut.mutate(deleteTarget.id)}
	oncancel={() => (deleteTarget = null)}
/>
