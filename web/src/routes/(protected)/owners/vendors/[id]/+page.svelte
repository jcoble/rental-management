<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { page } from '$app/stores';
	import { vendors } from '$lib/api/endpoints/vendors';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import * as Card from '$lib/components/ui/card';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';
	import PageBreadcrumb from '$lib/components/shared/PageBreadcrumb.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import StarRating from '$lib/components/shared/StarRating.svelte';
	import { Star, Mail, Phone } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const id = $derived(Number($page.params.id));

	const vendorQuery = createQuery(() => ({
		queryKey: ['vendor', id],
		queryFn: () => vendors.get(id),
		enabled: !isNaN(id) && id > 0,
	}));

	const scorecardQuery = createQuery(() => ({
		queryKey: ['vendor-scorecard', id],
		queryFn: () => vendors.scorecard(id),
		enabled: !isNaN(id) && id > 0,
	}));

	const vendor = $derived(vendorQuery.data);
	const scorecard = $derived(scorecardQuery.data);

	function invalidate() {
		queryClient.invalidateQueries({ queryKey: ['vendor', id] });
		queryClient.invalidateQueries({ queryKey: ['vendor-scorecard', id] });
		queryClient.invalidateQueries({ queryKey: ['vendors', portfolioId] });
	}

	// --- Rate the vendor ---
	let showRate = $state(false);
	let rateStars = $state(0);
	let rateComment = $state('');

	function openRate() {
		rateStars = 0;
		rateComment = '';
		showRate = true;
	}
	function closeRate() {
		showRate = false;
	}

	const rateMutation = createMutation(() => ({
		mutationFn: ({ stars, comment }: { stars: number; comment?: string }) =>
			vendors.rate(id, { stars, comment }),
		onSuccess: () => {
			showSuccess('Thanks — rating saved.');
			showRate = false;
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function confirmRate() {
		if (rateStars < 1) return;
		rateMutation.mutate({ stars: rateStars, comment: rateComment.trim() || undefined });
	}

	function fmtHours(h: number | null | undefined): string {
		if (h == null) return '—';
		if (h < 1) return `${Math.round(h * 60)} min`;
		return `${h.toFixed(1)} hrs`;
	}
</script>

<svelte:head>
	<title>{vendor?.name ?? 'Vendor'} - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="vendor-detail-page">
	<div class="mb-4">
		<PageBreadcrumb
			crumbs={[
				{ label: 'Owners & Vendors', href: '/owners' },
				{ label: vendor?.name ?? 'Vendor' },
			]}
		/>
	</div>

	{#if vendorQuery.isLoading}
		<p class="py-8 text-center text-sm text-muted-foreground" data-testid="vendor-detail-loading">Loading…</p>
	{:else if vendorQuery.isError}
		<p class="py-8 text-center text-sm text-destructive" data-testid="vendor-detail-error">Failed to load vendor.</p>
	{:else if !vendor}
		<p class="py-8 text-center text-sm text-muted-foreground" data-testid="vendor-detail-not-found">Vendor not found.</p>
	{:else}
		<!-- Header -->
		<div class="mb-6 flex flex-wrap items-start justify-between gap-4">
			<div class="space-y-2">
				<div class="flex flex-wrap items-center gap-2">
					<h1 class="text-2xl font-bold" data-testid="vendor-detail-name">{vendor.name}</h1>
					{#if vendor.preferred}
						<StatusBadge status="Preferred" map={{ Preferred: { class: 'bg-green-100 text-green-800 border-green-200 dark:bg-green-900/30 dark:text-green-400 dark:border-green-800' } }} />
					{/if}
				</div>
				<div class="flex flex-wrap items-center gap-x-4 gap-y-1 text-sm text-muted-foreground">
					<span data-testid="vendor-detail-service">{vendor.serviceType}</span>
					{#if vendor.email}
						<span class="flex items-center gap-1"><Mail class="h-3.5 w-3.5" />{vendor.email}</span>
					{/if}
					{#if vendor.phone}
						<span class="flex items-center gap-1"><Phone class="h-3.5 w-3.5" />{vendor.phone}</span>
					{/if}
				</div>
			</div>
			<Button variant="outline" size="sm" data-testid="vendor-rate-button" onclick={openRate}>
				<Star class="h-4 w-4" />
				Rate vendor
			</Button>
		</div>

		<!-- Scorecard -->
		<Card.Root data-testid="vendor-scorecard">
			<Card.Header>
				<Card.Title>Performance scorecard</Card.Title>
			</Card.Header>
			<Card.Content>
				{#if scorecardQuery.isLoading}
					<p class="py-4 text-sm text-muted-foreground" data-testid="vendor-scorecard-loading">Loading scorecard…</p>
				{:else if scorecard}
					<div class="grid gap-x-8 gap-y-6 sm:grid-cols-2 lg:grid-cols-4">
						<div>
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Average rating</dt>
							<dd class="mt-1.5" data-testid="vendor-scorecard-rating">
								{#if scorecard.averageRating != null && scorecard.ratingCount > 0}
									<div class="flex items-center gap-2">
										<StarRating value={scorecard.averageRating} testid="vendor-scorecard-stars" />
										<span class="font-mono text-sm tabular-nums">{scorecard.averageRating.toFixed(1)}</span>
									</div>
								{:else}
									<span class="text-sm text-muted-foreground">No ratings yet</span>
								{/if}
							</dd>
						</div>
						<div>
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Ratings</dt>
							<dd class="mt-1.5 font-mono text-2xl tabular-nums" data-testid="vendor-scorecard-rating-count">{scorecard.ratingCount}</dd>
						</div>
						<div>
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Jobs completed</dt>
							<dd class="mt-1.5 font-mono text-2xl tabular-nums" data-testid="vendor-scorecard-jobs">{scorecard.jobsCompleted}</dd>
						</div>
						<div>
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Avg response time</dt>
							<dd class="mt-1.5 font-mono text-2xl tabular-nums" data-testid="vendor-scorecard-response">{fmtHours(scorecard.avgResponseHours)}</dd>
							<p class="mt-1 text-xs text-muted-foreground">From the text-out to their DONE reply.</p>
						</div>
					</div>
				{:else}
					<p class="py-4 text-sm text-muted-foreground" data-testid="vendor-scorecard-empty">No scorecard yet.</p>
				{/if}
			</Card.Content>
		</Card.Root>

		<!-- Details -->
		<Card.Root class="mt-6" data-testid="vendor-detail-card">
			<Card.Header>
				<Card.Title>Details</Card.Title>
			</Card.Header>
			<Card.Content>
				<div class="grid gap-x-8 gap-y-4 sm:grid-cols-2 lg:grid-cols-3">
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Service type</dt>
						<dd class="mt-1 text-sm" data-testid="vendor-detail-service-type">{vendor.serviceType}</dd>
					</div>
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Phone</dt>
						<dd class="mt-1 text-sm" data-testid="vendor-detail-phone">{vendor.phone || '—'}</dd>
					</div>
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Email</dt>
						<dd class="mt-1 text-sm" data-testid="vendor-detail-email">{vendor.email || '—'}</dd>
					</div>
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">1099 eligible</dt>
						<dd class="mt-1 text-sm" data-testid="vendor-detail-1099">{vendor.is1099Eligible ? 'Yes' : 'No'}</dd>
					</div>
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">W-9 on file</dt>
						<dd class="mt-1 text-sm" data-testid="vendor-detail-w9">{vendor.w9OnFile ? 'Yes' : 'Missing'}</dd>
					</div>
					{#if vendor.taxId}
						<div>
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Tax ID</dt>
							<dd class="mt-1 font-mono text-sm" data-testid="vendor-detail-taxid">{vendor.taxId}</dd>
						</div>
					{/if}
					{#if vendor.notes}
						<div class="sm:col-span-2 lg:col-span-3">
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Notes</dt>
							<dd class="mt-1 whitespace-pre-line text-sm" data-testid="vendor-detail-notes">{vendor.notes}</dd>
						</div>
					{/if}
				</div>
			</Card.Content>
		</Card.Root>
	{/if}
</div>

<!-- Rate vendor -->
<Dialog.Root open={showRate} onOpenChange={(v) => { if (!v) closeRate(); }}>
	<Dialog.Content class="max-w-md" data-testid="vendor-rate-dialog">
		<Dialog.Header>
			<Dialog.Title>Rate {vendor?.name ?? 'vendor'}</Dialog.Title>
			<Dialog.Description>Your rating builds this vendor's scorecard.</Dialog.Description>
		</Dialog.Header>
		<div class="space-y-3">
			<div class="flex items-center gap-3">
				<StarRating bind:value={rateStars} interactive size="lg" testid="vendor-rate-stars" />
				<span class="text-sm text-muted-foreground" data-testid="vendor-rate-value">
					{rateStars > 0 ? `${rateStars} of 5` : 'Tap a star'}
				</span>
			</div>
			<div class="space-y-1.5">
				<label for="vendor-rate-comment" class="text-xs font-medium text-muted-foreground">Comment (optional)</label>
				<textarea
					id="vendor-rate-comment"
					bind:value={rateComment}
					rows={3}
					maxlength={2000}
					placeholder="e.g. on time, fixed it right the first time"
					class="w-full rounded-md border border-border bg-background px-3 py-2 text-sm"
					data-testid="vendor-rate-comment"
				></textarea>
			</div>
		</div>
		<Dialog.Footer>
			<Button variant="outline" onclick={closeRate} disabled={rateMutation.isPending} data-testid="vendor-rate-cancel">
				Cancel
			</Button>
			<Button onclick={confirmRate} disabled={rateStars < 1 || rateMutation.isPending} data-testid="vendor-rate-confirm">
				{rateMutation.isPending ? 'Saving…' : 'Save rating'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
