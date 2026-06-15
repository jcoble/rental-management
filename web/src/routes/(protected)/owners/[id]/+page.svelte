<script lang="ts">
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { owners } from '$lib/api/endpoints/owners';
	import type { Owner, OwnerEntityType } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { ownerSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { parseLegacyAddress, stripComposedSuffix } from '$lib/utils/parse-address';
	import PageBreadcrumb from '$lib/components/shared/PageBreadcrumb.svelte';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import InlineField from '$lib/components/shared/InlineField.svelte';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import AddressAutocomplete from '$lib/components/shared/AddressAutocomplete.svelte';
	import StateSelect from '$lib/components/shared/StateSelect.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Mail, Phone, AlertCircle, Pencil, Save, Trash2, X, Contact, MapPin, FileClock, Building2 } from '@lucide/svelte';
	import DocumentsPanel from '$lib/components/shared/DocumentsPanel.svelte';
	import RecordHistory from '$lib/components/shared/RecordHistory.svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const id = $derived(Number(page.params.id));

	const ownerQuery = createQuery(() => ({
		queryKey: ['owner', id],
		queryFn: () => owners.get(id),
		enabled: !isNaN(id) && id > 0,
	}));

	const owner = $derived(ownerQuery.data);

	const OWNER_ENTITY_TYPES: OwnerEntityType[] = ['Person', 'LLC', 'Trust'];
	const ownerTypeOptions = OWNER_ENTITY_TYPES.map((t) => ({ value: t, label: t }));

	// A human-readable, single-line rendering of the structured address for the
	// read-only state. Prefers the structured fields; falls back to the legacy
	// single-line `address` for owners seeded before structured addresses existed.
	const addressDisplay = $derived.by(() => {
		if (!owner) return '';
		const cityStateZip = [owner.city, [owner.state, owner.postalCode].filter(Boolean).join(' ')]
			.filter(Boolean)
			.join(', ');
		const composed = [owner.addressLine1, owner.addressLine2, cityStateZip]
			.filter(Boolean)
			.join(', ');
		return composed || owner.address || '';
	});

	// ── Inline edit ────────────────────────────────────────────────────────────
	const empty = {
		name: '',
		ownerEntityType: 'Person' as OwnerEntityType,
		taxId: '',
		addressLine1: '',
		addressLine2: '',
		city: '',
		state: '',
		postalCode: '',
		phone: '',
		email: '',
	};
	let editing = $state(false);
	let form = $state({ ...empty });
	let formErrors = $state<Record<string, string>>({});
	let showDeleteConfirm = $state(false);

	function startEditing() {
		if (!owner) return;

		// Back-compat: owners seeded with only the legacy single-line `address` (and no structured
		// fields) used to seed line 1 with the WHOLE line — then the user filled city/state/zip and
		// the suffix doubled. Instead, best-effort split the legacy line into line1/city/state/zip.
		// Only do this when the owner has NO structured address yet (so a real line1 is never clobbered).
		const hasStructured = !!(owner.addressLine1 || owner.city || owner.state || owner.postalCode);
		const seeded = hasStructured
			? {
					addressLine1: owner.addressLine1 ?? '',
					city: owner.city ?? '',
					state: owner.state ?? '',
					postalCode: owner.postalCode ?? '',
				}
			: parseLegacyAddress(owner.address);

		form = {
			name: owner.name,
			ownerEntityType: owner.ownerEntityType,
			taxId: owner.taxId ?? '',
			addressLine1: seeded.addressLine1,
			addressLine2: owner.addressLine2 ?? '',
			city: seeded.city,
			state: seeded.state,
			postalCode: seeded.postalCode,
			phone: owner.phone ?? '',
			email: owner.email ?? '',
		};
		formErrors = {};
		editing = true;
	}
	function cancelEditing() {
		editing = false;
		formErrors = {};
	}

	function submit() {
		// Belt-and-suspenders: if the user kept a line1 still carrying the composed ", City, ST ZIP"
		// tail (e.g. a legacy line we couldn't confidently split, edited by hand) alongside structured
		// city/state/zip, strip the duplicated suffix so display + stored data don't double it.
		if (form.city && form.state && form.postalCode) {
			form.addressLine1 = stripComposedSuffix(form.addressLine1, form.city, form.state, form.postalCode);
		}

		const result = parseForm(ownerSchema, form);
		if (result.errors) {
			formErrors = result.errors;
			return;
		}
		formErrors = {};
		saveMutation.mutate({ id, data: { portfolioId, ...result.data } });
	}

	const saveMutation = createMutation(() => ({
		mutationFn: ({ id: oid, data }: { id: number; data: Record<string, unknown> }) =>
			owners.update(oid, data),
		onSuccess: () => {
			showSuccess('Owner updated.');
			editing = false;
			formErrors = {};
			queryClient.invalidateQueries({ queryKey: ['owner', id] });
			queryClient.invalidateQueries({ queryKey: ['owners', portfolioId] });
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const deleteMutation = createMutation(() => ({
		mutationFn: (oid: number) => owners.delete(oid),
		onSuccess: () => {
			showSuccess('Owner deleted.');
			queryClient.invalidateQueries({ queryKey: ['owners', portfolioId] });
			goto('/owners');
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));
</script>

<svelte:head>
	<title>{owner?.name ? `${owner.name} - Owner` : 'Owner'} - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="owner-detail-page">
	<!-- Breadcrumb -->
	<div class="mb-4">
		<PageBreadcrumb
			crumbs={[
				{ label: 'Owners', href: '/owners' },
				{ label: owner?.name ?? '…' },
			]}
		/>
	</div>

	<!-- Loading state -->
	{#if ownerQuery.isLoading}
		<div class="flex items-center justify-center py-20 text-muted-foreground" data-testid="owner-detail-loading">
			<div class="flex flex-col items-center gap-2">
				<div class="h-6 w-6 animate-spin rounded-full border-2 border-current border-t-transparent"></div>
				<span class="text-sm">Loading…</span>
			</div>
		</div>

	<!-- Error state -->
	{:else if ownerQuery.isError}
		<div class="rounded-lg border border-destructive/30 bg-destructive/10 p-6 text-center" data-testid="owner-detail-error">
			<AlertCircle class="mx-auto mb-2 h-8 w-8 text-destructive" />
			<p class="font-medium text-destructive">Could not load owner</p>
			<p class="mt-1 text-sm text-muted-foreground">{apiErrorMessage(ownerQuery.error)}</p>
			<Button variant="outline" class="mt-4" onclick={() => ownerQuery.refetch()}>Retry</Button>
		</div>

	<!-- Not found -->
	{:else if !owner}
		<div class="rounded-lg border border-border p-6 text-center" data-testid="owner-detail-not-found">
			<Building2 class="mx-auto mb-2 h-8 w-8 text-muted-foreground" />
			<p class="font-medium">Owner not found</p>
			<Button variant="outline" class="mt-4" onclick={() => goto('/owners')}>Back to Owners</Button>
		</div>

	{:else}
		<!-- Header -->
		<div class="mb-6 flex flex-wrap items-start justify-between gap-3">
			<div>
				<h1 class="text-2xl font-bold" data-testid="owner-detail-name">{owner.name}</h1>
				<div class="mt-1 flex flex-wrap items-center gap-3 text-sm text-muted-foreground">
					<span data-testid="owner-detail-type">{owner.ownerEntityType}</span>
					{#if owner.email}
						<span class="flex items-center gap-1">
							<Mail class="h-3.5 w-3.5" />
							{owner.email}
						</span>
					{/if}
					{#if owner.phone}
						<span class="flex items-center gap-1">
							<Phone class="h-3.5 w-3.5" />
							{owner.phone}
						</span>
					{/if}
				</div>
			</div>
			<div class="flex items-center gap-2">
				{#if editing}
					<Button variant="outline" class="gap-2" onclick={cancelEditing} disabled={saveMutation.isPending} data-testid="owner-detail-cancel">
						<X class="h-4 w-4" />
						Cancel
					</Button>
					<Button class="gap-2" onclick={submit} disabled={saveMutation.isPending} data-testid="owner-detail-save">
						<Save class="h-4 w-4" />
						{saveMutation.isPending ? 'Saving…' : 'Save'}
					</Button>
				{:else}
					<Button variant="outline" class="gap-2" onclick={startEditing} data-testid="owner-detail-edit">
						<Pencil class="h-4 w-4" />
						Edit
					</Button>
					<Button
						variant="destructive"
						class="gap-2"
						onclick={() => (showDeleteConfirm = true)}
						data-testid="owner-detail-delete"
					>
						<Trash2 class="h-4 w-4" />
						Delete
					</Button>
				{/if}
			</div>
		</div>

		<!-- Grouped detail cards -->
		<div class="mb-6 grid gap-6 lg:grid-cols-2">
			<DetailCard title="Details" icon={Contact} accent="primary" testid="owner-detail-card" contentClass="grid gap-x-6 gap-y-4 sm:grid-cols-2">
				<InlineField label="Name" bind:value={form.name} display={owner.name} {editing} error={formErrors.name} testid="owner-detail-name-field" />
				<InlineField
					label="Type"
					type="select"
					options={ownerTypeOptions}
					bind:value={form.ownerEntityType}
					display={owner.ownerEntityType}
					{editing}
					error={formErrors.ownerEntityType}
					testid="owner-detail-type-field"
				/>
				<InlineField label="Tax ID / EIN" bind:value={form.taxId} display={owner.taxId} {editing} error={formErrors.taxId} testid="owner-detail-taxid" />
				<InlineField label="Phone" bind:value={form.phone} display={owner.phone} {editing} type="tel" error={formErrors.phone} testid="owner-detail-phone" />
				<InlineField label="Email" bind:value={form.email} display={owner.email} {editing} type="email" error={formErrors.email} testid="owner-detail-email" class="sm:col-span-2" />
			</DetailCard>

			<DetailCard title="Address" icon={MapPin} accent="muted" testid="owner-detail-address-card" contentClass="grid gap-x-6 gap-y-4 sm:grid-cols-2">
				{#if editing}
					<!-- Structured address with autocomplete (city/state/zip autofill on pick),
					     matching the Add-Owner modal field set exactly. -->
					<div class="sm:col-span-2" data-testid="owner-detail-addr1-field">
						<label class="mb-1 block text-xs font-medium text-muted-foreground" for="owner-detail-addr1-input">Street Address</label>
						<AddressAutocomplete
							id="owner-detail-addr1-input"
							testid="owner-detail-addr1-input"
							bind:value={form.addressLine1}
							placeholder="Address (optional)"
							onresolved={(a) => {
								if (a.city) form.city = a.city;
								if (a.state) form.state = a.state;
								if (a.zip) form.postalCode = a.zip;
							}}
						/>
						{#if formErrors.addressLine1}<p class="mt-1 text-xs text-destructive" data-testid="owner-detail-addr1-error">{formErrors.addressLine1}</p>{/if}
					</div>
					<InlineField label="Apt / Suite / Unit #" bind:value={form.addressLine2} {editing} error={formErrors.addressLine2} testid="owner-detail-addr2" class="sm:col-span-2" />
					<InlineField label="City" bind:value={form.city} {editing} error={formErrors.city} testid="owner-detail-city" />
					<div data-testid="owner-detail-state-field">
						<label class="mb-1 block text-xs font-medium text-muted-foreground" for="owner-detail-state-input">State</label>
						<StateSelect id="owner-detail-state-input" testid="owner-detail-state-input" bind:value={form.state} placeholder="State" />
						{#if formErrors.state}<p class="mt-1 text-xs text-destructive" data-testid="owner-detail-state-error">{formErrors.state}</p>{/if}
					</div>
					<InlineField label="ZIP" bind:value={form.postalCode} {editing} error={formErrors.postalCode} testid="owner-detail-zip" />
				{:else}
					<div class="sm:col-span-2" data-testid="owner-detail-address-value">
						<span class="m3-readonly-field__label block text-xs font-medium uppercase tracking-wide text-muted-foreground">Mailing Address</span>
						<span class="mt-1 block text-sm text-foreground">{addressDisplay || '-'}</span>
					</div>
				{/if}
			</DetailCard>
		</div>

		<!-- Record card -->
		<div class="mb-6">
			<DetailCard title="Record" icon={FileClock} accent="muted" testid="owner-detail-record" contentClass="grid gap-x-6 gap-y-4 sm:grid-cols-2">
				<div>
					<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Created</dt>
					<dd class="mt-1 text-sm text-foreground" data-testid="owner-detail-created">
						{new Date(owner.createdAt).toLocaleDateString()}
					</dd>
				</div>
				<div>
					<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Last Updated</dt>
					<dd class="mt-1 text-sm text-foreground" data-testid="owner-detail-updated">
						{new Date(owner.updatedAt).toLocaleDateString()}
					</dd>
				</div>
			</DetailCard>
		</div>

		<!-- Documents section -->
		<div class="mt-6" data-testid="owner-detail-documents">
			<DocumentsPanel entityType="OwnerEntity" entityId={id} />
		</div>

		<!-- Per-record audit history -->
		<div class="mt-6 rounded-lg border border-border bg-card p-4" data-testid="owner-history-section">
			<h2 class="mb-1 text-base font-semibold">History</h2>
			<p class="mb-3 text-sm text-muted-foreground">Every recorded change to this owner — who, what, and when.</p>
			<RecordHistory entityType="OwnerEntity" entityId={id} />
		</div>
	{/if}
</div>

<ConfirmDialog
	open={showDeleteConfirm}
	title="Delete owner"
	message={owner ? `Delete "${owner.name}"? This cannot be undone.` : ''}
	busy={deleteMutation.isPending}
	testid="owner-detail-delete-confirm"
	onconfirm={() => owner && deleteMutation.mutate(owner.id)}
	oncancel={() => (showDeleteConfirm = false)}
/>
