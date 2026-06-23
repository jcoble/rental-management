<script lang="ts">
	import { createQuery, createMutation } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import {
		applications,
		type ApplicationResponse,
		type ApplicationStatus,
	} from '$lib/api/endpoints/applications';
	import { showError, showSuccess, apiErrorMessage } from '$lib/utils/toast';
	import { debounced } from '$lib/utils/debounce.svelte';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Select from '$lib/components/ui/select';
	import { Button } from '$lib/components/ui/button';
	import { Link2, Copy, Check } from '@lucide/svelte';
	import { page } from '$app/state';
	import { readGridParam, syncGridUrl } from '$lib/utils/grid-url-state.svelte';
	import { formatApplicationsEmptyMessage } from '$lib/applications/application-display';
	import {
		buildApplicationLinkUrl,
		readUnitListingLinkContext,
		type UnitListingLinkContext,
	} from '$lib/applications/application-link';

	const PAGE_SIZE = 20;

	// Search + status filter + sort + page persisted in the URL so they survive navigating away and back.
	// Sort/page seed the server-side DataGrid query so applications are filtered/sorted/paged in SQL.
	const initialParams = page.url.searchParams;
	let search = $state(readGridParam(initialParams, 'q'));
	const debouncedSearch = debounced(() => search, 300);

	// shadcn Select binds a string; bits-ui treats '' as "no selection", so the "all" sentinel stands
	// in for "no status filter". `statusFilter` (below) maps it back to '' for the query.
	const ALL_STATUSES = 'all';
	let statusValue = $state<string>(readGridParam(initialParams, 'status') || ALL_STATUSES);
	const statusFilter = $derived<ApplicationStatus | ''>(
		statusValue === ALL_STATUSES ? '' : (statusValue as ApplicationStatus)
	);
	let gridSort = $state(readGridParam(initialParams, 'sort'));
	let gridPage = $state(readGridParam(initialParams, 'page', 1));

	// Reset to page 1 when a filter/search changes — but not on initial mount.
	let filterResetPrimed = false;
	$effect(() => {
		search;
		statusValue;
		if (!filterResetPrimed) {
			filterResetPrimed = true;
			return;
		}
		gridPage = 1;
	});

	$effect(() => {
		syncGridUrl(
			{ q: search, status: statusValue, sort: gridSort, page: gridPage },
			{ status: ALL_STATUSES, page: 1 }
		);
	});

	const STATUS_OPTIONS: { value: string; label: string }[] = [
		{ value: ALL_STATUSES, label: 'All statuses' },
		{ value: 'Submitted', label: 'Submitted' },
		{ value: 'UnderReview', label: 'Under Review' },
		{ value: 'Approved', label: 'Approved' },
		{ value: 'Declined', label: 'Declined' },
		{ value: 'Withdrawn', label: 'Withdrawn' },
	];

	const statusFilterLabel = $derived(
		STATUS_OPTIONS.find((o) => o.value === statusValue)?.label ?? 'All statuses'
	);
	const emptyMessage = $derived(formatApplicationsEmptyMessage(search, statusValue, ALL_STATUSES));

	const applicationsQuery = createQuery(() => ({
		queryKey: ['applications', 'page', statusFilter, debouncedSearch.value, gridSort, gridPage, PAGE_SIZE],
		queryFn: () => applications.listPage({
			status: statusFilter || undefined,
			search: debouncedSearch.value,
			sort: gridSort || undefined,
			skip: (gridPage - 1) * PAGE_SIZE,
			take: PAGE_SIZE,
		}),
	}));

	const list = $derived(applicationsQuery.data?.items ?? []);
	const totalCount = $derived(applicationsQuery.data?.totalCount ?? 0);

	// ── Application link ──────────────────────────────────────────────────────────
	let showLinkDialog = $state(false);
	let applyUrl = $state('');
	let copied = $state(false);
	let activeLinkContext = $state<UnitListingLinkContext | null>(null);
	let listUnitActionHandled = false;
	const listUnitContext = $derived(readUnitListingLinkContext(page.url.searchParams));
	const linkDialogTitle = $derived(
		activeLinkContext ? 'Application link for this unit' : 'Your application link'
	);
	const linkDialogDescription = $derived(
		activeLinkContext
			? 'Share this link with prospects for this unit. The public application will open with this property and unit preselected.'
			: 'Share this link with prospective tenants. Anyone with the link can apply — no account needed.'
	);

	const linkMutation = createMutation(() => ({
		mutationFn: async (context?: UnitListingLinkContext | null) => ({
			result: await applications.createLink(),
			context: context ?? null,
		}),
		onSuccess: ({ result, context }) => {
			const origin = typeof window !== 'undefined' ? window.location.origin : '';
			activeLinkContext = context;
			applyUrl = buildApplicationLinkUrl(origin, result.applyPath, context);
			copied = false;
			showLinkDialog = true;
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	$effect(() => {
		if (listUnitActionHandled || !listUnitContext) return;
		listUnitActionHandled = true;
		linkMutation.mutate(listUnitContext);
	});

	async function copyLink() {
		try {
			await navigator.clipboard.writeText(applyUrl);
			copied = true;
			showSuccess('Link copied to clipboard.');
			setTimeout(() => (copied = false), 2000);
		} catch {
			showError('Could not copy. Select the link and copy it manually.');
		}
	}

	const columns: ColumnDef<ApplicationResponse>[] = [
		{
			key: 'name',
			title: 'Applicant',
			sortable: true,
			mobileRole: 'title',
			accessor: (a) => `${a.firstName} ${a.lastName}`,
		},
		{
			key: 'email',
			title: 'Email',
			sortable: true,
			mobileRole: 'subtitle',
			accessor: (a) => a.email ?? '',
		},
		{
			key: 'phone',
			title: 'Phone',
			mobileRole: 'meta',
			accessor: (a) => a.phone ?? '',
		},
		{
			key: 'monthlyIncome',
			title: 'Income',
			format: 'currency',
			sortable: true,
			mobileRole: 'metric',
			accessor: (a) => a.monthlyIncome ?? 0,
		},
		{
			key: 'submittedAtUtc',
			title: 'Submitted',
			format: 'date',
			sortable: true,
			mobileRole: 'meta',
		},
		{
			key: 'status',
			title: 'Status',
			mobileRole: 'badge',
			cell: statusCell,
		},
	];
</script>

{#snippet statusCell(a: ApplicationResponse)}
	<StatusBadge status={a.status} map={{ Submitted: { label: 'Submitted', class: 'm3-tone-chip border m3-tone--info' }, UnderReview: { label: 'Under Review', class: 'm3-tone-chip border m3-tone--primary' }, Declined: { label: 'Declined', class: 'm3-tone-chip border m3-tone--error' }, Withdrawn: { label: 'Withdrawn', class: 'bg-muted text-muted-foreground border-border' } }} />
{/snippet}

<svelte:head>
	<title>Applications - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="applications-page">
	<div class="mb-4 flex items-center justify-between gap-3">
		<div>
			<h1 class="text-2xl font-bold">Applications</h1>
			<p class="text-sm text-muted-foreground">Prospective tenants who applied through your link.</p>
		</div>
	</div>

	<DataGrid
		data={list}
		{columns}
		loading={applicationsQuery.isLoading || applicationsQuery.isFetching}
		{emptyMessage}
		onRowClick={(a) => goto(`/applications/${a.id}`)}
		getRowKey={(a) => a.id}
		getRowTestId={() => 'application-row'}
		data-testid="applications-list"
		pageSize={PAGE_SIZE}
		page={gridPage}
		totalCount={totalCount}
		serverSide
		onPageChange={(page) => (gridPage = page)}
		sort={gridSort}
		onSortChange={(s) => { gridSort = s ?? ''; gridPage = 1; }}
	>
		{#snippet toolbar()}
			<div class="flex flex-1 flex-wrap items-center gap-2">
				<SearchInput bind:value={search} placeholder="Search applicants…" testid="application-search" />
				<Select.Root type="single" bind:value={statusValue}>
					<Select.Trigger class="h-9 w-44" data-testid="application-status-filter">
						{statusFilterLabel}
					</Select.Trigger>
					<Select.Content>
						{#each STATUS_OPTIONS as opt (opt.value)}
							<Select.Item value={opt.value} label={opt.label}>{opt.label}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
			</div>
			<Button
				class="gap-2 shrink-0"
				onclick={() => linkMutation.mutate(listUnitContext)}
				disabled={linkMutation.isPending}
				data-testid="application-get-link-button"
			>
				<Link2 class="h-4 w-4" />
				{linkMutation.isPending ? 'Generating…' : 'Get application link'}
			</Button>
		{/snippet}
	</DataGrid>
</div>

<!-- Application link dialog -->
<Dialog.Root open={showLinkDialog} onOpenChange={(v) => (showLinkDialog = v)}>
	<Dialog.Content class="max-w-lg">
		<Dialog.Header>
			<Dialog.Title>{linkDialogTitle}</Dialog.Title>
			<Dialog.Description>
				{linkDialogDescription}
			</Dialog.Description>
		</Dialog.Header>
		<div class="flex items-center gap-2" data-testid="application-link-row">
			<div class="flex-1 overflow-hidden">
				<input
					readonly
					value={applyUrl}
					class="h-10 w-full rounded-md border border-border bg-muted/40 px-3 text-sm text-foreground"
					data-testid="application-link-url"
					onclick={(e) => (e.currentTarget as HTMLInputElement).select()}
				/>
			</div>
			<Button variant="outline" class="gap-2 shrink-0" onclick={copyLink} data-testid="application-link-copy">
				{#if copied}
					<Check class="h-4 w-4 text-[var(--success)]" /> Copied
				{:else}
					<Copy class="h-4 w-4" /> Copy
				{/if}
			</Button>
		</div>
		<Dialog.Footer>
			<Button onclick={() => (showLinkDialog = false)} data-testid="application-link-done">Done</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
