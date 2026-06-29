<script lang="ts">
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { tenants } from '$lib/api/endpoints/tenants';
	import { leases } from '$lib/api/endpoints/leases';
	import { notices } from '$lib/api/endpoints/notices';
	import type { Lease, NoticeDraft, Tenant } from '$lib/types';
	import { recordHref } from '$lib/navigation/record-href';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { tenantSchema, parseForm } from '$lib/schemas';
	import { formatDateOnly } from '$lib/utils/date';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { getTenantNoticeEmptyCopy, getTenantNoticeEmptyState } from '$lib/tenants/tenant-notice-state';
	import { getTenantDeleteState } from '$lib/tenants/tenant-delete-state';
	import {
		clearTenantNoticeActionUrl,
		readTenantNoticeAction,
		tenantNoticeActionKey,
	} from '$lib/tenants/tenant-notice-action';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import PageBreadcrumb from '$lib/components/shared/PageBreadcrumb.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import InlineField from '$lib/components/shared/InlineField.svelte';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import { Button } from '$lib/components/ui/button';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Checkbox } from '$lib/components/ui/checkbox';
	import { Mail, Phone, AlertCircle, Pencil, Save, Trash2, User, X, Contact, FileClock, BellRing, Send, KeyRound } from '@lucide/svelte';
	import DocumentsPanel from '$lib/components/shared/DocumentsPanel.svelte';
	import RecordHistory from '$lib/components/shared/RecordHistory.svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const id = $derived(Number(page.params.id));

	const tenantQuery = createQuery(() => ({
		queryKey: ['tenant', id],
		queryFn: () => tenants.get(id),
		enabled: !isNaN(id) && id > 0,
	}));

	const leasesQuery = createQuery(() => ({
		queryKey: ['leases', portfolioId, { tenantId: id }],
		queryFn: () => leases.list(portfolioId, { tenantId: id, take: 100 }),
		enabled: !isNaN(id) && id > 0 && portfolioId > 0,
	}));

	const tenant = $derived(tenantQuery.data);
	const tenantLeases = $derived(leasesQuery.data ?? []);
	const activeTenantLeaseCount = $derived(tenant?.activeLeaseCount ?? 0);
	const fullName = $derived(
		tenant ? (tenant.fullName ?? `${tenant.firstName} ${tenant.lastName}`) : ''
	);
	const deleteState = $derived(tenant ? getTenantDeleteState(tenant) : null);

	// ── Inline edit ────────────────────────────────────────────────────────────
	const empty = { firstName: '', lastName: '', email: '', phone: '', emergencyContact: '' };
	let editing = $state(false);
	let form = $state({ ...empty });
	let formErrors = $state<Record<string, string>>({});
	let showDeleteConfirm = $state(false);

	function startEditing() {
		if (!tenant) return;
		form = {
			firstName: tenant.firstName,
			lastName: tenant.lastName,
			email: tenant.email ?? '',
			phone: tenant.phone ?? '',
			emergencyContact: tenant.emergencyContact ?? '',
		};
		formErrors = {};
		editing = true;
	}
	function cancelEditing() {
		editing = false;
		formErrors = {};
	}

	function submit() {
		const result = parseForm(tenantSchema, form);
		if (result.errors) {
			formErrors = result.errors;
			return;
		}
		formErrors = {};
		saveMutation.mutate({ id, data: { portfolioId, ...result.data } });
	}

	const saveMutation = createMutation(() => ({
		mutationFn: ({ id: tid, data }: { id: number; data: Record<string, unknown> }) =>
			tenants.update(tid, data),
		onSuccess: () => {
			showSuccess('Tenant updated.');
			editing = false;
			formErrors = {};
			queryClient.invalidateQueries({ queryKey: ['tenant', id] });
			queryClient.invalidateQueries({ queryKey: ['tenants', portfolioId] });
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const deleteMutation = createMutation(() => ({
		mutationFn: (tid: number) => tenants.delete(tid),
		onSuccess: () => {
			showSuccess('Tenant deleted.');
			queryClient.invalidateQueries({ queryKey: ['tenants', portfolioId] });
			goto('/tenants');
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	// ── Grant portal access ──────────────────────────────────────────────────────
	// Provision (or confirm) a tenant portal login on demand. The tenant signs in with their email +
	// the shared tenant password; only available once the tenant has an email on file.
	const grantPortalAccessMutation = createMutation(() => ({
		mutationFn: (tid: number) => tenants.grantPortalAccess(tid),
		onSuccess: (result) => {
			const who = fullName || 'This tenant';
			showSuccess(
				result.alreadyExisted
					? `Portal access already active — ${who} can sign in with their email.`
					: `Portal access granted — ${who} can sign in with their email.`
			);
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	// ── Per-tenant notice: Create / Send ─────────────────────────────────────────
	// Generate the notice draft(s) due for THIS tenant (POST /notices/generate { tenantId }, owned by a
	// sibling lane), review them in a dialog, then send each on the chosen channels (reusing the notice
	// approve endpoint) or dismiss it. Mirrors the portfolio-wide flow on /notices, scoped to one tenant.
	let showNoticeDialog = $state(false);
	let noticeDrafts = $state<NoticeDraft[]>([]);
	let forcedNoticeLabel = $state<string | null>(null);
	let handledNoticeActionKey: string | null = null;
	// Per-draft channel selection (portal / email / sms), defaulting to all on.
	let noticeChannels = $state<Record<number, { portal: boolean; email: boolean; sms: boolean }>>({});
	let noticeEdits = $state<Record<number, { subject: string; body: string }>>({});
	const noticeEmptyState = $derived(
		getTenantNoticeEmptyState({
			forcedNoticeLabel,
			activeLeaseCount: activeTenantLeaseCount,
			tenantId: id,
		})
	);

	function noticeTypeLabel(type: string) {
		switch (type) {
			case 'RentReminder': return 'Rent reminder';
			case 'RenewalOffer': return 'Renewal offer';
			case 'MonthToMonthConversion': return 'Month-to-month conversion';
			case 'MoveOutReminder': return 'Move-out reminder';
			case 'LateRentNotice': return 'Late rent / late fee';
			default: return type;
		}
	}

	function channelsFor(id: number) {
		return noticeChannels[id] ?? { portal: true, email: true, sms: true };
	}
	function setNoticeChannel(id: number, key: 'portal' | 'email' | 'sms', checked: boolean) {
		noticeChannels = { ...noticeChannels, [id]: { ...channelsFor(id), [key]: checked } };
	}
	function selectedChannelNames(id: number): string[] {
		const c = channelsFor(id);
		return [c.portal ? 'Portal' : '', c.email ? 'Email' : '', c.sms ? 'Sms' : ''].filter(Boolean);
	}
	function noticeEditFor(draft: NoticeDraft) {
		return noticeEdits[draft.id] ?? { subject: draft.subject, body: draft.body };
	}
	function setNoticeEdit(id: number, field: 'subject' | 'body', value: string) {
		noticeEdits = {
			...noticeEdits,
			[id]: { ...(noticeEdits[id] ?? { subject: '', body: '' }), [field]: value },
		};
	}
	function editedNoticePayload(draft: NoticeDraft) {
		const edit = noticeEditFor(draft);
		return {
			subject: edit.subject.trim(),
			body: edit.body.trim(),
		};
	}
	function hasEditedNoticeContent(draft: NoticeDraft) {
		const edit = editedNoticePayload(draft);
		return edit.subject.length > 0 && edit.body.length > 0;
	}
	function removeNoticeEdit(id: number) {
		const { [id]: _removed, ...next } = noticeEdits;
		noticeEdits = next;
	}

	// Notice types a landlord can FORCE for this tenant (generated even outside the usual trigger
	// window). Mirrors the mobile type-first picker; all five notice types are selectable so the
	// landlord can send any notice on demand (the API forces the type regardless of trigger windows).
	const FORCEABLE_NOTICE_TYPES: { type: string; label: string }[] = [
		{ type: 'RentReminder', label: 'Rent reminder (coming due)' },
		{ type: 'RenewalOffer', label: 'Lease renewal offer' },
		{ type: 'MonthToMonthConversion', label: 'Convert to month-to-month' },
		{ type: 'MoveOutReminder', label: 'Lease expiration / move-out' },
		{ type: 'LateRentNotice', label: 'Late rent / late fee' },
	];

	function openNoticeDialog(noticeType?: string) {
		noticeDrafts = [];
		noticeChannels = {};
		noticeEdits = {};
		forcedNoticeLabel = null;
		showNoticeDialog = true;
		// Default pass: generate whatever is actually due for this tenant.
		generateNoticeMutation.mutate(noticeType);
	}

	const generateNoticeMutation = createMutation(() => ({
		// noticeType omitted → generate all due; supplied → force that one type (early renewal / move-out).
		mutationFn: (noticeType?: string) => notices.generate(id, noticeType),
		onSuccess: (result, noticeType) => {
			const selectedForcedNoticeLabel = noticeType
				? FORCEABLE_NOTICE_TYPES.find((nt) => nt.type === noticeType)?.label ?? noticeTypeLabel(noticeType)
				: null;
			forcedNoticeLabel = selectedForcedNoticeLabel;
			noticeDrafts = result.drafts ?? [];
			const seeded: Record<number, { portal: boolean; email: boolean; sms: boolean }> = {};
			const seededEdits: Record<number, { subject: string; body: string }> = {};
			for (const d of noticeDrafts) {
				seeded[d.id] = { portal: true, email: true, sms: true };
				seededEdits[d.id] = { subject: d.subject, body: d.body };
			}
			noticeChannels = seeded;
			noticeEdits = seededEdits;
			queryClient.invalidateQueries({ queryKey: ['notice-drafts', portfolioId] });
			if (noticeDrafts.length === 0) {
				showSuccess(
					getTenantNoticeEmptyState({
						forcedNoticeLabel: selectedForcedNoticeLabel,
						activeLeaseCount: activeTenantLeaseCount,
						tenantId: id,
					}).message
				);
			}
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const sendNoticeMutation = createMutation(() => ({
		mutationFn: async (draft: NoticeDraft) => {
			const payload = editedNoticePayload(draft);
			const changed =
				payload.subject !== draft.subject.trim() || payload.body !== draft.body.trim();
			if (changed) await notices.update(draft.id, payload);
			return notices.approve(draft.id, { channels: selectedChannelNames(draft.id) });
		},
		onSuccess: (_r, draft) => {
			showSuccess('Notice sent.');
			noticeDrafts = noticeDrafts.filter((d) => d.id !== draft.id);
			removeNoticeEdit(draft.id);
			queryClient.invalidateQueries({ queryKey: ['notice-drafts', portfolioId] });
			if (noticeDrafts.length === 0) showNoticeDialog = false;
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const dismissNoticeMutation = createMutation(() => ({
		mutationFn: (draft: NoticeDraft) => notices.dismiss(draft.id),
		onSuccess: (_r, draft) => {
			showSuccess('Draft dismissed.');
			noticeDrafts = noticeDrafts.filter((d) => d.id !== draft.id);
			removeNoticeEdit(draft.id);
			queryClient.invalidateQueries({ queryKey: ['notice-drafts', portfolioId] });
			if (noticeDrafts.length === 0) showNoticeDialog = false;
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	$effect(() => {
		const action = readTenantNoticeAction(page.url.searchParams);
		if (!action) {
			handledNoticeActionKey = null;
			return;
		}
		if (!tenant) return;
		const actionKey = tenantNoticeActionKey(id, action);
		if (handledNoticeActionKey === actionKey) return;
		handledNoticeActionKey = actionKey;
		openNoticeDialog(action.noticeType);
		void goto(clearTenantNoticeActionUrl(page.url), {
			replaceState: true,
			noScroll: true,
			keepFocus: true,
		});
	});

	// ── Lease columns ──────────────────────────────────────────────────────────
	const leaseColumns: ColumnDef<Lease>[] = [
		{
			key: 'leaseNumber',
			title: 'Lease #',
			sortable: true,
			mobileRole: 'title',
		},
		{
			key: 'unitNumber',
			title: 'Unit',
			sortable: true,
			mobileRole: 'subtitle',
			accessor: (l) => l.unitNumber ?? '–',
		},
		{
			key: 'monthlyRent',
			title: 'Rent',
			format: 'currency',
			sortable: true,
			mobileRole: 'metric',
		},
		{
			key: 'startDate',
			title: 'Start',
			format: 'date',
			sortable: true,
			mobileRole: 'meta',
		},
		{
			key: 'endDate',
			title: 'End',
			format: 'date',
			sortable: true,
			mobileRole: 'meta',
		},
		{
			key: 'status',
			title: 'Status',
			mobileRole: 'badge',
			cell: leaseStatusCell,
		},
	];
</script>

{#snippet leaseStatusCell(lease: Lease)}
	<StatusBadge status={lease.status} />
{/snippet}

<svelte:head>
	<title>{fullName ? `${fullName} - Tenant` : 'Tenant'} - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="tenant-detail-page">
	<!-- Breadcrumb -->
	<div class="mb-4">
		<PageBreadcrumb
			crumbs={[
				{ label: 'Tenants', href: '/tenants' },
				{ label: fullName || '…' },
			]}
		/>
	</div>

	<!-- Loading state -->
	{#if tenantQuery.isLoading}
		<div class="flex items-center justify-center py-20 text-muted-foreground" data-testid="tenant-detail-loading">
			<div class="flex flex-col items-center gap-2">
				<div class="h-6 w-6 animate-spin rounded-full border-2 border-current border-t-transparent"></div>
				<span class="text-sm">Loading…</span>
			</div>
		</div>

	<!-- Error state -->
	{:else if tenantQuery.isError}
		<div class="rounded-lg border border-destructive/30 bg-destructive/10 p-6 text-center" data-testid="tenant-detail-error">
			<AlertCircle class="mx-auto mb-2 h-8 w-8 text-destructive" />
			<p class="font-medium text-destructive">Could not load tenant</p>
			<p class="mt-1 text-sm text-muted-foreground">{apiErrorMessage(tenantQuery.error)}</p>
			<Button variant="outline" class="mt-4" onclick={() => tenantQuery.refetch()}>Retry</Button>
		</div>

	<!-- Not found -->
	{:else if !tenant}
		<div class="rounded-lg border border-border p-6 text-center" data-testid="tenant-detail-not-found">
			<User class="mx-auto mb-2 h-8 w-8 text-muted-foreground" />
			<p class="font-medium">Tenant not found</p>
			<Button variant="outline" class="mt-4" onclick={() => goto('/tenants')}>Back to Tenants</Button>
		</div>

	{:else}
		<!-- Header -->
		<div class="mb-6 flex flex-wrap items-start justify-between gap-3">
			<div>
				<h1 class="text-2xl font-bold" data-testid="tenant-detail-name">{fullName}</h1>
				<div class="mt-1 flex flex-wrap items-center gap-3 text-sm text-muted-foreground">
					{#if tenant.email}
						<span class="flex items-center gap-1">
							<Mail class="h-3.5 w-3.5" />
							{tenant.email}
						</span>
					{/if}
					{#if tenant.phone}
						<span class="flex items-center gap-1">
							<Phone class="h-3.5 w-3.5" />
							{tenant.phone}
						</span>
					{/if}
				</div>
			</div>
			<div class="flex items-center gap-2">
				{#if editing}
					<Button variant="outline" class="gap-2" onclick={cancelEditing} disabled={saveMutation.isPending} data-testid="tenant-detail-cancel">
						<X class="h-4 w-4" />
						Cancel
					</Button>
					<Button class="gap-2" onclick={submit} disabled={saveMutation.isPending} data-testid="tenant-detail-save">
						<Save class="h-4 w-4" />
						{saveMutation.isPending ? 'Saving…' : 'Save'}
					</Button>
				{:else}
					<Button variant="outline" class="gap-2" onclick={() => openNoticeDialog()} data-testid="tenant-detail-create-notice">
						<BellRing class="h-4 w-4" />
						Create / Send notice
					</Button>
					<Button
						variant="outline"
						class="gap-2"
						onclick={() => grantPortalAccessMutation.mutate(tenant.id)}
						disabled={!tenant.email || grantPortalAccessMutation.isPending}
						title={tenant.email
							? 'Create a portal login for this tenant.'
							: 'Add an email to this tenant before granting portal access.'}
						data-testid="tenant-detail-grant-portal-access"
					>
						<KeyRound class="h-4 w-4" />
						{grantPortalAccessMutation.isPending ? 'Granting…' : 'Grant portal access'}
					</Button>
					<Button variant="outline" class="gap-2" onclick={startEditing} data-testid="tenant-detail-edit">
						<Pencil class="h-4 w-4" />
						Edit
					</Button>
					<Button
						variant="destructive"
						class="gap-2"
						onclick={() => (showDeleteConfirm = true)}
						data-testid="tenant-detail-delete"
					>
						<Trash2 class="h-4 w-4" />
						Delete
					</Button>
				{/if}
			</div>
		</div>

		<!-- Grouped detail cards -->
		<div class="mb-6 grid gap-6 lg:grid-cols-2">
			<DetailCard title="Contact" icon={Contact} accent="primary" testid="tenant-detail-card" contentClass="grid gap-x-6 gap-y-4 sm:grid-cols-2">
				<InlineField label="First Name" bind:value={form.firstName} display={tenant.firstName} {editing} error={formErrors.firstName} testid="tenant-detail-first-name" />
				<InlineField label="Last Name" bind:value={form.lastName} display={tenant.lastName} {editing} error={formErrors.lastName} testid="tenant-detail-last-name" />
				<InlineField label="Email" bind:value={form.email} display={tenant.email} {editing} type="email" error={formErrors.email} testid="tenant-detail-email" />
				<InlineField label="Phone" bind:value={form.phone} display={tenant.phone} {editing} type="tel" error={formErrors.phone} testid="tenant-detail-phone" />
				<InlineField label="Emergency Contact" bind:value={form.emergencyContact} display={tenant.emergencyContact} {editing} error={formErrors.emergencyContact} testid="tenant-detail-emergency" class="sm:col-span-2" />
			</DetailCard>

			<DetailCard title="Record" icon={FileClock} accent="muted" testid="tenant-detail-record" contentClass="grid gap-x-6 gap-y-4 sm:grid-cols-2">
				{#if tenant.dateOfBirth}
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Date of Birth</dt>
						<dd class="mt-1 text-sm text-foreground">
							{formatDateOnly(tenant.dateOfBirth)}
						</dd>
					</div>
				{/if}
				<div>
					<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Active Leases</dt>
					<dd class="mt-1 text-sm font-semibold tabular-nums text-foreground">
						{tenant.activeLeaseCount ?? 0}
					</dd>
				</div>
				<div>
					<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Created</dt>
					<dd class="mt-1 text-sm text-foreground">
						{new Date(tenant.createdAt).toLocaleDateString()}
					</dd>
				</div>
				{#if tenant.notes}
					<div class="sm:col-span-2">
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Notes</dt>
						<dd class="mt-1 text-sm text-foreground">{tenant.notes}</dd>
					</div>
				{/if}
			</DetailCard>
		</div>

		<!-- Leases section -->
		<div data-testid="tenant-detail-leases">
			<h2 class="mb-3 text-lg font-semibold">Leases</h2>
			<DataGrid
				data={tenantLeases}
				columns={leaseColumns}
				loading={leasesQuery.isLoading}
				emptyMessage="No leases found for this tenant."
				onRowClick={(lease) => goto(recordHref('lease', lease))}
				getRowKey={(l) => l.id}
				pageSize={10}
				data-testid="tenant-leases-grid"
			/>
		</div>

		<!-- Documents section -->
		<div class="mt-6" data-testid="tenant-detail-documents">
			<DocumentsPanel entityType="Tenant" entityId={id} />
		</div>

		<!-- Per-record audit history -->
		<div class="mt-6 rounded-lg border border-border bg-card p-4" data-testid="tenant-history-section">
			<h2 class="mb-1 text-base font-semibold">History</h2>
			<p class="mb-3 text-sm text-muted-foreground">Every recorded change to this tenant — who, what, and when.</p>
			<RecordHistory entityType="Tenant" entityId={id} />
		</div>
	{/if}
</div>

<ConfirmDialog
	open={showDeleteConfirm}
	title="Delete tenant"
	message={deleteState?.message ?? ''}
	busy={deleteMutation.isPending}
	confirmDisabled={deleteState?.confirmDisabled ?? false}
	testid="tenant-detail-delete-confirm"
	onconfirm={() => {
		if (!tenant || deleteState?.confirmDisabled) return;
		deleteMutation.mutate(tenant.id);
	}}
	oncancel={() => (showDeleteConfirm = false)}
/>

<!-- Per-tenant notice: generate the due draft(s) for this tenant, review, then send or dismiss each. -->
<Dialog.Root open={showNoticeDialog} onOpenChange={(v) => { if (!v) showNoticeDialog = false; }}>
	<Dialog.Content class="max-h-[85vh] max-w-2xl overflow-y-auto" data-testid="tenant-notice-dialog">
		<Dialog.Header>
			<Dialog.Title>Create / Send notice</Dialog.Title>
			<Dialog.Description>
				Notices due for {fullName || 'this tenant'} — review, choose how to deliver each, then send.
				Nothing goes out until you press Send.
			</Dialog.Description>
		</Dialog.Header>

		{#if generateNoticeMutation.isPending}
			<div class="flex items-center justify-center gap-2 py-10 text-muted-foreground" data-testid="tenant-notice-loading">
				<div class="h-5 w-5 animate-spin rounded-full border-2 border-current border-t-transparent"></div>
				<span class="text-sm">Generating notice drafts…</span>
			</div>
		{:else if generateNoticeMutation.isError}
			<div class="rounded-lg border border-destructive/30 bg-destructive/10 p-4 text-center" data-testid="tenant-notice-error">
				<AlertCircle class="mx-auto mb-2 h-6 w-6 text-destructive" />
				<p class="text-sm text-destructive">{apiErrorMessage(generateNoticeMutation.error)}</p>
				<Button variant="outline" class="mt-3" onclick={() => generateNoticeMutation.mutate(undefined)} data-testid="tenant-notice-retry">
					Try again
				</Button>
			</div>
		{:else if noticeDrafts.length === 0}
			<div class="rounded-lg border border-border p-6 text-center" data-testid="tenant-notice-empty">
				<BellRing class="mx-auto mb-2 h-6 w-6 text-muted-foreground" />
				<p class="text-sm font-medium">{noticeEmptyState.message}</p>
				<p class="mt-1 text-xs text-muted-foreground">
					{noticeEmptyState.description}
				</p>
				<!-- Force a specific notice even outside the trigger window (e.g. an early renewal offer). -->
				{#if noticeEmptyState.showForceControls}
					<p class="mt-4 text-xs font-medium uppercase tracking-wide text-muted-foreground">Create one anyway</p>
					<div class="mt-2 flex flex-wrap items-center justify-center gap-2">
						{#each FORCEABLE_NOTICE_TYPES as nt (nt.type)}
							<Button
								variant="outline"
								size="sm"
								disabled={generateNoticeMutation.isPending}
								onclick={() => generateNoticeMutation.mutate(nt.type)}
								data-testid="tenant-notice-force-{nt.type}"
							>
								{nt.label}
							</Button>
						{/each}
					</div>
				{:else if noticeEmptyState.leaseActionHref}
					<Button
						variant="outline"
						size="sm"
						class="mt-4"
						href={noticeEmptyState.leaseActionHref}
						data-testid="tenant-notice-create-lease"
					>
						{noticeEmptyState.leaseActionLabel}
					</Button>
				{/if}
			</div>
		{:else}
			<div class="space-y-4" data-testid="tenant-notice-drafts">
				{#each noticeDrafts as draft (draft.id)}
					{@const channels = channelsFor(draft.id)}
					{@const busy = sendNoticeMutation.isPending || dismissNoticeMutation.isPending}
					{@const canSend = channels.portal || channels.email || channels.sms}
					{@const edit = noticeEditFor(draft)}
					<div class="rounded-lg border border-border bg-card p-4" data-testid="tenant-notice-draft-{draft.id}">
						<div class="mb-2 flex items-center justify-between gap-2">
							<h3 class="text-sm font-semibold" data-testid="tenant-notice-draft-type">
								{noticeTypeLabel(draft.noticeType)}
							</h3>
							<StatusBadge status={draft.status} />
						</div>
						<div class="space-y-3">
							<label class="block">
								<span class="mb-1 block text-xs font-medium uppercase tracking-wide text-muted-foreground">Subject</span>
								<input
									class="h-10 w-full rounded-md border border-input bg-background px-3 text-sm"
									value={edit.subject}
									oninput={(event) => setNoticeEdit(draft.id, 'subject', (event.currentTarget as HTMLInputElement).value)}
									data-testid="tenant-notice-edit-subject-{draft.id}"
								/>
							</label>
							<label class="block">
								<span class="mb-1 block text-xs font-medium uppercase tracking-wide text-muted-foreground">Message</span>
								<textarea
									class="min-h-32 w-full rounded-md border border-input bg-background p-3 text-sm leading-6"
									value={edit.body}
									oninput={(event) => setNoticeEdit(draft.id, 'body', (event.currentTarget as HTMLTextAreaElement).value)}
									data-testid="tenant-notice-edit-body-{draft.id}"
								></textarea>
							</label>
						</div>

						<div class="mt-3 flex flex-wrap items-center gap-4">
							<span class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Send via</span>
							<label class="flex items-center gap-2 text-sm" data-testid="tenant-notice-channel-portal-{draft.id}">
								<Checkbox checked={channels.portal} onCheckedChange={(v) => setNoticeChannel(draft.id, 'portal', v === true)} />
								Portal
							</label>
							<label class="flex items-center gap-2 text-sm" data-testid="tenant-notice-channel-email-{draft.id}">
								<Checkbox checked={channels.email} onCheckedChange={(v) => setNoticeChannel(draft.id, 'email', v === true)} />
								Email
							</label>
							<label class="flex items-center gap-2 text-sm" data-testid="tenant-notice-channel-sms-{draft.id}">
								<Checkbox checked={channels.sms} onCheckedChange={(v) => setNoticeChannel(draft.id, 'sms', v === true)} />
								SMS
							</label>
						</div>

						<div class="mt-4 flex items-center justify-end gap-2">
							<Button variant="ghost" size="sm" disabled={busy} onclick={() => dismissNoticeMutation.mutate(draft)} data-testid="tenant-notice-dismiss-{draft.id}">
								<Trash2 class="h-4 w-4" />
								Dismiss
							</Button>
							<Button size="sm" class="gap-2" disabled={busy || !canSend || !hasEditedNoticeContent(draft)} onclick={() => sendNoticeMutation.mutate(draft)} data-testid="tenant-notice-send-{draft.id}">
								<Send class="h-4 w-4" />
								Send
							</Button>
						</div>
					</div>
				{/each}
			</div>
		{/if}

		<Dialog.Footer>
			<Button variant="outline" onclick={() => (showNoticeDialog = false)} data-testid="tenant-notice-close">
				Close
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
