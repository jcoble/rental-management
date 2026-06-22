<script lang="ts">
	import { createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { audit } from '$lib/api/endpoints/audit';
	import type { AdminAuditEntry } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { debounced } from '$lib/utils/debounce.svelte';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import Pagination from '$lib/components/shared/Pagination.svelte';
	import { overfetchPage } from '$lib/audit/pagination';
	import * as Card from '$lib/components/ui/card';
	import * as Select from '$lib/components/ui/select';
	import { Button } from '$lib/components/ui/button';
	import { RefreshCw, ShieldAlert, Download } from '@lucide/svelte';
	import { showError, showSuccess, apiErrorMessage } from '$lib/utils/toast';

	// The (admin) route group already gates this page behind the Admin role server-side. This is the
	// forensic view: every landlord-facing audit row, plus the IP address + raw old→new JSON withheld
	// from /audit.
	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const PAGE_SIZE = 50;
	const REQUEST_SIZE = PAGE_SIZE + 1;

	const OPERATIONS = ['Created', 'Updated', 'Deleted', 'Approved', 'Rejected'] as const;
	const ENTITY_TYPES = [
		'Payment',
		'Expense',
		'Lease',
		'SecurityDeposit',
		'Tenant',
		'Property',
		'WorkOrder',
		'Vendor',
		'OwnerEntity',
		'Appointment',
		'Inspection',
		'RentalApplication',
	] as const;

	let search = $state('');
	let operationFilter = $state('');
	let entityTypeFilter = $state('');
	let skip = $state(0);

	const debouncedSearch = debounced(() => search, 300);

	$effect(() => {
		debouncedSearch.value;
		operationFilter;
		entityTypeFilter;
		skip = 0;
	});

	const auditQuery = createQuery(() => ({
		queryKey: [
			'admin-audit',
			portfolioId,
			debouncedSearch.value,
			operationFilter,
			entityTypeFilter,
			skip,
		],
		queryFn: () =>
			audit.adminList(portfolioId, {
				search: debouncedSearch.value || undefined,
				operation: operationFilter || undefined,
				entityType: entityTypeFilter || undefined,
				skip,
				take: REQUEST_SIZE,
				sort: '-timestamp',
			}),
	}));

	function refresh() {
		queryClient.invalidateQueries({ queryKey: ['admin-audit', portfolioId] });
	}

	let exporting = $state(false);

	// Export the CURRENTLY FILTERED set (search + action + entity filters), not just the visible page.
	async function exportCsv() {
		if (exporting) return;
		exporting = true;
		try {
			await audit.adminExportCsv(portfolioId, {
				search: debouncedSearch.value || undefined,
				operation: operationFilter || undefined,
				entityType: entityTypeFilter || undefined,
				sort: '-timestamp',
			});
			showSuccess('Audit export downloaded.');
		} catch (err) {
			showError(apiErrorMessage(err, 'Export failed.'));
		} finally {
			exporting = false;
		}
	}

	const pageWindow = $derived(overfetchPage(auditQuery.data ?? [], PAGE_SIZE));
	const entries = $derived(pageWindow.items);

	function formatAbsolute(iso: string): string {
		return new Date(iso).toLocaleString('en-US', {
			month: 'short',
			day: 'numeric',
			year: 'numeric',
			hour: 'numeric',
			minute: '2-digit',
		});
	}

	function entityLabel(entry: AdminAuditEntry): string {
		if (!entry.entityType) return '';
		return entry.entityId ? `${entry.entityType} #${entry.entityId}` : entry.entityType;
	}

	/** Pretty-print a JSON string column; falls back to the raw text if it isn't valid JSON. */
	function pretty(json: string | null | undefined): string {
		if (!json) return '—';
		try {
			return JSON.stringify(JSON.parse(json), null, 2);
		} catch {
			return json;
		}
	}
</script>

<svelte:head>
	<title>Audit (forensic) - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="admin-audit-page">
	<div class="mb-4 flex items-center justify-between">
		<div>
			<h1 class="flex items-center gap-2 text-2xl font-bold">
				<ShieldAlert class="h-5 w-5 text-[var(--warning)]" />
				Audit — forensic
			</h1>
			<p class="text-sm text-muted-foreground">
				Admin-only deep view: every change with the actor's IP address and the raw before → after
				values.
			</p>
		</div>
		<div class="flex items-center gap-2">
			<Button
				variant="outline"
				size="sm"
				onclick={exportCsv}
				disabled={exporting || auditQuery.isLoading}
				data-testid="admin-audit-export"
				aria-label="Export the filtered audit trail as CSV"
			>
				<Download class="h-3.5 w-3.5 {exporting ? 'animate-pulse' : ''}" />
				{exporting ? 'Exporting…' : 'Export CSV'}
			</Button>
			<Button
				variant="outline"
				size="sm"
				onclick={refresh}
				disabled={auditQuery.isFetching}
				data-testid="admin-audit-refresh"
				aria-label="Refresh forensic audit trail"
			>
				<RefreshCw class="h-3.5 w-3.5 {auditQuery.isFetching ? 'animate-spin' : ''}" />
				Refresh
			</Button>
		</div>
	</div>

	<Card.Root class="gap-0 py-0">
		<div class="flex flex-wrap items-center gap-2 border-b border-border px-3 py-2">
			<div class="min-w-[180px] flex-1">
				<SearchInput bind:value={search} placeholder="Search audit…" testid="admin-audit-search" />
			</div>
			<Select.Root type="single" bind:value={operationFilter}>
				<Select.Trigger class="w-44" data-testid="admin-audit-operation-filter">
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
				<Select.Trigger class="w-44" data-testid="admin-audit-entity-type-filter">
					{entityTypeFilter || 'All entities'}
				</Select.Trigger>
				<Select.Content>
					<Select.Item value="" label="All entities">All entities</Select.Item>
					{#each ENTITY_TYPES as e}
						<Select.Item value={e} label={e}>{e}</Select.Item>
					{/each}
				</Select.Content>
			</Select.Root>
		</div>

		<Card.Content class="p-0" data-testid="admin-audit-list">
			{#if auditQuery.isLoading}
				<p class="py-10 text-center text-sm text-muted-foreground" data-testid="admin-audit-loading">
					Loading…
				</p>
			{:else if auditQuery.isError}
				<p class="py-10 text-center text-sm text-destructive" data-testid="admin-audit-error">
					Failed to load audit trail. Try refreshing.
				</p>
			{:else if entries.length === 0}
				<p class="py-10 text-center text-sm text-muted-foreground" data-testid="admin-audit-empty">
					No audit entries found.
				</p>
			{:else}
				<ul class="divide-y divide-border" role="list">
					{#each entries as entry (entry.id)}
						<li>
							<details class="group" data-testid={entry.testId ?? `admin-audit-${entry.id}`}>
								<summary
									class="flex cursor-pointer list-none items-start gap-3 px-4 py-3 hover:bg-muted/40"
								>
									<div class="min-w-0 flex-1">
										<p class="text-sm font-medium leading-snug">{entry.description}</p>
										<div
											class="mt-0.5 flex flex-wrap items-center gap-x-2 gap-y-0.5 text-xs text-muted-foreground"
										>
											<span>{entry.actor}{entry.userId ? ` (#${entry.userId})` : ''}</span>
											<span aria-hidden="true">·</span>
											{#if entry.entityType}
												{#if entry.detailHref}
													<a
														href={entry.detailHref}
														onclick={(e) => e.stopPropagation()}
														class="rounded bg-muted px-1 py-0.5 font-mono text-[11px] text-primary hover:underline"
														data-testid="admin-audit-entity-link-{entry.id}"
														title="Open record">{entityLabel(entry)}</a
													>
												{:else}
													<span class="rounded bg-muted px-1 py-0.5 font-mono text-[11px]"
														>{entityLabel(entry)}</span
													>
												{/if}
												<span aria-hidden="true">·</span>
											{/if}
											<span class="font-medium text-foreground/60">{entry.operationName}</span>
											<span aria-hidden="true">·</span>
											<span class="font-mono">IP {entry.ipAddress || '—'}</span>
										</div>
									</div>
									<time
										datetime={entry.timestamp}
										class="shrink-0 text-xs text-muted-foreground"
									>
										{formatAbsolute(entry.timestamp)}
									</time>
								</summary>

								<!-- Forensic detail: change reason + raw before/after JSON -->
								<div class="space-y-3 border-t border-border bg-muted/20 px-4 py-3 text-xs">
									{#if entry.changeReason}
										<div>
											<span class="font-semibold text-muted-foreground">Reason:</span>
											{entry.changeReason}
										</div>
									{/if}
									<div class="grid gap-3 md:grid-cols-2">
										<div>
											<p class="mb-1 font-semibold text-muted-foreground">Old values</p>
											<pre
												class="overflow-x-auto rounded bg-background p-2 font-mono text-[11px] leading-snug"
												data-testid="admin-audit-old-{entry.id}">{pretty(entry.oldValues)}</pre>
										</div>
										<div>
											<p class="mb-1 font-semibold text-muted-foreground">New values</p>
											<pre
												class="overflow-x-auto rounded bg-background p-2 font-mono text-[11px] leading-snug"
												data-testid="admin-audit-new-{entry.id}">{pretty(entry.newValues)}</pre>
										</div>
									</div>
									{#if entry.detailHref}
										<a class="inline-block text-primary hover:underline" href={entry.detailHref}>
											Open record →
										</a>
									{/if}
								</div>
							</details>
						</li>
					{/each}
				</ul>
			{/if}
		</Card.Content>

		<Card.Footer class="border-t border-border px-3 py-2">
			<Pagination
				bind:skip
				take={PAGE_SIZE}
				count={entries.length}
				hasNext={pageWindow.hasNext}
				testid="admin-audit-pagination"
			/>
		</Card.Footer>
	</Card.Root>
</div>
