<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { units } from '$lib/api/endpoints/units';
	import type { SaveUnitListingRequest, UnitDashboard, UnitListing, UnitListingStatus } from '$lib/types';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { showError, showSuccess, apiErrorMessage } from '$lib/utils/toast';
	import { Clipboard, ExternalLink, FileText, RefreshCw, Save } from '@lucide/svelte';

	const ZILLOW_RENTAL_MANAGER_URL = 'https://www.zillow.com/rental-manager/properties';
	const STATUS_OPTIONS: UnitListingStatus[] = ['Draft', 'ReadyToPost', 'Posted', 'Paused', 'Filled', 'Archived'];

	type ListingForm = {
		status: UnitListingStatus;
		headline: string;
		description: string;
		rent: string;
		securityDeposit: string;
		leaseTerms: string;
		petPolicy: string;
		utilities: string;
		parking: string;
		amenities: string;
		photoNotes: string;
		zillowListingUrl: string;
		zillowApplicationUrl: string;
	};

	let { dashboard }: { dashboard: UnitDashboard } = $props();

	const queryClient = useQueryClient();
	const unitId = $derived(dashboard.unit.id);
	const unitLabel = $derived(`${dashboard.propertyName} - Unit ${dashboard.unit.unitNumber}`);
	let loadedListingId = $state<number | null>(null);
	let form = $state<ListingForm>(blankForm());

	const listingQuery = createQuery(() => ({
		queryKey: ['unit-listing', unitId],
		enabled: unitId > 0,
		queryFn: () => units.listing(unitId),
	}));

	const listing = $derived(listingQuery.data ?? null);
	const hasListing = $derived(!!listing);
	const canSave = $derived(
		hasListing &&
			form.headline.trim().length > 0 &&
			form.description.trim().length > 0 &&
			isOptionalNumber(form.rent) &&
			isOptionalNumber(form.securityDeposit),
	);

	$effect(() => {
		const data = listingQuery.data;
		if (!data) {
			if (loadedListingId !== null && data === null) {
				loadedListingId = null;
				form = blankForm();
			}
			return;
		}
		if (data.id !== loadedListingId) {
			loadedListingId = data.id;
			form = formFromListing(data);
		}
	});

	const generateMutation = createMutation(() => ({
		mutationFn: () => units.generateListing(unitId),
		onSuccess: (data) => {
			loadedListingId = data.id;
			form = formFromListing(data);
			showSuccess('Listing packet generated.');
			queryClient.setQueryData(['unit-listing', unitId], data);
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const saveMutation = createMutation(() => ({
		mutationFn: () => units.saveListing(unitId, buildPayload(form)),
		onSuccess: (data) => {
			loadedListingId = data.id;
			form = formFromListing(data);
			showSuccess('Listing packet saved.');
			queryClient.setQueryData(['unit-listing', unitId], data);
			queryClient.invalidateQueries({ queryKey: ['unit-dashboard', unitId] });
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function blankForm(): ListingForm {
		return {
			status: 'Draft',
			headline: '',
			description: '',
			rent: '',
			securityDeposit: '',
			leaseTerms: '',
			petPolicy: '',
			utilities: '',
			parking: '',
			amenities: '',
			photoNotes: '',
			zillowListingUrl: '',
			zillowApplicationUrl: '',
		};
	}

	function formFromListing(value: UnitListing): ListingForm {
		return {
			status: value.status,
			headline: value.headline ?? '',
			description: value.description ?? '',
			rent: numberToFormValue(value.rent),
			securityDeposit: numberToFormValue(value.securityDeposit),
			leaseTerms: value.leaseTerms ?? '',
			petPolicy: value.petPolicy ?? '',
			utilities: value.utilities ?? '',
			parking: value.parking ?? '',
			amenities: value.amenities ?? '',
			photoNotes: value.photoNotes ?? '',
			zillowListingUrl: value.zillowListingUrl ?? '',
			zillowApplicationUrl: value.zillowApplicationUrl ?? '',
		};
	}

	function buildPayload(value: ListingForm): SaveUnitListingRequest {
		return {
			status: value.status,
			headline: value.headline,
			description: value.description,
			rent: parseOptionalNumber(value.rent),
			securityDeposit: parseOptionalNumber(value.securityDeposit),
			leaseTerms: value.leaseTerms,
			petPolicy: value.petPolicy,
			utilities: value.utilities,
			parking: value.parking,
			amenities: value.amenities,
			photoNotes: value.photoNotes,
			zillowListingUrl: value.zillowListingUrl,
			zillowApplicationUrl: value.zillowApplicationUrl,
		};
	}

	function parseOptionalNumber(value: string): number | null {
		const trimmed = value.trim();
		if (!trimmed) return null;
		const parsed = Number(trimmed);
		return Number.isFinite(parsed) ? parsed : null;
	}

	function isOptionalNumber(value: string): boolean {
		const trimmed = value.trim();
		return !trimmed || Number.isFinite(Number(trimmed));
	}

	function numberToFormValue(value: number | null | undefined): string {
		return value === null || value === undefined ? '' : String(value);
	}

	async function copyText(label: string, text: string) {
		const value = text.trim();
		if (!value) {
			showError(`${label} is empty.`);
			return;
		}
		await navigator.clipboard.writeText(value);
		showSuccess(`${label} copied.`);
	}
</script>

<div class="space-y-4" data-testid="unit-listing-tab">
	<div class="flex flex-wrap items-center justify-between gap-3">
		<div class="min-w-0">
			<h2 class="truncate text-lg font-semibold">Listing</h2>
			<p class="truncate text-sm text-muted-foreground">{unitLabel}</p>
		</div>
		<div class="flex flex-wrap items-center gap-2">
			<Button
				variant="outline"
				size="sm"
				class="gap-1"
				onclick={() => generateMutation.mutate()}
				disabled={generateMutation.isPending}
				data-testid="listing-generate"
			>
				<RefreshCw class="h-4 w-4" />
				{generateMutation.isPending ? 'Generating...' : hasListing ? 'Regenerate' : 'Generate packet'}
			</Button>
			<Button
				size="sm"
				class="gap-1"
				onclick={() => saveMutation.mutate()}
				disabled={!canSave || saveMutation.isPending}
				data-testid="listing-save"
			>
				<Save class="h-4 w-4" />
				{saveMutation.isPending ? 'Saving...' : 'Save'}
			</Button>
		</div>
	</div>

	{#if listingQuery.isLoading}
		<p class="rounded-lg border bg-card p-6 text-center text-sm text-muted-foreground">Loading listing...</p>
	{:else if !hasListing}
		<DetailCard title="No listing packet" icon={FileText} accent="muted" testid="listing-empty">
			<div class="flex flex-wrap items-center justify-between gap-3">
				<p class="text-sm text-muted-foreground">No Zillow packet is saved for this unit.</p>
				<Button
					size="sm"
					class="gap-1"
					onclick={() => generateMutation.mutate()}
					disabled={generateMutation.isPending}
					data-testid="listing-empty-generate"
				>
					<RefreshCw class="h-4 w-4" />
					Generate packet
				</Button>
			</div>
		</DetailCard>
	{:else}
		<div class="grid gap-4 xl:grid-cols-[minmax(0,1fr)_320px]">
			<DetailCard title="Zillow packet" icon={FileText} accent="primary" testid="listing-editor">
				<div class="space-y-4">
					<label class="space-y-1 text-sm">
						<span class="font-medium">Headline</span>
						<div class="flex gap-2">
							<Input bind:value={form.headline} data-testid="listing-headline" />
							<Button
								type="button"
								variant="outline"
								size="icon"
								title="Copy headline"
								aria-label="Copy headline"
								onclick={() => copyText('Headline', form.headline)}
								data-testid="listing-copy-headline"
							>
								<Clipboard class="h-4 w-4" />
							</Button>
						</div>
					</label>

					<label class="space-y-1 text-sm">
						<span class="font-medium">Description</span>
						<textarea
							class="min-h-40 w-full rounded-md border bg-background px-3 py-2 text-sm outline-none focus-visible:ring-2 focus-visible:ring-ring"
							bind:value={form.description}
							data-testid="listing-description"
						></textarea>
						<Button
							type="button"
							variant="outline"
							size="sm"
							class="gap-1"
							onclick={() => copyText('Description', form.description)}
							data-testid="listing-copy-description"
						>
							<Clipboard class="h-4 w-4" />
							Copy description
						</Button>
					</label>

					<div class="grid gap-3 sm:grid-cols-2">
						<label class="space-y-1 text-sm">
							<span class="font-medium">Rent</span>
							<Input inputmode="decimal" bind:value={form.rent} data-testid="listing-rent" />
						</label>
						<label class="space-y-1 text-sm">
							<span class="font-medium">Deposit</span>
							<Input inputmode="decimal" bind:value={form.securityDeposit} data-testid="listing-deposit" />
						</label>
					</div>

					<label class="space-y-1 text-sm">
						<span class="font-medium">Lease terms</span>
						<Input bind:value={form.leaseTerms} data-testid="listing-lease-terms" />
					</label>
					<label class="space-y-1 text-sm">
						<span class="font-medium">Pet policy</span>
						<Input bind:value={form.petPolicy} data-testid="listing-pet-policy" />
					</label>
					<label class="space-y-1 text-sm">
						<span class="font-medium">Utilities</span>
						<Input bind:value={form.utilities} data-testid="listing-utilities" />
					</label>
					<label class="space-y-1 text-sm">
						<span class="font-medium">Parking</span>
						<Input bind:value={form.parking} data-testid="listing-parking" />
					</label>
					<label class="space-y-1 text-sm">
						<span class="font-medium">Amenities</span>
						<textarea
							class="min-h-24 w-full rounded-md border bg-background px-3 py-2 text-sm outline-none focus-visible:ring-2 focus-visible:ring-ring"
							bind:value={form.amenities}
							data-testid="listing-amenities"
						></textarea>
					</label>
					<label class="space-y-1 text-sm">
						<span class="font-medium">Photo notes</span>
						<textarea
							class="min-h-24 w-full rounded-md border bg-background px-3 py-2 text-sm outline-none focus-visible:ring-2 focus-visible:ring-ring"
							bind:value={form.photoNotes}
							data-testid="listing-photo-notes"
						></textarea>
					</label>
				</div>
			</DetailCard>

			<DetailCard title="Zillow handoff" icon={ExternalLink} accent="success" testid="listing-handoff">
				<div class="space-y-4">
					<label class="space-y-1 text-sm">
						<span class="font-medium">Status</span>
						<select
							class="h-10 w-full rounded-md border bg-background px-3 text-sm outline-none focus-visible:ring-2 focus-visible:ring-ring"
							bind:value={form.status}
							data-testid="listing-status"
						>
							{#each STATUS_OPTIONS as status}
								<option value={status}>{status}</option>
							{/each}
						</select>
					</label>

					<a
						href={ZILLOW_RENTAL_MANAGER_URL}
						target="_blank"
						rel="noreferrer"
						class="inline-flex h-9 w-full items-center justify-center gap-2 rounded-md border px-3 text-sm font-medium transition hover:bg-muted"
						data-testid="listing-open-zillow"
					>
						<ExternalLink class="h-4 w-4" />
						Open Zillow Rental Manager
					</a>

					<label class="space-y-1 text-sm">
						<span class="font-medium">Zillow listing URL</span>
						<Input bind:value={form.zillowListingUrl} data-testid="listing-zillow-url" />
					</label>

					<label class="space-y-1 text-sm">
						<span class="font-medium">Zillow application URL</span>
						<Input bind:value={form.zillowApplicationUrl} data-testid="listing-zillow-application-url" />
					</label>
				</div>
			</DetailCard>
		</div>
	{/if}
</div>
