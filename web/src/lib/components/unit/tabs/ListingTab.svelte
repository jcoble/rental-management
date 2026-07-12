<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { units } from '$lib/api/endpoints/units';
	import type { ListingPublication, ListingPublicationStatus, ListingWorkspace, SaveListingWorkspaceRequest, UnitDashboard } from '$lib/types';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import { Camera, Check, Clipboard, ExternalLink, FileDown, Link2, RefreshCw, Save, WifiOff } from '@lucide/svelte';

	type Form = {
		headline: string; description: string; rent: string; securityDeposit: string;
		leaseTerms: string; petPolicy: string; utilities: string; parking: string; amenities: string;
		publicationStatus: ListingPublicationStatus; externalListingId: string; listingUrl: string;
		applicationUrl: string; externalStatus: string; copyConfirmed: boolean; termsConfirmed: boolean;
		photosConfirmed: boolean;
	};

	let { dashboard }: { dashboard: UnitDashboard } = $props();
	const queryClient = useQueryClient();
	const unitId = $derived(dashboard.unit.id);
	let loadedVersion = $state<number | null>(null);
	let form = $state<Form>(blankForm());

	const workspaceQuery = createQuery(() => ({
		queryKey: ['listing-workspace', unitId],
		queryFn: () => units.listingWorkspace(unitId),
	}));
	const workspace = $derived(workspaceQuery.data ?? null);
	const guided = $derived(workspace?.publications.find((item) => item.providerKey === 'Zillow' && item.mode === 'Guided') ?? null);
	const connected = $derived(workspace?.publications.find((item) => item.providerKey === 'Zillow' && item.mode === 'Connected') ?? null);
	const canSave = $derived(!!workspace && !!form.headline.trim() && !!form.description.trim() && validNumber(form.rent));

	$effect(() => {
		const value = workspaceQuery.data;
		if (value && value.contentVersion !== loadedVersion) {
			loadedVersion = value.contentVersion;
			form = formFrom(value, value.publications.find((item) => item.providerKey === 'Zillow' && item.mode === 'Guided'));
		}
	});

	const generateMutation = createMutation(() => ({
		mutationFn: () => units.generateListingWorkspace(unitId),
		onSuccess: acceptWorkspace,
		onError: (error) => showError(apiErrorMessage(error)),
	}));
	const saveMutation = createMutation(() => ({
		mutationFn: (markPublished = false) => units.saveListingWorkspace(unitId, payload(markPublished)),
		onSuccess: (value) => { acceptWorkspace(value); showSuccess('Listing workspace saved.'); },
		onError: (error) => showError(apiErrorMessage(error)),
	}));
	const signalMutation = createMutation(() => ({
		mutationFn: ({ signalId, accept }: { signalId: number; accept: boolean }) => units.confirmListingSignal(unitId, signalId, accept),
		onSuccess: acceptWorkspace,
		onError: (error) => showError(apiErrorMessage(error)),
	}));

	function acceptWorkspace(value: ListingWorkspace) {
		loadedVersion = value.contentVersion;
		form = formFrom(value, value.publications.find((item) => item.providerKey === 'Zillow' && item.mode === 'Guided'));
		queryClient.setQueryData(['listing-workspace', unitId], value);
	}

	function blankForm(): Form {
		return { headline: '', description: '', rent: '', securityDeposit: '', leaseTerms: '', petPolicy: '', utilities: '',
			parking: '', amenities: '', publicationStatus: 'Draft', externalListingId: '', listingUrl: '', applicationUrl: '',
			externalStatus: '', copyConfirmed: false, termsConfirmed: false, photosConfirmed: false };
	}

	function formFrom(value: ListingWorkspace, publication?: ListingPublication): Form {
		return { headline: value.headline, description: value.description, rent: String(value.rent),
			securityDeposit: value.securityDeposit == null ? '' : String(value.securityDeposit), leaseTerms: value.leaseTerms ?? '',
			petPolicy: value.petPolicy ?? '', utilities: value.utilities ?? '', parking: value.parking ?? '', amenities: value.amenities ?? '',
			publicationStatus: publication?.status ?? 'Draft', externalListingId: publication?.externalListingId ?? '',
			listingUrl: publication?.listingUrl ?? '', applicationUrl: publication?.applicationUrl ?? '',
			externalStatus: publication?.lastConfirmedExternalStatus ?? '', copyConfirmed: publication?.copyConfirmed ?? false,
			termsConfirmed: publication?.termsConfirmed ?? false, photosConfirmed: publication?.photosConfirmed ?? false };
	}

	function payload(markPublished: boolean): SaveListingWorkspaceRequest {
		return { headline: form.headline, description: form.description, rent: numberOrNull(form.rent),
			securityDeposit: numberOrNull(form.securityDeposit), leaseTerms: form.leaseTerms, petPolicy: form.petPolicy,
			utilities: form.utilities, parking: form.parking, amenities: form.amenities,
			zillowGuided: { status: form.publicationStatus, externalListingId: form.externalListingId,
				listingUrl: form.listingUrl, applicationUrl: form.applicationUrl, lastConfirmedExternalStatus: form.externalStatus,
				lastConfirmedAtUtc: form.externalStatus && form.externalStatus !== (guided?.lastConfirmedExternalStatus ?? '')
					? new Date().toISOString() : undefined, copyConfirmed: form.copyConfirmed,
				termsConfirmed: form.termsConfirmed, photosConfirmed: form.photosConfirmed,
				providerWorkspaceOpened: guided?.providerWorkspaceOpened ?? false, markCurrentVersionPublished: markPublished } };
	}

	function numberOrNull(value: string): number | null { return value.trim() ? Number(value) : null; }
	function validNumber(value: string): boolean { return !!value.trim() && Number.isFinite(Number(value)); }
	async function copy(label: string, value: string) {
		if (!value.trim()) return showError(`${label} is empty.`);
		await navigator.clipboard.writeText(value); showSuccess(`${label} copied.`);
	}
	function openZillow() {
		window.open(guided?.managementUrl ?? 'https://www.zillow.com/rental-manager/properties', '_blank', 'noopener,noreferrer');
		if (workspace) units.saveListingWorkspace(unitId, { zillowGuided: { providerWorkspaceOpened: true } }).then(acceptWorkspace);
	}
</script>

<div class="space-y-4" data-testid="unit-listing-tab">
	<div class="flex flex-wrap items-center justify-between gap-3">
		<div><h2 class="text-lg font-semibold">Listing workspace</h2><p class="text-sm text-muted-foreground">One listing for this unit, published through guided or connected channels.</p></div>
		<div class="flex gap-2">
			<Button variant="outline" size="sm" class="gap-1" onclick={() => generateMutation.mutate()} disabled={generateMutation.isPending}>
				<RefreshCw class="h-4 w-4" /> {workspace ? 'Refresh from unit' : 'Prepare listing'}
			</Button>
			<Button size="sm" class="gap-1" onclick={() => saveMutation.mutate(false)} disabled={!canSave || saveMutation.isPending}>
				<Save class="h-4 w-4" /> Save
			</Button>
		</div>
	</div>

	{#if workspaceQuery.isLoading}
		<p class="rounded-lg border bg-card p-6 text-center text-sm text-muted-foreground">Loading listing workspace…</p>
	{:else if !workspace}
		<DetailCard title="Prepare this rental listing" icon={FileDown} accent="primary">
			<p class="text-sm text-muted-foreground">Rental Command will prepare reusable copy, terms, and an ordered photo package for this unit.</p>
			<Button class="mt-4 gap-2" onclick={() => generateMutation.mutate()}><RefreshCw class="h-4 w-4" /> Prepare listing</Button>
		</DetailCard>
	{:else}
		{#if guided?.needsRepublish}
			<div class="rounded-lg border border-amber-400/50 bg-amber-400/10 p-3 text-sm"><strong>Changes need republishing.</strong> The saved listing is version {workspace.contentVersion}; Zillow was last confirmed at version {guided.publishedContentVersion}.</div>
		{/if}

		<div class="grid gap-4 xl:grid-cols-[minmax(0,1fr)_380px]">
			<div class="space-y-4">
				<DetailCard title="Listing copy" icon={Clipboard} accent="primary">
					<div class="space-y-4">
						<label class="space-y-1 text-sm"><span class="font-medium">Headline</span><div class="flex gap-2"><Input bind:value={form.headline} /><Button variant="outline" size="icon" onclick={() => copy('Headline', form.headline)} aria-label="Copy headline"><Clipboard class="h-4 w-4" /></Button></div></label>
						<label class="space-y-1 text-sm"><span class="font-medium">Description</span><textarea class="min-h-36 w-full rounded-md border bg-background p-3 text-sm" bind:value={form.description}></textarea><Button variant="outline" size="sm" class="gap-1" onclick={() => copy('Description', form.description)}><Clipboard class="h-4 w-4" /> Copy description</Button></label>
						<div class="grid gap-3 sm:grid-cols-2"><label class="space-y-1 text-sm"><span class="font-medium">Rent</span><Input bind:value={form.rent} inputmode="decimal" /></label><label class="space-y-1 text-sm"><span class="font-medium">Deposit</span><Input bind:value={form.securityDeposit} inputmode="decimal" /></label></div>
						<label class="space-y-1 text-sm"><span class="font-medium">Lease terms</span><Input bind:value={form.leaseTerms} /></label>
						<label class="space-y-1 text-sm"><span class="font-medium">Pet policy</span><Input bind:value={form.petPolicy} /></label>
						<label class="space-y-1 text-sm"><span class="font-medium">Utilities</span><Input bind:value={form.utilities} /></label>
						<label class="space-y-1 text-sm"><span class="font-medium">Parking</span><Input bind:value={form.parking} /></label>
						<label class="space-y-1 text-sm"><span class="font-medium">Amenities</span><textarea class="min-h-20 w-full rounded-md border bg-background p-3 text-sm" bind:value={form.amenities}></textarea></label>
					</div>
				</DetailCard>

				<DetailCard title="Ordered photo package" icon={Camera} accent="muted">
					<div class="space-y-2">{#each workspace.photoManifest as photo}<div class="flex items-center gap-3 rounded-md border p-3"><span class="flex h-7 w-7 items-center justify-center rounded-full bg-muted text-xs font-semibold">{photo.position}</span><div><p class="text-sm font-medium">{photo.category}</p><p class="text-xs text-muted-foreground">{photo.fileName ?? photo.caption ?? 'Photo needed'}</p></div></div>{/each}</div>
				</DetailCard>
			</div>

			<div class="space-y-4">
				<DetailCard title="Zillow Guided" icon={ExternalLink} accent="success">
					<div class="space-y-4">
						<p class="text-sm text-muted-foreground">Rental Command prepares and tracks the work. You remain signed into Zillow and publish there.</p>
						<Button class="w-full gap-2" onclick={openZillow}><ExternalLink class="h-4 w-4" /> Open Zillow Rental Manager</Button>
						<label class="space-y-1 text-sm"><span class="font-medium">Publication state</span><select class="h-10 w-full rounded-md border bg-background px-3" bind:value={form.publicationStatus}><option value="Draft">Draft</option><option value="Ready">Ready</option><option value="Published">Published</option><option value="Paused">Paused</option><option value="Removed">Removed</option></select></label>
						<div class="space-y-2 rounded-md border p-3 text-sm">
							<label class="flex gap-2"><input type="checkbox" bind:checked={form.copyConfirmed} /> Copy entered in Zillow</label>
							<label class="flex gap-2"><input type="checkbox" bind:checked={form.termsConfirmed} /> Terms reviewed in Zillow</label>
							<label class="flex gap-2"><input type="checkbox" bind:checked={form.photosConfirmed} /> Photos uploaded in order</label>
						</div>
						<label class="space-y-1 text-sm"><span class="font-medium">Listing URL</span><Input bind:value={form.listingUrl} /></label>
						<label class="space-y-1 text-sm"><span class="font-medium">Application URL</span><Input bind:value={form.applicationUrl} /></label>
						<label class="space-y-1 text-sm"><span class="font-medium">Last status you confirmed</span><Input bind:value={form.externalStatus} placeholder="Active, paused, rented…" /></label>
						<Button variant="outline" class="w-full gap-2" onclick={() => saveMutation.mutate(true)}><Check class="h-4 w-4" /> Save as published in Zillow</Button>
						<a class="inline-flex w-full items-center justify-center gap-2 rounded-md border px-3 py-2 text-sm font-medium hover:bg-muted" href={workspace.signedLeaseImportUrl}><FileDown class="h-4 w-4" /> Import signed Zillow lease</a>
					</div>
				</DetailCard>

				<DetailCard title="Zillow Connected" icon={WifiOff} accent="muted">
					<p class="text-sm text-muted-foreground">Ready for provider approval. The same listing will publish, reconcile status, and receive leads through a Zillow adapter—without changing this screen or duplicating your data.</p>
					<div class="mt-3 rounded-md bg-muted p-3 text-xs"><strong>Current state:</strong> {connected?.lastDeliveryStatus ?? 'Not connected'}<br />No Zillow API calls are made yet.</div>
				</DetailCard>

				{#if guided?.unconfirmedSignals.length}
					<DetailCard title="Updates to confirm" icon={Link2} accent="primary">
						<div class="space-y-3">{#each guided.unconfirmedSignals as signal}<div class="rounded-md border p-3 text-sm"><p class="font-medium">{signal.signalType}</p><p class="text-muted-foreground">{signal.suggestedExternalStatus ?? signal.suggestedListingUrl ?? 'External listing metadata changed'}</p><div class="mt-2 flex gap-2"><Button size="sm" onclick={() => signalMutation.mutate({ signalId: signal.id, accept: true })}>Confirm</Button><Button size="sm" variant="outline" onclick={() => signalMutation.mutate({ signalId: signal.id, accept: false })}>Ignore</Button></div></div>{/each}</div>
					</DetailCard>
				{/if}
			</div>
		</div>
	{/if}
</div>
