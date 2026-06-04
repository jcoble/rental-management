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
	import { Button } from '$lib/components/ui/button';
	import { Link2, Copy, Check } from '@lucide/svelte';

	let search = $state('');
	let statusFilter = $state<ApplicationStatus | ''>('');

	const STATUS_OPTIONS: { value: ApplicationStatus | ''; label: string }[] = [
		{ value: '', label: 'All statuses' },
		{ value: 'Submitted', label: 'Submitted' },
		{ value: 'UnderReview', label: 'Under Review' },
		{ value: 'Approved', label: 'Approved' },
		{ value: 'Declined', label: 'Declined' },
		{ value: 'Withdrawn', label: 'Withdrawn' },
	];

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
	<StatusBadge status={a.status} map={{ Submitted: { label: 'Submitted', class: 'bg-blue-100 text-blue-800 border-blue-200 dark:bg-blue-900/30 dark:text-blue-400 dark:border-blue-800' }, UnderReview: { label: 'Under Review', class: 'bg-purple-100 text-purple-800 border-purple-200 dark:bg-purple-900/30 dark:text-purple-400 dark:border-purple-800' }, Declined: { label: 'Declined', class: 'bg-red-100 text-red-800 border-red-200 dark:bg-red-900/30 dark:text-red-400 dark:border-red-800' }, Withdrawn: { label: 'Withdrawn', class: 'bg-muted text-muted-foreground border-border' } }} />
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
				<select
					bind:value={statusFilter}
					class="h-9 rounded-md border border-border bg-card px-2.5 text-sm text-foreground focus:border-ring focus:outline-none focus:ring-1 focus:ring-ring/50"
					data-testid="application-status-filter"
				>
					{#each STATUS_OPTIONS as opt (opt.value)}
						<option value={opt.value}>{opt.label}</option>
					{/each}
				</select>
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
					<Check class="h-4 w-4 text-green-600" /> Copied
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
