<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { units } from '$lib/api/endpoints/units';
	import type { ListingPhoto, ListingPublication, ListingPublicationStatus, ListingWorkspace, SaveListingWorkspaceRequest } from '$lib/types';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import { ArrowDown, ArrowUp, Camera, Check, Clipboard, Eye, ExternalLink, FileDown, Link2, Pencil, RefreshCw, Save, Trash2, Upload, WifiOff } from '@lucide/svelte';

	type Form = {
		headline: string; description: string; rent: string; securityDeposit: string;
		leaseTerms: string; petPolicy: string; utilities: string; parking: string; amenities: string;
		publicationStatus: ListingPublicationStatus; externalListingId: string; listingUrl: string;
		applicationUrl: string; externalStatus: string; copyConfirmed: boolean; termsConfirmed: boolean;
		photosConfirmed: boolean;
	};

	let { unitId }: { unitId: number } = $props();
	const queryClient = useQueryClient();
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
		mutationFn: (markPublished: boolean) => units.saveListingWorkspace(unitId, payload(markPublished)),
		onSuccess: (value) => { acceptWorkspace(value); showSuccess('Listing workspace saved.'); },
		onError: (error) => showError(apiErrorMessage(error)),
	}));
	const signalMutation = createMutation(() => ({
		mutationFn: ({ signalId, accept }: { signalId: number; accept: boolean }) => units.confirmListingSignal(unitId, signalId, accept),
		onSuccess: acceptWorkspace,
		onError: (error) => showError(apiErrorMessage(error)),
	}));
	const photoMutation = createMutation(() => ({
		mutationFn: (work: () => Promise<ListingWorkspace>) => work(),
		onSuccess: (value) => { acceptWorkspace(value); showSuccess('Photo package updated.'); },
		onError: (error) => showError(apiErrorMessage(error)),
	}));
	const connectedMutation = createMutation(() => ({
		mutationFn: ({ action, publicationId }: { action: 'prepare' | 'publish' | 'update' | 'unpublish'; publicationId: number }) =>
			action === 'prepare'
				? units.prepareConnectedListing(unitId, publicationId)
				: units.runConnectedListingCommand(unitId, publicationId, action, operationId()),
		onSuccess: (value) => { acceptWorkspace(value); showSuccess('Connected listing state updated.'); },
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
	function operationId(): string { return globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${unitId}`; }
	async function copy(label: string, value: string) {
		if (!value.trim()) return showError(`${label} is empty.`);
		await navigator.clipboard.writeText(value); showSuccess(`${label} copied.`);
	}
	function openZillow() {
		window.open(guided?.managementUrl ?? 'https://www.zillow.com/rental-manager/properties', '_blank', 'noopener,noreferrer');
		if (workspace) units.saveListingWorkspace(unitId, { zillowGuided: { providerWorkspaceOpened: true } }).then(acceptWorkspace);
	}

	function uploadPhoto(photoId: number, event: Event) {
		const input = event.currentTarget as HTMLInputElement;
		const file = input.files?.[0];
		if (!file) return;
		const operationId = globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${photoId}`;
		photoMutation.mutate(() => units.attachListingPhoto(unitId, photoId, file, operationId));
		input.value = '';
	}

	function editPhoto(photo: ListingPhoto) {
		const category = window.prompt('Photo category', photo.category)?.trim();
		if (!category) return;
		const caption = window.prompt('Caption (optional)', photo.caption ?? '')?.trim() ?? '';
		photoMutation.mutate(() => units.updateListingPhoto(unitId, photo.id, category, caption || null));
	}

	function movePhoto(photoId: number, direction: -1 | 1) {
		if (!workspace) return;
		const ids = workspace.photoManifest.map((photo) => photo.id);
		const index = ids.indexOf(photoId);
		const destination = index + direction;
		if (index < 0 || destination < 0 || destination >= ids.length) return;
		[ids[index], ids[destination]] = [ids[destination], ids[index]];
		photoMutation.mutate(() => units.reorderListingPhotos(unitId, ids));
	}

	async function viewPhoto(photoId: number) {
		try {
			const blob = await units.downloadListingPhoto(unitId, photoId);
			const url = URL.createObjectURL(blob);
			window.open(url, '_blank', 'noopener,noreferrer');
			setTimeout(() => URL.revokeObjectURL(url), 60_000);
		} catch (error) { showError(apiErrorMessage(error)); }
	}
</script>

<div class="space-y-4" data-testid="unit-listing-tab">
	<div class="flex flex-wrap items-center justify-between gap-3">
		<div><h2 class="text-lg font-semibold">Listing workspace</h2><p class="text-sm text-muted-foreground">One listing for this unit, published through guided or connected channels.</p></div>
		<div class="flex gap-2">
			<Button variant="outline" size="sm" class="gap-1" onclick={() => generateMutation.mutate()} disabled={generateMutation.isPending}>
				<RefreshCw class="h-4 w-4" /> {workspace ? 'Sync unit details' : 'Prepare listing'}
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
		<p class="text-xs text-muted-foreground">Syncing updates bedrooms, bathrooms, and square footage. Your listing copy, rent, deposit, and lease terms are never replaced.</p>
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
					<div class="space-y-2">
						{#each workspace.photoManifest as photo, index (photo.id)}
							<div class="flex flex-wrap items-center gap-3 rounded-md border p-3">
								<span class="flex h-7 w-7 items-center justify-center rounded-full bg-muted text-xs font-semibold">{photo.position}</span>
								<div class="min-w-40 flex-1"><p class="text-sm font-medium">{photo.category}</p><p class="text-xs text-muted-foreground">{photo.fileName ?? photo.caption ?? 'Photo needed'}</p></div>
								<div class="flex flex-wrap gap-1">
									<Button variant="outline" size="icon" aria-label="Move photo up" disabled={index === 0 || photoMutation.isPending} onclick={() => movePhoto(photo.id, -1)}><ArrowUp class="h-4 w-4" /></Button>
									<Button variant="outline" size="icon" aria-label="Move photo down" disabled={index === workspace.photoManifest.length - 1 || photoMutation.isPending} onclick={() => movePhoto(photo.id, 1)}><ArrowDown class="h-4 w-4" /></Button>
									<Button variant="outline" size="icon" aria-label="Edit photo details" disabled={photoMutation.isPending} onclick={() => editPhoto(photo)}><Pencil class="h-4 w-4" /></Button>
									{#if photo.storedFileId}<Button variant="outline" size="icon" aria-label="View photo" onclick={() => viewPhoto(photo.id)}><Eye class="h-4 w-4" /></Button>{/if}
									<label class="inline-flex h-9 cursor-pointer items-center gap-1 rounded-md border px-3 text-sm font-medium hover:bg-muted"><Upload class="h-4 w-4" /> {photo.storedFileId ? 'Replace' : 'Add'}<input class="sr-only" type="file" accept="image/jpeg,image/png,image/heic,image/heif,image/webp" onchange={(event) => uploadPhoto(photo.id, event)} /></label>
									{#if photo.storedFileId}<Button variant="outline" size="icon" aria-label="Remove photo" disabled={photoMutation.isPending} onclick={() => photoMutation.mutate(() => units.removeListingPhoto(unitId, photo.id))}><Trash2 class="h-4 w-4" /></Button>{/if}
								</div>
							</div>
						{/each}
					</div>
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

				<DetailCard title="Zillow Connected" icon={WifiOff} accent={connected?.channelAvailable ? 'primary' : 'muted'}>
					<div class="space-y-3">
						<p class="text-sm text-muted-foreground">The same canonical listing is prepared, published, updated, or removed through an approved provider adapter. Guided mode remains available independently.</p>
						<div class="rounded-md bg-muted p-3 text-xs">
							<strong>Connection:</strong> {connected?.channelState ?? 'Unavailable'}<br />
							<strong>Publication:</strong> {connected?.status ?? 'Draft'}<br />
							<strong>Last delivery:</strong> {connected?.lastDeliveryStatus ?? 'None'}
							{#if connected?.lastDeliveryError}<br /><span class="text-destructive">{connected.lastDeliveryError}</span>{/if}
						</div>
						{#if connected?.channelAvailable}
							<div class="grid gap-2 sm:grid-cols-2">
								<Button variant="outline" disabled={connectedMutation.isPending} onclick={() => connectedMutation.mutate({ action: 'prepare', publicationId: connected.id })}><Clipboard class="mr-2 h-4 w-4" /> Prepare</Button>
								{#if connected.status === 'Ready' || connected.status === 'Draft' || connected.status === 'Failed'}
									<Button disabled={connectedMutation.isPending || connected.status !== 'Ready'} onclick={() => connectedMutation.mutate({ action: 'publish', publicationId: connected.id })}><Check class="mr-2 h-4 w-4" /> Publish</Button>
								{:else if connected.status === 'Published'}
									<Button disabled={connectedMutation.isPending || !connected.needsRepublish} onclick={() => connectedMutation.mutate({ action: 'update', publicationId: connected.id })}><RefreshCw class="mr-2 h-4 w-4" /> Send updates</Button>
									<Button variant="outline" disabled={connectedMutation.isPending} onclick={() => connectedMutation.mutate({ action: 'unpublish', publicationId: connected.id })}><Trash2 class="mr-2 h-4 w-4" /> Remove listing</Button>
								{/if}
							</div>
						{:else}
							<p class="rounded-md border p-3 text-sm text-muted-foreground">{connected?.channelUnavailableReason ?? 'Connected publishing is not configured.'} No provider calls are made.</p>
						{/if}
					</div>
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
