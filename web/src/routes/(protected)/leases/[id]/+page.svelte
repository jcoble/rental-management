<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { page } from '$app/stores';
	import { leases } from '$lib/api/endpoints/leases';
	import { properties } from '$lib/api/endpoints/properties';
	import { tenants } from '$lib/api/endpoints/tenants';
	import { payments } from '$lib/api/endpoints/payments';
	import {
		openingBalances,
		type OpeningBalanceResponse,
	} from '$lib/api/endpoints/opening-balances';
	import type { Lease, Payment } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { leaseSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import PageBreadcrumb from '$lib/components/shared/PageBreadcrumb.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import InlineField from '$lib/components/shared/InlineField.svelte';
	import * as Card from '$lib/components/ui/card';
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Select from '$lib/components/ui/select';
	import * as Tabs from '$lib/components/ui/tabs';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { Pencil, Save, Trash2, X, FileText, Download, PenLine, Building2, DollarSign, Users, StickyNote, CalendarRange } from '@lucide/svelte';
	import { ApiError } from '$lib/api/client';
	import DocumentsPanel from '$lib/components/shared/DocumentsPanel.svelte';
	import HelpTooltip from '$lib/components/ui/HelpTooltip.svelte';
	import * as Tooltip from '$lib/components/ui/tooltip';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const LEASE_STATUSES = ['Draft', 'Active', 'NoticeGiven', 'Expired', 'Terminated'];

	const leaseId = $derived(parseInt($page.params.id ?? '0', 10));

	const leaseQuery = createQuery(() => ({
		queryKey: ['lease', leaseId],
		queryFn: () => leases.get(leaseId),
		enabled: leaseId > 0,
	}));

	const lease = $derived(leaseQuery.data);

	// Payments for this lease
	const paymentsQuery = createQuery(() => ({
		queryKey: ['payments', portfolioId, leaseId],
		queryFn: () => payments.list(portfolioId, { leaseId, take: 200 }),
		enabled: portfolioId > 0 && leaseId > 0,
	}));

	// Plain-English account history (charges, payments, balance) with a "why" per entry.
	const ledgerQuery = createQuery(() => ({
		queryKey: ['lease-ledger', leaseId],
		queryFn: () => leases.ledger(leaseId),
		enabled: leaseId > 0,
	}));
	const ledger = $derived(ledgerQuery.data);
	const balanceLine = $derived.by(() => {
		if (!ledger) return '';
		if (ledger.balance > 0.005) return `${ledger.tenantName ?? 'This tenant'} still owes ${formatCurrency(ledger.balance)}.`;
		if (ledger.balance < -0.005) return `Paid ahead by ${formatCurrency(Math.abs(ledger.balance))} (credit on the account).`;
		return 'All caught up — nothing owed.';
	});

	// --- Opening balance (carried-over tenant balance from before Rental Command) ---
	const openingBalanceQuery = createQuery(() => ({
		queryKey: ['opening-balance', leaseId],
		queryFn: () => openingBalances.list(leaseId),
		enabled: leaseId > 0,
	}));
	const openingBalance = $derived<OpeningBalanceResponse | undefined>(openingBalanceQuery.data?.[0]);

	function invalidateOpeningBalance() {
		queryClient.invalidateQueries({ queryKey: ['opening-balance', leaseId] });
		// Refresh the Account History so the auto-rendered "Opening" entry updates.
		queryClient.invalidateQueries({ queryKey: ['lease-ledger', leaseId] });
	}

	// Dialog state. We keep the UI sign-positive (always a non-negative amount the
	// user types) and capture direction with a select: "owed" → +, "credit" → -.
	let showOpeningDialog = $state(false);
	let openingDirection = $state<'owed' | 'credit'>('owed');
	let openingAmount = $state('');
	let openingAsOfDate = $state('');
	let openingNote = $state('');
	let openingErrors = $state<{ amount?: string; asOfDate?: string }>({});
	let showOpeningDeleteConfirm = $state(false);

	const directionOptions = [
		{ value: 'owed', label: 'Tenant owed' },
		{ value: 'credit', label: 'Tenant credit' },
	];
	const openingDirectionLabel = $derived(
		directionOptions.find((o) => o.value === openingDirection)?.label ?? 'Tenant owed'
	);

	function openOpeningDialog() {
		if (openingBalance) {
			openingDirection = openingBalance.amount < 0 ? 'credit' : 'owed';
			openingAmount = String(Math.abs(openingBalance.amount));
			openingAsOfDate = openingBalance.asOfDate?.slice(0, 10) ?? '';
			openingNote = openingBalance.note ?? '';
		} else {
			openingDirection = 'owed';
			openingAmount = '';
			// Default the as-of date to the lease start, falling back to today.
			openingAsOfDate = lease?.startDate?.slice(0, 10) ?? new Date().toISOString().slice(0, 10);
			openingNote = '';
		}
		openingErrors = {};
		showOpeningDialog = true;
	}
	function closeOpeningDialog() {
		openingErrors = {};
		showOpeningDialog = false;
	}

	const saveOpeningMutation = createMutation(() => ({
		mutationFn: (body: { amount: number; asOfDate: string; note?: string }) =>
			openingBalance
				? openingBalances.update(openingBalance.id, body)
				: openingBalances.create({ leaseId, ...body }),
		onSuccess: () => {
			showSuccess(openingBalance ? 'Opening balance updated.' : 'Opening balance saved.');
			closeOpeningDialog();
			invalidateOpeningBalance();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const deleteOpeningMutation = createMutation(() => ({
		mutationFn: (id: number) => openingBalances.remove(id),
		onSuccess: () => {
			showSuccess('Opening balance removed.');
			showOpeningDeleteConfirm = false;
			invalidateOpeningBalance();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function submitOpeningBalance() {
		const errors: { amount?: string; asOfDate?: string } = {};
		const magnitude = Number(openingAmount);
		if (!openingAmount.trim() || Number.isNaN(magnitude) || magnitude < 0) {
			errors.amount = 'Enter an amount of 0 or more.';
		}
		if (!openingAsOfDate) {
			errors.asOfDate = 'Pick an as-of date.';
		}
		if (errors.amount || errors.asOfDate) {
			openingErrors = errors;
			return;
		}
		openingErrors = {};
		const signed = openingDirection === 'credit' ? -Math.abs(magnitude) : Math.abs(magnitude);
		const body: { amount: number; asOfDate: string; note?: string } = {
			amount: signed,
			asOfDate: openingAsOfDate,
		};
		const note = openingNote.trim();
		if (note) body.note = note;
		saveOpeningMutation.mutate(body);
	}

	// Form (edit dialog) state
	const propertiesQuery = createQuery(() => ({
		queryKey: ['properties', portfolioId],
		queryFn: () => properties.list(portfolioId, { take: 200 }),
	}));
	const tenantsQuery = createQuery(() => ({
		queryKey: ['tenants', portfolioId],
		queryFn: () => tenants.list(portfolioId, { take: 200 }),
	}));

	let formPropertyId = $state('');
	const unitsForPropertyQuery = createQuery(() => ({
		queryKey: ['units-for-lease', formPropertyId],
		enabled: !!formPropertyId,
		queryFn: () => properties.listUnits(Number(formPropertyId)),
	}));

	const empty = {
		leaseNumber: '', propertyId: '', unitId: '', tenantId: '', startDate: '', endDate: '',
		monthlyRent: '', securityDeposit: '', lateFeeAmount: '75', rentDueDay: '1', status: 'Draft', notes: '',
	};
	let editing = $state(false);
	let form = $state({ ...empty });
	let formErrors = $state<Record<string, string>>({});
	let showDeleteConfirm = $state(false);

	$effect(() => {
		if (form.propertyId !== formPropertyId) {
			formPropertyId = form.propertyId;
		}
	});

	function invalidateLease() {
		queryClient.invalidateQueries({ queryKey: ['lease', leaseId] });
		queryClient.invalidateQueries({ queryKey: ['leases', portfolioId] });
	}

	const saveMutation = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => leases.update(leaseId, data),
		onSuccess: () => {
			showSuccess('Lease updated.');
			editing = false;
			formErrors = {};
			invalidateLease();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const statusMutation = createMutation(() => ({
		mutationFn: (status: string) => leases.update(leaseId, { status }),
		onSuccess: () => {
			showSuccess('Lease status updated.');
			invalidateLease();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const deleteMutation = createMutation(() => ({
		mutationFn: () => leases.delete(leaseId),
		onSuccess: () => {
			showSuccess('Lease deleted.');
			goto('/leases');
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const markPaidMutation = createMutation(() => ({
		mutationFn: (paymentId: number) => payments.markPaid(paymentId, {}),
		onSuccess: () => {
			showSuccess('Payment marked as paid.');
			queryClient.invalidateQueries({ queryKey: ['payments', portfolioId, leaseId] });
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	// --- Lease agreement PDF (generate + authed blob download) ---
	// Whether a generated agreement already exists. Probed once on load with a
	// no-download GET; 404 → only show Generate, 200 → also show Download.
	let hasDocument = $state(false);
	let documentChecked = $state(false);
	let downloadingDocument = $state(false);

	$effect(() => {
		if (leaseId > 0 && !documentChecked) {
			documentChecked = true;
			leases
				.downloadDocument(leaseId, false)
				.then((exists) => {
					hasDocument = exists;
				})
				.catch(() => {
					// Probe failure is non-fatal — leave the Generate button available.
				});
		}
	});

	const generateDocMutation = createMutation(() => ({
		mutationFn: () => leases.generateDocument(leaseId),
		onSuccess: () => {
			hasDocument = true;
			showSuccess('Lease agreement created. You can download it now.');
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	async function handleDocumentDownload() {
		downloadingDocument = true;
		try {
			const ok = await leases.downloadDocument(leaseId);
			if (!ok) {
				hasDocument = false;
				showError('No lease agreement has been generated yet.');
			}
		} catch {
			showError('Could not download the lease agreement. Please try again.');
		} finally {
			downloadingDocument = false;
		}
	}

	// --- E-signature (gated: provider stays dormant until keys are set) ---
	// Whether the e-sign provider replied "not configured" (HTTP 503). When true we
	// show a calm, unobtrusive note instead of a scary error.
	let esignNotConfigured = $state(false);
	let downloadingSignedDocument = $state(false);

	const signatureStatusQuery = createQuery(() => ({
		queryKey: ['lease-signature-status', leaseId],
		queryFn: () => leases.signatureStatus(leaseId),
		enabled: leaseId > 0,
		// While we're waiting on a signature, poll so the card flips to "Signed"
		// automatically once the tenant signs. Stop polling otherwise.
		refetchInterval: (query) => (query.state.data?.esignStatus === 'Sent' ? 5000 : false),
	}));
	const signature = $derived(signatureStatusQuery.data);

	const esignStatusLabel = $derived.by(() => {
		switch (signature?.esignStatus) {
			case 'Sent':
				return 'Sent — waiting for signature';
			case 'Signed':
				return 'Signed';
			case 'Declined':
				return 'Declined';
			default:
				return 'Not sent';
		}
	});

	function invalidateSignatureStatus() {
		queryClient.invalidateQueries({ queryKey: ['lease-signature-status', leaseId] });
		// Status changes flip the lease status too (e.g. PendingSignature).
		invalidateLease();
	}

	const sendForSignatureMutation = createMutation(() => ({
		mutationFn: () => leases.sendForSignature(leaseId),
		onSuccess: () => {
			esignNotConfigured = false;
			showSuccess(`Sent to ${lease?.tenantName ?? 'the tenant'} for signature.`);
			invalidateSignatureStatus();
		},
		onError: (err) => {
			// 503 = no e-sign provider configured. Stay calm: show an inline note, not a toast.
			if (err instanceof ApiError && err.status === 503) {
				esignNotConfigured = true;
				return;
			}
			showError(apiErrorMessage(err));
		},
	}));

	async function handleSignedDocumentDownload() {
		downloadingSignedDocument = true;
		try {
			const ok = await leases.downloadSignedDocument(leaseId);
			if (!ok) {
				showError('The signed lease isn’t available yet.');
				invalidateSignatureStatus();
			}
		} catch {
			showError('Could not download the signed lease. Please try again.');
		} finally {
			downloadingSignedDocument = false;
		}
	}

	function startEditing() {
		if (!lease) return;
		form = {
			leaseNumber: lease.leaseNumber,
			propertyId: String(lease.propertyId),
			unitId: String(lease.unitId),
			tenantId: String(lease.tenantId),
			startDate: lease.startDate?.slice(0, 10) ?? '',
			endDate: lease.endDate?.slice(0, 10) ?? '',
			monthlyRent: String(lease.monthlyRent),
			securityDeposit: String(lease.securityDeposit),
			lateFeeAmount: String(lease.lateFeeAmount),
			rentDueDay: String(lease.rentDueDay),
			status: lease.status,
			notes: lease.notes ?? '',
		};
		formPropertyId = String(lease.propertyId);
		formErrors = {};
		editing = true;
	}
	function cancelEditing() {
		editing = false;
		formErrors = {};
	}

	function submit() {
		const { propertyId: _p, ...rest } = form;
		const result = parseForm(leaseSchema, rest);
		if (result.errors) {
			formErrors = result.errors;
			return;
		}
		formErrors = {};
		saveMutation.mutate({ portfolioId, ...result.data });
	}

	// Select options for inline FK/enum fields
	const statusOptions = $derived(LEASE_STATUSES.map((value) => ({ value, label: value })));
	const propertyOptions = $derived([
		{ value: '', label: 'Select property' },
		...(propertiesQuery.data ?? []).map((p) => ({ value: String(p.id), label: p.name })),
	]);
	const unitOptions = $derived([
		{ value: '', label: 'Select unit' },
		...(unitsForPropertyQuery.data ?? []).map((u) => ({
			value: String(u.id),
			label: `Unit ${u.unitNumber} (${u.status})`,
		})),
	]);
	const tenantOptions = $derived([
		{ value: '', label: 'Select tenant' },
		...(tenantsQuery.data ?? []).map((t) => ({
			value: String(t.id),
			label: t.fullName || `${t.firstName} ${t.lastName}`,
		})),
	]);

	// Payments DataGrid columns
	const paymentColumns: ColumnDef<Payment>[] = [
		{
			key: 'dueDate',
			title: 'Due Date',
			format: 'date',
			sortable: true,
			mobileRole: 'title',
		},
		{
			key: 'type',
			title: 'Type',
			mobileRole: 'subtitle',
		},
		{
			key: 'amount',
			title: 'Amount',
			format: 'currency',
			sortable: true,
			mobileRole: 'metric',
		},
		{
			key: 'status',
			title: 'Status',
			mobileRole: 'badge',
			cell: paymentStatusCell,
		},
		{
			key: 'actions',
			title: '',
			mobileRole: 'hidden',
			align: 'right',
			cell: paymentActionsCell,
		},
	];

	function formatCurrency(val: number): string {
		return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(val);
	}

	function formatDate(val: string | undefined): string {
		if (!val) return '—';
		const d = new Date(val);
		return isNaN(d.getTime()) ? val : d.toLocaleDateString();
	}

	// Active tab for the detail page.
	let activeTab = $state('overview');
	const tabs = [
		{ value: 'overview', label: 'Overview' },
		{ value: 'agreement', label: 'Agreement & Signing' },
		{ value: 'ledger', label: 'Ledger' },
	];

	// Subtle tint for the hero rent figure / status-keyed accent bar.
	const heroAccent = $derived.by(() => {
		switch (lease?.status) {
			case 'Active': return 'from-success/10';
			case 'Expired':
			case 'Terminated': return 'from-destructive/10';
			case 'NoticeGiven': return 'from-warning/10';
			default: return 'from-primary/10';
		}
	});
</script>

{#snippet paymentStatusCell(payment: Payment)}
	<StatusBadge status={payment.status} />
{/snippet}

{#snippet paymentActionsCell(payment: Payment)}
	{#if payment.status !== 'Paid' && payment.status !== 'Waived'}
		<Button
			variant="outline"
			size="sm"
			onclick={(e) => { e.stopPropagation(); markPaidMutation.mutate(payment.id); }}
			disabled={markPaidMutation.isPending}
		>
			Mark Paid
		</Button>
	{/if}
{/snippet}

<svelte:head>
	<title>{lease ? `Lease ${lease.leaseNumber}` : 'Lease'} - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="lease-detail-page">
	{#if leaseQuery.isLoading}
		<div class="flex h-48 items-center justify-center" data-testid="lease-detail-loading">
			<div class="h-8 w-8 animate-spin rounded-full border-2 border-accent border-t-transparent"></div>
		</div>
	{:else if leaseQuery.isError || !lease}
		<div class="flex h-48 flex-col items-center justify-center gap-3 text-center" data-testid="lease-detail-not-found">
			<p class="text-sm font-medium">Lease not found.</p>
			<Button variant="outline" size="sm" onclick={() => goto('/leases')}>Back to Leases</Button>
		</div>
	{:else}
		<!-- Breadcrumb -->
		<div class="mb-4">
			<PageBreadcrumb
				crumbs={[
					{ label: 'Leases', href: '/leases' },
					{ label: lease.leaseNumber },
				]}
			/>
		</div>

		<!-- Header -->
		<div class="mb-6 flex flex-wrap items-start gap-3">
			<div class="flex-1">
				<div class="flex flex-wrap items-center gap-2">
					<h1 class="text-2xl font-bold" data-testid="lease-detail-number">{lease.leaseNumber}</h1>
					<StatusBadge status={lease.status} />
				</div>
				<p class="mt-1 text-sm text-muted-foreground">
					{#if lease.propertyName}{lease.propertyName}{/if}
					{#if lease.unitNumber} · Unit {lease.unitNumber}{/if}
					{#if lease.tenantName} · {lease.tenantName}{/if}
				</p>
			</div>
			<div class="flex flex-wrap items-center gap-2">
				{#if editing}
					<Button data-testid="lease-detail-cancel" variant="outline" size="sm" class="gap-1.5" onclick={cancelEditing} disabled={saveMutation.isPending}>
						<X class="h-4 w-4" />
						Cancel
					</Button>
					<Button data-testid="lease-detail-save" size="sm" class="gap-1.5" onclick={submit} disabled={saveMutation.isPending}>
						<Save class="h-4 w-4" />
						{saveMutation.isPending ? 'Saving…' : 'Save'}
					</Button>
				{:else}
					{#if lease.status !== 'Active'}
						<Button data-testid="lease-set-active" variant="outline" size="sm" onclick={() => statusMutation.mutate('Active')} disabled={statusMutation.isPending}>
							Set Active
						</Button>
					{:else}
						<Button data-testid="lease-give-notice" variant="outline" size="sm" onclick={() => statusMutation.mutate('NoticeGiven')} disabled={statusMutation.isPending}>
							Give Notice
						</Button>
					{/if}
					<Button data-testid="lease-edit" variant="outline" size="sm" class="gap-1.5" onclick={startEditing}>
						<Pencil class="h-4 w-4" />
						Edit
					</Button>
					<Button data-testid="lease-delete" variant="outline" size="sm" class="gap-1.5 hover:text-destructive" onclick={() => (showDeleteConfirm = true)}>
						<Trash2 class="h-4 w-4" />
						Delete
					</Button>
				{/if}
			</div>
		</div>

		<Tabs.Root bind:value={activeTab} class="w-full">
			<Tabs.List class="mb-6" data-testid="lease-detail-tabs">
				{#each tabs as t}
					<Tabs.Trigger value={t.value} data-testid="lease-tab-{t.value}">{t.label}</Tabs.Trigger>
				{/each}
			</Tabs.List>

			<!-- ───────────────────────── OVERVIEW ───────────────────────── -->
			<Tabs.Content value="overview" class="space-y-6">
				<!-- Hero: the few things that matter most + a state-aware primary CTA -->
				<Card.Root class="overflow-hidden" data-testid="lease-hero">
					<div class="bg-gradient-to-br {heroAccent} to-transparent">
						<Card.Content class="flex flex-wrap items-end justify-between gap-6 p-6">
							<div class="min-w-0">
								<p class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Monthly Rent</p>
								<p class="mt-1 font-mono text-4xl font-bold tabular-nums tracking-tight" data-testid="lease-hero-rent">
									{formatCurrency(lease.monthlyRent)}
								</p>
								<div class="mt-3 flex flex-wrap items-center gap-x-2 gap-y-1 text-sm text-muted-foreground">
									<StatusBadge status={lease.status} />
									{#if lease.tenantName}
										<a href="/tenants/{lease.tenantId}" class="font-medium text-foreground underline-offset-4 hover:underline">{lease.tenantName}</a>
									{/if}
									{#if lease.propertyName}
										<span aria-hidden="true">·</span>
										<a href="/properties/{lease.propertyId}" class="underline-offset-4 hover:underline">{lease.propertyName}</a>{#if lease.unitNumber}<span>, Unit {lease.unitNumber}</span>{/if}
									{/if}
								</div>
							</div>
							<div class="flex flex-col items-stretch gap-2">
								{#if ledger && Math.abs(ledger.balance) > 0.005}
									<div class="rounded-lg border border-border bg-background/70 px-4 py-2 text-right">
										<p class="text-[11px] uppercase tracking-wide text-muted-foreground">Balance</p>
										<p class="font-mono text-lg font-bold tabular-nums {ledger.balance > 0.005 ? 'text-warning' : 'text-success'}">{formatCurrency(ledger.balance)}</p>
									</div>
								{/if}
								{#if !editing}
									{#if lease.status === 'Active'}
										<Button data-testid="lease-hero-cta" size="sm" class="gap-1.5" onclick={() => activeTab = 'ledger'}>
											<DollarSign class="h-4 w-4" />
											View ledger
										</Button>
									{:else}
										<Button data-testid="lease-hero-cta" size="sm" class="gap-1.5" onclick={() => statusMutation.mutate('Active')} disabled={statusMutation.isPending}>
											Set lease active
										</Button>
									{/if}
								{/if}
							</div>
						</Card.Content>
					</div>
				</Card.Root>

				<!-- Grouped detail cards. Inline-edit happens within each card's context. -->
				<div class="grid gap-6 lg:grid-cols-2">
					<!-- Term -->
					<Card.Root data-testid="lease-card-term">
						<Card.Header class="pb-3">
							<Card.Title class="flex items-center gap-2 text-base">
								<CalendarRange class="h-4 w-4 text-primary" />
								Term
							</Card.Title>
						</Card.Header>
						<Card.Content class="grid grid-cols-2 gap-x-6 gap-y-4">
							<InlineField label="Start Date" bind:value={form.startDate} display={formatDate(lease.startDate)} {editing} onedit={startEditing} type="date" error={formErrors.startDate} testid="lease-detail-start" />
							<InlineField label="End Date" bind:value={form.endDate} display={formatDate(lease.endDate)} {editing} onedit={startEditing} type="date" error={formErrors.endDate} testid="lease-detail-end" />
							<InlineField label="Rent Due Day" bind:value={form.rentDueDay} display={`Day ${lease.rentDueDay}`} {editing} onedit={startEditing} type="number" error={formErrors.rentDueDay} testid="lease-detail-due-day" />
							<InlineField label="Status" bind:value={form.status} display={lease.status} {editing} onedit={startEditing} type="select" options={statusOptions} error={formErrors.status} testid="lease-detail-status" />
							{#if !editing && lease.moveInDate}
								<div>
									<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Move-In</dt>
									<dd class="mt-0.5 text-sm">{formatDate(lease.moveInDate)}</dd>
								</div>
							{/if}
							{#if !editing && lease.moveOutDate}
								<div>
									<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Move-Out</dt>
									<dd class="mt-0.5 text-sm">{formatDate(lease.moveOutDate)}</dd>
								</div>
							{/if}
						</Card.Content>
					</Card.Root>

					<!-- Financials -->
					<Card.Root data-testid="lease-card-financials">
						<Card.Header class="pb-3">
							<Card.Title class="flex items-center gap-2 text-base">
								<DollarSign class="h-4 w-4 text-success" />
								Financials
							</Card.Title>
						</Card.Header>
						<Card.Content class="grid grid-cols-2 gap-x-6 gap-y-4">
							<InlineField label="Monthly Rent" bind:value={form.monthlyRent} display={formatCurrency(lease.monthlyRent)} {editing} onedit={startEditing} type="number" error={formErrors.monthlyRent} testid="lease-detail-rent" />
							<InlineField label="Security Deposit" bind:value={form.securityDeposit} display={formatCurrency(lease.securityDeposit)} {editing} onedit={startEditing} type="number" error={formErrors.securityDeposit} testid="lease-detail-deposit" />
							<InlineField label="Late Fee" bind:value={form.lateFeeAmount} display={formatCurrency(lease.lateFeeAmount)} {editing} onedit={startEditing} type="number" error={formErrors.lateFeeAmount} testid="lease-detail-late-fee" />
							<InlineField label="Lease Number" bind:value={form.leaseNumber} display={lease.leaseNumber} {editing} onedit={startEditing} error={formErrors.leaseNumber} testid="lease-detail-number-field" />
						</Card.Content>
					</Card.Root>

					<!-- Parties -->
					<Card.Root data-testid="lease-card-parties">
						<Card.Header class="pb-3">
							<Card.Title class="flex items-center gap-2 text-base">
								<Users class="h-4 w-4 text-primary" />
								Parties
							</Card.Title>
						</Card.Header>
						<Card.Content class="grid grid-cols-2 gap-x-6 gap-y-4">
							<InlineField label="Property" bind:value={form.propertyId} display={lease.propertyName} {editing} onedit={startEditing} type="select" options={propertyOptions} testid="lease-detail-property" />
							<InlineField label="Unit" bind:value={form.unitId} display={lease.unitNumber ? `Unit ${lease.unitNumber}` : ''} {editing} onedit={startEditing} type="select" options={unitOptions} error={formErrors.unitId} testid="lease-detail-unit" />
							<InlineField label="Tenant" bind:value={form.tenantId} display={lease.tenantName} {editing} onedit={startEditing} type="select" options={tenantOptions} error={formErrors.tenantId} testid="lease-detail-tenant" class="col-span-2" />
						</Card.Content>
					</Card.Root>

					<!-- Notes -->
					<Card.Root data-testid="lease-card-notes">
						<Card.Header class="pb-3">
							<Card.Title class="flex items-center gap-2 text-base">
								<StickyNote class="h-4 w-4 text-muted-foreground" />
								Notes
							</Card.Title>
						</Card.Header>
						<Card.Content>
							<InlineField label="Notes" bind:value={form.notes} display={lease.notes} {editing} onedit={startEditing} type="textarea" error={formErrors.notes} testid="lease-detail-notes" />
						</Card.Content>
					</Card.Root>
				</div>
			</Tabs.Content>

			<!-- ───────────────────── AGREEMENT & SIGNING ───────────────────── -->
			<Tabs.Content value="agreement" class="space-y-6">
		<!-- Lease agreement PDF — generate, then download an authed blob -->
		<Card.Root data-testid="lease-agreement-card">
			<Card.Header>
				<Card.Title class="text-base">Lease Agreement</Card.Title>
				<Card.Description>Create a printable lease-agreement PDF from this lease's details, download it, or send it to the tenant to sign.</Card.Description>
			</Card.Header>
			<Card.Content>
				<div class="flex flex-wrap items-center gap-2">
					<Button
						data-testid="lease-generate-document"
						variant={hasDocument ? 'outline' : 'default'}
						size="sm"
						class="gap-1.5"
						onclick={() => generateDocMutation.mutate()}
						disabled={generateDocMutation.isPending}
					>
						<FileText class="h-4 w-4" />
						{generateDocMutation.isPending
							? 'Generating…'
							: hasDocument
								? 'Regenerate lease agreement (PDF)'
								: 'Generate lease agreement (PDF)'}
					</Button>
					{#if hasDocument}
						<Button
							data-testid="lease-download-document"
							size="sm"
							class="gap-1.5"
							onclick={handleDocumentDownload}
							disabled={downloadingDocument}
						>
							<Download class="h-4 w-4" />
							{downloadingDocument ? 'Preparing…' : 'Download agreement'}
						</Button>
					{/if}
				</div>
				{#if !hasDocument && !generateDocMutation.isPending}
					<p class="mt-2 text-xs text-muted-foreground">No agreement has been generated yet.</p>
				{/if}

				<!-- E-signature: send the lease to the tenant to sign, track status, download the signed copy -->
				<div class="mt-4 border-t border-border pt-4" data-testid="lease-esign">
					<div class="flex flex-wrap items-center justify-between gap-2">
						<div class="flex items-center gap-2">
							<p class="text-sm font-medium">E-signature</p>
							<span
								class="inline-flex items-center rounded-full border border-border bg-muted/40 px-2 py-0.5 text-xs font-medium text-muted-foreground"
								data-testid="lease-esign-status"
							>
								{esignStatusLabel}
							</span>
							{#if signature?.leaseStatus === 'PendingSignature'}
								<span
									class="inline-flex items-center rounded-full border border-warning/40 bg-warning/10 px-2 py-0.5 text-xs font-medium text-warning"
									data-testid="lease-esign-pending"
								>
									Lease pending signature
								</span>
							{/if}
						</div>
						<div class="flex flex-wrap items-center gap-2">
							<Button
								data-testid="lease-send-for-signature"
								variant="outline"
								size="sm"
								class="gap-1.5"
								onclick={() => sendForSignatureMutation.mutate()}
								disabled={sendForSignatureMutation.isPending}
							>
								<PenLine class="h-4 w-4" />
								{sendForSignatureMutation.isPending
									? 'Sending…'
									: signature?.esignStatus === 'Sent'
										? 'Resend for signature'
										: 'Send for signature'}
							</Button>
							{#if signature?.hasSignedDocument}
								<Button
									data-testid="lease-download-signed-document"
									size="sm"
									class="gap-1.5"
									onclick={handleSignedDocumentDownload}
									disabled={downloadingSignedDocument}
								>
									<Download class="h-4 w-4" />
									{downloadingSignedDocument ? 'Preparing…' : 'Download signed lease'}
								</Button>
							{/if}
						</div>
					</div>

					{#if esignNotConfigured}
						<p class="mt-2 text-xs text-muted-foreground" data-testid="lease-esign-not-configured">
							E-signature isn’t set up yet (no e-sign provider configured).
						</p>
					{:else if signature?.esignStatus === 'Sent'}
						<p class="mt-2 text-xs text-muted-foreground" data-testid="lease-esign-sent-note">
							Waiting for {lease?.tenantName ?? 'the tenant'} to sign. This updates automatically once they do.
						</p>
					{:else if signature?.esignStatus === 'Signed'}
						<p class="mt-2 text-xs text-success" data-testid="lease-esign-signed-note">
							Signed. You can download the signed lease above.
						</p>
					{:else if signature?.esignStatus === 'Declined'}
						<p class="mt-2 text-xs text-muted-foreground" data-testid="lease-esign-declined-note">
							The signer declined. You can send it again when you’re ready.
						</p>
					{:else}
						<p class="mt-2 text-xs text-muted-foreground">
							Send this lease to {lease?.tenantName ?? 'the tenant'} to sign electronically. We’ll generate the agreement if needed.
						</p>
					{/if}
				</div>
			</Card.Content>
		</Card.Root>
			</Tabs.Content>

			<!-- ───────────────────────── LEDGER ───────────────────────── -->
			<Tabs.Content value="ledger" class="space-y-6">
		<!-- Account history (plain-English ledger with a "why" per line) -->
		<Card.Root data-testid="lease-ledger-card">
			<Card.Header>
				<Card.Title class="text-base">Account History</Card.Title>
				<Card.Description>Every charge and payment on this lease, in plain English.</Card.Description>
			</Card.Header>
			<Card.Content>
				{#if ledgerQuery.isLoading}
					<div class="space-y-2">
						{#each [0, 1, 2] as _}
							<div class="h-12 w-full animate-pulse rounded border border-border bg-muted"></div>
						{/each}
					</div>
				{:else if ledgerQuery.isError || !ledger}
					<p class="text-sm text-muted-foreground">Account history is unavailable right now.</p>
				{:else}
					<div class="mb-4 grid grid-cols-3 gap-3">
						<div class="rounded-md border border-border bg-background p-3" data-testid="lease-ledger-charged">
							<p class="text-xs text-muted-foreground">Charged</p>
							<p class="mt-0.5 font-mono tabular-nums text-lg font-bold">{formatCurrency(ledger.totalCharged)}</p>
						</div>
						<div class="rounded-md border border-success/30 bg-success/5 p-3" data-testid="lease-ledger-paid">
							<p class="text-xs text-muted-foreground">Paid</p>
							<p class="mt-0.5 font-mono tabular-nums text-lg font-bold text-success">{formatCurrency(ledger.totalPaid)}</p>
						</div>
						<div class="rounded-md border border-border bg-background p-3" data-testid="lease-ledger-balance">
							<p class="text-xs text-muted-foreground">Balance</p>
							<p class="mt-0.5 font-mono tabular-nums text-lg font-bold {ledger.balance > 0.005 ? 'text-warning' : 'text-success'}">{formatCurrency(ledger.balance)}</p>
						</div>
					</div>
					<p class="mb-4 text-sm font-medium" data-testid="lease-ledger-balance-line">{balanceLine}</p>

					<!-- Opening balance control (carried-over balance from before Rental Command) -->
					<div class="mb-4 rounded-md border border-dashed border-border bg-muted/20 p-3" data-testid="lease-opening-balance">
						{#if openingBalanceQuery.isLoading}
							<div class="h-9 w-full animate-pulse rounded bg-muted"></div>
						{:else if openingBalance}
							<div class="flex flex-wrap items-center justify-between gap-3">
								<div class="min-w-0">
									<p class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Opening balance</p>
									<p class="mt-0.5 text-sm font-medium" data-testid="lease-opening-balance-value">
										{#if openingBalance.amount < -0.005}
											Tenant credit of <span class="font-mono tabular-nums">{formatCurrency(Math.abs(openingBalance.amount))}</span>
										{:else if openingBalance.amount > 0.005}
											Tenant owed <span class="font-mono tabular-nums">{formatCurrency(openingBalance.amount)}</span>
										{:else}
											<span class="font-mono tabular-nums">{formatCurrency(0)}</span>
										{/if}
										<span class="text-muted-foreground"> · as of {formatDate(openingBalance.asOfDate)}</span>
									</p>
									{#if openingBalance.note}
										<p class="mt-0.5 text-xs text-muted-foreground">{openingBalance.note}</p>
									{/if}
								</div>
								<div class="flex items-center gap-2">
									<Button variant="outline" size="sm" class="gap-1.5" onclick={openOpeningDialog} data-testid="lease-opening-balance-edit">
										<Pencil class="h-4 w-4" />
										Edit
									</Button>
									<Button variant="outline" size="sm" class="gap-1.5 hover:text-destructive" onclick={() => (showOpeningDeleteConfirm = true)} data-testid="lease-opening-balance-remove">
										<Trash2 class="h-4 w-4" />
										Remove
									</Button>
								</div>
							</div>
						{:else}
							<div class="flex flex-wrap items-center justify-between gap-3">
								<p class="min-w-0 text-xs text-muted-foreground">
									Carrying a balance from before Rental Command? Set the tenant's starting balance so the ledger is accurate.
								</p>
								<Button variant="outline" size="sm" onclick={openOpeningDialog} data-testid="lease-opening-balance-set">
									Set opening balance
								</Button>
							</div>
						{/if}
					</div>

					{#if ledger.entries.length === 0}
						<p class="text-sm text-muted-foreground">No charges or payments recorded yet.</p>
					{:else}
						<Tooltip.Provider delayDuration={150}>
							<ul class="space-y-2" data-testid="lease-ledger-entries">
								{#each ledger.entries as entry}
									<li class="rounded-md border border-border bg-background p-3" data-testid="lease-ledger-entry">
										<div class="flex items-start justify-between gap-3">
											<div class="min-w-0">
												<div class="flex items-center gap-1.5">
													<p class="truncate text-sm font-medium">{entry.description}</p>
													{#if entry.explanation}
														<HelpTooltip text={entry.explanation} label="Why this is here" />
													{/if}
												</div>
												<p class="mt-0.5 text-xs text-muted-foreground">{formatDate(entry.date)} · {entry.type}{entry.status ? ` · ${entry.status}` : ''}</p>
												{#if entry.explanation}
													<p class="mt-1 text-xs leading-snug text-muted-foreground" data-testid="lease-ledger-entry-explanation">{entry.explanation}</p>
												{/if}
											</div>
											<p class="shrink-0 font-mono tabular-nums text-sm font-semibold {entry.type === 'Payment' ? 'text-success' : ''}">{formatCurrency(entry.amount)}</p>
										</div>
									</li>
								{/each}
							</ul>
						</Tooltip.Provider>
					{/if}
				{/if}
			</Card.Content>
		</Card.Root>

		<!-- Payments section -->
		<div>
			<div class="mb-2 flex items-center justify-between">
				<h2 class="text-lg font-semibold">Payments</h2>
				<a href="/deposits" class="text-xs text-muted-foreground underline-offset-4 hover:underline">View Deposits</a>
			</div>
			<DataGrid
				data={paymentsQuery.data ?? []}
				columns={paymentColumns}
				loading={paymentsQuery.isLoading}
				emptyMessage="No payments recorded for this lease."
				getRowKey={(p) => p.id}
				data-testid="lease-payments-grid"
			/>
		</div>

		<!-- Documents section -->
		<div data-testid="lease-detail-documents">
			<DocumentsPanel entityType="Lease" entityId={leaseId} />
		</div>
			</Tabs.Content>
		</Tabs.Root>
	{/if}
</div>

<ConfirmDialog
	open={showDeleteConfirm}
	title="Delete lease"
	message={lease ? `Delete lease ${lease.leaseNumber}? This cannot be undone.` : ''}
	busy={deleteMutation.isPending}
	testid="lease-delete-confirm"
	onconfirm={() => deleteMutation.mutate()}
	oncancel={() => (showDeleteConfirm = false)}
/>

<!-- Opening balance dialog (set / edit) -->
<Dialog.Root open={showOpeningDialog} onOpenChange={(v) => { if (!v) closeOpeningDialog(); }}>
	<Dialog.Content class="max-w-md">
		<Dialog.Header>
			<Dialog.Title>{openingBalance ? 'Edit opening balance' : 'Set opening balance'}</Dialog.Title>
			<Dialog.Description>
				Did this tenant already owe you (or have a credit) before you started using Rental Command?
				Enter the starting balance so the ledger is accurate.
			</Dialog.Description>
		</Dialog.Header>
		<div class="space-y-3" data-testid="lease-opening-balance-form">
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">Direction</span>
				<Select.Root type="single" bind:value={openingDirection}>
					<Select.Trigger class="w-full" data-testid="lease-opening-balance-direction">
						{openingDirectionLabel}
					</Select.Trigger>
					<Select.Content>
						{#each directionOptions as option}
							<Select.Item value={option.value} label={option.label}>{option.label}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
				<p class="mt-1 text-xs text-muted-foreground">
					"Tenant owed" means they were behind; "Tenant credit" means they were paid ahead.
				</p>
			</div>
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">Amount</span>
				<Input
					data-testid="lease-opening-balance-amount"
					bind:value={openingAmount}
					type="number"
					min="0"
					step="0.01"
					placeholder="0.00"
				/>
				{#if openingErrors.amount}<p class="mt-1 text-xs text-destructive" data-testid="lease-opening-balance-amount-error">{openingErrors.amount}</p>{/if}
			</div>
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">As of date</span>
				<Input
					data-testid="lease-opening-balance-date"
					bind:value={openingAsOfDate}
					type="date"
				/>
				{#if openingErrors.asOfDate}<p class="mt-1 text-xs text-destructive" data-testid="lease-opening-balance-date-error">{openingErrors.asOfDate}</p>{/if}
			</div>
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">Note (optional)</span>
				<textarea
					data-testid="lease-opening-balance-note"
					bind:value={openingNote}
					rows="2"
					class="w-full rounded-md border border-input bg-background px-3 py-2 text-sm shadow-xs outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50"
				></textarea>
			</div>
		</div>
		<Dialog.Footer>
			<Button variant="outline" onclick={closeOpeningDialog} data-testid="lease-opening-balance-cancel">Cancel</Button>
			<Button onclick={submitOpeningBalance} disabled={saveOpeningMutation.isPending} data-testid="lease-opening-balance-save">
				{saveOpeningMutation.isPending ? 'Saving…' : openingBalance ? 'Save changes' : 'Save opening balance'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<ConfirmDialog
	open={showOpeningDeleteConfirm}
	title="Remove opening balance"
	message="Remove the opening balance for this lease? The ledger will no longer show the carried-over starting balance."
	confirmLabel="Remove"
	busy={deleteOpeningMutation.isPending}
	testid="lease-opening-balance-delete-confirm"
	onconfirm={() => { if (openingBalance) deleteOpeningMutation.mutate(openingBalance.id); }}
	oncancel={() => (showOpeningDeleteConfirm = false)}
/>
