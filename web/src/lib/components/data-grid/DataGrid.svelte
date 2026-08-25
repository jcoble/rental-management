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
	import { untrack, type Snippet } from 'svelte';
	import type { ColumnDef, MobileColumnRole, SortDirection } from './types.js';
	import { cn } from '$lib/utils.js';
	import { Loader2, ChevronUp, ChevronDown, ChevronsUpDown, ChevronLeft, ChevronRight } from '@lucide/svelte';
	import EmptyState from '$lib/components/shared/EmptyState.svelte';
	import { formatAccountingCurrency } from '$lib/accounting/accounting-display';
	import * as Table from '$lib/components/ui/table/index.js';

	type Props = {
		/** The full data array. In client-side mode all sorting/paging happens here. */
		data: T[];
		columns: ColumnDef<T>[];
		loading?: boolean;
		/** Headline for the first-run / no-results empty state. */
		emptyMessage?: string;
		/** A15: optional friendly empty-state extras forwarded to <EmptyState>. */
		emptyDescription?: string;
		emptyIcon?: import('svelte').Component<{ class?: string }>;
		emptyActionLabel?: string;
		emptyOnAction?: () => void;
		emptyTone?: 'muted' | 'primary' | 'success' | 'warning' | 'destructive';
		/** Called when a row (desktop) or card (mobile) is clicked. */
		onRowClick?: (item: T) => void;
		/** Stable key extractor (falls back to item.id then index). */
		getRowKey?: (item: T) => string | number;
		// ── Pagination ────────────────────────────────────────────────────────────
		/**
		 * 1-based current page. When `serverSide=true` it's the controlled value (parent feeds the
		 * matching page of data). In client-side mode it seeds the initial page once on mount (so a
		 * URL-persisted `?page=` is restored) and stays in sync as the user pages — pair it with
		 * `onPageChange` to mirror page changes into the URL.
		 */
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
		/** Called when the current page changes (server-side AND client-side modes). */
		onPageChange?: (page: number) => void;
		/** Sort field, using the API convention of a leading '-' for descending. */
		sort?: string;
		/**
		 * Client-side mode only: seed the initial sort (API convention, e.g. `-name`) once on mount.
		 * Used by pages that persist sort in the URL so a reload/Back restores it; `onSortChange` then
		 * reports subsequent header toggles back so the page can mirror them into the URL. Ignored when
		 * `serverSide` (there `sort` is the controlled value).
		 */
		initialSort?: string;
		/** Called when a sortable header changes (server-side AND client-side modes). */
		onSortChange?: (sort?: string) => void;
		// ── Slots ─────────────────────────────────────────────────────────────────
		toolbar?: Snippet;
		mobileActions?: Snippet<[T]>;
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
		emptyDescription,
		emptyIcon,
		emptyActionLabel,
		emptyOnAction,
		emptyTone,
		onRowClick,
		getRowKey,
		getRowTestId,
		page = $bindable(1),
		pageSize = 20,
		totalCount,
		serverSide = false,
		onPageChange,
		sort,
		initialSort,
		onSortChange,
		toolbar,
		mobileActions,
		class: className,
		'data-testid': dataTestId
	}: Props = $props();

	// ── Sort state ────────────────────────────────────────────────────────────────
	// Parse an API-convention sort string ("-name" desc / "name" asc / "" none) into key + direction.
	function parseSort(s: string | undefined): { key: string | null; dir: SortDirection } {
		if (!s) return { key: null, dir: 'none' };
		return s.startsWith('-') ? { key: s.slice(1), dir: 'desc' } : { key: s, dir: 'asc' };
	}
	// Serialize key + direction back to the API convention (undefined when unsorted).
	function serializeSort(key: string | null, dir: SortDirection): string | undefined {
		if (dir === 'none' || key == null) return undefined;
		return `${dir === 'desc' ? '-' : ''}${key}`;
	}

	// Seed once from the controlled `sort` (server-side) or `initialSort` (client-side persisted state).
	// Server-side stays controlled via the $effect below; client-side owns its state after the seed so
	// header toggles work locally while `onSortChange` reports them up for URL persistence. `untrack`
	// makes the one-time read explicit (these props are reactive; we only want their initial value here).
	const seededSort = untrack(() => parseSort(serverSide ? sort : initialSort));
	let sortKey = $state<string | null>(seededSort.key);
	let sortDir = $state<SortDirection>(seededSort.dir);

	$effect(() => {
		if (!serverSide) return;
		const parsed = parseSort(sort);
		sortKey = parsed.key;
		sortDir = parsed.dir;
	});

	function toggleSort(col: ColumnDef<T>) {
		if (!col.sortable) return;
		if (sortKey === col.key) {
			sortDir = sortDir === 'asc' ? 'desc' : sortDir === 'desc' ? 'none' : 'asc';
			if (sortDir === 'none') sortKey = null;
		} else {
			sortKey = col.key;
			sortDir = 'asc';
		}
		// Reset to the first page on a new sort (both modes) and report sort + page up so a parent can
		// persist them in the URL. Server-side the parent re-fetches; client-side the grid re-slices.
		page = 1;
		onPageChange?.(1);
		onSortChange?.(serializeSort(sortKey, sortDir));
	}

	// ── Column ordering: pinned action columns first ──────────────────────────────
	// Action columns (isAction) render at the LEFT of the desktop table and stick there while the
	// rest of the grid scrolls horizontally, so a row's Mark Paid / Edit / Delete controls are never
	// pushed off-screen. Original relative order is preserved within each group. The mobile card list
	// keys off the original `columns` array, so this reorder only affects the desktop table.
	const orderedColumns = $derived.by(() => {
		const actions = columns.filter((c) => c.isAction);
		const rest = columns.filter((c) => !c.isAction);
		return [...actions, ...rest];
	});

	// ── Value extraction ──────────────────────────────────────────────────────────
	function getValue(item: T, col: ColumnDef<T>): unknown {
		if (col.accessor) return col.accessor(item);
		return (item as Record<string, unknown>)[col.key];
	}

	function formatValue(val: unknown, format?: ColumnDef<T>['format']): string {
		if (val == null) return '–';
		if (format === 'currency' && typeof val === 'number') {
			return formatAccountingCurrency(val);
		}
		if (format === 'number' && typeof val === 'number') {
			return new Intl.NumberFormat('en-US').format(val);
		}
		if (format === 'date') {
			const d = val instanceof Date ? val : new Date(String(val));
			// Format in UTC so a UTC date string (e.g. "2026-05-31T00:00:00Z") renders as
			// the intended calendar day for every viewer, instead of drifting a day back
			// for those behind UTC.
			return isNaN(d.getTime()) ? String(val) : d.toLocaleDateString(undefined, { timeZone: 'UTC' });
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

	// Sticky-left classes for a pinned action column. The opaque background lets data cells scroll
	// underneath it; a right divider separates the pinned block from the scrolling columns. The
	// `<th>` gets the header tint, the `<td>` the surface tint — both supplied by the m3-data-surface.
	function pinnedHeadClass(col: ColumnDef<T>): string {
		if (col.isAction || col.pinned === 'left') return 'datagrid-pinned-head sticky left-0 z-20';
		return col.pinned === 'right' ? 'datagrid-pinned-right sticky right-0 z-20' : '';
	}
	function pinnedCellClass(col: ColumnDef<T>): string {
		if (col.isAction || col.pinned === 'left') return 'datagrid-pinned-cell sticky left-0 z-10';
		return col.pinned === 'right' ? 'datagrid-pinned-right sticky right-0 z-10' : '';
	}

	// ── Column sizing ───────────────────────────────────────────────────────────────
	// Builds the inline `style` for a column's <th>/<td> from width/minWidth/maxWidth.
	// A baseline min-width is applied to every column (unless it sets its own width/minWidth)
	// so a dense table grows past its container — the wrapper's overflow-x-auto then kicks in
	// (horizontal scroll) instead of squishing every column to nothing. A sparse table whose
	// columns fit stays w-full and fills the container, so there's no lonely scrollbar.
	// max-width caps a long free-text column so it truncates instead of blowing out the layout.
	const DEFAULT_MIN_WIDTH = '5rem';
	function colStyle(col: ColumnDef<T>): string {
		const parts: string[] = [];
		if (col.width) parts.push(`width:${col.width}`);
		// An explicit fixed `width` already pins the column; only add a min-width otherwise.
		if (col.minWidth) parts.push(`min-width:${col.minWidth}`);
		// Action columns size to their buttons (best-fit, whitespace-nowrap) — never pad them out
		// with the baseline min-width, which only exists to stop data columns squishing to nothing.
		else if (!col.width && !col.isAction) parts.push(`min-width:${DEFAULT_MIN_WIDTH}`);
		if (col.maxWidth) parts.push(`max-width:${col.maxWidth}`);
		if (col.pinned === 'right' && col.pinnedOffset) parts.push(`right:${col.pinnedOffset}`);
		return parts.join(';');
	}

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

	// ── Page state ──────────────────────────────────────────────────────────────────
	// `page` (the $bindable prop) is the single source of truth for the current 1-based page in BOTH
	// modes: server-side the parent feeds the matching slice; client-side the grid slices `sortedData`
	// by it below. Driving off the prop (rather than a private copy) means a parent that resets it on a
	// filter change — `page = 1` / `bind:page` — propagates, while user paging writes it back through
	// `goToPage` and reports via `onPageChange` so it can be persisted in the URL.
	const clientPage = $derived(page);

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

	// Keep the range and page controls visible even when the current data set fits on one page.
	// This makes the grid's paging contract discoverable instead of making pagination appear and
	// disappear as filters change or sample data grows.
	const showPagination = $derived(effectiveTotalCount > 0);

	const rangeStart = $derived((page - 1) * pageSize + 1);
	const rangeEnd = $derived(Math.min(page * pageSize, effectiveTotalCount));

	// ── Pagination navigation ─────────────────────────────────────────────────────
	function goToPage(p: number) {
		const clamped = Math.min(Math.max(p, 1), pageCount);
		page = clamped;
		onPageChange?.(clamped);
	}

	const currentPage = $derived(page);

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
			// Action columns never become the implicit mobile title and default to hidden — the mobile
			// card uses its own row-tap/affordances, so an action column with no explicit role is dropped.
			const fallback: MobileColumnRole = col.isAction ? 'hidden' : i === 0 ? 'title' : 'meta';
			const role = col.mobileRole ?? fallback;
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

	function stopRowNavigation(event: MouseEvent | KeyboardEvent) {
		event.stopPropagation();
	}
</script>

<div class={cn('space-y-3', className)} data-testid={dataTestId}>
	<!-- Toolbar -->
	{#if toolbar}
		<div
			class="m3-tonal-card m3-tonal-card--violet m3-tonal-card--plain flex flex-wrap items-center gap-3 rounded-lg p-3"
			data-testid="datagrid-toolbar"
		>
			{@render toolbar()}
		</div>
	{/if}

	<!-- ── Desktop table (hidden on mobile + tablet) ──────────────────────────── -->
	<div
		class="datagrid-desktop-surface m3-data-surface hidden overflow-x-auto rounded-lg border lg:block"
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
						{#each orderedColumns as col}
							{@const align = effectiveAlign(col)}
							<Table.Head
								class={cn(
									'select-none whitespace-nowrap px-2.5 py-2 text-xs font-semibold text-muted-foreground',
									alignClass[align],
									col.sortable && 'hover:text-foreground',
									pinnedHeadClass(col),
									col.class
								)}
								style={colStyle(col)}
								aria-sort={col.sortable
									? sortKey === col.key
										? sortDir === 'asc'
											? 'ascending'
											: 'descending'
										: 'none'
									: undefined}
							>
								{#if col.sortable}
									<button
										type="button"
										class="datagrid-sort-control"
										aria-label={`Sort by ${col.title}`}
										onclick={() => toggleSort(col)}
									>
										<span>{col.title}</span>
										{#if sortKey === col.key && sortDir === 'asc'}
											<ChevronUp class="h-3.5 w-3.5 shrink-0" />
										{:else if sortKey === col.key && sortDir === 'desc'}
											<ChevronDown class="h-3.5 w-3.5 shrink-0" />
										{:else}
											<ChevronsUpDown class="h-3.5 w-3.5 shrink-0 opacity-40" />
										{/if}
									</button>
								{:else}
									{col.title}
								{/if}
							</Table.Head>
						{/each}
					</Table.Row>
				</Table.Header>

				<Table.Body class="m3-motion-reveal-list">
					{#if loading && pagedData.length === 0}
						<!-- Layout-matching initial skeleton. It preserves the table shape while the
						     first server-side page is fetched instead of collapsing to a spinner. -->
						{#each Array(6) as _, rowIndex}
							<Table.Row data-testid={rowIndex === 0 ? 'datagrid-loading-initial' : undefined}>
								<Table.Cell colspan={columns.length} class="px-3 py-3">
									<div
										class="h-4 animate-pulse rounded bg-muted"
										style={`width:${Math.max(52, 92 - rowIndex * 6)}%`}
										aria-hidden="true"
									></div>
									{#if rowIndex === 0}<span class="sr-only">Loading results</span>{/if}
								</Table.Cell>
							</Table.Row>
						{/each}
					{:else if !loading && pagedData.length === 0}
						<Table.Row class="hover:[&,&>svelte-css-wrapper]:[&>th,td]:bg-transparent">
							<Table.Cell colspan={columns.length} class="py-2">
								<EmptyState
									title={emptyMessage}
									description={emptyDescription}
									icon={emptyIcon}
									actionLabel={emptyActionLabel}
									onaction={emptyOnAction}
									tone={emptyTone ?? 'muted'}
								/>
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
								onclick={onRowClick
									? (e) => {
											// Container transform (TSK-596): tag the clicked row as the shared
											// `rc-hero` element so a matching header on the destination morphs from
											// it. Skip when the current page already owns an `.rc-hero` (e.g. a
											// detail page's own header) so a sub-grid click can't create a duplicate
											// view-transition-name (which would make the browser skip the transition).
											if (typeof document !== 'undefined' && !document.querySelector('.rc-hero')) {
												(e.currentTarget as HTMLElement).style.viewTransitionName = 'rc-hero';
											}
											onRowClick(item);
										}
									: undefined}
								onkeydown={onRowClick ? (e) => handleRowKeydown(e, item) : undefined}
								data-testid={getRowTestId ? getRowTestId(item) : 'datagrid-row'}
							>
								{#each orderedColumns as col}
									{@const align = effectiveAlign(col)}
									{@const tabular = isTabular(col.format)}
									<Table.Cell
										class={cn(
											'px-2.5 py-2 text-sm',
											alignClass[align],
											tabular && 'font-mono tabular-nums',
											pinnedCellClass(col),
											col.class
										)}
										style={colStyle(col)}
										onclick={col.isAction ? stopRowNavigation : undefined}
										onkeydown={col.isAction ? stopRowNavigation : undefined}
									>
										{#if col.maxWidth}
											<!-- Inner block so max-width + ellipsis truncate reliably in an auto-layout table cell. -->
											<div class="truncate">
												{#if col.cell}
													{@render col.cell(item)}
												{:else}
													{formatValue(getValue(item, col), col.format)}
												{/if}
											</div>
										{:else if col.cell}
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

	<!-- ── Mobile/tablet card list (visible below lg) ──────────────────────────── -->
	<div class="m3-motion-reveal-list divide-y overflow-hidden rounded-xl bg-card lg:hidden" data-testid="datagrid-mobile">
		{#if loading && pagedData.length === 0}
			<div class="space-y-4 p-4" data-testid="datagrid-mobile-loading" role="status" aria-label="Loading results">
				{#each Array(5) as _, rowIndex}
					<div class="space-y-2 py-2" aria-hidden="true">
						<div class="h-4 animate-pulse rounded bg-muted" style={`width:${82 - rowIndex * 5}%`}></div>
						<div class="h-3 w-2/5 animate-pulse rounded bg-muted"></div>
					</div>
				{/each}
			</div>
		{:else if !loading && pagedData.length === 0}
			<div data-testid="datagrid-mobile-empty">
				<EmptyState
					title={emptyMessage}
					description={emptyDescription}
					icon={emptyIcon}
					actionLabel={emptyActionLabel}
					onaction={emptyOnAction}
					tone={emptyTone ?? 'muted'}
				/>
			</div>
		{:else}
			{#each pagedData as item, i (rowKey(item, i))}
				<!-- svelte-ignore a11y_no_noninteractive_tabindex -->
				<article
					class={cn(
						'p-4 transition-colors',
						onRowClick && 'cursor-pointer hover:bg-muted/30'
					)}
					role={onRowClick ? 'button' : undefined}
					tabindex={onRowClick ? 0 : undefined}
					aria-label={onRowClick ? `Open row ${i + 1}` : undefined}
					onclick={onRowClick ? () => onRowClick(item) : undefined}
					onkeydown={onRowClick ? (e) => handleRowKeydown(e, item) : undefined}
					data-testid={getRowTestId ? getRowTestId(item) : 'datagrid-mobile-card'}
				>
					<!-- Title + subtitle -->
						<div class="flex items-start justify-between gap-3">
						<div class="min-w-0 space-y-1">
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
							{#if mobileActions}
								<!-- svelte-ignore a11y_no_noninteractive_element_interactions -- action group stops row/card activation -->
								<div
									class="shrink-0"
									role="group"
									aria-label="Row actions"
									data-testid="datagrid-mobile-actions"
									onclick={stopRowNavigation}
									onkeydown={stopRowNavigation}
								>
									{@render mobileActions(item)}
								</div>
							{/if}
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
									<p class="text-xs font-medium text-muted-foreground">
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
				class="m3-tonal-card m3-tonal-card--violet m3-tonal-card--plain flex items-center justify-between px-3 py-2.5"
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

<style>
	/* ── Pinned (sticky-left) action column ──────────────────────────────────────
	   The action column is rendered first and stuck to the left edge so a row's
	   controls stay on-screen while the data columns scroll horizontally. Sticky
	   cells must paint an OPAQUE background or the scrolling cells show through, so
	   each pinned cell composites its contextual tint over the opaque surface base
	   (the m3-data-surface wrapper's `background`). A right border divides the pinned
	   block from the scrolling region. */
	:global(.datagrid-desktop-surface .datagrid-pinned-head) {
		background:
			color-mix(in srgb, var(--m3c-surface-violet-high, var(--m3c-surface-container-high)) 64%, transparent),
			var(--m3c-surface-violet, var(--m3c-surface-container));
		box-shadow: inset -1px 0 0 color-mix(in srgb, var(--m3c-outline-variant) 60%, transparent);
	}
	:global(.datagrid-desktop-surface .datagrid-pinned-cell) {
		background: var(--m3c-surface-violet, var(--m3c-surface-container));
		box-shadow: inset -1px 0 0 color-mix(in srgb, var(--m3c-outline-variant) 60%, transparent);
	}
	/* Keep the pinned cell opaque on row hover — layer the row's hover tint over the
	   opaque base instead of the table component's semi-transparent bg-muted/50, which
	   would let scrolling cells bleed through the pinned column. */
	:global(.datagrid-desktop-surface [data-slot='table-row']:hover .datagrid-pinned-cell) {
		background:
			color-mix(in srgb, var(--m3c-surface-container-lowest) 42%, transparent),
			var(--m3c-surface-violet, var(--m3c-surface-container));
	}
</style>
