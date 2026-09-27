<script lang="ts">
	import { createQuery, createMutation } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import {
		applications,
		type ApplicationResponse,
		type ApplicationStatus,
	} from '$lib/api/endpoints/applications';
	import { showError, showSuccess, apiErrorMessage } from '$lib/utils/toast';
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

	// Search + status filter persisted in the URL so they survive navigating away and back.
	let search = $state(readGridParam(page.url.searchParams, 'q'));

	// shadcn Select binds a string; bits-ui treats '' as "no selection", so the "all" sentinel stands
	// in for "no status filter". `statusFilter` (below) maps it back to '' for the query.
	const ALL_STATUSES = 'all';
	let statusValue = $state<string>(readGridParam(page.url.searchParams, 'status') || ALL_STATUSES);
	const statusFilter = $derived<ApplicationStatus | ''>(
		statusValue === ALL_STATUSES ? '' : (statusValue as ApplicationStatus)
	);

	$effect(() => {
		syncGridUrl({ q: search, status: statusValue }, { status: ALL_STATUSES });
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

	// Status filter goes to the server; free-text search is filtered client-side
	// so it works across name/email/phone without extra round-trips.
	const applicationsQuery = createQuery(() => ({
		queryKey: ['applications', statusFilter],
		queryFn: () => applications.list({ status: statusFilter || undefined }),
	}));

	const list = $derived.by(() => {
		const all = applicationsQuery.data ?? [];
		if (!search.trim()) return all;
		const q = search.trim().toLowerCase();
		return all.filter(
			(a) =>
				`${a.firstName} ${a.lastName}`.toLowerCase().includes(q) ||
				(a.email ?? '').toLowerCase().includes(q) ||
				(a.phone ?? '').toLowerCase().includes(q)
		);
	});

	// ── Application link ──────────────────────────────────────────────────────────
	let showLinkDialog = $state(false);
	let applyUrl = $state('');
	let copied = $state(false);

	const linkMutation = createMutation(() => ({
		mutationFn: () => applications.createLink(),
		onSuccess: (result) => {
			const origin = typeof window !== 'undefined' ? window.location.origin : '';
			applyUrl = `${origin}${result.applyPath}`;
			copied = false;
			showLinkDialog = true;
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

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
		loading={applicationsQuery.isLoading}
		emptyMessage="No applications yet. Share your application link to get started."
		onRowClick={(a) => goto(`/applications/${a.id}`)}
		getRowKey={(a) => a.id}
		getRowTestId={() => 'application-row'}
		data-testid="applications-list"
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
				onclick={() => linkMutation.mutate()}
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
			<Dialog.Title>Your application link</Dialog.Title>
			<Dialog.Description>
				Share this link with prospective tenants. Anyone with the link can apply — no account needed.
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
