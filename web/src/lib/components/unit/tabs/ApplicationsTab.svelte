<script lang="ts">
	import { createMutation, createQuery } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { untrack } from 'svelte';
	import type { UnitDashboard } from '$lib/types';
	import { applications, type ApplicationResponse } from '$lib/api/endpoints/applications';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import {
		buildApplicationLinkUrl,
		type UnitListingLinkContext,
	} from '$lib/applications/application-link';
	import { showError, showSuccess, apiErrorMessage } from '$lib/utils/toast';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import ApplicationDetail from '$lib/components/records/ApplicationDetail.svelte';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';
	import { ArrowLeft, Check, ClipboardList, Copy, ExternalLink, Link2 } from '@lucide/svelte';

	let { dashboard }: { dashboard: UnitDashboard } = $props();

	const portfolioId = $derived(getCurrentPortfolioId());
	const unitId = $derived(dashboard.unit.id);
	const propertyId = $derived(dashboard.unit.propertyId);
	const unitLabel = $derived(`${dashboard.propertyName} · Unit ${dashboard.unit.unitNumber}`);
	const canCreateApplicationLink = $derived(propertyId > 0 && unitId > 0);

	// A selected application folds its full detail inline (?app=<id> on the unit URL); otherwise the list shows.
	const selectedApp = $derived(Number(page.url.searchParams.get('app')) || null);

	const TAB_PAGE_SIZE = 20;
	let appPage = $state(1);
	let appItems = $state<ApplicationResponse[]>([]);

	// Applications tied to THIS unit only — filtered DB-side via the API's ?unitId= filter.
	const applicationsQuery = createQuery(() => ({
		queryKey: ['unit-applications', portfolioId, unitId, appPage, TAB_PAGE_SIZE],
		enabled: portfolioId > 0 && unitId > 0,
		queryFn: () => applications.listPage({
			unitId,
			skip: (appPage - 1) * TAB_PAGE_SIZE,
			take: TAB_PAGE_SIZE,
		}),
	}));

	// Reset accumulation when the unit changes.
	$effect(() => {
		void unitId;
		appPage = 1;
		appItems = [];
	});

	// Accumulate pages (first page replaces; later pages append de-duped) so "Load more" grows the list.
	$effect(() => {
		const data = applicationsQuery.data;
		if (!data) return;
		if (data.skip === 0) {
			appItems = data.items;
			return;
		}
		const current = untrack(() => appItems);
		const seen = new Set(current.map((a) => a.id));
		appItems = [...current, ...data.items.filter((a) => !seen.has(a.id))];
	});

	const appList = $derived(appItems);
	const totalApps = $derived(applicationsQuery.data?.totalCount ?? appItems.length);
	const hasMore = $derived(appItems.length < totalApps);

	// ── Unit-scoped public application link ─────────────────────────────────────
	let showLinkDialog = $state(false);
	let applyUrl = $state('');
	let copied = $state(false);

	const linkMutation = createMutation(() => ({
		mutationFn: async (context: UnitListingLinkContext) => ({
			result: await applications.createLink(),
			context,
		}),
		onSuccess: ({ result, context }) => {
			const origin = typeof window !== 'undefined' ? window.location.origin : '';
			applyUrl = buildApplicationLinkUrl(origin, result.applyPath, context);
			copied = false;
			showLinkDialog = true;
			showSuccess('Application link created.');
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const STATUS_MAP = {
		Submitted: { label: 'Submitted', class: 'm3-tone-chip border m3-tone--info' },
		UnderReview: { label: 'Under Review', class: 'm3-tone-chip border m3-tone--primary' },
		Approved: { label: 'Approved', class: 'm3-tone-chip border m3-tone--success' },
		Declined: { label: 'Declined', class: 'm3-tone-chip border m3-tone--error' },
		Withdrawn: { label: 'Withdrawn', class: 'bg-muted text-muted-foreground border-border' },
	};

	function fmtDate(value: string | null | undefined): string {
		if (!value) return '—';
		const d = new Date(value);
		return isNaN(d.getTime()) ? '—' : d.toLocaleDateString(undefined, { timeZone: 'UTC' });
	}

	function loadMore() {
		if (applicationsQuery.isFetching || !hasMore) return;
		appPage += 1;
	}

	function createApplicationLink() {
		if (!canCreateApplicationLink || linkMutation.isPending) return;
		linkMutation.mutate({ propertyId, unitId });
	}

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

	// Selecting a row is a real navigation step (no replaceState) so Back returns to the list.
	function openApp(id: number) {
		goto('/units/' + unitId + '?tab=applications&app=' + id, { keepFocus: true, noScroll: true });
	}

	// Clearing the selection drops ?app= (replaceState — it's a peer of the list, not a new history step).
	function clearSelection() {
		goto('/units/' + unitId + '?tab=applications', { replaceState: true, keepFocus: true, noScroll: true });
	}
</script>

<div class="space-y-4" data-testid="unit-applications-tab">
{#if selectedApp}
	<!-- Folded application detail: the same <ApplicationDetail> the generic /applications/[id] page mounts. -->
	<div class="flex flex-wrap items-center justify-between gap-2">
		<Button variant="outline" size="sm" class="gap-1" onclick={clearSelection} data-testid="application-back-to-list">
			<ArrowLeft class="h-4 w-4" /> Back to applications
		</Button>
		<Button
			size="sm"
			class="gap-2"
			onclick={createApplicationLink}
			disabled={!canCreateApplicationLink || linkMutation.isPending}
			data-testid="unit-application-create-link"
		>
			<Link2 class="h-4 w-4" />
			{linkMutation.isPending ? 'Creating…' : 'Create application link'}
		</Button>
	</div>
	<ApplicationDetail
		applicationId={selectedApp}
		onDeleted={clearSelection}
		expectedUnitId={unitId}
		onUnitMismatch={clearSelection}
	/>
{:else}
	{#if !applicationsQuery.isLoading && appList.length > 0}
		<div class="flex flex-wrap justify-end gap-2">
			<Button
				class="gap-2"
				onclick={createApplicationLink}
				disabled={!canCreateApplicationLink || linkMutation.isPending}
				data-testid="unit-application-create-link"
			>
				<Link2 class="h-4 w-4" />
				{linkMutation.isPending ? 'Creating…' : 'Create application link'}
			</Button>
		</div>
	{/if}

	{#if applicationsQuery.isLoading}
		<p class="rounded-xl border bg-card p-6 text-center text-sm text-muted-foreground">Loading applications…</p>
	{:else if appList.length === 0}
		<DetailCard title="No applications" icon={ClipboardList} accent="muted" testid="unit-applications-empty">
			<div class="space-y-3">
				<p class="text-sm text-muted-foreground">No applications tied to this unit yet.</p>
				<Button
					variant="outline"
					size="sm"
					class="gap-2"
					onclick={createApplicationLink}
					disabled={!canCreateApplicationLink || linkMutation.isPending}
					data-testid="unit-application-empty-create-link"
				>
					<Link2 class="h-4 w-4" />
					{linkMutation.isPending ? 'Creating…' : 'Create application link'}
				</Button>
			</div>
		</DetailCard>
	{:else}
		<ul class="space-y-2" data-testid="unit-applications-list">
			{#each appList as a (a.id)}
				<li class="rounded-xl border bg-card">
					<button
						type="button"
						class="flex w-full items-center justify-between gap-3 p-3 text-left transition hover:bg-muted/40"
						onclick={() => openApp(a.id)}
						data-testid="unit-application-{a.id}"
					>
						<span class="min-w-0">
							<span class="block truncate text-sm font-medium">{a.firstName} {a.lastName}</span>
							<span class="mt-0.5 block text-xs text-muted-foreground">Submitted {fmtDate(a.submittedAtUtc)}</span>
						</span>
						<StatusBadge status={a.status} map={STATUS_MAP} />
					</button>
				</li>
			{/each}
		</ul>
		{#if hasMore}
			<div class="flex justify-center">
				<Button variant="outline" size="sm" onclick={loadMore} disabled={applicationsQuery.isFetching} data-testid="unit-applications-load-more">
					{applicationsQuery.isFetching ? 'Loading…' : 'Load more'}
				</Button>
			</div>
		{/if}
	{/if}
{/if}
</div>

<Dialog.Root open={showLinkDialog} onOpenChange={(v) => (showLinkDialog = v)}>
	<Dialog.Content class="max-w-lg">
		<Dialog.Header>
			<Dialog.Title>Application link for Unit {dashboard.unit.unitNumber}</Dialog.Title>
			<Dialog.Description>
				Share this link with prospects for {unitLabel}. The public application opens with this property and unit preselected.
			</Dialog.Description>
		</Dialog.Header>
		<div class="space-y-3">
			<p class="rounded-md border bg-muted/30 px-3 py-2 text-xs text-muted-foreground">
				Generating another link refreshes the public application token. Use the newest link when sharing.
			</p>
			<div class="flex items-center gap-2" data-testid="unit-application-link-row">
				<div class="flex-1 overflow-hidden">
					<input
						readonly
						value={applyUrl}
						class="h-10 w-full rounded-md border border-border bg-muted/40 px-3 text-sm text-foreground"
						data-testid="unit-application-link-url"
						onclick={(e) => (e.currentTarget as HTMLInputElement).select()}
					/>
				</div>
				<Button variant="outline" class="gap-2 shrink-0" onclick={copyLink} data-testid="unit-application-link-copy">
					{#if copied}
						<Check class="h-4 w-4 text-[var(--success)]" /> Copied
					{:else}
						<Copy class="h-4 w-4" /> Copy
					{/if}
				</Button>
			</div>
		</div>
		<Dialog.Footer>
			<Button variant="outline" class="gap-2" href={applyUrl} target="_blank" rel="noreferrer" data-testid="unit-application-link-open">
				<ExternalLink class="h-4 w-4" /> Open
			</Button>
			<Button onclick={() => (showLinkDialog = false)} data-testid="unit-application-link-done">Done</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
