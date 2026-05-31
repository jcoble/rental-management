<!--
  DataGrid — usage example
  ========================

  import DataGrid from '$lib/components/data-grid/DataGrid.svelte';
  import type { ColumnDef } from '$lib/components/data-grid/types';

  interface Lease { id: number; tenant: string; unit: string; rent: number; startDate: string; status: string; }

  const columns: ColumnDef<Lease>[] = [
    { key: 'tenant',    title: 'Tenant',    sortable: true, mobileRole: 'title' },
    { key: 'unit',      title: 'Unit',      sortable: true, mobileRole: 'subtitle' },
    { key: 'rent',      title: 'Rent',      format: 'currency', sortable: true, mobileRole: 'metric' },
    { key: 'startDate', title: 'Start',     format: 'date',     sortable: true, mobileRole: 'meta' },
    { key: 'status',    title: 'Status',    mobileRole: 'badge',
      cell: leaseStatusSnippet },
  ];

  <DataGrid
    {data}
    {columns}
    loading={query.isLoading}
    emptyMessage="No leases found."
    onRowClick={(lease) => goto(`/leases/${lease.id}`)}
    getRowKey={(l) => l.id}
  >
    {#snippet toolbar()}
      <SearchInput bind:value={search} placeholder="Search leases…" />
      <Button size="sm" onclick={openCreate}><Plus class="h-4 w-4" /> New Lease</Button>
    {/snippet}
  </DataGrid>
-->

<script lang="ts" generics="T extends object">
	import type { Snippet } from 'svelte';
	import type { ColumnDef, SortDirection } from './types.js';
	import { cn } from '$lib/utils.js';
	import { Loader2, ChevronUp, ChevronDown, ChevronsUpDown, ChevronLeft, ChevronRight } from '@lucide/svelte';
	import EmptyState from '$lib/components/shared/EmptyState.svelte';
	import * as Table from '$lib/components/ui/table/index.js';

	type Props = {
		/** The full data array. In client-side mode all sorting/paging happens here. */
		data: T[];
		columns: ColumnDef<T>[];
		loading?: boolean;
		emptyMessage?: string;
		/** Called when a row (desktop) or card (mobile) is clicked. */
		onRowClick?: (item: T) => void;
		/** Stable key extractor (falls back to item.id then index). */
		getRowKey?: (item: T) => string | number;
		// ── Pagination ────────────────────────────────────────────────────────────
		/** 1-based current page. Controlled externally only when `serverSide=true`. */
		page?: number;
		/** Rows per page (client-side) or page size hint (server-side). */
		pageSize?: number;
		/** Total records across all pages — required when `serverSide=true`. */
		totalCount?: number;
		/**
		 * When true the component does NOT slice/sort data itself; it relies on the
		 * parent to feed the correct page of data and calls `onPageChange` when the
		 * user navigates.
		 */
		serverSide?: boolean;
		onPageChange?: (page: number) => void;
		// ── Slots ─────────────────────────────────────────────────────────────────
		toolbar?: Snippet;
		class?: string;
		'data-testid'?: string;
		/** Optional per-row testid generator. When provided, each row element receives this as `data-testid`. */
		getRowTestId?: (item: T) => string;
	};

	let {
		data = [],
		columns,
		loading = false,
		emptyMessage = 'No results found.',
		onRowClick,
		getRowKey,
		getRowTestId,
		page = $bindable(1),
		pageSize = 20,
		totalCount,
		serverSide = false,
		onPageChange,
		toolbar,
		class: className,
		'data-testid': dataTestId
	}: Props = $props();

	// ── Sort state ────────────────────────────────────────────────────────────────
	let sortKey = $state<string | null>(null);
	let sortDir = $state<SortDirection>('none');

	function toggleSort(col: ColumnDef<T>) {
		if (!col.sortable) return;
		if (sortKey === col.key) {
			sortDir = sortDir === 'asc' ? 'desc' : sortDir === 'desc' ? 'none' : 'asc';
			if (sortDir === 'none') sortKey = null;
		} else {
			sortKey = col.key;
			sortDir = 'asc';
		}
		// Reset to page 1 when sort changes in client-side mode
		if (!serverSide) clientPage = 1;
	}

	// ── Value extraction ──────────────────────────────────────────────────────────
	function getValue(item: T, col: ColumnDef<T>): unknown {
		if (col.accessor) return col.accessor(item);
		return (item as Record<string, unknown>)[col.key];
	}

	function formatValue(val: unknown, format?: ColumnDef<T>['format']): string {
		if (val == null) return '–';
		if (format === 'currency' && typeof val === 'number') {
			return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(val);
		}
		if (format === 'number' && typeof val === 'number') {
			return new Intl.NumberFormat('en-US').format(val);
		}
		if (format === 'date') {
			const d = val instanceof Date ? val : new Date(String(val));
			return isNaN(d.getTime()) ? String(val) : d.toLocaleDateString();
		}
		if (format === 'datetime') {
			const d = val instanceof Date ? val : new Date(String(val));
			return isNaN(d.getTime()) ? String(val) : d.toLocaleString();
		}
		return String(val);
	}

	// ── Numeric/mono class helper ─────────────────────────────────────────────────
	function isTabular(format?: ColumnDef<T>['format']): boolean {
		return format === 'currency' || format === 'number' || format === 'date' || format === 'datetime';
	}

	function effectiveAlign(col: ColumnDef<T>): 'left' | 'center' | 'right' {
		if (col.align) return col.align;
		return isTabular(col.format) ? 'right' : 'left';
	}

	const alignClass: Record<string, string> = {
		left: 'text-left',
		center: 'text-center',
		right: 'text-right'
	};

	// ── Sort comparator ───────────────────────────────────────────────────────────
	function compareValues(a: unknown, b: unknown, format?: ColumnDef<T>['format']): number {
		if (a == null && b == null) return 0;
		if (a == null) return 1;
		if (b == null) return -1;
		// Date/datetime: compare as timestamps
		if (format === 'date' || format === 'datetime') {
			const da = new Date(String(a)).getTime();
			const db = new Date(String(b)).getTime();
			return isNaN(da) || isNaN(db) ? String(a).localeCompare(String(b)) : da - db;
		}
		// Numeric
		if (typeof a === 'number' && typeof b === 'number') return a - b;
		// Fallback: locale string compare
		return String(a).localeCompare(String(b), undefined, { sensitivity: 'base' });
	}

	// ── Client-side page state ────────────────────────────────────────────────────
	let clientPage = $state(1);

	// Sync clientPage from the controlled `page` prop on server-side mode
	$effect(() => {
		if (serverSide) clientPage = page;
	});

	// ── Derived data (sort + slice) ───────────────────────────────────────────────
	const sortedData = $derived.by(() => {
		if (serverSide || sortKey === null || sortDir === 'none') return data;
		const col = columns.find((c) => c.key === sortKey);
		if (!col) return data;
		const multiplier = sortDir === 'asc' ? 1 : -1;
		return [...data].sort((a, b) => {
			const av = getValue(a, col);
			const bv = getValue(b, col);
			return multiplier * compareValues(av, bv, col.format);
		});
	});

	const effectiveTotalCount = $derived(
		serverSide ? (totalCount ?? data.length) : sortedData.length
	);

	const pageCount = $derived(Math.max(1, Math.ceil(effectiveTotalCount / pageSize)));

	const pagedData = $derived.by(() => {
		if (serverSide) return data; // parent already feeds the right page
		const start = (clientPage - 1) * pageSize;
		return sortedData.slice(start, start + pageSize);
	});

	const showPagination = $derived(pageCount > 1);

	const rangeStart = $derived(
		serverSide
			? (page - 1) * pageSize + 1
			: (clientPage - 1) * pageSize + 1
	);
	const rangeEnd = $derived(
		serverSide
			? Math.min(page * pageSize, effectiveTotalCount)
			: Math.min(clientPage * pageSize, effectiveTotalCount)
	);

	// ── Pagination navigation ─────────────────────────────────────────────────────
	function goToPage(p: number) {
		const clamped = Math.min(Math.max(p, 1), pageCount);
		if (serverSide) {
			page = clamped;
			onPageChange?.(clamped);
		} else {
			clientPage = clamped;
		}
	}

	const currentPage = $derived(serverSide ? page : clientPage);

	// ── Row key helper ────────────────────────────────────────────────────────────
	function rowKey(item: T, index: number): string | number {
		if (getRowKey) return getRowKey(item);
		const id = (item as Record<string, unknown>).id;
		return id != null ? (id as string | number) : index;
	}

	// ── Mobile card helpers ───────────────────────────────────────────────────────
	type MobileGroup = {
		title: ColumnDef<T>[];
		subtitle: ColumnDef<T>[];
		badge: ColumnDef<T>[];
		metric: ColumnDef<T>[];
		meta: ColumnDef<T>[];
	};

	const mobileGroups = $derived.by<MobileGroup>(() => {
		const groups: MobileGroup = { title: [], subtitle: [], badge: [], metric: [], meta: [] };
		columns.forEach((col, i) => {
			const role = col.mobileRole ?? (i === 0 ? 'title' : 'meta');
			if (role === 'hidden') return;
			groups[role].push(col);
		});
		return groups;
	});

	// ── Keyboard row activation ───────────────────────────────────────────────────
	function handleRowKeydown(e: KeyboardEvent, item: T) {
		if (e.key === 'Enter' || e.key === ' ') {
			e.preventDefault();
			onRowClick?.(item);
		}
	}
</script>

<div class={cn('space-y-3', className)} data-testid={dataTestId}>
	<!-- Toolbar -->
	{#if toolbar}
		<div
			class="flex flex-wrap items-center gap-3 rounded-lg border border-border bg-card p-3"
			data-testid="datagrid-toolbar"
		>
			{@render toolbar()}
		</div>
	{/if}

	<!-- ── Desktop table (hidden on mobile) ───────────────────────────────────── -->
	<div
		class="hidden rounded-lg border border-border sm:block"
		data-testid="datagrid-desktop"
		aria-busy={loading}
	>
		<div class="relative">
			{#if loading && pagedData.length > 0}
				<!-- Overlay while refreshing (keeps prior rows visible + dimmed) -->
				<div
					class="absolute inset-0 z-10 flex items-center justify-center rounded-lg bg-background/50"
					data-testid="datagrid-loading-overlay"
				>
					<Loader2 class="h-6 w-6 animate-spin text-muted-foreground" />
				</div>
			{/if}

			<Table.Root>
				<Table.Header>
					<Table.Row class="hover:[&,&>svelte-css-wrapper]:[&>th,td]:bg-transparent">
						{#each columns as col}
							{@const align = effectiveAlign(col)}
							<Table.Head
								class={cn(
									'select-none whitespace-nowrap px-3 py-2.5 text-xs font-semibold uppercase tracking-wide text-muted-foreground',
									alignClass[align],
									col.sortable && 'cursor-pointer hover:text-foreground',
									col.class
								)}
								style={col.width ? `width:${col.width}` : undefined}
								onclick={col.sortable ? () => toggleSort(col) : undefined}
								aria-sort={col.sortable
									? sortKey === col.key
										? sortDir === 'asc'
											? 'ascending'
											: 'descending'
										: 'none'
									: undefined}
							>
								<span class="inline-flex items-center gap-1">
									{col.title}
									{#if col.sortable}
										{#if sortKey === col.key && sortDir === 'asc'}
											<ChevronUp class="h-3.5 w-3.5 shrink-0" />
										{:else if sortKey === col.key && sortDir === 'desc'}
											<ChevronDown class="h-3.5 w-3.5 shrink-0" />
										{:else}
											<ChevronsUpDown class="h-3.5 w-3.5 shrink-0 opacity-40" />
										{/if}
									{/if}
								</span>
							</Table.Head>
						{/each}
					</Table.Row>
				</Table.Header>

				<Table.Body>
					{#if loading && pagedData.length === 0}
						<!-- Initial loading skeleton -->
						<Table.Row>
							<Table.Cell colspan={columns.length} class="py-12 text-center text-muted-foreground">
								<div class="flex flex-col items-center gap-2" data-testid="datagrid-loading-initial">
									<Loader2 class="h-6 w-6 animate-spin" />
									<span class="text-sm">Loading…</span>
								</div>
							</Table.Cell>
						</Table.Row>
					{:else if !loading && pagedData.length === 0}
						<Table.Row class="hover:[&,&>svelte-css-wrapper]:[&>th,td]:bg-transparent">
							<Table.Cell colspan={columns.length} class="py-2">
								<EmptyState title={emptyMessage} />
							</Table.Cell>
						</Table.Row>
					{:else}
						{#each pagedData as item, i (rowKey(item, i))}
							<Table.Row
								class={cn(
									'border-b border-border/60 transition-colors',
									onRowClick && 'cursor-pointer hover:bg-muted/40'
								)}
								role={onRowClick ? 'button' : undefined}
								tabindex={onRowClick ? 0 : undefined}
								aria-label={onRowClick
									? `Open row ${i + 1}`
									: undefined}
								onclick={onRowClick ? () => onRowClick(item) : undefined}
								onkeydown={onRowClick ? (e) => handleRowKeydown(e, item) : undefined}
								data-testid={getRowTestId ? getRowTestId(item) : 'datagrid-row'}
							>
								{#each columns as col}
									{@const align = effectiveAlign(col)}
									{@const tabular = isTabular(col.format)}
									<Table.Cell
										class={cn(
											'px-3 py-2.5 text-sm',
											alignClass[align],
											tabular && 'font-mono tabular-nums',
											col.class
										)}
										style={col.width ? `width:${col.width}` : undefined}
									>
										{#if col.cell}
											{@render col.cell(item)}
										{:else}
											{formatValue(getValue(item, col), col.format)}
										{/if}
									</Table.Cell>
								{/each}
							</Table.Row>
						{/each}
					{/if}
				</Table.Body>
			</Table.Root>
		</div>

		<!-- Desktop pagination bar -->
		{#if showPagination}
			<div
				class="flex items-center justify-between border-t border-border px-4 py-2.5"
				data-testid="datagrid-pagination"
			>
				<span class="text-xs text-muted-foreground" data-testid="datagrid-range">
					{rangeStart}–{rangeEnd} of {effectiveTotalCount}
				</span>
				<div class="flex items-center gap-1">
					<button
						type="button"
						class="inline-flex h-8 items-center gap-1 rounded-md border border-input px-2.5 text-xs font-medium text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground disabled:pointer-events-none disabled:opacity-40"
						disabled={currentPage <= 1}
						onclick={() => goToPage(currentPage - 1)}
						data-testid="datagrid-prev"
					>
						<ChevronLeft class="h-3.5 w-3.5" /> Prev
					</button>
					<span class="min-w-[5rem] text-center text-xs text-muted-foreground" data-testid="datagrid-page-label">
						Page {currentPage} of {pageCount}
					</span>
					<button
						type="button"
						class="inline-flex h-8 items-center gap-1 rounded-md border border-input px-2.5 text-xs font-medium text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground disabled:pointer-events-none disabled:opacity-40"
						disabled={currentPage >= pageCount}
						onclick={() => goToPage(currentPage + 1)}
						data-testid="datagrid-next"
					>
						Next <ChevronRight class="h-3.5 w-3.5" />
					</button>
				</div>
			</div>
		{/if}
	</div>

	<!-- ── Mobile card list (visible only on < sm) ─────────────────────────────── -->
	<div class="space-y-3 sm:hidden" data-testid="datagrid-mobile">
		{#if loading && pagedData.length === 0}
			<div class="flex flex-col items-center gap-2 rounded-lg border border-border p-8 text-muted-foreground" data-testid="datagrid-mobile-loading">
				<Loader2 class="h-6 w-6 animate-spin" />
				<span class="text-sm">Loading…</span>
			</div>
		{:else if !loading && pagedData.length === 0}
			<div class="rounded-lg border border-border" data-testid="datagrid-mobile-empty">
				<EmptyState title={emptyMessage} />
			</div>
		{:else}
			{#each pagedData as item, i (rowKey(item, i))}
				<!-- svelte-ignore a11y_no_noninteractive_tabindex -->
				<article
					class={cn(
						'rounded-lg border border-border bg-card p-4 shadow-sm transition-colors',
						onRowClick && 'cursor-pointer hover:border-primary/40 hover:bg-muted/30'
					)}
					role={onRowClick ? 'button' : undefined}
					tabindex={onRowClick ? 0 : undefined}
					aria-label={onRowClick ? `Open row ${i + 1}` : undefined}
					onclick={onRowClick ? () => onRowClick(item) : undefined}
					onkeydown={onRowClick ? (e) => handleRowKeydown(e, item) : undefined}
					data-testid={getRowTestId ? getRowTestId(item) : 'datagrid-mobile-card'}
				>
					<!-- Title + subtitle -->
					<div class="space-y-1">
						{#each mobileGroups.title as col}
							<div class="break-words text-sm font-semibold text-foreground">
								{#if col.cell}
									{@render col.cell(item)}
								{:else}
									{formatValue(getValue(item, col), col.format)}
								{/if}
							</div>
						{/each}
						{#each mobileGroups.subtitle as col}
							<div class="truncate text-xs text-muted-foreground">
								{#if col.cell}
									{@render col.cell(item)}
								{:else}
									{formatValue(getValue(item, col), col.format)}
								{/if}
							</div>
						{/each}
					</div>

					<!-- Badges + metrics row -->
					{#if mobileGroups.badge.length > 0 || mobileGroups.metric.length > 0}
						<div class="mt-2.5 flex flex-wrap items-center gap-2">
							{#each mobileGroups.badge as col}
								<div>
									{#if col.cell}
										{@render col.cell(item)}
									{:else}
										{formatValue(getValue(item, col), col.format)}
									{/if}
								</div>
							{/each}
							{#each mobileGroups.metric as col}
								<div class={cn('text-sm font-semibold', isTabular(col.format) && 'font-mono tabular-nums')}>
									{#if col.cell}
										{@render col.cell(item)}
									{:else}
										{formatValue(getValue(item, col), col.format)}
									{/if}
								</div>
							{/each}
						</div>
					{/if}

					<!-- Meta fields grid -->
					{#if mobileGroups.meta.length > 0}
						<div class="mt-3 grid grid-cols-2 gap-x-4 gap-y-2.5">
							{#each mobileGroups.meta as col}
								<div class="min-w-0">
									<p class="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">
										{col.mobileLabel ?? col.title}
									</p>
									<p class={cn('mt-0.5 break-words text-sm text-foreground', isTabular(col.format) && 'font-mono tabular-nums')}>
										{#if col.cell}
											{@render col.cell(item)}
										{:else}
											{formatValue(getValue(item, col), col.format)}
										{/if}
									</p>
								</div>
							{/each}
						</div>
					{/if}
				</article>
			{/each}
		{/if}

		<!-- Mobile pagination bar -->
		{#if showPagination}
			<div
				class="flex items-center justify-between rounded-lg border border-border bg-card px-3 py-2.5"
				data-testid="datagrid-mobile-pagination"
			>
				<button
					type="button"
					class="inline-flex h-9 items-center gap-1 rounded-md border border-input px-3 text-sm font-medium text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground disabled:pointer-events-none disabled:opacity-50"
					disabled={currentPage <= 1}
					onclick={() => goToPage(currentPage - 1)}
					data-testid="datagrid-mobile-prev"
				>
					<ChevronLeft class="h-4 w-4" /> Prev
				</button>
				<span class="text-xs text-muted-foreground" data-testid="datagrid-mobile-page-label">
					Page {currentPage} of {pageCount}
				</span>
				<button
					type="button"
					class="inline-flex h-9 items-center gap-1 rounded-md border border-input px-3 text-sm font-medium text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground disabled:pointer-events-none disabled:opacity-50"
					disabled={currentPage >= pageCount}
					onclick={() => goToPage(currentPage + 1)}
					data-testid="datagrid-mobile-next"
				>
					Next <ChevronRight class="h-4 w-4" />
				</button>
			</div>
		{/if}
	</div>
</div>
