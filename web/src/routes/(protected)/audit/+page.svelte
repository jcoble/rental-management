<script lang="ts">
	import { createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { audit } from '$lib/api/endpoints/audit';
	import type { AuditEntry } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { debounced } from '$lib/utils/debounce.svelte';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import RangeDatePicker from '$lib/components/shared/RangeDatePicker.svelte';
	import Pagination from '$lib/components/shared/Pagination.svelte';
	import { overfetchPage } from '$lib/audit/pagination';
	import * as Card from '$lib/components/ui/card';
	import * as Select from '$lib/components/ui/select';
	import { Button } from '$lib/components/ui/button';
	import { RefreshCw, ShieldAlert } from '@lucide/svelte';
	import { currentUserIsPlatformAdmin } from '$lib/stores/auth.svelte';
	import { page } from '$app/state';
	import { readGridParam, syncGridUrl } from '$lib/utils/grid-url-state.svelte';
	import { formatAuditChangeValue, formatStatusLabel } from '$lib/utils/status-labels';
	import { auditChangeLabel, auditEntryTitle } from '$lib/utils/audit-change-label';

	// The forensic view includes IP addresses and raw before/after JSON. Its API is intentionally
	// platform-operator-only, so workspace security administrators must not receive a dead link. The
	// landlord page itself only receives sanitized changes and the portfolio-user-authored reason.
	const showAdvanced = $derived(currentUserIsPlatformAdmin());

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const PAGE_SIZE = 50;
	const REQUEST_SIZE = PAGE_SIZE + 1;

	const OPERATIONS = ['Created', 'Updated', 'Deleted', 'Approved', 'Rejected'] as const;

	const ENTITY_TYPES = [
		'Payment',
		'Expense',
		'LeaseManagement',
		'LeaseAgreement',
		'LeaseAddendum',
		'Tenant',
		'Property',
		'WorkOrder',
		'Vendor',
		'OwnerEntity',
		'Appointment',
		'Inspection',
		'RentalApplication',
		'RecurringTenantCharge',
		'RecurringExpense',
		'CapitalAsset',
		'ApplicantScreening',
		'RentalListing',
		'ListingPublication',
		'ListingPhoto',
		'InspectionItem',
		'VendorDispatch',
		'ScanDraft',
	] as const;

	// Keep the exact forensic position in the URL so opening a target and using browser Back restores
	// the same search, filters, date span, and page. The URL uses a human page number while the API
	// continues to receive its canonical row offset.
	const initialParams = page.url.searchParams;
	let search = $state(readGridParam(initialParams, 'q'));
	let operationFilter = $state(readGridParam(initialParams, 'operation'));
	let entityTypeFilter = $state(readGridParam(initialParams, 'entity'));
	let dateFrom = $state(readGridParam(initialParams, 'from'));
	let dateTo = $state(readGridParam(initialParams, 'to'));
	let skip = $state((readGridParam(initialParams, 'page', 1) - 1) * PAGE_SIZE);

	const debouncedSearch = debounced(() => search, 300);

	// Reset pagination when filters change, but preserve a URL-restored page on the initial mount.
	let filterResetPrimed = false;
	$effect(() => {
		debouncedSearch.value;
		operationFilter;
		entityTypeFilter;
		dateFrom;
		dateTo;
		if (!filterResetPrimed) {
			filterResetPrimed = true;
			return;
		}
		skip = 0;
	});

	$effect(() => {
		syncGridUrl(
			{
				q: search,
				operation: operationFilter,
				entity: entityTypeFilter,
				from: dateFrom,
				to: dateTo,
				page: Math.floor(skip / PAGE_SIZE) + 1,
			},
			{ page: 1 },
		);
	});

	const auditQuery = createQuery(() => ({
		queryKey: ['audit', portfolioId, debouncedSearch.value, operationFilter, entityTypeFilter, dateFrom, dateTo, skip],
		queryFn: () =>
			audit.list(portfolioId, {
				search: debouncedSearch.value || undefined,
				operation: operationFilter || undefined,
				entityType: entityTypeFilter || undefined,
				from: dateFrom || undefined,
				to: dateTo || undefined,
				skip,
				take: REQUEST_SIZE,
				sort: '-timestamp',
			}),
	}));

	function refresh() {
		queryClient.invalidateQueries({ queryKey: ['audit', portfolioId] });
	}

	const pageWindow = $derived(overfetchPage(auditQuery.data ?? [], PAGE_SIZE));
	const entries = $derived(pageWindow.items);

	// --- Display helpers ---

	/** Format timestamp as "May 30, 2026 at 3:42 PM" */
	function formatAbsolute(iso: string): string {
		return new Date(iso).toLocaleString('en-US', {
			month: 'short',
			day: 'numeric',
			year: 'numeric',
			hour: 'numeric',
			minute: '2-digit',
		});
	}

	/** Relative time: "2 minutes ago", "3 days ago", etc. */
	function formatRelative(iso: string): string {
		const diffMs = Date.now() - new Date(iso).getTime();
		const diffSec = Math.floor(diffMs / 1000);
		if (diffSec < 60) return 'just now';
		const diffMin = Math.floor(diffSec / 60);
		if (diffMin < 60) return `${diffMin}m ago`;
		const diffHr = Math.floor(diffMin / 60);
		if (diffHr < 24) return `${diffHr}h ago`;
		const diffDay = Math.floor(diffHr / 24);
		if (diffDay < 30) return `${diffDay}d ago`;
		return formatAbsolute(iso);
	}

	/** Dot color by entity type */
	function dotColor(entry: AuditEntry): string {
		switch (entry.entityType?.toLowerCase()) {
			case 'lease':
				return 'bg-[var(--info)]';
			case 'payment':
				return 'bg-[var(--success)]';
			case 'expense':
				return 'bg-[var(--warning)]';
			case 'workorder':
				return 'bg-[var(--warning)]';
			case 'tenant':
				return 'bg-[var(--m3c-primary)]';
			case 'property':
				return 'bg-[var(--m3c-outline)]';
			case 'appointment':
			case 'inspection':
				return 'bg-[var(--accent-cyan)]';
			default:
				return 'bg-muted-foreground';
		}
	}

	/** Human-readable label for entity type + id */
	const entityTypeLabels: Record<string, string> = {
		Tenant: 'Tenant',
		TenantAccount: 'Tenant account',
		TenantLedgerEntry: 'Tenant account entry',
		TenantPaymentAttempt: 'Payment attempt',
		TenantAccountConditionPeriod: 'Tenant account condition',
		TenantAutopayEnrollment: 'Autopay enrollment',
		SecurityDepositAccount: 'Security deposit account',
		SecurityDepositEntry: 'Security-deposit movement',
		LeaseAgreement: 'Lease agreement',
		LeaseAgreementSigner: 'Agreement signer',
		LeaseAddendum: 'Lease addendum',
		LeaseAddendumSigner: 'Addendum signer',
		LeaseAddendumFinancialEffect: 'Addendum financial effect',
		LeaseRenewalAddendumDecision: 'Addendum renewal decision',
		StoredFile: 'Document',
		LegalDocumentArtifact: 'Legal document',
		SignatureRequest: 'Signature request',
		SignatureSigner: 'Signature signer',
		NoticeDraft: 'Tenant notice draft',
		RenderedNotice: 'Rendered tenant notice',
		NoticeDeliveryEvidence: 'Notice delivery evidence',
		TenantNoticeWorkItem: 'Tenant notice work item',
		Conversation: 'Conversation',
		ConversationMessage: 'Conversation message',
		Notification: 'Notification',
		LeaseManagementParty: 'Tenant relationship party',
		TenantUserAccess: 'Tenant portal access',
		UnitOperationalPeriod: 'Unit operational period',
		EvictionCase: 'Eviction case',
		EvictionCaseEvent: 'Eviction case event',
		WorkOrderStatusEvent: 'Work-order activity',
		RecurringTenantCharge: 'Recurring tenant charge',
		RecurringExpense: 'Recurring expense',
		CapitalAsset: 'Capital asset',
		ApplicantScreening: 'Applicant screening',
		RentalListing: 'Rental listing',
		ListingPublication: 'Listing publication',
		ListingPhoto: 'Listing photo',
		InspectionItem: 'Inspection checklist item',
		VendorDispatch: 'Vendor dispatch',
		ScanDraft: 'Scan draft',
	};

	function entityLabel(entry: AuditEntry): string {
		if (!entry.entityType) return '';
		const label = entityTypeLabels[entry.entityType] ?? formatStatusLabel(entry.entityType);
		return entry.entityId ? `${label} #${entry.entityId}` : label;
	}

	function entityTypeFilterLabel(value: string): string {
		return value ? (entityTypeLabels[value] ?? formatStatusLabel(value)) : 'All records';
	}
</script>

<svelte:head>
	<title>Activity history - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="audit-page">
	<div class="mb-4 flex items-center justify-between">
		<div>
			<h1 class="text-2xl font-bold">Activity history</h1>
			<p class="text-sm text-muted-foreground">A record of every change across your portfolio.</p>
		</div>
		<div class="flex items-center gap-2">
			{#if showAdvanced}
				<Button
					variant="outline"
					size="sm"
					href="/admin/audit"
					data-testid="audit-advanced"
					aria-label="Open the advanced forensic view"
				>
					<ShieldAlert class="h-3.5 w-3.5" />
					Advanced
				</Button>
			{/if}
			<Button
				variant="outline"
				size="sm"
				onclick={refresh}
				disabled={auditQuery.isFetching}
				data-testid="audit-refresh"
				aria-label="Refresh activity history"
			>
				<RefreshCw class="h-3.5 w-3.5 {auditQuery.isFetching ? 'animate-spin' : ''}" />
				Refresh
			</Button>
		</div>
	</div>

	<Card.Root class="gap-0 py-0">
		<!-- Filters bar -->
		<div class="flex flex-wrap items-center gap-2 border-b border-border px-3 py-2">
			<div class="min-w-[180px] flex-1">
				<SearchInput bind:value={search} placeholder="Search audit…" testid="audit-search" />
			</div>
			<Select.Root type="single" bind:value={operationFilter}>
				<Select.Trigger class="w-44" data-testid="audit-operation-filter">
					{operationFilter || 'All actions'}
				</Select.Trigger>
				<Select.Content>
					<Select.Item value="" label="All actions">All actions</Select.Item>
					{#each OPERATIONS as op}
						<Select.Item value={op} label={op}>{op}</Select.Item>
					{/each}
				</Select.Content>
			</Select.Root>
			<Select.Root type="single" bind:value={entityTypeFilter}>
				<Select.Trigger class="w-40" data-testid="audit-entity-type-filter">
					{entityTypeFilterLabel(entityTypeFilter)}
				</Select.Trigger>
				<Select.Content>
					<Select.Item value="" label="All records">All records</Select.Item>
					{#each ENTITY_TYPES as e}
						<Select.Item value={e} label={entityTypeLabels[e] ?? formatStatusLabel(e)}>{entityTypeLabels[e] ?? formatStatusLabel(e)}</Select.Item>
					{/each}
				</Select.Content>
			</Select.Root>
			<RangeDatePicker
				bind:start={dateFrom}
				bind:end={dateTo}
				presets
				placeholder="All dates"
				align="end"
				testid="audit-date-range"
			/>
		</div>

		<!-- Entry list -->
		<Card.Content class="p-0" data-testid="audit-list">
			{#if auditQuery.isLoading}
				<p class="py-10 text-center text-sm text-muted-foreground" data-testid="audit-loading">
					Loading…
				</p>
			{:else if auditQuery.isError}
				<p class="py-10 text-center text-sm text-destructive" data-testid="audit-error">
					Failed to load audit trail. Try refreshing.
				</p>
			{:else if entries.length === 0}
				<p class="py-10 text-center text-sm text-muted-foreground" data-testid="audit-empty">
					No audit entries found.
				</p>
			{:else}
				<ul class="divide-y divide-border" role="list">
					{#each entries as entry (entry.id)}
						{@const Tag = entry.detailHref ? 'a' : 'div'}
						{@const visibleChanges = (entry.changes ?? [])
							.map((change) => ({ change, label: auditChangeLabel(change.field) }))
							.filter((row) => row.label !== null)}
						<li>
							<svelte:element
								this={Tag}
								href={entry.detailHref}
								class="flex items-start gap-3 px-4 py-3 hover:bg-muted/40 {entry.detailHref ? 'cursor-pointer' : ''}"
								data-testid={entry.testId ?? `audit-${entry.id}`}
							>
								<!-- Colored dot -->
								<span
									class="mt-1.5 h-2.5 w-2.5 shrink-0 rounded-full {dotColor(entry)}"
									aria-hidden="true"
								></span>

								<!-- Main content -->
								<div class="min-w-0 flex-1">
									<p class="text-sm font-medium leading-snug">{auditEntryTitle(entry.description)}</p>
									<div class="mt-0.5 flex flex-wrap items-center gap-x-2 gap-y-0.5 text-xs text-muted-foreground">
										<span>{entry.actor}</span>
										<span aria-hidden="true">·</span>
										{#if entry.entityType}
											<span class="rounded bg-muted px-1 py-0.5 font-mono text-[11px]">{entityLabel(entry)}</span>
											<span aria-hidden="true">·</span>
										{/if}
										<span class="font-medium text-foreground/60">{entry.operationName}</span>
									</div>
								</div>

								<!-- Timestamp -->
								<time
									datetime={entry.timestamp}
									title={formatAbsolute(entry.timestamp)}
									class="shrink-0 text-xs text-muted-foreground"
								>
									{formatRelative(entry.timestamp)}
								</time>
							</svelte:element>
							{#if visibleChanges.length || entry.changeReason}
								<div
									class="ml-8 border-l border-border px-3 pb-2 text-xs text-muted-foreground"
									data-testid="audit-changes-{entry.id}"
								>
									{#if visibleChanges.length}
										{#each visibleChanges as { change, label } (change.field)}
											<p>
												<span class="font-medium text-foreground">{label}</span>
												{#if entry.operationName === 'Created'}
													set to {formatAuditChangeValue(change.field, change.newValue)}
												{:else if entry.operationName === 'Deleted'}
													was {formatAuditChangeValue(change.field, change.oldValue)}
												{:else}
													changed from {formatAuditChangeValue(change.field, change.oldValue)} to
													{formatAuditChangeValue(change.field, change.newValue)}
												{/if}
											</p>
										{/each}
									{/if}
									{#if entry.changeReason}
										<p class="mt-2 border-t border-border pt-2">
											<span class="font-medium text-foreground">Reason:</span> {entry.changeReason}
										</p>
									{/if}
								</div>
							{/if}
						</li>
					{/each}
				</ul>
			{/if}
		</Card.Content>

		<!-- Pagination -->
		<Card.Footer class="border-t border-border px-3 py-2">
			<Pagination
				bind:skip
				take={PAGE_SIZE}
				count={entries.length}
				hasNext={pageWindow.hasNext}
				testid="audit-pagination"
			/>
		</Card.Footer>
	</Card.Root>
</div>
